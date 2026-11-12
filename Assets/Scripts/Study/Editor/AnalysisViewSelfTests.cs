using System.Collections.Generic;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>
    /// PF-05 to PF-07: the analysis buttons (PreparedViews.Analysis) on the converter's movement and
    /// moles columns, DATA-01's SegmentHitOffset and SegmentTime included: each layer resolves, a
    /// missing column is named, the slowest-3 pick (PreparedViews.TopKeys: ties, fewer moles,
    /// missing columns) and the stacks ApplyLayers builds, the MoleOrder filter of PF-06 included.
    /// The dashboard buttons themselves (one undo step, one ApplyView row) go through the same
    /// ApplyLayeredView path as the layered views and need a panel; they are not exercised here.
    /// </summary>
    public static class AnalysisViewSelfTests
    {
        const string k_Movement = "PF5_part1_movement.cdataset";
        const string k_Moles = "PF5_part1_moles.cdataset";
        const string k_OldMovement = "PF5_part1_old_movement.cdataset";

        // Movement rows of 4 moles (2 rows each); mole 2 was not hit, so its rows have no SegmentHitOffset.
        static DatasetColumnar Movement(string name, bool dataColumns = true, bool moleOrder = true)
        {
            var cols = new List<Col> { Col.Num("Time", 0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f) };
            if (moleOrder) cols.Add(Col.Num("MoleOrder", 1f, 1f, 2f, 2f, 3f, 3f, 4f, 4f));
            cols.Add(Col.Num("LaserX", 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f));
            cols.Add(Col.Num("LaserY", 1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f));
            if (dataColumns)
            {
                cols.Add(Col.Num("SegmentHitOffset", 0.01f, 0.01f, null, null, 0.03f, 0.03f, 0.02f, 0.02f));
                cols.Add(Col.Num("SegmentTime", 1f, 1f, 3f, 3f, 2f, 2f, 3f, 3f));
            }
            return SelfTestKit.Dataset(name, cols.ToArray());
        }

        // Moles 1..4: ReactionTimeOrLifetime 1, 3, 2, 3 -> slowest three are 2 and 4 (tie, lower first) then 3.
        static DatasetColumnar Moles(string name) => SelfTestKit.Dataset(name,
            Col.Num("MoleOrder", 1f, 2f, 3f, 4f),
            Col.Cat("MoleOrderLabel", "Mole 01", "Mole 02", "Mole 03", "Mole 04"),
            Col.Num("MoleX", 0.1f, 0.3f, 0.5f, 0.7f),
            Col.Num("MoleY", 1.1f, 1.3f, 1.5f, 1.7f),
            Col.Num("SpawnTime", 0f, 2f, 4f, 6f),
            Col.Num("ReactionTimeOrLifetime", 1f, 3f, 2f, 3f),
            Col.Num("HitOffsetX", 0.01f, null, 0.02f, -0.01f),
            Col.Num("HitOffsetY", -0.01f, null, 0.02f, 0.01f));

        static PreparedView View(string id) => PreparedViews.Find(id);

        [SelfTest("PF-05..07: analysis views listed apart")]
        static void Listed(SelfTestTally t)
        {
            t.Eq("All unchanged", 3, PreparedViews.All.Count);
            t.Eq("Layered unchanged", 3, PreparedViews.Layered.Count);
            t.Eq("Analysis count", 3, PreparedViews.Analysis.Count);
            t.Eq("titles", "Accuracy path|Where was I slow|Hit position",
                string.Join("|", new List<PreparedView>(PreparedViews.Analysis).ConvertAll(v => v.Title)));
            foreach (PreparedView v in PreparedViews.Analysis)
                t.True("found by id " + v.Id, View(v.Id) == v && v.Layers != null && v.Layers.Length > 0);
            t.True("only PF-06 highlights", View("accuracy-path").Highlight == null && View("hit-position").Highlight == null
                                            && View("where-slow").Highlight != null);
            PreparedHighlight h = View("where-slow").Highlight;
            t.Eq("PF-06 count 3", 3, h.Count);
            t.True("PF-06 count fits the filter ranges", h.Count <= MultiplayerScatterplotManager.MaxFilterRanges);
            t.Eq("PF-06 rank column", "ReactionTimeOrLifetime", h.RankColumn);
            t.Eq("PF-06 key column", "MoleOrder", h.KeyColumn);
            t.Eq("PF-06 rank layer = moles", PreparedViews.KindMoles, View("where-slow").Layers[h.RankLayer].Kind);
        }

        [SelfTest("PF-06: slowest three moles")]
        static void Slowest(SelfTestTally t)
        {
            List<float> keys = PreparedViews.TopKeys(Moles(k_Moles), "ReactionTimeOrLifetime", "MoleOrder", 3, out string missing);
            t.Eq("ties by MoleOrder", "2, 4, 3", PreparedViews.KeyText(keys));
            t.Eq("no missing", "", missing);

            DatasetColumnar two = SelfTestKit.Dataset("two", Col.Num("MoleOrder", 1f, 2f), Col.Num("ReactionTimeOrLifetime", 0.5f, 0.9f));
            t.Eq("fewer than 3 moles", "2, 1", PreparedViews.KeyText(PreparedViews.TopKeys(two, "ReactionTimeOrLifetime", "MoleOrder", 3, out _)));

            DatasetColumnar none = SelfTestKit.Dataset("none", Col.Num("MoleOrder"), Col.Num("ReactionTimeOrLifetime"));
            keys = PreparedViews.TopKeys(none, "ReactionTimeOrLifetime", "MoleOrder", 3, out missing);
            t.True("no moles: empty, not null", keys != null && keys.Count == 0);

            DatasetColumnar gaps = SelfTestKit.Dataset("gaps",
                Col.Num("MoleOrder", 1f, 2f, 3f, 4f, 5f),
                Col.Num("ReactionTimeOrLifetime", 2f, null, 9f, 2f, 2f));
            t.Eq("empty rank skipped, three-way tie by MoleOrder", "3, 1, 4",
                PreparedViews.KeyText(PreparedViews.TopKeys(gaps, "ReactionTimeOrLifetime", "MoleOrder", 3, out _)));

            DatasetColumnar dup = SelfTestKit.Dataset("dup", Col.Num("MoleOrder", 1f, 1f, 2f), Col.Num("ReactionTimeOrLifetime", 5f, 4f, 3f));
            t.Eq("a key counts once", "1, 2", PreparedViews.KeyText(PreparedViews.TopKeys(dup, "ReactionTimeOrLifetime", "MoleOrder", 3, out _)));

            DatasetColumnar noRank = SelfTestKit.Dataset("norank", Col.Num("MoleOrder", 1f, 2f));
            keys = PreparedViews.TopKeys(noRank, "ReactionTimeOrLifetime", "MoleOrder", 3, out missing);
            t.True("missing rank column: null", keys == null);
            t.Eq("missing rank column named", "ReactionTimeOrLifetime", missing);
            keys = PreparedViews.TopKeys(SelfTestKit.Dataset("empty", Col.Num("Time", 1f)), "ReactionTimeOrLifetime", "MoleOrder", 3, out missing);
            t.Eq("both missing named", "ReactionTimeOrLifetime, MoleOrder", missing);
            t.True("null dataset", PreparedViews.TopKeys(null, "ReactionTimeOrLifetime", "MoleOrder", 3, out missing) == null
                                   && missing == "(no dataset)");
            t.Eq("key text", "12, 7.5", PreparedViews.KeyText(new List<float> { 12f, 7.5f }));
        }

        [SelfTest("PF-05..07: views resolve on the converter columns")]
        static void Resolve(SelfTestTally t)
        {
            DatasetColumnar movement = Movement(k_Movement), moles = Moles(k_Moles);

            PreparedView accuracy = View("accuracy-path");
            t.True("PF-05 path on movement", PreparedViews.TryResolve(accuracy.Layers[0], movement, out ResolvedLayer c, out string missing));
            t.Eq("PF-05 path colour SegmentHitOffset", 4, c.Color);
            t.Eq("PF-05 path X LaserX", 2, c.X);
            t.Eq("PF-05 path Z Time", 0, c.Z);
            t.Eq("PF-05 path no time column", -1, c.Time);
            t.True("PF-05 moles on moles", PreparedViews.TryResolve(accuracy.Layers[1], moles, out c, out _));
            t.Eq("PF-05 moles fixed colour", -1, c.Color);
            t.Eq("PF-05 moles X", 2, c.X);
            t.True("PF-05 moles has a fixed colour", accuracy.Layers[1].FixedColour.HasValue);

            PreparedView slow = View("where-slow");
            t.True("PF-06 path on movement", PreparedViews.TryResolve(slow.Layers[0], movement, out c, out _));
            t.Eq("PF-06 path colour SegmentTime", 5, c.Color);
            t.True("PF-06 moles on moles", PreparedViews.TryResolve(slow.Layers[1], moles, out c, out _));
            t.Eq("PF-06 moles colour ReactionTimeOrLifetime", 5, c.Color);
            t.Eq("PF-06 moles label MoleOrder", 0, c.Label);

            PreparedView hits = View("hit-position");
            t.Eq("PF-07 one layer", 1, hits.Layers.Length);
            t.Eq("PF-07 reads moles", PreparedViews.KindMoles, hits.Layers[0].Kind);
            t.True("PF-07 on moles", PreparedViews.TryResolve(hits.Layers[0], moles, out c, out _));
            t.Eq("PF-07 X HitOffsetX", 6, c.X);
            t.Eq("PF-07 Y HitOffsetY", 7, c.Y);
            t.Eq("PF-07 Z MoleOrder", 0, c.Z);
            t.Eq("PF-07 colour MoleOrderLabel", 1, c.Color);

            t.True("PF-07 on movement fails", !PreparedViews.TryResolve(hits.Layers[0], movement, out _, out missing));
            t.Eq("PF-07 on movement names the columns", "HitOffsetX, HitOffsetY, MoleOrderLabel", missing);
            t.True("PF-05 path on moles fails", !PreparedViews.TryResolve(accuracy.Layers[0], moles, out _, out missing));
            t.Eq("PF-05 path on moles names the columns", "LaserX, LaserY, Time, SegmentHitOffset", missing);

            t.Eq("PF-05 files", "P07_part2_movement.cdataset,P07_part2_moles.cdataset",
                string.Join(",", PreparedViews.MissingFiles(accuracy, "07", 2, new string[0])));
            t.Eq("PF-07 file", "P07_part1_moles.cdataset",
                string.Join(",", PreparedViews.MissingFiles(hits, "p07", 1, new[] { "P07_part1_movement.cdataset" })));
        }

        [SelfTest("PF-05..07: missing column reported by name")]
        static void MissingColumns(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                // A movement file converted before DATA-01.
                var oldFiles = new List<string> { k_OldMovement, k_Moles };
                t.True("PF-05 refuses old movement", !PreparedViews.CheckLayers(View("accuracy-path"), oldFiles, out string error));
                t.Eq("PF-05 message", k_OldMovement + " has no column SegmentHitOffset: re-run the converter", error);
                t.True("PF-06 refuses old movement", !PreparedViews.CheckLayers(View("where-slow"), oldFiles, out error));
                t.Eq("PF-06 message", k_OldMovement + " has no column SegmentTime: re-run the converter", error);

                var files = new List<string> { k_Movement, k_Moles };
                t.True("PF-05 accepts new files", PreparedViews.CheckLayers(View("accuracy-path"), files, out _));
                t.True("PF-06 accepts new files", PreparedViews.CheckLayers(View("where-slow"), files, out _));
                t.True("PF-07 accepts moles", PreparedViews.CheckLayers(View("hit-position"), new List<string> { k_Moles }, out _));
                t.True("PF-07 refuses movement", !PreparedViews.CheckLayers(View("hit-position"), new List<string> { k_Movement }, out error));
                t.Eq("PF-07 message", k_Movement + " has no column HitOffsetX, HitOffsetY, MoleOrderLabel: re-run the converter", error);
            }, (k_Movement, Movement(k_Movement)), (k_Moles, Moles(k_Moles)), (k_OldMovement, Movement(k_OldMovement, dataColumns: false)));

            // The highlight's key column on the path layer (not named by any mapping) is checked too.
            const string noKey = "PF5_part1_nokey_movement.cdataset";
            SelfTestKit.WithManager(m =>
            {
                var files = new List<string> { noKey, k_Moles };
                t.True("PF-06 path resolves without MoleOrder", PreparedViews.TryResolve(View("where-slow").Layers[0], DatasetRegistry.Get(noKey), out _, out _));
                t.True("PF-06 refuses no MoleOrder", !PreparedViews.CheckLayers(View("where-slow"), files, out string error));
                t.Eq("PF-06 key message", noKey + " has no column MoleOrder: re-run the converter", error);
                t.True("PF-05 does not need MoleOrder on the path", PreparedViews.CheckLayers(View("accuracy-path"), files, out _));
            }, (noKey, Movement(noKey, moleOrder: false)), (k_Moles, Moles(k_Moles)));
        }

        [SelfTest("PF-05..07: ApplyLayers builds the views")]
        static void Apply(SelfTestTally t)
        {
            DatasetColumnar movement = Movement(k_Movement), moles = Moles(k_Moles);
            SelfTestKit.WithManager(m =>
            {
                var files = new List<string> { k_Movement, k_Moles };

                string error = PreparedViews.ApplyLayers(m, View("where-slow"), files);
                t.Eq("PF-06 applied", "", error ?? "");
                t.Eq("PF-06 two layers", 2, m.Layers.Count);
                if (m.Layers.Count == 2)
                {
                    PlotLayer path = m.Layers[0], targets = m.Layers[1];
                    t.Eq("PF-06 path colour", 5, path.ColorColumnIndex);
                    t.Eq("PF-06 moles colour", 5, targets.ColorColumnIndex);
                    t.Eq("PF-06 path one filter", 1, path.Filters.Count);
                    t.Eq("PF-06 moles one filter", 1, targets.Filters.Count);
                    if (path.Filters.Count == 1 && targets.Filters.Count == 1)
                    {
                        t.Eq("PF-06 path filter on MoleOrder", 1, path.Filters[0].ColumnIndex);
                        t.Eq("PF-06 moles filter on MoleOrder", 0, targets.Filters[0].ColumnIndex);
                        t.Eq("PF-06 three ranges", 3, path.Filters[0].Ranges.Count);
                    }
                    t.True("PF-06 filters dim, not hide", !path.FiltersExclude && !targets.FiltersExclude);

                    var passing = new List<string>();
                    for (int r = 0; r < movement.RowCount; r++)
                        if (LayerRows.PassesFilters(path, movement, r)) passing.Add(r.ToString());
                    t.Eq("PF-06 path rows at full opacity: moles 2, 3, 4", "2,3,4,5,6,7", string.Join(",", passing));
                    passing.Clear();
                    for (int r = 0; r < moles.RowCount; r++)
                        if (LayerRows.PassesFilters(targets, moles, r)) passing.Add(r.ToString());
                    t.Eq("PF-06 moles at full opacity: 2, 3, 4", "1,2,3", string.Join(",", passing));
                    t.Eq("PF-06 path active", path.Id, m.ActiveLayerId);
                }

                error = PreparedViews.ApplyLayers(m, View("accuracy-path"), files);
                t.Eq("PF-05 applied", "", error ?? "");
                if (m.Layers.Count == 2)
                {
                    PlotLayer path = m.Layers[0], targets = m.Layers[1];
                    t.Eq("PF-05 path colour SegmentHitOffset", 4, path.ColorColumnIndex);
                    t.Eq("PF-05 path no filter (PF-06's cleared)", 0, path.Filters.Count);
                    t.Eq("PF-05 moles no filter", 0, targets.Filters.Count);
                    t.Eq("PF-05 moles fixed colour", -1, targets.ColorColumnIndex);
                    t.True("PF-05 moles orange", LayerSnapshot.PackColour(targets.FixedColour) == LayerSnapshot.PackColour(new Color32(242, 166, 38, 255)));
                    t.Eq("PF-05 path name", "Path", path.Name);
                    t.Eq("PF-05 moles name", "Moles", targets.Name);
                }

                error = PreparedViews.ApplyLayers(m, View("hit-position"), new List<string> { k_Moles });
                t.Eq("PF-07 applied", "", error ?? "");
                t.Eq("PF-07 one layer", 1, m.Layers.Count);
                if (m.Layers.Count == 1)
                {
                    PlotLayer hits = m.Layers[0];
                    t.Eq("PF-07 X", 6, hits.XColumnIndex);
                    t.Eq("PF-07 Y", 7, hits.YColumnIndex);
                    t.Eq("PF-07 Z", 0, hits.ZColumnIndex);
                    t.Eq("PF-07 colour", 1, hits.ColorColumnIndex);
                    t.Eq("PF-07 name", "Hits", hits.Name);
                    // The unhit mole has no HitOffsetX/Y: the axis rule leaves it out (one point per hit).
                    int withHit = 0;
                    for (int r = 0; r < moles.RowCount; r++)
                        if (moles.HasValue(r, hits.XColumnIndex) && moles.HasValue(r, hits.YColumnIndex)) withHit++;
                    t.Eq("PF-07 three moles with a hit position", 3, withHit);
                }
            }, (k_Movement, movement), (k_Moles, moles));
        }
    }
}
