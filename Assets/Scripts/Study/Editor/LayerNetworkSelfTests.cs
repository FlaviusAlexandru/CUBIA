using System;
using System.Collections.Generic;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>
    /// WP8 checks (LFR-30, LNFR-04, R7): the per-layer dataset status, a client applying the host's
    /// stack with layers it lacks, requests for a removed layer, and that re-applying a snapshot
    /// raises no second OnDatasetLoaded (risk 3). No network runs here: the static halves of
    /// NetworkScatterplotState are called directly, and DatasetHash.LocalHashOverride stands in for
    /// the local files.
    /// </summary>
    public static class LayerNetworkSelfTests
    {
        static DatasetColumnar Movement() => SelfTestKit.Dataset("movement.cdataset",
            Col.Num("Time", 0f, 1f, 2f, 3f),
            Col.Num("X", 0f, 2f, 4f, 8f),
            Col.Num("Y", 10f, 11f, 12f, 14f));

        static DatasetColumnar Moles() => SelfTestKit.Dataset("moles.cdataset",
            Col.Num("Time", 20f, 30f, 40f),
            Col.Num("X", 50f, 60f, 70f),
            Col.Num("Y", -5f, 0f, 5f));

        static LayerSnapshot L(string id, LayerKind kind, string dataset, string hash, string source = "") => new LayerSnapshot
        {
            Id = id, Name = id, Kind = (byte)kind, DatasetName = dataset ?? "", DatasetHash = hash ?? "", SourceLayerId = source,
        };

        static PlotSnapshot Published(params LayerSnapshot[] layers) => new PlotSnapshot
        {
            Layers = layers, ActiveLayerId = layers.Length > 0 ? layers[0].Id : "", NextLayerNumber = layers.Length + 1, Published = true,
        };

        // Local files: movement as the host has it, moles in another version, nothing else.
        static string LocalHash(string name) => name switch
        {
            "movement.cdataset" => "aaa",   // the host's "AAA": hashes compare case-insensitively
            "moles.cdataset" => "CCC",
            _ => "",
        };

        [SelfTest("Network: stack dataset status")]
        static void Status(SelfTestTally t)
        {
            StackDatasetCheck c = DatasetHash.CheckStack(new PlotSnapshot(), LocalHash);
            t.True("unpublished is Unknown", c.Status == DatasetMatch.Unknown);
            c = DatasetHash.CheckStack(null, LocalHash);
            t.True("null is Unknown", c.Status == DatasetMatch.Unknown);
            c = DatasetHash.CheckStack(new PlotSnapshot { DatasetName = "movement.cdataset", DatasetHash = "AAA" }, LocalHash);
            t.True("unpublished with layers is Unknown", c.Status == DatasetMatch.Unknown && !c.Unavailable[0]);

            c = DatasetHash.CheckStack(Published(), LocalHash);
            t.True("empty published stack is Match (Q13)", c.Status == DatasetMatch.Match && c.Layers.Length == 0);

            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "movement.cdataset", "AAA"),
                                                  L("L2", LayerKind.Subset, "movement.cdataset", "AAA", "L1")), LocalHash);
            t.True("all match", c.Status == DatasetMatch.Match && !c.Unavailable[0] && !c.Unavailable[1]);
            t.True("match states", c.Layers[0] == LayerDatasetState.Match && c.Layers[1] == LayerDatasetState.Match);

            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "movement.cdataset", "AAA"),
                                                  L("L2", LayerKind.Dataset, "moles.cdataset", "BBB"),
                                                  L("L3", LayerKind.Subset, "moles.cdataset", "BBB", "L2")), LocalHash);
            t.True("one mismatch: Mismatch", c.Status == DatasetMatch.Mismatch);
            t.True("one mismatch: only those layers hidden", !c.Unavailable[0] && c.Unavailable[1] && c.Unavailable[2]);
            t.Eq("one mismatch: named once", "moles.cdataset", string.Join("|", c.Mismatched));
            t.Eq("one mismatch: none missing", 0, c.Missing.Count);

            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "movement.cdataset", "AAA"),
                                                  L("L2", LayerKind.Dataset, "other.cdataset", "DDD")), LocalHash);
            t.True("one missing: Missing", c.Status == DatasetMatch.Missing);
            t.True("one missing: state", c.Layers[1] == LayerDatasetState.Missing && c.Unavailable[1] && !c.Unavailable[0]);
            t.Eq("one missing: named", "other.cdataset", string.Join("|", c.Missing));

            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "moles.cdataset", "BBB"),
                                                  L("L2", LayerKind.Dataset, "other.cdataset", "DDD")), LocalHash);
            t.True("missing wins over mismatch", c.Status == DatasetMatch.Missing && c.Mismatched.Count == 1 && c.Missing.Count == 1);

            // Computed layers: on a live source they follow it; frozen ones are checked by their own name.
            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "movement.cdataset", "AAA"),
                                                  L("L2", LayerKind.Dataset, "moles.cdataset", "BBB"),
                                                  L("L3", LayerKind.Computed, "", "", "L1"),
                                                  L("L4", LayerKind.Computed, "", "", "L2"),
                                                  L("L5", LayerKind.Computed, "movement.cdataset", "AAA", "L9")), LocalHash);
            t.True("computed not checked", c.Layers[2] == LayerDatasetState.NotChecked && c.Layers[3] == LayerDatasetState.NotChecked);
            t.True("computed on available source shown", !c.Unavailable[2]);
            t.True("computed on hidden source hidden", c.Unavailable[3]);
            t.True("frozen computed checked", c.Layers[4] == LayerDatasetState.Match && !c.Unavailable[4]);
            t.True("computed does not change status", c.Status == DatasetMatch.Mismatch);

            // The host itself lacks the file: hidden here too, but it does not make this player view-only.
            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "movement.cdataset", "AAA"),
                                                  L("L2", LayerKind.Dataset, "moles.cdataset", "")), LocalHash);
            t.True("host missing: hidden, status Match", c.Layers[1] == LayerDatasetState.HostMissing && c.Unavailable[1] && c.Status == DatasetMatch.Match);

            // A layer naming no dataset (seed layer before any load) is not checked.
            c = DatasetHash.CheckStack(Published(L("L1", LayerKind.Dataset, "", "")), LocalHash);
            t.True("no dataset name: Match, shown", c.Status == DatasetMatch.Match && !c.Unavailable[0]);
        }

        [SelfTest("Network: told once per dataset")]
        static void ToldOnce(SelfTestTally t)
        {
            var told = new HashSet<string>();
            t.Eq("first", "a|b", string.Join("|", SharedPlotAuthority.TakeNotYetTold(told, new[] { "a", "b", "a", "", null })));
            t.Eq("second", "c", string.Join("|", SharedPlotAuthority.TakeNotYetTold(told, new[] { "b", "c" })));
            t.Eq("null", 0, SharedPlotAuthority.TakeNotYetTold(told, null).Count);
        }

        [SelfTest("Network: main dataset name")]
        static void MainName(SelfTestTally t)
        {
            t.Eq("null", "", NetworkScatterplotState.MainDatasetName(null));
            t.Eq("empty", "", NetworkScatterplotState.MainDatasetName(Published()));
            PlotSnapshot s = Published(L("L1", LayerKind.Dataset, "movement.cdataset", "AAA"),
                                       L("L2", LayerKind.Dataset, "moles.cdataset", "BBB"),
                                       L("L3", LayerKind.Computed, "", "", "L2"));
            s.ActiveLayerId = "L2";
            t.Eq("active", "moles.cdataset", NetworkScatterplotState.MainDatasetName(s));
            s.ActiveLayerId = "L3";
            t.Eq("computed active: its source", "moles.cdataset", NetworkScatterplotState.MainDatasetName(s));
            s = Published(L("L1", LayerKind.Computed, "", "", "L9"), L("L2", LayerKind.Dataset, "moles.cdataset", "BBB"));
            t.Eq("orphan computed active: first named", "moles.cdataset", NetworkScatterplotState.MainDatasetName(s));
        }

        [SelfTest("Network: request for a removed layer is dropped")]
        static void DroppedRequest(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("movement.cdataset");
                m.RequestAddDatasetLayerRpc("moles.cdataset");
                m.RequestSetActiveLayerRpc("L1");
                m.RequestRemoveLayerRpc("L2");
                t.Eq("setup one layer", 1, m.Layers.Count);
                int x = m.Stack.Find("L1").XColumnIndex;

                int events = 0;
                Action count = () => events++;
                m.OnPlotSettingsChanged += count;
                m.OnLayersChanged += count;
                m.OnDatasetLoaded += count;

                t.Eq("legacy op dropped", false, NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.XColumn, 2, layer: "L2")));
                t.Eq("layer op dropped", false, NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.SetLayerOpacity, floatArg: 0.3f, layer: "L2")));
                t.Eq("select dropped", false, NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.SetActiveLayer, layer: "L2")));
                t.Eq("subset of removed source dropped", false, NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.AddSubsetLayer, -1, layer: "L2")));
                t.Eq("dropped: no events", 0, events);
                t.Eq("dropped: L1 x unchanged", x, m.Stack.Find("L1").XColumnIndex);
                t.Eq("dropped: one layer", 1, m.Layers.Count);
                t.Eq("dropped: active", "L1", m.ActiveLayerId);

                int other = x == 2 ? 1 : 2;
                t.Eq("live layer applied", true, NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.XColumn, other, layer: "L1")));
                t.Eq("live layer changed", other, m.Stack.Find("L1").XColumnIndex);
                t.Eq("active-layer request applied", true, NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.SetLayerOpacity, floatArg: 0.5f)));
                t.Near("active-layer opacity", 0.5f, m.Stack.Find("L1").Opacity);
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));
        }

        [SelfTest("Network: client applies the stack, hides layers it lacks")]
        static void ClientApply(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            Func<string, string> saved = DatasetHash.LocalHashOverride;
            try
            {
                SelfTestKit.WithManager(host =>
                {
                    host.LoadLocalDataset("movement.cdataset");
                    host.RequestAddDatasetLayerRpc("moles.cdataset");
                    host.RequestAddComputedLayerRpc("L2", ComputedLayerKind.Mean);
                    host.RequestSetActiveLayerRpc("L1");
                    PlotSnapshot shared = PlotSnapshot.Capture(host);
                    foreach (LayerSnapshot l in shared.Layers)
                    {
                        if (l.DatasetName == "movement.cdataset") l.DatasetHash = "AAA";
                        else if (l.DatasetName == "moles.cdataset") l.DatasetHash = "BBB";
                    }
                    shared.Published = true;
                    t.Eq("host three layers", 3, shared.Layers.Length);

                    // Client: same movement file, another version of moles.
                    DatasetHash.LocalHashOverride = LocalHash;
                    SelfTestKit.WithManager(client =>
                    {
                        StackDatasetCheck check = DatasetHash.CheckStack(shared, DatasetHash.Of);
                        t.True("client status Mismatch", check.Status == DatasetMatch.Mismatch);
                        NetworkScatterplotState.ApplySnapshot(client, shared, 0f, check);

                        t.Eq("client has the whole stack", 3, client.Layers.Count);
                        t.Eq("client active shared", "L1", client.ActiveLayerId);
                        PlotLayer l1 = client.Stack.Find("L1"), l2 = client.Stack.Find("L2"), l3 = client.Stack.Find("L3");
                        t.True("matching layer shown", !l1.LocalUnavailable && ReferenceEquals(l1.Dataset, mv));
                        t.True("other-version layer hidden, no data", l2.LocalUnavailable && l2.Dataset == null);
                        t.True("computed on it hidden", l3.LocalUnavailable);
                        t.True("client layers equal the host's", SameLayers(client, shared));

                        // The layers this client has sit where the host draws them.
                        AxisBounds b = client.SharedBounds;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            if ((shared.BoundsValid & (1 << axis)) == 0) continue;
                            t.Near("host bounds min " + axis, shared.BoundsMin[axis], b.Get(axis).Min);
                            t.Near("host bounds max " + axis, shared.BoundsMax[axis], b.Get(axis).Max);
                        }

                        // Risk 3: the same snapshot again raises no OnDatasetLoaded; switching the
                        // active layer by snapshot raises it once.
                        int loaded = 0;
                        client.OnDatasetLoaded += () => loaded++;
                        NetworkScatterplotState.ApplySnapshot(client, shared, 0f, check);
                        t.Eq("re-apply: no dataset event", 0, loaded);
                        t.True("re-apply: still hidden", client.Stack.Find("L2").LocalUnavailable);

                        // Every file matches now: everything shown, local bounds.
                        DatasetHash.LocalHashOverride = n => n == "moles.cdataset" ? "BBB" : LocalHash(n);
                        StackDatasetCheck ok = DatasetHash.CheckStack(shared, DatasetHash.Of);
                        t.True("now Match", ok.Status == DatasetMatch.Match);
                        NetworkScatterplotState.ApplySnapshot(client, shared, 0f, ok);
                        t.True("now all shown", !client.Stack.Find("L2").LocalUnavailable && ReferenceEquals(client.Stack.Find("L2").Dataset, ml)
                                                && !client.Stack.Find("L3").LocalUnavailable);
                        t.True("no bounds override when all shown", client.SharedBoundsOverride == null);

                        PlotSnapshot switched = PlotSnapshot.Capture(client);
                        switched.ActiveLayerId = "L2";
                        foreach (LayerSnapshot l in switched.Layers)
                            l.DatasetHash = shared.FindLayer(l.Id).DatasetHash;
                        switched.Published = true;
                        loaded = 0;
                        NetworkScatterplotState.ApplySnapshot(client, switched, 0f, DatasetHash.CheckStack(switched, DatasetHash.Of));
                        t.Eq("active switch: one dataset event", 1, loaded);
                        NetworkScatterplotState.ApplySnapshot(client, switched, 0f, DatasetHash.CheckStack(switched, DatasetHash.Of));
                        t.Eq("active switch re-applied: no more", 1, loaded);
                    }, ("movement.cdataset", mv), ("moles.cdataset", ml));

                    // The host lacks moles itself (published hash ""): the client hides it although it has a file.
                    DatasetHash.LocalHashOverride = LocalHash;
                    PlotSnapshot hostless = PlotSnapshot.Capture(host);
                    foreach (LayerSnapshot l in hostless.Layers)
                        l.DatasetHash = l.DatasetName == "movement.cdataset" ? "AAA" : "";
                    hostless.Published = true;
                    SelfTestKit.WithManager(client =>
                    {
                        StackDatasetCheck check = DatasetHash.CheckStack(hostless, DatasetHash.Of);
                        t.True("host-missing: status Match", check.Status == DatasetMatch.Match);
                        NetworkScatterplotState.ApplySnapshot(client, hostless, 0f, check);
                        t.True("host-missing: hidden", client.Stack.Find("L2").LocalUnavailable && client.Stack.Find("L2").Dataset == null);
                        t.True("host-missing: movement shown", !client.Stack.Find("L1").LocalUnavailable);
                    }, ("movement.cdataset", mv), ("moles.cdataset", ml));

                    // Q13: the host empties the plot; the client follows.
                    host.RequestRemoveLayerRpc("L3");
                    host.RequestRemoveLayerRpc("L2");
                    host.RequestRemoveLayerRpc("L1");
                    PlotSnapshot empty = PlotSnapshot.Capture(host);
                    empty.Published = true;
                    t.Eq("host emptied", 0, empty.Layers.Length);
                    SelfTestKit.WithManager(client =>
                    {
                        client.LoadLocalDataset("movement.cdataset");
                        StackDatasetCheck check = DatasetHash.CheckStack(empty, DatasetHash.Of);
                        t.True("empty: Match", check.Status == DatasetMatch.Match);
                        NetworkScatterplotState.ApplySnapshot(client, empty, 0f, check);
                        t.Eq("empty: client emptied", 0, client.Layers.Count);
                    }, ("movement.cdataset", mv));
                }, ("movement.cdataset", mv), ("moles.cdataset", ml));
            }
            finally
            {
                DatasetHash.LocalHashOverride = saved;
            }
        }

        // Every layer of the client's stack equals the shared one (hashes are not kept in PlotLayer, so copied over).
        static bool SameLayers(MultiplayerScatterplotManager m, PlotSnapshot shared)
        {
            PlotSnapshot s = PlotSnapshot.Capture(m);
            if (s.Layers.Length != shared.Layers.Length || s.ActiveLayerId != shared.ActiveLayerId)
                return false;
            for (int i = 0; i < s.Layers.Length; i++)
            {
                s.Layers[i].DatasetHash = shared.Layers[i].DatasetHash;
                if (!s.Layers[i].SameAs(shared.Layers[i]))
                    return false;
            }
            return true;
        }
    }
}
