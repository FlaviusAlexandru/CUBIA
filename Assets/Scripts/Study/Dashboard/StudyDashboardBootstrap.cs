using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace DataViz.Study
{
    /// <summary>
    /// Spawns the facilitator dashboard (UIDocument + StudySessionLogger + DashboardController) on
    /// the PC monitor whenever a scene with a MultiplayerScatterplotManager loads. Desktop only:
    /// never on Android (Quest) builds, and skipped with the -nodashboard command-line argument.
    /// The dashboard lives in the manager's scene, so it goes away with it.
    /// </summary>
    public static class StudyDashboardBootstrap
    {
        const string k_ObjectName = "[Facilitator Dashboard]";
        const string k_ThemePath = "CUBIA/Dashboard/DashboardTheme";
        const string k_LayoutPath = "CUBIA/Dashboard/FacilitatorDashboard";
        const string k_DisableArgument = "-nodashboard";

        /// <summary>The dashboard of the current scene, if any (for debugging).</summary>
        public static DashboardController Current { get; private set; }

        static Runner s_Runner;

        /// <summary>Hosts the one-frame wait after a scene load (the manager's Awake sets Instance).</summary>
        sealed class Runner : MonoBehaviour { }

        // Statics survive play mode with domain reload disabled; start clean each time.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            s_Runner = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            // Runtime platform check only (no #if): the editor often has Android as its build
            // target for Quest work, and the dashboard should still show there.
            if (!IsEnabled()) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Schedule(); // the scene that is already loaded
        }

        static bool IsEnabled()
        {
            if (Application.platform == RuntimePlatform.Android) return false;
            foreach (string argument in Environment.GetCommandLineArgs())
                if (string.Equals(argument, k_DisableArgument, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Schedule();

        static void Schedule()
        {
            if (s_Runner == null)
            {
                var host = new GameObject("[Facilitator Dashboard Bootstrap]") { hideFlags = HideFlags.HideInHierarchy };
                UnityEngine.Object.DontDestroyOnLoad(host);
                s_Runner = host.AddComponent<Runner>();
            }
            s_Runner.StartCoroutine(SpawnNextFrame());
        }

        static IEnumerator SpawnNextFrame()
        {
            yield return null;
            TrySpawn();
        }

        static void TrySpawn()
        {
            MultiplayerScatterplotManager manager = MultiplayerScatterplotManager.Instance;
            if (manager == null) return;

            Scene scene = manager.gameObject.scene;
            if (Current != null && Current.gameObject.scene == scene) return;
            var existing = UnityEngine.Object.FindAnyObjectByType<DashboardController>();
            if (existing != null && existing.gameObject.scene == scene)
            {
                Current = existing;
                return;
            }

            var go = new GameObject(k_ObjectName);
            go.SetActive(false); // assign everything before UIDocument/logger enable
            PlaceInScene(go, scene);

            var theme = Resources.Load<ThemeStyleSheet>(k_ThemePath);
            var layout = Resources.Load<VisualTreeAsset>(k_LayoutPath);
            if (theme == null || layout == null)
            {
                Debug.LogError($"[StudyDashboardBootstrap] Could not load Resources/{k_ThemePath} (.tss) or " +
                               $"Resources/{k_LayoutPath} (.uxml); facilitator dashboard not created.");
                UnityEngine.Object.Destroy(go);
                return;
            }

            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = "Facilitator Dashboard Panel";
            settings.themeStyleSheet = theme;
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;
            settings.sortingOrder = 1000;
            settings.targetTexture = null; // draw straight to the desktop window, never into XR

            var document = go.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.visualTreeAsset = layout;
            var logger = go.AddComponent<StudySessionLogger>();
            var controller = go.AddComponent<DashboardController>();
            controller.OwnedPanelSettings = settings;

            go.SetActive(true);
            try
            {
                logger.Initialise(manager);
                controller.Initialise(document, logger, manager);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                UnityEngine.Object.Destroy(go);
                return;
            }

            Current = controller;
        }

        static void PlaceInScene(GameObject go, Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || go.scene == scene) return;
            // A manager kept with DontDestroyOnLoad: follow it there (MoveGameObjectToScene can't target that scene).
            if (scene.buildIndex == -1 && scene.name == "DontDestroyOnLoad")
            {
                UnityEngine.Object.DontDestroyOnLoad(go);
                return;
            }
            SceneManager.MoveGameObjectToScene(go, scene);
        }
    }
}
