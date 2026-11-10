using System.Collections.Generic;

namespace DataViz
{
    /// <summary>
    /// Shared axis bounds (LFR-08) and per-layer axis frames (LFR-10), plus axis titles (LFR-07).
    /// Bounds depend only on the stack's structure, mappings, Normalised flags and datasets, never on
    /// visibility, filters, encodings or time (LFR-09).
    /// </summary>
    public static class LayerBounds
    {
        /// <summary>
        /// Per axis, over the point layers that are aligned (not Normalised), not LocalUnavailable,
        /// have a dataset and map an existing numeric or categorical column there (hidden layers
        /// included): numeric → smallest min .. largest max of ColumnRange.Get; categorical → the
        /// ordered union of the category tables (stack order, first seen). The first contributing
        /// column decides the kind; a column of the other kind is ignored (LayerRules refuses that
        /// mix). An axis no layer contributes to is not Mapped.
        /// </summary>
        public static AxisBounds Compute(IReadOnlyList<PlotLayer> layers)
        {
            var bounds = new AxisBounds();
            for (int axis = 0; axis < 3; axis++)
                bounds.Set(axis, ComputeAxis(layers, axis));
            return bounds;
        }

        static AxisScale ComputeAxis(IReadOnlyList<PlotLayer> layers, int axis)
        {
            bool any = false, categorical = false;
            float min = 0f, max = 0f;
            List<string> categories = null;
            HashSet<string> seen = null;

            if (layers != null)
            {
                foreach (PlotLayer layer in layers)
                {
                    if (layer == null || !layer.IsPointLayer || layer.Normalised || layer.LocalUnavailable || layer.Dataset == null)
                        continue;

                    int columnIndex = LayerRules.AxisColumn(layer, axis);
                    DatasetColumn column = layer.Dataset.GetColumn(columnIndex);
                    if (column == null || (!column.IsNumeric && !column.IsCategorical))
                        continue;

                    if (!any)
                    {
                        categorical = column.IsCategorical;
                        if (categorical)
                        {
                            categories = new List<string>();
                            seen = new HashSet<string>();
                        }
                    }
                    else if (column.IsCategorical != categorical)
                    {
                        continue;
                    }

                    if (categorical)
                    {
                        string[] table = layer.Dataset.GetCategoryTable(columnIndex);
                        if (table != null)
                        {
                            foreach (string label in table)
                            {
                                if (seen.Add(label ?? ""))
                                    categories.Add(label ?? "");
                            }
                        }
                        any = true;
                    }
                    else if (ColumnRange.Get(layer.Dataset, columnIndex, out float lo, out float hi))
                    {
                        if (!any || lo < min) min = lo;
                        if (!any || hi > max) max = hi;
                        any = true;
                    }
                }
            }

            if (!any)
                return new AxisScale();
            return categorical ? AxisScale.Category(categories.ToArray()) : AxisScale.Numeric(min, max);
        }

        /// <summary>
        /// The scales one point layer is placed with. Aligned: per axis the shared scale, provided
        /// the layer maps a column of the same kind there (else not Mapped). Normalised: its own
        /// ColumnRange per axis (its own category table for a categorical column); for a Subset the
        /// range over its member rows. dataset null means layer.Dataset. A computed layer is read by
        /// its own X/Y/Z/Normalised fields, which are only meaningful once frozen; use FrameInStack to
        /// get a computed layer's source frame.
        /// </summary>
        public static AxisFrame FrameFor(PlotLayer layer, DatasetColumnar dataset, AxisBounds shared)
        {
            var frame = new AxisFrame();
            if (layer == null)
                return frame;

            DatasetColumnar ds = dataset ?? layer.Dataset;
            if (ds == null)
                return frame;

            List<int> members = null;
            if (layer.Normalised && layer.Kind == LayerKind.Subset)
            {
                members = new List<int>();
                LayerRows.FilterPassing(layer, null, members);
            }

            for (int axis = 0; axis < 3; axis++)
            {
                int columnIndex = LayerRules.AxisColumn(layer, axis);
                DatasetColumn column = ds.GetColumn(columnIndex);
                if (column == null || (!column.IsNumeric && !column.IsCategorical))
                    continue;

                if (!layer.Normalised)
                {
                    AxisScale s = shared.Get(axis);
                    if (s.Mapped && s.Categorical == column.IsCategorical)
                        frame.Set(axis, s);
                    continue;
                }

                if (column.IsCategorical)
                {
                    frame.Set(axis, AxisScale.Category(ds.GetCategoryTable(columnIndex)));
                }
                else
                {
                    float lo, hi;
                    bool ok = members != null
                        ? ColumnRange.GetOverRows(ds, columnIndex, members, out lo, out hi)
                        : ColumnRange.Get(ds, columnIndex, out lo, out hi);
                    if (ok)
                        frame.Set(axis, AxisScale.Numeric(lo, hi));
                }
            }
            return frame;
        }

        /// <summary>
        /// FrameFor for a layer as drawn in a stack: a computed layer gets its source's frame (its
        /// own frozen fields when the source is gone); every other layer its own.
        /// </summary>
        public static AxisFrame FrameInStack(PlotLayer layer, LayerStack stack, AxisBounds shared)
        {
            if (layer != null && layer.Kind == LayerKind.Computed)
            {
                PlotLayer source = stack?.Find(layer.SourceLayerId);
                if (source != null)
                    return FrameFor(source, source.Dataset, shared);
            }
            return layer == null ? new AxisFrame() : FrameFor(layer, layer.Dataset, shared);
        }

        /// <summary>
        /// LFR-07 axis title, one string per line. When every visible point layer that maps a column
        /// there maps the same column name and none is Normalised: one line, the column name. Otherwise
        /// one line per such layer, "Layer name: column", with " (normalised)" where it applies.
        /// Empty when no visible layer maps the axis.
        /// </summary>
        public static List<string> AxisTitleLines(IReadOnlyList<PlotLayer> layers, int axis)
        {
            var lines = new List<string>();
            if (layers == null)
                return lines;

            var contributing = new List<(PlotLayer layer, string column)>();
            foreach (PlotLayer layer in layers)
            {
                if (layer == null || !layer.Visible || !layer.IsPointLayer || layer.Dataset == null)
                    continue;
                DatasetColumn column = layer.Dataset.GetColumn(LayerRules.AxisColumn(layer, axis));
                if (column != null)
                    contributing.Add((layer, column.Name ?? ""));
            }

            if (contributing.Count == 0)
                return lines;

            bool same = true;
            foreach (var c in contributing)
            {
                if (c.layer.Normalised || c.column != contributing[0].column)
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                lines.Add(contributing[0].column);
                return lines;
            }

            foreach (var c in contributing)
                lines.Add(c.layer.Name + ": " + c.column + (c.layer.Normalised ? " (normalised)" : ""));
            return lines;
        }
    }
}
