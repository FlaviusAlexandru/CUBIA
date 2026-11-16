using System;
using System.Collections.Generic;
using System.IO;
using DataViz.Flow;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>Checks for saved views (LFR-33) and change batches (review finding 6).</summary>
    public static class SavedViewSelfTests
    {
        static DatasetColumnar Movement() => SelfTestKit.Dataset("movement.cdataset",
            Col.Num("Time", 0f, 1f, 2f, 3f, 4f),
            Col.Num("X", 0f, 2f, 4f, 6f, 8f),
            Col.Num("Y", 10f, 11f, 12f, 13f, 14f),
            Col.Num("Z", -1f, 0f, 1f, 2f, 3f),
            Col.Cat("Phase", "a", "b", "a", "c", "b"));

        // The same columns in another order, as after re-running the converter.
        static DatasetColumnar MovementReordered() => SelfTestKit.Dataset("movement.cdataset",
            Col.Cat("Phase", "a", "b", "a", "c", "b"),
            Col.Num("Z", -1f, 0f, 1f, 2f, 3f),
            Col.Num("Y", 10f, 11f, 12f, 13f, 14f),
            Col.Num("X", 0f, 2f, 4f, 6f, 8f),
            Col.Num("Time", 0f, 1f, 2f, 3f, 4f));

        static DatasetColumnar MovementWithoutPhase() => SelfTestKit.Dataset("movement.cdataset",
            Col.Num("Time", 0f, 1f, 2f, 3f, 4f),
            Col.Num("X", 0f, 2f, 4f, 6f, 8f),
            Col.Num("Y", 10f, 11f, 12f, 13f, 14f),
            Col.Num("Z", -1f, 0f, 1f, 2f, 3f));

        static DatasetColumnar Moles() => SelfTestKit.Dataset("moles.cdataset",
            Col.Num("X", 5f, 20f, 7f),
            Col.Num("Y", 9f, 15f, 12f),
            Col.Num("SpawnTime", 1f, 6f, null),
            Col.Num("Z", 0f, 1f, 2f));

        // movement: X/Y/Z = X/Y/Z, colour Phase, time Time, filter on Phase; moles on top; L1 active.
        static void BuildTwoLayers(MultiplayerScatterplotManager m)
        {
            m.LoadLocalDataset("movement.cdataset");
            m.RequestXColumnRpc(1);
            m.RequestYColumnRpc(2);
            m.RequestZColumnRpc(3);
            m.RequestColorColumnRpc(4);
            m.RequestTimeColumnRpc(0);
            m.RequestFilterIndexRpc(4);
            m.RequestAddDatasetLayerRpc("moles.cdataset");
            m.RequestSetLayerOpacityRpc("L2", 0.5f);
            m.RequestSetActiveLayerRpc("L1");
        }

        [SelfTest("Saved view: columns stored by name")]
        static void ColumnsByName(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            SelfTestKit.WithManager(m =>
            {
                BuildTwoLayers(m);
                SavedView view = SavedViewStore.FromSnapshot(PlotSnapshot.Capture(m), "v", f => DatasetRegistry.Get(f));
                t.Eq("layers", 2, view.Layers.Count);
                t.Eq("x name", "X", view.Layers[0].X);
                t.Eq("colour name", "Phase", view.Layers[0].Colour);
                t.Eq("time name", "Time", view.Layers[0].Time);
                t.Eq("filter name", "Phase", view.Layers[0].Filters.Length > 0 ? view.Layers[0].Filters[0] : "(none)");
                t.Eq("active", "L1", view.ActiveLayerId);
                t.Eq("summary", "movement.cdataset + 1 more", view.DatasetSummary);

                DatasetColumnar reordered = MovementReordered();
                PlotSnapshot s = SavedViewStore.ToSnapshot(view, f => f == "movement.cdataset" ? reordered : ml, out string error);
                t.True("reordered loads", s != null && error == null);
                if (s == null) return;
                t.Eq("x by name", 3, s.Layers[0].XColumn);
                t.Eq("y by name", 2, s.Layers[0].YColumn);
                t.Eq("z by name", 1, s.Layers[0].ZColumn);
                t.Eq("colour by name", 0, s.Layers[0].ColourColumn);
                t.Eq("time by name", 4, s.Layers[0].TimeColumn);
                t.Eq("filter by name", 0, s.Layers[0].FilterColumns[0]);
                t.Near("opacity kept", 0.5f, s.Layers[1].Opacity, 1e-6f);
                t.True("loaded paused", !s.IsPlaying);

                DatasetColumnar noPhase = MovementWithoutPhase();
                t.True("missing column refused",
                    SavedViewStore.ToSnapshot(view, f => f == "movement.cdataset" ? noPhase : ml, out error) == null
                    && error != null && error.Contains("Phase"));
                t.True("missing dataset refused",
                    SavedViewStore.ToSnapshot(view, f => f == "movement.cdataset" ? mv : null, out error) == null
                    && error != null && error.Contains("moles.cdataset"));
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));
        }

        [SelfTest("Saved view: store round trip and load")]
        static void StoreAndLoad(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            string path = Path.Combine(Path.GetTempPath(), "cubia_saved_views_selftest.json");
            if (File.Exists(path)) File.Delete(path);
            SavedViewStore.PathOverride = path;
            SavedViewStore.Reload();
            try
            {
                SelfTestKit.WithManager(m =>
                {
                    BuildTwoLayers(m);
                    t.Eq("default name", "View 1", SavedViewStore.NextDefaultName());
                    SavedViewStore.SaveCurrent(m, "Two layers");
                    t.True("file written", File.Exists(path));

                    SavedViewStore.Reload(); // read it back from the file
                    t.Eq("one view", 1, SavedViewStore.All().Count);
                    SavedView view = SavedViewStore.Find("two LAYERS");
                    t.True("found case-insensitively", view != null);
                    t.Eq("default name skips none", "View 1", SavedViewStore.NextDefaultName());

                    SavedViewStore.SaveCurrent(m, "Two layers"); // same name replaces
                    t.Eq("replaced, not added", 1, SavedViewStore.All().Count);

                    m.RequestRemoveLayerRpc("L2");
                    m.RequestColorColumnRpc(-1);
                    t.Eq("changed before load", 1, m.Layers.Count);

                    string error = SavedViewStore.Load(m, view);
                    t.True("load ok", error == null);
                    t.Eq("layers back", 2, m.Layers.Count);
                    t.Eq("active back", "L1", m.ActiveLayerId);
                    t.Eq("colour back", 4, m.Stack.Find("L1")?.ColorColumnIndex ?? -99);
                    t.True("moles dataset", ReferenceEquals(m.Stack.Find("L2")?.Dataset, ml));
                    t.Near("opacity back", 0.5f, m.Stack.Find("L2")?.Opacity ?? -1f, 1e-6f);

                    t.True("delete", SavedViewStore.Delete("Two layers"));
                    t.Eq("none left", 0, SavedViewStore.All().Count);
                    t.True("load of nothing refused", SavedViewStore.Load(m, null) != null);
                }, ("movement.cdataset", mv), ("moles.cdataset", ml));
            }
            finally
            {
                SavedViewStore.PathOverride = null;
                SavedViewStore.Reload();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [SelfTest("Change batches raise each event once, at the end")]
        static void Batches(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("movement.cdataset");
                var events = new List<string>();
                m.OnDatasetLoaded += () => events.Add("D");
                m.OnBookmarksChanged += () => events.Add("B");
                m.OnLayersChanged += () => events.Add("L");
                m.OnPlotSettingsChanged += () => events.Add("P");

                using (m.BatchChanges())
                {
                    m.RequestXColumnRpc(1);
                    m.RequestAddDatasetLayerRpc("moles.cdataset");
                    m.RequestSetLayerOpacityRpc("L2", 0.5f);
                    t.True("held in batch", m.IsBatchingChanges);
                    t.Eq("nothing raised yet", "", string.Join(",", events));
                }
                t.Eq("once each, in order", "D,B,L,P", string.Join(",", events));
                t.True("batch closed", !m.IsBatchingChanges);

                events.Clear();
                using (m.EditLayerScope("L1"))
                {
                    m.RequestColorColumnRpc(4);
                    m.RequestSetLayerOpacityRpc("L1", 0.75f);
                    t.Eq("scope holds events", "", string.Join(",", events));
                }
                t.Eq("scope raises after close", "L,P", string.Join(",", events));

                events.Clear();
                using (m.BatchChanges())
                {
                    using (m.EditLayerScope("L1"))
                        m.RequestColorColumnRpc(-1);
                    t.Eq("outer batch still holds", "", string.Join(",", events));
                }
                t.Eq("raised by the outer batch", "P", string.Join(",", events));
                t.True("not a playback tick", !m.IsPlaybackTick);
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));
        }
    }
}
