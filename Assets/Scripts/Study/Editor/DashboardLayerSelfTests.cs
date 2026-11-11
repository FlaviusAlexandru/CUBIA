using System;
using System.Collections.Generic;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>
    /// WP6 checks (LFR-26, 29): the dashboard's undo/redo history over a layer stack.
    /// The UI itself (DashboardLayersSection) needs a panel and is not exercised here.
    /// </summary>
    public static class DashboardLayerSelfTests
    {
        const string k_Movement1 = "P07_part1_movement.cdataset";
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
