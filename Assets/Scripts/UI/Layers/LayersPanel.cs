using System;
using System.Collections.Generic;
using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Option = DataViz.MenuUI.LayersPanelModel.Option;

namespace DataViz.MenuUI
{
    /// <summary>
    /// The summoned menu's Layers tab (LFR-25, LFR-05), built at runtime with PanelKit by
    /// LayersMenuBootstrap. Left: the layer list (select, show/hide, reorder, duplicate, remove;
    /// paged) and the add buttons (dataset from the catalogue, subset of the active layer, computed
    /// layer on the active layer). Right: the active layer's own settings (rename on the VR
    /// keyboard, Normalise, opacity, fixed colour, glyph, shuffle unlock, time behaviour, end-time
    /// and label columns, hide in source; computed parameters), or a picker list while one is open.
    ///
    /// Every control calls a manager request, so the change is forwarded, logged and synced like
    /// any other. What is shown comes from LayersPanelModel.Build (pure, tested). The panel
    /// rebuilds on OnLayersChanged, and otherwise only when LayersPanelModel.Signature changes
    /// (mapping fields, datasets, warnings): OnPlotSettingsChanged and OnFrameBuilt fire every
    /// frame during playback and are not listened to.
    /// </summary>
    public sealed class LayersPanel : MonoBehaviour
    {
        public const float PanelWidth = 1240f;
        public const float PanelHeight = 720f;
        const float k_Left = 30f;
        const float k_Right = 650f;
        const float k_RightWidth = 560f;
        const float k_Top = 70f;
        const float k_Pitch = 62f;
        const float k_RowH = 52f;
        const float k_Half = 275f;
        const float k_MessageSeconds = 4f;

        enum Picker { None, Dataset, SubsetColumn, SubsetValue, Computed, EndColumn, LabelColumn, OrderColumn }

        MultiplayerScatterplotManager m_Manager;
        ScatterplotVisualizer m_Visualizer;
        LayerLineRenderer m_Lines;
        MenuTabBar m_Bar;

        // list
        readonly List<LayerRowView> m_Rows = new();
        TMP_Text m_EmptyText, m_PageText, m_AddHint;
        Button m_PrevPage, m_NextPage, m_AddDataset, m_AddSubset, m_AddComputed;

        // message line
        TMP_Text m_Message;
        float m_MessageUntil;

        // properties
        RectTransform m_Props;
        TMP_Text m_Title, m_TimeHint, m_Warning, m_OpacityLabel, m_AxisLabel, m_TimeLabel, m_SpanText, m_LineText, m_ValueText;
        Button m_Rename, m_Normalise, m_HideInSource, m_ByColumn, m_Glyph, m_Shuffle, m_EndColumn, m_LabelColumn, m_Flag, m_OrderColumn;
        Button m_SpanDown, m_SpanUp, m_LineDown, m_LineUp, m_ValueDown, m_ValueUp;
        readonly Button[] m_Opacity = new Button[4];
        readonly Button[] m_Colours = new Button[8];
        readonly Button[] m_TimeModes = new Button[3];
        readonly Button[] m_Axes = new Button[4];
        RectTransform m_PointOnly, m_ComputedOnly;

        // picker
        RectTransform m_PickerRoot;
        TMP_Text m_PickerTitle, m_PickerPage;
        readonly Button[] m_PickerItems = new Button[LayersPanelModel.PickerItemsPerPage];
        Button m_PickerPrev, m_PickerNext, m_PickerCancel;
        Picker m_Picker;
        List<Option> m_PickerOptions = new();
        int m_PickerPageIndex;
        string m_PickerLayerId = "";
        int m_SubsetColumn = -1;

        // keyboard
        GameObject m_KeyboardBackdrop;
        VRKeyboard m_Keyboard;

        // state
        LayersPanelModel.View m_View;
        int m_Page;
        bool m_Dirty = true;
        int m_Signature;
        string m_LastActiveId;

        // ------------------------------------------------------------------ build

        /// <summary>Adds the panel's widgets under panel (a 1240 x 720 rect, top-left origin for its children).</summary>
        public static LayersPanel Build(RectTransform panel, MenuTabBar bar)
        {
            var p = panel.gameObject.AddComponent<LayersPanel>();
            p.m_Bar = bar;
            p.BuildWidgets(panel);
            return p;
        }

        void BuildWidgets(RectTransform panel)
        {
            MenuTheme theme = PanelKit.Theme;
            float body = theme.BodyFontSize;

            PanelKit.Label(panel, "Heading_Layers", "Layers", k_Left + 10f, -14f, 300f, 44f, body * 1.2f, theme.TextMuted);
            m_Message = PanelKit.Label(panel, "Message", "", k_Right, -14f, k_RightWidth, 44f, body * 0.85f, PanelKit.Warning);

            // ---- list
            RectTransform list = PanelKit.Rect(panel, "LayerList", k_Left, -k_Top, LayerRowView.Width, 5 * (LayerRowView.Height + 8f));
            m_EmptyText = PanelKit.Label(list, "Empty", "", 10f, 0f, LayerRowView.Width - 20f, 80f, body, theme.TextMuted,
                TextAlignmentOptions.TopLeft, wrap: true);
            for (int i = 0; i < LayersPanelModel.RowsPerPage; i++)
            {
                LayerRowView row = LayerRowView.Build(list, "Row" + i, 0f, -i * (LayerRowView.Height + 8f));
                row.Selected += id => m_Manager?.RequestSetActiveLayerRpc(id);
                row.VisibilityToggled += OnVisibilityToggled;
                row.MovedUp += id => Move(id, -1);
                row.MovedDown += id => Move(id, +1);
                row.Duplicated += id => m_Manager?.RequestDuplicateLayerRpc(id);
                row.Removed += id => m_Manager?.RequestRemoveLayerRpc(id);
                m_Rows.Add(row);
            }

            // Paging is only navigation: it stays usable in presenter mode.
            RectTransform pager = PanelKit.Rect(panel, "Pager", k_Left, -532f, LayerRowView.Width, 50f);
            pager.gameObject.AddComponent<PresenterModeExempt>();
            m_PrevPage = PanelKit.Button(pager, "Prev", "◀", null, 0f, 0f, 60f, 50f, PanelKit.Style.Secondary);
            m_PageText = PanelKit.Label(pager, "Page", "", 70f, 0f, 200f, 50f, body * 0.85f, theme.TextMuted, TextAlignmentOptions.Center);
            m_NextPage = PanelKit.Button(pager, "Next", "▶", null, 280f, 0f, 60f, 50f, PanelKit.Style.Secondary);
            m_PrevPage.onClick.AddListener(() => { m_Page--; m_Dirty = true; });
            m_NextPage.onClick.AddListener(() => { m_Page++; m_Dirty = true; });

            RectTransform add = PanelKit.Rect(panel, "AddLayer", k_Left, -594f, LayerRowView.Width, 110f);
            m_AddDataset = PanelKit.Button(add, "AddDataset", "Add dataset", null, 0f, 0f, 190f, 60f, PanelKit.Style.Primary);
            m_AddSubset = PanelKit.Button(add, "AddSubset", "Add subset", null, 200f, 0f, 190f, 60f, PanelKit.Style.Secondary);
            m_AddComputed = PanelKit.Button(add, "AddComputed", "Add computed", null, 400f, 0f, 190f, 60f, PanelKit.Style.Secondary);
            m_AddHint = PanelKit.Label(add, "Hint", "", 4f, -66f, LayerRowView.Width - 8f, 40f, body * 0.75f, theme.TextMuted,
                TextAlignmentOptions.TopLeft, wrap: true);
            m_AddDataset.onClick.AddListener(() => OpenPicker(Picker.Dataset, ""));
            m_AddSubset.onClick.AddListener(() => OpenPicker(Picker.SubsetColumn, m_Manager != null ? m_Manager.ActiveLayerId : ""));
            m_AddComputed.onClick.AddListener(() => OpenPicker(Picker.Computed, m_Manager != null ? m_Manager.ActiveLayerId : ""));

            PanelKit.Divider(panel, "Divider", k_Right - 16f, -k_Top, 2f, PanelHeight - k_Top - 20f);

            BuildProps(panel, theme);
            BuildPicker(panel, theme);

            // Rename: a full-panel backdrop over everything with the shared VR keyboard on it.
            Image backdrop = PanelKit.Panel(panel, "KeyboardBackdrop", 0f, 0f, PanelWidth, PanelHeight,
                new Color(theme.PanelColor.r, theme.PanelColor.g, theme.PanelColor.b, 0.98f), theme.PanelOutline);
            m_KeyboardBackdrop = backdrop.gameObject;
            RectTransform host = PanelKit.Rect(backdrop.transform, "KeyboardHost", (PanelWidth - VRKeyboardFactory.Width) * 0.5f, -120f,
                VRKeyboardFactory.Width, VRKeyboardFactory.Height);
            m_Keyboard = VRKeyboardFactory.Build(host, "Keyboard");
            m_Keyboard.OnClosed += () => { if (m_KeyboardBackdrop != null) m_KeyboardBackdrop.SetActive(false); };
            m_KeyboardBackdrop.SetActive(false);
        }

        static float RowY(int row) => -row * k_Pitch;

        void BuildProps(RectTransform panel, MenuTheme theme)
        {
            float body = theme.BodyFontSize;
            float small = body * 0.85f;
            m_Props = PanelKit.Rect(panel, "ActiveLayer", k_Right, -k_Top, k_RightWidth, PanelHeight - k_Top);
            RectTransform props = m_Props;

            m_Title = PanelKit.Label(props, "Title", "", 0f, RowY(0), 400f, k_RowH, body, theme.Text);
            m_Rename = PanelKit.Button(props, "Rename", "Rename", null, 410f, RowY(0), 150f, k_RowH, PanelKit.Style.Secondary, small);
            m_Rename.onClick.AddListener(OpenRename);

            m_OpacityLabel = PanelKit.Label(props, "OpacityLabel", "", 0f, RowY(2), 160f, k_RowH, small, theme.TextMuted);
            for (int i = 0; i < m_Opacity.Length; i++)
            {
                float value = LayersPanelModel.OpacitySteps[i];
                m_Opacity[i] = PanelKit.Button(props, "Opacity" + i, Mathf.RoundToInt(value * 100f) + "%", null, 166f + i * 99f, RowY(2),
                    93f, k_RowH, PanelKit.Style.Secondary, small);
                m_Opacity[i].onClick.AddListener(() => Request((m, id) => m.RequestSetLayerOpacityRpc(id, value)));
            }

            Color[] palette = PlayerProfile.Palette;
            for (int i = 0; i < m_Colours.Length && i < palette.Length; i++)
            {
                Color32 colour = palette[i];
                m_Colours[i] = PanelKit.Button(props, "Colour" + i, "", null, i * 50f, RowY(3), 46f, k_RowH, PanelKit.Style.Secondary);
                PanelKit.Dot(m_Colours[i].transform, "Swatch", 9f, -(k_RowH - 28f) * 0.5f, 28f, palette[i]);
                m_Colours[i].onClick.AddListener(() => Request((m, id) => m.RequestSetLayerColourRpc(id, colour)));
            }
            m_ByColumn = PanelKit.Button(props, "ByColumn", "By column", null, 410f, RowY(3), 150f, k_RowH, PanelKit.Style.Secondary, small);
            m_ByColumn.onClick.AddListener(OpenMapTab);

            m_Warning = PanelKit.Label(props, "Warning", "", 0f, RowY(7) - 40f, k_RightWidth, 120f, small, PanelKit.Warning,
                TextAlignmentOptions.TopLeft, wrap: true);

            // ---- point layers
            m_PointOnly = PanelKit.Rect(props, "PointLayer", 0f, 0f, k_RightWidth, PanelHeight - k_Top);
            RectTransform pt = m_PointOnly;
            m_Normalise = PanelKit.Button(pt, "Normalise", "…", null, 0f, RowY(1), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_Normalise.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetLayerNormalisedRpc(l.Id, !l.Normalised)));
            m_HideInSource = PanelKit.Button(pt, "HideInSource", "…", null, k_Half + 10f, RowY(1), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_HideInSource.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetSubsetHiddenInSourceRpc(l.Id, !l.HiddenInSource)));

            m_Glyph = PanelKit.Button(pt, "Glyph", "…", null, 0f, RowY(4), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_Glyph.onClick.AddListener(() => RequestToggle((m, l) =>
                m.RequestSetLayerGlyphRpc(l.Id, (Mathf.Max(0, l.DefaultGlyph) + 1) % ScatterplotUI.k_GlyphShapeNames.Length)));
            m_Shuffle = PanelKit.Button(pt, "Shuffle", "…", null, k_Half + 10f, RowY(4), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_Shuffle.gameObject.AddComponent<PresenterModeExempt>(); // a local preference, like the field locks
            m_Shuffle.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetLayerShuffleUnlockedRpc(l.Id, !m.IsLayerShuffleUnlocked(l.Id))));

            m_TimeLabel = PanelKit.Label(pt, "TimeLabel", "Time", 0f, RowY(5), 100f, k_RowH, small, theme.TextMuted);
            for (int i = 0; i < m_TimeModes.Length; i++)
            {
                var mode = (LayerTimeBehaviour)i;
                m_TimeModes[i] = PanelKit.Button(pt, "Time" + LayersPanelModel.TimeModeNames[i], LayersPanelModel.TimeModeNames[i], null,
                    110f + i * 150f, RowY(5), 144f, k_RowH, PanelKit.Style.Secondary, small);
                m_TimeModes[i].onClick.AddListener(() => Request((m, id) => m.RequestSetLayerTimeBehaviourRpc(id, mode)));
            }
            m_EndColumn = PanelKit.Button(pt, "EndColumn", "…", null, 0f, RowY(6), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_EndColumn.onClick.AddListener(() => OpenPicker(Picker.EndColumn, m_Manager?.ActiveLayerId));
            m_LabelColumn = PanelKit.Button(pt, "LabelColumn", "…", null, k_Half + 10f, RowY(6), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_LabelColumn.onClick.AddListener(() => OpenPicker(Picker.LabelColumn, m_Manager?.ActiveLayerId));
            m_TimeHint = PanelKit.Label(pt, "TimeHint", "", 0f, RowY(7), k_RightWidth, 34f, small, theme.TextMuted);

            // ---- computed layers
            m_ComputedOnly = PanelKit.Rect(props, "ComputedLayer", 0f, 0f, k_RightWidth, PanelHeight - k_Top);
            RectTransform cp = m_ComputedOnly;
            m_AxisLabel = PanelKit.Label(cp, "AxisLabel", "Driver axis", 0f, RowY(1), 140f, k_RowH, small, theme.TextMuted);
            for (int i = 0; i < m_Axes.Length; i++)
            {
                int axis = i - 1;
                m_Axes[i] = PanelKit.Button(cp, "Axis" + LayersPanelModel.AxisChoiceNames[i], LayersPanelModel.AxisChoiceNames[i], null,
                    146f + i * 104f, RowY(1), 98f, k_RowH, PanelKit.Style.Secondary, small);
                m_Axes[i].onClick.AddListener(() => Request((m, id) => m.RequestSetComputedAxisRpc(id, axis)));
            }
            m_Flag = PanelKit.Button(cp, "Flag", "…", null, 0f, RowY(4), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_Flag.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetComputedFlagRpc(l.Id, !l.Smoothed)));
            m_OrderColumn = PanelKit.Button(cp, "OrderColumn", "…", null, k_Half + 10f, RowY(4), k_Half, k_RowH, PanelKit.Style.Secondary, small);
            m_OrderColumn.onClick.AddListener(() => OpenPicker(Picker.OrderColumn, m_Manager?.ActiveLayerId));

            Stepper(cp, "Span", 0f, RowY(5), out m_SpanDown, out m_SpanText, out m_SpanUp, small, theme);
            m_SpanDown.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetComputedSpanRpc(l.Id, l.SmoothSpan - 0.05f)));
            m_SpanUp.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetComputedSpanRpc(l.Id, l.SmoothSpan + 0.05f)));
            Stepper(cp, "Line", k_Half + 10f, RowY(5), out m_LineDown, out m_LineText, out m_LineUp, small, theme);
            m_LineDown.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetLayerLineWidthRpc(l.Id, l.LineWidth / 1.5f)));
            m_LineUp.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetLayerLineWidthRpc(l.Id, l.LineWidth * 1.5f)));
            Stepper(cp, "Value", 0f, RowY(6), out m_ValueDown, out m_ValueText, out m_ValueUp, small, theme);
            m_ValueDown.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetComputedValueRpc(l.Id, l.RefValue - ReferenceStep(l))));
            m_ValueUp.onClick.AddListener(() => RequestToggle((m, l) => m.RequestSetComputedValueRpc(l.Id, l.RefValue + ReferenceStep(l))));
        }

        static void Stepper(Transform parent, string name, float x, float y, out Button down, out TMP_Text text, out Button up,
                            float fontSize, MenuTheme theme)
        {
            down = PanelKit.Button(parent, name + "Down", "−", null, x, y, 56f, k_RowH, PanelKit.Style.Secondary);
            text = PanelKit.Label(parent, name + "Text", "", x + 60f, y, k_Half - 120f, k_RowH, fontSize, theme.Text, TextAlignmentOptions.Center);
            up = PanelKit.Button(parent, name + "Up", "+", null, x + k_Half - 56f, y, 56f, k_RowH, PanelKit.Style.Secondary);
        }

        void BuildPicker(RectTransform panel, MenuTheme theme)
        {
            float small = theme.BodyFontSize * 0.85f;
            m_PickerRoot = PanelKit.Rect(panel, "Picker", k_Right, -k_Top, k_RightWidth, PanelHeight - k_Top);
            m_PickerTitle = PanelKit.Label(m_PickerRoot, "Title", "", 0f, 0f, k_RightWidth, 44f, theme.BodyFontSize, theme.Text);
            for (int i = 0; i < m_PickerItems.Length; i++)
            {
                int slot = i;
                m_PickerItems[i] = PanelKit.Button(m_PickerRoot, "Item" + i, "", null, (i % 2) * (k_Half + 10f), -54f - (i / 2) * k_Pitch,
                    k_Half, k_RowH, PanelKit.Style.Secondary, small);
                // PanelKit adds no label to a button created without text; add one for the item text.
                PanelKit.Label(m_PickerItems[i].transform, "Label", "", 12f, 0f, k_Half - 24f, k_RowH, small, theme.Accent, TextAlignmentOptions.Center);
                m_PickerItems[i].onClick.AddListener(() => OnPickerItem(slot));
            }
            float navY = -54f - 6 * k_Pitch;
            m_PickerPrev = PanelKit.Button(m_PickerRoot, "Prev", "◀", null, 0f, navY, 70f, k_RowH, PanelKit.Style.Secondary);
            m_PickerPage = PanelKit.Label(m_PickerRoot, "Page", "", 76f, navY, 200f, k_RowH, small, theme.TextMuted, TextAlignmentOptions.Center);
            m_PickerNext = PanelKit.Button(m_PickerRoot, "Next", "▶", null, 282f, navY, 70f, k_RowH, PanelKit.Style.Secondary);
            m_PickerCancel = PanelKit.Button(m_PickerRoot, "Cancel", "Cancel", null, k_RightWidth - 150f, navY, 150f, k_RowH, PanelKit.Style.Secondary);
            m_PickerPrev.onClick.AddListener(() => { m_PickerPageIndex--; ApplyPicker(); });
            m_PickerNext.onClick.AddListener(() => { m_PickerPageIndex++; ApplyPicker(); });
            m_PickerCancel.onClick.AddListener(ClosePicker);
            m_PickerRoot.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance != null
                    ? MultiplayerScatterplotManager.Instance
                    : FindAnyObjectByType<MultiplayerScatterplotManager>();
            if (m_Visualizer == null)
                m_Visualizer = FindAnyObjectByType<ScatterplotVisualizer>();
            if (m_Manager != null)
            {
                m_Manager.OnLayersChanged += MarkDirty;
                m_Manager.OnPlotMessage += OnPlotMessage;
            }
            m_Dirty = true;
        }

        void OnDisable()
        {
            if (m_Manager != null)
            {
                m_Manager.OnLayersChanged -= MarkDirty;
                m_Manager.OnPlotMessage -= OnPlotMessage;
            }
            if (m_Keyboard != null)
                m_Keyboard.CloseSilently();
            ClosePicker();
        }

        void MarkDirty() => m_Dirty = true;

        void OnPlotMessage(string text, PlotMessageKind kind)
        {
            if (m_Message == null) return;
            m_Message.text = text;
            m_Message.color = kind == PlotMessageKind.Info ? PanelKit.Theme.TextMuted : PanelKit.Warning;
            m_MessageUntil = Time.unscaledTime + k_MessageSeconds;
        }

        void LateUpdate()
        {
            if (m_MessageUntil > 0f && Time.unscaledTime > m_MessageUntil)
            {
                m_MessageUntil = 0f;
                m_Message.text = "";
            }
            if (m_Manager == null)
                return;

            if (m_Lines == null && m_Visualizer != null)
                m_Lines = m_Visualizer.GetComponent<LayerLineRenderer>();

            int signature = LayersPanelModel.Signature(m_Manager.Stack, WarningOf);
            if (!m_Dirty && signature == m_Signature)
                return;
            m_Dirty = false;
            m_Signature = signature;
            Refresh();
        }

        string WarningOf(PlotLayer layer)
        {
            if (layer == null) return null;
            if (layer.Kind == LayerKind.Computed)
                return m_Lines != null ? m_Lines.GetStatus(layer.Id) : null;
            LayerFrame frame = m_Visualizer != null ? m_Visualizer.LastFrame?.Find(layer.Id) : null;
            return frame?.Info.Warning;
        }

        float ReferenceStep(PlotLayer layer)
        {
            if (m_Manager == null || layer == null) return 1f;
            LayerStack stack = m_Manager.Stack;
            PlotLayer source = stack.Find(layer.SourceLayerId) ?? layer;
            int axis = layer.DriverAxis >= 0 ? layer.DriverAxis : LayerRules.DefaultDriverAxis(source);
            AxisFrame frame = LayerBounds.FrameInStack(layer, stack, m_Manager.SharedBounds);
            return LayersPanelModel.ReferenceStep(frame.Get(Mathf.Clamp(axis, 0, 2)));
        }

        // ------------------------------------------------------------------ refresh

        void Refresh()
        {
            LayerStack stack = m_Manager.Stack;

            // Follow the active layer to its page when it changes (select, add, duplicate, remove).
            if (stack.ActiveLayerId != m_LastActiveId)
            {
                m_LastActiveId = stack.ActiveLayerId;
                if (stack.ActiveIndex >= 0)
                    m_Page = LayersPanelModel.PageOf(stack.ActiveIndex);
            }

            m_View = LayersPanelModel.Build(stack, WarningOf, m_Manager.IsLayerShuffleUnlocked, m_Page, ReferenceStep(stack.Active));
            m_Page = m_View.Page;
            LayersPanelModel.View v = m_View;

            m_EmptyText.text = v.EmptyText;
            for (int i = 0; i < m_Rows.Count; i++)
            {
                bool shown = i < v.Rows.Count;
                if (m_Rows[i].gameObject.activeSelf != shown)
                    m_Rows[i].gameObject.SetActive(shown);
                if (shown)
                    m_Rows[i].Apply(v.Rows[i]);
            }
            m_PageText.text = v.PageText;
            m_PrevPage.interactable = v.Page > 0;
            m_NextPage.interactable = v.Page < v.PageCount - 1;
            m_PrevPage.transform.parent.gameObject.SetActive(v.PageCount > 1);

            m_AddSubset.interactable = v.CanAddSubset;
            m_AddComputed.interactable = v.CanAddComputed;
            m_AddHint.text = v.AddHint;

            // A picker for a layer that is gone (or no longer active) closes.
            if (m_Picker != Picker.None && m_Picker != Picker.Dataset && m_PickerLayerId != stack.ActiveLayerId)
                ClosePicker();

            ApplyProps(v.Props);
        }

        void ApplyProps(LayersPanelModel.Props p)
        {
            m_Props.gameObject.SetActive(m_Picker == Picker.None);
            m_Title.text = p.HasLayer ? p.Title : "No active layer";
            m_Rename.gameObject.SetActive(p.HasLayer);
            m_PointOnly.gameObject.SetActive(p.HasLayer && p.IsPoint);
            m_ComputedOnly.gameObject.SetActive(p.HasLayer && p.IsComputed);
            m_Warning.text = p.Warning;

            bool any = p.HasLayer;
            m_OpacityLabel.gameObject.SetActive(any);
            m_OpacityLabel.text = p.OpacityText;
            for (int i = 0; i < m_Opacity.Length; i++)
            {
                m_Opacity[i].gameObject.SetActive(any);
                PanelKit.SetHighlighted(m_Opacity[i], i == p.OpacityStep);
            }
            for (int i = 0; i < m_Colours.Length; i++)
            {
                if (m_Colours[i] == null) continue;
                m_Colours[i].gameObject.SetActive(any);
                SetSelectedRing(m_Colours[i], i == p.ColourIndex);
            }
            m_ByColumn.gameObject.SetActive(any && p.IsPoint);
            SetText(m_ByColumn, p.ColourColumnText);
            PanelKit.SetHighlighted(m_ByColumn, p.ColourByColumn);

            if (p.IsPoint)
            {
                SetToggle(m_Normalise, p.NormaliseText, p.NormaliseOn);
                m_HideInSource.gameObject.SetActive(p.ShowHideInSource);
                SetToggle(m_HideInSource, p.HideInSourceText, p.HideInSourceOn);
                SetText(m_Glyph, p.GlyphText);
                SetToggle(m_Shuffle, p.ShuffleText, p.ShuffleOn);
                m_Shuffle.interactable = p.ShuffleEnabled;
                for (int i = 0; i < m_TimeModes.Length; i++)
                    PanelKit.SetHighlighted(m_TimeModes[i], i == p.TimeMode);
                SetText(m_EndColumn, p.EndColumnText);
                m_EndColumn.interactable = p.EndColumnEnabled;
                SetText(m_LabelColumn, p.LabelColumnText);
                m_TimeHint.text = p.TimeHint;
            }
            else if (p.IsComputed)
            {
                m_AxisLabel.gameObject.SetActive(p.ShowDriverAxis);
                for (int i = 0; i < m_Axes.Length; i++)
                {
                    m_Axes[i].gameObject.SetActive(p.ShowDriverAxis);
                    PanelKit.SetHighlighted(m_Axes[i], i == p.DriverAxisChoice);
                }
                m_Flag.gameObject.SetActive(p.ShowFlag);
                SetToggle(m_Flag, p.FlagText, p.FlagOn);
                m_OrderColumn.gameObject.SetActive(p.ShowOrderColumn);
                SetText(m_OrderColumn, p.OrderColumnText);
                SetStepper(m_SpanDown, m_SpanText, m_SpanUp, p.ShowSpan, p.SpanText);
                SetStepper(m_LineDown, m_LineText, m_LineUp, true, p.LineWidthText);
                SetStepper(m_ValueDown, m_ValueText, m_ValueUp, p.ShowValue, p.ValueText);
            }
        }

        static void SetStepper(Button down, TMP_Text text, Button up, bool shown, string value)
        {
            down.gameObject.SetActive(shown);
            up.gameObject.SetActive(shown);
            text.gameObject.SetActive(shown);
            text.text = value;
        }

        static void SetToggle(Button button, string text, bool on)
        {
            SetText(button, text);
            PanelKit.SetHighlighted(button, on);
        }

        static void SetText(Button button, string text)
        {
            TMP_Text label = PanelKit.LabelOf(button);
            if (label != null && label.text != text)
                label.text = text;
        }

        // A colour swatch's button turns accent-filled when it is the layer's colour.
        static void SetSelectedRing(Button button, bool selected) => PanelKit.SetHighlighted(button, selected);

        // ------------------------------------------------------------------ actions

        /// <summary>Runs a request on the active layer (by id, so it lands on that layer even if another becomes active meanwhile).</summary>
        void Request(Action<MultiplayerScatterplotManager, string> request)
        {
            if (m_Manager == null || m_Manager.ActiveLayer == null) return;
            request(m_Manager, m_Manager.ActiveLayerId);
        }

        void RequestToggle(Action<MultiplayerScatterplotManager, PlotLayer> request)
        {
            if (m_Manager == null || m_Manager.ActiveLayer == null) return;
            request(m_Manager, m_Manager.ActiveLayer);
        }

        void OnVisibilityToggled(string id)
        {
            PlotLayer layer = m_Manager?.Stack.Find(id);
            if (layer != null)
                m_Manager.RequestSetLayerVisibleRpc(id, !layer.Visible);
        }

        void Move(string id, int delta)
        {
            if (m_Manager == null) return;
            int index = m_Manager.Stack.IndexOf(id);
            if (index < 0) return;
            m_Manager.RequestReorderLayerRpc(id, index + delta);
            m_Page = LayersPanelModel.PageOf(index + delta); // keep the moved row in view
            m_Dirty = true;
        }

        void OpenMapTab()
        {
            if (m_Bar == null) return;
            foreach (MenuTab tab in m_Bar.m_Tabs)
                if (tab != null && tab.m_Id == "Map")
                {
                    m_Bar.Open(tab);
                    return;
                }
        }

        void OpenRename()
        {
            PlotLayer layer = m_Manager?.ActiveLayer;
            if (layer == null || m_Keyboard == null) return;
            string id = layer.Id;
            ClosePicker();
            m_KeyboardBackdrop.SetActive(true);
            m_KeyboardBackdrop.transform.SetAsLastSibling();
            m_Keyboard.Open("Rename layer", layer.Name, VRKeyboard.Layout.Text, 40, "Rename",
                name => m_Manager?.RequestRenameLayerRpc(id, name));
        }

        // ------------------------------------------------------------------ picker

        void OpenPicker(Picker picker, string layerId)
        {
            if (m_Manager == null) return;
            LayerStack stack = m_Manager.Stack;
            PlotLayer layer = stack.Find(layerId);
            string title;
            List<Option> options;
            switch (picker)
            {
                case Picker.Dataset:
                    title = "Add a dataset layer";
                    options = LayersPanelModel.DatasetOptions(DatasetStore.Files());
                    break;
                case Picker.SubsetColumn:
                    if (layer == null || !layer.IsPointLayer) return;
                    title = "Subset of " + layer.Name + ": filter on";
                    options = LayersPanelModel.SubsetColumnOptions(layer);
                    break;
                case Picker.SubsetValue:
                    if (layer == null) return;
                    title = "Rows where " + LayersPanelModel.ColumnName(layer.Dataset, m_SubsetColumn) + " is";
                    options = LayersPanelModel.FilterValueOptions(layer.Dataset, m_SubsetColumn);
                    break;
                case Picker.Computed:
                    if (layer == null || !layer.IsPointLayer) return;
                    title = "Computed layer on " + layer.Name;
                    options = LayersPanelModel.ComputedKindOptions();
                    break;
                case Picker.EndColumn:
                    if (layer == null) return;
                    title = "End time column of " + layer.Name;
                    options = LayersPanelModel.ColumnOptions(layer.Dataset, "None");
                    break;
                case Picker.LabelColumn:
                    if (layer == null) return;
                    title = "Label column of " + layer.Name;
                    options = LayersPanelModel.ColumnOptions(layer.Dataset, "None");
                    break;
                case Picker.OrderColumn:
                    if (layer == null) return;
                    title = "Order " + layer.Name + " by";
                    options = LayersPanelModel.ColumnOptions(LayersPanelModel.RowDataset(stack, layer), "Time column (default)");
                    break;
                default:
                    return;
            }

            m_Picker = picker;
            m_PickerLayerId = layerId ?? "";
            m_PickerOptions = options;
            m_PickerPageIndex = 0;
            m_PickerTitle.text = options.Count == 0 ? title + " (nothing to pick)" : title;
            m_PickerRoot.gameObject.SetActive(true);
            m_Props.gameObject.SetActive(false);
            ApplyPicker();
        }

        void ApplyPicker()
        {
            int perPage = m_PickerItems.Length;
            int pages = Mathf.Max(1, (m_PickerOptions.Count + perPage - 1) / perPage);
            m_PickerPageIndex = Mathf.Clamp(m_PickerPageIndex, 0, pages - 1);
            for (int i = 0; i < perPage; i++)
            {
                int index = m_PickerPageIndex * perPage + i;
                bool shown = index < m_PickerOptions.Count;
                m_PickerItems[i].gameObject.SetActive(shown);
                if (shown)
                    SetText(m_PickerItems[i], m_PickerOptions[index].Label);
            }
            m_PickerPage.text = pages > 1 ? $"{m_PickerPageIndex + 1} of {pages}" : "";
            m_PickerPrev.gameObject.SetActive(pages > 1);
            m_PickerNext.gameObject.SetActive(pages > 1);
            m_PickerPrev.interactable = m_PickerPageIndex > 0;
            m_PickerNext.interactable = m_PickerPageIndex < pages - 1;
        }

        void ClosePicker()
        {
            m_Picker = Picker.None;
            m_PickerLayerId = "";
            if (m_PickerRoot != null) m_PickerRoot.gameObject.SetActive(false);
            if (m_Props != null) m_Props.gameObject.SetActive(true);
            m_Dirty = true;
        }

        void OnPickerItem(int slot)
        {
            int index = m_PickerPageIndex * m_PickerItems.Length + slot;
            if (m_Manager == null || index < 0 || index >= m_PickerOptions.Count) return;
            Option option = m_PickerOptions[index];
            string id = m_PickerLayerId;
            Picker picker = m_Picker;
            ClosePicker();

            switch (picker)
            {
                case Picker.Dataset:
                    m_Manager.RequestAddDatasetLayerRpc(option.Text);
                    break;
                case Picker.SubsetColumn:
                    if (option.Value < 0)
                    {
                        m_Manager.RequestAddSubsetLayerRpc(id, -1, "");
                    }
                    else
                    {
                        m_SubsetColumn = option.Value;
                        OpenPicker(Picker.SubsetValue, id);
                    }
                    break;
                case Picker.SubsetValue:
                    m_Manager.RequestAddSubsetLayerRpc(id, m_SubsetColumn, option.Text);
                    break;
                case Picker.Computed:
                    m_Manager.RequestAddComputedLayerRpc(id, (ComputedLayerKind)option.Value);
                    break;
                case Picker.EndColumn:
                    m_Manager.RequestSetLayerEndTimeColumnRpc(id, option.Value);
                    break;
                case Picker.LabelColumn:
                    m_Manager.RequestSetLayerLabelColumnRpc(id, option.Value);
                    break;
                case Picker.OrderColumn:
                    m_Manager.RequestSetComputedColumnRpc(id, option.Value);
                    break;
            }
        }
    }
}
