using System;
using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.MenuUI
{
    /// <summary>
    /// One row of the Layers tab's list, built with PanelKit: a select button showing the layer's
    /// name, kind and tags and its warning, then show/hide, up, down, duplicate and remove, and
    /// under them the layer's point size stepper (PF-01: resize a layer without selecting it;
    /// hidden on computed layers). Remove asks once more ("Sure?") before it fires, because only
    /// the facilitator can undo.
    /// The row only reports clicks (by layer id); LayersPanel turns them into requests.
    /// </summary>
    public sealed class LayerRowView : MonoBehaviour
    {
        public const float Width = 590f;
        public const float Height = 84f;
        const float k_SelectWidth = 240f;
        const float k_SmallButton = 44f;
        const float k_ButtonH = 40f;      // two button rows: actions on top, the size stepper below
        const float k_ButtonGap = 4f;
        const float k_ConfirmSeconds = 3f;

        public event Action<string> Selected, VisibilityToggled, MovedUp, MovedDown, Duplicated, Removed;
        /// <summary>Layer id and direction (-1 smaller, +1 bigger) of a size stepper click.</summary>
        public event Action<string, int> SizeStepped;

        Button m_Select, m_Eye, m_Up, m_Down, m_Duplicate, m_Remove, m_SizeDown, m_SizeUp;
        TMP_Text m_Name, m_Tags, m_Warning, m_SizeLabel, m_SizeText;
        Image m_ActiveBar;
        string m_LayerId = "";
        float m_ConfirmUntil = -1f;

        public string LayerId => m_LayerId;

        public static LayerRowView Build(Transform parent, string name, float x, float y)
        {
            MenuTheme theme = PanelKit.Theme;
            RectTransform root = PanelKit.Rect(parent, name, x, y, Width, Height);
            var view = root.gameObject.AddComponent<LayerRowView>();

            // Select: a secondary button holding three labels of its own (name, tags, warning).
            view.m_Select = PanelKit.Button(root, "Select", "", null, 0f, 0f, k_SelectWidth, Height, PanelKit.Style.Secondary);
            view.m_ActiveBar = PanelKit.Panel(view.m_Select.transform, "ActiveBar", 4f, -10f, 8f, Height - 20f, theme.Accent, null, 4f);
            view.m_ActiveBar.raycastTarget = false;
            view.m_Name = PanelKit.Label(view.m_Select.transform, "Name", "", 20f, -6f, k_SelectWidth - 30f, 30f, theme.BodyFontSize, theme.Text);
            view.m_Tags = PanelKit.Label(view.m_Select.transform, "Tags", "", 20f, -34f, k_SelectWidth - 30f, 22f, theme.BodyFontSize * 0.72f, theme.TextMuted);
            view.m_Warning = PanelKit.Label(view.m_Select.transform, "Warning", "", 20f, -56f, k_SelectWidth - 30f, 22f, theme.BodyFontSize * 0.72f, PanelKit.Warning);

            float left = k_SelectWidth + 8f;
            float bx = left;
            float by = 0f;
            float small = theme.BodyFontSize * 0.8f;
            view.m_Eye = PanelKit.Button(root, "Visible", "Shown", null, bx, by, 80f, k_ButtonH, PanelKit.Style.Secondary, small);
            bx += 80f + k_ButtonGap;
            view.m_Up = PanelKit.Button(root, "Up", "▲", null, bx, by, k_SmallButton, k_ButtonH, PanelKit.Style.Secondary);
            bx += k_SmallButton + k_ButtonGap;
            view.m_Down = PanelKit.Button(root, "Down", "▼", null, bx, by, k_SmallButton, k_ButtonH, PanelKit.Style.Secondary);
            bx += k_SmallButton + k_ButtonGap;
            view.m_Duplicate = PanelKit.Button(root, "Duplicate", "Copy", null, bx, by, 64f, k_ButtonH, PanelKit.Style.Secondary, small);
            bx += 64f + k_ButtonGap;
            view.m_Remove = PanelKit.Button(root, "Remove", "Remove", null, bx, by, Width - bx, k_ButtonH, PanelKit.Style.Secondary, small);

            // Size stepper (PF-01), second row.
            bx = left;
            by = -(Height - k_ButtonH);
            view.m_SizeLabel = PanelKit.Label(root, "SizeLabel", "Size", bx + 6f, by, 74f, k_ButtonH, small, theme.TextMuted);
            bx += 80f + k_ButtonGap;
            view.m_SizeDown = PanelKit.Button(root, "SizeDown", "−", null, bx, by, 60f, k_ButtonH, PanelKit.Style.Secondary);
            bx += 60f + k_ButtonGap;
            view.m_SizeText = PanelKit.Label(root, "SizeText", "", bx, by, 100f, k_ButtonH, theme.BodyFontSize * 0.9f, theme.Text,
                TMPro.TextAlignmentOptions.Center);
            bx += 100f + k_ButtonGap;
            view.m_SizeUp = PanelKit.Button(root, "SizeUp", "+", null, bx, by, 60f, k_ButtonH, PanelKit.Style.Secondary);

            view.m_Select.onClick.AddListener(() => view.Selected?.Invoke(view.m_LayerId));
            view.m_Eye.onClick.AddListener(() => view.VisibilityToggled?.Invoke(view.m_LayerId));
            view.m_Up.onClick.AddListener(() => view.MovedUp?.Invoke(view.m_LayerId));
            view.m_Down.onClick.AddListener(() => view.MovedDown?.Invoke(view.m_LayerId));
            view.m_Duplicate.onClick.AddListener(() => view.Duplicated?.Invoke(view.m_LayerId));
            view.m_Remove.onClick.AddListener(view.OnRemoveClicked);
            view.m_SizeDown.onClick.AddListener(() => view.SizeStepped?.Invoke(view.m_LayerId, -1));
            view.m_SizeUp.onClick.AddListener(() => view.SizeStepped?.Invoke(view.m_LayerId, +1));
            return view;
        }

        public void Apply(LayersPanelModel.Row row)
        {
            MenuTheme theme = PanelKit.Theme;
            if (row.Id != m_LayerId)
                CancelConfirm();
            m_LayerId = row.Id;

            m_Name.text = row.Name;
            m_Name.color = row.Greyed ? theme.TextMuted : theme.Text;
            m_Tags.text = row.Tags;
            m_Warning.text = row.Warning;
            m_ActiveBar.enabled = row.Active;
            PanelKit.SetHighlighted(m_Eye, row.Visible);
            SetLabel(m_Eye, row.VisibleText);
            m_Up.interactable = row.CanMoveUp;
            m_Down.interactable = row.CanMoveDown;

            m_SizeLabel.gameObject.SetActive(row.ShowSize);
            m_SizeText.gameObject.SetActive(row.ShowSize);
            m_SizeDown.gameObject.SetActive(row.ShowSize);
            m_SizeUp.gameObject.SetActive(row.ShowSize);
            m_SizeText.text = row.SizeText;
            m_SizeDown.interactable = row.CanShrink;
            m_SizeUp.interactable = row.CanGrow;
        }

        void OnRemoveClicked()
        {
            if (Time.unscaledTime <= m_ConfirmUntil)
            {
                CancelConfirm();
                Removed?.Invoke(m_LayerId);
                return;
            }
            m_ConfirmUntil = Time.unscaledTime + k_ConfirmSeconds;
            SetLabel(m_Remove, "Sure?");
            PanelKit.SetHighlighted(m_Remove, true);
        }

        void CancelConfirm()
        {
            m_ConfirmUntil = -1f;
            if (m_Remove == null) return;
            SetLabel(m_Remove, "Remove");
            PanelKit.SetHighlighted(m_Remove, false);
        }

        void Update()
        {
            if (m_ConfirmUntil > 0f && Time.unscaledTime > m_ConfirmUntil)
                CancelConfirm();
        }

        void OnDisable() => CancelConfirm();

        static void SetLabel(Button button, string text)
        {
            TMP_Text label = PanelKit.LabelOf(button);
            if (label != null && label.text != text)
                label.text = text;
        }
    }
}
