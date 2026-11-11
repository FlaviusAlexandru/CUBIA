using System;
using System.Collections.Generic;
using System.Globalization;
using DataViz.Networking;
using UnityEngine;
using UnityEngine.UIElements;

namespace DataViz.Study
{
    /// <summary>
    /// The dashboard's Layers section (LFR-26): the same layer controls as the VR Layers tab, built
    /// in code (like DashboardRangeEditor) so FacilitatorDashboard.uxml stays unchanged.
    ///
    /// Top: one row per layer (select, show/hide, up, down, duplicate, remove) with its warning line
    /// (point budget, skipped rows, computed status, "not on this device", "source missing").
    /// Then the add controls (dataset file, subset of the active layer, computed kind), then the
    /// active layer's own settings. Every change goes through the <see cref="Change"/> delegate,
    /// which DashboardController routes into Facilitate, so it gets an undo step and a log row with
    /// the right layer name. Old/new values use the same text as PlotStateDiff.DescribeStack, so
    /// facilitator and participant rows read the same.
    ///
    /// Refresh is cheap when nothing changed (signature check), so the controller may call it on
    /// every OnPlotSettingsChanged; UpdateWarnings runs from OnFrameBuilt and only touches labels.
    /// </summary>
    public sealed class DashboardLayersSection
    {
        /// <summary>action, layer name for the Layer column (null = the active layer after apply), old, new, apply, push an undo step.</summary>
        public delegate void ChangeHandler(string action, string layer, string oldValue, string newValue, Action apply, bool pushUndo);

        /// <summary>Not in StudyActions (the unlock is local and not part of the snapshot). See the WP6 report.</summary>
        public const string SetLayerShuffleAction = StudyActions.SetLayerShuffleUnlocked;

        const string k_None = "None";
        const string k_Auto = "Auto";
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;
        static readonly float[] k_Opacities = { 1f, 0.75f, 0.5f, 0.25f };
        static readonly ComputedLayerKind[] k_ComputedKinds =
        {
            ComputedLayerKind.Trendline, ComputedLayerKind.Path, ComputedLayerKind.Mean,
            ComputedLayerKind.Reference, ComputedLayerKind.SmoothedPath,
        };

        readonly MultiplayerScatterplotManager m_Manager;
        readonly ChangeHandler m_Change;

        readonly VisualElement m_List;
        readonly DropdownField m_AddFile, m_AddComputedKind;
        readonly Button m_AddDataset, m_AddSubset, m_AddComputed;
        readonly VisualElement m_Props, m_Computed, m_ColourRow;
        readonly Label m_PropsTitle, m_ColourNote, m_Empty;
        readonly TextField m_Name;
        readonly Toggle m_Normalised, m_HideInSource, m_Shuffle, m_Smoothed;
        readonly DropdownField m_Opacity, m_Glyph, m_Label, m_TimeMode, m_EndTime, m_Axis, m_Order;
        readonly FloatField m_Value, m_Span, m_LineWidth;
        readonly List<Button> m_Swatches = new();
        readonly Dictionary<string, Label> m_WarningLabels = new();

        List<string> m_Files = new();
        int m_Signature;
        bool m_Updating;
        PlotFrame m_Frame;
        LayerLineRenderer m_Lines;

        public DashboardLayersSection(VisualElement host, MultiplayerScatterplotManager manager, ChangeHandler change)
        {
            m_Manager = manager;
            m_Change = change;
            host.Clear();

            var header = new Label("Layers");
            header.AddToClassList("dash-section__header");
            host.Add(header);

            m_Empty = Note("The plot is empty: add a dataset layer or load a dataset.");
            host.Add(m_Empty);
            m_List = new VisualElement();
            host.Add(m_List);

            // ---- add
            VisualElement addRow = Row();
            m_AddFile = new DropdownField("Add dataset");
            m_AddFile.AddToClassList("dash-grow");
            addRow.Add(m_AddFile);
            m_AddDataset = ButtonOf("Add", AddDataset, "Adds the file as a new layer on top (Load above replaces the whole plot)");
            addRow.Add(m_AddDataset);
            host.Add(addRow);

            VisualElement addRow2 = Row();
            m_AddComputedKind = new DropdownField("Add computed");
            m_AddComputedKind.AddToClassList("dash-grow");
            var kinds = new List<string>();
            foreach (ComputedLayerKind kind in k_ComputedKinds) kinds.Add(LayerRules.ComputedKindName(kind));
            m_AddComputedKind.choices = kinds;
            m_AddComputedKind.SetValueWithoutNotify(kinds[0]);
            addRow2.Add(m_AddComputedKind);
            m_AddComputed = ButtonOf("Add", AddComputed, "Adds a computed layer of the active layer");
            addRow2.Add(m_AddComputed);
            host.Add(addRow2);

            VisualElement addRow3 = Row();
            addRow3.AddToClassList("dash-row--end");
            m_AddSubset = ButtonOf("Add subset of active layer", AddSubset,
                "A copy of the active layer that draws only the rows passing its current filter");
            addRow3.Add(m_AddSubset);
            host.Add(addRow3);

            // ---- active layer
            m_Props = new VisualElement();
            host.Add(m_Props);
            m_PropsTitle = new Label();
            m_PropsTitle.AddToClassList("dash-section__header");
            m_Props.Add(m_PropsTitle);

            m_Name = new TextField("Name") { isDelayed = true, maxLength = 40 };
            m_Name.RegisterValueChangedCallback(evt => { if (!m_Updating) Rename(evt.newValue); });
            m_Props.Add(m_Name);

            m_Normalised = ToggleOf("Normalise (own scale)", v => Set(StudyActions.SetLayerNormalised, l => l.Normalised, v,
                (id, b) => m_Manager.RequestSetLayerNormalisedRpc(id, b)));
            m_Props.Add(m_Normalised);

            m_Opacity = DropdownOf("Opacity", OnOpacity);
            var opacityChoices = new List<string>();
            foreach (float o in k_Opacities) opacityChoices.Add(Percent(o));
            m_Opacity.choices = opacityChoices;
            m_Props.Add(m_Opacity);

            m_ColourRow = Row();
            var colourLabel = new Label("Colour");
            colourLabel.AddToClassList("dash-grow");
            m_ColourRow.Add(colourLabel);
            foreach (Color c in PlayerProfile.Palette)
            {
                Color32 colour = c;
                var swatch = new Button(() => OnColour(colour)) { text = "", tooltip = PlotStateDiff.ColourText(LayerSnapshot.PackColour(colour)) };
                swatch.AddToClassList("dash-button");
                swatch.style.backgroundColor = c;
                swatch.style.width = 22;
                swatch.style.height = 22;
                m_Swatches.Add(swatch);
                m_ColourRow.Add(swatch);
            }
            m_Props.Add(m_ColourRow);
            m_ColourNote = Note("Coloured by a column (Mappings): picking a colour replaces it.");
            m_Props.Add(m_ColourNote);

            m_Glyph = DropdownOf("Shape", OnGlyph);
            m_Glyph.choices = new List<string>(ScatterplotUI.k_GlyphShapeNames);
            m_Props.Add(m_Glyph);
            m_Label = DropdownOf("Labels", OnLabel);
            m_Props.Add(m_Label);
            m_TimeMode = DropdownOf("Time mode", OnTimeMode);
            m_TimeMode.choices = new List<string> { "Follow", "Always", "Span" };
            m_TimeMode.tooltip = "Follow: fades with the playback window. Always: always shown. Span: shown from its time column to its end column.";
            m_Props.Add(m_TimeMode);
            m_EndTime = DropdownOf("End column", OnEndTime);
            m_Props.Add(m_EndTime);
            m_HideInSource = ToggleOf("Hide these rows in the source layer", v => Set(StudyActions.SetSubsetHiddenInSource,
                l => l.HiddenInSource, v, (id, b) => m_Manager.RequestSetSubsetHiddenInSourceRpc(id, b)));
            m_Props.Add(m_HideInSource);
            m_Shuffle = ToggleOf("Shuffle with the first layer", OnShuffle);
            m_Props.Add(m_Shuffle);

            m_Computed = new VisualElement();
            m_Axis = DropdownOf("Driver axis", OnAxis);
            m_Axis.choices = new List<string> { k_Auto, "X", "Y", "Z" };
            m_Computed.Add(m_Axis);
            m_Order = DropdownOf("Order column", OnOrder);
            m_Computed.Add(m_Order);
            m_Smoothed = ToggleOf("Curve", OnSmoothed);
            m_Computed.Add(m_Smoothed);
            m_Value = FloatOf("Value", v => SetComputedFloat("Value", l => l.RefValue, v, (id, f) => m_Manager.RequestSetComputedValueRpc(id, f)));
            m_Computed.Add(m_Value);
            m_Span = FloatOf("Smoothing span", v => SetComputedFloat("Span", l => l.SmoothSpan, v, (id, f) => m_Manager.RequestSetComputedSpanRpc(id, f)));
            m_Computed.Add(m_Span);
            m_LineWidth = FloatOf("Line width", OnLineWidth);
            m_Computed.Add(m_LineWidth);
            m_Props.Add(m_Computed);

            Refresh(true);
        }

        // ================================================================== inputs from the controller

        /// <summary>The dataset files the Add dataset list offers (the controller's Rescan list).</summary>
        public void SetDatasets(List<string> files)
        {
            m_Files = files != null ? new List<string>(files) : new List<string>();
            string selected = m_AddFile.value;
            m_AddFile.choices = new List<string>(m_Files);
            m_AddFile.SetValueWithoutNotify(m_Files.Contains(selected) ? selected : m_Files.Count > 0 ? m_Files[0] : "");
            m_AddDataset.SetEnabled(m_Files.Count > 0);
        }

        /// <summary>The visualizer's latest frame, for the warning lines.</summary>
        public void UpdateWarnings(PlotFrame frame, LayerLineRenderer lines)
        {
            m_Frame = frame;
            m_Lines = lines;
            foreach (PlotLayer layer in m_Manager.Layers)
            {
                if (!m_WarningLabels.TryGetValue(layer.Id, out Label label)) continue;
                string text = WarningFor(layer, m_Manager.Stack, frame, lines);
                if (label.text != text) label.text = text;
                Show(label, text.Length > 0);
            }
        }

        /// <summary>
        /// Everything to warn about for one layer: not on this device, source missing, the
        /// computed layer's status, and the renderer's LayerFrame.Info.Warning (several lines).
        /// "" when fine.
        /// </summary>
        public static string WarningFor(PlotLayer layer, LayerStack stack, PlotFrame frame, LayerLineRenderer lines)
        {
            if (layer == null) return "";
            var parts = new List<string>();
            if (layer.LocalUnavailable) parts.Add("Not on this device: " + (layer.DatasetName ?? ""));
            if (layer.Kind == LayerKind.Computed)
            {
                if (ComputedLayerBuilder.IsSourceMissing(layer, stack)) parts.Add("Source missing (kept with its last data)");
                string status = lines != null ? lines.GetStatus(layer.Id) : "";
                if (!string.IsNullOrEmpty(status)) parts.Add(status);
            }
            else if (layer.Dataset == null && !layer.LocalUnavailable && !string.IsNullOrEmpty(layer.DatasetName))
            {
                parts.Add("Dataset not loaded: " + layer.DatasetName);
            }
            string warning = frame?.Find(layer.Id)?.Info.Warning;
            if (!string.IsNullOrEmpty(warning)) parts.Add(warning);
            return string.Join("\n", parts);
        }

        /// <summary>Rebuilds the list and the properties when the stack or the active layer changed (or always with force).</summary>
        public void Refresh(bool force)
        {
            int signature = Signature();
            if (!force && signature == m_Signature) return;
            m_Signature = signature;

            m_Updating = true;
            try
            {
                RebuildList();
                RefreshProperties();
            }
            finally
            {
                m_Updating = false;
            }
            UpdateWarnings(m_Frame, m_Lines);
        }

        int Signature()
        {
            var h = new HashCode();
            h.Add(m_Manager.Stack.Version);
            h.Add(m_Manager.ActiveLayerId);
            foreach (PlotLayer l in m_Manager.Layers)
            {
                h.Add(l.Id);
                h.Add(l.ColorColumnIndex); // legacy colour requests do not raise OnLayersChanged
                h.Add(l.Dataset != null ? l.Dataset.GetHashCode() : 0);
                h.Add(l.LocalUnavailable);
                h.Add(m_Manager.IsLayerShuffleUnlocked(l.Id));
            }
            return h.ToHashCode();
        }

        // ================================================================== list

        void RebuildList()
        {
            m_List.Clear();
            m_WarningLabels.Clear();
            IReadOnlyList<PlotLayer> layers = m_Manager.Layers;
            Show(m_Empty, layers.Count == 0);
            for (int i = 0; i < layers.Count; i++)
            {
                PlotLayer layer = layers[i];
                string id = layer.Id;
                bool active = id == m_Manager.ActiveLayerId;

                VisualElement row = Row();
                var select = ButtonOf(RowText(layer), () => Select(id), active ? "Active layer: the menus edit it" : "Make this the active layer");
                select.AddToClassList("dash-grow");
                select.style.unityTextAlign = TextAnchor.MiddleLeft;
                if (active) select.AddToClassList("dash-button--primary");
                if (layer.LocalUnavailable || !layer.Visible) select.style.opacity = 0.55f; // greyed in lists
                row.Add(select);
                row.Add(ButtonOf(layer.Visible ? "Hide" : "Show", () => SetVisible(id, !layer.Visible), "Show or hide this layer"));
                Button up = ButtonOf("↑", () => Move(id, -1), "Move down the stack (drawn earlier)");
                up.SetEnabled(i > 0);
                row.Add(up);
                Button down = ButtonOf("↓", () => Move(id, +1), "Move up the stack (drawn later)");
                down.SetEnabled(i < layers.Count - 1);
                row.Add(down);
                row.Add(ButtonOf("Copy", () => Duplicate(id), "Duplicate this layer"));
                row.Add(ButtonOf("×", () => Remove(id), "Remove this layer"));
                m_List.Add(row);

                Label warning = Note("");
                warning.style.color = new Color(0.95f, 0.7f, 0.3f);
                Show(warning, false);
                m_List.Add(warning);
                m_WarningLabels[id] = warning;
            }

            PlotLayer activeLayer = m_Manager.ActiveLayer;
            m_AddSubset.SetEnabled(activeLayer != null && activeLayer.IsPointLayer);
            m_AddComputed.SetEnabled(activeLayer != null && activeLayer.IsPointLayer);
            m_AddDataset.SetEnabled(m_Files.Count > 0);
        }

        string RowText(PlotLayer layer)
        {
            string kind = layer.Kind == LayerKind.Computed ? LayerRules.ComputedKindName(layer.ComputedKind) : layer.Kind.ToString();
            string text = $"{layer.Name}  [{kind}]";
            if (layer.Normalised) text += " · Normalised";
            if (layer.LocalUnavailable) text += " · not on this device";
            if (layer.Kind == LayerKind.Computed && ComputedLayerBuilder.IsSourceMissing(layer, m_Manager.Stack)) text += " · source missing";
            return text;
        }

        // ================================================================== properties

        void RefreshProperties()
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            Show(m_Props, layer != null);
            if (layer == null) return;

            bool computed = layer.Kind == LayerKind.Computed;
            m_PropsTitle.text = "Active layer: " + layer.Name;
            m_Name.SetValueWithoutNotify(layer.Name);
            m_Normalised.SetValueWithoutNotify(layer.Normalised);
            m_Normalised.SetEnabled(!computed);
            m_Opacity.SetValueWithoutNotify(Percent(Nearest(k_Opacities, layer.Opacity)));

            uint packed = LayerSnapshot.PackColour(layer.FixedColour);
            for (int i = 0; i < m_Swatches.Count; i++)
            {
                bool chosen = layer.ColorColumnIndex < 0 && LayerSnapshot.PackColour(PlayerProfile.Palette[i]) == packed;
                float width = chosen ? 3f : 0f;
                m_Swatches[i].style.borderTopWidth = width;
                m_Swatches[i].style.borderBottomWidth = width;
                m_Swatches[i].style.borderLeftWidth = width;
                m_Swatches[i].style.borderRightWidth = width;
                m_Swatches[i].style.borderTopColor = Color.white;
                m_Swatches[i].style.borderBottomColor = Color.white;
                m_Swatches[i].style.borderLeftColor = Color.white;
                m_Swatches[i].style.borderRightColor = Color.white;
            }
            Show(m_ColourNote, !computed && layer.ColorColumnIndex >= 0);

            Show(m_Glyph, !computed);
            m_Glyph.SetValueWithoutNotify(ScatterplotUI.k_GlyphShapeNames[Mathf.Clamp(layer.DefaultGlyph, 0, ScatterplotUI.k_GlyphShapeNames.Length - 1)]);

            List<string> columns = ColumnsWithNone(layer.Dataset);
            Show(m_Label, !computed);
            m_Label.choices = columns;
            m_Label.SetValueWithoutNotify(Choice(columns, layer.LabelColumnIndex + 1));
            Show(m_TimeMode, !computed);
            m_TimeMode.SetValueWithoutNotify(layer.TimeBehaviour.ToString());
            Show(m_EndTime, !computed);
            m_EndTime.choices = columns;
            m_EndTime.SetValueWithoutNotify(Choice(columns, layer.EndTimeColumnIndex + 1));
            m_EndTime.SetEnabled(layer.TimeBehaviour == LayerTimeBehaviour.Span);

            Show(m_HideInSource, layer.Kind == LayerKind.Subset);
            m_HideInSource.SetValueWithoutNotify(layer.HiddenInSource);

            Show(m_Shuffle, !computed);
            bool first = m_Manager.Stack.IndexOf(layer.Id) == 0;
            m_Shuffle.SetValueWithoutNotify(first || m_Manager.IsLayerShuffleUnlocked(layer.Id));
            m_Shuffle.SetEnabled(!first); // the first layer always shuffles (LFR-32)

            Show(m_Computed, computed);
            if (computed)
            {
                PlotLayer source = m_Manager.Stack.Find(layer.SourceLayerId) ?? layer;
                m_Axis.SetValueWithoutNotify(m_Axis.choices[Mathf.Clamp(layer.DriverAxis + 1, 0, 3)]);
                var order = new List<string> { k_Auto };
                order.AddRange(ColumnNames(source.Dataset));
                m_Order.choices = order;
                m_Order.SetValueWithoutNotify(Choice(order, layer.OrderColumnIndex + 1));
                m_Smoothed.label = layer.ComputedKind == ComputedLayerKind.Reference ? "Plane" : "Curve";
                m_Smoothed.SetValueWithoutNotify(layer.Smoothed);
                Show(m_Value, layer.ComputedKind == ComputedLayerKind.Reference);
                m_Value.SetValueWithoutNotify(layer.RefValue);
                Show(m_Span, layer.ComputedKind == ComputedLayerKind.SmoothedPath || layer.ComputedKind == ComputedLayerKind.Trendline);
                m_Span.SetValueWithoutNotify(layer.SmoothSpan);
                m_LineWidth.SetValueWithoutNotify(layer.LineWidth);
            }
        }

        // ================================================================== changes

        void AddDataset()
        {
            string file = m_AddFile.value;
            if (string.IsNullOrEmpty(file)) return;
            m_Change(StudyActions.AddLayer, null, "", "Dataset: " + file, () => m_Manager.RequestAddDatasetLayerRpc(file), true);
        }

        void AddSubset()
        {
            PlotLayer source = m_Manager.ActiveLayer;
            if (source == null || !source.IsPointLayer) return;
            string id = source.Id;
            m_Change(StudyActions.AddLayer, null, "", "Subset: " + (source.DatasetName ?? ""),
                () => m_Manager.RequestAddSubsetLayerRpc(id), true);
        }

        void AddComputed()
        {
            PlotLayer source = m_Manager.ActiveLayer;
            int index = m_AddComputedKind.index;
            if (source == null || !source.IsPointLayer || index < 0 || index >= k_ComputedKinds.Length) return;
            ComputedLayerKind kind = k_ComputedKinds[index];
            string id = source.Id;
            m_Change(StudyActions.AddLayer, null, "", $"Computed: {LayerRules.ComputedKindName(kind)} of {source.Name}",
                () => m_Manager.RequestAddComputedLayerRpc(id, kind), true);
        }

        void Select(string id)
        {
            PlotLayer layer = m_Manager.Stack.Find(id);
            PlotLayer active = m_Manager.ActiveLayer;
            if (layer == null || layer == active) return;
            m_Change(StudyActions.SelectLayer, layer.Name, active?.Name ?? "", layer.Name,
                () => m_Manager.RequestSetActiveLayerRpc(id), true);
        }

        void SetVisible(string id, bool visible)
        {
            PlotLayer layer = m_Manager.Stack.Find(id);
            if (layer == null) return;
            m_Change(StudyActions.SetLayerVisible, layer.Name, Bool(layer.Visible), Bool(visible),
                () => m_Manager.RequestSetLayerVisibleRpc(id, visible), true);
        }

        void Move(string id, int delta)
        {
            int from = m_Manager.Stack.IndexOf(id);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= m_Manager.Layers.Count) return;
            m_Change(StudyActions.ReorderLayer, m_Manager.Layers[from].Name, (from + 1).ToString(k_Inv), (to + 1).ToString(k_Inv),
                () => m_Manager.RequestReorderLayerRpc(id, to), true);
        }

        void Duplicate(string id)
        {
            PlotLayer layer = m_Manager.Stack.Find(id);
            if (layer == null) return;
            m_Change(StudyActions.DuplicateLayer, null, layer.Name, LayerRules.UniqueName(m_Manager.Stack, layer.Name + " copy"),
                () => m_Manager.RequestDuplicateLayerRpc(id), true);
        }

        void Remove(string id)
        {
            PlotLayer layer = m_Manager.Stack.Find(id);
            if (layer == null) return;
            m_Change(StudyActions.RemoveLayer, layer.Name, KindText(layer), "", () => m_Manager.RequestRemoveLayerRpc(id), true);
        }

        void Rename(string name)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            string trimmed = (name ?? "").Trim();
            if (layer == null || trimmed.Length == 0 || trimmed == layer.Name)
            {
                if (layer != null) m_Name.SetValueWithoutNotify(layer.Name); // refused: back to the real name
                return;
            }
            string id = layer.Id;
            m_Change(StudyActions.RenameLayer, layer.Name, layer.Name, trimmed, () => m_Manager.RequestRenameLayerRpc(id, trimmed), true);
        }

        void OnOpacity(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || index < 0 || index >= k_Opacities.Length) return;
            float value = k_Opacities[index];
            if (Mathf.Approximately(value, layer.Opacity)) return;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerOpacity, layer.Name, layer.Opacity.ToString("0.##", k_Inv), value.ToString("0.##", k_Inv),
                () => m_Manager.RequestSetLayerOpacityRpc(id, value), true);
        }

        void OnColour(Color32 colour)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null) return;
            uint packed = LayerSnapshot.PackColour(colour);
            if (layer.ColorColumnIndex < 0 && packed == LayerSnapshot.PackColour(layer.FixedColour)) return;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerColour, layer.Name, PlotStateDiff.ColourText(LayerSnapshot.PackColour(layer.FixedColour)),
                PlotStateDiff.ColourText(packed), () => m_Manager.RequestSetLayerColourRpc(id, colour), true);
        }

        void OnGlyph(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || index < 0 || index == layer.DefaultGlyph) return;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerGlyph, layer.Name, layer.DefaultGlyph.ToString(k_Inv), index.ToString(k_Inv),
                () => m_Manager.RequestSetLayerGlyphRpc(id, index), true);
        }

        void OnLabel(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            int column = index - 1;
            if (layer == null || index < 0 || column == layer.LabelColumnIndex) return;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerLabel, layer.Name, PlotStateDiff.ColumnName(layer.LabelColumnIndex, layer.Dataset),
                PlotStateDiff.ColumnName(column, layer.Dataset), () => m_Manager.RequestSetLayerLabelColumnRpc(id, column), true);
        }

        void OnTimeMode(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || index < 0 || index > 2 || (int)layer.TimeBehaviour == index) return;
            var behaviour = (LayerTimeBehaviour)index;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerTimeBehaviour, layer.Name, layer.TimeBehaviour.ToString(), behaviour.ToString(),
                () => m_Manager.RequestSetLayerTimeBehaviourRpc(id, behaviour), true);
        }

        void OnEndTime(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            int column = index - 1;
            if (layer == null || index < 0 || column == layer.EndTimeColumnIndex) return;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerEndTime, layer.Name, PlotStateDiff.ColumnName(layer.EndTimeColumnIndex, layer.Dataset),
                PlotStateDiff.ColumnName(column, layer.Dataset), () => m_Manager.RequestSetLayerEndTimeColumnRpc(id, column), true);
        }

        void OnShuffle(bool unlocked)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || m_Manager.IsLayerShuffleUnlocked(layer.Id) == unlocked) return;
            string id = layer.Id;
            // Local and not in the snapshot: no undo step (as the field locks).
            m_Change(SetLayerShuffleAction, layer.Name, Bool(!unlocked), Bool(unlocked),
                () => m_Manager.RequestSetLayerShuffleUnlockedRpc(id, unlocked), false);
        }

        void OnAxis(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            int axis = index - 1;
            if (layer == null || index < 0 || axis == layer.DriverAxis) return;
            string id = layer.Id;
            m_Change(StudyActions.SetComputedParam, layer.Name, "Axis=" + PlotStateDiff.AxisText(layer.DriverAxis),
                "Axis=" + PlotStateDiff.AxisText(axis), () => m_Manager.RequestSetComputedAxisRpc(id, axis), true);
        }

        void OnOrder(int index)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            int column = index - 1;
            if (layer == null || index < 0 || column == layer.OrderColumnIndex) return;
            DatasetColumnar ds = (m_Manager.Stack.Find(layer.SourceLayerId) ?? layer).Dataset;
            string id = layer.Id;
            m_Change(StudyActions.SetComputedParam, layer.Name, "Order=" + OrderText(layer.OrderColumnIndex, ds),
                "Order=" + OrderText(column, ds), () => m_Manager.RequestSetComputedColumnRpc(id, column), true);
        }

        void OnSmoothed(bool flag)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || layer.Smoothed == flag) return;
            string id = layer.Id;
            m_Change(StudyActions.SetComputedParam, layer.Name, "Smoothed=" + Bool(layer.Smoothed), "Smoothed=" + Bool(flag),
                () => m_Manager.RequestSetComputedFlagRpc(id, flag), true);
        }

        void SetComputedFloat(string param, Func<PlotLayer, float> get, float value, Action<string, float> request)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || float.IsNaN(value) || float.IsInfinity(value) || get(layer).Equals(value)) return;
            string id = layer.Id;
            m_Change(StudyActions.SetComputedParam, layer.Name, param + "=" + PlotStateDiff.FormatFloat(get(layer)),
                param + "=" + PlotStateDiff.FormatFloat(value), () => request(id, value), true);
            Refresh(true); // the manager clamps: show what it kept
        }

        void OnLineWidth(float width)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || float.IsNaN(width) || float.IsInfinity(width) || layer.LineWidth.Equals(width)) return;
            string id = layer.Id;
            m_Change(StudyActions.SetLayerLineWidth, layer.Name, layer.LineWidth.ToString("0.####", k_Inv), width.ToString("0.####", k_Inv),
                () => m_Manager.RequestSetLayerLineWidthRpc(id, width), true);
            Refresh(true);
        }

        void Set(string action, Func<PlotLayer, bool> get, bool value, Action<string, bool> request)
        {
            PlotLayer layer = m_Manager.ActiveLayer;
            if (layer == null || get(layer) == value) return;
            string id = layer.Id;
            m_Change(action, layer.Name, Bool(get(layer)), Bool(value), () => request(id, value), true);
            Refresh(true); // a refusal (e.g. Normalise off, LFR-12) leaves the toggle where the manager is
        }

        // ================================================================== helpers

        /// <summary>"Dataset: file.cdataset", "Subset: file.cdataset", "Computed: Trendline of Movement" (as PlotStateDiff).</summary>
        public static string KindText(PlotLayer layer, LayerStack stack = null)
        {
            if (layer == null) return "";
            if (layer.Kind != LayerKind.Computed) return layer.Kind + ": " + (layer.DatasetName ?? "");
            PlotLayer source = stack?.Find(layer.SourceLayerId);
            return "Computed: " + LayerRules.ComputedKindName(layer.ComputedKind) + " of " + (source?.Name ?? "source");
        }

        string KindText(PlotLayer layer) => KindText(layer, m_Manager.Stack);

        static string OrderText(int column, DatasetColumnar ds) => column < 0 ? k_Auto : PlotStateDiff.ColumnName(column, ds);
        static string Bool(bool value) => value ? "True" : "False";
        static string Percent(float value) => Mathf.RoundToInt(value * 100f).ToString(k_Inv) + "%";

        static float Nearest(float[] options, float value)
        {
            float best = options[0];
            foreach (float o in options)
                if (Mathf.Abs(o - value) < Mathf.Abs(best - value)) best = o;
            return best;
        }

        static List<string> ColumnNames(DatasetColumnar ds)
        {
            var names = new List<string>();
            if (ds == null) return names;
            foreach (DatasetColumn column in ds.Columns) names.Add(column != null ? column.Name : "");
            return names;
        }

        static List<string> ColumnsWithNone(DatasetColumnar ds)
        {
            var list = new List<string> { k_None };
            list.AddRange(ColumnNames(ds));
            return list;
        }

        static string Choice(List<string> choices, int index) => index >= 0 && index < choices.Count ? choices[index] : choices.Count > 0 ? choices[0] : "";

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList("dash-row");
            return row;
        }

        static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("dash-note");
            return label;
        }

        static Button ButtonOf(string text, Action click, string tooltip)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            button.AddToClassList("dash-button");
            return button;
        }

        DropdownField DropdownOf(string label, Action<int> handler)
        {
            var field = new DropdownField(label);
            field.RegisterValueChangedCallback(_ => { if (!m_Updating) handler(field.index); });
            return field;
        }

        Toggle ToggleOf(string label, Action<bool> handler)
        {
            var toggle = new Toggle(label);
            toggle.RegisterValueChangedCallback(evt => { if (!m_Updating) handler(evt.newValue); });
            return toggle;
        }

        FloatField FloatOf(string label, Action<float> handler)
        {
            var field = new FloatField(label) { isDelayed = true };
            field.RegisterValueChangedCallback(evt => { if (!m_Updating) handler(evt.newValue); });
            return field;
        }

        static void Show(VisualElement e, bool visible) => e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
