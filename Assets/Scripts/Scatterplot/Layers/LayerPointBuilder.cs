using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// Builds one point layer's points into the concatenated buffers (the per-row loop that used to
    /// live in ScatterplotVisualizer.RegeneratePlot). Pure: no manager or visualizer reference.
    ///
    /// WP1 legacy pinning: every dataset row is drawn (no LFR-11 skips, no subset exclusion, no
    /// budget stride) and the colour fallback, alpha and glyph fallback are exactly today's (cyan,
    /// filter/time alpha only, glyph 0); only the Follow time behaviour exists. With the WP1
    /// ColumnRange pinning this reproduces the single-dataset output bit for bit. WP2 owns the rest.
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

        /// <summary>
        /// Appends the layer's points to into. axes places them (LayerBounds.FrameFor); the clock
        /// arguments are the plot's (PlotClock): a layer's time column only fades points while
        /// clockActive. Returns what was drawn.
        /// </summary>
        public static LayerDrawInfo Append(PlotLayer layer, LayerStack stack, AxisFrame axes, int budgetLeft,
                                           bool clockActive, float clockMin, float clockMax, float clockTime,
                                           in LayerBuildSettings settings, PointBuffers into)
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
            int timeCol = layer.TimeColumnIndex;
            float pointSize = layer.PointSize;

            DatasetColumn colorColumn = dataset.GetColumn(colorCol);
            DatasetColumn timeColumn = dataset.GetColumn(timeCol);

            // Categorical axes: this layer's category index → index in the shared union.
            int[] xRemap = CategoryRemap(dataset, xCol, axes.X);
            int[] yRemap = CategoryRemap(dataset, yCol, axes.Y);
            int[] zRemap = CategoryRemap(dataset, zCol, axes.Z);

            // Time window (Follow): rows outside stay in the buffer, faded and not hoverable.
            bool timeActive = clockActive && timeColumn != null && timeColumn.IsNumeric;
            bool timeDiscrete = false;
            int currentTimeStep = 0;
            float timeWindowLo = 0f, timeWindowHi = 0f;
            if (timeActive)
            {
                timeDiscrete = IsDiscreteTimeColumn(dataset, timeCol, timeColumn);
                if (timeDiscrete)
                {
                    currentTimeStep = Mathf.RoundToInt(clockTime);
                }
                else
                {
                    float trail = Mathf.Max(0f, settings.TimeTrailFraction) * (clockMax - clockMin);
                    timeWindowHi = Mathf.Max(clockTime, clockMin + trail);
                    timeWindowLo = timeWindowHi - trail;
                }
            }

            List<LayerRows.ResolvedFilter> activeFilters = LayerRows.ResolveFilters<LayerRows.ResolvedFilter>(layer.Filters, dataset);
            bool filterActive = activeFilters.Count > 0;

            int sizeCol = layer.SizeColumnIndex;
            bool sizeMappedActive = layer.SizeMapped && sizeCol >= 0 && sizeCol < dataset.ColumnCount && dataset.Columns[sizeCol].IsNumeric;

            int glyphCol = layer.GlyphColumnIndex;
            bool glyphActive = glyphCol >= 0 && glyphCol < dataset.ColumnCount;
            DatasetColumn glyphColumn = glyphActive ? dataset.Columns[glyphCol] : null;
            List<string> glyphCategoryTable = (glyphActive && glyphColumn.IsCategorical) ? new List<string>(glyphColumn.UniqueValues) : null;

            bool glyphPreviewHideActive = glyphActive && glyphColumn.IsNumeric &&
                                          settings.GlyphPreviewHidden != null && settings.GlyphPreviewHidden.Count > 0 &&
                                          settings.GlyphPreviewLayerId == layer.Id;

            Vector3 basePosition = settings.Origin;
            float axisLength = settings.AxisLength;

            for (int i = 0; i < dataset.RowCount; i++)
            {
                bool inTimeWindow = true;
                if (timeActive)
                {
                    if (!dataset.HasValue(i, timeCol))
                    {
                        inTimeWindow = false;
                    }
                    else
                    {
                        float rawTimeValue = dataset.GetNumericValue(i, timeCol);
                        inTimeWindow = timeDiscrete
                            ? Mathf.RoundToInt(rawTimeValue) == currentTimeStep
                            : rawTimeValue >= timeWindowLo && rawTimeValue <= timeWindowHi;
                    }
                }

                float xNorm = AxisValue(dataset, i, xCol, axes.X, xRemap);
                float yNorm = AxisValue(dataset, i, yCol, axes.Y, yRemap);
                float zNorm = AxisValue(dataset, i, zCol, axes.Z, zRemap);
                Vector3 worldPos = basePosition + (new Vector3(xNorm, yNorm, zNorm) * axisLength);

                Color pointColor = Color.cyan;
                if (colorColumn != null)
                {
                    if (colorColumn.IsNumeric)
                    {
                        float rawVal = dataset.GetNumericValue(i, colorCol);
                        float range = colorColumn.MaxValue - colorColumn.MinValue;
                        float norm = 0.5f;
                        if (range > 0.00001f)
                            norm = (rawVal - colorColumn.MinValue) / range;
                        norm = Mathf.Clamp01(norm);
                        pointColor = Color.Lerp(Color.blue, Color.red, norm);
                    }
                    else if (colorColumn.IsCategorical)
                    {
                        int catIdx = dataset.GetCategoryIndex(i, colorCol);
                        pointColor = k_CategoricalPalette[catIdx % k_CategoricalPalette.Length];
                    }
                }

                if (filterActive)
                {
                    bool matches = LayerRows.PassesFilters(dataset, i, activeFilters);
                    pointColor.a = matches ? 1f : settings.FilteredOutAlpha;
                }

                if (!inTimeWindow)
                    pointColor.a = Mathf.Min(pointColor.a, settings.TimeOutsideAlpha);

                if (glyphPreviewHideActive && dataset.HasValue(i, glyphCol) &&
                    InAnyInterval(settings.GlyphPreviewHidden, dataset.GetNumericValue(i, glyphCol)))
                {
                    pointColor.a = 0f;
                }

                into.Sizes.Add(sizeMappedActive ? MappedPointSize(dataset, i, sizeCol, pointSize) : pointSize);

                int glyphIndex = 0;
                if (glyphActive)
                    glyphIndex = RowGlyph(layer, dataset, i, glyphCol, glyphColumn, glyphCategoryTable, 0);

                into.Positions.Add(worldPos);
                into.Colors.Add(pointColor);
                into.Glyphs.Add(glyphIndex);
                into.Rows.Add(i);
                into.InTimeWindow.Add(inTimeWindow);
            }

            info.Drawable = dataset.RowCount;
            info.Drawn = dataset.RowCount;
            return info;
        }

        /// <summary>
        /// Mapped size for one row: baseSize x lerp(SizeMappedMinScale, SizeMappedMaxScale, normalised
        /// value). Rows with no value keep baseSize.
        /// </summary>
        public static float MappedPointSize(DatasetColumnar dataset, int rowIndex, int sizeCol, float baseSize)
        {
            if (!dataset.HasValue(rowIndex, sizeCol))
                return baseSize;

            float norm = dataset.GetNormalizedValue(rowIndex, sizeCol);
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

            discrete = ScanIsDiscrete(dataset, timeCol, timeColumn);
            perColumn[timeCol] = discrete;
            return discrete;
        }

        static bool ScanIsDiscrete(DatasetColumnar dataset, int timeCol, DatasetColumn timeColumn)
        {
            float range = timeColumn.MaxValue - timeColumn.MinValue;
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
}
