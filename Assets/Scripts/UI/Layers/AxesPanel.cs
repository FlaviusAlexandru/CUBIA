using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.MenuUI
{
    /// <summary>
    /// The Map tab's axis controls (spec "axis scales and real proportions", ASR-08 and ASR-14),
    /// added at runtime by LayersMenuBootstrap so no scene edit is needed:
    /// - a one-tap button under Reset position that switches Proportional / Fit each axis;
    /// - an "Axes…" button that opens an overlay over the Map panel with every axis setting:
    ///   aspect, padding 0 % / 5 %, Reset axes, Close, and per axis X/Y/Z the range mode (Auto,
    ///   Include 0, Limits, Zoom), the limits (typed on the VR keyboard's number layout), Reverse
    ///   and In group; plus the flattened-axis warning with its one-tap fix.
    /// Every control calls a manager request, so changes are forwarded, logged and synced like any
    /// other plot setting. The controls sit two levels under the menu canvas, so PresenterModeUIGate
    /// locks them in presenter mode (ASR-15); the Axes… and Close buttons stay usable (viewing only).
    /// What is shown comes from AxesPanelModel.Build; the panel refreshes only when
    /// AxesPanelModel.Signature changes (OnPlotSettingsChanged fires every playback frame).
    /// </summary>
    public sealed class AxesPanel : MonoBehaviour
    {
        // Buttons are created with placeholder text: PanelKit adds no label to a button made without text.
        public const string AspectButtonName = "AxesAspectButton";
        public const string OpenButtonName = "AxesOpenButton";
        const string k_OverlayName = "AxesOverlay";
        const float k_Width = 1240f, k_Height = 450f, k_RowH = 64f;
        const float k_MessageSeconds = 4f;

        MultiplayerScatterplotManager m_Manager;
        Button m_AspectButton, m_OpenButton;
        GameObject m_Overlay;
        Button m_OverlayAspect, m_PadZero, m_PadDefault, m_Reset, m_Close, m_Fix;
        TMP_Text m_Warning, m_Status;
        readonly Button[,] m_Modes = new Button[3, 4];
        readonly Button[] m_Limits = new Button[3], m_Reverse = new Button[3], m_Group = new Button[3];
        readonly TMP_Text[] m_AxisNames = new TMP_Text[3];
        GameObject m_KeyboardBackdrop;
        VRKeyboard m_Keyboard;
        AxesPanelModel.View m_View;
        int m_Signature;
        bool m_Dirty = true;
        float m_StatusUntil;

        /// <summary>Adds the controls to the Map panel once (idempotent: skips a panel that has them).</summary>
        public static AxesPanel Attach(RectTransform mapPanel)
        {
            if (mapPanel == null)
                return null;
            AxesPanel existing = mapPanel.GetComponent<AxesPanel>();
            if (existing != null)
                return existing;
            var panel = mapPanel.gameObject.AddComponent<AxesPanel>();
            panel.BuildWidgets(mapPanel);
            return panel;
        }

        void BuildWidgets(RectTransform map)
        {
            MenuTheme theme = PanelKit.Theme;
            float small = theme.BodyFontSize * 0.85f;

            // ---- on the Map panel: under Reset position (its rect is centred at 937,-240; sized like its visible button)
            m_AspectButton = PanelKit.Button(map, AspectButtonName, "Proportional", null, 842f, -320f, 190f, 60f, PanelKit.Style.Secondary, small);
            m_AspectButton.onClick.AddListener(ToggleAspect);
            m_OpenButton = PanelKit.Button(map, OpenButtonName, "Axes…", null, 1050f, -320f, 150f, 60f, PanelKit.Style.Secondary, small);
            m_OpenButton.gameObject.AddComponent<PresenterModeExempt>(); // opening is only viewing
            m_OpenButton.onClick.AddListener(() => SetOverlayOpen(true));

            // ---- the overlay, covering the Map panel
            Color fill = new Color(theme.PanelColor.r, theme.PanelColor.g, theme.PanelColor.b, 1f);
            Image overlay = PanelKit.Panel(map, k_OverlayName, 0f, 0f, k_Width, k_Height, fill, theme.PanelOutline);
            m_Overlay = overlay.gameObject;
            Transform o = overlay.transform;

            PanelKit.Label(o, "Title", "Axes", 30f, -20f, 120f, 60f, theme.BodyFontSize * 1.2f, theme.TextMuted);
            m_OverlayAspect = PanelKit.Button(o, "Aspect", "Proportional", null, 160f, -20f, 260f, 60f, PanelKit.Style.Secondary, small);
            m_OverlayAspect.onClick.AddListener(ToggleAspect);
            PanelKit.Label(o, "PaddingLabel", "Padding", 440f, -20f, 110f, 60f, small, theme.TextMuted);
            m_PadZero = PanelKit.Button(o, "Padding0", "0 %", null, 550f, -20f, 100f, 60f, PanelKit.Style.Secondary, small);
            m_PadZero.onClick.AddListener(() => m_Manager?.RequestSetAxisPaddingRpc(0f));
            m_PadDefault = PanelKit.Button(o, "Padding5", "5 %", null, 660f, -20f, 100f, 60f, PanelKit.Style.Secondary, small);
            m_PadDefault.onClick.AddListener(() => m_Manager?.RequestSetAxisPaddingRpc(AxisSettings.DefaultPadding));
            m_Reset = PanelKit.Button(o, "ResetAxes", "Reset axes", null, 830f, -20f, 180f, 60f, PanelKit.Style.Secondary, small);
            m_Reset.onClick.AddListener(() => m_Manager?.RequestResetAxesRpc());
            m_Close = PanelKit.Button(o, "Close", "Close", null, 1030f, -20f, 180f, 60f, PanelKit.Style.Primary, small);
            m_Close.gameObject.AddComponent<PresenterModeExempt>();
            m_Close.onClick.AddListener(() => SetOverlayOpen(false));

            float[] modeX = { 90f, 210f, 370f, 490f };
            float[] modeW = { 110f, 150f, 110f, 100f };
            for (int axis = 0; axis < 3; axis++)
            {
                int a = axis;
                float y = -100f - axis * 78f;
                m_AxisNames[axis] = PanelKit.Label(o, "Axis" + axis, AxisSettings.AxisName(axis), 30f, y, 50f, k_RowH, theme.BodyFontSize, theme.Text);
                for (int m = 0; m < 4; m++)
                {
                    int mode = m;
                    m_Modes[axis, m] = PanelKit.Button(o, $"Mode{axis}_{m}", AxesPanelModel.ModeNames[m], null, modeX[m], y, modeW[m], k_RowH,
                        PanelKit.Style.Secondary, small);
                    m_Modes[axis, m].onClick.AddListener(() => SetMode(a, (AxisRangeMode)mode));
                }
                m_Limits[axis] = PanelKit.Button(o, "Limits" + axis, "…", null, 600f, y, 270f, k_RowH, PanelKit.Style.Secondary, small);
                m_Limits[axis].onClick.AddListener(() => OpenLimitsKeyboard(a));
                m_Reverse[axis] = PanelKit.Button(o, "Reverse" + axis, AxesPanelModel.ReverseText, null, 880f, y, 150f, k_RowH, PanelKit.Style.Secondary, small);
                m_Reverse[axis].onClick.AddListener(() => m_Manager?.RequestSetAxisReverseRpc(a, !(m_Manager.Axes.Reversed(a))));
                m_Group[axis] = PanelKit.Button(o, "Group" + axis, AxesPanelModel.GroupText, null, 1040f, y, 170f, k_RowH, PanelKit.Style.Secondary, small);
                m_Group[axis].onClick.AddListener(() => m_Manager?.RequestSetAxisGroupRpc(a, !(m_Manager.Axes.InGroup(a))));
            }

            m_Warning = PanelKit.Label(o, "Warning", "", 30f, -340f, 760f, 50f, small, PanelKit.Warning);
            m_Fix = PanelKit.Button(o, "Fix", "…", null, 820f, -335f, 390f, 60f, PanelKit.Style.Primary, small);
            m_Fix.onClick.AddListener(FixFlattened);
            m_Status = PanelKit.Label(o, "Status", "", 30f, -395f, 1180f, 44f, small, theme.TextMuted);

            // ---- the VR keyboard for limits, over the overlay (like the Layers tab's rename)
            Image backdrop = PanelKit.Panel(o, "KeyboardBackdrop", 0f, 0f, k_Width, k_Height, fill, theme.PanelOutline);
            m_KeyboardBackdrop = backdrop.gameObject;
            RectTransform host = PanelKit.Rect(backdrop.transform, "KeyboardHost", (k_Width - VRKeyboardFactory.Width) * 0.5f, -14f,
                VRKeyboardFactory.Width, VRKeyboardFactory.Height);
            m_Keyboard = VRKeyboardFactory.Build(host, "Keyboard");
            m_Keyboard.OnClosed += () => { if (m_KeyboardBackdrop != null) m_KeyboardBackdrop.SetActive(false); };
            m_KeyboardBackdrop.SetActive(false);

            m_Overlay.SetActive(false);
        }

        // ------------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance != null
                    ? MultiplayerScatterplotManager.Instance
                    : FindAnyObjectByType<MultiplayerScatterplotManager>();
            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged += MarkDirty;
                m_Manager.OnLayersChanged += MarkDirty;
            }
            m_Dirty = true;
        }

        void OnDisable()
        {
            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged -= MarkDirty;
                m_Manager.OnLayersChanged -= MarkDirty;
            }
        }

        void MarkDirty() => m_Dirty = true;

        void LateUpdate()
        {
            if (m_StatusUntil > 0f && Time.unscaledTime > m_StatusUntil)
            {
                m_StatusUntil = 0f;
                m_Status.text = "";
            }
            if (m_Manager == null || !m_Dirty)
                return;
            m_Dirty = false;

            AxisSettings settings = m_Manager.Axes;
            AxisBounds data = m_Manager.DataBounds;
            byte flattened = m_Manager.FlattenedAxes;
            int signature = AxesPanelModel.Signature(settings, data, flattened);
            if (m_View != null && signature == m_Signature)
                return;
            m_Signature = signature;
            m_View = AxesPanelModel.Build(settings, data, flattened);
            Apply(m_View);
        }

        void Apply(AxesPanelModel.View v)
        {
            SetText(m_AspectButton, v.AspectText);
            SetText(m_OverlayAspect, v.AspectText);
            PanelKit.SetHighlighted(m_PadZero, v.PaddingZero);
            PanelKit.SetHighlighted(m_PadDefault, v.PaddingDefault);

            for (int axis = 0; axis < 3; axis++)
            {
                AxesPanelModel.AxisRow row = v.Rows[axis];
                for (int m = 0; m < 4; m++)
                {
                    m_Modes[axis, m].interactable = row.Numeric;
                    PanelKit.SetHighlighted(m_Modes[axis, m], row.Numeric && m == row.Mode);
                }
                SetText(m_Limits[axis], row.LimitsText);
                m_Limits[axis].interactable = row.Numeric;
                m_Reverse[axis].interactable = row.Numeric;
                PanelKit.SetHighlighted(m_Reverse[axis], row.Numeric && row.Reversed);
                m_Group[axis].interactable = row.Numeric;
                PanelKit.SetHighlighted(m_Group[axis], row.Numeric && row.InGroup);
            }

            m_Warning.text = v.Warning;
            m_Fix.gameObject.SetActive(v.FlattenedMask != 0);
            SetText(m_Fix, v.FixText);
        }

        // ------------------------------------------------------------------ actions

        void ToggleAspect()
        {
            if (m_Manager == null) return;
            m_Manager.RequestSetAxisAspectRpc(m_Manager.Axes.AspectMode == AxisAspect.Proportional ? AxisAspect.FitEach : AxisAspect.Proportional);
        }

        void SetOverlayOpen(bool open)
        {
            if (m_Overlay == null) return;
            if (!open && m_Keyboard != null)
                m_Keyboard.CloseSilently();
            m_Overlay.SetActive(open);
            if (open)
            {
                m_Overlay.transform.SetAsLastSibling();
                m_Dirty = true;
                m_View = null; // rebuild now
            }
        }

        // Limits and Zoom use the stored limits, or the data range when there are none (one tap).
        void SetMode(int axis, AxisRangeMode mode)
        {
            if (m_Manager == null) return;
            AxesPanelModel.LimitsOrData(m_Manager.Axes, m_Manager.DataBounds, axis, out float min, out float max);
            m_Manager.RequestSetAxisRangeRpc(axis, mode, min, max);
        }

        void OpenLimitsKeyboard(int axis)
        {
            if (m_Manager == null || m_Keyboard == null) return;
            string text = AxesPanelModel.KeyboardText(m_Manager.Axes, m_Manager.DataBounds, axis);
            m_KeyboardBackdrop.SetActive(true);
            m_KeyboardBackdrop.transform.SetAsLastSibling();
            m_Keyboard.Open($"{AxisSettings.AxisName(axis)} limits: min and max, with a space between", text, VRKeyboard.Layout.Number, 32, "Set",
                value => SubmitLimits(axis, value));
        }

        void SubmitLimits(int axis, string value)
        {
            if (m_Manager == null) return;
            if (!AxesPanelModel.TryParseLimits(value, out float min, out float max, out string error))
            {
                ShowStatus(error);
                m_Manager.PostPlotMessage(error, PlotMessageKind.Refused);
                return;
            }
            AxisRangeMode mode = m_Manager.Axes.Mode(axis) == AxisRangeMode.Zoom ? AxisRangeMode.Zoom : AxisRangeMode.Limits;
            m_Manager.RequestSetAxisRangeRpc(axis, mode, min, max);
        }

        void FixFlattened()
        {
            if (m_Manager == null) return;
            byte mask = m_Manager.FlattenedAxes;
            for (int axis = 0; axis < 3; axis++)
                if ((mask & (1 << axis)) != 0)
                    m_Manager.RequestSetAxisGroupRpc(axis, false);
        }

        void ShowStatus(string text)
        {
            m_Status.text = text ?? "";
            m_StatusUntil = Time.unscaledTime + k_MessageSeconds;
        }

        static void SetText(Button button, string text)
        {
            TMP_Text label = PanelKit.LabelOf(button);
            if (label != null && label.text != text)
                label.text = text;
        }
    }
}
