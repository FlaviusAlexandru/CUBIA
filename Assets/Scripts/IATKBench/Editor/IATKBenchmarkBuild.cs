using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DataViz.EditorTools
{
    /// <summary>
    /// Batch-mode entry points for the IATK comparison benchmark.
    ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod DataViz.EditorTools.IATKBenchmarkBuild.SetupScene
    ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod DataViz.EditorTools.IATKBenchmarkBuild.Build -iatkOut "C:\path\VRVDM&VIZ.exe"
    /// SetupScene derives IATKBenchmarkScene from the benchmark scene (VisualizerSceneTest): the GRIVAPP
    /// point renderers are disabled, the BenchmarkingManager is disabled and used only as the settings
    /// source, and an IATKBenchmarkRunner is added. Everything else (XR rig, URP, lighting) is unchanged.
    /// </summary>
    public static class IATKBenchmarkBuild
    {
        const string SourceScene = "Assets/Scenes/VisualizerSceneTest.unity";
        const string IatkScene = "Assets/Scenes/IATKBenchmarkScene.unity";
        const string ReportPath = "Logs/IATKBenchmarkSetup.txt";

        public static void SetupScene()
        {
            var report = new StringBuilder();
            var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);

            var bm = Object.FindObjectsByType<BenchmarkingManager>(FindObjectsInactive.Include).FirstOrDefault();
            var viz = Object.FindObjectsByType<ScatterplotVisualizer>(FindObjectsInactive.Include).FirstOrDefault();
            var mgr = Object.FindObjectsByType<MultiplayerScatterplotManager>(FindObjectsInactive.Include).FirstOrDefault();
            if (bm == null || viz == null)
            {
                Debug.LogError($"[IATKBenchmarkBuild] BenchmarkingManager={bm != null} ScatterplotVisualizer={viz != null} - cannot set up.");
                EditorApplication.Exit(1);
                return;
            }

            report.AppendLine($"BenchmarkingManager on '{GetPath(bm.transform)}' enabled={bm.enabled} autoRun={bm.m_AutoRunOnStart}");
            report.AppendLine($"  datasets={string.Join("|", bm.m_DatasetFileNames)} warmup={bm.m_WarmupFrames} windows={bm.m_WindowsPerCondition} frames={bm.m_FramesPerWindow} repeats={bm.m_BuildRepeats} max={bm.m_MaxSecondsPerCondition}");
            report.AppendLine($"  viewCam={(bm.m_ViewCameraOverride ? GetPath(bm.m_ViewCameraOverride.transform) : "null")} fixedPose={(bm.m_FixedViewPose ? GetPath(bm.m_FixedViewPose) : "null")} dist={bm.m_ViewDistance} out={bm.m_OutputFileName}");
            report.AppendLine($"ScatterplotVisualizer on '{GetPath(viz.transform)}' pos={viz.transform.position} rot={viz.transform.eulerAngles} lossyScale={viz.transform.lossyScale} axisLength={viz.m_AxisLength}");
            report.AppendLine($"Manager PointSize={(mgr ? mgr.PointSize : -1)} ColorColumnIndex={(mgr ? mgr.ColorColumnIndex : -99)}");

            // Turn off every GRIVAPP point/axis renderer so only IATK draws the data.
            foreach (var c in viz.GetComponents<Behaviour>())
            {
                if (c is ScatterplotVisualizer || c is ScatterplotInstancedRenderer || c is ScatterplotParticleRenderer ||
                    c is ScatterplotVFXRenderer || c is GPUPointInteractable || c is UnityEngine.VFX.VisualEffect)
                {
                    c.enabled = false;
                    report.AppendLine($"  disabled {c.GetType().Name}");
                }
            }
            foreach (var ps in viz.GetComponentsInChildren<ParticleSystem>(true))
            {
                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r) r.enabled = false;
                report.AppendLine($"  disabled ParticleSystemRenderer on {ps.name}");
            }

            bm.enabled = false;
            var runner = bm.GetComponent<IATKBenchmarkRunner>() ?? bm.gameObject.AddComponent<IATKBenchmarkRunner>();
            runner.m_Settings = bm;
            runner.m_PlotAnchor = viz.transform;
            runner.m_AxisLength = viz.m_AxisLength;

            report.AppendLine("Root objects:");
            foreach (var go in scene.GetRootGameObjects())
                report.AppendLine($"  {go.name} active={go.activeSelf} comps={string.Join(",", go.GetComponents<Component>().Select(c => c ? c.GetType().Name : "MISSING"))}");

            // IATK creates its materials at runtime with Shader.Find("IATK/..."). Nothing in a build
            // references those shaders, so the player strips them and Shader.Find returns null.
            // A material per IATK shader inside a Resources folder keeps them in the build.
            const string keepDir = "Assets/IATK/Resources/IATKBenchShaders";
            Directory.CreateDirectory(keepDir);
            AssetDatabase.Refresh();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/IATK" }))
            {
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                if (sh == null) continue;
                string matPath = $"{keepDir}/{sh.name.Replace('/', '_')}.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
                    AssetDatabase.CreateAsset(new Material(sh), matPath);
                report.AppendLine($"  kept shader {sh.name}");
            }
            AssetDatabase.SaveAssets();

            // IATK tags its views (view.gameObject.tag = "View"); the tags ship in IATK's own
            // TagManager, so they must be added to this project or View creation throws.
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tags = tagManager.FindProperty("tags");
            foreach (string t in new[] { "View", "Axis", "Visualisation", "Scatterplot2D", "Scatterplot3D", "PCP", "LinkedVisualisation" })
            {
                bool exists = false;
                for (int i = 0; i < tags.arraySize; i++) if (tags.GetArrayElementAtIndex(i).stringValue == t) exists = true;
                if (exists) continue;
                tags.InsertArrayElementAtIndex(tags.arraySize);
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = t;
                report.AppendLine($"  added tag {t}");
            }
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            EditorSceneManager.SaveScene(scene, IatkScene);
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[IATKBenchmarkBuild] Scene set up:\n" + report);
        }

        public static void SetupAndBuild()
        {
            SetupScene();
            Build();
        }

        public static void Build()
        {
            string outPath = GetArg("-iatkOut") ?? @"C:\Users\FU05OG\Desktop\StandaloneBuild_IATK\VRVDM&VIZ.exe";
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { IatkScene },
                locationPathName = outPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };
            var result = BuildPipeline.BuildPlayer(opts);
            var s = result.summary;
            string msg = $"[IATKBenchmarkBuild] result={s.result} errors={s.totalErrors} size={s.totalSize / 1048576}MB time={s.totalTime} out={outPath} backend={PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone)}";
            File.AppendAllText(ReportPath, msg + "\n");
            Debug.Log(msg);
            if (s.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        static string GetArg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        static string GetPath(Transform t) => t.parent == null ? t.name : GetPath(t.parent) + "/" + t.name;
    }
}
