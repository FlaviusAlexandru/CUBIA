using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// Turns a computed layer into a shape in the unit cube (LFR-04, 20, 21, 22). Input: the source
    /// layer's LayerRows.FilterPassing rows (after its filters, before time and down-sampling), so
    /// a computed layer and the points always agree on membership. Once the source is removed the
    /// layer's own frozen fields are the row source and the dataset comes from DatasetRegistry when
    /// the layer does not hold it. Pure apart from that registry read; caching is the caller's job,
    /// keyed on Signature, so scrubbing never recalculates.
    /// </summary>
    public static class ComputedLayerBuilder
    {
        public const string StatusSourceMissing = "source missing";
        public const string StatusNotEnoughPoints = "not enough points";
        public const string StatusOutsideRange = "outside the axis range";
        public const string StatusNoData = "data not loaded";

        /// <summary>Kind == Computed and no layer in the stack has its SourceLayerId (LFR-22).</summary>
        public static bool IsSourceMissing(PlotLayer layer, LayerStack stack) =>
            layer != null && layer.Kind == LayerKind.Computed && stack?.Find(layer.SourceLayerId) == null;

        /// <summary>
        /// The shape of a computed layer in the unit cube, the frame it was mapped with, and a status:
        /// "" when all is well, else one or two of the Status constants joined by ", " (for example
        /// "source missing, not enough points"). Never throws on missing data: the shape is invalid.
        /// </summary>
        public static ComputedShape Build(PlotLayer computed, LayerStack stack, AxisBounds bounds, out AxisFrame frame, out string status)
        {
            frame = new AxisFrame();
            status = "";
            if (computed == null || computed.Kind != LayerKind.Computed)
                return default;

            bool missing = ResolveRowSource(computed, stack, out PlotLayer rowLayer, out DatasetColumnar ds);
            if (rowLayer == null || ds == null || rowLayer.LocalUnavailable)
            {
                status = Join(missing ? StatusSourceMissing : "", StatusNoData);
                return default;
            }

            // FilterPassing and FrameFor read layer.Dataset; a frozen layer whose dataset came from
            // the registry is read through a copy so the stored layer is never changed here.
            if (!ReferenceEquals(rowLayer.Dataset, ds))
            {
                rowLayer = rowLayer.Clone(rowLayer.Id);
                rowLayer.Dataset = ds;
            }

            frame = FrameOf(rowLayer, ds, bounds);
            int driver = DriverAxis(computed, rowLayer);
            ComputedShape shape;

            if (computed.ComputedKind == ComputedLayerKind.Reference)
            {
                shape = ComputedLayerMath.Reference(frame.Get(driver), driver, computed.RefValue, computed.Smoothed);
                status = Join(missing ? StatusSourceMissing : "", shape.Valid ? "" : StatusOutsideRange);
                return shape; // already in the unit cube
            }

            var rows = new List<int>();
            LayerRows.FilterPassing(rowLayer, stack, rows);

            // ASR-04: an axis on Limits (R scale limits) drops the rows outside before the maths;
            // Zoom (R coord_cartesian) keeps them. Either way the result is clipped at the walls.
            bool clips = frame.X.Clip != AxisClip.None || frame.Y.Clip != AxisClip.None || frame.Z.Clip != AxisClip.None;
            if (clips)
            {
                int xc = rowLayer.XColumnIndex, yc = rowLayer.YColumnIndex, zc = rowLayer.ZColumnIndex;
                AxisFrame f = frame;
                rows.RemoveAll(r => Dropped(ds, r, xc, f.X) || Dropped(ds, r, yc, f.Y) || Dropped(ds, r, zc, f.Z));
            }
            List<Vector3> points = Points(rowLayer, ds, frame, rows);

            switch (computed.ComputedKind)
            {
                case ComputedLayerKind.Trendline:
                    shape = ComputedLayerMath.Trendline(points, driver, computed.Smoothed, computed.SmoothSpan);
                    break;
                case ComputedLayerKind.Path:
                    shape = ComputedLayerMath.Path(points, OrderValues(computed, rowLayer, ds, rows));
                    break;
                case ComputedLayerKind.SmoothedPath:
                    shape = ComputedLayerMath.SmoothedPath(points, OrderValues(computed, rowLayer, ds, rows), computed.SmoothSpan);
                    break;
                case ComputedLayerKind.Mean:
                    shape = ComputedLayerMath.Mean(points);
                    break;
                default:
                    shape = default;
                    break;
            }

            status = Join(missing ? StatusSourceMissing : "", shape.Valid ? "" : StatusNotEnoughPoints);
            if (!clips)
                return ToCube(shape, frame);

            ComputedShape clipped = ClipToCube(shape, frame);
            if (shape.Valid && !clipped.Valid)
                status = Join(missing ? StatusSourceMissing : "", StatusOutsideRange);
            return clipped;
        }

        /// <summary>
        /// Hash of everything Build depends on: the row source's LayerRows.InputSignature and dataset
        /// instance, the computed parameters (with the driver axis and order column resolved), the
        /// source's time column, alignment and availability, whether the source is missing, and the
        /// shared bounds. Equal signature ⇒ Build gives the same result. Not the clock, visibility,
        /// colour, opacity or line width: those never need a recalculation.
        /// </summary>
        public static int Signature(PlotLayer computed, LayerStack stack, AxisBounds bounds)
        {
            if (computed == null)
                return 0;

            bool missing = ResolveRowSource(computed, stack, out PlotLayer rowLayer, out DatasetColumnar ds);
            var h = new HashCode();
            h.Add(computed.ComputedKind);
            h.Add(computed.Smoothed);
            h.Add(computed.RefValue);
            h.Add(computed.SmoothSpan);
            h.Add(computed.OrderColumnIndex);
            h.Add(missing);
            if (rowLayer != null)
            {
                h.Add(LayerRows.InputSignature(rowLayer, stack));
                h.Add(ds != null ? RuntimeHelpers.GetHashCode(ds) : 0);
                h.Add(DriverAxis(computed, rowLayer));
                h.Add(rowLayer.TimeColumnIndex);
                h.Add(rowLayer.Normalised);
                h.Add(rowLayer.LocalUnavailable);
            }
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = bounds.Get(axis);
                h.Add(s.Mapped);
                h.Add(s.Categorical);
                h.Add(s.Min);
                h.Add(s.Max);
                h.Add(s.Clip);
                h.Add(s.ClipMin);
                h.Add(s.ClipMax);
                if (s.Categories != null)
                {
                    h.Add(s.Categories.Length);
                    foreach (string c in s.Categories)
                        h.Add(c);
                }
            }
            h.Add(bounds.Settings?.Hash() ?? 0); // own-scale sources take padding and Reverse from it
            return h.ToHashCode();
        }

        /// <summary>
        /// Where the rows come from: the source layer while it exists, else the computed layer's own
        /// frozen fields, with the dataset it holds or DatasetRegistry.Get(DatasetName). Returns
        /// whether the source is missing.
        /// </summary>
        static bool ResolveRowSource(PlotLayer computed, LayerStack stack, out PlotLayer rowLayer, out DatasetColumnar ds)
        {
            PlotLayer source = stack?.Find(computed.SourceLayerId);
            if (source != null)
            {
                // A computed layer cannot be a source (LayerStack refuses it); treat one as "no data".
                rowLayer = source.IsPointLayer ? source : null;
                ds = rowLayer?.Dataset;
                return false;
            }

            rowLayer = computed;
            ds = computed.Dataset;
            // A client that lacks the file marks the layer unavailable; don't hit the disk every frame.
            if (ds == null && !computed.LocalUnavailable && !string.IsNullOrEmpty(computed.DatasetName))
                ds = DatasetRegistry.Get(computed.DatasetName);
            return true;
        }

        /// <summary>DriverAxis when set (0..2); else Reference: Y; Trendline: the axis holding the source's time column, else X (LayerRules.DefaultDriverAxis).</summary>
        static int DriverAxis(PlotLayer computed, PlotLayer rowLayer)
        {
            if (computed.DriverAxis >= 0 && computed.DriverAxis <= 2)
                return computed.DriverAxis;
            return computed.ComputedKind == ComputedLayerKind.Reference ? 1 : LayerRules.DefaultDriverAxis(rowLayer);
        }

        /// <summary>
        /// LayerBounds.FrameFor of the row source. An axis the shared bounds no longer cover (a frozen
        /// layer whose dataset no point layer uses any more) falls back to the column's own range,
        /// so the line keeps drawing where the points were.
        /// </summary>
        static AxisFrame FrameOf(PlotLayer rowLayer, DatasetColumnar ds, AxisBounds bounds)
        {
            AxisFrame frame = LayerBounds.FrameFor(rowLayer, ds, bounds);
            for (int axis = 0; axis < 3; axis++)
            {
                if (frame.Get(axis).Mapped)
                    continue;
                int columnIndex = LayerRules.AxisColumn(rowLayer, axis);
                DatasetColumn column = ds.GetColumn(columnIndex);
                if (column == null)
                    continue;
                if (column.IsCategorical)
                    frame.Set(axis, AxisScale.Category(ds.GetCategoryTable(columnIndex)));
                else if (column.IsNumeric && ColumnRange.Get(ds, columnIndex, out float lo, out float hi))
                    frame.Set(axis, AxisScale.Numeric(lo, hi));
            }
            return frame;
        }

        /// <summary>Each row's X/Y/Z in data space: numeric value, or the union category index on a categorical axis; 0 on an unmapped axis.</summary>
        static List<Vector3> Points(PlotLayer layer, DatasetColumnar ds, AxisFrame frame, List<int> rows)
        {
            var columns = new int[3];
            var categoryMaps = new int[3][];
            for (int axis = 0; axis < 3; axis++)
            {
                columns[axis] = -1;
                int columnIndex = LayerRules.AxisColumn(layer, axis);
                DatasetColumn column = ds.GetColumn(columnIndex);
                if (column == null)
                    continue;
                if (column.IsNumeric)
                {
                    columns[axis] = columnIndex;
                }
                else if (column.IsCategorical)
                {
                    columns[axis] = columnIndex;
                    string[] table = ds.GetCategoryTable(columnIndex) ?? Array.Empty<string>();
                    AxisScale scale = frame.Get(axis);
                    int[] map = scale.IndexesOfCategories(table); // one dictionary, not a linear search per category
                    categoryMaps[axis] = map;
                }
            }

            var points = new List<Vector3>(rows.Count);
            foreach (int row in rows)
            {
                var p = new Vector3();
                for (int axis = 0; axis < 3; axis++)
                {
                    int c = columns[axis];
                    if (c < 0)
                        continue;
                    int[] map = categoryMaps[axis];
                    if (map == null)
                    {
                        p[axis] = ds.GetNumericValue(row, c);
                    }
                    else
                    {
                        int index = ds.GetCategoryIndex(row, c);
                        p[axis] = index >= 0 && index < map.Length ? Math.Max(0, map[index]) : 0;
                    }
                }
                points.Add(p);
            }
            return points;
        }

        /// <summary>
        /// Order value per row for Path / SmoothedPath: OrderColumnIndex, else the source's time
        /// column (numeric value or category index; NaN for an empty cell, which Path skips). With
        /// neither, or an untyped column, the dataset's row order.
        /// </summary>
        static List<float> OrderValues(PlotLayer computed, PlotLayer rowLayer, DatasetColumnar ds, List<int> rows)
        {
            int columnIndex = computed.OrderColumnIndex >= 0 ? computed.OrderColumnIndex : rowLayer.TimeColumnIndex;
            DatasetColumn column = ds.GetColumn(columnIndex);
            bool typed = column != null && (column.IsNumeric || column.IsCategorical);

            var order = new List<float>(rows.Count);
            foreach (int row in rows)
            {
                if (!typed)
                    order.Add(row);
                else if (!ds.HasValue(row, columnIndex))
                    order.Add(float.NaN);
                else
                    order.Add(column.IsNumeric ? ds.GetNumericValue(row, columnIndex) : ds.GetCategoryIndex(row, columnIndex));
            }
            return order;
        }

        static ComputedShape ToCube(ComputedShape shape, AxisFrame frame)
        {
            if (!shape.Valid)
                return shape;
            if (shape.IsMarker)
            {
                shape.Marker = ToCube(frame, shape.Marker);
                return shape;
            }
            var cube = new Vector3[shape.Polyline.Length];
            for (int i = 0; i < cube.Length; i++)
                cube[i] = ToCube(frame, shape.Polyline[i]);
            shape.Polyline = cube;
            return shape;
        }

        // AxisFrame.ToCube, except that a categorical axis takes a fractional index (a mean or a
        // fitted value between two categories) instead of truncating it to a whole category.
        static Vector3 ToCube(AxisFrame frame, Vector3 p)
        {
            Vector3 unit = frame.ToCube(p.x, p.y, p.z);
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = frame.Get(axis);
                int n = s.Categories?.Length ?? 0;
                if (s.Mapped && s.Categorical && n > 1)
                    unit[axis] = Mathf.Clamp01(p[axis] / (n - 1));
            }
            return unit;
        }

        static bool Dropped(DatasetColumnar ds, int row, int column, AxisScale scale) =>
            scale.Clip == AxisClip.Drop && column >= 0 && scale.Mapped && !scale.Categorical && scale.Outside(ds.GetNumericValue(row, column));

        // Data space -> unit cube without clamping numeric axes (categorical as in ToCube).
        static Vector3 UnitOf(AxisFrame frame, Vector3 p)
        {
            var unit = new Vector3();
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = frame.Get(axis);
                int n = s.Categories?.Length ?? 0;
                if (!s.Mapped)
                    unit[axis] = 0f;
                else if (s.Categorical)
                    unit[axis] = n > 1 ? Mathf.Clamp01(p[axis] / (n - 1)) : s.Normalise(p[axis]);
                else
                    unit[axis] = s.Unit(p[axis]);
            }
            return unit;
        }

        const float k_Eps = 1e-5f;

        static bool InCube(Vector3 u) =>
            u.x >= -k_Eps && u.x <= 1f + k_Eps && u.y >= -k_Eps && u.y <= 1f + k_Eps && u.z >= -k_Eps && u.z <= 1f + k_Eps;

        /// <summary>
        /// ASR-04: the shape in the unit cube, cut at the walls instead of clamped onto them. A marker
        /// outside is dropped (Valid false). A line leaving and re-entering the cube becomes several
        /// pieces (Runs); Polyline is the longest. Nothing inside: Valid false.
        /// </summary>
        public static ComputedShape ClipToCube(ComputedShape shape, AxisFrame frame)
        {
            if (!shape.Valid)
                return shape;
            if (shape.IsMarker)
            {
                Vector3 m = UnitOf(frame, shape.Marker);
                if (!InCube(m))
                    return default;
                shape.Marker = m;
                return shape;
            }

            Vector3[] line = shape.Polyline ?? Array.Empty<Vector3>();
            var units = new Vector3[line.Length];
            for (int i = 0; i < line.Length; i++)
                units[i] = UnitOf(frame, line[i]);

            var runs = new List<List<Vector3>>();
            List<Vector3> current = null;
            if (units.Length == 1 && InCube(units[0]))
                runs.Add(new List<Vector3> { units[0] });

            int segments = units.Length - 1 + (shape.Closed && units.Length > 2 ? 1 : 0);
            for (int i = 0; i < segments; i++)
            {
                Vector3 a = units[i], b = units[(i + 1) % units.Length];
                if (!ClipSegment(ref a, ref b, out bool startCut, out bool endCut))
                {
                    current = null;
                    continue;
                }
                if (current == null || startCut)
                {
                    current = new List<Vector3> { a };
                    runs.Add(current);
                }
                current.Add(b);
                if (endCut)
                    current = null;
            }

            if (runs.Count == 0)
                return default;

            // A closed shape that stayed whole keeps its loop; otherwise it is open pieces.
            bool whole = runs.Count == 1 && runs[0].Count == units.Length + (shape.Closed && units.Length > 2 ? 1 : 0);
            List<Vector3> longest = runs[0];
            foreach (List<Vector3> r in runs)
                if (r.Count > longest.Count)
                    longest = r;

            if (whole && shape.Closed)
            {
                longest.RemoveAt(longest.Count - 1); // the closing vertex repeats the first
                shape.Polyline = longest.ToArray();
                shape.Runs = null;
                return shape;
            }

            shape.Closed = false;
            shape.Polyline = longest.ToArray();
            if (runs.Count > 1)
            {
                shape.Runs = new Vector3[runs.Count][];
                for (int i = 0; i < runs.Count; i++)
                    shape.Runs[i] = runs[i].ToArray();
            }
            else
            {
                shape.Runs = null;
            }
            return shape;
        }

        // Liang-Barsky against the unit cube. startCut / endCut: that end was moved onto a wall.
        static bool ClipSegment(ref Vector3 a, ref Vector3 b, out bool startCut, out bool endCut)
        {
            startCut = endCut = false;
            float t0 = 0f, t1 = 1f;
            Vector3 d = b - a;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!Edge(-d[axis], a[axis] - 0f, ref t0, ref t1)) return false;
                if (!Edge(d[axis], 1f - a[axis], ref t0, ref t1)) return false;
            }
            Vector3 na = a + d * t0, nb = a + d * t1;
            startCut = t0 > 0f;
            endCut = t1 < 1f;
            a = na;
            b = nb;
            return true;
        }

        static bool Edge(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-12f)
                return q >= -k_Eps;
            float r = q / p;
            if (p < 0f)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }

        static string Join(string first, string second) =>
            string.IsNullOrEmpty(first) ? second ?? "" : string.IsNullOrEmpty(second) ? first : first + ", " + second;
    }
}
