using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace DataViz.Wizard
{
    /// <summary>
    /// The wizard's world-space panel frame: a header (step, title, subtitle), a body the current
    /// step fills, and a footer for Back / Skip / Next. Set up like the data scene's world-space
    /// canvases (GraphicRaycaster + TrackedDeviceGraphicRaycaster on the UI layer) so ray and poke
    /// both work through the scene's XRUIInputModule.
    /// </summary>
    public sealed class WizardPanel : MonoBehaviour
    {
        public static readonly Vector2 CanvasSize = new Vector2(1600f, 1080f);
        public const float CanvasScale = 0.00065f; // ~1.04 x 0.70 m

        TextMeshProUGUI m_StepLabel;
        TextMeshProUGUI m_Title;
        TextMeshProUGUI m_Subtitle;

        public RectTransform Body { get; private set; }
        public RectTransform Footer { get; private set; }

        public static WizardPanel Create(string name, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(name, typeof(RectTransform));
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0)
                go.layer = uiLayer;
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = Vector3.one * CanvasScale;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            ((RectTransform)go.transform).sizeDelta = CanvasSize;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<TrackedDeviceGraphicRaycaster>();

            var panel = go.AddComponent<WizardPanel>();
            panel.Build();
            return panel;
        }

        void Build()
        {
            Image background = WizardUI.RoundedImage(transform, "Background", WizardUI.Theme.PanelColor, 40f);
            WizardUI.Stretch(background.rectTransform);
            WizardUI.OutlineOverlay(background.transform, WizardUI.Theme.PanelOutline, 40f);

            VerticalLayoutGroup frame = WizardUI.Column(transform, "Frame", 20f, new RectOffset(64, 64, 52, 48));
            frame.childForceExpandWidth = true;
            WizardUI.Stretch((RectTransform)frame.transform);

            m_StepLabel = WizardUI.Text(frame.transform, "", WizardUI.SmallSize, WizardUI.Theme.Accent, FontStyles.Bold);
            m_Title = WizardUI.Text(frame.transform, "", WizardUI.TitleSize, WizardUI.Theme.Text, FontStyles.Bold);
            m_Subtitle = WizardUI.Text(frame.transform, "", WizardUI.BodySize, WizardUI.Theme.TextMuted);

            Body = WizardUI.Rect("Body", frame.transform);
            WizardUI.Size(Body, flexibleHeight: 1f);

            HorizontalLayoutGroup footer = WizardUI.Row(frame.transform, "Footer", 20f, null, TextAnchor.MiddleLeft);
            Footer = (RectTransform)footer.transform;
            WizardUI.Size(footer, -1f, 80f);
        }

        /// <summary>Clears body and footer and sets the header. Empty stepLabel/subtitle hide them.</summary>
        public void Show(string stepLabel, string title, string subtitle)
        {
            WizardUI.Clear(Body);
            WizardUI.Clear(Footer);
            SetText(m_StepLabel, stepLabel);
            SetText(m_Title, title);
            SetText(m_Subtitle, subtitle);
        }

        public void SetSubtitle(string subtitle) => SetText(m_Subtitle, subtitle);

        static void SetText(TextMeshProUGUI text, string value)
        {
            text.text = value;
            text.gameObject.SetActive(!string.IsNullOrEmpty(value));
        }
    }
}
