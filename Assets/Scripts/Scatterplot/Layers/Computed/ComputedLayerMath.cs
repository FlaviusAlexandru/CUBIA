using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// What a computed layer draws: one polyline (open, or closed when Closed), or a marker at one
    /// point. Valid is false when there is nothing to draw (too few points, value out of range).
    /// ComputedLayerMath returns it in data space (Reference: unit cube); ComputedLayerBuilder
    /// returns it in the unit cube.
    /// </summary>
    public struct ComputedShape
    {
        public bool Valid;
        public Vector3[] Polyline;
        public bool Closed;
        public bool IsMarker;
        public Vector3 Marker;

        /// <summary>
        /// Set only when clipping at the cube walls (axis Limits / Zoom, ASR-04) split the line into
        /// several pieces: every piece, in order. Polyline is then the longest piece.
        /// </summary>
        public Vector3[][] Runs;
    }

    /// <summary>
    /// The maths of the five computed kinds (LFR-04, LFR-21). Pure: no Unity objects, no layers.
    /// Points come in data space (a categorical axis as its union category index) and the shape
    /// goes out in the same space, except Reference, which only needs the axis scale and works in
    /// the unit cube. Every function is O(N) or O(N log N), so 10,000 rows stay far inside LNFR-03.
    /// </summary>
    public static class ComputedLayerMath
    {
        public const int SmoothBins = 256;
        public const int SmoothSamples = 64;
        public const int DefaultMaxVertices = 20000;

        /// <summary>
        /// Fits the two other axes against driverAxis (Q10 option a), each one separately. Linear:
        /// least squares, two vertices at the smallest and largest driver value. Smoothed: loess as
        /// in R's stats::loess and ggplot2 geom_smooth(method = "loess") with their defaults (local
        /// quadratic, tricube weights, family gaussian so no robustness iterations), evaluated
        /// directly (R's surface = "direct") at SmoothSamples evenly spaced driver values. span is
        /// R's span: the fraction of the points in each local fit, clamped to 0.01..1. For speed the
        /// points are first binned into SmoothBins bins along the driver; each non-empty bin is one
        /// observation at its mean driver value, weighted by its count, so the result equals loess on
        /// the raw points whenever there are no more distinct driver values than bins (otherwise up
        /// to binning error). Invalid with fewer than two points or no driver range.
        /// </summary>
        public static ComputedShape Trendline(IReadOnlyList<Vector3> points, int driverAxis, bool smoothed, float span)
        {
            if (points == null || driverAxis < 0 || driverAxis > 2)
                return default;

            int a = (driverAxis + 1) % 3, b = (driverAxis + 2) % 3;
            int n = 0;
            double min = double.MaxValue, max = double.MinValue, meanD = 0, meanA = 0, meanB = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                if (!Finite(p))
                    continue;
                double d = p[driverAxis];
                n++;
                meanD += d; meanA += p[a]; meanB += p[b];
                if (d < min) min = d;
                if (d > max) max = d;
            }

            double range = max - min;
            if (n < 2 || !(range > 1e-9 * Math.Max(1.0, Math.Abs(max))))
                return default;
            meanD /= n; meanA /= n; meanB /= n;

            if (!smoothed)
            {
                // Two passes (centred sums) so large offsets such as timestamps keep their precision.
                double sdd = 0, sda = 0, sdb = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    Vector3 p = points[i];
                    if (!Finite(p))
                        continue;
                    double dd = p[driverAxis] - meanD;
                    sdd += dd * dd;
                    sda += dd * (p[a] - meanA);
                    sdb += dd * (p[b] - meanB);
                }
                double slopeA = sda / sdd, slopeB = sdb / sdd;
                var line = new Vector3[2];
                line[0] = Compose(driverAxis, a, b, min, meanA + slopeA * (min - meanD), meanB + slopeB * (min - meanD));
                line[1] = Compose(driverAxis, a, b, max, meanA + slopeA * (max - meanD), meanB + slopeB * (max - meanD));
                return new ComputedShape { Valid = true, Polyline = line };
            }

            // One observation per non-empty bin: x = mean driver value, y = mean of a / b, weight =
            // count. Exact when every bin holds a single distinct driver value.
            var count = new double[SmoothBins];
            var sumD = new double[SmoothBins];
            var sumA = new double[SmoothBins];
            var sumB = new double[SmoothBins];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                if (!Finite(p))
                    continue;
                int bin = (int)((p[driverAxis] - min) / range * SmoothBins);
                bin = Math.Max(0, Math.Min(SmoothBins - 1, bin));
                count[bin]++;
                sumD[bin] += p[driverAxis];
                sumA[bin] += p[a];
                sumB[bin] += p[b];
            }

            int m = 0;
            var bx = new double[SmoothBins];
            var bn = new double[SmoothBins];
            var ba = new double[SmoothBins];
            var bb = new double[SmoothBins];
            for (int bin = 0; bin < SmoothBins; bin++)
            {
                double c = count[bin];
                if (c <= 0)
                    continue;
                bx[m] = sumD[bin] / c; // ascending: bins are disjoint driver intervals
                bn[m] = c;
                ba[m] = sumA[bin] / c;
                bb[m] = sumB[bin] / c;
                m++;
            }

            double f = float.IsFinite(span) ? Math.Max(MinTrendSpan, Math.Min(MaxTrendSpan, span)) : LayerRules.DefaultTrendlineSpan;
            double q = Math.Min(n, Math.Max(1.0, Math.Floor(n * f))); // R: nf = min(n, floor(n * span))
            var w = new double[m];
            var curve = new List<Vector3>(SmoothSamples);
            for (int k = 0; k < SmoothSamples; k++)
            {
                double x = min + range * k / (SmoothSamples - 1);
                if (LoessAt(bx, bn, ba, bb, m, x, q, f, w, out double fitA, out double fitB))
                    curve.Add(Compose(driverAxis, a, b, x, fitA, fitB));
            }

            if (curve.Count < 2)
                return default;
            return new ComputedShape { Valid = true, Polyline = curve.ToArray() };
        }

        const double MinTrendSpan = 0.01, MaxTrendSpan = 1.0; // same range as RequestSetComputedSpanRpc

        /// <summary>
        /// One loess estimate at x for both fitted axes. Neighbourhood: the bins nearest x, nearest
        /// first, until their counts reach q points (R: the q nearest points); dmax = distance to the
        /// farthest bin taken (times span when span > 1, as R does with one predictor). Weights:
        /// count × (1 - (d/dmax)³)³, so the farthest bin gets 0 like R's q-th point. Fit: weighted
        /// least squares quadratic in u = (x_i - x) / dmax (centred and scaled for a stable solve);
        /// the estimate is its intercept. Falls back to a line and then to the weighted mean when
        /// fewer than three (two) driver values carry weight or the normal equations are singular.
        /// w is scratch space of length >= m. False when there is nothing to fit.
        /// </summary>
        static bool LoessAt(double[] bx, double[] bn, double[] ba, double[] bb, int m, double x, double q, double span,
                            double[] w, out double fitA, out double fitB)
        {
            fitA = fitB = 0;
            if (m == 0)
                return false;

            int hi = 0;
            while (hi < m && bx[hi] < x)
                hi++;
            int lo = hi - 1;
            double taken = 0, dmax = 0;
            while (taken < q && (lo >= 0 || hi < m))
            {
                double dl = lo >= 0 ? x - bx[lo] : double.PositiveInfinity;
                double dh = hi < m ? bx[hi] - x : double.PositiveInfinity;
                if (dl <= dh) { taken += bn[lo]; dmax = dl; lo--; }
                else { taken += bn[hi]; dmax = dh; hi++; }
            }
            int first = lo + 1, last = hi - 1;
            dmax *= Math.Max(1.0, span);
            double h = dmax > 0 ? dmax : 1.0;

            int carrying = 0;
            for (int i = first; i <= last; i++)
            {
                double wi;
                if (dmax <= 0)
                    wi = bn[i]; // every point taken sits exactly at x
                else
                {
                    double u = Math.Abs(bx[i] - x) / dmax;
                    double t = u < 1 ? 1 - u * u * u : 0;
                    wi = bn[i] * t * t * t;
                }
                w[i] = wi;
                if (wi > 0)
                    carrying++;
            }
            if (carrying == 0)
            {
                // Only the boundary bin is in reach (span tiny): its mean is all we can say.
                for (int i = first; i <= last; i++)
                    w[i] = bn[i];
                carrying = last - first + 1;
            }

            double m0 = 0, m1 = 0, m2 = 0, m3 = 0, m4 = 0, a0 = 0, a1 = 0, a2 = 0, b0 = 0, b1 = 0, b2 = 0;
            for (int i = first; i <= last; i++)
            {
                double wi = w[i];
                if (wi <= 0)
                    continue;
                double u = (bx[i] - x) / h;
                double wu = wi * u, wu2 = wu * u;
                m0 += wi; m1 += wu; m2 += wu2; m3 += wu2 * u; m4 += wu2 * u * u;
                a0 += wi * ba[i]; a1 += wu * ba[i]; a2 += wu2 * ba[i];
                b0 += wi * bb[i]; b1 += wu * bb[i]; b2 += wu2 * bb[i];
            }

            for (int degree = Math.Min(2, carrying - 1); degree >= 0; degree--)
            {
                if (Intercept(degree + 1, m0, m1, m2, m3, m4, a0, a1, a2, out fitA) &&
                    Intercept(degree + 1, m0, m1, m2, m3, m4, b0, b1, b2, out fitB))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The intercept of a local polynomial with size coefficients (1..3) from its weighted normal
        /// equations [m0 m1 m2; m1 m2 m3; m2 m3 m4] c = [t0 t1 t2], by Gaussian elimination with
        /// partial pivoting. False when a pivot is negligible against m0 (singular).
        /// </summary>
        static bool Intercept(int size, double m0, double m1, double m2, double m3, double m4,
                              double t0, double t1, double t2, out double c0)
        {
            c0 = 0;
            if (!(m0 > 0))
                return false;
            if (size == 1)
            {
                c0 = t0 / m0;
                return true;
            }

            var s = new double[3, 4] { { m0, m1, m2, t0 }, { m1, m2, m3, t1 }, { m2, m3, m4, t2 } };
            for (int col = 0; col < size; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < size; r++)
                    if (Math.Abs(s[r, col]) > Math.Abs(s[pivot, col]))
                        pivot = r;
                if (!(Math.Abs(s[pivot, col]) > 1e-10 * m0))
                    return false;
                if (pivot != col)
                    for (int c = 0; c < 4; c++)
                        (s[col, c], s[pivot, c]) = (s[pivot, c], s[col, c]);
                for (int r = col + 1; r < size; r++)
                {
                    double factor = s[r, col] / s[col, col];
                    for (int c = col; c < 4; c++)
                        s[r, c] -= factor * s[col, c];
                }
            }

            var coef = new double[3];
            for (int r = size - 1; r >= 0; r--)
            {
                double sum = s[r, 3];
                for (int c = r + 1; c < size; c++)
                    sum -= s[r, c] * coef[c];
                coef[r] = sum / s[r, r];
            }
            c0 = coef[0];
            return !(double.IsNaN(c0) || double.IsInfinity(c0));
        }

        /// <summary>
        /// The points joined in ascending order of order[i]; points whose order value is NaN or
        /// infinite are skipped, ties keep their input order. Thinned to at most maxVertices by
        /// keeping every k-th point plus the last. Invalid with fewer than two points.
        /// </summary>
        public static ComputedShape Path(IReadOnlyList<Vector3> points, IReadOnlyList<float> order, int maxVertices = DefaultMaxVertices)
        {
            List<Vector3> sorted = SortByOrder(points, order);
            if (sorted.Count < 2)
                return default;
            return new ComputedShape { Valid = true, Polyline = Thin(sorted, maxVertices) };
        }

        /// <summary>
        /// Path, then a centred moving average over max(3, span × count) points (odd). Near the
        /// ends the window shrinks symmetrically, so the first and last points stay where they are.
        /// </summary>
        public static ComputedShape SmoothedPath(IReadOnlyList<Vector3> points, IReadOnlyList<float> order, float span, int maxVertices = DefaultMaxVertices)
        {
            List<Vector3> sorted = SortByOrder(points, order);
            int n = sorted.Count;
            if (n < 2)
                return default;

            int window = Math.Max(3, (int)Math.Round(Math.Max(0f, span) * n));
            if (window % 2 == 0)
                window++;
            int half = window / 2;

            // Prefix sums in double: O(N) whatever the window.
            var prefix = new double[n + 1, 3];
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 3; c++)
                    prefix[i + 1, c] = prefix[i, c] + sorted[i][c];
            }

            var smooth = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                int h = Math.Min(half, Math.Min(i, n - 1 - i));
                int lo = i - h, hi = i + h + 1;
                int len = hi - lo;
                smooth.Add(new Vector3(
                    (float)((prefix[hi, 0] - prefix[lo, 0]) / len),
                    (float)((prefix[hi, 1] - prefix[lo, 1]) / len),
                    (float)((prefix[hi, 2] - prefix[lo, 2]) / len)));
            }
            return new ComputedShape { Valid = true, Polyline = Thin(smooth, maxVertices) };
        }

        /// <summary>The centroid of the points as a marker. Invalid when there is no point.</summary>
        public static ComputedShape Mean(IReadOnlyList<Vector3> points)
        {
            if (points == null)
                return default;
            double x = 0, y = 0, z = 0;
            int n = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                if (!Finite(p))
                    continue;
                x += p.x; y += p.y; z += p.z;
                n++;
            }
            if (n == 0)
                return default;
            return new ComputedShape { Valid = true, IsMarker = true, Marker = new Vector3((float)(x / n), (float)(y / n), (float)(z / n)) };
        }

        /// <summary>
        /// A reference at value on axis, in the unit cube. Line (plane false): an L on the cube's
        /// two minimum faces of the other axes, (0,1) → (0,0) → (1,0). Plane: the closed rectangle
        /// round the cube at that value. On a categorical axis value is a union category index
        /// (rounded). Invalid when the axis is not mapped or value lies outside the scale.
        /// </summary>
        public static ComputedShape Reference(AxisScale scale, int axis, float value, bool plane)
        {
            if (!scale.Mapped || axis < 0 || axis > 2 || float.IsNaN(value) || float.IsInfinity(value))
                return default;

            float u;
            if (scale.Categorical)
            {
                int index = (int)Math.Round(value);
                int n = scale.Categories?.Length ?? 0;
                if (index < 0 || index >= n)
                    return default;
                u = scale.NormaliseCategoryIndex(index);
            }
            else
            {
                float lo = Math.Min(scale.Min, scale.Max), hi = Math.Max(scale.Min, scale.Max);
                if (value < lo || value > hi)
                    return default;
                u = scale.Normalise(value);
            }

            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            Vector3[] vertices = plane
                ? new[] { Compose(axis, a, b, u, 0, 0), Compose(axis, a, b, u, 1, 0), Compose(axis, a, b, u, 1, 1), Compose(axis, a, b, u, 0, 1) }
                : new[] { Compose(axis, a, b, u, 0, 1), Compose(axis, a, b, u, 0, 0), Compose(axis, a, b, u, 1, 0) };
            return new ComputedShape { Valid = true, Polyline = vertices, Closed = plane };
        }

        static List<Vector3> SortByOrder(IReadOnlyList<Vector3> points, IReadOnlyList<float> order)
        {
            var result = new List<Vector3>();
            if (points == null || order == null)
                return result;

            int n = Math.Min(points.Count, order.Count);
            var keys = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                float o = order[i];
                if (!float.IsNaN(o) && !float.IsInfinity(o) && Finite(points[i]))
                    keys.Add(i);
            }
            // Order value, then input index: a stable sort, so ties keep the dataset's row order.
            keys.Sort((i, j) =>
            {
                int c = order[i].CompareTo(order[j]);
                return c != 0 ? c : i.CompareTo(j);
            });

            result.Capacity = keys.Count;
            foreach (int i in keys)
                result.Add(points[i]);
            return result;
        }

        static Vector3[] Thin(List<Vector3> list, int maxVertices)
        {
            maxVertices = Math.Max(2, maxVertices);
            if (list.Count <= maxVertices)
                return list.ToArray();

            int stride = (list.Count + maxVertices - 2) / (maxVertices - 1); // ceil((n-1)/(max-1)): last point fits
            var result = new List<Vector3>(maxVertices);
            for (int i = 0; i < list.Count - 1; i += stride)
                result.Add(list[i]);
            result.Add(list[list.Count - 1]);
            return result.ToArray();
        }

        static Vector3 Compose(int driverAxis, int a, int b, double d, double va, double vb)
        {
            var v = new Vector3();
            v[driverAxis] = (float)d;
            v[a] = (float)va;
            v[b] = (float)vb;
            return v;
        }

        static bool Finite(Vector3 p) =>
            !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
              float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
    }
}
