using System;
using System.Collections.Generic;
using System.Globalization;
using DataViz.Networking;

namespace DataViz.Study
{
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

        /// <summary>True when anything other than the ignored fields differs.</summary>
        public static bool HasRelevantChange(PlotSnapshot a, PlotSnapshot b)
        {
            if (a == null || b == null)
                return !ReferenceEquals(a, b);

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
                   a.IsPlaying != b.IsPlaying;
        }

        /// <summary>One entry per named change; empty when nothing relevant changed.</summary>
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

            if (before.IsPlaying != after.IsPlaying)
                result.Add(after.IsPlaying ? (StudyActions.Play, "", "") : (StudyActions.Pause, "", ""));

            return result;
        }

        // ---- glyphs -------------------------------------------------------------------------

        static void DescribeGlyphs(List<(string, string, string)> result, PlotSnapshot before, PlotSnapshot after, DatasetColumnar dataset)
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

        static Dictionary<string, int> GlyphMap(PlotSnapshot s)
        {
            var map = new Dictionary<string, int>();
            int n = Math.Min(Len(s.GlyphLabels), Len(s.GlyphIndices));
            for (int i = 0; i < n; i++)
                map[s.GlyphLabels[i] ?? ""] = s.GlyphIndices[i];
            return map;
        }

        static string GlyphRangesText(PlotSnapshot s)
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

        internal static List<FilterInfo> Filters(PlotSnapshot s)
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

        static void DescribeFilters(List<(string, string, string)> result, PlotSnapshot before, PlotSnapshot after, DatasetColumnar dataset)
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
