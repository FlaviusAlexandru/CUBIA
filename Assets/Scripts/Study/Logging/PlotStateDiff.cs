using System;
using System.Collections.Generic;
using System.Globalization;
using DataViz.Networking;
using UnityEngine;

namespace DataViz.Study
{
    /// <summary>One named change from PlotStateDiff.DescribeStack. Layer is the layer's Name, "" for plot-level rows.</summary>
    public readonly struct LayerChange
    {
        public readonly string Layer;
        public readonly string Action;
        public readonly string OldValue;
        public readonly string NewValue;

        public LayerChange(string layer, string action, string oldValue, string newValue)
        {
            Layer = layer ?? "";
            Action = action ?? "";
            OldValue = oldValue ?? "";
            NewValue = newValue ?? "";
        }

        public override string ToString() => $"{Action}[{Layer}]({OldValue}->{NewValue})";
    }

    /// <summary>
    /// Names what changed between two <see cref="PlotSnapshot"/>s, as StudyActions with old/new
    /// values (column names, not indices). Used to log participant changes, which the logger only
    /// sees as "OnPlotSettingsChanged fired". TimeScrub, ScrubServerTime, PlaybackSpeed and
    /// DatasetHash are ignored: the scrub moves every frame during playback.
    /// </summary>
    public static class PlotStateDiff
    {
        const float k_PointSizeEpsilon = 1e-4f;
        internal const string AnyValue = "Any value";
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;
        static readonly LayerSnapshot k_NoLayer = new LayerSnapshot(); // read-only stand-in for an empty plot

        /// <summary>True when anything other than the ignored fields differs.</summary>
        public static bool HasRelevantChange(PlotSnapshot a, PlotSnapshot b)
        {
            if (a == null || b == null)
                return !ReferenceEquals(a, b);

            if (StackDiffers(a, b))
                return true;

            return a.DatasetName != b.DatasetName ||
                   a.XColumn != b.XColumn || a.YColumn != b.YColumn || a.ZColumn != b.ZColumn ||
                   a.ColourColumn != b.ColourColumn || a.TimeColumn != b.TimeColumn ||
                   Math.Abs(a.PointSize - b.PointSize) > k_PointSizeEpsilon ||
                   a.SizeMapped != b.SizeMapped || a.SizeColumn != b.SizeColumn ||
                   !Same(a.FilterColumns, b.FilterColumns) || !Same(a.FilterLabels, b.FilterLabels) ||
                   !Same(a.FilterRangeCounts, b.FilterRangeCounts) ||
                   !Same(a.FilterRangeMins, b.FilterRangeMins) || !Same(a.FilterRangeMaxs, b.FilterRangeMaxs) ||
                   a.GlyphColumn != b.GlyphColumn ||
                   !Same(a.GlyphLabels, b.GlyphLabels) || !Same(a.GlyphIndices, b.GlyphIndices) ||
                   !Same(a.RangeGlyphs, b.RangeGlyphs) || !Same(a.RangeMins, b.RangeMins) || !Same(a.RangeMaxs, b.RangeMaxs) ||
                   a.IsPlaying != b.IsPlaying ||
                   Math.Abs(a.PlaybackRate - b.PlaybackRate) > 1e-4f;
        }

        /// <summary>One entry per named change; empty when nothing relevant changed. Compares the two active layers and playback.</summary>
        public static List<(string action, string oldValue, string newValue)> Describe(
            PlotSnapshot before, PlotSnapshot after, DatasetColumnar dataset)
        {
            var result = new List<(string action, string oldValue, string newValue)>();
            if (before == null || after == null)
                return result;

            // A dataset load resets every mapping; one row says it all.
            if ((before.DatasetName ?? "") != (after.DatasetName ?? ""))
            {
                result.Add((StudyActions.LoadDataset, before.DatasetName ?? "", after.DatasetName ?? ""));
                return result;
            }

            DescribeMapping(result, before.ActiveLayer ?? k_NoLayer, after.ActiveLayer ?? k_NoLayer, dataset);
            DescribePlayback(result, before, after);
            return result;
        }

        // The settings the pre-layer plot had, in the pre-layer row order.
        static void DescribeMapping(List<(string, string, string)> result, LayerSnapshot before, LayerSnapshot after, DatasetColumnar dataset)
        {
            AddColumn(result, StudyActions.SetX, before.XColumn, after.XColumn, dataset);
            AddColumn(result, StudyActions.SetY, before.YColumn, after.YColumn, dataset);
            AddColumn(result, StudyActions.SetZ, before.ZColumn, after.ZColumn, dataset);
            AddColumn(result, StudyActions.SetColor, before.ColourColumn, after.ColourColumn, dataset);
            AddColumn(result, StudyActions.SetTime, before.TimeColumn, after.TimeColumn, dataset);

            if (before.SizeMapped != after.SizeMapped)
                result.Add((StudyActions.SetSizeMapped, BoolText(before.SizeMapped), BoolText(after.SizeMapped)));
            AddColumn(result, StudyActions.SetSize, before.SizeColumn, after.SizeColumn, dataset);

            if (Math.Abs(before.PointSize - after.PointSize) > k_PointSizeEpsilon)
                result.Add((StudyActions.SetPointSize,
                    before.PointSize.ToString("0.###", k_Inv), after.PointSize.ToString("0.###", k_Inv)));

            DescribeGlyphs(result, before, after, dataset);
            DescribeFilters(result, before, after, dataset);
        }

        static void DescribePlayback(List<(string, string, string)> result, PlotSnapshot before, PlotSnapshot after)
        {
            if (Math.Abs(before.PlaybackRate - after.PlaybackRate) > 1e-4f)
                result.Add((StudyActions.SetPlaybackRate,
                    before.PlaybackRate.ToString("0.##", k_Inv), after.PlaybackRate.ToString("0.##", k_Inv)));

            if (before.IsPlaying != after.IsPlaying)
                result.Add(after.IsPlaying ? (StudyActions.Play, "", "") : (StudyActions.Pause, "", ""));
        }

        // ---- layer stacks -------------------------------------------------------------------

        /// <summary>
        /// Names what changed between two layer stacks, one LayerChange per log row. Layers are
        /// matched by Id; Layer is the layer's Name after the change ("" for plot-level rows).
        /// Order: a dataset load that leaves one layer with a new dataset (RequestLoadDatasetRpc,
        /// "new plot") is the single LoadDataset row with an empty Layer, as before layers.
        /// Otherwise RemoveLayer, AddLayer / DuplicateLayer, ReorderLayer (1-based positions),
        /// SelectLayer (names), then per remaining layer in stack order: RenameLayer, the pre-layer
        /// mapping rows (SetX … filters), the layer-only rows, and last the plot-level rows (Play,
        /// Pause, SetPlaybackRate). SelectLayer is left out when the active layer changed only
        /// because it was added or the old one was removed. Column names come from each layer's own
        /// dataset (a computed layer: its source's, or its frozen one). datasetOf resolves a
        /// dataset file name to the loaded dataset and may return null (columns are then "#index").
        /// Identical stacks and scrub-only changes give an empty list.
        /// </summary>
        public static List<LayerChange> DescribeStack(PlotSnapshot before, PlotSnapshot after, Func<string, DatasetColumnar> datasetOf)
        {
            var result = new List<LayerChange>();
            if (before == null || after == null)
                return result;

            LayerSnapshot[] oldLayers = before.Layers ?? Array.Empty<LayerSnapshot>();
            LayerSnapshot[] newLayers = after.Layers ?? Array.Empty<LayerSnapshot>();
            LayerSnapshot activeBefore = before.ActiveLayer;
            LayerSnapshot activeAfter = after.ActiveLayer;

            if (IsStackLoad(before, after, activeBefore, activeAfter, out string loadedFrom))
            {
                result.Add(new LayerChange("", StudyActions.LoadDataset, loadedFrom, after.DatasetName ?? ""));
                return result;
            }

            foreach (LayerSnapshot old in oldLayers)
            {
                if (old != null && after.FindLayer(old.Id) == null)
                    result.Add(new LayerChange(old.Name, StudyActions.RemoveLayer, LayerKindText(before, old), ""));
            }

            for (int i = 0; i < newLayers.Length; i++)
            {
                LayerSnapshot added = newLayers[i];
                if (added == null || before.FindLayer(added.Id) != null)
                    continue;
                LayerSnapshot original = DuplicateSource(before, newLayers, i);
                if (original != null)
                    result.Add(new LayerChange(added.Name, StudyActions.DuplicateLayer, original.Name, added.Name));
                else
                    result.Add(new LayerChange(added.Name, StudyActions.AddLayer, "", LayerKindText(after, added)));
            }

            DescribeOrder(result, before, after, activeAfter);

            // A new layer becomes active, and removing the active layer activates a neighbour: both
            // are already named by the Add/Remove row, so only a real selection gets its own row.
            if (activeBefore != null && activeAfter != null && activeBefore.Id != activeAfter.Id &&
                after.FindLayer(activeBefore.Id) != null && before.FindLayer(activeAfter.Id) != null)
                result.Add(new LayerChange(activeAfter.Name, StudyActions.SelectLayer, activeBefore.Name, activeAfter.Name));

            var rows = new List<(string action, string oldValue, string newValue)>();
            foreach (LayerSnapshot now in newLayers)
            {
                LayerSnapshot was = now != null ? before.FindLayer(now.Id) : null;
                if (was == null)
                    continue;
                rows.Clear();
                DescribeLayer(rows, after, was, now, datasetOf);
                foreach (var row in rows)
                    result.Add(new LayerChange(now.Name, row.action, row.oldValue, row.newValue));
            }

            rows.Clear();
            DescribePlayback(rows, before, after);
            foreach (var row in rows)
                result.Add(new LayerChange("", row.action, row.oldValue, row.newValue));
            return result;
        }

        // "New plot" (RequestLoadDatasetRpc): one point layer is left and it is a fresh layer or its
        // dataset changed. Removing layers down to one whose dataset is unchanged, or a computed
        // layer frozen by its source's removal, is not a load. from = the kept layer's old dataset
        // (the active one's when the stack was replaced).
        static bool IsStackLoad(PlotSnapshot before, PlotSnapshot after, LayerSnapshot activeBefore, LayerSnapshot activeAfter, out string from)
        {
            from = before.DatasetName ?? "";
            if (activeBefore == null || activeAfter == null || Len(after.Layers) != 1 ||
                (LayerKind)activeAfter.Kind == LayerKind.Computed)
                return false;
            LayerSnapshot kept = before.FindLayer(activeAfter.Id);
            if (kept == null)
                return true;
            from = kept.DatasetName ?? "";
            return from != (activeAfter.DatasetName ?? "");
        }

        // LayerStack.Duplicate inserts "<name> copy" (made unique) right after the original with the
        // same settings. Returns the original as it was before, or null when layers[index] is not one.
        static LayerSnapshot DuplicateSource(PlotSnapshot before, LayerSnapshot[] layers, int index)
        {
            if (index <= 0 || layers[index - 1] == null)
                return null;
            LayerSnapshot copy = layers[index];
            LayerSnapshot original = before.FindLayer(layers[index - 1].Id);
            if (original == null || !(copy.Name ?? "").StartsWith((original.Name ?? "") + " copy", StringComparison.Ordinal))
                return null;
            return SettingsDiffer(original, copy) ? null : original;
        }

        static void DescribeOrder(List<LayerChange> result, PlotSnapshot before, PlotSnapshot after, LayerSnapshot activeAfter)
        {
            var oldOrder = new List<string>();
            foreach (LayerSnapshot l in before.Layers ?? Array.Empty<LayerSnapshot>())
                if (l != null && after.FindLayer(l.Id) != null) oldOrder.Add(l.Id);
            var newOrder = new List<string>();
            foreach (LayerSnapshot l in after.Layers ?? Array.Empty<LayerSnapshot>())
                if (l != null && before.FindLayer(l.Id) != null) newOrder.Add(l.Id);
            if (SameOrder(oldOrder, newOrder, null))
                return;

            // One layer moved (RequestReorderLayerRpc): the one whose removal makes both orders
            // equal. The active layer is tried first, since the menus move the selected layer.
            var candidates = new List<string>(newOrder);
            if (activeAfter != null && candidates.Remove(activeAfter.Id))
                candidates.Insert(0, activeAfter.Id);
            foreach (string id in candidates)
            {
                if (!SameOrder(oldOrder, newOrder, id))
                    continue;
                LayerSnapshot moved = after.FindLayer(id);
                result.Add(new LayerChange(moved.Name, StudyActions.ReorderLayer,
                    (IndexOf(before.Layers, id) + 1).ToString(k_Inv), (IndexOf(after.Layers, id) + 1).ToString(k_Inv)));
                return;
            }

            // Several moved at once (a snapshot apply): the whole order, by name.
            result.Add(new LayerChange("", StudyActions.ReorderLayer, OrderText(before, oldOrder), OrderText(after, newOrder)));
        }

        static bool SameOrder(List<string> a, List<string> b, string skip)
        {
            int i = 0, j = 0;
            while (true)
            {
                while (i < a.Count && a[i] == skip) i++;
                while (j < b.Count && b[j] == skip) j++;
                if (i >= a.Count || j >= b.Count)
                    return i >= a.Count && j >= b.Count;
                if (a[i++] != b[j++])
                    return false;
            }
        }

        static int IndexOf(LayerSnapshot[] layers, string id)
        {
            for (int i = 0; i < Len(layers); i++)
                if (layers[i] != null && layers[i].Id == id) return i;
            return -1;
        }

        static string OrderText(PlotSnapshot s, List<string> ids)
        {
            var names = new List<string>(ids.Count);
            foreach (string id in ids)
                names.Add(s.FindLayer(id)?.Name ?? id);
            return string.Join(" | ", names);
        }

        /// <summary>
        /// The rows for one layer that exists on both sides (after = the stack it is in now).
        /// A point layer whose dataset changed is one LoadDataset row (its mapping was reset with
        /// it). A computed layer has no mapping of its own (requests refuse it; a freeze copies the
        /// source's), so it only gets its layer-only and computed rows.
        /// </summary>
        static void DescribeLayer(List<(string, string, string)> result, PlotSnapshot after, LayerSnapshot was, LayerSnapshot now,
                                  Func<string, DatasetColumnar> datasetOf)
        {
            if ((was.Name ?? "") != (now.Name ?? ""))
                result.Add((StudyActions.RenameLayer, was.Name ?? "", now.Name ?? ""));

            bool computed = (LayerKind)now.Kind == LayerKind.Computed;
            if (!computed && (was.DatasetName ?? "") != (now.DatasetName ?? ""))
            {
                result.Add((StudyActions.LoadDataset, was.DatasetName ?? "", now.DatasetName ?? ""));
                return;
            }

            DatasetColumnar dataset = DatasetOfLayer(after, now, datasetOf);
            if (!computed)
                DescribeMapping(result, was, now, dataset);

            if (was.Visible != now.Visible)
                result.Add((StudyActions.SetLayerVisible, BoolText(was.Visible), BoolText(now.Visible)));
            if (was.Normalised != now.Normalised)
                result.Add((StudyActions.SetLayerNormalised, BoolText(was.Normalised), BoolText(now.Normalised)));
            if (Math.Abs(was.Opacity - now.Opacity) > 1e-4f)
                result.Add((StudyActions.SetLayerOpacity, was.Opacity.ToString("0.##", k_Inv), now.Opacity.ToString("0.##", k_Inv)));
            if (was.FixedColour != now.FixedColour)
                result.Add((StudyActions.SetLayerColour, ColourText(was.FixedColour), ColourText(now.FixedColour)));
            if (was.DefaultGlyph != now.DefaultGlyph)
                result.Add((StudyActions.SetLayerGlyph, was.DefaultGlyph.ToString(k_Inv), now.DefaultGlyph.ToString(k_Inv)));
            if (was.TimeBehaviour != now.TimeBehaviour)
                result.Add((StudyActions.SetLayerTimeBehaviour, TimeBehaviourText(was.TimeBehaviour), TimeBehaviourText(now.TimeBehaviour)));
            AddColumn(result, StudyActions.SetLayerEndTime, was.EndTimeColumn, now.EndTimeColumn, dataset);
            AddColumn(result, StudyActions.SetLayerLabel, was.LabelColumn, now.LabelColumn, dataset);
            if (was.HiddenInSource != now.HiddenInSource)
                result.Add((StudyActions.SetSubsetHiddenInSource, BoolText(was.HiddenInSource), BoolText(now.HiddenInSource)));
            if (Math.Abs(was.LineWidth - now.LineWidth) > 1e-6f)
                result.Add((StudyActions.SetLayerLineWidth, was.LineWidth.ToString("0.####", k_Inv), now.LineWidth.ToString("0.####", k_Inv)));

            if (was.DriverAxis != now.DriverAxis)
                AddParam(result, "Axis", AxisText(was.DriverAxis), AxisText(now.DriverAxis));
            if (was.OrderColumn != now.OrderColumn)
                AddParam(result, "Order", OrderColumnText(was.OrderColumn, dataset), OrderColumnText(now.OrderColumn, dataset));
            if (was.Smoothed != now.Smoothed)
                AddParam(result, "Smoothed", BoolText(was.Smoothed), BoolText(now.Smoothed));
            if (!was.RefValue.Equals(now.RefValue))
                AddParam(result, "Value", FormatFloat(was.RefValue), FormatFloat(now.RefValue));
            if (Math.Abs(was.SmoothSpan - now.SmoothSpan) > 1e-6f)
                AddParam(result, "Span", was.SmoothSpan.ToString("0.###", k_Inv), now.SmoothSpan.ToString("0.###", k_Inv));
        }

        static void AddParam(List<(string, string, string)> result, string name, string before, string after) =>
            result.Add((StudyActions.SetComputedParam, name + "=" + before, name + "=" + after));

        /// <summary>
        /// The dataset whose column names a layer's indices refer to: its own, or for a computed
        /// layer its source's while the source exists in s (its own frozen one after that).
        /// </summary>
        internal static DatasetColumnar DatasetOfLayer(PlotSnapshot s, LayerSnapshot layer, Func<string, DatasetColumnar> datasetOf)
        {
            if (layer == null || datasetOf == null)
                return null;
            string name = layer.DatasetName ?? "";
            if ((LayerKind)layer.Kind == LayerKind.Computed)
            {
                LayerSnapshot source = s?.FindLayer(layer.SourceLayerId);
                if (source != null)
                    name = source.DatasetName ?? "";
            }
            return name.Length > 0 ? datasetOf(name) : null;
        }

        /// <summary>"#RRGGBB", or "#RRGGBBAA" when not opaque (LayerSnapshot.PackColour layout).</summary>
        internal static string ColourText(uint packed)
        {
            Color32 c = LayerSnapshot.UnpackColour(packed);
            string rgb = "#" + c.r.ToString("X2", k_Inv) + c.g.ToString("X2", k_Inv) + c.b.ToString("X2", k_Inv);
            return c.a == 255 ? rgb : rgb + c.a.ToString("X2", k_Inv);
        }

        internal static string TimeBehaviourText(byte value) => ((LayerTimeBehaviour)value).ToString();

        // -1 = LayerRules.DefaultDriverAxis / the source's time column.
        internal static string AxisText(int axis) => axis < 0 ? "Auto" : LayerRules.AxisName(axis);
        static string OrderColumnText(int column, DatasetColumnar dataset) => column < 0 ? "Auto" : ColumnName(column, dataset);

        /// <summary>"Dataset: movement.cdataset", "Subset: movement.cdataset", "Computed: Trendline of Movement".</summary>
        static string LayerKindText(PlotSnapshot s, LayerSnapshot layer)
        {
            var kind = (LayerKind)layer.Kind;
            if (kind != LayerKind.Computed)
                return kind + ": " + (layer.DatasetName ?? "");
            LayerSnapshot source = s.FindLayer(layer.SourceLayerId);
            return kind + ": " + LayerRules.ComputedKindName((ComputedLayerKind)layer.ComputedKind) + " of " + (source?.Name ?? "source");
        }

        // Structure, active layer, and every layer's settings except DatasetHash (the scrub-only
        // rule: hashes and time do not count).
        static bool StackDiffers(PlotSnapshot a, PlotSnapshot b)
        {
            int n = Len(a.Layers);
            if (n != Len(b.Layers) || (a.ActiveLayerId ?? "") != (b.ActiveLayerId ?? ""))
                return true;
            for (int i = 0; i < n; i++)
            {
                LayerSnapshot x = a.Layers[i], y = b.Layers[i];
                if (x == null || y == null)
                {
                    if (!ReferenceEquals(x, y)) return true;
                    continue;
                }
                if (x.Id != y.Id || x.Name != y.Name || SettingsDiffer(x, y))
                    return true;
            }
            return false;
        }

        // Every field but Id, Name and DatasetHash.
        static bool SettingsDiffer(LayerSnapshot x, LayerSnapshot y) =>
            x.Kind != y.Kind || x.Visible != y.Visible ||
            x.DatasetName != y.DatasetName || x.SourceLayerId != y.SourceLayerId ||
            x.XColumn != y.XColumn || x.YColumn != y.YColumn || x.ZColumn != y.ZColumn || x.Normalised != y.Normalised ||
            x.ColourColumn != y.ColourColumn || x.FixedColour != y.FixedColour ||
            Math.Abs(x.PointSize - y.PointSize) > k_PointSizeEpsilon || x.SizeMapped != y.SizeMapped || x.SizeColumn != y.SizeColumn ||
            Math.Abs(x.Opacity - y.Opacity) > 1e-4f || x.DefaultGlyph != y.DefaultGlyph || x.GlyphColumn != y.GlyphColumn ||
            !Same(x.GlyphLabels, y.GlyphLabels) || !Same(x.GlyphIndices, y.GlyphIndices) ||
            !Same(x.RangeGlyphs, y.RangeGlyphs) || !Same(x.RangeMins, y.RangeMins) || !Same(x.RangeMaxs, y.RangeMaxs) ||
            !Same(x.FilterColumns, y.FilterColumns) || !Same(x.FilterLabels, y.FilterLabels) ||
            !Same(x.FilterRangeCounts, y.FilterRangeCounts) || !Same(x.FilterRangeMins, y.FilterRangeMins) || !Same(x.FilterRangeMaxs, y.FilterRangeMaxs) ||
            x.TimeColumn != y.TimeColumn || x.TimeBehaviour != y.TimeBehaviour || x.EndTimeColumn != y.EndTimeColumn ||
            x.LabelColumn != y.LabelColumn || x.HiddenInSource != y.HiddenInSource ||
            x.ComputedKind != y.ComputedKind || x.DriverAxis != y.DriverAxis || x.OrderColumn != y.OrderColumn ||
            x.Smoothed != y.Smoothed || x.RefValue != y.RefValue || x.SmoothSpan != y.SmoothSpan || x.LineWidth != y.LineWidth;

        // ---- glyphs -------------------------------------------------------------------------

        static void DescribeGlyphs(List<(string, string, string)> result, LayerSnapshot before, LayerSnapshot after, DatasetColumnar dataset)
        {
            bool columnChanged = before.GlyphColumn != after.GlyphColumn;
            AddColumn(result, StudyActions.SetGlyphColumn, before.GlyphColumn, after.GlyphColumn, dataset);

            Dictionary<string, int> oldMap = GlyphMap(before);
            Dictionary<string, int> newMap = GlyphMap(after);
            bool assignmentsChanged = !Same(before.GlyphLabels, after.GlyphLabels) || !Same(before.GlyphIndices, after.GlyphIndices);

            if (assignmentsChanged)
            {
                bool any = false;
                foreach (KeyValuePair<string, int> kv in newMap)
                {
                    bool had = oldMap.TryGetValue(kv.Key, out int oldIndex);
                    if (had && oldIndex == kv.Value)
                        continue;
                    result.Add((StudyActions.AssignGlyph,
                        had ? kv.Key + "=" + oldIndex.ToString(k_Inv) : "",
                        kv.Key + "=" + kv.Value.ToString(k_Inv)));
                    any = true;
                }

                // Only removals (e.g. a label reset to the default glyph). A glyph column change
                // clears the assignments by itself, so that case needs no extra row.
                if (!any && !columnChanged)
                {
                    var removed = new List<string>();
                    foreach (KeyValuePair<string, int> kv in oldMap)
                        if (!newMap.ContainsKey(kv.Key))
                            removed.Add(kv.Key + "=" + kv.Value.ToString(k_Inv));
                    if (removed.Count > 0)
                        result.Add((StudyActions.AssignGlyph, string.Join("; ", removed), ""));
                }
            }

            bool rangesChanged = !Same(before.RangeGlyphs, after.RangeGlyphs) ||
                                 !Same(before.RangeMins, after.RangeMins) || !Same(before.RangeMaxs, after.RangeMaxs);
            if (rangesChanged && !(columnChanged && Len(after.RangeGlyphs) == 0))
                result.Add((StudyActions.SetGlyphRanges, GlyphRangesText(before), GlyphRangesText(after)));
        }

        static Dictionary<string, int> GlyphMap(LayerSnapshot s)
        {
            var map = new Dictionary<string, int>();
            int n = Math.Min(Len(s.GlyphLabels), Len(s.GlyphIndices));
            for (int i = 0; i < n; i++)
                map[s.GlyphLabels[i] ?? ""] = s.GlyphIndices[i];
            return map;
        }

        static string GlyphRangesText(LayerSnapshot s)
        {
            int n = Math.Min(Len(s.RangeGlyphs), Math.Min(Len(s.RangeMins), Len(s.RangeMaxs)));
            var parts = new List<string>(n);
            for (int i = 0; i < n; i++)
                parts.Add(s.RangeGlyphs[i].ToString(k_Inv) + ":[" + FormatFloat(s.RangeMins[i]) + "," + FormatFloat(s.RangeMaxs[i]) + "]");
            return string.Join("; ", parts);
        }

        // ---- filters ------------------------------------------------------------------------

        /// <summary>One filter entry unpacked from the snapshot's flattened arrays.</summary>
        internal struct FilterInfo
        {
            public int Column;
            public string Label;
            public List<(float min, float max)> Ranges;

            public bool SameAs(FilterInfo o)
            {
                if (Column != o.Column || Label != o.Label || Ranges.Count != o.Ranges.Count)
                    return false;
                for (int i = 0; i < Ranges.Count; i++)
                    if (!Ranges[i].min.Equals(o.Ranges[i].min) || !Ranges[i].max.Equals(o.Ranges[i].max))
                        return false;
                return true;
            }
        }

        internal static List<FilterInfo> Filters(PlotSnapshot s) => Filters(s?.ActiveLayer ?? k_NoLayer);

        internal static List<FilterInfo> Filters(LayerSnapshot s)
        {
            int n = Len(s.FilterColumns);
            var list = new List<FilterInfo>(n);
            int cursor = 0;
            for (int i = 0; i < n; i++)
            {
                var info = new FilterInfo
                {
                    Column = s.FilterColumns[i],
                    Label = i < Len(s.FilterLabels) ? s.FilterLabels[i] ?? "" : "",
                    Ranges = new List<(float min, float max)>(),
                };
                int count = i < Len(s.FilterRangeCounts) ? s.FilterRangeCounts[i] : 0;
                for (int k = 0; k < count && cursor < Len(s.FilterRangeMins) && cursor < Len(s.FilterRangeMaxs); k++, cursor++)
                    info.Ranges.Add((s.FilterRangeMins[cursor], s.FilterRangeMaxs[cursor]));
                list.Add(info);
            }
            return list;
        }

        static void DescribeFilters(List<(string, string, string)> result, LayerSnapshot before, LayerSnapshot after, DatasetColumnar dataset)
        {
            List<FilterInfo> oldF = Filters(before);
            List<FilterInfo> newF = Filters(after);

            if (oldF.Count > 0 && newF.Count == 0)
            {
                var all = new List<string>(oldF.Count);
                foreach (FilterInfo f in oldF)
                    all.Add(FilterText(f, dataset));
                result.Add((StudyActions.ClearFilters, string.Join(" | ", all), ""));
                return;
            }

            if (oldF.Count == newF.Count)
            {
                for (int i = 0; i < newF.Count; i++)
                    DescribeEntryChange(result, i, oldF[i], newF[i], dataset);
                return;
            }

            if (oldF.Count == 0)
            {
                // The first entry appeared (Filter Column dropdown or the first palette add).
                FilterInfo first = newF[0];
                result.Add((StudyActions.SetFilterColumn, ColumnName(-1, dataset), ColumnName(first.Column, dataset)));
                if (first.Label != "")
                    result.Add((StudyActions.SetFilterValue, AnyValue, first.Label));
                if (first.Ranges.Count > 0)
                    result.Add((StudyActions.SetFilterRanges, "", RangesText(first.Ranges)));
                for (int i = 1; i < newF.Count; i++)
                    result.Add((StudyActions.AddFilter, "", FilterText(newF[i], dataset)));
                return;
            }

            // Count changed with entries on both sides: match entries so a removal from the middle
            // (or the dropdown's "None", which drops entry 0) names the right one.
            List<FilterInfo> removed = Unmatched(oldF, newF);
            List<FilterInfo> added = Unmatched(newF, oldF);
            foreach (FilterInfo f in removed)
                result.Add((StudyActions.RemoveFilter, FilterText(f, dataset), ""));
            foreach (FilterInfo f in added)
                result.Add((StudyActions.AddFilter, "", FilterText(f, dataset)));
        }

        static void DescribeEntryChange(List<(string, string, string)> result, int index, FilterInfo a, FilterInfo b, DatasetColumnar dataset)
        {
            // Entries after the first are prefixed with their 1-based position so rows stay unambiguous.
            string prefix = index == 0 ? "" : (index + 1).ToString(k_Inv) + ": ";

            if (a.Column != b.Column)
            {
                // The manager resets value and ranges with the column, so that is one change.
                result.Add((StudyActions.SetFilterColumn, prefix + ColumnName(a.Column, dataset), prefix + ColumnName(b.Column, dataset)));
                return;
            }

            if (a.Label != b.Label)
                result.Add((StudyActions.SetFilterValue, prefix + LabelText(a.Label), prefix + LabelText(b.Label)));

            var ra = new FilterInfo { Column = 0, Label = "", Ranges = a.Ranges };
            var rb = new FilterInfo { Column = 0, Label = "", Ranges = b.Ranges };
            if (!ra.SameAs(rb))
                result.Add((StudyActions.SetFilterRanges, prefix + RangesText(a.Ranges), prefix + RangesText(b.Ranges)));
        }

        static List<FilterInfo> Unmatched(List<FilterInfo> source, List<FilterInfo> other)
        {
            var used = new bool[other.Count];
            var unmatched = new List<FilterInfo>();
            foreach (FilterInfo f in source)
            {
                int match = -1;
                for (int j = 0; j < other.Count; j++)
                {
                    if (!used[j] && f.SameAs(other[j]))
                    {
                        match = j;
                        break;
                    }
                }
                if (match >= 0)
                    used[match] = true;
                else
                    unmatched.Add(f);
            }
            return unmatched;
        }

        static string FilterText(FilterInfo f, DatasetColumnar dataset)
        {
            string column = ColumnName(f.Column, dataset);
            if (f.Ranges.Count > 0)
                return column + " in " + RangesText(f.Ranges);
            return column + " = " + LabelText(f.Label);
        }

        static string LabelText(string label) => string.IsNullOrEmpty(label) ? AnyValue : label;

        static string RangesText(List<(float min, float max)> ranges)
        {
            var parts = new List<string>(ranges.Count);
            foreach (var r in ranges)
                parts.Add("[" + FormatFloat(r.min) + "," + FormatFloat(r.max) + "]");
            return string.Join("; ", parts);
        }

        // ---- shared helpers (also used by PlotStateJson) ------------------------------------

        /// <summary>"None" for -1, the column name, or "#index" when out of range / no dataset.</summary>
        internal static string ColumnName(int index, DatasetColumnar dataset)
        {
            if (index < 0)
                return "None";
            DatasetColumn column = dataset?.GetColumn(index);
            if (column == null || column.Name == null)
                return "#" + index.ToString(k_Inv);
            return column.Name;
        }

        /// <summary>Short invariant number text; large values (DateTime seconds) keep every integer digit.</summary>
        internal static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return value.ToString(k_Inv);
            if (Math.Abs(value) >= 1e6f)
                return ((double)value).ToString("0", k_Inv);
            return value.ToString("0.#####", k_Inv);
        }

        static string BoolText(bool value) => value ? "True" : "False";

        static void AddColumn(List<(string, string, string)> result, string action, int before, int after, DatasetColumnar dataset)
        {
            if (before != after)
                result.Add((action, ColumnName(before, dataset), ColumnName(after, dataset)));
        }

        static int Len<T>(T[] array) => array?.Length ?? 0;

        static bool Same<T>(T[] a, T[] b) where T : IEquatable<T>
        {
            int n = Len(a);
            if (n != Len(b))
                return false;
            for (int i = 0; i < n; i++)
            {
                T x = a[i];
                T y = b[i];
                if (x == null ? y != null : !x.Equals(y))
                    return false;
            }
            return true;
        }
    }
}
