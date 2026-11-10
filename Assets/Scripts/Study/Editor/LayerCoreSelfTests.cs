using System;
using System.Collections.Generic;
using DataViz.Networking;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>WP1 checks for the layer core: stack, snapshot, wire format, bounds, clock, rows, scope, events.</summary>
    public static class LayerCoreSelfTests
    {
        static DatasetColumnar Movement() => SelfTestKit.Dataset("movement.cdataset",
            Col.Num("Time", 0f, 1f, 2f, 3f, 4f),
            Col.Num("X", 0f, 2f, 4f, 6f, 8f),
            Col.Num("Y", 10f, 11f, 12f, 13f, 14f),
            Col.Num("Z", -1f, 0f, 1f, 2f, 3f),
            Col.Cat("Phase", "a", "b", "a", "c", "b"));

        static DatasetColumnar Moles() => SelfTestKit.Dataset("moles.cdataset",
            Col.Num("X", 5f, 20f, 7f),
            Col.Num("Y", 9f, 15f, 12f),
            Col.Num("SpawnTime", 1f, 6f, null),
            Col.Num("Z", 0f, 1f, 2f));

        // ---- LayerStack ---------------------------------------------------------------------

        [SelfTest("LayerStack add/duplicate/remove/move")]
        static void StackBasics(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("movement.cdataset", mv);
            t.Eq("stack first id", "L1", a.Id);
            t.Eq("stack first active", "L1", stack.ActiveLayerId);
            t.Eq("stack first name", "movement", a.Name);
            t.Eq("stack first mapping x", 0, a.XColumnIndex);

            a.XColumnIndex = 1; a.YColumnIndex = 2; a.ZColumnIndex = 3; a.TimeColumnIndex = 0; a.PointSize = 0.03f;
            PlotLayer b = stack.AddDataset("moles.cdataset", ml);
            t.Eq("stack second id", "L2", b.Id);
            t.Eq("stack second active", "L2", stack.ActiveLayerId);
            t.Eq("stack same-name x", 0, b.XColumnIndex);   // "X"
            t.Eq("stack same-name y", 1, b.YColumnIndex);   // "Y"
            t.Eq("stack same-name z", 3, b.ZColumnIndex);   // "Z"
            t.Eq("stack time not matched", -1, b.TimeColumnIndex); // moles has no "Time"
            t.Near("stack point size copied", 0.03f, b.PointSize);

            PlotLayer copy = stack.Duplicate("L1");
            t.Eq("dup id", "L3", copy.Id);
            t.Eq("dup position", 1, stack.IndexOf("L3"));
            t.Eq("dup name", "movement copy", copy.Name);
            t.True("dup deep filters", !ReferenceEquals(copy.Filters, a.Filters));
            t.True("dup shares dataset", ReferenceEquals(copy.Dataset, a.Dataset));

            PlotLayer trend = stack.AddComputed(a, ComputedLayerKind.Trendline);
            t.Eq("computed id", "L4", trend.Id);
            t.Eq("computed name", "Trendline of movement", trend.Name);
            t.True("computed no dataset yet", trend.Dataset == null);

            stack.SetActive("L1");
            int version = stack.Version;
            t.True("remove ok", stack.Remove("L1"));
            t.True("remove bumps version", stack.Version > version);
            t.Eq("remove active falls to next when first", "L3", stack.ActiveLayerId);
            t.Eq("frozen dataset name", "movement.cdataset", trend.DatasetName);
            t.True("frozen dataset", ReferenceEquals(trend.Dataset, mv));
            t.Eq("frozen x", 1, trend.XColumnIndex);
            t.Eq("frozen time", 0, trend.TimeColumnIndex);
            t.True("source missing", stack.Find(trend.SourceLayerId) == null);

            stack.SetActive("L2");
            stack.Remove("L2");
            t.Eq("remove active falls to previous", "L3", stack.ActiveLayerId);

            t.True("move ok", stack.Move("L4", 0));
            t.Eq("move order", "L4,L3", string.Join(",", stack.Layers.ConvertAll(l => l.Id)));
            t.True("move same index false", !stack.Move("L4", 0));

            PlotLayer again = stack.AddDataset("movement.cdataset", mv);
            t.Eq("ids not reused", "L5", again.Id);

            stack.Remove("L4"); stack.Remove("L3"); stack.Remove("L5");
            t.Eq("empty count", 0, stack.Count);
            t.Eq("empty active", "", stack.ActiveLayerId);
            t.True("empty active null", stack.Active == null);
            t.Eq("ids still not reused", "L6", stack.AddDataset("moles.cdataset", ml).Id);
        }

        [SelfTest("LayerStack subset")]
        static void StackSubset(SelfTestTally t)
        {
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("movement.cdataset", Movement());
            PlotLayer s = stack.AddSubset(a, 4, "b");
            t.Eq("subset kind", (int)LayerKind.Subset, (int)s.Kind);
            t.Eq("subset source", "L1", s.SourceLayerId);
            t.Eq("subset filter", "4:b", s.Filters.Count == 1 ? s.Filters[0].ColumnIndex + ":" + s.Filters[0].Label : "?");
            t.Eq("subset name", "movement: b", s.Name);
            t.True("subset of computed refused", stack.AddSubset(stack.AddComputed(a, ComputedLayerKind.Mean), 0, "") == null);

            var rows = new List<int>();
            LayerRows.Drawable(s, stack, rows);
            t.Eq("subset drawable rows", "1,4", string.Join(",", rows));
            LayerRows.Drawable(a, stack, rows);
            t.Eq("source keeps rows", 5, rows.Count);
            int sigBefore = LayerRows.InputSignature(a, stack);
            s.HiddenInSource = true;
            LayerRows.Drawable(a, stack, rows);
            t.Eq("hide in source", "0,2,3", string.Join(",", rows));
            t.True("hide changes signature", sigBefore != LayerRows.InputSignature(a, stack));
        }

        // ---- PlotSnapshot ---------------------------------------------------------------------

        [SelfTest("PlotSnapshot flat properties")]
        static void SnapshotFlat(SelfTestTally t)
        {
            var empty = new PlotSnapshot();
            t.Eq("empty layers", 0, empty.Layers.Length);
            t.Eq("empty x", 0, empty.XColumn);
            t.Eq("empty colour", -1, empty.ColourColumn);
            t.Eq("empty dataset", "", empty.DatasetName);
            t.Eq("empty filters", 0, empty.FilterColumns.Length);
            t.Eq("empty still empty after reads", 0, empty.Layers.Length);

            var s = new PlotSnapshot { XColumn = 3 };
            t.Eq("flat set creates one layer", 1, s.Layers.Length);
            t.Eq("flat set id", "L1", s.Layers[0].Id);
            t.Eq("flat set active", "L1", s.ActiveLayerId);
            t.Eq("flat set value", 3, s.Layers[0].XColumn);
            s.FilterColumns = new[] { 2 };
            t.Eq("flat set same layer", 1, s.Layers.Length);
            t.Eq("flat read back", 2, s.FilterColumns[0]);
        }

        [SelfTest("PlotSnapshot Capture/WriteTo round trip")]
        static void SnapshotRoundTrip(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("movement.cdataset");
                t.Eq("rt one layer", 1, m.Layers.Count);
                t.True("rt dataset loaded", ReferenceEquals(m.LoadedDataset, mv));
                m.RequestColorColumnRpc(4);
                PlotSnapshot one = PlotSnapshot.Capture(m);

                SelfTestKit.WithManager(m2 =>
                {
                    m2.ApplyNetworkSnapshot(one, one.TimeScrub);
                    t.True("rt 1 layer same", one.SamePlotAs(PlotSnapshot.Capture(m2)));
                    t.True("rt 1 layer dataset resolved", ReferenceEquals(m2.LoadedDataset, mv));
                }, ("movement.cdataset", mv));

                m.RequestAddDatasetLayerRpc("moles.cdataset");
                m.RequestAddComputedLayerRpc("L1", ComputedLayerKind.Trendline);
                m.RequestSetLayerOpacityRpc("L2", 0.5f);
                m.RequestSetLayerColourRpc("L2", new Color32(10, 20, 30, 255));
                m.RequestSetLayerNormalisedRpc("L2", true);
                using (m.EditLayerScope("L2"))
                    m.RequestAddFilterColumnRpc(0, "");
                m.RequestSetActiveLayerRpc("L2");
                PlotSnapshot three = PlotSnapshot.Capture(m);
                t.Eq("rt three layers", 3, three.Layers.Length);
                t.Eq("rt active", "L2", three.ActiveLayerId);
                t.Eq("rt next number", 4, three.NextLayerNumber);

                SelfTestKit.WithManager(m3 =>
                {
                    m3.ApplyNetworkSnapshot(three, 0.25f);
                    PlotSnapshot back = PlotSnapshot.Capture(m3);
                    t.True("rt 3 layers same", three.SamePlotAs(back));
                    t.Eq("rt 3 active id", "L2", m3.ActiveLayerId);
                    t.True("rt 3 moles resolved", ReferenceEquals(m3.LoadedDataset, ml));
                    t.Near("rt 3 scrub", 0.25f, m3.TimeScrub);
                    t.Eq("rt 3 new id after restore", "L4", m3.Stack.NewId());
                }, ("movement.cdataset", mv), ("moles.cdataset", ml));

                // A layer whose dataset cannot be resolved is kept and marked unavailable.
                SelfTestKit.WithManager(m4 =>
                {
                    m4.ApplyNetworkSnapshot(three, 0f);
                    PlotLayer moles = m4.Stack.Find("L2");
                    t.True("rt missing dataset kept", moles != null);
                    t.True("rt missing dataset unavailable", moles != null && moles.LocalUnavailable && moles.Dataset == null);
                }, ("movement.cdataset", mv));
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));
        }

        [SelfTest("LayerSnapshot / PlotSnapshot / PlotRequest wire round trip")]
        static void WireRoundTrip(SelfTestTally t)
        {
            var layer = new PlotLayer
            {
                Id = "L7", Name = "Moles \"x\"", Kind = LayerKind.Subset, Visible = false, DatasetName = "moles.cdataset",
                SourceLayerId = "L1", XColumnIndex = 2, YColumnIndex = 0, ZColumnIndex = 1, Normalised = true,
                ColorColumnIndex = 3, FixedColour = new Color32(1, 2, 3, 4), PointSize = 0.04f, SizeMapped = true,
                SizeColumnIndex = 2, Opacity = 0.6f, DefaultGlyph = 3, GlyphColumnIndex = 1, TimeColumnIndex = 2,
                TimeBehaviour = LayerTimeBehaviour.Span, EndTimeColumnIndex = 3, LabelColumnIndex = 0, HiddenInSource = true,
                ComputedKind = ComputedLayerKind.Path, DriverAxis = 1, OrderColumnIndex = 2, Smoothed = true,
                RefValue = 1.5f, SmoothSpan = 0.2f, LineWidth = 0.01f,
            };
            layer.GlyphAssignments.Add(new MultiplayerScatterplotManager.GlyphAssignment { Label = "a", GlyphIndex = 2 });
            layer.GlyphRanges.Add(new MultiplayerScatterplotManager.GlyphRange { glyphIndex = 4, min = 1f, max = 2f });
            var filter = new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 1, Label = "" };
            filter.Ranges.Add(new MultiplayerScatterplotManager.FilterRange { min = 0.5f, max = 0.75f });
            layer.Filters.Add(filter);

            LayerSnapshot ls = LayerSnapshot.From(layer);
            ls.DatasetHash = "ABC";
            LayerSnapshot back = Roundtrip(ls);
            t.True("wire layer same", back != null && ls.SameAs(back));
            t.True("wire layer to PlotLayer", LayerSnapshot.From(back.ToLayer()).Kind == ls.Kind &&
                                             LayerSnapshot.From(back.ToLayer()).FilterRangeMins.Length == 1);
            t.Eq("wire colour", "1,2,3,4", ColourText(back.ToLayer().FixedColour));

            var snap = new PlotSnapshot
            {
                Layers = new[] { ls, new LayerSnapshot { Id = "L8", Name = "Other", Kind = (byte)LayerKind.Computed } },
                ActiveLayerId = "L8", NextLayerNumber = 9, Published = true,
                BoundsMin = new Vector3(1, 2, 3), BoundsMax = new Vector3(4, 5, 6), BoundsValid = 5,
                HasTimeColumn = true, IsPlaying = true, PlaybackSpeed = 0.25f, PlaybackRate = 2f,
                ScrubPerSecond = 0.1f, TimeScrub = 0.3f, ScrubServerTime = 12.5,
            };
            PlotSnapshot snapBack = Roundtrip(snap);
            t.True("wire snapshot equal", snapBack != null && snap.Equals(snapBack));

            var request = new PlotRequest(PlotOp.SetLayerOpacity, 7, 0.5f, true, "txt", "L3");
            PlotRequest requestBack = Roundtrip(request);
            t.Eq("wire request", "SetLayerOpacity 7 0.5 True txt L3",
                $"{requestBack.Op} {requestBack.Int} {requestBack.Float.ToString(System.Globalization.CultureInfo.InvariantCulture)} {requestBack.Bool} {requestBack.Text} {requestBack.Layer}");
            t.Eq("shuffle layer ids", "L1|L3", string.Join("|", PlotRequest.ForShuffle(0, new string[0], "L1,L3").LayerIds));
        }

        static T Roundtrip<T>(T value) where T : INetworkSerializable, new()
        {
            using var writer = new FastBufferWriter(256, Allocator.Temp, 1 << 20);
            writer.WriteNetworkSerializable(value);
            using var reader = new FastBufferReader(writer, Allocator.Temp);
            reader.ReadNetworkSerializable(out T result);
            return result;
        }

        static string ColourText(Color32 c) => c.r + "," + c.g + "," + c.b + "," + c.a;

        // ---- bounds, clock, rows ------------------------------------------------------------

        [SelfTest("LayerBounds and PlotClock")]
        static void BoundsAndClock(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("movement.cdataset", mv);
            a.XColumnIndex = 1; a.YColumnIndex = 2; a.ZColumnIndex = 4; a.TimeColumnIndex = 0;

            AxisBounds one = LayerBounds.Compute(stack.Layers);
            t.True("bounds one x = column range", one.X.Mapped && one.X.Min == mv.Columns[1].MinValue && one.X.Max == mv.Columns[1].MaxValue);
            t.True("bounds one categorical z", one.Z.Mapped && one.Z.Categorical && string.Join(",", one.Z.Categories) == "a,b,c");
            t.True("clock one = time range", PlotClock.Range(stack.Layers, out float c0, out float c1) && c0 == 0f && c1 == 4f);
            t.Near("clock time", 2f, PlotClock.TimeAt(0.5f, c0, c1));

            // The frame of a lone aligned layer reproduces DatasetColumnar.GetNormalizedValue exactly.
            AxisFrame frame = LayerBounds.FrameFor(a, mv, one);
            bool exact = true;
            for (int r = 0; r < mv.RowCount; r++)
            {
                exact &= frame.X.Normalise(mv.GetNumericValue(r, 1)) == mv.GetNormalizedValue(r, 1);
                exact &= frame.Z.NormaliseCategoryIndex(mv.GetCategoryIndex(r, 4)) == mv.GetNormalizedValue(r, 4);
            }
            t.True("frame matches GetNormalizedValue", exact);

            PlotLayer b = stack.AddDataset("moles.cdataset", ml);
            b.XColumnIndex = 0; b.YColumnIndex = 1; b.ZColumnIndex = 3; b.TimeColumnIndex = 2;
            AxisBounds two = LayerBounds.Compute(stack.Layers);
            t.True("bounds union x", two.X.Min == 0f && two.X.Max == 20f);
            t.True("bounds union y", two.Y.Min == 9f && two.Y.Max == 15f);
            t.True("bounds categorical vs numeric: first kind wins", two.Z.Categorical);
            t.True("clock union", PlotClock.Range(stack.Layers, out c0, out c1) && c0 == 0f && c1 == 6f);

            b.Visible = false;
            t.True("hidden layer keeps bounds", LayerBounds.Compute(stack.Layers).X.Max == 20f);
            b.Visible = true;
            b.Normalised = true;
            t.True("normalised layer leaves bounds", LayerBounds.Compute(stack.Layers).X.Max == 8f);
            AxisFrame own = LayerBounds.FrameFor(b, ml, LayerBounds.Compute(stack.Layers));
            t.True("normalised frame own range", own.X.Min == 5f && own.X.Max == 20f);

            t.True("axis title differs", string.Join("|", LayerBounds.AxisTitleLines(stack.Layers, 0)) == "movement: X|moles: X (normalised)");
            b.Normalised = false;
            t.True("axis title same", string.Join("|", LayerBounds.AxisTitleLines(stack.Layers, 0)) == "X");

            // LFR-12: moles Z is numeric, movement Z axis shows a categorical column.
            t.True("can map refused", !LayerRules.CanMapAxis(stack, b, 2, 3, out string msg) && msg.Contains("Phase"));
            t.True("can map same kind", LayerRules.CanMapAxis(stack, b, 0, 1, out _));
            t.Eq("driver axis default", 2, LayerRules.DefaultDriverAxis(b));
            a.ZColumnIndex = 0;
            t.Eq("driver axis = time axis", 2, LayerRules.DefaultDriverAxis(a));
            a.XColumnIndex = 0; a.ZColumnIndex = 4;
            t.Eq("driver axis x", 0, LayerRules.DefaultDriverAxis(a));
        }

        [SelfTest("ColumnRange valid rows")]
        static void ColumnRangeModes(SelfTestTally t)
        {
            DatasetColumnar ml = Moles();
            bool saved = ColumnRange.ValidRowsOnly;
            try
            {
                ColumnRange.ValidRowsOnly = false;
                t.True("legacy includes placeholder 0", ColumnRange.Get(ml, 2, out float lo, out float hi) && lo == 0f && hi == 6f);
                ColumnRange.ValidRowsOnly = true;
                t.True("valid rows only", ColumnRange.Get(ml, 2, out lo, out hi) && lo == 1f && hi == 6f);
                t.True("no column", !ColumnRange.Get(ml, 9, out _, out _));
            }
            finally
            {
                ColumnRange.ValidRowsOnly = saved;
            }
        }

        [SelfTest("LayerRows filters")]
        static void RowFilters(SelfTestTally t)
        {
            DatasetColumnar ml = Moles();
            DatasetColumnar mv = Movement();
            var layer = new PlotLayer { Dataset = mv };
            t.True("no filters pass", LayerRows.PassesFilters(layer, mv, 0));

            layer.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 4, Label = "b" });
            t.True("value match", LayerRows.PassesFilters(layer, mv, 1) && !LayerRows.PassesFilters(layer, mv, 0));

            var moles = new PlotLayer { Dataset = ml };
            moles.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 2, Label = "" });
            t.True("any value has value", LayerRows.PassesFilters(moles, ml, 0));
            t.True("any value missing cell", !LayerRows.PassesFilters(moles, ml, 2));

            moles.Filters[0].Ranges.Add(new MultiplayerScatterplotManager.FilterRange { min = 5f, max = 10f });
            t.True("range in", LayerRows.PassesFilters(moles, ml, 1));
            t.True("range out", !LayerRows.PassesFilters(moles, ml, 0));
            t.True("range missing cell (stored 0) never passes", !LayerRows.PassesFilters(moles, ml, 2));

            moles.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 0, Label = "5" });
            t.True("OR over entries", LayerRows.PassesFilters(moles, ml, 0));

            var rows = new List<int>();
            moles.XColumnIndex = 0; moles.YColumnIndex = 2; moles.ZColumnIndex = 3;
            LayerRows.Drawable(moles, null, rows);
            t.Eq("drawable skips empty axis cell", "0,1", string.Join(",", rows));
            LayerRows.FilterPassing(moles, null, rows);
            t.Eq("filter passing", "0,1", string.Join(",", rows));
        }

        // ---- builder ------------------------------------------------------------------------

        [SelfTest("LayerPointBuilder legacy output")]
        static void BuilderLegacy(SelfTestTally t)
        {
            DatasetColumnar mv = Movement();
            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset("movement.cdataset", mv);
            a.XColumnIndex = 1; a.YColumnIndex = 2; a.ZColumnIndex = 4; a.TimeColumnIndex = 0; a.ColorColumnIndex = -1;
            a.Filters.Add(new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = 4, Label = "a" });

            var settings = new LayerBuildSettings
            {
                Origin = new Vector3(-0.5f, -0.5f, -0.5f), AxisLength = 1f,
                FilteredOutAlpha = 0.08f, TimeOutsideAlpha = 0.18f, TimeTrailFraction = 0.02f,
            };
            var buffers = new PointBuffers();
            PlotClock.Range(stack.Layers, out float c0, out float c1);
            float clock = PlotClock.TimeAt(0.5f, c0, c1);
            LayerDrawInfo info = LayerPointBuilder.Append(a, stack, LayerBounds.FrameFor(a, mv, LayerBounds.Compute(stack.Layers)),
                1_000_000, true, c0, c1, clock, settings, buffers);

            t.Eq("builder drawn", 5, info.Drawn);
            t.Eq("builder count", 5, buffers.Count);
            bool exact = true;
            for (int r = 0; r < mv.RowCount; r++)
            {
                Vector3 expected = settings.Origin + new Vector3(mv.GetNormalizedValue(r, 1), mv.GetNormalizedValue(r, 2), mv.GetNormalizedValue(r, 4)) * settings.AxisLength;
                exact &= buffers.Positions[r] == expected && buffers.Positions[r].x.Equals(expected.x) &&
                         buffers.Positions[r].y.Equals(expected.y) && buffers.Positions[r].z.Equals(expected.z);
            }
            t.True("builder positions bit-identical", exact);
            t.Eq("builder discrete time window", "False,False,True,False,False", string.Join(",", buffers.InTimeWindow));
            t.Near("builder filtered alpha", 0.08f, buffers.Colors[1].a);
            t.Near("builder time alpha", 0.18f, buffers.Colors[0].a);
            t.Near("builder in-window alpha", 1f, buffers.Colors[2].a);
            t.True("builder cyan", buffers.Colors[2].r == 0f && buffers.Colors[2].g == 1f && buffers.Colors[2].b == 1f);
            t.Near("builder fixed size", a.PointSize, buffers.Sizes[3]);
        }

        // ---- manager: scope, forwarders, events ----------------------------------------------

        [SelfTest("Manager edit scope and events")]
        static void ManagerScopeAndEvents(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            SelfTestKit.WithManager(m =>
            {
                t.Eq("seed layer", "L1", m.ActiveLayerId);
                t.True("seed has no dataset", m.LoadedDataset == null);
                t.True("seed has edit layer", m.HasEditLayer);

                m.LoadLocalDataset("movement.cdataset");
                t.Eq("load names layer", "movement", m.ActiveLayer.Name);
                t.Eq("load dataset name", "movement.cdataset", m.CurrentDatasetName);

                var events = new List<string>();
                m.OnDatasetLoaded += () => events.Add("D");
                m.OnBookmarksChanged += () => events.Add("B");
                m.OnLayersChanged += () => events.Add("L");
                m.OnPlotSettingsChanged += () => events.Add("P");

                m.RequestAddDatasetLayerRpc("moles.cdataset");
                t.Eq("add events", "D,B,L,P", string.Join(",", events));
                t.Eq("add active", "L2", m.ActiveLayerId);
                t.True("forwarders follow active", ReferenceEquals(m.LoadedDataset, ml));

                events.Clear();
                m.RequestSetLayerOpacityRpc("L2", 0.5f);
                t.Eq("layer setting events", "L,P", string.Join(",", events));

                events.Clear();
                using (m.EditLayerScope("L1"))
                {
                    t.True("scope dataset", ReferenceEquals(m.LoadedDataset, mv));
                    m.RequestColorColumnRpc(4);
                    using (m.EditLayerScope("L2"))
                        t.True("nested scope", ReferenceEquals(m.LoadedDataset, ml));
                    t.True("nested scope restored", ReferenceEquals(m.LoadedDataset, mv));
                }
                t.Eq("scoped change on L1", 4, m.Stack.Find("L1").ColorColumnIndex);
                t.Eq("active untouched", -1, m.Stack.Find("L2").ColorColumnIndex);
                t.Eq("legacy request events", "P", string.Join(",", events));

                string xBefore = m.Stack.Find("L1").XColumnIndex + "," + m.Stack.Find("L2").XColumnIndex;
                using (m.EditLayerScope("L99"))
                {
                    t.True("unknown scope no edit layer", !m.HasEditLayer);
                    m.RequestXColumnRpc(1);
                }
                t.Eq("unknown scope request ignored", xBefore, m.Stack.Find("L1").XColumnIndex + "," + m.Stack.Find("L2").XColumnIndex);

                events.Clear();
                m.RequestSetActiveLayerRpc("L1");
                t.Eq("select events", "D,B,L,P", string.Join(",", events));

                m.RequestAddComputedLayerRpc("L1", ComputedLayerKind.Trendline);
                t.Eq("computed active", "L3", m.ActiveLayerId);
                t.True("computed shows source dataset", ReferenceEquals(m.LoadedDataset, mv));
                int before = m.Stack.Find("L1").XColumnIndex;
                m.RequestXColumnRpc(3);
                t.Eq("computed refuses mapping", before, m.Stack.Find("L1").XColumnIndex);
                t.Eq("computed own x untouched", 0, m.Stack.Find("L3").XColumnIndex);

                m.RequestRemoveLayerRpc("L1");
                t.Eq("computed source frozen", "movement.cdataset", m.Stack.Find("L3").DatasetName);
                m.RequestRemoveLayerRpc("L3");
                m.RequestRemoveLayerRpc("L2");
                t.Eq("plot emptied", 0, m.Layers.Count);
                t.True("empty plot no dataset", m.LoadedDataset == null && !m.HasEditLayer && m.CurrentDatasetName == "");

                m.RequestLoadDatasetRpc("movement.cdataset");
                t.Eq("load on empty plot", 1, m.Layers.Count);
                t.True("load on empty plot dataset", ReferenceEquals(m.LoadedDataset, mv));
                t.Eq("load on empty plot fresh id", "L4", m.ActiveLayerId);
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));
        }

        [SelfTest("RequestLoadDatasetRpc resets the stack")]
        static void LoadResetsStack(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("movement.cdataset");
                m.RequestPointSizeRpc(0.05f);
                m.RequestSizeMappedRpc(true);
                m.RequestAddDatasetLayerRpc("moles.cdataset");
                m.RequestLoadDatasetRpc("moles.cdataset");
                t.Eq("reset one layer", 1, m.Layers.Count);
                t.Eq("reset keeps base id", "L1", m.ActiveLayerId);
                t.Near("reset keeps point size", 0.05f, m.PointSize);
                t.True("reset keeps size mapped", m.SizeMapped);
                t.Eq("reset mapping", "0,1,2", m.XColumnIndex + "," + m.YColumnIndex + "," + m.ZColumnIndex);
                t.True("reset dataset", ReferenceEquals(m.LoadedDataset, ml));
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));
        }

        // ---- logging contract (basic bodies) ------------------------------------------------

        [SelfTest("PlotStateDiff.DescribeStack basics")]
        static void DescribeStackBasics(SelfTestTally t)
        {
            var a = new PlotSnapshot { DatasetName = "movement.cdataset", XColumn = 0 };
            a.Layers[0].Name = "movement";
            var b = new PlotSnapshot
            {
                Layers = new[] { LayerCopy(a.Layers[0]), new LayerSnapshot { Id = "L2", Name = "moles", DatasetName = "moles.cdataset" } },
                ActiveLayerId = "L1",
            };
            b.Layers[0].XColumn = 2;

            List<LayerChange> changes = PlotStateDiff.DescribeStack(a, b, _ => null);
            t.Eq("stack diff", "AddLayer[moles](->Dataset: moles.cdataset),SetX[movement](#0->#2)", string.Join(",", changes));
            t.True("stack relevant", PlotStateDiff.HasRelevantChange(a, b));

            b.ActiveLayerId = "L2";
            // Selecting a layer that was just added gives no SelectLayer row (one user action, one row).
            t.Eq("stack select", "AddLayer[moles](->Dataset: moles.cdataset),SetX[movement](#0->#2)",
                string.Join(",", PlotStateDiff.DescribeStack(a, b, _ => null)));

            var load = new PlotSnapshot { DatasetName = "moles.cdataset" };
            t.Eq("stack load", "LoadDataset[](movement.cdataset->moles.cdataset)", string.Join(",", PlotStateDiff.DescribeStack(a, load, _ => null)));
            t.True("json overload", PlotStateJson.Write(a, _ => null).Contains("\"ds\":\"movement.cdataset\""));
        }

        static LayerSnapshot LayerCopy(LayerSnapshot s) => LayerSnapshot.From(s.ToLayer());
    }
}
