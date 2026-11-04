using System.Collections.Generic;
using UnityEngine;

namespace DataViz.MenuUI
{
    /// <summary>
    /// The summoned menu's tile row. Pressing a tile opens its panel below the row (and closes the
    /// open one); pressing the open tile again closes it. The last tile opened is remembered in
    /// PlayerPrefs, so the menu reopens on it (Map the first time).
    ///
    /// The bookmark palette strip follows the open panel: it sits just under it for tabs whose
    /// panel holds drop slots (Map, Animate, Filter) and is hidden otherwise.
    ///
    /// Lives under the summoned menu canvas, which is inactive until summoned, so OnEnable runs on
    /// every summon and restores the remembered tab.
    /// </summary>
    public class MenuTabBar : MonoBehaviour
    {
        public const string k_LastTabPrefKey = "CUBIA.Menu.LastTab";

        [Tooltip("Left to right. The first one opens the very first time the menu is summoned.")]
        public List<MenuTab> m_Tabs = new();
        [Tooltip("Strip shown under panels that hold drop slots. Same parent as the panels.")]
        public RectTransform m_BookmarkPalette;
        [Tooltip("Vertical gap between the open panel and the bookmark palette, in canvas units.")]
        public float m_PaletteGap = 24f;

        private MenuTab m_OpenTab;

        /// <summary>The open tab, or null when every panel is closed.</summary>
        public MenuTab OpenTab => m_OpenTab;

        private void Awake()
        {
            foreach (MenuTab tab in m_Tabs)
            {
                if (tab == null || tab.m_Button == null) continue;
                MenuTab captured = tab;
                tab.m_Button.onClick.AddListener(() => OnTabPressed(captured));
            }
        }

        private void OnEnable()
        {
            Open(FindTab(PlayerPrefs.GetString(k_LastTabPrefKey, "")) ?? FirstTab());
        }

        private void OnTabPressed(MenuTab tab)
        {
            Open(tab == m_OpenTab ? null : tab);
        }

        /// <summary>Opens the given tab's panel (null closes all) and remembers it.</summary>
        public void Open(MenuTab tab)
        {
            if (tab != null)
                PlayerPrefs.SetString(k_LastTabPrefKey, tab.m_Id);
            Show(tab);
        }

        /// <summary>Opens the given tab's panel (null closes all) without remembering it; also
        /// used by the editor builder to lay out the initial state.</summary>
        public void Show(MenuTab tab)
        {
            m_OpenTab = tab;

            foreach (MenuTab t in m_Tabs)
                if (t != null) t.SetOpen(t == tab);

            UpdateBookmarkPalette();
        }

        private void UpdateBookmarkPalette()
        {
            if (m_BookmarkPalette == null) return;

            RectTransform panel = m_OpenTab != null ? m_OpenTab.m_Panel : null;
            bool show = panel != null && m_OpenTab.m_ShowsBookmarkPalette;
            if (m_BookmarkPalette.gameObject.activeSelf != show)
                m_BookmarkPalette.gameObject.SetActive(show);
            if (!show) return;

            // Panels and palette share a parent, so their local rects can be compared directly.
            float panelBottom = panel.localPosition.y + panel.rect.yMin;
            float paletteTopOffset = m_BookmarkPalette.rect.yMax;
            Vector3 position = m_BookmarkPalette.localPosition;
            position.y = panelBottom - m_PaletteGap - paletteTopOffset;
            m_BookmarkPalette.localPosition = position;
        }

        private MenuTab FindTab(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (MenuTab tab in m_Tabs)
                if (tab != null && tab.m_Id == id) return tab;
            return null;
        }

        private MenuTab FirstTab() => m_Tabs.Count > 0 ? m_Tabs[0] : null;
    }
}
