using System.IO;
using DataViz.Flow;
using TMPro;
using Unity.VectorGraphics.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace DataViz.Wizard.EditorTools
{
    /// <summary>
    /// Tools > CUBIA > Scenes > Build Start Scene. Creates or rebuilds Assets/Scenes/Start.unity, the
    /// import wizard's scene: the same XR rig prefab as SampleScene (with the same overrides that
    /// matter), an XRUIInputModule event system, the studio floor and sky, and the StartSceneBoot
    /// that opens the wizard panel. Also creates the wizard's Resources/WizardAssets (font, icons).
    ///
    /// Idempotent: the scene is regenerated from scratch on every run. It is opened additively and
    /// closed again, so the scenes already open (e.g. SampleScene) are left untouched. Adding the
    /// scene to Build Settings (index 0) is left to the integrator.
    /// </summary>
    static class StartSceneSetupMenu
    {
        const string k_ScenePath = "Assets/Scenes/" + SceneNames.Start + ".unity";
        const string k_RigPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Hands Variant.prefab";
        const string k_PermissionsPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Hands Permissions Manager.prefab";
        const string k_Font = "Assets/VRTemplateAssets/Fonts/Inter/Inter-Regular SDF.asset";
        const string k_PostProfile = "Assets/VRTemplateAssets/Graphics/Post Process Volume Profile.asset";
        const string k_FloorMaterial = "Assets/Environment/Materials/StudioFloor.mat";
        const string k_SkyMaterial = "Assets/Environment/Materials/StudioSky.mat";
        const string k_Reflection = "Assets/Environment/StudioReflection.cubemap";
        const string k_IconFolder = "Assets/UI/Icons/";
        const string k_ResourcesFolder = "Assets/Scripts/Wizard/Resources";

        // Wizard panel: centred in front of the rig (which faces +Z), slightly below eye height.
        static readonly Vector3 k_PanelPosition = new Vector3(0f, 1.4f, 1.4f);

        [MenuItem("Tools/CUBIA/Scenes/Build Start Scene")]
        static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[StartScene] Leave Play mode first.");
                return;
            }

            BuildWizardAssets();

            Scene scene = SceneManager.GetSceneByPath(k_ScenePath);
            bool wasOpen = scene.IsValid() && scene.isLoaded;
            if (!wasOpen)
                scene = File.Exists(k_ScenePath)
                    ? EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Additive)
                    : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            // New objects go to the active scene, so Start is active while it is built.
            Scene previous = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(scene);

            foreach (GameObject root in scene.GetRootGameObjects())
                Object.DestroyImmediate(root);

            BuildRig(scene);
            BuildEventSystem();
            InstantiatePrefab(k_PermissionsPrefab, scene);
            BuildLighting();
            BuildFloor();
            BuildWizard();
            ApplyRenderSettings();

            Directory.CreateDirectory(Path.GetDirectoryName(k_ScenePath));
            EditorSceneManager.SaveScene(scene, k_ScenePath);
            if (previous.IsValid() && previous.isLoaded && previous != scene)
                SceneManager.SetActiveScene(previous);
            if (!wasOpen)
                EditorSceneManager.CloseScene(scene, true);

            Debug.Log($"[StartScene] Built {k_ScenePath}. Add it to Build Settings at index 0.");
        }

        static void BuildRig(Scene scene)
        {
            GameObject rig = InstantiatePrefab(k_RigPrefab, scene);
            if (rig == null)
                return;
            rig.name = "XR Origin Hands (XR Rig)";
            rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // The overrides SampleScene's rig instance makes (tooltip layout ones aside).
            SetSerialized(rig.GetComponent<CharacterController>(), "m_Enabled", p => p.boolValue = false);
            foreach (NearFarInteractor interactor in rig.GetComponentsInChildren<NearFarInteractor>(true))
                SetSerialized(interactor, "m_InteractionLayers.m_Bits", p => p.longValue = uint.MaxValue);
            foreach (Transform t in rig.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Left Controller" && t.name != "Right Controller")
                    continue;
                var driver = t.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
                SetSerialized(driver, "m_UpdateType", p => p.intValue = 2);
                SetSerialized(driver, "m_IgnoreTrackingState", p => p.boolValue = true);
            }
        }

        static void BuildEventSystem()
        {
            new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem), typeof(XRUIInputModule));
        }

        static void BuildLighting()
        {
            var lighting = new GameObject("Lighting");

            var lightGo = new GameObject("Directional Light");
            lightGo.transform.SetParent(lighting.transform, false);
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.975f, 0.94f);
            light.intensity = 1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.6f;

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(k_PostProfile);
            if (profile != null)
            {
                var volumeGo = new GameObject("Post Process Volume");
                volumeGo.transform.SetParent(lighting.transform, false);
                var volume = volumeGo.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }
        }

        static void BuildFloor()
        {
            var environment = new GameObject("Environment");

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10 x 10 m
            floor.name = "Floor";
            floor.transform.SetParent(environment.transform, false);
            floor.transform.localScale = new Vector3(60f, 1f, 60f);  // as the studio: fades out long before its edge
            var renderer = floor.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(k_FloorMaterial);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            GameObjectUtility.SetStaticEditorFlags(floor, StaticEditorFlags.BatchingStatic);
        }

        static void BuildWizard()
        {
            var wizard = new GameObject("Import Wizard");
            var anchor = new GameObject("Panel Anchor").transform;
            anchor.SetParent(wizard.transform, false);
            anchor.localPosition = k_PanelPosition;

            var boot = wizard.AddComponent<StartSceneBoot>();
            SetSerialized(boot, "m_PanelAnchor", p => p.objectReferenceValue = anchor);
        }

        /// <summary>Sky, ambient and reflections of the active scene (Start, while it is built).</summary>
        static void ApplyRenderSettings()
        {
            RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(k_SkyMaterial);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.53f, 0.57f);
            RenderSettings.ambientEquatorColor = new Color(0.44f, 0.45f, 0.47f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.31f, 0.32f);
            RenderSettings.fog = false;
            var reflection = AssetDatabase.LoadAssetAtPath<Cubemap>(k_Reflection);
            if (reflection != null)
            {
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = reflection;
            }
        }

        /// <summary>Resources/WizardAssets: the menu font and the wizard_*.svg icons as sprites.</summary>
        static void BuildWizardAssets()
        {
            if (!AssetDatabase.IsValidFolder(k_ResourcesFolder))
                AssetDatabase.CreateFolder("Assets/Scripts/Wizard", "Resources");

            string path = $"{k_ResourcesFolder}/{WizardAssets.ResourceName}.asset";
            var assets = AssetDatabase.LoadAssetAtPath<WizardAssets>(path);
            if (assets == null)
            {
                assets = ScriptableObject.CreateInstance<WizardAssets>();
                AssetDatabase.CreateAsset(assets, path);
            }

            assets.Font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(k_Font);
            assets.Back = Icon("wizard_back");
            assets.Next = Icon("wizard_next");
            assets.Dataset = Icon("wizard_dataset");
            assets.LeaveOut = Icon("wizard_leaveout");
            assets.Check = Icon("wizard_check");
            EditorUtility.SetDirty(assets);
            AssetDatabase.SaveAssetIfDirty(assets);
        }

        /// <summary>Loads an icon as a sprite, switching its SVG import to a textured sprite if needed
        /// (same settings as the menu icons: white line art on a 64 x 64 viewBox, tinted by Image.color).</summary>
        static Sprite Icon(string name)
        {
            string path = k_IconFolder + name + ".svg";
            if (AssetImporter.GetAtPath(path) is SVGImporter importer && importer.SvgType != SVGType.TexturedSprite)
            {
                importer.SvgType = SVGType.TexturedSprite;
                importer.KeepTextureAspectRatio = true;
                importer.TextureSize = 256;
                importer.SampleCount = 4;
                importer.FilterMode = FilterMode.Bilinear;
                importer.WrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
                Debug.LogWarning($"[StartScene] Icon {path} is missing or not a sprite; the wizard shows no icon there.");
            return sprite;
        }

        static GameObject InstantiatePrefab(string path, Scene scene)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError($"[StartScene] Prefab not found: {path}");
                return null;
            }
            return (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        }

        static void SetSerialized(Object target, string property, System.Action<SerializedProperty> set)
        {
            if (target == null)
                return;
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(property);
            if (p == null)
            {
                Debug.LogWarning($"[StartScene] {target.GetType().Name}.{property} not found.");
                return;
            }
            set(p);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
