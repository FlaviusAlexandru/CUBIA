using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;

namespace DataViz.MenuUI
{
    /// <summary>
    /// "Editing: &lt;layer&gt;" at the bottom right of the Map, Animate and Filter panels (LFR-25): those
    /// menus edit the active layer, and this names it. Shown only while the plot has two or more
    /// layers, so a one-layer plot looks as before (AT-01). Text comes from LayersPanelModel.BadgeText.
    /// </summary>
    public sealed class ActiveLayerBadge : MonoBehaviour
    {
        const float k_Width = 520f;
        const float k_Height = 30f;

        TMP_Text m_Label;
        MultiplayerScatterplotManager m_Manager;

        /// <summary>Adds the badge to panel once (idempotent by name).</summary>
        public static ActiveLayerBadge Attach(RectTransform panel)
        {
            Transform existing = panel.Find("ActiveLayerBadge");
            if (existing != null)
                return existing.GetComponent<ActiveLayerBadge>();

            MenuTheme theme = PanelKit.Theme;
            float height = panel.rect.height > 0f ? panel.rect.height : panel.sizeDelta.y;
            float width = panel.rect.width > 0f ? panel.rect.width : panel.sizeDelta.x;
            TMP_Text label = PanelKit.Label(panel, "ActiveLayerBadge", "", width - k_Width - 24f, -(height - k_Height - 4f),
                k_Width, k_Height, theme.BodyFontSize * 0.85f, theme.Accent, TextAlignmentOptions.MidlineRight);
            var badge = label.gameObject.AddComponent<ActiveLayerBadge>();
            badge.m_Label = label;
            label.enabled = false;
            return badge;
        }

        void OnEnable()
        {
            if (m_Label == null)
                m_Label = GetComponent<TMP_Text>();
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance != null
                    ? MultiplayerScatterplotManager.Instance
                    : FindAnyObjectByType<MultiplayerScatterplotManager>();
            if (m_Manager != null)
                m_Manager.OnLayersChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            if (m_Manager != null)
                m_Manager.OnLayersChanged -= Refresh;
        }

        void Refresh()
        {
            if (m_Label == null)
                return;
            string text = m_Manager != null ? LayersPanelModel.BadgeText(m_Manager.Stack) : null;
            m_Label.enabled = text != null;
            if (text != null && m_Label.text != text)
                m_Label.text = text;
        }
    }
}
