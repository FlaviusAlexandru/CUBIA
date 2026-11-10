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
        /// Fits the two other axes against driverAxis (Q10 option a). Linear: least squares, two
        /// vertices at the smallest and largest driver value. Smoothed: the points are binned into
        /// SmoothBins bins along the driver, then a local-linear Gaussian kernel fit (bandwidth
        /// span × driver range, like loess of degree 1) is evaluated at SmoothSamples evenly spaced
        /// driver values. Local-linear rather than a plain kernel average so the curve does not bend
        /// towards the mean at both ends. Invalid with fewer than two points or no driver range.
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

            double sigma = Math.Max(0.01, Math.Min(1.0, span)) * range;
            var curve = new List<Vector3>(SmoothSamples);
            for (int k = 0; k < SmoothSamples; k++)
            {
                double x = min + range * k / (SmoothSamples - 1);
                double s0 = 0, s1 = 0, s2 = 0, ta = 0, ta1 = 0, tb = 0, tb1 = 0;
                for (int bin = 0; bin < SmoothBins; bin++)
                {
                    if (count[bin] <= 0)
                        continue;
                    double c = count[bin];
                    double dx = sumD[bin] / c - x;
                    double u = dx / sigma;
                    double w = c * Math.Exp(-0.5 * u * u);
                    if (w <= 0)
                        continue;
                    double ya = sumA[bin] / c, yb = sumB[bin] / c;
                    s0 += w; s1 += w * dx; s2 += w * dx * dx;
                    ta += w * ya; ta1 += w * dx * ya;
                    tb += w * yb; tb1 += w * dx * yb;
                }
                if (s0 < 1e-12)
                    continue; // no data within reach of this sample: leave it out rather than invent a value

                double det = s0 * s2 - s1 * s1;
                double fitA, fitB;
                if (det > 1e-9 * s0 * s2 && det > 0)
                {
                    fitA = (s2 * ta - s1 * ta1) / det;
                    fitB = (s2 * tb - s1 * tb1) / det;
                }
                else
                {
                    fitA = ta / s0; // all weight in one place: the local mean is all we can say
                    fitB = tb / s0;
                }
                curve.Add(Compose(driverAxis, a, b, x, fitA, fitB));
            }

            if (curve.Count < 2)
                return default;
            return new ComputedShape { Valid = true, Polyline = curve.ToArray() };
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
