using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using IATK;
using UnityEngine;
using UnityEngine.XR;

namespace DataViz
{
    /// <summary>
    /// Runs the CUBIA benchmark protocol (BenchmarkingManager) with IATK as the renderer,
    /// so its numbers can be compared directly with the Instanced / Particle / VFX runs.
    ///
    /// Protocol, view control, frame timing, flags, env logging, session chaining and the
    /// CSV schema are copied from BenchmarkingManager; all trial settings (datasets, warm-up,
    /// windows, frames, build repeats, caps, view camera/pose) are read from the scene's
    /// BenchmarkingManager (left disabled) so both harnesses use identical values. The same
    /// command-line overrides apply. Pipeline column = "IATK".
    ///
    /// Build time per repeat = .cdataset load (ColumnarBinaryImporter) + min-max normalisation
    /// of the mapped dimensions (IATKCDatasetSource) + IATK's CreateVisualisation (ViewBuilder,
    /// BigMesh, axes). Point size: IATK's billboard is 0.05 * _Size object units wide, so _Size
    /// is set to give the same world diameter as MultiplayerScatterplotManager.PointSize.
    /// </summary>
    public class IATKBenchmarkRunner : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The scene's BenchmarkingManager (disabled) - trial settings are read from it.")]
        public BenchmarkingManager m_Settings;
        [Tooltip("The ScatterplotVisualizer transform: the IATK plot is placed in the same 1 m cube.")]
        public Transform m_PlotAnchor;
        public float m_AxisLength = 1f;

        [Header("IATK")]
        public string m_XDimension = "Dim_0";
        public string m_YDimension = "Dim_1";
        public string m_ZDimension = "Dim_2";
        public Color m_PointColour = Color.cyan;   // ScatterplotVisualizer's uniform colour

        public const string PipelineName = "IATK";

        // settings (copied from m_Settings, then command line)
        private List<string> m_Datasets = new();
        private int m_WarmupFrames, m_WindowsPerCondition, m_FramesPerWindow, m_BuildRepeats;
        private bool m_RandomizeOrder, m_FixView, m_PinXRRenderScale, m_CaptureView, m_AutoRun;
        private float m_MaxSecondsPerCondition, m_ViewDistance, m_ChainCooldownSeconds;
        private string m_OutputFileName, m_SessionLabel;
        private Camera m_ViewCameraOverride;
        private Transform m_FixedViewPose;

        private int m_SessionIndex = -1;
        private int m_SessionCount = -1;
        private readonly List<string> m_PassThroughArgs = new();

        private Visualisation m_Vis;
        private IATKCDatasetSource m_Source;

        private StreamWriter m_Writer;
        private string m_OutputPath;
        private bool m_IsRunning;

        private Camera m_ViewCamera;
        private Transform m_ViewRig;
        private Vector3 m_SavedRigPos;
        private Quaternion m_SavedRigRot;
        private bool m_ViewFrozen;
        private Vector3 m_FrozenPos;
        private Quaternion m_FrozenRot;

        private void Start()
        {
            Application.runInBackground = true;
            CopySettings();
            ApplyCommandLine();

            var go = new GameObject("IATK Visualisation");
            go.transform.SetParent(m_PlotAnchor, false);
            go.transform.localPosition = Vector3.one * (-m_AxisLength / 2f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            m_Source = go.AddComponent<IATKCDatasetSource>();
            m_Vis = go.AddComponent<Visualisation>();
            m_Vis.dataSource = m_Source;
            m_Vis.geometry = AbstractVisualisation.GeometryType.Points;
            m_Vis.visualisationType = AbstractVisualisation.VisualisationTypes.SCATTERPLOT;
            m_Vis.xDimension = new DimensionFilter { Attribute = m_XDimension };
            m_Vis.yDimension = new DimensionFilter { Attribute = m_YDimension };
            m_Vis.zDimension = new DimensionFilter { Attribute = m_ZDimension };
            // A Visualisation added from code (not via IATK's inspector) leaves these null; IATK
            // treats anything other than "Undefined" as a mapped dimension and dereferences it.
            m_Vis.colourDimension = "Undefined";
            m_Vis.colorPaletteDimension = "Undefined";
            m_Vis.sizeDimension = "Undefined";
            m_Vis.linkingDimension = "Undefined";
            m_Vis.graphDimension = "Undefined";
            m_Vis.originDimension = "Undefined";
            m_Vis.destinationDimension = "Undefined";
            m_Vis.attributeFilters = new AttributeFilter[0];
            m_Vis.colour = m_PointColour;
            m_Vis.width = m_Vis.height = m_Vis.depth = m_AxisLength;

            if (m_AutoRun) RunBenchmark();
        }

        private void CopySettings()
        {
            var s = m_Settings;
            m_Datasets = new List<string>(s.m_DatasetFileNames);
            m_WarmupFrames = s.m_WarmupFrames;
            m_WindowsPerCondition = s.m_WindowsPerCondition;
            m_FramesPerWindow = s.m_FramesPerWindow;
            m_RandomizeOrder = s.m_RandomizeOrder;
            m_BuildRepeats = s.m_BuildRepeats;
            m_MaxSecondsPerCondition = s.m_MaxSecondsPerCondition;
            m_OutputFileName = s.m_OutputFileName;
            m_SessionLabel = s.m_SessionLabel;
            m_FixView = s.m_FixViewDuringConditions;
            m_ViewCameraOverride = s.m_ViewCameraOverride;
            m_FixedViewPose = s.m_FixedViewPose;
            m_ViewDistance = s.m_ViewDistance;
            m_PinXRRenderScale = s.m_PinXRRenderScale;
            m_AutoRun = s.m_AutoRunOnStart;
            m_CaptureView = s.m_CaptureViewPerCondition;
            m_ChainCooldownSeconds = s.m_ChainCooldownSeconds;
        }

        private void ApplyCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length - 1; i++)
            {
                string key = args[i];
                string val = args[i + 1];
                bool known = true;
                switch (key)
                {
                    case "-session": m_SessionLabel = val; break;
                    case "-sessionIndex": if (int.TryParse(val, out int si)) m_SessionIndex = si; break;
                    case "-sessionCount": if (int.TryParse(val, out int sc)) m_SessionCount = sc; break;
                    case "-framesPerWindow": if (int.TryParse(val, out int fpw)) m_FramesPerWindow = fpw; break;
                    case "-windows": if (int.TryParse(val, out int wn)) m_WindowsPerCondition = wn; break;
                    case "-buildRepeats": if (int.TryParse(val, out int br)) m_BuildRepeats = br; break;
                    case "-warmup": if (int.TryParse(val, out int wu)) m_WarmupFrames = wu; break;
                    case "-maxSeconds": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float ms)) m_MaxSecondsPerCondition = ms; break;
                    default: known = false; break;
                }

                if (known)
                {
                    if (key != "-session" && key != "-sessionIndex" && key != "-sessionCount")
                    {
                        m_PassThroughArgs.Add(key);
                        m_PassThroughArgs.Add(val);
                    }
                    i++;
                }
            }

            if (m_SessionIndex > 0 && !Array.Exists(args, a => a == "-session"))
                m_SessionLabel = "s" + m_SessionIndex;

            Debug.Log($"[IATKBenchmark] session={m_SessionLabel} index={m_SessionIndex}/{m_SessionCount} " +
                      $"frames={m_FramesPerWindow} windows={m_WindowsPerCondition} repeats={m_BuildRepeats} " +
                      $"warmup={m_WarmupFrames} cap={m_MaxSecondsPerCondition} datasets={string.Join("|", m_Datasets)}");
        }

        private string OutputDirectory
        {
            get
            {
                if (Application.isEditor)
                    return Application.persistentDataPath;
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "BenchmarkResults");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private IEnumerator ChainOrQuit()
        {
            if (Application.isEditor || m_SessionCount <= 0)
                yield break;

            if (m_SessionIndex > 0 && m_SessionIndex < m_SessionCount)
            {
                Debug.Log($"[IATKBenchmark] Session {m_SessionIndex}/{m_SessionCount} done - cooling down " +
                          $"{m_ChainCooldownSeconds}s, then relaunching for session {m_SessionIndex + 1}.");
                yield return new WaitForSecondsRealtime(m_ChainCooldownSeconds);

                string buildDir = Path.GetDirectoryName(Application.dataPath) ?? ".";
                string dataFolder = Path.GetFileName(Application.dataPath);
                string exeName = dataFolder.EndsWith("_Data") ? dataFolder.Substring(0, dataFolder.Length - 5) + ".exe" : Application.productName + ".exe";
                string exePath = Path.Combine(buildDir, exeName);

                var nextArgs = new List<string> { "-sessionIndex", (m_SessionIndex + 1).ToString(), "-sessionCount", m_SessionCount.ToString() };
                nextArgs.AddRange(m_PassThroughArgs);
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, string.Join(" ", nextArgs))
                    {
                        WorkingDirectory = buildDir,
                        UseShellExecute = true
                    });
                }
                catch (Exception e)
                {
                    Debug.LogError($"[IATKBenchmark] Failed to relaunch for next session: {e.Message}");
                }
            }

            Application.Quit();
        }

        [ContextMenu("Run Benchmark")]
        public void RunBenchmark()
        {
            if (m_IsRunning) return;
            StartCoroutine(RunAllConditions());
        }

        private IEnumerator RunAllConditions()
        {
            m_IsRunning = true;

            if (m_Datasets.Count == 0)
            {
                Debug.LogError("[IATKBenchmark] No datasets configured - aborting.");
                m_IsRunning = false;
                yield break;
            }

            if (m_PinXRRenderScale && XRSettings.enabled)
            {
                XRSettings.eyeTextureResolutionScale = 1f;
                XRSettings.renderViewportScale = 1f;
            }

            OpenCsv();

            var conditions = new List<string>(m_Datasets);
            if (m_RandomizeOrder) ShuffleList(conditions);

            for (int c = 0; c < conditions.Count; c++)
                yield return RunSingleCondition(conditions[c], c, conditions.Count);

            CloseCsv();
            m_IsRunning = false;
            Debug.Log($"[IATKBenchmark] Complete - {conditions.Count} conditions run. Results: {m_OutputPath}");

            yield return ChainOrQuit();
        }

        private float PointDiameter
        {
            get
            {
                var mgr = MultiplayerScatterplotManager.Instance;
                return mgr != null ? mgr.PointSize : 0.02f;
            }
        }

        /// <summary>One build: load .cdataset, hand it to IATK, let IATK build its view.</summary>
        private long BuildOnce(string datasetFileName)
        {
            DatasetColumnar ds = ColumnarBinaryImporter.Load(datasetFileName);
            m_Source.SetDataset(ds);
            if (ds == null) return 0;

            // IATK billboard width = 0.05 * _Size in view-local units; view scale = axis length,
            // further scaled by the anchor. Min/max size = 0 removes the per-point size term.
            float worldPerUnit = m_AxisLength * m_PlotAnchor.lossyScale.x;
            m_Vis.size = PointDiameter / (0.05f * worldPerUnit);
            m_Vis.minSize = 0f;
            m_Vis.maxSize = 0f;

            m_Vis.CreateVisualisation(AbstractVisualisation.VisualisationTypes.SCATTERPLOT);
            return ds.RowCount;
        }

        private IEnumerator RunSingleCondition(string dataset, int index, int total)
        {
            Debug.Log($"[IATKBenchmark] ({index + 1}/{total}) pipeline={PipelineName} dataset={dataset}");
            yield return null;

            long pointCount = 0;
            for (int r = 0; r < m_BuildRepeats; r++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { pointCount = BuildOnce(dataset); }
                catch (Exception e)
                {
                    Debug.LogError($"[IATKBenchmark] Build failed for {dataset}: {e}");
                    pointCount = 0;
                }
                sw.Stop();

                if (pointCount == 0) break;
                WriteBuildRow(PipelineName, dataset, pointCount, r, sw.Elapsed.TotalMilliseconds);
                yield return null;
            }

            if (pointCount == 0)
            {
                Debug.LogError($"[IATKBenchmark] Dataset load/build failed for '{dataset}' - skipping.");
                WriteErrorRow(PipelineName, dataset, "dataset_load_failed");
                m_Writer?.Flush();
                yield break;
            }

            FreezeView();
            WriteEnvRow(PipelineName, dataset, pointCount);

            var conditionStopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (int f = 0; f < m_WarmupFrames; f++)
            {
                if (m_CaptureView && f == Mathf.Min(10, m_WarmupFrames - 1))
                {
                    string shot = Path.Combine(OutputDirectory,
                        $"view_{m_SessionLabel}_{PipelineName}_{Path.GetFileNameWithoutExtension(dataset)}.png");
                    ScreenCapture.CaptureScreenshot(shot);
                }

                if (conditionStopwatch.Elapsed.TotalSeconds > m_MaxSecondsPerCondition)
                {
                    WriteTimeoutRow(PipelineName, dataset, pointCount, "warmup", conditionStopwatch.Elapsed.TotalSeconds);
                    m_Writer?.Flush();
                    UnfreezeView();
                    yield break;
                }

                yield return null;
            }

            for (int w = 0; w < m_WindowsPerCondition; w++)
            {
                bool completed = true;
                yield return CaptureWindow(PipelineName, dataset, pointCount, w, conditionStopwatch, result => completed = result);

                if (!completed)
                {
                    WriteTimeoutRow(PipelineName, dataset, pointCount, $"window_{w}", conditionStopwatch.Elapsed.TotalSeconds);
                    m_Writer?.Flush();
                    UnfreezeView();
                    yield break;
                }
            }

            m_Writer?.Flush();
            UnfreezeView();
            yield return null;
        }

        private IEnumerator CaptureWindow(string pipelineName, string datasetFileName, long pointCount, int windowIndex, System.Diagnostics.Stopwatch conditionStopwatch, Action<bool> onComplete)
        {
            long memBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            int gpuTimingHits = 0;
            const int kTimingHistoryDepth = 8;
            FrameTiming[] timings = new FrameTiming[kTimingHistoryDepth];

            for (int f = 0; f < m_FramesPerWindow; f++)
            {
                if (conditionStopwatch.Elapsed.TotalSeconds > m_MaxSecondsPerCondition)
                {
                    onComplete(false);
                    yield break;
                }

                yield return new WaitForEndOfFrame();

                FrameTimingManager.CaptureFrameTimings();
                uint got = FrameTimingManager.GetLatestTimings(kTimingHistoryDepth, timings);

                double cpuMs = -1.0, gpuMs = -1.0, cpuMainMs = -1.0, cpuRenderMs = -1.0;
                for (int i = 0; i < got; i++)
                {
                    if (timings[i].gpuFrameTime > 0)
                    {
                        cpuMs = timings[i].cpuFrameTime;
                        gpuMs = timings[i].gpuFrameTime;
                        cpuMainMs = timings[i].cpuMainThreadFrameTime;
                        cpuRenderMs = timings[i].cpuRenderThreadFrameTime;
                        break;
                    }
                }
                if (gpuMs > 0) gpuTimingHits++;

                double frameMs = Time.unscaledDeltaTime * 1000.0;
                WriteFrameRow(pipelineName, datasetFileName, pointCount, windowIndex, f, frameMs, cpuMs, gpuMs, cpuMainMs, cpuRenderMs, GetFrameFlags());
            }

            float hitRate = m_FramesPerWindow > 0 ? (float)gpuTimingHits / m_FramesPerWindow : 0f;
            if (hitRate < 0.9f)
                Debug.LogWarning($"[IATKBenchmark] GPU timing reliability low for {pipelineName}/{datasetFileName} window {windowIndex}: {hitRate:P0}");

            long memAfter = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            WriteMemoryRow(pipelineName, datasetFileName, pointCount, windowIndex, memBefore, memAfter);
            onComplete(true);
        }

        // ---- CSV (same schema as BenchmarkingManager) -----------------------

        private void OpenCsv()
        {
            string environmentTag = Application.isEditor ? "editor" : "standalone";
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string baseName = Path.GetFileNameWithoutExtension(m_OutputFileName);
            string extension = Path.GetExtension(m_OutputFileName);
            string session = string.IsNullOrWhiteSpace(m_SessionLabel) ? "" : "_" + m_SessionLabel.Trim();
            string taggedFileName = $"{baseName}_{PipelineName}_{environmentTag}{session}_{timestamp}{extension}";

            m_OutputPath = Path.Combine(OutputDirectory, taggedFileName);
            m_Writer = new StreamWriter(m_OutputPath, false);
            m_Writer.WriteLine("RowType,Pipeline,Dataset,PointCount,Window,Frame,FrameTimeMs,CpuTimeMs,GpuTimeMs,BuildTimeMs,MemBeforeBytes,MemAfterBytes,Timestamp,Stage,CpuMainMs,CpuRenderMs,Flags");
            Debug.Log($"[IATKBenchmark] Writing to {m_OutputPath}");
        }

        private void WriteFrameRow(string pipeline, string dataset, long pointCount, int window, int frame, double frameMs, double cpuMs, double gpuMs, double cpuMainMs, double cpuRenderMs, string flags)
        {
            var ci = CultureInfo.InvariantCulture;
            m_Writer?.WriteLine($"frame,{pipeline},{dataset},{pointCount},{window},{frame}," +
                                $"{frameMs.ToString("F4", ci)},{cpuMs.ToString("F4", ci)},{gpuMs.ToString("F4", ci)},,,,{DateTime.UtcNow:o},," +
                                $"{cpuMainMs.ToString("F4", ci)},{cpuRenderMs.ToString("F4", ci)},{flags}");
        }

        private void WriteBuildRow(string pipeline, string dataset, long pointCount, int repeat, double buildMs) =>
            m_Writer?.WriteLine($"build,{pipeline},{dataset},{pointCount},{repeat},,,,,{buildMs.ToString("F4", CultureInfo.InvariantCulture)},,,{DateTime.UtcNow:o},,,,");

        private void WriteMemoryRow(string pipeline, string dataset, long pointCount, int window, long memBefore, long memAfter) =>
            m_Writer?.WriteLine($"memory,{pipeline},{dataset},{pointCount},{window},,,,,,{memBefore},{memAfter},{DateTime.UtcNow:o},,,,");

        private void WriteTimeoutRow(string pipeline, string dataset, long pointCount, string stage, double elapsedSeconds) =>
            m_Writer?.WriteLine($"timeout,{pipeline},{dataset},{pointCount},,,,,,,,,{DateTime.UtcNow:o},{stage}_after_{elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)}s,,,");

        private void WriteErrorRow(string pipeline, string dataset, string reason) =>
            m_Writer?.WriteLine($"error,{pipeline},{dataset},0,,,,,,,,,{DateTime.UtcNow:o},{reason},,,");

        private static string GetFrameFlags()
        {
            string flags = "";
            if (!Application.isFocused) flags = "unfocused";
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid && head.TryGetFeatureValue(CommonUsages.userPresence, out bool present) && !present)
                flags = flags.Length > 0 ? flags + "|nopresence" : "nopresence";
            return flags;
        }

        private void WriteEnvRow(string pipeline, string dataset, long pointCount)
        {
            var ci = CultureInfo.InvariantCulture;
            float refresh = -1f;
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var d in displays)
                if (d.running && d.TryGetDisplayRefreshRate(out float hz)) { refresh = hz; break; }

            float urpScale = -1f;
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
                urpScale = urp.renderScale;

            Vector3 camPos = m_ViewCamera != null ? m_ViewCamera.transform.position : Vector3.zero;
            Vector3 camEuler = m_ViewCamera != null ? m_ViewCamera.transform.eulerAngles : Vector3.zero;
            string Clean(string v) => (v ?? "").Replace(",", " ").Replace(";", " ").Replace("=", " ");

            string env =
                $"xr={XRSettings.enabled};device={Clean(XRSettings.loadedDeviceName)};" +
                $"eyeTex={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight};" +
                $"eyeScale={XRSettings.eyeTextureResolutionScale.ToString("F2", ci)};" +
                $"viewportScale={XRSettings.renderViewportScale.ToString("F2", ci)};" +
                $"urpRenderScale={urpScale.ToString("F2", ci)};" +
                $"xrRefreshHz={refresh.ToString("F1", ci)};" +
                $"screen={Screen.width}x{Screen.height};" +
                $"quality={Clean(QualitySettings.names[QualitySettings.GetQualityLevel()])};" +
                $"vSync={QualitySettings.vSyncCount};targetFps={Application.targetFrameRate};" +
                $"gpu={Clean(SystemInfo.graphicsDeviceName)};api={SystemInfo.graphicsDeviceType};" +
                $"viewFrozen={m_ViewFrozen};" +
                $"camPos={camPos.x.ToString("F3", ci)} {camPos.y.ToString("F3", ci)} {camPos.z.ToString("F3", ci)};" +
                $"camRot={camEuler.x.ToString("F1", ci)} {camEuler.y.ToString("F1", ci)} {camEuler.z.ToString("F1", ci)};" +
                $"focused={Application.isFocused};" +
                $"iatkSize={m_Vis.size.ToString("F4", ci)};pointDiameter={PointDiameter.ToString("F4", ci)}";

            m_Writer?.WriteLine($"env,{pipeline},{dataset},{pointCount},,,,,,,,,{DateTime.UtcNow:o},{env},,,");
        }

        // ---- Controlled view (same as BenchmarkingManager) --------------------

        private void OnEnable() => Application.onBeforeRender += ApplyFrozenView;
        private void OnDisable() => Application.onBeforeRender -= ApplyFrozenView;

        private void FreezeView()
        {
            if (!m_FixView) return;

            if (m_ViewCamera == null)
                m_ViewCamera = m_ViewCameraOverride != null ? m_ViewCameraOverride : Camera.main;
            if (m_ViewCamera == null || m_ViewCamera.transform.parent == null)
            {
                Debug.LogWarning("[IATKBenchmark] No XR rig camera with a parent transform found - view NOT fixed.");
                return;
            }

            m_ViewRig = m_ViewCamera.transform.parent;
            m_SavedRigPos = m_ViewRig.position;
            m_SavedRigRot = m_ViewRig.rotation;

            if (m_FixedViewPose != null)
            {
                m_FrozenPos = m_FixedViewPose.position;
                m_FrozenRot = m_FixedViewPose.rotation;
            }
            else
            {
                // Same derivation as BenchmarkingManager: m_ViewDistance in front of the
                // ScatterplotVisualizer (the plot centre), looking at it.
                Vector3 target = m_PlotAnchor.position;
                Vector3 back = -m_PlotAnchor.forward;
                back.y = 0f;
                if (back.sqrMagnitude < 1e-4f) back = -Vector3.forward;
                m_FrozenPos = target + back.normalized * m_ViewDistance;
                m_FrozenRot = Quaternion.LookRotation(target - m_FrozenPos, Vector3.up);
            }

            m_ViewFrozen = true;
            ApplyFrozenView();
        }

        private void ApplyFrozenView()
        {
            if (!m_ViewFrozen || m_ViewCamera == null || m_ViewRig == null) return;
            Transform cam = m_ViewCamera.transform;
            Quaternion rigRot = m_FrozenRot * Quaternion.Inverse(cam.localRotation);
            Vector3 rigPos = m_FrozenPos - rigRot * Vector3.Scale(m_ViewRig.lossyScale, cam.localPosition);
            m_ViewRig.SetPositionAndRotation(rigPos, rigRot);
        }

        private void UnfreezeView()
        {
            if (!m_ViewFrozen) return;
            m_ViewFrozen = false;
            if (m_ViewRig != null) m_ViewRig.SetPositionAndRotation(m_SavedRigPos, m_SavedRigRot);
        }

        private void LateUpdate() => ApplyFrozenView();

        private void CloseCsv()
        {
            m_Writer?.Flush();
            m_Writer?.Close();
            m_Writer = null;
        }

        private static void ShuffleList<T>(List<T> list)
        {
            var rng = new System.Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }
        }

        private void OnDestroy()
        {
            UnfreezeView();
            CloseCsv();
        }
    }
}
