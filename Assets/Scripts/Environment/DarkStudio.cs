using System.Collections;
using DataViz.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DataViz
{
    /// <summary>
    /// PF-02 (pilot 2): the plot text stays white, so the VR surroundings go dark. At runtime only
    /// (nothing in the scenes, prefabs or material assets changes) it recolours the studio built by
    /// Tools > CUBIA > Environment > Build Studio Environment: a near-black sky, a dark floor whose
    /// grid, distance rings and plot footprint turn light, light ring labels and scale posts, a
    /// dark table top; and it recolours the menus baked into the scenes with the light MenuTheme
    /// colours to the dark theme (MenuTheme.RemapToDark). Menus built at runtime get the dark
    /// theme straight from MenuTheme.Load(). The facilitator dashboard (desktop UI Toolkit) is not
    /// part of the VR environment and is left as is.
    /// </summary>
    public static class DarkStudio
    {
        /// <summary>False to keep the light studio (the menus follow MenuTheme.DarkInPlayMode).</summary>
        public static bool Enabled = true;

        public static readonly Color SkyZenith = new Color(0.015f, 0.018f, 0.025f);
        public static readonly Color SkyHorizon = new Color(0.055f, 0.06f, 0.07f);
        public static readonly Color SkyGround = new Color(0.035f, 0.038f, 0.045f);

        // Alex's floor (17 Nov 2026): grid lines and distance rings in the horizon colour, so they
        // vanish and do not compete with the data; only the plot footprint stays light.
        public static readonly Color FloorBase = new Color(0.059f, 0.067f, 0.075f);
        public static readonly Color FloorHorizon = new Color(0.055f, 0.059f, 0.071f);
        public static readonly Color FloorMinor = FloorHorizon;
        public static readonly Color FloorMajor = FloorHorizon;
        public static readonly Color Ring = FloorHorizon;
        public static readonly Color Footprint = new Color(0.78f, 0.84f, 0.92f);
        /// <summary>The ring labels and scale posts ("5 m", "10 m", …) are hidden with the rings.</summary>
        public static bool ShowScaleMarkers = false;
        static readonly Color k_MarkerColour = new Color(0.85f, 0.88f, 0.93f);
        public static readonly Color Post = new Color(0.82f, 0.85f, 0.90f);
        public static readonly Color TableTop = new Color(0.13f, 0.14f, 0.16f);

        const string k_DarkTag = " (dark)";

        /// <summary>Recolours one loaded scene. Safe to call again (idempotent).</summary>
        public static void Apply(Scene scene)
        {
            if (!Enabled || !scene.IsValid() || !scene.isLoaded)
                return;
            bool studio = false;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "Environment")
                    studio |= ApplyEnvironment(root.transform);
                if (root.name == "DataVisualizationTable")
                    ApplyTable(root.transform);
            }
            // Only where the studio was found: the Start scene has its own floor, which stays light.
            if (studio && scene == SceneManager.GetActiveScene())
                ApplySky();
            RemapMenus(scene);
        }

        static void ApplySky()
        {
            Material sky = RenderSettings.skybox;
            if (sky != null && !sky.name.EndsWith(k_DarkTag) && sky.HasProperty("_ZenithColor"))
            {
                var dark = new Material(sky) { name = sky.name + k_DarkTag };
                dark.SetColor("_ZenithColor", SkyZenith);
                dark.SetColor("_HorizonColor", SkyHorizon);
                if (dark.HasProperty("_GroundColor")) dark.SetColor("_GroundColor", SkyGround);
                RenderSettings.skybox = dark;
            }
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam.clearFlags == CameraClearFlags.SolidColor)
                    cam.backgroundColor = SkyHorizon;
            }
            // The custom reflection cubemap is the light studio's: keep glossy surfaces from glowing.
            RenderSettings.reflectionIntensity = Mathf.Min(RenderSettings.reflectionIntensity, 0.25f);
        }

        // True when the environment holds the studio.
        static bool ApplyEnvironment(Transform environment)
        {
            Transform studio = environment.Find("Studio");
            if (studio == null)
                return false;

            Transform floor = studio.Find("Floor");
            Renderer floorRenderer = floor != null ? floor.GetComponent<Renderer>() : null;
            if (floorRenderer != null)
            {
                Material m = floorRenderer.material; // instance; the asset stays light
                SetColour(m, "_BaseColor", FloorBase);
                SetColour(m, "_HorizonColor", FloorHorizon);
                SetColour(m, "_MinorColor", FloorMinor);
                SetColour(m, "_MajorColor", FloorMajor);
                SetColour(m, "_RingColor", Ring);
                SetColour(m, "_FootprintColor", Footprint);
            }

            Transform markers = studio.Find("ScaleMarkers");
            if (markers == null)
                return true;
            markers.gameObject.SetActive(ShowScaleMarkers);
            if (!ShowScaleMarkers)
                return true;
            foreach (TMP_Text label in markers.GetComponentsInChildren<TMP_Text>(true))
                label.color = new Color(k_MarkerColour.r, k_MarkerColour.g, k_MarkerColour.b, 0.9f);
            foreach (Renderer r in markers.GetComponentsInChildren<Renderer>(true))
            {
                if (r.GetComponent<TMP_Text>() != null || r.GetComponent<TMP_SubMesh>() != null)
                    continue;
                SetColour(r.material, "_BaseColor", r.name == "Cap" ? k_MarkerColour : Post);
            }
            return true;
        }

        static void ApplyTable(Transform tableRoot)
        {
            Transform top = tableRoot.Find("Table");
            Renderer r = top != null ? top.GetComponent<Renderer>() : null;
            if (r != null)
                SetColour(r.material, "_BaseColor", TableTop);
        }

        /// <summary>Every UI graphic in the scene (inactive ones too) that has a light theme colour gets its dark one. Returns how many changed.</summary>
        public static int RemapMenus(Scene scene)
        {
            MenuTheme light = MenuTheme.LoadLight();
            MenuTheme dark = MenuTheme.Load();
            if (light == null || dark == null || ReferenceEquals(light, dark))
                return 0;
            int changed = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Graphic g in root.GetComponentsInChildren<Graphic>(true))
                {
                    Color c = g.color;
                    Color d = MenuTheme.RemapToDark(c, light, dark);
                    if (d != c)
                    {
                        g.color = d;
                        changed++;
                    }
                }
            }
            return changed;
        }

        static void SetColour(Material m, string property, Color colour)
        {
            if (m != null && m.HasProperty(property))
                m.SetColor(property, colour);
        }
    }

    /// <summary>Runs DarkStudio on every scene load, and again a frame and a second later for UI made in Start (the PlotLegendBootstrap pattern).</summary>
    static class DarkStudioBootstrap
    {
        sealed class Runner : MonoBehaviour { }
        static Runner s_Runner;

        // Statics survive play mode with domain reload disabled; start clean each time.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Runner = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplyAll();
            Schedule();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            DarkStudio.Apply(scene);
            Schedule();
        }

        static void Schedule()
        {
            if (s_Runner == null)
            {
                var host = new GameObject("[Dark Studio Bootstrap]") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(host);
                s_Runner = host.AddComponent<Runner>();
            }
            s_Runner.StartCoroutine(Again());
        }

        static IEnumerator Again()
        {
            yield return null;
            ApplyAll();
            yield return new WaitForSecondsRealtime(1f);
            ApplyAll();
        }

        static void ApplyAll()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                DarkStudio.Apply(SceneManager.GetSceneAt(i));
        }
    }
}
