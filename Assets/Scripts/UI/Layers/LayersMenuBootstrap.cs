using System.Collections;
using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DataViz.MenuUI
{
    /// <summary>
    /// Adds the Layers tab to the summoned menu at runtime, so no scene edit is needed (PLAN B12):
    /// a fifth tile "Layers" in the TileBar (cloned from the last tile, so it keeps the theme), the
    /// five tiles re-laid to equal width, the Panel_Layers panel with a LayersPanel, and the
    /// "Editing: …" badge on Map, Animate and Filter.
    ///
    /// The new MenuTab keeps m_Button null on purpose: MenuTabBar.Awake wires a click listener to
    /// every tab whose m_Button is set and runs at the first summon, maybe after this bootstrap, so
    /// setting it would toggle twice (PLAN risk 5). The tile's own Button is wired here instead.
    /// Runs after each scene load and again a frame later; idempotent (skips a bar that already
    /// has Tile_Layers). The tile row is in PresenterModeUIGate.m_Excluded already, and the
    /// panel's controls sit two levels under the canvas, so the gate covers them.
    /// </summary>
    static class LayersMenuBootstrap
    {
        public const string TabId = "Layers";
        const string k_TileName = "Tile_Layers";
        const string k_PanelName = "Panel_Layers";
        const float k_TileGap = 24f;

        sealed class Runner : MonoBehaviour { }
        static Runner s_Runner;
        static Sprite s_Icon;

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
            AttachAll();
            Schedule();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            AttachAll();
            Schedule();
        }

        static void Schedule()
        {
            if (s_Runner == null)
            {
                var host = new GameObject("[Layers Menu Bootstrap]") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(host);
                s_Runner = host.AddComponent<Runner>();
            }
            s_Runner.StartCoroutine(AttachNextFrame());
        }

        static IEnumerator AttachNextFrame()
        {
            yield return null;
            AttachAll();
        }

        static void AttachAll()
        {
            foreach (MenuTabBar bar in Object.FindObjectsByType<MenuTabBar>(FindObjectsInactive.Include))
            {
                if (bar == null || bar.transform.Find(k_TileName) != null || bar.m_Tabs.Count == 0)
                    continue;
                try
                {
                    Attach(bar);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        static void Attach(MenuTabBar bar)
        {
            MenuTab template = bar.m_Tabs[bar.m_Tabs.Count - 1];
            RectTransform templatePanel = FindPanel(bar);
            if (template == null || templatePanel == null)
            {
                Debug.LogWarning("[LayersMenuBootstrap] Tile or panel template not found; Layers tab not added.");
                return;
            }
            Transform canvas = templatePanel.parent;

            // ---- panel, placed like the other panels (same anchors, top edge and width)
            var panelGo = new GameObject(k_PanelName, typeof(RectTransform), typeof(Image));
            panelGo.layer = canvas.gameObject.layer;
            var panel = (RectTransform)panelGo.transform;
            panel.SetParent(canvas, false);
            panel.anchorMin = templatePanel.anchorMin;
            panel.anchorMax = templatePanel.anchorMax;
            panel.pivot = templatePanel.pivot;
            panel.anchoredPosition = templatePanel.anchoredPosition;
            panel.localScale = templatePanel.localScale;
            panel.sizeDelta = new Vector2(LayersPanel.PanelWidth, LayersPanel.PanelHeight);
            PanelKit.Theme.StylePanel(panelGo.GetComponent<Image>());
            panel.SetSiblingIndex(LastPanelSibling(bar) + 1); // under the bookmark palette, which renders over the panels
            LayersPanel.Build(panel, bar);
            panelGo.SetActive(false);

            // ---- tile, a copy of the last one
            GameObject tileGo = Object.Instantiate(template.gameObject, bar.transform, false);
            tileGo.name = k_TileName;
            var tab = tileGo.GetComponent<MenuTab>();
            tab.m_Id = TabId;
            tab.m_Button = null;   // see the class comment (PLAN risk 5)
            tab.m_Panel = panel;
            tab.m_ShowsBookmarkPalette = false;
            if (tab.m_Label != null) tab.m_Label.text = "Layers";
            if (tab.m_Icon != null)
            {
                tab.m_Icon.sprite = LayersIcon();
                tab.m_Icon.preserveAspect = true;
            }

            var button = tileGo.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => bar.Open(bar.OpenTab == tab ? null : tab));
            }
            bar.m_Tabs.Add(tab);
            LayoutTiles(bar);
            tab.SetOpen(bar.OpenTab == tab);

            foreach (MenuTab t in bar.m_Tabs)
            {
                if (t != null && t.m_Panel != null && (t.m_Id == "Map" || t.m_Id == "Animate" || t.m_Id == "Filter"))
                    ActiveLayerBadge.Attach(t.m_Panel);
            }
            Debug.Log("[LayersMenuBootstrap] Layers tab added to " + canvas.name + ".");
        }

        /// <summary>Panel_Map's rect (or the first tab's panel) as the placement template.</summary>
        static RectTransform FindPanel(MenuTabBar bar)
        {
            foreach (MenuTab t in bar.m_Tabs)
                if (t != null && t.m_Id == "Map" && t.m_Panel != null)
                    return t.m_Panel;
            foreach (MenuTab t in bar.m_Tabs)
                if (t != null && t.m_Panel != null)
                    return t.m_Panel;
            return null;
        }

        static int LastPanelSibling(MenuTabBar bar)
        {
            int last = 0;
            foreach (MenuTab t in bar.m_Tabs)
                if (t != null && t.m_Panel != null)
                    last = Mathf.Max(last, t.m_Panel.GetSiblingIndex());
            return last;
        }

        /// <summary>Equal-width tiles across the bar: (width - gaps) / count each. Tile children are laid relative to the tile, so only width and x change.</summary>
        static void LayoutTiles(MenuTabBar bar)
        {
            var barRect = (RectTransform)bar.transform;
            int n = bar.m_Tabs.Count;
            float total = barRect.rect.width > 0f ? barRect.rect.width : barRect.sizeDelta.x;
            float width = (total - (n - 1) * k_TileGap) / n;
            for (int i = 0; i < n; i++)
            {
                if (bar.m_Tabs[i] == null) continue;
                var rt = (RectTransform)bar.m_Tabs[i].transform;
                rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
                rt.anchoredPosition = new Vector2(i * (width + k_TileGap), rt.anchoredPosition.y);
            }
        }

        /// <summary>A white line icon of three stacked layers (tinted by the tile like the SVG icons).</summary>
        static Sprite LayersIcon()
        {
            if (s_Icon != null)
                return s_Icon;
            const int n = 64;
            const float a = 26f, b = 11f;                 // rhombus half width and half height
            float norm = Mathf.Sqrt(1f / (a * a) + 1f / (b * b));
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "LayersIcon", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color32[n * n];
            float[] centres = { 20f, 32f, 44f };           // from the top; the lower two show only their lower edge
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float top = n - 1 - y + 0.5f;
                float alpha = 0f;
                for (int k = 0; k < centres.Length; k++)
                {
                    float dx = x + 0.5f - n * 0.5f, dy = top - centres[k];
                    if (k > 0 && dy < 0f) continue;
                    float dist = (Mathf.Abs(dx) / a + Mathf.Abs(dy) / b - 1f) / norm;
                    alpha = Mathf.Max(alpha, Mathf.Clamp01(2.2f - Mathf.Abs(dist)));
                }
                pixels[y * n + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            s_Icon = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            s_Icon.hideFlags = HideFlags.DontSave;
            return s_Icon;
        }
    }
}
