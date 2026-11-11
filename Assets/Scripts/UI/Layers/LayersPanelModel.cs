using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DataViz.MenuUI
{
    /// <summary>
    /// What the Layers tab shows, worked out from the stack alone: one row per layer (texts, tags,
    /// warning, which buttons work) and the active layer's property block. Pure and static so the
    /// rules can be checked in edit mode (LayersPanelSelfTests); LayersPanel only copies the result
    /// onto its widgets and turns clicks into manager requests.
    /// </summary>
    public static class LayersPanelModel
    {
        public const int RowsPerPage = 5;
        public const int PickerItemsPerPage = 12;
        public const int MaxFilterValues = 200;

        /// <summary>Opacity steps offered on the panel (LFR-15).</summary>
        public static readonly float[] OpacitySteps = { 1f, 0.75f, 0.5f, 0.25f };
        public static readonly string[] TimeModeNames = { "Follow", "Always", "Span" };
        public static readonly string[] AxisChoiceNames = { "Auto", "X", "Y", "Z" };   // DriverAxis -1..2

        public const string TagNormalised = "Normalised";
        public const string TagHidden = "hidden";
        public const string TagSourceMissing = "source missing";
        public const string TagUnavailable = "not on this device";
        public const string AnyValueOption = "Any value";

        public sealed class Row
        {
            public string Id = "";
            public int Index;
            public string Name = "";        // "1  Movement"
            public string Tags = "";        // "Dataset · movement.cdataset · Normalised"
            public string Warning = "";     // one line; "" when fine
            public bool Active, Visible, Greyed;
            public string VisibleText = "";
            public bool CanMoveUp, CanMoveDown;
        }

        /// <summary>The active layer's property block. Booleans named Show* pick which widgets exist; *Enabled whether they react.</summary>
        public sealed class Props
        {
            public bool HasLayer;
            public string LayerId = "";
            public string Title = "No layer";
            public bool IsPoint, IsSubset, IsComputed;
            public string Warning = "";     // all lines of the active layer's warning

            // Point and computed layers
            public int OpacityStep = -1;    // index into OpacitySteps, -1 = none matches
            public string OpacityText = "";
            public int ColourIndex = -1;    // index into PlayerProfile.Palette, -1 = none matches
            public bool ColourByColumn;
            public string ColourColumnText = "By column";

            // Point layers
            public bool NormaliseOn;
            public string NormaliseText = "";
            public bool ShowHideInSource;
            public bool HideInSourceOn;
            public string HideInSourceText = "";
            public string GlyphText = "";
            public bool ShuffleEnabled, ShuffleOn;
            public string ShuffleText = "";
            public int TimeMode;            // (int)LayerTimeBehaviour
            public string TimeHint = "";
            public bool EndColumnEnabled;
            public string EndColumnText = "";
            public string LabelColumnText = "";

            // Computed layers
            public bool ShowDriverAxis;
            public int DriverAxisChoice;    // index into AxisChoiceNames (DriverAxis + 1)
            public bool ShowFlag, FlagOn;
            public string FlagText = "";
            public bool ShowOrderColumn;
            public string OrderColumnText = "";
            public bool ShowSpan;
            public string SpanText = "";
            public bool ShowValue;
            public string ValueText = "";
            public string LineWidthText = "";
        }

        public sealed class View
        {
            public readonly List<Row> Rows = new();   // the rows on the shown page only
            public int Page, PageCount = 1, LayerCount;
            public string PageText = "";
            public string EmptyText = "";
            public bool CanAddSubset, CanAddComputed;
            public string AddSubsetText = "Add subset", AddComputedText = "Add computed";
            public string AddHint = "";
            public Props Props = new();
        }

        /// <summary>One entry of a picker list: its button text and the value it stands for.</summary>
        public struct Option
        {
            public string Label;
            public int Value;      // column index (-1 = none), kind, …
            public string Text;    // file name, filter label, …
            public Option(string label, int value, string text = "") { Label = label; Value = value; Text = text; }
        }

        // ------------------------------------------------------------------ build

        /// <summary>
        /// The whole view. warningOf(layer) gives the layer's warning (LastFrame for point layers,
        /// the line renderer's status for computed ones; may be null); shuffleUnlocked(id) the local
        /// shuffle flag; referenceStep the RefValue step for the active layer's driver axis.
        /// </summary>
        public static View Build(LayerStack stack, Func<PlotLayer, string> warningOf, Func<string, bool> shuffleUnlocked,
                                 int page, float referenceStep = 1f)
        {
            var view = new View();
            int count = stack?.Count ?? 0;
            view.LayerCount = count;
            view.PageCount = Mathf.Max(1, (count + RowsPerPage - 1) / RowsPerPage);
            view.Page = Mathf.Clamp(page, 0, view.PageCount - 1);
            view.PageText = $"Page {view.Page + 1} of {view.PageCount}";
            view.EmptyText = count == 0 ? "This plot has no layers. Add a dataset layer below." : "";

            for (int i = view.Page * RowsPerPage; i < count && i < (view.Page + 1) * RowsPerPage; i++)
                view.Rows.Add(BuildRow(stack, i, warningOf));

            PlotLayer active = stack?.Active;
            view.CanAddSubset = active != null && active.IsPointLayer;
            view.CanAddComputed = view.CanAddSubset;
            view.AddSubsetText = view.CanAddSubset ? "Add subset of " + active.Name : "Add subset";
            view.AddComputedText = view.CanAddComputed ? "Add computed on " + active.Name : "Add computed";
            view.AddHint = view.CanAddSubset
                ? "Subsets and computed layers start from " + active.Name + "."
                : active != null ? "Select a dataset or subset layer to add a subset or computed layer from it." : "";
            view.Props = BuildProps(stack, active, warningOf, shuffleUnlocked, referenceStep);
            return view;
        }

        public static Row BuildRow(LayerStack stack, int index, Func<PlotLayer, string> warningOf)
        {
            PlotLayer layer = stack.Layers[index];
            var row = new Row
            {
                Id = layer.Id,
                Index = index,
                Name = (index + 1).ToString(CultureInfo.InvariantCulture) + "  " + layer.Name,
                Active = layer.Id == stack.ActiveLayerId,
                Visible = layer.Visible,
                VisibleText = layer.Visible ? "Shown" : "Hidden",
                CanMoveUp = index > 0,
                CanMoveDown = index < stack.Count - 1,
            };

            var tags = new List<string> { KindText(stack, layer) };
            if (layer.IsPointLayer && layer.Normalised) tags.Add(TagNormalised);
            if (!layer.Visible) tags.Add(TagHidden);
            bool missing = IsSourceMissing(stack, layer);
            if (missing) tags.Add(TagSourceMissing);
            if (layer.LocalUnavailable) tags.Add(TagUnavailable);
            row.Tags = string.Join(" · ", tags);
            row.Greyed = layer.LocalUnavailable || !layer.Visible;
            row.Warning = OneLine(CleanWarning(warningOf?.Invoke(layer)));
            return row;
        }

        static Props BuildProps(LayerStack stack, PlotLayer layer, Func<PlotLayer, string> warningOf,
                                Func<string, bool> shuffleUnlocked, float referenceStep)
        {
            var p = new Props();
            if (layer == null)
                return p;

            p.HasLayer = true;
            p.LayerId = layer.Id;
            p.IsPoint = layer.IsPointLayer;
            p.IsSubset = layer.Kind == LayerKind.Subset;
            p.IsComputed = layer.Kind == LayerKind.Computed;
            p.Title = "Editing: " + layer.Name;
            p.Warning = CleanWarning(warningOf?.Invoke(layer));

            p.OpacityStep = Array.FindIndex(OpacitySteps, s => Mathf.Abs(s - layer.Opacity) < 0.01f);
            p.OpacityText = "Opacity " + Mathf.RoundToInt(layer.Opacity * 100f).ToString(CultureInfo.InvariantCulture) + "%";

            DatasetColumnar rowData = RowDataset(stack, layer);
            p.ColourByColumn = p.IsPoint && layer.ColorColumnIndex >= 0;
            p.ColourColumnText = p.ColourByColumn ? "By " + ColumnName(rowData, layer.ColorColumnIndex) : "By column";
            p.ColourIndex = p.ColourByColumn ? -1 : PaletteIndex(layer.FixedColour);

            if (p.IsPoint)
            {
                p.NormaliseOn = layer.Normalised;
                p.NormaliseText = layer.Normalised ? "Normalise: on" : "Normalise: off";
                p.ShowHideInSource = p.IsSubset;
                p.HideInSourceOn = layer.HiddenInSource;
                p.HideInSourceText = layer.HiddenInSource ? "Hide in source: on" : "Hide in source: off";

                int glyph = Mathf.Clamp(layer.DefaultGlyph, 0, ScatterplotUI.k_GlyphShapeNames.Length - 1);
                p.GlyphText = "Glyph: " + ScatterplotUI.k_GlyphShapeNames[glyph];

                p.TimeMode = (int)layer.TimeBehaviour;
                p.TimeHint = layer.TimeColumnIndex < 0 ? "No time column: pick one in Animate" : "";
                p.EndColumnEnabled = layer.TimeBehaviour == LayerTimeBehaviour.Span;
                p.EndColumnText = "End: " + (layer.EndTimeColumnIndex < 0 ? "none" : ColumnName(rowData, layer.EndTimeColumnIndex));
                p.LabelColumnText = "Labels: " + (layer.LabelColumnIndex < 0 ? "none" : ColumnName(rowData, layer.LabelColumnIndex));

                int index = stack.IndexOf(layer.Id);
                if (index == 0)
                {
                    p.ShuffleText = "Shuffle: always (first layer)";
                    p.ShuffleOn = true;
                }
                else
                {
                    p.ShuffleEnabled = true;
                    p.ShuffleOn = shuffleUnlocked != null && shuffleUnlocked(layer.Id);
                    p.ShuffleText = p.ShuffleOn ? "Shuffle: unlocked" : "Shuffle: locked";
                }
            }
            else
            {
                p.NormaliseText = "Normalise: as source";
                p.ShuffleText = "Shuffle: never";
                p.LineWidthText = "Line " + (layer.LineWidth * 1000f).ToString("0.#", CultureInfo.InvariantCulture);

                ComputedLayerKind kind = layer.ComputedKind;
                p.ShowDriverAxis = kind == ComputedLayerKind.Trendline || kind == ComputedLayerKind.Reference;
                p.DriverAxisChoice = Mathf.Clamp(layer.DriverAxis, -1, 2) + 1;
                p.ShowFlag = kind == ComputedLayerKind.Trendline || kind == ComputedLayerKind.Reference;
                p.FlagOn = layer.Smoothed;
                p.FlagText = kind == ComputedLayerKind.Reference
                    ? (layer.Smoothed ? "Shape: plane" : "Shape: line")
                    : (layer.Smoothed ? "Smoothed: on" : "Smoothed: off");
                p.ShowOrderColumn = kind == ComputedLayerKind.Path || kind == ComputedLayerKind.SmoothedPath;
                p.OrderColumnText = "Order: " + (layer.OrderColumnIndex < 0 ? "time column" : ColumnName(rowData, layer.OrderColumnIndex));
                p.ShowSpan = kind == ComputedLayerKind.SmoothedPath || (kind == ComputedLayerKind.Trendline && layer.Smoothed);
                p.SpanText = "Span " + Mathf.RoundToInt(layer.SmoothSpan * 100f).ToString(CultureInfo.InvariantCulture) + "%";
                p.ShowValue = kind == ComputedLayerKind.Reference;
                p.ValueText = "At " + FormatValue(layer.RefValue, referenceStep);
            }
            return p;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>"Dataset · file", "Subset of Movement", "Trendline of Movement"; the source name is the frozen dataset when the source is gone.</summary>
        public static string KindText(LayerStack stack, PlotLayer layer)
        {
            switch (layer.Kind)
            {
                case LayerKind.Dataset:
                    return "Dataset · " + (string.IsNullOrEmpty(layer.DatasetName) ? "no file" : layer.DatasetName);
                case LayerKind.Subset:
                {
                    PlotLayer source = stack?.Find(layer.SourceLayerId);
                    string of = source != null ? source.Name : layer.DatasetName;
                    string filter = string.IsNullOrEmpty(layer.FilterLabel) ? "" : ": " + layer.FilterLabel;
                    return "Subset of " + of + filter;
                }
                default:
                {
                    PlotLayer source = stack?.Find(layer.SourceLayerId);
                    string of = source != null ? source.Name : layer.DatasetName;
                    return LayerRules.ComputedKindName(layer.ComputedKind) + " of " + of;
                }
            }
        }

        public static bool IsSourceMissing(LayerStack stack, PlotLayer layer) =>
            layer != null && layer.Kind == LayerKind.Computed && stack?.Find(layer.SourceLayerId) == null;

        /// <summary>The dataset whose columns the layer's pickers offer: its own, or a computed layer's source's (its frozen one when the source is gone).</summary>
        public static DatasetColumnar RowDataset(LayerStack stack, PlotLayer layer)
        {
            if (layer == null)
                return null;
            if (layer.Kind != LayerKind.Computed)
                return layer.Dataset;
            PlotLayer source = stack?.Find(layer.SourceLayerId);
            if (source != null)
                return source.Dataset;
            return layer.Dataset != null ? layer.Dataset : DatasetRegistry.Get(layer.DatasetName);
        }

        /// <summary>Drops "source missing" (a tag already says it) and blank lines; lines joined with "\n".</summary>
        public static string CleanWarning(string warning)
        {
            if (string.IsNullOrEmpty(warning))
                return "";
            var parts = new List<string>();
            foreach (string line in warning.Split('\n'))
            {
                var bits = new List<string>();
                foreach (string bit in line.Split(new[] { ", " }, StringSplitOptions.None))
                {
                    string b = bit.Trim();
                    if (b.Length > 0 && b != TagSourceMissing)
                        bits.Add(b);
                }
                if (bits.Count > 0)
                    parts.Add(string.Join(", ", bits));
            }
            return string.Join("\n", parts);
        }

        /// <summary>First line, with "(+n more)" when there are more.</summary>
        public static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            string[] lines = text.Split('\n');
            return lines.Length == 1 ? lines[0] : lines[0] + $" (+{lines.Length - 1} more)";
        }

        public static string ColumnName(DatasetColumnar ds, int column) =>
            ds != null && column >= 0 && column < ds.ColumnCount ? ds.Columns[column].Name : "column " + (column + 1);

        /// <summary>Index of the PlayerProfile.Palette colour equal to c (RGB, after the Color32 round trip), or -1.</summary>
        public static int PaletteIndex(Color32 c)
        {
            Color[] palette = DataViz.Networking.PlayerProfile.Palette;
            for (int i = 0; i < palette.Length; i++)
            {
                Color32 p = palette[i];
                if (p.r == c.r && p.g == c.g && p.b == c.b)
                    return i;
            }
            return -1;
        }

        /// <summary>Step for the reference value: a twentieth of the axis's numeric range, else 1.</summary>
        public static float ReferenceStep(AxisScale scale)
        {
            if (!scale.Mapped || scale.Categorical)
                return 1f;
            float step = (scale.Max - scale.Min) / 20f;
            return float.IsFinite(step) && step > 1e-6f ? step : 1f;
        }

        static string FormatValue(float value, float step)
        {
            int decimals = step >= 1f ? 0 : Mathf.Clamp(Mathf.CeilToInt(-Mathf.Log10(step)), 1, 6);
            return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        /// <summary>Which page shows the layer at index.</summary>
        public static int PageOf(int index) => index < 0 ? 0 : index / RowsPerPage;

        // ------------------------------------------------------------------ picker lists

        /// <summary>Every column of ds; with noneLabel a first entry of value -1.</summary>
        public static List<Option> ColumnOptions(DatasetColumnar ds, string noneLabel)
        {
            var list = new List<Option>();
            if (noneLabel != null)
                list.Add(new Option(noneLabel, -1));
            if (ds == null)
                return list;
            for (int i = 0; i < ds.ColumnCount; i++)
                list.Add(new Option(ds.Columns[i].Name, i));
            return list;
        }

        /// <summary>The subset filter's first step: the source's own filters (value -1), then each column.</summary>
        public static List<Option> SubsetColumnOptions(PlotLayer source)
        {
            List<Option> list = ColumnOptions(source?.Dataset, null);
            list.Insert(0, new Option(source != null && source.Filters.Count > 0 ? "Same filters as " + source.Name : "All rows of " + (source?.Name ?? ""), -1));
            return list;
        }

        /// <summary>
        /// Values to filter a column on: "Any value" (Text "") first, then the column's values in
        /// the order the Filter tab uses (numbers by value), at most max of them.
        /// </summary>
        public static List<Option> FilterValueOptions(DatasetColumnar ds, int column, int max = MaxFilterValues)
        {
            var list = new List<Option> { new Option(AnyValueOption, 0, "") };
            if (ds == null || column < 0 || column >= ds.ColumnCount)
                return list;

            DatasetColumn col = ds.Columns[column];
            var values = new List<string>();
            if (col.UniqueValues != null && col.UniqueValues.Count > 0)
            {
                values.AddRange(col.UniqueValues);
            }
            else if (col.IsNumeric)
            {
                var seen = new HashSet<string>();
                for (int row = 0; row < ds.RowCount && seen.Count <= max; row++)
                {
                    if (ds.HasValue(row, column))
                        seen.Add(ds.GetNumericValue(row, column).ToString());
                }
                values.AddRange(seen);
            }
            values.Sort(CompareValues);
            for (int i = 0; i < values.Count && i < max; i++)
                list.Add(new Option(values[i], i + 1, values[i]));
            return list;
        }

        static int CompareValues(string a, string b)
        {
            if (float.TryParse(a, out float x) && float.TryParse(b, out float y))
                return x.CompareTo(y);
            return string.Compare(a, b, StringComparison.Ordinal);
        }

        public static List<Option> ComputedKindOptions()
        {
            var list = new List<Option>();
            foreach (ComputedLayerKind kind in new[] { ComputedLayerKind.Trendline, ComputedLayerKind.Path, ComputedLayerKind.SmoothedPath,
                                                       ComputedLayerKind.Mean, ComputedLayerKind.Reference })
                list.Add(new Option(LayerRules.ComputedKindName(kind), (int)kind));
            return list;
        }

        public static List<Option> DatasetOptions(IEnumerable<string> files)
        {
            var list = new List<Option>();
            if (files == null)
                return list;
            foreach (string f in files)
                list.Add(new Option(System.IO.Path.GetFileNameWithoutExtension(f), list.Count, f));
            return list;
        }

        /// <summary>Text for the "Editing: …" badge on Map, Animate and Filter; null while the plot has fewer than two layers (AT-01: nothing new on a one-layer plot).</summary>
        public static string BadgeText(LayerStack stack)
        {
            if (stack == null || stack.Count < 2 || stack.Active == null)
                return null;
            PlotLayer active = stack.Active;
            return active.Kind == LayerKind.Computed
                ? "Editing: " + active.Name + " (mapping comes from its source)"
                : "Editing: " + active.Name;
        }

        /// <summary>
        /// Cheap hash of what the view shows but OnLayersChanged does not announce (mapping-side
        /// fields, datasets, warnings): the panel compares it each frame and rebuilds only on change.
        /// </summary>
        public static int Signature(LayerStack stack, Func<PlotLayer, string> warningOf)
        {
            if (stack == null)
                return 0;
            var h = new HashCode();
            h.Add(stack.Version);
            h.Add(stack.Count);
            h.Add(stack.ActiveLayerId);
            foreach (PlotLayer layer in stack.Layers)
            {
                h.Add(layer.Id);
                h.Add(layer.ColorColumnIndex);
                h.Add(layer.TimeColumnIndex);
                h.Add(layer.DatasetName);
                h.Add(layer.LocalUnavailable);
                h.Add(layer.Dataset != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(layer.Dataset) : 0);
                h.Add(layer.Filters.Count > 0 ? layer.Filters[0].Label : null);
                if (warningOf != null)
                    h.Add(warningOf(layer));
            }
            return h.ToHashCode();
        }
    }
}
