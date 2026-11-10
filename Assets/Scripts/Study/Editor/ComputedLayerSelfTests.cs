using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>WP3 checks for computed layers: the maths, the builder (rows, frame, signature, source missing) and LNFR-03 timing.</summary>
    public static class ComputedLayerSelfTests
    {
        // Time 0..4; X = 2t, Y = 10 + t, Z = t - 1: every row on one straight line.
        static DatasetColumnar Movement(string name = "wp3_movement.cdataset") => SelfTestKit.Dataset(name,
            Col.Num("Time", 0f, 1f, 2f, 3f, 4f),
            Col.Num("X", 0f, 2f, 4f, 6f, 8f),
            Col.Num("Y", 10f, 11f, 12f, 13f, 14f),
            Col.Num("Z", -1f, 0f, 1f, 2f, 3f),
            Col.Cat("Phase", "a", "b", "a", "c", "b"));

        // X = X, Y = Y, Z = Time (column 0), time column Time: the default driver is Z.
        static (LayerStack stack, PlotLayer source) MovementStack(DatasetColumnar ds)
        {
            var stack = new LayerStack();
            PlotLayer source = stack.AddDataset(ds.Name, ds);
            source.XColumnIndex = 1;
            source.YColumnIndex = 2;
            source.ZColumnIndex = 0;
            source.TimeColumnIndex = 0;
            return (stack, source);
        }

        static ComputedShape Build(PlotLayer computed, LayerStack stack, out string status) =>
            ComputedLayerBuilder.Build(computed, stack, LayerBounds.Compute(stack.Layers), out _, out status);

        static void NearV(SelfTestTally t, string name, Vector3 expected, Vector3 actual, float tolerance = 1e-4f)
        {
            t.Near(name + " x", expected.x, actual.x, tolerance);
            t.Near(name + " y", expected.y, actual.y, tolerance);
            t.Near(name + " z", expected.z, actual.z, tolerance);
        }

        // ---- ComputedLayerMath ----------------------------------------------------------------

        [SelfTest("Computed: linear trendline end points")]
        static void LinearTrend(SelfTestTally t)
        {
            // On the line x = 2z + 1, y = 3 - z, in shuffled order, driver Z.
            var points = new List<Vector3>();
            foreach (float z in new[] { 4f, 0f, 7f, 10f, 2f, 5f })
                points.Add(new Vector3(2f * z + 1f, 3f - z, z));
            ComputedShape s = ComputedLayerMath.Trendline(points, 2, false, 0.1f);
            t.True("linear valid", s.Valid);
            t.Eq("linear two vertices", 2, s.Polyline?.Length ?? 0);
            if (s.Polyline == null || s.Polyline.Length != 2) return;
            NearV(t, "linear start", new Vector3(1f, 3f, 0f), s.Polyline[0]);
            NearV(t, "linear end", new Vector3(21f, -7f, 10f), s.Polyline[1]);

            // Driver X: the other two are fitted against x.
            ComputedShape sx = ComputedLayerMath.Trendline(points, 0, false, 0.1f);
            t.True("linear driver x valid", sx.Valid);
            if (sx.Valid)
            {
                NearV(t, "linear driver x start", new Vector3(1f, 3f, 0f), sx.Polyline[0]);
                NearV(t, "linear driver x end", new Vector3(21f, -7f, 10f), sx.Polyline[1]);
            }
        }

        [SelfTest("Computed: smoothed trendline follows a noisy sine")]
        static void SmoothedTrend(SelfTestTally t)
        {
            var random = new System.Random(1);
            var points = new List<Vector3>();
            const int n = 2000;
            for (int i = 0; i < n; i++)
            {
                float x = 2f * Mathf.PI * i / (n - 1);
                points.Add(new Vector3(x, Mathf.Sin(x) + (float)(random.NextDouble() * 0.2 - 0.1), Mathf.Cos(x) + (float)(random.NextDouble() * 0.2 - 0.1)));
            }
            ComputedShape s = ComputedLayerMath.Trendline(points, 0, true, 0.03f);
            t.True("smooth valid", s.Valid);
            t.Eq("smooth 64 vertices", ComputedLayerMath.SmoothSamples, s.Polyline?.Length ?? 0);
            if (s.Polyline == null) return;

            float worstY = 0f, worstZ = 0f;
            foreach (Vector3 p in s.Polyline)
            {
                worstY = Mathf.Max(worstY, Mathf.Abs(p.y - Mathf.Sin(p.x)));
                worstZ = Mathf.Max(worstZ, Mathf.Abs(p.z - Mathf.Cos(p.x)));
            }
            t.Near("smooth within sine", 0f, worstY, 0.06f);
            t.Near("smooth within cosine", 0f, worstZ, 0.06f);
            t.Near("smooth starts at driver min", 0f, s.Polyline[0].x, 1e-4f);
            t.Near("smooth ends at driver max", 2f * Mathf.PI, s.Polyline[s.Polyline.Length - 1].x, 1e-3f);
        }

        [SelfTest("Computed: path order and skips")]
        static void PathOrder(SelfTestTally t)
        {
            var points = new List<Vector3> { new(30, 0, 0), new(10, 0, 0), new(99, 0, 0), new(20, 0, 0), new(15, 0, 0) };
            var order = new List<float> { 3f, 1f, float.NaN, 2f, 1f };
            ComputedShape s = ComputedLayerMath.Path(points, order);
            t.True("path valid", s.Valid);
            t.Eq("path skips NaN order", 4, s.Polyline?.Length ?? 0);
            if (s.Polyline == null || s.Polyline.Length != 4) return;
            // Ties (order 1) keep input order: 10 before 15.
            t.Near("path 0", 10f, s.Polyline[0].x); t.Near("path 1", 15f, s.Polyline[1].x);
            t.Near("path 2", 20f, s.Polyline[2].x); t.Near("path 3", 30f, s.Polyline[3].x);

            // Thinning keeps the first and last point and at most maxVertices.
            var many = new List<Vector3>(); var manyOrder = new List<float>();
            for (int i = 0; i < 1001; i++) { many.Add(new Vector3(i, 0, 0)); manyOrder.Add(i); }
            ComputedShape thin = ComputedLayerMath.Path(many, manyOrder, 100);
            t.True("path thinned to max", thin.Polyline.Length <= 100 && thin.Polyline.Length >= 50);
            t.Near("path thinned first", 0f, thin.Polyline[0].x);
            t.Near("path thinned last", 1000f, thin.Polyline[thin.Polyline.Length - 1].x);

            // Smoothed path: same order, ends kept, a zig-zag flattened.
            var zig = new List<Vector3>(); var zigOrder = new List<float>();
            for (int i = 0; i < 21; i++) { zig.Add(new Vector3(i, i % 2 == 0 ? 1f : -1f, 0)); zigOrder.Add(20 - i); }
            ComputedShape sm = ComputedLayerMath.SmoothedPath(zig, zigOrder, 0.2f);
            t.Eq("smoothed path count", 21, sm.Polyline?.Length ?? 0);
            if (sm.Polyline != null && sm.Polyline.Length == 21)
            {
                t.Near("smoothed path first kept", 20f, sm.Polyline[0].x);
                t.Near("smoothed path last kept", 0f, sm.Polyline[20].x);
                t.True("smoothed path flattened", Mathf.Abs(sm.Polyline[10].y) < 0.3f);
            }
        }

        [SelfTest("Computed: mean marker")]
        static void MeanMarker(SelfTestTally t)
        {
            ComputedShape s = ComputedLayerMath.Mean(new List<Vector3> { new(1, 2, 3), new(3, 4, 5), new(5, 0, -2) });
            t.True("mean valid", s.Valid);
            t.True("mean is marker", s.IsMarker);
            NearV(t, "mean value", new Vector3(3f, 2f, 2f), s.Marker);
        }

        [SelfTest("Computed: reference line and plane")]
        static void ReferenceShapes(SelfTestTally t)
        {
            AxisScale scale = AxisScale.Numeric(0f, 10f);
            ComputedShape line = ComputedLayerMath.Reference(scale, 1, 5f, false);
            t.True("ref line valid", line.Valid);
            t.Eq("ref line vertices", 3, line.Polyline?.Length ?? 0);
            t.Eq("ref line open", false, line.Closed);
            if (line.Polyline != null && line.Polyline.Length == 3)
            {
                NearV(t, "ref line 0", new Vector3(1f, 0.5f, 0f), line.Polyline[0]);
                NearV(t, "ref line 1", new Vector3(0f, 0.5f, 0f), line.Polyline[1]);
                NearV(t, "ref line 2", new Vector3(0f, 0.5f, 1f), line.Polyline[2]);
            }

            ComputedShape plane = ComputedLayerMath.Reference(scale, 0, 2.5f, true);
            t.True("ref plane valid", plane.Valid);
            t.Eq("ref plane closed", true, plane.Closed);
            t.Eq("ref plane vertices", 4, plane.Polyline?.Length ?? 0);
            if (plane.Polyline != null && plane.Polyline.Length == 4)
            {
                NearV(t, "ref plane 0", new Vector3(0.25f, 0f, 0f), plane.Polyline[0]);
                NearV(t, "ref plane 1", new Vector3(0.25f, 1f, 0f), plane.Polyline[1]);
                NearV(t, "ref plane 2", new Vector3(0.25f, 1f, 1f), plane.Polyline[2]);
                NearV(t, "ref plane 3", new Vector3(0.25f, 0f, 1f), plane.Polyline[3]);
            }

            t.Eq("ref above range invalid", false, ComputedLayerMath.Reference(scale, 1, 10.5f, false).Valid);
            t.Eq("ref below range invalid", false, ComputedLayerMath.Reference(scale, 1, -0.1f, true).Valid);
            t.Eq("ref unmapped invalid", false, ComputedLayerMath.Reference(new AxisScale(), 1, 0f, false).Valid);
            t.Eq("ref NaN invalid", false, ComputedLayerMath.Reference(scale, 1, float.NaN, false).Valid);
            ComputedShape cat = ComputedLayerMath.Reference(AxisScale.Category(new[] { "a", "b", "c" }), 2, 1f, false);
            t.True("ref categorical valid", cat.Valid);
            if (cat.Valid) t.Near("ref categorical position", 0.5f, cat.Polyline[0].z);
            t.Eq("ref categorical out of range", false, ComputedLayerMath.Reference(AxisScale.Category(new[] { "a", "b" }), 2, 2f, false).Valid);
        }

        [SelfTest("Computed: too few points is invalid, no throw")]
        static void TooFew(SelfTestTally t)
        {
            var one = new List<Vector3> { new(1, 2, 3) };
            var none = new List<Vector3>();
            t.Eq("trend one point", false, ComputedLayerMath.Trendline(one, 2, false, 0.1f).Valid);
            t.Eq("trend one point smoothed", false, ComputedLayerMath.Trendline(one, 2, true, 0.1f).Valid);
            t.Eq("trend none", false, ComputedLayerMath.Trendline(none, 2, false, 0.1f).Valid);
            t.Eq("trend null", false, ComputedLayerMath.Trendline(null, 2, false, 0.1f).Valid);
            t.Eq("trend zero driver range", false,
                 ComputedLayerMath.Trendline(new List<Vector3> { new(1, 2, 3), new(4, 5, 3) }, 2, false, 0.1f).Valid);
            t.Eq("path one point", false, ComputedLayerMath.Path(one, new List<float> { 0f }).Valid);
            t.Eq("path null", false, ComputedLayerMath.Path(null, null).Valid);
            t.Eq("smoothed path one point", false, ComputedLayerMath.SmoothedPath(one, new List<float> { 0f }, 0.1f).Valid);
            t.Eq("mean none", false, ComputedLayerMath.Mean(none).Valid);
            t.Eq("mean one point", true, ComputedLayerMath.Mean(one).Valid);
        }

        // ---- ComputedLayerBuilder -------------------------------------------------------------

        [SelfTest("Computed: builder trendline on its source, after filters")]
        static void BuilderTrend(SelfTestTally t)
        {
            var (stack, source) = MovementStack(Movement());
            PlotLayer trend = stack.AddComputed(source, ComputedLayerKind.Trendline);
            ComputedShape s = Build(trend, stack, out string status);
            t.True("builder trend valid", s.Valid);
            t.Eq("builder trend status", "", status);
            if (!s.Valid) return;
            NearV(t, "builder trend start (cube)", Vector3.zero, s.Polyline[0]);
            NearV(t, "builder trend end (cube)", Vector3.one, s.Polyline[1]);

            // Filter the source to Phase = a (rows t = 0 and 2): the line now ends half-way up Z.
            int before = LayerRows.InputSignature(source, stack);
            int sigBefore = ComputedLayerBuilder.Signature(trend, stack, LayerBounds.Compute(stack.Layers));
            source.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 4, Label = "a" });
            t.True("filter changes InputSignature", before != LayerRows.InputSignature(source, stack));
            t.True("filter changes Signature", sigBefore != ComputedLayerBuilder.Signature(trend, stack, LayerBounds.Compute(stack.Layers)));
            ComputedShape filtered = Build(trend, stack, out _);
            t.True("filtered trend valid", filtered.Valid);
            if (filtered.Valid)
                NearV(t, "filtered trend end", new Vector3(0.5f, 0.5f, 0.5f), filtered.Polyline[1]);

            source.Filters[0].Label = "c"; // one row left
            ComputedShape oneRow = Build(trend, stack, out string oneStatus);
            t.Eq("one row invalid", false, oneRow.Valid);
            t.Eq("one row status", ComputedLayerBuilder.StatusNotEnoughPoints, oneStatus);

            // Driver default follows the time column: time on X → driver X.
            source.Filters.Clear();
            source.XColumnIndex = 0; source.ZColumnIndex = 3;
            t.Eq("default driver is time axis", 0, LayerRules.DefaultDriverAxis(source));
            ComputedShape onX = Build(trend, stack, out _);
            t.True("driver x valid", onX.Valid);
            if (onX.Valid)
            {
                t.Near("driver x start", 0f, onX.Polyline[0].x);
                t.Near("driver x end", 1f, onX.Polyline[1].x);
            }
        }

        [SelfTest("Computed: signature ignores style, follows parameters")]
        static void SignatureRules(SelfTestTally t)
        {
            var (stack, source) = MovementStack(Movement());
            PlotLayer trend = stack.AddComputed(source, ComputedLayerKind.Trendline);
            AxisBounds bounds = LayerBounds.Compute(stack.Layers);
            int sig = ComputedLayerBuilder.Signature(trend, stack, bounds);
            t.Eq("signature stable", sig, ComputedLayerBuilder.Signature(trend, stack, bounds));

            trend.Visible = false; trend.Opacity = 0.3f; trend.LineWidth = 0.02f; trend.FixedColour = new Color32(1, 2, 3, 255);
            source.Visible = false;
            t.Eq("signature ignores style and visibility", sig, ComputedLayerBuilder.Signature(trend, stack, bounds));

            trend.Smoothed = true;
            int smoothSig = ComputedLayerBuilder.Signature(trend, stack, bounds);
            t.True("signature follows Smoothed", smoothSig != sig);
            trend.DriverAxis = 0;
            t.True("signature follows driver", ComputedLayerBuilder.Signature(trend, stack, bounds) != smoothSig);
            trend.DriverAxis = -1;
            t.Eq("signature back", smoothSig, ComputedLayerBuilder.Signature(trend, stack, bounds));

            stack.Remove(source.Id);
            t.True("signature follows source removal", ComputedLayerBuilder.Signature(trend, stack, bounds) != smoothSig);
        }

        [SelfTest("Computed: source removed keeps the shape (LFR-22)")]
        static void SourceMissing(SelfTestTally t)
        {
            DatasetColumnar ds = Movement();
            var (stack, source) = MovementStack(ds);
            source.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 4, Label = "a" });
            var kinds = new[] { ComputedLayerKind.Trendline, ComputedLayerKind.Path, ComputedLayerKind.Mean, ComputedLayerKind.Reference, ComputedLayerKind.SmoothedPath };
            var layers = new List<PlotLayer>();
            var before = new List<ComputedShape>();
            AxisBounds bounds = LayerBounds.Compute(stack.Layers);
            foreach (ComputedLayerKind kind in kinds)
            {
                PlotLayer c = stack.AddComputed(source, kind);
                c.RefValue = 12f;
                layers.Add(c);
                before.Add(ComputedLayerBuilder.Build(c, stack, bounds, out _, out _));
                t.True("before removal valid " + kind, before[before.Count - 1].Valid);
                t.Eq("before removal not missing " + kind, false, ComputedLayerBuilder.IsSourceMissing(c, stack));
            }

            stack.Remove(source.Id);
            // The bounds the plot would have now: no point layer left. The builder falls back to the
            // frozen columns' own ranges, which here equal the old shared ones.
            AxisBounds after = LayerBounds.Compute(stack.Layers);
            for (int i = 0; i < kinds.Length; i++)
            {
                ComputedShape s = ComputedLayerBuilder.Build(layers[i], stack, after, out _, out string status);
                t.Eq("missing status " + kinds[i], ComputedLayerBuilder.StatusSourceMissing, status);
                t.True("missing flag " + kinds[i], ComputedLayerBuilder.IsSourceMissing(layers[i], stack));
                SameShape(t, "missing same shape " + kinds[i], before[i], s);
            }

            // A client: the frozen layer holds no dataset, only its name; the registry supplies it.
            PlotLayer trend = layers[0];
            trend.Dataset = null;
            DatasetRegistry.Register(ds.Name, ds);
            try
            {
                ComputedShape s = ComputedLayerBuilder.Build(trend, stack, after, out _, out string status);
                t.Eq("registry status", ComputedLayerBuilder.StatusSourceMissing, status);
                SameShape(t, "registry same shape", before[0], s);
                t.True("registry layer untouched", trend.Dataset == null);
            }
            finally
            {
                DatasetRegistry.Unregister(ds.Name);
            }

            // Data gone too: invalid, never throws.
            trend.DatasetName = "wp3_no_such_file.cdataset";
            ComputedShape gone = ComputedLayerBuilder.Build(trend, stack, after, out _, out string goneStatus);
            t.Eq("gone invalid", false, gone.Valid);
            t.Eq("gone status", ComputedLayerBuilder.StatusSourceMissing + ", " + ComputedLayerBuilder.StatusNoData, goneStatus);
        }

        static void SameShape(SelfTestTally t, string name, ComputedShape a, ComputedShape b)
        {
            t.Eq(name + " valid", a.Valid, b.Valid);
            t.Eq(name + " marker", a.IsMarker, b.IsMarker);
            if (a.IsMarker)
            {
                NearV(t, name + " marker", a.Marker, b.Marker, 1e-5f);
                return;
            }
            int n = a.Polyline?.Length ?? 0;
            t.Eq(name + " count", n, b.Polyline?.Length ?? 0);
            if (n != (b.Polyline?.Length ?? 0)) return;
            float worst = 0f;
            for (int i = 0; i < n; i++)
                worst = Mathf.Max(worst, (a.Polyline[i] - b.Polyline[i]).magnitude);
            t.Near(name + " vertices", 0f, worst, 1e-5f);
        }

        [SelfTest("Computed: builder path, mean, reference")]
        static void BuilderKinds(SelfTestTally t)
        {
            DatasetColumnar ds = SelfTestKit.Dataset("wp3_path.cdataset",
                Col.Num("Order", 3f, 1f, null, 2f),
                Col.Num("X", 30f, 10f, 0f, 20f),
                Col.Num("Y", 1f, 1f, 1f, 1f),
                Col.Num("Z", 0f, 1f, 2f, 3f));
            var stack = new LayerStack();
            PlotLayer source = stack.AddDataset(ds.Name, ds);
            source.XColumnIndex = 1; source.YColumnIndex = 2; source.ZColumnIndex = 3; source.TimeColumnIndex = 0;

            PlotLayer path = stack.AddComputed(source, ComputedLayerKind.Path);
            ComputedShape p = Build(path, stack, out _);
            t.Eq("builder path skips empty order cell", 3, p.Polyline?.Length ?? 0);
            if (p.Polyline != null && p.Polyline.Length == 3)
            {
                // X range 0..30 (the empty cell's row still has X 0): 10, 20, 30 → 1/3, 2/3, 1.
                t.Near("builder path 0", 1f / 3f, p.Polyline[0].x);
                t.Near("builder path 1", 2f / 3f, p.Polyline[1].x);
                t.Near("builder path 2", 1f, p.Polyline[2].x);
            }
            path.OrderColumnIndex = 3; // by Z: rows 0, 1, 2, 3 → X 30, 10, 0, 20
            ComputedShape byZ = Build(path, stack, out _);
            t.Eq("builder path by column count", 4, byZ.Polyline?.Length ?? 0);
            if (byZ.Polyline != null && byZ.Polyline.Length == 4)
                t.Near("builder path by column first", 1f, byZ.Polyline[0].x);

            PlotLayer mean = stack.AddComputed(source, ComputedLayerKind.Mean);
            ComputedShape m = Build(mean, stack, out _);
            t.True("builder mean marker", m.Valid && m.IsMarker);
            if (m.Valid) t.Near("builder mean x", 0.5f, m.Marker.x); // (30+10+0+20)/4 = 15 of 0..30

            // Reference on Y (the default axis), with the 0..3 Z column mapped there.
            PlotLayer reference = stack.AddComputed(source, ComputedLayerKind.Reference);
            source.YColumnIndex = 3; // Z column 0..3 on Y
            reference.RefValue = 1.5f;
            ComputedShape r = Build(reference, stack, out string rs);
            t.True("builder reference valid", r.Valid);
            t.Eq("builder reference status", "", rs);
            if (r.Valid) t.Near("builder reference y", 0.5f, r.Polyline[0].y);
            reference.RefValue = 7f;
            ComputedShape outside = Build(reference, stack, out string os);
            t.Eq("builder reference outside invalid", false, outside.Valid);
            t.Eq("builder reference outside status", ComputedLayerBuilder.StatusOutsideRange, os);

            // Categorical axis: the mean sits between categories (union index 0.8 of 0..2).
            var (mstack, msource) = MovementStack(Movement());
            msource.YColumnIndex = 4; // Phase a,b,a,c,b → 0,1,0,2,1
            PlotLayer catMean = mstack.AddComputed(msource, ComputedLayerKind.Mean);
            ComputedShape cm = Build(catMean, mstack, out _);
            t.True("categorical mean valid", cm.Valid);
            if (cm.Valid) t.Near("categorical mean y", 0.4f, cm.Marker.y);
        }

        [SelfTest("Computed: 10,000 source rows under 0.5 s (LNFR-03)")]
        static void Timing(SelfTestTally t)
        {
            const int n = 10000;
            var time = new float?[n]; var x = new float?[n]; var y = new float?[n]; var z = new float?[n];
            var random = new System.Random(3);
            for (int i = 0; i < n; i++)
            {
                time[i] = i;
                x[i] = (float)random.NextDouble();
                y[i] = Mathf.Sin(i * 0.001f) + (float)random.NextDouble() * 0.1f;
                z[i] = (float)random.NextDouble();
            }
            DatasetColumnar ds = SelfTestKit.Dataset("wp3_big.cdataset", Col.Num("Time", time), Col.Num("X", x), Col.Num("Y", y), Col.Num("Z", z));
            var stack = new LayerStack();
            PlotLayer source = stack.AddDataset(ds.Name, ds);
            source.XColumnIndex = 1; source.YColumnIndex = 2; source.ZColumnIndex = 0; source.TimeColumnIndex = 0;
            source.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 2 }); // "has a value": every row, but the filter path runs

            foreach (ComputedLayerKind kind in new[] { ComputedLayerKind.Trendline, ComputedLayerKind.Path, ComputedLayerKind.SmoothedPath, ComputedLayerKind.Mean })
            {
                foreach (bool smoothed in new[] { false, true })
                {
                    if (smoothed && kind != ComputedLayerKind.Trendline)
                        continue;
                    PlotLayer c = stack.AddComputed(source, kind);
                    c.Smoothed = smoothed;
                    AxisBounds bounds = LayerBounds.Compute(stack.Layers);
                    var watch = Stopwatch.StartNew();
                    ComputedShape s = ComputedLayerBuilder.Build(c, stack, bounds, out _, out _);
                    ComputedLayerBuilder.Signature(c, stack, bounds);
                    watch.Stop();
                    string name = "timing " + kind + (smoothed ? " smoothed" : "");
                    t.True(name + " valid", s.Valid);
                    if (watch.Elapsed.TotalSeconds < 0.5)
                        t.Pass++;
                    else
                        t.Fail(name, "took " + watch.Elapsed.TotalMilliseconds.ToString("F0") + " ms for " + n + " rows");
                    UnityEngine.Debug.Log("[ComputedLayerSelfTests] " + name + ": " + watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms for " + n + " rows");
                }
            }
        }
    }
}
