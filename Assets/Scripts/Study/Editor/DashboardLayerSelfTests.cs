using System;
using System.Collections.Generic;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>
    /// WP6 checks (LFR-26, 27, 29, 34): the layered prepared views (per-layer TryResolve on the
    /// converter's movement and moles columns, a missing EndTime named, FileFor with a fixed part,
    /// the stack ApplyLayers builds) and the dashboard's undo/redo history over a layer stack.
    /// The UI itself (DashboardLayersSection) needs a panel and is not exercised here.
    /// </summary>
    public static class DashboardLayerSelfTests
    {
        const string k_Movement1 = "P07_part1_movement.cdataset";
        const string k_Movement2 = "P07_part2_movement.cdataset";
        const string k_Moles1 = "P07_part1_moles.cdataset";

        // The converter's column names (MOVEMENT_COLS / MOLES_COLS), trimmed to what the views use plus neighbours.
        static DatasetColumnar Movement(string name, float offset = 0f) => SelfTestKit.Dataset(name,
            Col.Num("Time", 0f, 1f, 2f, 3f),
            Col.Num("ControllerX", 1f, 2f, 3f, 4f),
            Col.Num("LaserX", 0f + offset, 0.5f, 1f, 1.5f),
            Col.Num("LaserY", 1f, 1.2f, 1.4f, 1.6f));

        static DatasetColumnar Moles(string name, bool withEndTime = true)
        {
            var cols = new List<Col>
            {
                Col.Num("MoleOrder", 1f, 2f, 3f),
                Col.Num("MoleX", 0.2f, 0.8f, 1.2f),
                Col.Num("MoleY", 1.1f, 1.3f, 1.5f),
                Col.Num("SpawnTime", 0.5f, 1.5f, 2.5f),
            };
            if (withEndTime) cols.Add(Col.Num("EndTime", 1f, 2f, 3f));
            cols.Add(Col.Cat("Outcome", "Hit", "Expired", "Hit"));
            return SelfTestKit.Dataset(name, cols.ToArray());
        }

        static PreparedView View(string id) => PreparedViews.Find(id);

        [SelfTest("Dashboard: layered views kept apart from All")]
        static void LayeredList(SelfTestTally t)
        {
            t.Eq("All count unchanged", 3, PreparedViews.All.Count);
            foreach (PreparedView v in PreparedViews.All)
                t.True("All view single-layer " + v.Id, v.Layers == null);
            t.Eq("Layered count", 3, PreparedViews.Layered.Count);
            foreach (PreparedView v in PreparedViews.Layered)
                t.True("Layered has 2 layers " + v.Id, v.Layers != null && v.Layers.Length == 2);
            t.True("find layered", View("targets-cursor-layers") != null && View("targets-until-hit") != null && View("part1-part2") != null);

            PreparedView targets = View("targets-cursor-layers");
            t.Eq("targets base kind", PreparedViews.KindMovement, targets.Layers[0].Kind);
            t.Eq("targets moles kind", PreparedViews.KindMoles, targets.Layers[1].Kind);
            t.Eq("targets moles always", (int)LayerTimeBehaviour.Always, (int)targets.Layers[1].TimeBehaviour);
            t.Eq("targets moles star", PreparedViews.GlyphStar, targets.Layers[1].DefaultGlyph);
            t.Eq("until hit span", (int)LayerTimeBehaviour.Span, (int)View("targets-until-hit").Layers[1].TimeBehaviour);
            t.Eq("until hit end column", "EndTime", View("targets-until-hit").Layers[1].EndTime);
        }

        [SelfTest("Dashboard: per-layer TryResolve")]
        static void ResolveLayers(SelfTestTally t)
        {
            DatasetColumnar movement = Movement(k_Movement1);
            DatasetColumnar moles = Moles(k_Moles1);
            PreparedView view = View("targets-until-hit");

            bool ok = PreparedViews.TryResolve(view.Layers[0], movement, out ResolvedLayer c, out string missing);
            t.True("movement resolves", ok);
            t.Eq("movement missing", "", missing);
            t.Eq("movement X LaserX", 2, c.X);
            t.Eq("movement Y LaserY", 3, c.Y);
            t.Eq("movement Z Time", 0, c.Z);
            t.Eq("movement time", 0, c.Time);
            t.Eq("movement colour none", -1, c.Color);
            t.Eq("movement end none", -1, c.EndTime);
            t.Eq("movement label none", -1, c.Label);

            ok = PreparedViews.TryResolve(view.Layers[1], moles, out c, out missing);
            t.True("moles resolves", ok);
            t.Eq("moles X", 1, c.X);
            t.Eq("moles Y", 2, c.Y);
            t.Eq("moles Z SpawnTime", 3, c.Z);
            t.Eq("moles time SpawnTime", 3, c.Time);
            t.Eq("moles end EndTime", 4, c.EndTime);
            t.Eq("moles label MoleOrder", 0, c.Label);

            ok = PreparedViews.TryResolve(view.Layers[1], movement, out c, out missing);
            t.True("moles layer on movement fails", !ok);
            t.Eq("moles layer on movement missing", "MoleX, MoleY, SpawnTime, EndTime, MoleOrder", missing);

            t.True("null dataset", !PreparedViews.TryResolve(view.Layers[0], null, out _, out missing) && missing == "(no dataset)");
        }

        [SelfTest("Dashboard: missing EndTime reported by name")]
        static void MissingEndTime(SelfTestTally t)
        {
            DatasetColumnar old = Moles(k_Moles1, withEndTime: false);
            bool span = PreparedViews.TryResolve(View("targets-until-hit").Layers[1], old, out _, out string missing);
            t.True("span view needs EndTime", !span);
            t.Eq("span missing names EndTime", "EndTime", missing);
            t.True("always view does not", PreparedViews.TryResolve(View("targets-cursor-layers").Layers[1], old, out _, out _));

            SelfTestKit.WithManager(m =>
            {
                var files = new List<string> { k_Movement1, k_Moles1 };
                bool checkedOk = PreparedViews.CheckLayers(View("targets-until-hit"), files, out string error);
                t.True("check refuses", !checkedOk);
                t.Eq("check message", k_Moles1 + " has no column EndTime: re-run the converter", error);
                t.True("check passes for always", PreparedViews.CheckLayers(View("targets-cursor-layers"), files, out _));
                t.True("check missing file", !PreparedViews.CheckLayers(View("targets-cursor-layers"),
                    new List<string> { k_Movement1, "P99_part1_moles.cdataset" }, out error) && error.Contains("P99_part1_moles.cdataset"));
            }, (k_Movement1, Movement(k_Movement1)), (k_Moles1, old));
        }

        [SelfTest("Dashboard: FileFor with a fixed part")]
        static void FileForPart(SelfTestTally t)
        {
            PreparedView parts = View("part1-part2");
            t.Eq("part1 layer, part 2 picked", "P07_part1_movement.cdataset", PreparedViews.FileFor("07", 2, parts.Layers[0]));
            t.Eq("part2 layer, part 1 picked", "P07_part2_movement.cdataset", PreparedViews.FileFor("07", 1, parts.Layers[1]));
            PreparedView targets = View("targets-cursor-layers");
            t.Eq("selected part 2", "P07_part2_moles.cdataset", PreparedViews.FileFor("p07", 2, targets.Layers[1]));
            t.Eq("selected part 1", "P07_part1_movement.cdataset", PreparedViews.FileFor("P07", 1, targets.Layers[0]));

            var have = new[] { "P07_part1_movement.cdataset", "p07_part1_moles.cdataset" };
            t.Eq("missing none", 0, PreparedViews.MissingFiles(targets, "P07", 1, have).Count);
            t.Eq("missing part 2", "P07_part2_movement.cdataset", string.Join(",", PreparedViews.MissingFiles(parts, "P07", 1, have)));
            t.Eq("missing moles", "P07_part2_movement.cdataset,P07_part2_moles.cdataset",
                string.Join(",", PreparedViews.MissingFiles(targets, "P07", 2, have)));
        }

        [SelfTest("Dashboard: layered view builds the stack")]
        static void ApplyLayered(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                string error = PreparedViews.ApplyLayers(m, View("targets-cursor-layers"), new List<string> { k_Movement1, k_Moles1 });
                t.Eq("targets applied", "", error ?? "");
                t.Eq("targets 2 layers", 2, m.Layers.Count);
                if (m.Layers.Count == 2)
                {
                    PlotLayer cursor = m.Layers[0], moles = m.Layers[1];
                    t.Eq("cursor name", "Cursor", cursor.Name);
                    t.Eq("cursor X", 2, cursor.XColumnIndex);
                    t.Eq("cursor Z", 0, cursor.ZColumnIndex);
                    t.Eq("cursor time", 0, cursor.TimeColumnIndex);
                    t.Eq("cursor colour fixed", -1, cursor.ColorColumnIndex);
                    t.Eq("cursor follow", (int)LayerTimeBehaviour.Follow, (int)cursor.TimeBehaviour);
                    t.Eq("moles name", "Moles", moles.Name);
                    t.Eq("moles dataset", k_Moles1, moles.DatasetName);
                    t.Eq("moles X", 1, moles.XColumnIndex);
                    t.Eq("moles time", 3, moles.TimeColumnIndex);
                    t.Eq("moles always", (int)LayerTimeBehaviour.Always, (int)moles.TimeBehaviour);
                    t.Eq("moles star", PreparedViews.GlyphStar, moles.DefaultGlyph);
                    t.Eq("moles label", 0, moles.LabelColumnIndex);
                    t.Eq("moles end none", -1, moles.EndTimeColumnIndex);
                    t.True("moles colour", LayerSnapshot.PackColour(moles.FixedColour) == LayerSnapshot.PackColour(new Color32(242, 166, 38, 255)));
                    t.Eq("base active", cursor.Id, m.ActiveLayerId);
                }

                error = PreparedViews.ApplyLayers(m, View("targets-until-hit"), new List<string> { k_Movement1, k_Moles1 });
                t.Eq("until hit applied", "", error ?? "");
                t.Eq("until hit replaces stack", 2, m.Layers.Count);
                if (m.Layers.Count == 2)
                {
                    t.Eq("until hit span", (int)LayerTimeBehaviour.Span, (int)m.Layers[1].TimeBehaviour);
                    t.Eq("until hit end", 4, m.Layers[1].EndTimeColumnIndex);
                }

                error = PreparedViews.ApplyLayers(m, View("part1-part2"), new List<string> { k_Movement1, k_Movement2 });
                t.Eq("parts applied", "", error ?? "");
                t.Eq("parts 2 layers", 2, m.Layers.Count);
                if (m.Layers.Count == 2)
                {
                    t.Eq("part 1 file", k_Movement1, m.Layers[0].DatasetName);
                    t.Eq("part 2 file", k_Movement2, m.Layers[1].DatasetName);
                    t.Eq("part 2 name", "Part 2", m.Layers[1].Name);
                    t.True("parts differ in colour", LayerSnapshot.PackColour(m.Layers[0].FixedColour) != LayerSnapshot.PackColour(m.Layers[1].FixedColour));
                    t.Eq("part 2 same mapping", m.Layers[0].XColumnIndex, m.Layers[1].XColumnIndex);
                }

                // A dataset load from the dashboard is a new plot: one layer (decision E4).
                m.RequestLoadDatasetRpc(k_Moles1);
                t.Eq("load replaces stack", 1, m.Layers.Count);
            }, (k_Movement1, Movement(k_Movement1)), (k_Movement2, Movement(k_Movement2, 0.25f)), (k_Moles1, Moles(k_Moles1)));
        }

        // LayerStack.Restore never lowers NextLayerNumber (ids are not reused in a session), so a
        // restored stack equals the earlier one in everything but the counter.
        static bool SameExceptCounter(PlotSnapshot now, PlotSnapshot earlier)
        {
            int kept = earlier.NextLayerNumber;
            earlier.NextLayerNumber = now.NextLayerNumber;
            bool same = now.SamePlotAs(earlier);
            earlier.NextLayerNumber = kept;
            return same;
        }

        [SelfTest("Dashboard: undo and redo restore the stack")]
        static void UndoRedo(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                var history = new DashboardUndoHistory(50);
                bool Restore(PlotSnapshot s)
                {
                    m.ApplyNetworkSnapshot(s, s.TimeScrub);
                    return true;
                }

                m.RequestLoadDatasetRpc(k_Movement1);
                PlotSnapshot s0 = PlotSnapshot.Capture(m);

                history.Push(PlotSnapshot.Capture(m));
                m.RequestAddDatasetLayerRpc(k_Moles1);           // add
                PlotSnapshot s1 = PlotSnapshot.Capture(m);

                history.Push(PlotSnapshot.Capture(m));
                string added = m.ActiveLayerId;
                m.RequestSetLayerOpacityRpc(added, 0.5f);         // restyle
                m.RequestSetLayerColourRpc(added, new Color32(255, 0, 0, 255));
                m.RequestRenameLayerRpc(added, "Targets");
                PlotSnapshot s2 = PlotSnapshot.Capture(m);
                t.True("restyled", !s2.SamePlotAs(s1));

                t.True("undo 1", history.TryUndo(PlotSnapshot.Capture(m), Restore));
                t.True("undo 1 = after add", PlotSnapshot.Capture(m).SamePlotAs(s1));
                t.True("undo 2", history.TryUndo(PlotSnapshot.Capture(m), Restore));
                t.True("undo 2 = before add", SameExceptCounter(PlotSnapshot.Capture(m), s0));
                t.True("undo 2 keeps id counter", PlotSnapshot.Capture(m).NextLayerNumber >= s1.NextLayerNumber);
                t.Eq("undo 2 one layer", 1, m.Layers.Count);
                t.True("undo 3 nothing", !history.TryUndo(PlotSnapshot.Capture(m), Restore));

                t.True("redo 1", history.TryRedo(PlotSnapshot.Capture(m), Restore));
                t.True("redo 1 = after add", PlotSnapshot.Capture(m).SamePlotAs(s1));
                t.True("redo 2", history.TryRedo(PlotSnapshot.Capture(m), Restore));
                t.True("redo 2 = restyled", PlotSnapshot.Capture(m).SamePlotAs(s2));
                t.Eq("redo 2 name", "Targets", m.ActiveLayer != null ? m.ActiveLayer.Name : "");
                t.True("redo 3 nothing", !history.TryRedo(PlotSnapshot.Capture(m), Restore));

                // A refused restore moves nothing; a new change clears redo.
                t.True("undo again", history.TryUndo(PlotSnapshot.Capture(m), Restore));
                int undo = history.UndoCount, redo = history.RedoCount;
                t.True("refused", !history.TryUndo(PlotSnapshot.Capture(m), _ => false));
                t.Eq("refused keeps undo", undo, history.UndoCount);
                t.Eq("refused keeps redo", redo, history.RedoCount);
                history.Push(PlotSnapshot.Capture(m));
                t.Eq("new change clears redo", 0, history.RedoCount);
                history.Push(PlotSnapshot.Capture(m), clearRedo: false);
                history.DropLastUndo();
                t.Eq("drag without change dropped", undo + 1, history.UndoCount);

                t.Eq("missing dataset none", "", DashboardUndoHistory.MissingDataset(s1) ?? "");
                var gone = new PlotSnapshot
                {
                    Layers = new[] { new LayerSnapshot { Id = "L1", Name = "x", Kind = (byte)LayerKind.Dataset, DatasetName = "WP6_no_such_file.cdataset" } },
                    ActiveLayerId = "L1",
                };
                t.Eq("missing dataset named", "WP6_no_such_file.cdataset", DashboardUndoHistory.MissingDataset(gone) ?? "");
            }, (k_Movement1, Movement(k_Movement1)), (k_Moles1, Moles(k_Moles1)));

            var capped = new DashboardUndoHistory(2);
            capped.Push(new PlotSnapshot());
            capped.Push(new PlotSnapshot());
            capped.Push(new PlotSnapshot());
            t.Eq("cap", 2, capped.UndoCount);
        }
    }
}
