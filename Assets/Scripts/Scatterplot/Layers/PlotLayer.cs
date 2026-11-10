using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataViz
{
    /// <summary>Where a layer's rows come from (spec section 2).</summary>
    public enum LayerKind : byte { Dataset = 0, Subset = 1, Computed = 2 }

    /// <summary>How a layer reacts to the plot's playback clock (LFR-24). Follow = today's fade.</summary>
    public enum LayerTimeBehaviour : byte { Follow = 0, Always = 1, Span = 2 }

    /// <summary>The computed layer kinds of LFR-04. None on every non-computed layer.</summary>
    public enum ComputedLayerKind : byte { None = 0, Trendline = 1, Path = 2, Mean = 3, Reference = 4, SmoothedPath = 5 }

    /// <summary>Severity of a message posted through MultiplayerScatterplotManager.PostPlotMessage.</summary>
    public enum PlotMessageKind { Info, Warning, Refused }

    /// <summary>
    /// One layer of the plot's stack: its source, column mapping, encodings, filters and time
    /// behaviour. Everything the single plot held before layers existed now lives here once per
    /// layer; only playback stays on the plot. Plain data: the manager and LayerStack mutate it,
    /// LayerSnapshot carries it over the network. Column indices are indices into this layer's own
    /// dataset.
    /// </summary>
    [Serializable]
    public sealed class PlotLayer
    {
        public string Id = "";            // "L1", "L2", ...; never reused in a session
        public string Name = "";
        public LayerKind Kind;
        public bool Visible = true;
        public string DatasetName = "";   // file name, as CurrentDatasetName used to be
        [NonSerialized] public DatasetColumnar Dataset;   // resolved through DatasetRegistry; null until loaded
        public string SourceLayerId = ""; // Subset: layer it was taken from. Computed: its source

        public int XColumnIndex = 0, YColumnIndex = 1, ZColumnIndex = 2;
        public bool Normalised;
        public int ColorColumnIndex = -1;
        public Color32 FixedColour = new Color32(0, 255, 255, 255);   // used when ColorColumnIndex < 0; cyan = the pre-layer default
        public float PointSize = 0.1f;
        public bool SizeMapped;
        public int SizeColumnIndex = -1;
        public float Opacity = 1f;
        public int DefaultGlyph = 0;      // layer-wide glyph for rows no glyph rule picks
        public int GlyphColumnIndex = -1;
        public List<MultiplayerScatterplotManager.GlyphAssignment> GlyphAssignments = new();
        public List<MultiplayerScatterplotManager.GlyphRange> GlyphRanges = new();
        public List<MultiplayerScatterplotManager.FilterEntry> Filters = new();
        public int TimeColumnIndex = -1;
        public LayerTimeBehaviour TimeBehaviour = LayerTimeBehaviour.Follow;
        public int EndTimeColumnIndex = -1;
        public int LabelColumnIndex = -1;
        public bool HiddenInSource;                    // Subset only

        public ComputedLayerKind ComputedKind;         // Computed only, as are the six fields below
        public int DriverAxis = -1;                    // Trendline driver / Reference axis (0 X, 1 Y, 2 Z); -1 = auto
        public int OrderColumnIndex = -1;              // Path, SmoothedPath; -1 = source's time column
        public bool Smoothed;                          // Trendline: curve. Reference: plane instead of line
        public float RefValue;                         // Reference: value on DriverAxis, data units
        public float SmoothSpan = 0.1f;                // fraction of the driver / order range
        public float LineWidth = 0.006f;               // plot units

        [NonSerialized] public bool LocalUnavailable;  // this client lacks the dataset or its hash differs

        /// <summary>Dataset and Subset layers draw points; Computed layers draw lines.</summary>
        public bool IsPointLayer => Kind != LayerKind.Computed;

        /// <summary>Subset filters remove rows; the other kinds dim the rows their filters reject.</summary>
        public bool FiltersExclude => Kind == LayerKind.Subset;

        public int FilterColumnIndex => Filters.Count > 0 ? Filters[0].ColumnIndex : -1;
        public string FilterLabel => Filters.Count > 0 ? (Filters[0].Label ?? "") : "";

        /// <summary>The label's own glyph assignment, else DefaultGlyph.</summary>
        public int GetGlyphForLabel(string label) => GetGlyphForLabel(label, DefaultGlyph);

        /// <summary>The label's own glyph assignment, else defaultGlyph.</summary>
        public int GetGlyphForLabel(string label, int defaultGlyph)
        {
            if (string.IsNullOrEmpty(label))
                return defaultGlyph;

            MultiplayerScatterplotManager.GlyphAssignment match = GlyphAssignments.Find(a => a.Label == label);
            return match != null ? match.GlyphIndex : defaultGlyph;
        }

        /// <summary>GetGlyphForRow with DefaultGlyph as the fallback.</summary>
        public int GetGlyphForRow(string label, bool hasValue, float numericValue) =>
            GetGlyphForRow(label, hasValue, numericValue, DefaultGlyph);

        /// <summary>
        /// Glyph for one row of the glyph column: its value's own assignment if it has one,
        /// otherwise the glyph of the range its numeric value falls in (NaN for a categorical
        /// column), otherwise defaultGlyph. Rows with no value always get defaultGlyph.
        /// </summary>
        public int GetGlyphForRow(string label, bool hasValue, float numericValue, int defaultGlyph)
        {
            if (!hasValue)
                return defaultGlyph;

            MultiplayerScatterplotManager.GlyphAssignment match = string.IsNullOrEmpty(label)
                ? null
                : GlyphAssignments.Find(a => a.Label == label);
            if (match != null)
                return match.GlyphIndex;

            int rangeIndex = GetGlyphRangeIndex(numericValue);
            return rangeIndex >= 0 ? GlyphRanges[rangeIndex].glyphIndex : defaultGlyph;
        }

        /// <summary>Index in GlyphRanges of the range containing value (both ends inclusive; sorted, so a shared edge goes to the lower range), or -1.</summary>
        public int GetGlyphRangeIndex(float value)
        {
            if (float.IsNaN(value))
                return -1;

            for (int i = 0; i < GlyphRanges.Count; i++)
            {
                if (value >= GlyphRanges[i].min && value <= GlyphRanges[i].max)
                    return i;
            }

            return -1;
        }

        /// <summary>Deep copy (lists and their entries) with a new id. Dataset is shared, not copied.</summary>
        public PlotLayer Clone(string newId)
        {
            PlotLayer copy = (PlotLayer)MemberwiseClone();
            copy.Id = newId ?? "";
            copy.GlyphAssignments = CopyAssignments(GlyphAssignments);
            copy.GlyphRanges = CopyGlyphRanges(GlyphRanges);
            copy.Filters = CopyFilters(Filters);
            return copy;
        }

        public static List<MultiplayerScatterplotManager.FilterEntry> CopyFilters(List<MultiplayerScatterplotManager.FilterEntry> source)
        {
            var result = new List<MultiplayerScatterplotManager.FilterEntry>(source?.Count ?? 0);
            if (source == null)
                return result;

            foreach (MultiplayerScatterplotManager.FilterEntry entry in source)
            {
                if (entry == null)
                    continue;
                var copy = new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = entry.ColumnIndex, Label = entry.Label ?? "" };
                if (entry.Ranges != null)
                {
                    foreach (MultiplayerScatterplotManager.FilterRange range in entry.Ranges)
                        copy.Ranges.Add(new MultiplayerScatterplotManager.FilterRange { min = range.min, max = range.max });
                }
                result.Add(copy);
            }

            return result;
        }

        public static List<MultiplayerScatterplotManager.GlyphAssignment> CopyAssignments(List<MultiplayerScatterplotManager.GlyphAssignment> source)
        {
            var result = new List<MultiplayerScatterplotManager.GlyphAssignment>(source?.Count ?? 0);
            if (source == null)
                return result;

            foreach (MultiplayerScatterplotManager.GlyphAssignment a in source)
            {
                if (a != null)
                    result.Add(new MultiplayerScatterplotManager.GlyphAssignment { Label = a.Label, GlyphIndex = a.GlyphIndex });
            }

            return result;
        }

        public static List<MultiplayerScatterplotManager.GlyphRange> CopyGlyphRanges(List<MultiplayerScatterplotManager.GlyphRange> source)
        {
            var result = new List<MultiplayerScatterplotManager.GlyphRange>(source?.Count ?? 0);
            if (source == null)
                return result;

            foreach (MultiplayerScatterplotManager.GlyphRange r in source)
            {
                if (r != null)
                    result.Add(new MultiplayerScatterplotManager.GlyphRange { glyphIndex = r.glyphIndex, min = r.min, max = r.max });
            }

            return result;
        }

        public override string ToString() => $"{Id} '{Name}' ({Kind}, {DatasetName})";
    }
}
