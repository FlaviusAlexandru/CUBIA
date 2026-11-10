using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace DataViz.Study
{
    /// <summary>
    /// Dashboard version of the VR menu's GlyphRangeEditor: value ranges on a numeric or DateTime
    /// column, drawn as two-handle range sliders (MinMaxSlider) inside a host element of
    /// FacilitatorDashboard.uxml.
    ///
    /// Glyph target: one row per range = shape dropdown + range slider + remove. Each shape (not the
    /// default Circle) is used by one row only, so at most MaxGlyphRanges rows; overlapping rows turn
    /// red and block Apply (ranges may share one edge value). Filter target: no shape, overlaps are
    /// fine (a row passes when it lies in any range), at most MaxFilterRanges rows.
    ///
    /// Edits are a draft held here; the plot changes only on Apply (the controller logs it and calls
    /// RequestSetGlyphRangesRpc / RequestSetFilterRangesRpc), Revert goes back to the committed
    /// ranges. The draft resets when the dataset or the target column changes, or when the committed
    /// ranges change while there is no draft. Sync(manager) is cheap when nothing changed, so it can
    /// run on every OnPlotSettingsChanged (every frame during playback).
    /// </summary>
    public sealed class DashboardRangeEditor
    {
        public enum RangeTarget { Glyph, Filter }

        /// <summary>Raised by Apply with the draft (glyphIndex 0 for filters).</summary>
        public event Action<List<MultiplayerScatterplotManager.GlyphRange>> ApplyRequested;

        public const string k_OverlapWarning = "Ranges overlap — adjust them to apply";
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        readonly RangeTarget m_Target;
        readonly VisualElement m_Host;
        readonly VisualElement m_Rows;
        readonly Label m_Hint, m_Warning, m_Note;
        readonly Button m_Add, m_Apply, m_Revert;

        sealed class Draft
        {
            public int Glyph;
            public VisualElement Row;
            public MinMaxSlider Slider;
            public DropdownField Shape;
            public Label Values;
        }

        readonly List<Draft> m_Draft = new();
        List<MultiplayerScatterplotManager.GlyphRange> m_Committed = new();
        bool m_Dirty;
        bool m_HasOverlap;

        DatasetColumnar m_Dataset;
        int m_Column = int.MinValue;
        bool m_Rangeable;
        float m_Min, m_Max;
        int m_Decimals = 2;
        bool m_IsDateTime, m_DateIsAbsolute;

        public bool IsDirty => m_Dirty;
        public int DraftCount => m_Draft.Count;

        bool IsFilter => m_Target == RangeTarget.Filter;
        int MaxRanges => IsFilter ? MultiplayerScatterplotManager.MaxFilterRanges : MultiplayerScatterplotManager.MaxGlyphRanges;

        public DashboardRangeEditor(VisualElement host, RangeTarget target)
        {
            m_Host = host;
            m_Target = target;
            host.Clear();
            host.AddToClassList("dash-ranges");

            var header = new VisualElement();
            header.AddToClassList("dash-row");
            var title = new Label(IsFilter ? "Value ranges" : "Shape per value range");
            title.AddToClassList("dash-ranges__title");
            title.AddToClassList("dash-grow");
            header.Add(title);
            m_Add = new Button(OnAdd) { text = "Add range" };
            m_Add.AddToClassList("dash-button");
            header.Add(m_Add);
            host.Add(header);

            m_Hint = new Label(IsFilter
                ? "Pick a numeric or date column above to filter by value ranges."
                : "Pick a numeric or date glyph column to assign shapes to value ranges.");
            m_Hint.AddToClassList("dash-note");
            host.Add(m_Hint);

            m_Rows = new VisualElement();
            m_Rows.AddToClassList("dash-ranges__rows");
            host.Add(m_Rows);

            m_Note = new Label(IsFilter
                ? "Rows inside any range are shown. Applying ranges replaces the picked value."
                : "Rows outside every range keep the default shape; a per-label shape above wins.");
            m_Note.AddToClassList("dash-note");
            host.Add(m_Note);

            m_Warning = new Label(k_OverlapWarning);
            m_Warning.AddToClassList("dash-ranges__warning");
            host.Add(m_Warning);

            var buttons = new VisualElement();
            buttons.AddToClassList("dash-row");
            buttons.AddToClassList("dash-row--end");
            m_Revert = new Button(() => Reset()) { text = "Revert" };
            m_Revert.AddToClassList("dash-button");
            m_Apply = new Button(OnApply) { text = "Apply ranges" };
            m_Apply.AddToClassList("dash-button");
            m_Apply.AddToClassList("dash-button--primary");
            buttons.Add(m_Revert);
            buttons.Add(m_Apply);
            host.Add(buttons);

            Refresh();
        }

        // ------------------------------------------------------------ manager sync

        /// <summary>Follows the manager; keeps an unapplied draft unless its column/dataset changed.</summary>
        public void Sync(MultiplayerScatterplotManager m)
        {
            if (m == null) return;
            int column = TargetColumn(m);
            List<MultiplayerScatterplotManager.GlyphRange> committed = Committed(m);
            bool context = m.LoadedDataset != m_Dataset || column != m_Column;
            bool changed = !Same(committed, m_Committed);

            if (context || (changed && !m_Dirty)) Reset(m);
            else if (changed) m_Committed = Copy(committed); // keep the facilitator's draft
        }

        MultiplayerScatterplotManager m_Manager;

        void Reset(MultiplayerScatterplotManager m = null)
        {
            if (m != null) m_Manager = m;
            m = m_Manager;

            m_Draft.Clear();
            m_Rows.Clear();

            m_Dataset = m != null ? m.LoadedDataset : null;
            m_Column = m != null ? TargetColumn(m) : -1;
            DatasetColumn column = m_Dataset != null && m_Column >= 0 && m_Column < m_Dataset.ColumnCount
                ? m_Dataset.Columns[m_Column]
                : null;
            m_Rangeable = column != null && column.IsNumeric && column.MaxValue > column.MinValue;

            List<MultiplayerScatterplotManager.GlyphRange> committed = m != null ? Committed(m) : new();
            if (m_Rangeable)
            {
                m_Min = column.MinValue;
                m_Max = column.MaxValue;
                float span = m_Max - m_Min;
                m_Decimals = Mathf.Clamp(Mathf.CeilToInt(-Mathf.Log10(span / 100f)), 0, 4);
                m_IsDateTime = column.Type == DataValueType.DateTime;
                m_DateIsAbsolute = m_IsDateTime && column.MinValue > 1e8f; // CSVImporter: Unix seconds
                foreach (MultiplayerScatterplotManager.GlyphRange r in committed)
                    AddRow(r.glyphIndex, r.min, r.max);
            }

            m_Committed = Copy(committed);
            m_Dirty = false;
            Refresh();
        }

        int TargetColumn(MultiplayerScatterplotManager m) => IsFilter ? m.FilterColumnIndex : m.GlyphColumnIndex;

        List<MultiplayerScatterplotManager.GlyphRange> Committed(MultiplayerScatterplotManager m)
        {
            if (!IsFilter) return m.GlyphRanges ?? new List<MultiplayerScatterplotManager.GlyphRange>();
            if (m.Filters.Count == 0) return new List<MultiplayerScatterplotManager.GlyphRange>();
            return m.Filters[0].Ranges.ConvertAll(r => new MultiplayerScatterplotManager.GlyphRange { glyphIndex = 0, min = r.min, max = r.max });
        }

        // ------------------------------------------------------------ rows

        void OnAdd()
        {
            if (!m_Rangeable || m_Draft.Count >= MaxRanges) return;
            int glyph = IsFilter ? 0 : FirstUnusedGlyph(null);
            if (glyph < 0) return;

            float low = m_Min, high = m_Max;
            // Glyphs: start in the widest free gap rather than on top of the other ranges.
            // With no gap left, split the widest range in two so the new one does not overlap.
            if (!IsFilter && m_Draft.Count > 0)
            {
                if (TryWidestGap(out float gapLow, out float gapHigh))
                {
                    low = gapLow;
                    high = gapHigh;
                }
                else
                {
                    Draft widest = m_Draft[0];
                    foreach (Draft d in m_Draft)
                        if (d.Slider.maxValue - d.Slider.minValue > widest.Slider.maxValue - widest.Slider.minValue)
                            widest = d;
                    float mid = (widest.Slider.minValue + widest.Slider.maxValue) * 0.5f;
                    low = mid;
                    high = widest.Slider.maxValue;
                    widest.Slider.SetValueWithoutNotify(new Vector2(widest.Slider.minValue, mid));
                }
            }
            AddRow(glyph, low, high);
            m_Dirty = true;
            Refresh();
        }

        void AddRow(int glyph, float low, float high)
        {
            var draft = new Draft { Glyph = glyph };

            var row = new VisualElement();
            row.AddToClassList("dash-range-row");

            var top = new VisualElement();
            top.AddToClassList("dash-row");
            if (!IsFilter)
            {
                draft.Shape = new DropdownField();
                draft.Shape.AddToClassList("dash-range-row__shape");
                draft.Shape.RegisterValueChangedCallback(evt => OnShapeChanged(draft, evt.newValue));
                top.Add(draft.Shape);
            }
            draft.Values = new Label();
            draft.Values.AddToClassList("dash-range-row__values");
            draft.Values.AddToClassList("dash-grow");
            top.Add(draft.Values);
            var remove = new Button(() => OnRemove(draft)) { text = "×", tooltip = "Remove this range" };
            remove.AddToClassList("dash-button");
            remove.AddToClassList("dash-range-row__remove");
            top.Add(remove);
            row.Add(top);

            draft.Slider = new MinMaxSlider(Mathf.Clamp(low, m_Min, m_Max), Mathf.Clamp(high, m_Min, m_Max), m_Min, m_Max);
            draft.Slider.AddToClassList("dash-range-row__slider");
            draft.Slider.RegisterValueChangedCallback(_ =>
            {
                m_Dirty = true;
                Refresh();
            });
            row.Add(draft.Slider);

            draft.Row = row;
            m_Draft.Add(draft);
            m_Rows.Add(row);
        }

        void OnShapeChanged(Draft draft, string shapeName)
        {
            int glyph = Array.IndexOf(ScatterplotUI.k_GlyphShapeNames, shapeName);
            if (glyph < 1 || glyph == draft.Glyph) return;
            if (m_Draft.Exists(d => d != draft && d.Glyph == glyph)) { Refresh(); return; }
            draft.Glyph = glyph;
            m_Dirty = true;
            Refresh();
        }

        void OnRemove(Draft draft)
        {
            m_Draft.Remove(draft);
            draft.Row.RemoveFromHierarchy();
            m_Dirty = true;
            Refresh();
        }

        void OnApply()
        {
            if (!m_Rangeable || m_HasOverlap || !m_Dirty) return;
            List<MultiplayerScatterplotManager.GlyphRange> ranges = BuildDraft();
            // Not dirty any more: the manager's change then rebuilds the rows from the sorted ranges.
            m_Dirty = false;
            ApplyRequested?.Invoke(ranges);
        }

        /// <summary>Called by the controller when the manager refused the ranges.</summary>
        public void MarkApplyFailed()
        {
            m_Dirty = true;
            Refresh();
        }

        public List<MultiplayerScatterplotManager.GlyphRange> BuildDraft()
        {
            var list = new List<MultiplayerScatterplotManager.GlyphRange>(m_Draft.Count);
            foreach (Draft d in m_Draft)
                list.Add(new MultiplayerScatterplotManager.GlyphRange { glyphIndex = d.Glyph, min = d.Slider.minValue, max = d.Slider.maxValue });
            return list;
        }

        int FirstUnusedGlyph(Draft except)
        {
            for (int glyph = 1; glyph < ScatterplotUI.k_GlyphShapeNames.Length; glyph++)
                if (!m_Draft.Exists(d => d != except && d.Glyph == glyph))
                    return glyph;
            return -1;
        }

        bool TryWidestGap(out float gapLow, out float gapHigh)
        {
            var taken = new List<Vector2>();
            foreach (Draft d in m_Draft) taken.Add(new Vector2(d.Slider.minValue, d.Slider.maxValue));
            taken.Sort((a, b) => a.x.CompareTo(b.x));
            gapLow = gapHigh = m_Min;
            float best = 0f, cursor = m_Min;
            foreach (Vector2 t in taken)
            {
                if (t.x - cursor > best) { best = t.x - cursor; gapLow = cursor; gapHigh = t.x; }
                cursor = Mathf.Max(cursor, t.y);
            }
            if (m_Max - cursor > best) { best = m_Max - cursor; gapLow = cursor; gapHigh = m_Max; }
            return best > 0f;
        }

        // ------------------------------------------------------------ refresh

        void Refresh()
        {
            Show(m_Hint, !m_Rangeable);
            Show(m_Rows, m_Rangeable);
            Show(m_Note, m_Rangeable && m_Draft.Count > 0);

            m_HasOverlap = false;
            var overlapping = new HashSet<Draft>();
            if (!IsFilter)
            {
                for (int i = 0; i < m_Draft.Count; i++)
                    for (int j = i + 1; j < m_Draft.Count; j++)
                        if (MultiplayerScatterplotManager.GlyphRangesOverlap(ToRange(m_Draft[i]), ToRange(m_Draft[j])))
                        {
                            overlapping.Add(m_Draft[i]);
                            overlapping.Add(m_Draft[j]);
                        }
                m_HasOverlap = overlapping.Count > 0;
            }

            foreach (Draft d in m_Draft)
            {
                d.Row.EnableInClassList("dash-range-row--overlap", overlapping.Contains(d));
                d.Values.text = $"{Format(d.Slider.minValue)} – {Format(d.Slider.maxValue)}";
                if (d.Shape != null)
                {
                    var options = new List<string>();
                    for (int glyph = 1; glyph < ScatterplotUI.k_GlyphShapeNames.Length; glyph++)
                        if (glyph == d.Glyph || !m_Draft.Exists(o => o != d && o.Glyph == glyph))
                            options.Add(ScatterplotUI.k_GlyphShapeNames[glyph]);
                    d.Shape.choices = options;
                    d.Shape.SetValueWithoutNotify(ScatterplotUI.k_GlyphShapeNames[Mathf.Clamp(d.Glyph, 0, ScatterplotUI.k_GlyphShapeNames.Length - 1)]);
                }
            }

            Show(m_Warning, m_Rangeable && m_HasOverlap);
            m_Add.SetEnabled(m_Rangeable && m_Draft.Count < MaxRanges && (IsFilter || FirstUnusedGlyph(null) >= 0));
            m_Apply.SetEnabled(m_Rangeable && m_Dirty && !m_HasOverlap);
            m_Revert.SetEnabled(m_Rangeable && m_Dirty);
            m_Apply.text = m_Draft.Count == 0 && m_Committed.Count > 0 ? "Remove ranges" : "Apply ranges";
        }

        static MultiplayerScatterplotManager.GlyphRange ToRange(Draft d) =>
            new() { glyphIndex = d.Glyph, min = d.Slider.minValue, max = d.Slider.maxValue };

        static void Show(VisualElement e, bool visible) =>
            e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>Value as shown on the rows: fixed decimals, or a date/elapsed time for DateTime columns.</summary>
        public string Format(float value)
        {
            if (!m_IsDateTime) return value.ToString("F" + m_Decimals, k_Inv);
            if (m_DateIsAbsolute)
                return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value * 1000.0)).LocalDateTime
                    .ToString(m_Max - m_Min >= 2f * 86400f ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", k_Inv);
            // .cdataset: seconds after the column's earliest timestamp.
            return "+" + TimeSpan.FromSeconds(Math.Round(value)).ToString();
        }

        // ------------------------------------------------------------ helpers

        static List<MultiplayerScatterplotManager.GlyphRange> Copy(List<MultiplayerScatterplotManager.GlyphRange> source)
        {
            var copy = new List<MultiplayerScatterplotManager.GlyphRange>();
            if (source == null) return copy;
            foreach (var r in source) copy.Add(new() { glyphIndex = r.glyphIndex, min = r.min, max = r.max });
            return copy;
        }

        static bool Same(List<MultiplayerScatterplotManager.GlyphRange> a, List<MultiplayerScatterplotManager.GlyphRange> b)
        {
            int na = a?.Count ?? 0, nb = b?.Count ?? 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
                if (a[i].glyphIndex != b[i].glyphIndex || a[i].min != b[i].min || a[i].max != b[i].max)
                    return false;
            return true;
        }

        /// <summary>"Square:[1.5,2.25]; Star:[3,4]" (glyphs) or "[1.5,2.25]; [3,4]" (filters), for the log.</summary>
        public static string Describe(List<MultiplayerScatterplotManager.GlyphRange> ranges, bool withShape)
        {
            if (ranges == null || ranges.Count == 0) return "";
            var parts = new List<string>(ranges.Count);
            foreach (var r in ranges)
            {
                string bounds = "[" + r.min.ToString("R", k_Inv) + "," + r.max.ToString("R", k_Inv) + "]";
                string shape = r.glyphIndex >= 0 && r.glyphIndex < ScatterplotUI.k_GlyphShapeNames.Length
                    ? ScatterplotUI.k_GlyphShapeNames[r.glyphIndex] : r.glyphIndex.ToString(k_Inv);
                parts.Add(withShape ? shape + ":" + bounds : bounds);
            }
            return string.Join("; ", parts);
        }

        public List<MultiplayerScatterplotManager.GlyphRange> CommittedCopy() => Copy(m_Committed);
    }
}
