using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// Builds one point layer's points into the concatenated buffers (the per-row loop that used to
    /// live in ScatterplotVisualizer.RegeneratePlot). Pure: no manager or visualizer reference.
    ///
    /// Which rows (LFR-03, LFR-11): a Subset draws only the rows passing its filters; rows a subset
    /// of this layer takes with HiddenInSource are left out; a row with no value in a mapped axis
    /// column, in the colour column or in the mapped size column is skipped and counted (no value is
    /// imputed). A Dataset layer keeps its filter-rejected rows, dimmed, as before layers.
    /// Budget (LFR-06): when the drawable rows exceed budgetLeft, 1 of every Stride rows is drawn.
    /// Per point: FixedColour when no colour column (LFR-14), alpha = filter/time rule x Opacity
    /// (LFR-15), glyph fallback DefaultGlyph (LFR-13), time behaviour Follow/Always/Span (LFR-24).
    /// Colour and size scales use ColumnRange, so they come from rows with a value.
    /// </summary>
    public static class LayerPointBuilder
    {
        // A time column whose values are all whole numbers spanning at most this many steps animates
        // step by step (the original behaviour); anything else uses the trailing window.
        const float k_MaxDiscreteTimeSteps = 1000f;
        const float k_WholeNumberEpsilon = 1e-3f;

        static readonly Color[] k_CategoricalPalette =
        {
            Color.red, Color.blue, Color.green, Color.yellow, Color.cyan, Color.magenta,
            new Color(1f, 0.5f, 0f), new Color(0.5f, 0f, 0.5f), new Color(0f, 0.5f, 0.5f),
            new Color(0.7f, 0.2f, 0.2f), new Color(0.2f, 0.7f, 0.2f), new Color(0.2f, 0.2f, 0.7f)
        };

        // Discrete/continuous verdict per (dataset instance, column); weak so unloaded datasets can go.
        static readonly ConditionalWeakTable<DatasetColumnar, Dictionary<int, bool>> s_DiscreteCache = new();

        // Reused by Append (main thread only): the layer's drawable rows before the budget stride.
        static readonly List<int> s_Drawable = new();

        /// <summary>
        /// Appends the layer's points to into. axes places them (LayerBounds.FrameFor); the clock
        /// arguments are the plot's (PlotClock): a layer's time column only fades points while
        /// clockActive. budgetLeft is what the layers before this one left of the plot's budget.
        /// Returns what was drawn, skipped and why (Info.Warning).
        /// </summary>
        public static LayerDrawInfo Append(PlotLayer layer, LayerStack stack, AxisFrame axes, int budgetLeft,
                                           bool clockActive, float clockMin, float clockMax, float clockTime,
                                           in LayerBuildSettings settings, PointBuffers into) =>
            Append(layer, stack, axes, budgetLeft, clockActive, clockMin, clockMax, clockTime, settings, into, null);

        /// <summary>
        /// Append that also records each point's alpha before the time rule and Opacity (filter dim,
        /// or 0 for the glyph preview) in baseAlphaOut, parallel to into, so Retime can redo only
        /// the time fade and opacity later. baseAlphaOut may be null.
        /// </summary>
        public static LayerDrawInfo Append(PlotLayer layer, LayerStack stack, AxisFrame axes, int budgetLeft,
                                           bool clockActive, float clockMin, float clockMax, float clockTime,
                                           in LayerBuildSettings settings, PointBuffers into, List<float> baseAlphaOut)
        {
            var info = new LayerDrawInfo { Stride = 1 };
            DatasetColumnar dataset = layer?.Dataset;
            if (dataset == null || into == null)
                return info;

            info.Rows = dataset.RowCount;
            int xCol = layer.XColumnIndex;
            int yCol = layer.YColumnIndex;
            int zCol = layer.ZColumnIndex;
            int colorCol = layer.ColorColumnIndex;
            float pointSize = layer.PointSize;

            // ---- encodings, resolved once ----
            DatasetColumn colorColumn = dataset.GetColumn(colorCol);
            bool colourByColumn = colorColumn != null && (colorColumn.IsNumeric || colorColumn.IsCategorical);
            float colourMin = 0f, colourMax = 0f;
            if (colorColumn != null && colorColumn.IsNumeric)
                ColumnRange.Get(dataset, colorCol, out colourMin, out colourMax);
            Color fixedColour = layer.FixedColour;
            fixedColour.a = 1f;

            int sizeCol = layer.SizeColumnIndex;
            bool sizeMappedActive = layer.SizeMapped && sizeCol >= 0 && sizeCol < dataset.ColumnCount && dataset.Columns[sizeCol].IsNumeric;
            AxisScale sizeScale = default;
            if (sizeMappedActive && ColumnRange.Get(dataset, sizeCol, out float sizeMin, out float sizeMax))
                sizeScale = AxisScale.Numeric(sizeMin, sizeMax);

            int glyphCol = layer.GlyphColumnIndex;
            bool glyphActive = glyphCol >= 0 && glyphCol < dataset.ColumnCount;
            DatasetColumn glyphColumn = glyphActive ? dataset.Columns[glyphCol] : null;
            List<string> glyphCategoryTable = (glyphActive && glyphColumn.IsCategorical) ? new List<string>(glyphColumn.UniqueValues) : null;

            bool glyphPreviewHideActive = glyphActive && glyphColumn.IsNumeric &&
                                          settings.GlyphPreviewHidden != null && settings.GlyphPreviewHidden.Count > 0 &&
                                          settings.GlyphPreviewLayerId == layer.Id;

            // Categorical axes: this layer's category index → index in the shared union.
            int[] xRemap = CategoryRemap(dataset, xCol, axes.X);
            int[] yRemap = CategoryRemap(dataset, yCol, axes.Y);
            int[] zRemap = CategoryRemap(dataset, zCol, axes.Z);

            // ---- which rows (LFR-03, LFR-11) ----
            List<LayerRows.ResolvedFilter> ownFilters = LayerRows.ResolveFilters<LayerRows.ResolvedFilter>(layer.Filters, dataset);
            List<LayerRows.ResolvedFilter> memberFilters = layer.FiltersExclude && ownFilters.Count > 0 ? ownFilters : null;
            bool dimByFilter = !layer.FiltersExclude && ownFilters.Count > 0;
            List<List<LayerRows.ResolvedFilter>> hiders = HidingSubsets(layer, stack, dataset);
            int[] axisColumns = LayerRows.MappedAxes(layer, dataset);

            // Skip counts per column (index into the dataset's columns), for the warning text.
            int[] skippedPerColumn = null;
            List<int> rows = s_Drawable;
            rows.Clear();
            for (int i = 0; i < dataset.RowCount; i++)
            {
                if (memberFilters != null && !LayerRows.PassesFilters(dataset, i, memberFilters))
                    continue;
                if (hiders != null && TakenByHider(dataset, i, hiders))
                    continue;

                int missing = FirstMissing(dataset, i, axisColumns);
                if (missing >= 0)
                {
                    info.SkippedNoAxisValue++;
                    CountSkip(ref skippedPerColumn, dataset.ColumnCount, missing);
                    continue;
                }
                if (colourByColumn && !dataset.HasValue(i, colorCol))
                {
                    info.SkippedNoEncodingValue++;
                    CountSkip(ref skippedPerColumn, dataset.ColumnCount, colorCol);
                    continue;
                }
                if (sizeMappedActive && !dataset.HasValue(i, sizeCol))
                {
                    info.SkippedNoEncodingValue++;
                    CountSkip(ref skippedPerColumn, dataset.ColumnCount, sizeCol);
                    continue;
                }
                rows.Add(i);
            }

            // ---- budget (LFR-06) ----
            int drawable = rows.Count;
            info.Drawable = drawable;
            int stride = 1;
            string budgetWarning = null;
            if (drawable > 0 && budgetLeft <= 0)
            {
                stride = 0;
                budgetWarning = $"{layer.Name}: not drawn, the point budget is used up";
            }
            else if (drawable > budgetLeft)
            {
                stride = (drawable + budgetLeft - 1) / budgetLeft;   // ceil(drawable / budgetLeft)
            }
            info.Stride = Math.Max(stride, 1);

            // ---- points ----
            TimeTest time = TimeTest.For(layer, dataset, clockActive, clockMin, clockMax, clockTime, settings.TimeTrailFraction);
            float opacity = layer.Opacity;
            Vector3 basePosition = settings.Origin;
            float axisLength = settings.AxisLength;
            int drawn = 0;

            for (int k = 0; stride > 0 && k < drawable; k += stride)
            {
                int i = rows[k];

                float xNorm = AxisValue(dataset, i, xCol, axes.X, xRemap);
                float yNorm = AxisValue(dataset, i, yCol, axes.Y, yRemap);
                float zNorm = AxisValue(dataset, i, zCol, axes.Z, zRemap);
                Vector3 worldPos = basePosition + (new Vector3(xNorm, yNorm, zNorm) * axisLength);

                Color pointColor = fixedColour;
                if (colourByColumn)
                {
                    if (colorColumn.IsNumeric)
                    {
                        float rawVal = dataset.GetNumericValue(i, colorCol);
                        float range = colourMax - colourMin;
                        float norm = 0.5f;
                        if (range > 0.00001f)
                            norm = (rawVal - colourMin) / range;
                        norm = Mathf.Clamp01(norm);
                        pointColor = Color.Lerp(Color.blue, Color.red, norm);
                    }
                    else
                    {
                        int catIdx = dataset.GetCategoryIndex(i, colorCol);
                        pointColor = k_CategoricalPalette[((catIdx % k_CategoricalPalette.Length) + k_CategoricalPalette.Length) % k_CategoricalPalette.Length];
                    }
                }

                float baseAlpha = 1f;
                if (dimByFilter && !LayerRows.PassesFilters(dataset, i, ownFilters))
                    baseAlpha = settings.FilteredOutAlpha;
                if (glyphPreviewHideActive && dataset.HasValue(i, glyphCol) && InAnyInterval(settings.GlyphPreviewHidden, dataset.GetNumericValue(i, glyphCol)))
                    baseAlpha = 0f;

                bool inTimeWindow = time.InWindow(dataset, i);
                pointColor.a = FinalAlpha(baseAlpha, inTimeWindow, settings.TimeOutsideAlpha, opacity);

                into.Sizes.Add(sizeMappedActive ? pointSize * Mathf.Lerp(MultiplayerScatterplotManager.SizeMappedMinScale,
                                                                         MultiplayerScatterplotManager.SizeMappedMaxScale,
                                                                         sizeScale.Normalise(dataset.GetNumericValue(i, sizeCol)))
                                                : pointSize);

                into.Positions.Add(worldPos);
                into.Colors.Add(pointColor);
                into.Glyphs.Add(glyphActive
                    ? RowGlyph(layer, dataset, i, glyphCol, glyphColumn, glyphCategoryTable, layer.DefaultGlyph)
                    : layer.DefaultGlyph);
                into.Rows.Add(i);
                into.InTimeWindow.Add(inTimeWindow);
                baseAlphaOut?.Add(baseAlpha);
                drawn++;
            }

            info.Drawn = drawn;
            info.Warning = BuildWarning(layer, dataset, skippedPerColumn, budgetWarning,
                stride > 1 ? $"{layer.Name}: showing 1 of every {stride} points ({drawn} of {drawable}) to stay within the point budget ({budgetLeft} left)" : null);
            rows.Clear();
            return info;
        }

        /// <summary>
        /// Redoes only the time fade and opacity of a slice Append built earlier (the alpha-only path
        /// during playback): InTimeWindow and the colours' alpha, from baseAlpha (Append's
        /// baseAlphaOut, same indexing as buffers), the layer's current time column, time behaviour,
        /// end-time column and Opacity, and the given clock. Gives the same result as a fresh Append.
        /// </summary>
        public static void Retime(PlotLayer layer, bool clockActive, float clockMin, float clockMax, float clockTime,
                                  in LayerBuildSettings settings, PointBuffers buffers, int start, int count, IReadOnlyList<float> baseAlpha)
        {
            DatasetColumnar dataset = layer?.Dataset;
            if (dataset == null || buffers == null || baseAlpha == null)
                return;

            TimeTest time = TimeTest.For(layer, dataset, clockActive, clockMin, clockMax, clockTime, settings.TimeTrailFraction);
            float opacity = layer.Opacity;
            int end = Math.Min(start + count, buffers.Count);
            for (int p = Math.Max(start, 0); p < end; p++)
            {
                bool inTimeWindow = time.InWindow(dataset, buffers.Rows[p]);
                Color c = buffers.Colors[p];
                c.a = FinalAlpha(baseAlpha[p], inTimeWindow, settings.TimeOutsideAlpha, opacity);
                buffers.Colors[p] = c;
                buffers.InTimeWindow[p] = inTimeWindow;
            }
        }

        // Pre-layer rule (lower of filter and time alpha; the glyph preview's 0 stays 0), then the
        // layer's opacity on top. With Opacity 1 this is exactly the pre-layer alpha.
        static float FinalAlpha(float baseAlpha, bool inTimeWindow, float timeOutsideAlpha, float opacity)
        {
            float a = inTimeWindow ? baseAlpha : Mathf.Min(baseAlpha, timeOutsideAlpha);
            return a * opacity;
        }

        /// <summary>
        /// Whether a row is inside the layer's time window for one clock time (LFR-23, LFR-24).
        /// Follow: discrete columns match the rounded step, continuous ones the trailing window
        /// [hi - W, hi] with W = trail fraction x clock range and hi = max(t, clockMin + W).
        /// Always (and a layer with no numeric time column, or no clock): every row.
        /// Span: start &lt;= t &lt;= end, end = the row's end-time value, +infinity without one.
        /// A row with no time value is outside in Follow and Span.
        /// </summary>
        struct TimeTest
        {
            bool m_Active, m_Span, m_Discrete;
            int m_TimeCol, m_EndCol, m_Step;
            float m_Lo, m_Hi, m_T;

            public static TimeTest For(PlotLayer layer, DatasetColumnar dataset, bool clockActive, float clockMin, float clockMax,
                                       float clockTime, float trailFraction)
            {
                var t = new TimeTest();
                DatasetColumn timeColumn = dataset.GetColumn(layer.TimeColumnIndex);
                if (!clockActive || timeColumn == null || !timeColumn.IsNumeric || layer.TimeBehaviour == LayerTimeBehaviour.Always)
                    return t;

                t.m_Active = true;
                t.m_TimeCol = layer.TimeColumnIndex;
                t.m_T = clockTime;
                if (layer.TimeBehaviour == LayerTimeBehaviour.Span)
                {
                    t.m_Span = true;
                    DatasetColumn endColumn = dataset.GetColumn(layer.EndTimeColumnIndex);
                    t.m_EndCol = endColumn != null && endColumn.IsNumeric ? layer.EndTimeColumnIndex : -1;
                    return t;
                }

                t.m_Discrete = IsDiscreteTimeColumn(dataset, t.m_TimeCol, timeColumn);
                if (t.m_Discrete)
                {
                    t.m_Step = Mathf.RoundToInt(clockTime);
                }
                else
                {
                    float trail = Mathf.Max(0f, trailFraction) * (clockMax - clockMin);
                    t.m_Hi = Mathf.Max(clockTime, clockMin + trail);
                    t.m_Lo = t.m_Hi - trail;
                }
                return t;
            }

            public bool InWindow(DatasetColumnar dataset, int row)
            {
                if (!m_Active)
                    return true;
                if (!dataset.HasValue(row, m_TimeCol))
                    return false;

                float value = dataset.GetNumericValue(row, m_TimeCol);
                if (m_Span)
                {
                    if (value > m_T)
                        return false;
                    return m_EndCol < 0 || !dataset.HasValue(row, m_EndCol) || m_T <= dataset.GetNumericValue(row, m_EndCol);
                }
                return m_Discrete
                    ? Mathf.RoundToInt(value) == m_Step
                    : value >= m_Lo && value <= m_Hi;
            }
        }

        /// <summary>
        /// Mapped size for one row: baseSize x lerp(SizeMappedMinScale, SizeMappedMaxScale, value
        /// normalised by ColumnRange). Rows with no value keep baseSize (Append skips them).
        /// </summary>
        public static float MappedPointSize(DatasetColumnar dataset, int rowIndex, int sizeCol, float baseSize)
        {
            if (!dataset.HasValue(rowIndex, sizeCol))
                return baseSize;

            float norm = ColumnRange.Get(dataset, sizeCol, out float min, out float max)
                ? AxisScale.Numeric(min, max).Normalise(dataset.GetNumericValue(rowIndex, sizeCol))
                : 0f;
            return baseSize * Mathf.Lerp(MultiplayerScatterplotManager.SizeMappedMinScale,
                                         MultiplayerScatterplotManager.SizeMappedMaxScale, norm);
        }

        /// <summary>Glyph of one row on the layer's glyph column (per-value assignment, else range, else defaultGlyph).</summary>
        public static int RowGlyph(PlotLayer layer, DatasetColumnar dataset, int rowIndex, int glyphCol,
                                   DatasetColumn glyphColumn, List<string> categoryTable, int defaultGlyph)
        {
            bool hasValue = dataset.HasValue(rowIndex, glyphCol);
            string label = LayerRows.RowLabel(dataset, rowIndex, glyphCol, glyphColumn, categoryTable);
            float numericValue = (hasValue && glyphColumn.IsNumeric) ? dataset.GetNumericValue(rowIndex, glyphCol) : float.NaN;
            return layer.GetGlyphForRow(label, hasValue, numericValue, defaultGlyph);
        }

        /// <summary>
        /// True when the time column animates step by step: every value is a whole number and the
        /// column range spans at most 1000 steps. Scanned once per (dataset instance, column).
        /// </summary>
        public static bool IsDiscreteTimeColumn(DatasetColumnar dataset, int timeCol, DatasetColumn timeColumn)
        {
            Dictionary<int, bool> perColumn = s_DiscreteCache.GetOrCreateValue(dataset);
            if (perColumn.TryGetValue(timeCol, out bool discrete))
                return discrete;

            discrete = timeColumn != null && ScanIsDiscrete(dataset, timeCol);
            perColumn[timeCol] = discrete;
            return discrete;
        }

        static bool ScanIsDiscrete(DatasetColumnar dataset, int timeCol)
        {
            if (!ColumnRange.Get(dataset, timeCol, out float min, out float max))
                return false;
            float range = max - min;
            if (!(range >= 0f) || range > k_MaxDiscreteTimeSteps) // also rejects NaN
                return false;

            for (int i = 0; i < dataset.RowCount; i++)
            {
                if (!dataset.HasValue(i, timeCol))
                    continue;
                float v = dataset.GetNumericValue(i, timeCol);
                if (Mathf.Abs(v - Mathf.Round(v)) > k_WholeNumberEpsilon)
                    return false;
            }
            return true;
        }

        // Same arithmetic as DatasetColumnar.GetNormalizedValue when the scale is the column's own.
        static float AxisValue(DatasetColumnar dataset, int row, int column, AxisScale scale, int[] remap)
        {
            if (!scale.Mapped)
                return 0f;
            if (scale.Categorical)
            {
                int index = dataset.GetCategoryIndex(row, column);
                return scale.NormaliseCategoryIndex(remap != null && index >= 0 && index < remap.Length ? remap[index] : index);
            }
            return scale.Normalise(dataset.GetNumericValue(row, column));
        }

        static int[] CategoryRemap(DatasetColumnar dataset, int column, AxisScale scale)
        {
            if (!scale.Mapped || !scale.Categorical)
                return null;
            string[] table = dataset.GetCategoryTable(column);
            if (table == null)
                return null;

            var remap = new int[table.Length];
            for (int i = 0; i < table.Length; i++)
                remap[i] = scale.IndexOfCategory(table[i] ?? "");
            return remap;
        }

        static int FirstMissing(DatasetColumnar dataset, int row, int[] columns)
        {
            for (int c = 0; c < columns.Length; c++)
            {
                if (!dataset.HasValue(row, columns[c]))
                    return columns[c];
            }
            return -1;
        }

        static void CountSkip(ref int[] perColumn, int columnCount, int column)
        {
            perColumn ??= new int[columnCount];
            perColumn[column]++;
        }

        // Same rule as LayerRows.Drawable (whose helper is private): subsets of this layer with
        // HiddenInSource on the same dataset instance take the rows passing their filters.
        static List<List<LayerRows.ResolvedFilter>> HidingSubsets(PlotLayer layer, LayerStack stack, DatasetColumnar ds)
        {
            if (stack == null || layer == null || !layer.IsPointLayer)
                return null;

            List<List<LayerRows.ResolvedFilter>> result = null;
            foreach (PlotLayer other in stack.Layers)
            {
                if (other == layer || other.Kind != LayerKind.Subset || !other.HiddenInSource ||
                    other.SourceLayerId != layer.Id || !ReferenceEquals(other.Dataset, ds))
                    continue;
                result ??= new List<List<LayerRows.ResolvedFilter>>();
                result.Add(LayerRows.ResolveFilters(other, ds));
            }
            return result;
        }

        static bool TakenByHider(DatasetColumnar ds, int row, List<List<LayerRows.ResolvedFilter>> hiders)
        {
            foreach (List<LayerRows.ResolvedFilter> filters in hiders)
            {
                if (LayerRows.PassesFilters(ds, row, filters))
                    return true;
            }
            return false;
        }

        // One sentence per reason, one per line: skipped rows per column (LFR-11), then the budget (LFR-06).
        static string BuildWarning(PlotLayer layer, DatasetColumnar dataset, int[] skippedPerColumn, string budgetUsedUp, string budgetStride)
        {
            List<string> lines = null;
            if (skippedPerColumn != null)
            {
                for (int c = 0; c < skippedPerColumn.Length; c++)
                {
                    int n = skippedPerColumn[c];
                    if (n == 0)
                        continue;
                    lines ??= new List<string>();
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1} row{2} no value in {3} and {4} not drawn",
                        layer.Name, n, n == 1 ? " has" : "s have", dataset.Columns[c].Name, n == 1 ? "is" : "are"));
                }
            }
            if (budgetUsedUp != null)
                (lines ??= new List<string>()).Add(budgetUsedUp);
            if (budgetStride != null)
                (lines ??= new List<string>()).Add(budgetStride);
            return lines == null ? null : string.Join("\n", lines);
        }

        static bool InAnyInterval(IReadOnlyList<Vector2> intervals, float value)
        {
            for (int i = 0; i < intervals.Count; i++)
            {
                if (value >= intervals[i].x && value <= intervals[i].y)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The whole plot's points, built layer by layer in stack order into one concatenated buffer
    /// within the shared budget (B7, B11), and kept for the next build. When nothing but the clock,
    /// a layer's time column / time behaviour / end-time column or its Opacity changed since the
    /// last build, Build only rewrites alphas and the time-window mask (the alpha-only path, so
    /// playback with several layers does not redo positions, colours, glyphs and filters every
    /// frame). The visualizer owns one; tests can drive one with a plain LayerStack.
    /// </summary>
    public sealed class PlotPointCache
    {
        sealed class Slice
        {
            public AxisFrame Axes;
            public int Start, Count;
            public LayerDrawInfo Info;
        }

        readonly List<float> m_BaseAlpha = new();
        readonly List<Slice> m_Slices = new();
        int m_Signature;
        bool m_Valid;

        /// <summary>The concatenated buffers of the last Build. Rewritten in place by the next one.</summary>
        public PointBuffers Buffers { get; } = new PointBuffers();

        /// <summary>True when the last Build took the alpha-only path.</summary>
        public bool LastBuildRetimed { get; private set; }

        /// <summary>Forces the next Build to rebuild everything.</summary>
        public void Invalidate() => m_Valid = false;

        /// <summary>
        /// Fills frame.Layers (one LayerFrame per layer, stack order) and frame.TotalPoints, and
        /// Buffers. The caller sets frame.Bounds, Budget and the clock fields first. Hidden,
        /// computed, LocalUnavailable and dataset-less layers get an empty slice.
        /// </summary>
        public void Build(LayerStack stack, PlotFrame frame, in LayerBuildSettings settings)
        {
            frame.Layers.Clear();
            int signature = Signature(stack, frame, settings);
            int layerCount = stack?.Count ?? 0;
            LastBuildRetimed = m_Valid && signature == m_Signature && m_Slices.Count == layerCount;

            if (!LastBuildRetimed)
            {
                Buffers.Clear();
                m_BaseAlpha.Clear();
                m_Slices.Clear();
            }

            for (int i = 0; i < layerCount; i++)
            {
                PlotLayer layer = stack.Layers[i];
                var layerFrame = new LayerFrame
                {
                    Layer = layer,
                    Dataset = layer.Kind == LayerKind.Computed ? (stack.Find(layer.SourceLayerId) ?? layer).Dataset : layer.Dataset,
                };
                frame.Layers.Add(layerFrame);

                Slice slice;
                if (LastBuildRetimed)
                {
                    slice = m_Slices[i];
                    if (slice.Count > 0)
                        LayerPointBuilder.Retime(layer, frame.ClockActive, frame.ClockMin, frame.ClockMax, frame.ClockTime,
                            settings, Buffers, slice.Start, slice.Count, m_BaseAlpha);
                }
                else
                {
                    slice = new Slice { Axes = LayerBounds.FrameInStack(layer, stack, frame.Bounds), Start = Buffers.Count };
                    if (Draws(layer))
                    {
                        slice.Info = LayerPointBuilder.Append(layer, stack, slice.Axes, frame.Budget - Buffers.Count,
                            frame.ClockActive, frame.ClockMin, frame.ClockMax, frame.ClockTime, settings, Buffers, m_BaseAlpha);
                    }
                    slice.Count = Buffers.Count - slice.Start;
                    m_Slices.Add(slice);
                }

                layerFrame.Axes = slice.Axes;
                layerFrame.BufferStart = slice.Count > 0 ? slice.Start : 0;
                layerFrame.BufferCount = slice.Count;
                layerFrame.Info = slice.Info;
            }

            frame.TotalPoints = Buffers.Count;
            m_Signature = signature;
            m_Valid = true;
        }

        /// <summary>Visible, available point layer with a dataset.</summary>
        public static bool Draws(PlotLayer layer) =>
            layer != null && layer.Visible && layer.IsPointLayer && !layer.LocalUnavailable && layer.Dataset != null;

        // Everything a full build depends on except the clock and each layer's time column, time
        // behaviour, end-time column and Opacity (those only change alpha / the time mask: Retime).
        static int Signature(LayerStack stack, PlotFrame frame, in LayerBuildSettings settings)
        {
            var h = new HashCode();
            h.Add(ColumnRange.ValidRowsOnly);
            h.Add(frame.Budget);
            AddScale(ref h, frame.Bounds.X);
            AddScale(ref h, frame.Bounds.Y);
            AddScale(ref h, frame.Bounds.Z);
            h.Add(settings.Origin);
            h.Add(settings.AxisLength);
            h.Add(settings.FilteredOutAlpha);
            h.Add(settings.GlyphPreviewLayerId ?? "");
            int previews = settings.GlyphPreviewHidden?.Count ?? 0;
            h.Add(previews);
            for (int i = 0; i < previews; i++)
                h.Add(settings.GlyphPreviewHidden[i]);

            if (stack == null)
                return h.ToHashCode();

            h.Add(stack.Count);
            foreach (PlotLayer layer in stack.Layers)
            {
                h.Add(layer.Id);
                h.Add(layer.Name);
                h.Add(layer.Kind);
                h.Add(layer.Visible);
                h.Add(layer.LocalUnavailable);
                h.Add(layer.Dataset != null ? RuntimeHelpers.GetHashCode(layer.Dataset) : 0);
                h.Add(layer.SourceLayerId);
                h.Add(layer.XColumnIndex);
                h.Add(layer.YColumnIndex);
                h.Add(layer.ZColumnIndex);
                h.Add(layer.Normalised);
                h.Add(layer.ColorColumnIndex);
                h.Add(layer.FixedColour.r | (layer.FixedColour.g << 8) | (layer.FixedColour.b << 16) | (layer.FixedColour.a << 24));
                h.Add(layer.PointSize);
                h.Add(layer.SizeMapped);
                h.Add(layer.SizeColumnIndex);
                h.Add(layer.DefaultGlyph);
                h.Add(layer.GlyphColumnIndex);
                h.Add(layer.HiddenInSource);
                if (layer.GlyphAssignments != null)
                {
                    h.Add(layer.GlyphAssignments.Count);
                    foreach (MultiplayerScatterplotManager.GlyphAssignment a in layer.GlyphAssignments)
                    {
                        if (a == null) continue;
                        h.Add(a.Label ?? "");
                        h.Add(a.GlyphIndex);
                    }
                }
                if (layer.GlyphRanges != null)
                {
                    h.Add(layer.GlyphRanges.Count);
                    foreach (MultiplayerScatterplotManager.GlyphRange r in layer.GlyphRanges)
                    {
                        if (r == null) continue;
                        h.Add(r.glyphIndex);
                        h.Add(r.min);
                        h.Add(r.max);
                    }
                }
                if (layer.Filters != null)
                {
                    h.Add(layer.Filters.Count);
                    foreach (MultiplayerScatterplotManager.FilterEntry f in layer.Filters)
                    {
                        if (f == null) continue;
                        h.Add(f.ColumnIndex);
                        h.Add(f.Label ?? "");
                        if (f.Ranges == null) continue;
                        h.Add(f.Ranges.Count);
                        foreach (MultiplayerScatterplotManager.FilterRange r in f.Ranges)
                        {
                            h.Add(r.min);
                            h.Add(r.max);
                        }
                    }
                }
            }
            return h.ToHashCode();
        }

        static void AddScale(ref HashCode h, AxisScale s)
        {
            h.Add(s.Mapped);
            h.Add(s.Categorical);
            h.Add(s.Min);
            h.Add(s.Max);
            h.Add(s.Categories != null ? RuntimeHelpers.GetHashCode(s.Categories) : 0);
            h.Add(s.Categories?.Length ?? 0);
        }
    }
}
