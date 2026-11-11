using System;
using System.Collections.Generic;
using DataViz.Networking;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>WP9 checks for Shuffle across layers (LFR-32): which layers, the client wire entries, shuffle and back/forward over two layers.</summary>
    public static class ShuffleLayerSelfTests
    {
        // Five numeric columns, so every unlocked axis always has another label to move to.
        static DatasetColumnar Five(string name, float offset) => SelfTestKit.Dataset(name,
            Col.Num("A0", 0f + offset, 1f, 2f),
            Col.Num("A1", 0f, 10f + offset, 20f),
            Col.Num("A2", 5f, 6f, 70f + offset),
            Col.Num("A3", -3f - offset, 0f, 3f),
            Col.Num("A4", 1f, 2f, 300f + offset));

        static PlotLayer L(string id, LayerKind kind) => new PlotLayer { Id = id, Kind = kind };

        static string Join(List<string> ids) => string.Join(",", ids);

        static string Axes(MultiplayerScatterplotManager m, string layerId)
        {
            using (m.EditLayerScope(layerId))
                return $"{m.XColumnIndex}/{m.YColumnIndex}/{m.ZColumnIndex}";
        }

        [SelfTest("Shuffle layers: first layer only by default, unlocked added, computed never")]
        static void LayersToShuffle(SelfTestTally t)
        {
            var layers = new List<PlotLayer>
            {
                L("L1", LayerKind.Dataset), L("L2", LayerKind.Dataset), L("L3", LayerKind.Computed), L("L4", LayerKind.Subset),
            };
            var unlocked = new HashSet<string>();

            t.Eq("default first only", "L1", Join(ShuffleLocks.LayersToShuffle(layers, unlocked.Contains)));
            t.Eq("null predicate first only", "L1", Join(ShuffleLocks.LayersToShuffle(layers, null)));

            unlocked.Add("L2"); unlocked.Add("L3"); unlocked.Add("L4");
            t.Eq("unlocked added, computed never", "L1,L2,L4", Join(ShuffleLocks.LayersToShuffle(layers, unlocked.Contains)));

            unlocked.Add("L1");
            t.Eq("first layer listed once", "L1,L2,L4", Join(ShuffleLocks.LayersToShuffle(layers, unlocked.Contains)));

            var computedFirst = new List<PlotLayer> { L("L3", LayerKind.Computed), L("L1", LayerKind.Dataset) };
            t.Eq("computed first layer skipped", "", Join(ShuffleLocks.LayersToShuffle(computedFirst, id => false)));
            t.Eq("computed first, unlocked second", "L1", Join(ShuffleLocks.LayersToShuffle(computedFirst, id => id == "L1")));

            t.Eq("null layers", "", Join(ShuffleLocks.LayersToShuffle(null, id => true)));
            t.Eq("empty stack", "", Join(ShuffleLocks.LayersToShuffle(new List<PlotLayer>(), id => true)));
        }

        [SelfTest("Shuffle layers: client request entries carry ids and per-layer lock masks")]
        static void LayerEntries(SelfTestTally t)
        {
            string text = ShuffleLocks.FormatLayerEntries(new[] { "L1", "L3" }, new[] { 7, 120 });
            t.Eq("format", "L1:7,L3:120", text);
            t.Eq("format no masks", "L1,L3", ShuffleLocks.FormatLayerEntries(new[] { "L1", "L3" }, null));
            t.Eq("format empty", "", ShuffleLocks.FormatLayerEntries(new string[0], null));

            PlotRequest r = PlotRequest.ForShuffle(7, new[] { "A1" }, text);
            t.Eq("request entry count", 2, r.LayerIds.Length);

            t.True("parse 1", ShuffleLocks.ParseLayerEntry(r.LayerIds[0], 99, out string id, out int mask));
            t.Eq("parse 1 id", "L1", id);
            t.Eq("parse 1 mask", 7, mask);
            t.True("parse 2", ShuffleLocks.ParseLayerEntry(r.LayerIds[1], 99, out id, out mask));
            t.Eq("parse 2 id", "L3", id);
            t.Eq("parse 2 mask", 120, mask);

            t.True("parse bare id", ShuffleLocks.ParseLayerEntry("L2", 99, out id, out mask));
            t.Eq("bare id", "L2", id);
            t.Eq("bare id default mask", 99, mask);
            t.True("parse bad mask", ShuffleLocks.ParseLayerEntry("L2:x", 5, out id, out mask));
            t.Eq("bad mask default", 5, mask);
            t.True("parse empty false", !ShuffleLocks.ParseLayerEntry("", 5, out _, out _));
            t.True("parse colon only false", !ShuffleLocks.ParseLayerEntry(":3", 5, out _, out _));
        }

        [SelfTest("Shuffle layers: manager shuffles first + unlocked layers; back/forward over two layers")]
        static void ManagerShuffleAndHistory(SelfTestTally t)
        {
            DatasetColumnar a = Five("wp9_a.cdataset", 0f), b = Five("wp9_b.cdataset", 1f);
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("wp9_a.cdataset");
                m.RequestAddDatasetLayerRpc("wp9_b.cdataset");
                t.Eq("setup two layers", 2, m.Layers.Count);
                t.Eq("setup active is L2", "L2", m.ActiveLayerId);

                string a0 = Axes(m, "L1"), b0 = Axes(m, "L2");

                // Default: the first layer changes, the active (second) layer does not.
                m.RequestShuffleColumnsRpc();
                string a1 = Axes(m, "L1");
                t.True("first layer shuffled", a1 != a0);
                t.Eq("active second layer untouched", b0, Axes(m, "L2"));

                // Unlocked: both change.
                m.RequestSetLayerShuffleUnlockedRpc("L2", true);
                t.True("unlock stored", m.IsLayerShuffleUnlocked("L2"));
                m.RequestShuffleColumnsRpc();
                string a2 = Axes(m, "L1"), b2 = Axes(m, "L2");
                t.True("first layer shuffled again", a2 != a1);
                t.True("unlocked layer shuffled", b2 != b0);

                // Back restores both layers to what was shown before the second press, then the first layer alone.
                m.RequestShuffleBackwardRpc();
                t.Eq("back 1 first layer", a1, Axes(m, "L1"));
                t.Eq("back 1 second layer", b0, Axes(m, "L2"));
                m.RequestShuffleBackwardRpc();
                t.Eq("back 2 first layer", a0, Axes(m, "L1"));
                t.Eq("back 2 second layer", b0, Axes(m, "L2"));
                m.RequestShuffleBackwardRpc();
                t.Eq("back past oldest no-op", a0, Axes(m, "L1"));

                m.RequestShuffleForwardRpc();
                t.Eq("forward 1 first layer", a1, Axes(m, "L1"));
                t.Eq("forward 1 second layer", b0, Axes(m, "L2"));
                m.RequestShuffleForwardRpc();
                t.Eq("forward 2 first layer", a2, Axes(m, "L1"));
                t.Eq("forward 2 second layer", b2, Axes(m, "L2"));
                m.RequestShuffleForwardRpc();
                t.Eq("forward past newest no-op", b2, Axes(m, "L2"));

                // A computed layer, even unlocked, is never shuffled and does not stop the others.
                m.RequestAddComputedLayerRpc("L1", ComputedLayerKind.Trendline);
                string computedId = m.ActiveLayerId;
                t.True("computed added", m.Stack.Find(computedId)?.Kind == LayerKind.Computed);
                m.RequestSetLayerShuffleUnlockedRpc(computedId, true);
                PlotLayer computed = m.Stack.Find(computedId);
                string cBefore = $"{computed.XColumnIndex}/{computed.YColumnIndex}/{computed.ZColumnIndex}";
                m.RequestShuffleColumnsRpc();
                t.True("shuffle with computed active still shuffles first layer", Axes(m, "L1") != a2);
                t.Eq("computed layer untouched", cBefore, $"{computed.XColumnIndex}/{computed.YColumnIndex}/{computed.ZColumnIndex}");

                // First layer computed and nothing else unlocked: refused with a message, nothing changes.
                m.RequestSetLayerShuffleUnlockedRpc("L2", false);
                m.RequestReorderLayerRpc(computedId, 0);
                t.Eq("computed moved first", computedId, m.Layers[0].Id);
                var messages = new List<string>();
                Action<string, PlotMessageKind> onMessage = (text, kind) => { if (kind == PlotMessageKind.Refused) messages.Add(text); };
                m.OnPlotMessage += onMessage;
                string aBefore = Axes(m, "L1"), bBefore = Axes(m, "L2");
                m.RequestShuffleColumnsRpc();
                m.OnPlotMessage -= onMessage;
                t.Eq("computed first refused", 1, messages.Count);
                t.Eq("computed first: L1 untouched", aBefore, Axes(m, "L1"));
                t.Eq("computed first: L2 untouched", bBefore, Axes(m, "L2"));
            }, ("wp9_a.cdataset", a), ("wp9_b.cdataset", b));
        }

        [SelfTest("Shuffle layers: host applies a client's layer list, masks and blacklist")]
        static void ClientRequest(SelfTestTally t)
        {
            DatasetColumnar a = Five("wp9_a.cdataset", 0f), b = Five("wp9_b.cdataset", 1f);
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("wp9_a.cdataset");
                m.RequestAddDatasetLayerRpc("wp9_b.cdataset");
                using (m.EditLayerScope("L2"))
                {
                    m.RequestXColumnRpc(0);
                    m.RequestYColumnRpc(1);
                    m.RequestZColumnRpc(2);
                }
                string a0 = Axes(m, "L1");

                // Only L2, everything but X locked, and every column but A4 blacklisted: X must become A4.
                int onlyXUnlocked = 0xFF & ~(1 << (int)MappingTarget.X);
                PlotRequest r = PlotRequest.ForShuffle(0, new[] { "A0", "A1", "A2", "A3" },
                    ShuffleLocks.FormatLayerEntries(new[] { "L2", "L99" }, new[] { onlyXUnlocked, 0 }));
                m.RequestShuffleColumnsForClient(r.Int, r.Blacklist, r.LayerIds);

                t.Eq("client: L1 not in list, untouched", a0, Axes(m, "L1"));
                t.Eq("client: L2 X from blacklist + mask", "4/1/2", Axes(m, "L2"));

                // The client's blacklist applied only during its request.
                using (m.EditLayerScope("L2"))
                    t.True("client blacklist not kept", !m.IsColumnBlacklisted("A0"));

                // No layer list: the host chooses (its first layer).
                m.RequestShuffleColumnsForClient(0, Array.Empty<string>());
                t.True("client without list: first layer shuffled", Axes(m, "L1") != a0);
            }, ("wp9_a.cdataset", a), ("wp9_b.cdataset", b));
        }
    }
}
