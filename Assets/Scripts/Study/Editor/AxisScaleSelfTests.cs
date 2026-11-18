using System.Collections.Generic;
using DataViz.Flow;
using DataViz.Networking;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>
    /// Spec "axis scales and real proportions" (ASR-01..14): the axis settings on the shared bounds,
    /// rows outside Limits / Zoom, computed layers cut at the walls, sync, saved views and log rows.
    /// </summary>
    public static class AxisScaleSelfTests
    {
        static LayerBuildSettings Settings() => new LayerBuildSettings
        {
            Origin = Vector3.zero, AxisLength = 1f,
            FilteredOutAlpha = 0.08f, TimeOutsideAlpha = 0.18f, TimeTrailFraction = 0.02f,
        };

        static PlotFrame Build(LayerStack stack, AxisSettings axes, out PointBuffers buffers)
        {
            var frame = new PlotFrame { Bounds = AxisScaling.Apply(LayerBounds.Compute(stack.Layers), axes), Budget = 1_000_000, StackVersion = stack.Version };
            var cache = new PlotPointCache();
            cache.Build(stack, frame, Settings());
            buffers = cache.Buffers;
            return frame;
        }

        // The architect's slab, small: X 0..6, Y 0..2 (lengths), Z 20..60 (rebound).
        static DatasetColumnar Slab() => SelfTestKit.Dataset("slab.cdataset",
            Col.Num("X", 0f, 6f, 3f, 1f), Col.Num("Y", 0f, 2f, 1f, 2f), Col.Num("Z", 20f, 60f, 40f, 30f));

        static AxisSettings Fit(float padding = 0f) => new AxisSettings { Aspect = (byte)AxisAspect.FitEach, Padding = padding };

        [SelfTest("Axes: legacy settings change nothing")]
        static void LegacyBitForBit(SelfTestTally t)
        {
            var stack = new LayerStack();
            stack.AddDataset("slab.cdataset", Slab());
            AxisBounds data = LayerBounds.Compute(stack.Layers);
            AxisBounds legacy = AxisScaling.Apply(data, AxisSettings.CreateLegacy(), out byte flat);
            t.True("legacy bounds equal the data bounds", legacy.SameAs(data));
            t.Eq("legacy: nothing flattened", 0, flat);

            var plain = new PlotFrame { Bounds = data, Budget = 1_000_000 };
            var c1 = new PlotPointCache();
            c1.Build(stack, plain, Settings());
            Build(stack, AxisSettings.CreateLegacy(), out PointBuffers b);
            bool same = c1.Buffers.Count == b.Count;
            for (int i = 0; same && i < b.Count; i++)
                same &= c1.Buffers.Positions[i] == b.Positions[i];
            t.True("legacy positions bit for bit", same);
        }

        [SelfTest("Axes: proportional slab keeps its shape")]
        static void ProportionalSlab(SelfTestTally t)
        {
            var stack = new LayerStack();
            stack.AddDataset("slab.cdataset", Slab());
            var axes = new AxisSettings { Padding = 0f, GroupMask = 0b011 }; // X and Y share a scale, Z on its own
            AxisBounds b = AxisScaling.Apply(LayerBounds.Compute(stack.Layers), axes, out byte flat);
            t.Near("x 0..6", 0f, b.X.Min); t.Near("x max", 6f, b.X.Max);
            t.Near("y centred min", -2f, b.Y.Min); t.Near("y centred max", 4f, b.Y.Max);
            t.Near("z own range min", 20f, b.Z.Min); t.Near("z own range max", 60f, b.Z.Max);
            t.Eq("slab not flattened", 0, flat);

            Build(stack, axes, out PointBuffers p);
            t.Near("y spans a third of the edge", 1f / 3f, p.Positions[1].y - p.Positions[0].y, 1e-5f);
            t.Near("y centred at 0.5", 0.5f, (p.Positions[1].y + p.Positions[0].y) * 0.5f, 1e-5f);
            t.Near("x fills the edge", 1f, p.Positions[1].x - p.Positions[0].x, 1e-5f);

            AxisBounds all = AxisScaling.Apply(LayerBounds.Compute(stack.Layers), new AxisSettings { Padding = 0f }, out flat);
            t.Near("all three grouped: x widened to z's span", 40f, all.X.Max - all.X.Min, 1e-4f);
            t.Eq("x and y flagged flattened (6/40 = 15 % is not, 2/40 = 5 % is not under)", 0, flat & 0b001);
        }

        [SelfTest("Axes: flattened axis warning")]
        static void Flattened(SelfTestTally t)
        {
            var stack = new LayerStack();
            stack.AddDataset("f.cdataset", SelfTestKit.Dataset("f.cdataset",
                Col.Num("X", 0f, 100f), Col.Num("Y", 0f, 1f), Col.Num("Z", 0f, 50f)));
            AxisScaling.Apply(LayerBounds.Compute(stack.Layers), new AxisSettings { Padding = 0f }, out byte flat);
            t.Eq("y (1 of 100) flattened, z (50 of 100) not", 0b010, flat);
            AxisScaling.Apply(LayerBounds.Compute(stack.Layers), new AxisSettings { Padding = 0f, GroupMask = 0b101 }, out flat);
            t.Eq("y out of the group: no warning", 0, flat);
        }

        [SelfTest("Axes: include zero, padding, reverse")]
        static void ZeroPaddingReverse(SelfTestTally t)
        {
            var stack = new LayerStack();
            stack.AddDataset("slab.cdataset", Slab());
            AxisBounds data = LayerBounds.Compute(stack.Layers);

            AxisSettings zero = Fit();
            zero.Modes[2] = (byte)AxisRangeMode.IncludeZero;
            AxisBounds b = AxisScaling.Apply(data, zero);
            t.Near("include zero: z from 0", 0f, b.Z.Min); t.Near("include zero: z to 60", 60f, b.Z.Max);

            b = AxisScaling.Apply(data, Fit(0.05f));
            t.Near("padding 5 %: x min", -0.3f, b.X.Min, 1e-5f); t.Near("padding 5 %: x max", 6.3f, b.X.Max, 1e-5f);

            AxisSettings rev = Fit();
            rev.ReverseMask = 0b001;
            b = AxisScaling.Apply(data, rev);
            t.True("reverse stored as Min > Max", b.X.Min == 6f && b.X.Max == 0f);
            t.Near("reverse: 0 at the far end", 1f, b.X.Normalise(0f));
            t.Near("reverse: 6 at the origin", 0f, b.X.Normalise(6f));
            t.Near("reverse: ticks read back", 6f, b.X.ValueAt(0f));
            Build(stack, rev, out PointBuffers p);
            t.Near("reversed point", 1f, p.Positions[0].x);

            // Own-scale layer: padding and Reverse only (ASR-05).
            PlotLayer own = stack.Layers[0];
            own.Normalised = true;
            AxisSettings s = Fit(0.05f);
            s.ReverseMask = 0b001;
            s.Modes[0] = (byte)AxisRangeMode.Limits; s.LimitMins[0] = 2f; s.LimitMaxs[0] = 3f;
            AxisFrame f = LayerBounds.FrameFor(own, own.Dataset, AxisScaling.Apply(LayerBounds.Compute(stack.Layers), s));
            t.True("own scale: limits ignored, padding and reverse kept", Mathf.Approximately(f.X.Min, 6.3f) && Mathf.Approximately(f.X.Max, -0.3f));
            t.True("own scale: no clip", f.X.Clip == AxisClip.None);
        }

        [SelfTest("Axes: limits drop rows, zoom hides them")]
        static void LimitsAndZoom(SelfTestTally t)
        {
            var stack = new LayerStack();
            stack.AddDataset("slab.cdataset", Slab());
            AxisSettings lim = Fit();
            lim.Modes[0] = (byte)AxisRangeMode.Limits; lim.LimitMins[0] = 0f; lim.LimitMaxs[0] = 3f;
            PlotFrame frame = Build(stack, lim, out PointBuffers b);
            LayerDrawInfo info = frame.Layers[0].Info;
            t.Eq("limits: rows kept", "0,2,3", string.Join(",", b.Rows));
            t.Eq("limits: counted", 1, info.SkippedOutOfRange);
            t.True("limits: warning", info.Warning != null && info.Warning.Contains("1 row outside the axis limits is not drawn"));
            t.Near("limits: x range is the limits", 3f, frame.Bounds.X.Max);

            AxisSettings lim5 = lim.Clone();
            lim5.Padding = 0.05f;
            frame = Build(stack, lim5, out b);
            t.Eq("limits + padding: clip range is the limits, not the padded axis", 1, frame.Layers[0].Info.SkippedOutOfRange);

            AxisSettings zoom = lim.Clone();
            zoom.Modes[0] = (byte)AxisRangeMode.Zoom;
            frame = Build(stack, zoom, out b);
            t.Eq("zoom: same points hidden", 1, frame.Layers[0].Info.SkippedOutOfRange);

            // Computed: a path through all four rows. Limits drops row 1 (x = 6) before the maths;
            // Zoom keeps it and cuts the line at the wall.
            PlotLayer source = stack.Layers[0];
            PlotLayer path = stack.AddComputed(source, ComputedLayerKind.Path);
            ComputedShape s = ComputedLayerBuilder.Build(path, stack, AxisScaling.Apply(LayerBounds.Compute(stack.Layers), lim), out _, out string status);
            t.True("limits: path valid", s.Valid);
            t.Eq("limits: path of the kept rows", 3, s.Polyline?.Length ?? 0);
            bool inside = true;
            foreach (Vector3 v in s.Polyline ?? new Vector3[0]) inside &= v.x >= 0f && v.x <= 1f;
            t.True("limits: path inside the cube", inside);

            s = ComputedLayerBuilder.Build(path, stack, AxisScaling.Apply(LayerBounds.Compute(stack.Layers), zoom), out _, out status);
            t.True("zoom: path valid", s.Valid);
            t.True("zoom: path cut into two pieces", s.Runs != null && s.Runs.Length == 2);
            bool onWall = false;
            foreach (Vector3[] run in s.Runs ?? new Vector3[0][])
                foreach (Vector3 v in run) { onWall |= Mathf.Approximately(v.x, 1f); inside &= v.x >= -1e-5f && v.x <= 1f + 1e-5f; }
            t.True("zoom: pieces end on the wall, nothing outside", onWall && inside);

            // Mean marker outside the zoom window: hidden with a status.
            AxisSettings far = Fit();
            far.Modes[2] = (byte)AxisRangeMode.Zoom; far.LimitMins[2] = 100f; far.LimitMaxs[2] = 200f;
            PlotLayer mean = stack.AddComputed(source, ComputedLayerKind.Mean);
            s = ComputedLayerBuilder.Build(mean, stack, AxisScaling.Apply(LayerBounds.Compute(stack.Layers), far), out _, out status);
            t.True("marker outside hidden", !s.Valid && status.Contains(ComputedLayerBuilder.StatusOutsideRange));
        }

        [SelfTest("Axes: manager requests and shared bounds")]
        static void ManagerRequests(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("slab.cdataset");
                t.True("new plot: default settings", m.Axes.SameAs(AxisSettings.CreateDefault()));

                int events = 0;
                System.Action count = () => events++;
                m.OnPlotSettingsChanged += count;
                m.RequestSetAxisAspectRpc(AxisAspect.FitEach);
                m.RequestSetAxisPaddingRpc(0f);
                t.Eq("two changes, two events", 2, events);
                t.True("fit: data bounds", m.SharedBounds.SameAs(AxisScaling.Apply(m.DataBounds, AxisSettings.CreateLegacy())));

                m.RequestSetAxisRangeRpc(0, AxisRangeMode.Limits, 4f, 1f);
                t.True("limits swapped", m.Axes.LimitMin(0) == 1f && m.Axes.LimitMax(0) == 4f);
                t.True("limits on the bounds", m.SharedBounds.X.Min == 1f && m.SharedBounds.X.Clip == AxisClip.Drop);

                events = 0;
                m.RequestSetAxisRangeRpc(1, AxisRangeMode.Limits, 2f, 2f);
                t.True("equal limits refused", m.Axes.Mode(1) == AxisRangeMode.Auto);

                m.RequestSetAxisReverseRpc(2, true);
                t.True("reverse on", m.Axes.Reversed(2) && m.SharedBounds.Z.Min > m.SharedBounds.Z.Max);
                m.RequestSetAxisGroupRpc(2, false);
                t.True("group mask", m.Axes.GroupMask == 0b011);

                t.True("ApplyRequest: aspect", NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.SetAxisAspect, (int)AxisAspect.Proportional)));
                t.True("aspect applied", m.Axes.AspectMode == AxisAspect.Proportional);
                var range = new PlotRequest(PlotOp.SetAxisRange, 1, (float)AxisRangeMode.Zoom);
                range.RangeMins = new[] { -1f };
                range.RangeMaxs = new[] { 5f };
                t.True("ApplyRequest: range", NetworkScatterplotState.ApplyRequest(m, range));
                t.True("zoom applied", m.Axes.Mode(1) == AxisRangeMode.Zoom && m.Axes.LimitMax(1) == 5f);

                m.RequestResetAxesRpc();
                t.True("reset", m.Axes.SameAs(AxisSettings.CreateDefault()));
                m.OnPlotSettingsChanged -= count;
            }, ("slab.cdataset", Slab()));
        }

        [SelfTest("Axes: snapshot, network and saved views")]
        static void SnapshotAndViews(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("slab.cdataset");
                m.RequestSetAxisRangeRpc(0, AxisRangeMode.Zoom, 1f, 5f);
                m.RequestSetAxisReverseRpc(1, true);
                m.RequestSetAxisPaddingRpc(0.1f);

                PlotSnapshot s = PlotSnapshot.Capture(m);
                t.True("capture carries the axes", s.Axes.SameAs(m.Axes));

                using (var writer = new FastBufferWriter(1024, Allocator.Temp, 1 << 20))
                {
                    writer.WriteNetworkSerializable(s);
                    using (var reader = new FastBufferReader(writer, Allocator.Temp))
                    {
                        reader.ReadNetworkSerializable(out PlotSnapshot copy);
                        t.True("network round trip", copy.Axes.SameAs(s.Axes) && copy.SamePlotAs(s));
                    }
                }

                PlotSnapshot changed = PlotSnapshot.Capture(m);
                changed.Axes.Aspect = (byte)AxisAspect.FitEach;
                t.True("snapshots differ on axes", !changed.SamePlotAs(s));

                // Undo-style restore.
                m.RequestResetAxesRpc();
                m.ApplyNetworkSnapshot(s, 0f);
                t.True("WriteTo restores the axes", m.Axes.SameAs(s.Axes));

                SavedView view = SavedViewStore.FromSnapshot(s, "v", f => DatasetRegistry.Get(f));
                string json = JsonUtility.ToJson(view);
                SavedView back = JsonUtility.FromJson<SavedView>(json);
                PlotSnapshot loaded = SavedViewStore.ToSnapshot(back, f => DatasetRegistry.Get(f), out string error);
                t.True("saved view round trip", error == null && loaded.Axes.SameAs(s.Axes));

                // A view saved before the feature: no HasAxes / Axes in its JSON.
                string old = json.Replace("\"HasAxes\":true,", "");
                int cut = old.IndexOf(",\"Axes\":");
                if (cut >= 0) old = old.Substring(0, cut) + "}";
                SavedView oldView = JsonUtility.FromJson<SavedView>(old);
                PlotSnapshot oldSnap = SavedViewStore.ToSnapshot(oldView, f => DatasetRegistry.Get(f), out error);
                t.True("old view loads with legacy axes", error == null && oldSnap.Axes.SameAs(AxisSettings.CreateLegacy()));
            }, ("slab.cdataset", Slab()));
        }

        [SelfTest("Axes: log rows")]
        static void LogRows(SelfTestTally t)
        {
            var before = new PlotSnapshot();
            var after = new PlotSnapshot { Axes = before.Axes.Clone() };
            after.Axes.Aspect = (byte)AxisAspect.FitEach;
            after.Axes.Modes[0] = (byte)AxisRangeMode.Limits; after.Axes.LimitMins[0] = 0f; after.Axes.LimitMaxs[0] = 6f;
            after.Axes.ReverseMask = 0b100;
            t.True("relevant change", PlotStateDiff.HasRelevantChange(before, after));

            var rows = new List<(string, string, string)>();
            PlotStateDiff.DescribeAxes(rows, before.Axes, after.Axes);
            string text = string.Join("|", rows.ConvertAll(r => r.Item1 + "(" + r.Item2 + "->" + r.Item3 + ")"));
            t.Eq("rows", "SetAxisAspect(Proportional->Fit each axis)|SetAxisRange(X: Auto->X: Limits 0..6)|SetAxisReverse(Z: False->Z: True)", text);
            t.True("plot-level", StudyActions.IsPlotLevel(StudyActions.SetAxisRange) && StudyActions.IsPlotLevel(StudyActions.ResetAxes));

            rows.Clear();
            PlotStateDiff.DescribeAxes(rows, after.Axes, AxisSettings.CreateDefault());
            t.Eq("reset is one row", "ResetAxes", rows.Count == 1 ? rows[0].Item1 : rows.Count.ToString());

            List<LayerChange> stackRows = PlotStateDiff.DescribeStack(before, after, _ => null);
            t.True("stack diff has plot-level axis rows", stackRows.Exists(r => r.Action == StudyActions.SetAxisAspect && r.Layer == ""));
        }
    }
}
