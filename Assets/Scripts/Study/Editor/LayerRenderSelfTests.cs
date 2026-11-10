using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>WP2 checks for point rendering: row skips, shared axes, budget, time behaviours, subsets, encodings, alpha-only path, pins.</summary>
    public static class LayerRenderSelfTests
    {
        // Origin 0 and axis length 1: buffer positions are unit-cube coordinates.
        static LayerBuildSettings Settings() => new LayerBuildSettings
        {
            Origin = Vector3.zero, AxisLength = 1f,
            FilteredOutAlpha = 0.08f, TimeOutsideAlpha = 0.18f, TimeTrailFraction = 0.02f,
        };

        static PlotFrame Frame(LayerStack stack, int budget = 1_000_000, float scrub = 0f)
        {
            var frame = new PlotFrame { Bounds = LayerBounds.Compute(stack.Layers), Budget = budget, StackVersion = stack.Version };
            frame.ClockActive = PlotClock.Range(stack.Layers, out frame.ClockMin, out frame.ClockMax);
            frame.ClockTime = frame.ClockActive ? PlotClock.TimeAt(scrub, frame.ClockMin, frame.ClockMax) : 0f;
            return frame;
        }

        static PlotFrame Build(LayerStack stack, out PointBuffers buffers, int budget = 1_000_000, float scrub = 0f)
        {
            var cache = new PlotPointCache();
            PlotFrame frame = Frame(stack, budget, scrub);
            cache.Build(stack, frame, Settings());
            buffers = cache.Buffers;
            return frame;
        }

        static List<T> Slice<T>(List<T> list, LayerFrame f) => list.GetRange(f.BufferStart, f.BufferCount);

        static string Join<T>(IEnumerable<T> values) => string.Join(",", values);

        static string Masks(List<bool> mask) => string.Join("", mask.ConvertAll(b => b ? "T" : "F"));

        static float?[] Ramp(int n, float scale = 1f)
        {
            var v = new float?[n];
            for (int i = 0; i < n; i++)
                v[i] = i * scale;
            return v;
        }

        // ---- LFR-11 -------------------------------------------------------------------------

        [SelfTest("Render: valid-row ranges are the default")]
        static void ValidRowsDefault(SelfTestTally t)
        {
            t.True("ColumnRange.ValidRowsOnly default", ColumnRange.ValidRowsOnly);
        }

        [SelfTest("Render: empty axis cell skipped and counted")]
        static void EmptyAxisCell(SelfTestTally t)
        {
            DatasetColumnar ds = SelfTestKit.Dataset("a.cdataset",
                Col.Num("X", 1f, null, 3f), Col.Num("Y", 1f, 2f, 3f), Col.Num("Z", 4f, 5f, 6f));
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("a.cdataset", ds);

            PlotFrame frame = Build(stack, out PointBuffers b);
            LayerDrawInfo info = frame.Layers[0].Info;
            t.Eq("axis skip drawn", 2, info.Drawn);
            t.Eq("axis skip rows", "0,2", Join(b.Rows));
            t.Eq("axis skip count", 1, info.SkippedNoAxisValue);
            t.Eq("axis skip drawable", 2, info.Drawable);
            t.True("axis skip warning", info.Warning != null && info.Warning.Contains("a: 1 row has no value in X and is not drawn"));
            t.True("axis range from valid rows", frame.Bounds.X.Min == 1f && frame.Bounds.X.Max == 3f);
            t.Near("smallest valid value at 0 (range not stretched to the placeholder 0)", 0f, b.Positions[0].x);

            var drawable = new List<int>();
            LayerRows.Drawable(a, stack, drawable);
            t.Eq("same rows as LayerRows.Drawable", Join(drawable), Join(b.Rows));
        }

        [SelfTest("Render: empty colour or size cell skipped and counted")]
        static void EmptyEncodingCell(SelfTestTally t)
        {
            DatasetColumnar ds = SelfTestKit.Dataset("c.cdataset",
                Col.Num("X", 1f, 2f, 3f, 4f), Col.Num("Y", 1f, 2f, 3f, 4f), Col.Num("Z", 1f, 2f, 3f, 4f),
                Col.Num("C", 5f, null, 7f, 9f), Col.Num("S", 2f, 4f, null, 6f));
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("c.cdataset", ds);
            a.ColorColumnIndex = 3;

            PlotFrame frame = Build(stack, out PointBuffers b);
            t.Eq("colour skip rows", "0,2,3", Join(b.Rows));
            t.Eq("colour skip count", 1, frame.Layers[0].Info.SkippedNoEncodingValue);
            t.Eq("colour skip no axis skip", 0, frame.Layers[0].Info.SkippedNoAxisValue);
            t.True("colour skip warning", frame.Layers[0].Info.Warning != null && frame.Layers[0].Info.Warning.Contains("no value in C"));
            t.True("colour scale from valid rows (5 → blue)", b.Colors[0].b > 0.99f && b.Colors[0].r < 0.01f);

            a.SizeMapped = true;
            a.SizeColumnIndex = 4;
            frame = Build(stack, out b);
            t.Eq("colour + size skip rows", "0,3", Join(b.Rows));
            t.Eq("colour + size skip count", 2, frame.Layers[0].Info.SkippedNoEncodingValue);
            t.True("size warning", frame.Layers[0].Info.Warning.Contains("no value in S"));
            t.Near("size smallest valid value", a.PointSize * MultiplayerScatterplotManager.SizeMappedMinScale, b.Sizes[0]);
            t.Near("size largest valid value", a.PointSize * MultiplayerScatterplotManager.SizeMappedMaxScale, b.Sizes[1]);

            a.SizeMapped = false; // fixed size: an empty size cell no longer matters
            frame = Build(stack, out b);
            t.Eq("fixed size keeps row", "0,2,3", Join(b.Rows));
        }

        // ---- LFR-08, 09, 10 ------------------------------------------------------------------

        static LayerStack TwoLayers(out PlotLayer a, out PlotLayer m)
        {
            DatasetColumnar ds1 = SelfTestKit.Dataset("one.cdataset",
                Col.Num("X", 0f, 10f, 4f), Col.Num("Y", 0f, 1f, 2f), Col.Num("Z", 0f, 1f, 2f));
            DatasetColumnar ds2 = SelfTestKit.Dataset("two.cdataset",
                Col.Num("X", 5f, 10f, 20f), Col.Num("Y", 3f, 1f, 2f), Col.Num("Z", 0f, 1f, 2f));
            var stack = new LayerStack();
            a = stack.AddDataset("one.cdataset", ds1);
            m = stack.AddDataset("two.cdataset", ds2);
            return stack;
        }

        [SelfTest("Render: shared bounds place equal values together")]
        static void SharedBounds(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            PlotFrame frame = Build(stack, out PointBuffers b);
            LayerFrame fa = frame.Layers[0], fm = frame.Layers[1];
            t.True("shared x range 0..20", frame.Bounds.X.Min == 0f && frame.Bounds.X.Max == 20f);
            t.Eq("slices", "0+3,3+3", fa.BufferStart + "+" + fa.BufferCount + "," + fm.BufferStart + "+" + fm.BufferCount);
            t.Near("value 10 in layer 1", 0.5f, b.Positions[fa.BufferStart + 1].x);
            t.Near("value 10 in layer 2", 0.5f, b.Positions[fm.BufferStart + 1].x);
            t.True("same cube x", b.Positions[fa.BufferStart + 1].x == b.Positions[fm.BufferStart + 1].x);
            t.True("frame of point", frame.FrameOfPoint(4) == fm && frame.FrameOfPoint(0) == fa);
        }

        [SelfTest("Render: hiding a layer keeps the other in place")]
        static void HideKeepsPositions(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            Build(stack, out PointBuffers both);
            List<Vector3> before = both.Positions.GetRange(0, 3);

            m.Visible = false;
            PlotFrame frame = Build(stack, out PointBuffers hidden);
            t.Eq("hidden layer draws nothing", 0, frame.Layers[1].BufferCount);
            t.Eq("visible layer count", 3, hidden.Count);
            bool same = true;
            for (int i = 0; i < 3; i++)
                same &= hidden.Positions[i] == before[i];
            t.True("positions unchanged", same);
            t.True("bounds unchanged", frame.Bounds.X.Max == 20f);
        }

        [SelfTest("Render: normalised layer spans the axes")]
        static void NormalisedSpans(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            m.Normalised = true;
            PlotFrame frame = Build(stack, out PointBuffers b);
            List<Vector3> pos = Slice(b.Positions, frame.Layers[1]);
            t.Near("normalised min x", 0f, pos[0].x);
            t.Near("normalised max x", 1f, pos[2].x);
            t.Near("normalised max y", 1f, pos[0].y);
            t.True("aligned layer now alone on x", frame.Bounds.X.Max == 10f);
            t.Near("aligned layer x by its own data", 1f, b.Positions[1].x);
        }

        // ---- LFR-06 -------------------------------------------------------------------------

        [SelfTest("Render: point budget down-samples the layer that goes over")]
        static void Budget(SelfTestTally t)
        {
            DatasetColumnar ds1 = SelfTestKit.Dataset("b1.cdataset", Col.Num("X", Ramp(600)), Col.Num("Y", Ramp(600)), Col.Num("Z", Ramp(600)));
            DatasetColumnar ds2 = SelfTestKit.Dataset("b2.cdataset", Col.Num("X", Ramp(600)), Col.Num("Y", Ramp(600)), Col.Num("Z", Ramp(600)));
            var stack = new LayerStack();
            stack.AddDataset("b1.cdataset", ds1);
            PlotLayer second = stack.AddDataset("b2.cdataset", ds2);

            PlotFrame frame = Build(stack, out PointBuffers b, budget: 1000);
            LayerDrawInfo i1 = frame.Layers[0].Info, i2 = frame.Layers[1].Info;
            t.Eq("first layer whole", 600, i1.Drawn);
            t.Eq("first layer stride", 1, i1.Stride);
            t.True("first layer no warning", i1.Warning == null);
            t.Eq("second stride", 2, i2.Stride);
            t.Eq("second drawn", 300, i2.Drawn);
            t.Eq("second drawable", 600, i2.Drawable);
            t.Eq("total", 900, frame.TotalPoints);
            t.True("second warning", i2.Warning != null && i2.Warning.Contains("b2: showing 1 of every 2 points (300 of 600)"));
            t.Eq("every second row", "0,2,4", Join(b.Rows.GetRange(600, 3)));

            frame = Build(stack, out b, budget: 600);
            t.Eq("used up drawn", 0, frame.Layers[1].Info.Drawn);
            t.True("used up warning", frame.Layers[1].Info.Warning != null && frame.Layers[1].Info.Warning.Contains("not drawn, the point budget is used up"));

            second.Visible = false;
            frame = Build(stack, out b, budget: 600);
            t.True("hidden layer uses no budget and gets no warning", frame.Layers[1].Info.Warning == null && frame.TotalPoints == 600);
        }

        // ---- LFR-23, 24 ---------------------------------------------------------------------

        [SelfTest("Render: Follow, Always and Span time behaviours")]
        static void TimeBehaviours(SelfTestTally t)
        {
            DatasetColumnar ds = SelfTestKit.Dataset("t.cdataset",
                Col.Num("X", 0f, 1f, 2f, 3f, 4f), Col.Num("Y", 0f, 1f, 2f, 3f, 4f), Col.Num("Z", 0f, 1f, 2f, 3f, 4f),
                Col.Num("Time", 0f, 1f, 2f, 3f, 4f), Col.Num("End", 2f, 1f, null, 4f, 4f));
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("t.cdataset", ds);
            a.TimeColumnIndex = 3;
            a.EndTimeColumnIndex = 4;

            PlotFrame frame = Build(stack, out PointBuffers b, scrub: 0.5f);
            t.Near("clock time", 2f, frame.ClockTime);
            t.Eq("follow mask", "FFTFF", Masks(b.InTimeWindow));
            t.Near("follow outside alpha", 0.18f, b.Colors[0].a);
            t.Near("follow inside alpha", 1f, b.Colors[2].a);

            a.TimeBehaviour = LayerTimeBehaviour.Always;
            Build(stack, out b, scrub: 0.5f);
            t.Eq("always mask", "TTTTT", Masks(b.InTimeWindow));

            a.TimeBehaviour = LayerTimeBehaviour.Span;
            Build(stack, out b, scrub: 0.5f);
            t.Eq("span mask (start <= t <= end, no end = open)", "TFTFF", Masks(b.InTimeWindow));
            t.Near("span outside fades like follow", 0.18f, b.Colors[1].a);

            a.EndTimeColumnIndex = -1;
            Build(stack, out b, scrub: 0.5f);
            t.Eq("span without end column", "TTTFF", Masks(b.InTimeWindow));

            a.TimeColumnIndex = -1;
            Build(stack, out b, scrub: 0.5f);
            t.Eq("no time column = always", "TTTTT", Masks(b.InTimeWindow));
        }

        [SelfTest("Render: alpha-only path matches a full build")]
        static void AlphaOnlyPath(SelfTestTally t)
        {
            DatasetColumnar ds = SelfTestKit.Dataset("r.cdataset",
                Col.Num("X", Ramp(50)), Col.Num("Y", Ramp(50)), Col.Num("Z", Ramp(50)),
                Col.Num("Time", Ramp(50, 0.37f)), Col.Cat("Kind", Labels(50)));
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("r.cdataset", ds);
            a.TimeColumnIndex = 3;
            a.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 4, Label = "even" });
            PlotLayer s = stack.AddSubset(a, 4, "odd");
            s.TimeBehaviour = LayerTimeBehaviour.Span;

            var cache = new PlotPointCache();
            cache.Build(stack, Frame(stack, scrub: 0.2f), Settings());
            t.True("first build is full", !cache.LastBuildRetimed);

            PlotFrame later = Frame(stack, scrub: 0.7f);
            cache.Build(stack, later, Settings());
            t.True("clock only: retimed", cache.LastBuildRetimed);
            Build(stack, out PointBuffers fresh, scrub: 0.7f);
            t.True("retimed equals fresh (clock)", SameBuffers(cache.Buffers, fresh));
            t.Eq("retimed frame keeps slices", 25, later.Layers[1].BufferCount);

            a.Opacity = 0.5f;
            s.TimeBehaviour = LayerTimeBehaviour.Follow;
            cache.Build(stack, Frame(stack, scrub: 0.7f), Settings());
            t.True("opacity/time behaviour only: retimed", cache.LastBuildRetimed);
            Build(stack, out fresh, scrub: 0.7f);
            t.True("retimed equals fresh (opacity, behaviour)", SameBuffers(cache.Buffers, fresh));

            a.XColumnIndex = 1;
            cache.Build(stack, Frame(stack, scrub: 0.7f), Settings());
            t.True("mapping change: full build", !cache.LastBuildRetimed);
        }

        static string[] Labels(int n)
        {
            var labels = new string[n];
            for (int i = 0; i < n; i++)
                labels[i] = i % 2 == 0 ? "even" : "odd";
            return labels;
        }

        static bool SameBuffers(PointBuffers x, PointBuffers y)
        {
            if (x.Count != y.Count)
                return false;
            for (int i = 0; i < x.Count; i++)
            {
                if (x.Positions[i] != y.Positions[i] || x.Colors[i] != y.Colors[i] || x.InTimeWindow[i] != y.InTimeWindow[i] ||
                    x.Rows[i] != y.Rows[i] || x.Glyphs[i] != y.Glyphs[i] || !x.Sizes[i].Equals(y.Sizes[i]))
                    return false;
            }
            return true;
        }

        // ---- LFR-03, 13 to 16 ---------------------------------------------------------------

        [SelfTest("Render: subset members and hide in source")]
        static void SubsetAndHide(SelfTestTally t)
        {
            DatasetColumnar ds = SelfTestKit.Dataset("s.cdataset",
                Col.Num("X", 0f, 1f, 2f, 3f, 4f), Col.Num("Y", 0f, 1f, 2f, 3f, 4f), Col.Num("Z", 0f, 1f, 2f, 3f, 4f),
                Col.Cat("Phase", "a", "b", "a", "c", "b"));
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("s.cdataset", ds);
            PlotLayer s = stack.AddSubset(a, 3, "b");

            PlotFrame frame = Build(stack, out PointBuffers b);
            t.Eq("source draws all", "0,1,2,3,4", Join(Slice(b.Rows, frame.Layers[0])));
            t.Eq("subset draws members only", "1,4", Join(Slice(b.Rows, frame.Layers[1])));
            t.True("subset members not dimmed", Slice(b.Colors, frame.Layers[1]).TrueForAll(c => c.a == 1f));

            s.HiddenInSource = true;
            frame = Build(stack, out b);
            t.Eq("hide in source", "0,2,3", Join(Slice(b.Rows, frame.Layers[0])));
            t.Eq("subset unchanged", "1,4", Join(Slice(b.Rows, frame.Layers[1])));

            s.Visible = false;
            frame = Build(stack, out b);
            t.Eq("hidden subset still hides in source", "0,2,3", Join(Slice(b.Rows, frame.Layers[0])));

            // A dataset layer's own filter still dims instead of removing.
            s.HiddenInSource = false;
            a.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 3, Label = "a" });
            frame = Build(stack, out b);
            t.Eq("dataset filter keeps rows", 5, frame.Layers[0].BufferCount);
            t.Near("dataset filter dims", 0.08f, b.Colors[1].a);
        }

        [SelfTest("Render: fixed colour, opacity and default glyph")]
        static void LayerEncodings(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            m.FixedColour = new Color32(255, 0, 0, 255);
            m.Opacity = 0.5f;
            m.DefaultGlyph = 3;
            m.PointSize = 0.05f;

            PlotFrame frame = Build(stack, out PointBuffers b);
            int first = frame.Layers[1].BufferStart;
            t.True("fixed colour", b.Colors[first].r == 1f && b.Colors[first].g == 0f && b.Colors[first].b == 0f);
            t.Near("opacity", 0.5f, b.Colors[first].a);
            t.Eq("default glyph", 3, b.Glyphs[first]);
            t.Near("own size", 0.05f, b.Sizes[first]);
            t.True("other layer untouched (cyan, opaque, glyph 0)", b.Colors[0] == new Color(0f, 1f, 1f, 1f) && b.Glyphs[0] == 0);

            m.GlyphColumnIndex = 0;
            m.GlyphRanges.Add(new MultiplayerScatterplotManager.GlyphRange { glyphIndex = 5, min = 9f, max = 11f });
            frame = Build(stack, out b);
            t.Eq("glyph range wins, default elsewhere", "3,5,3", Join(Slice(b.Glyphs, frame.Layers[1])));
        }

        [SelfTest("Render: unavailable and computed layers draw no points")]
        static void SkippedLayers(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            stack.AddComputed(a, ComputedLayerKind.Mean);
            m.LocalUnavailable = true;
            PlotFrame frame = Build(stack, out PointBuffers b);
            t.Eq("frame has every layer", 3, frame.Layers.Count);
            t.Eq("unavailable slice empty", 0, frame.Layers[1].BufferCount);
            t.Eq("computed slice empty", 0, frame.Layers[2].BufferCount);
            t.True("computed frame dataset is its source's", ReferenceEquals(frame.Layers[2].Dataset, a.Dataset));
            t.Eq("only the available layer drawn", 3, b.Count);
            t.True("unavailable layer leaves the bounds", frame.Bounds.X.Max == 10f);
        }

        // ---- LFR-07 -------------------------------------------------------------------------

        [SelfTest("Render: axis titles")]
        static void AxisTitles(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            a.Name = "movement";
            m.Name = "moles";
            AxisBounds bounds = LayerBounds.Compute(stack.Layers);
            string range = $"[{0f:F1} to {20f:F1}]";

            t.Eq("same column one line", "X  " + range, ScatterplotVisualizer.AxisTitle(stack.Layers, 0, bounds.X, out int lines));
            t.Eq("same column line count", 1, lines);

            m.Normalised = true;
            bounds = LayerBounds.Compute(stack.Layers);
            t.Eq("one line per layer", "movement: X\nmoles: X (normalised)\n" + $"[{0f:F1} to {10f:F1}]",
                ScatterplotVisualizer.AxisTitle(stack.Layers, 0, bounds.X, out lines));
            t.Eq("per-layer line count", 3, lines);

            a.Visible = false;
            m.Visible = false;
            t.True("no visible layer no title", ScatterplotVisualizer.AxisTitle(stack.Layers, 0, bounds.X, out _) == null);
        }

        // ---- LFR-19 / pinned points ---------------------------------------------------------

        [SelfTest("Render: pinned point lookup by layer id")]
        static void PinLookup(SelfTestTally t)
        {
            LayerStack stack = TwoLayers(out PlotLayer a, out PlotLayer m);
            PlotFrame frame = Build(stack, out PointBuffers b);

            GameObject go = EditorUtility.CreateGameObjectWithHideFlags("SelfTest Points", HideFlags.HideAndDontSave);
            try
            {
                var points = go.AddComponent<GPUPointInteractable>();
                points.SetPointData(b.Positions, b.Colors, 0.02f, b.Rows, frame, null);
                t.True("row 1 of moles", points.TryGetRowLocalPosition(m.Id, 1, out Vector3 p) && p == b.Positions[4]);
                t.True("row 1 of movement", points.TryGetRowLocalPosition(a.Id, 1, out p) && p == b.Positions[1]);
                t.True("unknown layer", !points.TryGetRowLocalPosition("L99", 1, out _));

                // In-window subset only (as during playback): hover point i is buffer index map[i].
                var map = new List<int> { 1, 5 };
                var hoverPositions = new List<Vector3> { b.Positions[1], b.Positions[5] };
                var hoverRows = new List<int> { b.Rows[1], b.Rows[5] };
                points.SetPointData(hoverPositions, null, 0.02f, hoverRows, frame, map);
                t.True("subset: row 2 of moles", points.TryGetRowLocalPosition(m.Id, 2, out p) && p == b.Positions[5]);
                t.True("subset: row 0 of moles not hoverable", !points.TryGetRowLocalPosition(m.Id, 0, out _));
                t.Eq("nothing pinned", -1, points.PinnedRowIndex);
                t.Eq("nothing pinned layer", "", points.PinnedLayerId);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
