using DataViz.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.MenuUI
{
    /// <summary>
    /// One tile of the summoned menu's tile row (Map, Animate, Filter, Settings): the tile's visuals
    /// and the panel it opens. MenuTabBar decides which tab is open; this only draws the state.
    /// </summary>
    public class MenuTab : MonoBehaviour
    {
        [Tooltip("Stable id saved to PlayerPrefs as the last used tile; don't rename once shipped.")]
        public string m_Id;
        public Button m_Button;
        [Tooltip("Opened below the tile row while this tab is active.")]
        public RectTransform m_Panel;
        [Tooltip("Show the bookmark palette strip under this tab's panel (it holds drop slots).")]
        public bool m_ShowsBookmarkPalette = true;

        [Header("Visuals")]
        public Image m_Face;
        public Image m_ActiveOutline;
        public Image m_Icon;
        public TMP_Text m_Label;

        public void SetOpen(bool open)
        {
            MenuTheme theme = MenuTheme.Load();

            if (m_Panel != null && m_Panel.gameObject.activeSelf != open)
                m_Panel.gameObject.SetActive(open);
            if (m_Face != null)
                m_Face.color = open ? theme.TileActiveColor : theme.PanelColor;
            if (m_ActiveOutline != null)
            {
                m_ActiveOutline.enabled = open;
                m_ActiveOutline.color = theme.Accent;
            }
            if (m_Icon != null)
                m_Icon.color = open ? theme.Accent : theme.IconTint;
            if (m_Label != null)
                m_Label.color = open ? theme.Accent : theme.Text;
        }
    }
}
