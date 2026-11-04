using System.Linq;
using DataViz.Networking.EditorTools;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz.Networking
{
    /// <summary>
    /// Tools > CUBIA > Multiplayer > 5 Move Network To Bootstrap. Run once on the open data scene
    /// (after the new menu is built, since the strip goes into its "SessionStripAnchor"):
    ///  1. turns on NGO scene management on the scene's "Network" object, saves that object as the
    ///     prefab Resources/CUBIANetwork (spawned at startup by <see cref="NetworkBootstrap"/>) and
    ///     removes it from the scene;
    ///  2. removes the old Session island (SessionIslandBackground, SessionIslandController);
    ///  3. adds the session strip into SessionStripAnchor (or below the summoned menu canvas);
    ///  4. makes sure the strip's menu canvas has a PresenterModeUIGate and marks the known
    ///     local-only controls PresenterModeExempt.
    /// Idempotent. Marks the scene dirty; saving is left to the user.
    /// </summary>
    static class NetworkBootstrapMenu
    {
        const string k_PrefabFolder = "Assets/Scripts/Networking/Resources";
        const string k_PrefabPath = k_PrefabFolder + "/" + NetworkBootstrap.PrefabResourcePath + ".prefab";
        const string k_AnchorName = "SessionStripAnchor";
        const string k_StripName = "SessionStrip";
        const string k_FallbackCanvas = "SummonedMenuCanvas";
        const float k_FallbackStripHeight = 80f;

        // Local-only controls of the old menu that stay usable in presenter mode.
        static readonly string[] k_LocalOnlyNames =
        {
            "TooltipsToggle", "TooltipAnchor", "BookmarkPalettePanel", "ShuffleLocksSection",
            "BlacklistColumnDropdown", "BookmarkColumnDropdown",
        };

        [MenuItem("Tools/CUBIA/Multiplayer/5 Move Network To Bootstrap")]
        static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[NetworkBootstrapMenu] Exit Play mode first.");
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            Undo.SetCurrentGroupName("Move Network To Bootstrap");
            int undoGroup = Undo.GetCurrentGroup();

            MoveNetworkToPrefab(scene);
            RemoveByName(scene, "SessionIslandBackground");
            RemoveByName(scene, "SessionIslandController");
            SessionStripUI strip = BuildStrip(scene);
            if (strip != null) GateCanvasOf(strip);
            MarkLocalOnlyControls(scene);

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[NetworkBootstrapMenu] Done: network prefab at " + k_PrefabPath + ", session island removed, strip added. " +
                      "Save the scene; add Start, SampleScene and Lobby to Build Settings.");
        }

        // ------------------------------------------------------------------ network

        static void MoveNetworkToPrefab(Scene scene)
        {
            NetworkManager manager = scene.GetRootGameObjects()
                .SelectMany(r => r.GetComponentsInChildren<NetworkManager>(true)).FirstOrDefault();

            if (manager == null)
            {
                EnsurePrefabSceneManagement();
                return;
            }

            Undo.RecordObject(manager, "Move Network To Bootstrap");
            manager.NetworkConfig.EnableSceneManagement = true;
            if (!AssetDatabase.IsValidFolder(k_PrefabFolder))
                AssetDatabase.CreateFolder("Assets/Scripts/Networking", "Resources");

            string sceneName = manager.gameObject.name;
            PrefabUtility.SaveAsPrefabAsset(manager.gameObject, k_PrefabPath, out bool saved);
            if (!saved)
            {
                Debug.LogError($"[NetworkBootstrapMenu] Couldn't save {k_PrefabPath}; '{sceneName}' left in the scene.");
                return;
            }
            Undo.DestroyObjectImmediate(manager.gameObject);
            Debug.Log($"[NetworkBootstrapMenu] '{sceneName}' saved as {k_PrefabPath} (scene management on) and removed from the scene.");
        }

        static void EnsurePrefabSceneManagement()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(k_PrefabPath);
            if (root == null)
            {
                Debug.LogError($"[NetworkBootstrapMenu] No NetworkManager in the scene and no {k_PrefabPath}. " +
                               "Run Multiplayer menus 1-3 on the data scene first.");
                return;
            }
            try
            {
                var manager = root.GetComponent<NetworkManager>();
                if (manager != null && !manager.NetworkConfig.EnableSceneManagement)
                {
                    manager.NetworkConfig.EnableSceneManagement = true;
                    PrefabUtility.SaveAsPrefabAsset(root, k_PrefabPath);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ------------------------------------------------------------------ strip

        static SessionStripUI BuildStrip(Scene scene)
        {
            RemoveByName(scene, k_StripName);

            RectTransform anchor = FindByName(scene, k_AnchorName) as RectTransform;
            RectTransform strip;
            if (anchor != null)
            {
                strip = NewRect(k_StripName, anchor);
                strip.anchorMin = Vector2.zero;
                strip.anchorMax = Vector2.one;
                strip.offsetMin = strip.offsetMax = Vector2.zero;
            }
            else
            {
                var canvas = FindByName(scene, k_FallbackCanvas) as RectTransform;
                if (canvas == null)
                {
                    Debug.LogError($"[NetworkBootstrapMenu] Neither '{k_AnchorName}' nor '{k_FallbackCanvas}' found; no session strip added.");
                    return null;
                }
                Debug.LogWarning($"[NetworkBootstrapMenu] No '{k_AnchorName}'; the strip goes just below '{k_FallbackCanvas}'.");
                strip = NewRect(k_StripName, canvas);
                strip.anchorMin = new Vector2(0f, 0f);
                strip.anchorMax = new Vector2(1f, 0f);
                strip.pivot = new Vector2(0.5f, 1f);
                strip.sizeDelta = new Vector2(0f, k_FallbackStripHeight);
                strip.anchoredPosition = new Vector2(0f, -16f);
            }

            var ui = strip.gameObject.AddComponent<SessionStripUI>(); // also adds PresenterModeExempt
            var so = new SerializedObject(ui);
            SerializedProperty icons = so.FindProperty("m_Icons");
            UiIcons loaded = UiIconsLoader.Load();
            foreach (var field in typeof(UiIcons).GetFields())
                icons.FindPropertyRelative(field.Name).objectReferenceValue = (Object)field.GetValue(loaded);
            so.ApplyModifiedPropertiesWithoutUndo();
            return ui;
        }

        static void GateCanvasOf(SessionStripUI strip)
        {
            Canvas canvas = strip.GetComponentInParent<Canvas>(true);
            if (canvas == null) return;
            Canvas root = canvas.rootCanvas;
            if (root.GetComponent<PresenterModeUIGate>() == null)
            {
                Undo.AddComponent<PresenterModeUIGate>(root.gameObject);
                Debug.Log($"[NetworkBootstrapMenu] Added a PresenterModeUIGate to '{root.name}'.");
            }
        }

        static void MarkLocalOnlyControls(Scene scene)
        {
            foreach (string name in k_LocalOnlyNames)
            {
                foreach (Transform t in AllTransforms(scene).Where(t => t.name == name))
                {
                    if (t.GetComponent<PresenterModeExempt>() == null)
                        Undo.AddComponent<PresenterModeExempt>(t.gameObject);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            Undo.RegisterCreatedObjectUndo(go, "Move Network To Bootstrap");
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        static void RemoveByName(Scene scene, string name)
        {
            foreach (Transform t in AllTransforms(scene).Where(t => t.name == name).ToList())
            {
                if (t != null) Undo.DestroyObjectImmediate(t.gameObject);
            }
        }

        static Transform FindByName(Scene scene, string name) => AllTransforms(scene).FirstOrDefault(t => t.name == name);

        static System.Collections.Generic.IEnumerable<Transform> AllTransforms(Scene scene) =>
            scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true));
    }
}
