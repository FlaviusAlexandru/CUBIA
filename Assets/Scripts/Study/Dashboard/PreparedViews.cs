using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DataViz.Study
{
    /// <summary>
    /// One prepared view (pilot finding P11, spec Q17): which converter output it reads and which
    /// column goes to which mapping. Column names; null = leave that mapping on "None".
    /// </summary>
    public sealed class PreparedView
    {
        public string Id;
        public string Title;
        public string Kind;   // PreparedViews.KindMovement / KindCombined / KindMoles
        public string X, Y, Z;
        public string Color;
        public string Size;
        public string Time;
        public string Glyph;
        /// <summary>Glyph shape per label of the Glyph column (index into ScatterplotUI.k_GlyphShapeNames). May be null.</summary>
        public (string label, int glyph)[] GlyphShapes;
        /// <summary>
        /// LFR-27: the whole layer stack, base layer first. Null = the single-layer view above
        /// (Kind and the column fields); views saved before layers stay single-layer plots (LFR-31).
        /// </summary>
        public PreparedLayer[] Layers;
        /// <summary>
        /// PF-06: rows to keep at full opacity, worked out from the data when the view is applied;
        /// every other row is dimmed by a filter on each layer. Null = no highlight.
        /// </summary>
        public PreparedHighlight Highlight;
    }

    /// <summary>
    /// PF-06: the <see cref="Count"/> rows of layer <see cref="RankLayer"/> with the highest
    /// <see cref="RankColumn"/> (ties: lowest <see cref="KeyColumn"/> first) pick their
    /// KeyColumn values; every layer then gets a filter on KeyColumn with one range per value, so
    /// the matching rows are drawn as usual and the rest stay drawn, dimmed (CUBIA's filter dim).
    /// </summary>
    public sealed class PreparedHighlight
    {
        public int RankLayer;      // index into PreparedView.Layers
        public string RankColumn;  // e.g. ReactionTimeOrLifetime
        public string KeyColumn;   // e.g. MoleOrder; needed on every layer
        public int Count = 3;      // at most MultiplayerScatterplotManager.MaxFilterRanges
    }

    /// <summary>
    /// One layer of a layered prepared view: which converter output (and which part) it reads and
    /// how it is drawn. Column names; null = "None".
    /// </summary>
    public sealed class PreparedLayer
    {
        public string Name;   // layer name in the stack and the log; null = the file name
        public string Kind;   // PreparedViews.KindMovement / KindMoles / KindCombined
        public int Part;      // 0 = the part picked in Session; 1 or 2 = always that part (LFR-34)
        public string X, Y, Z;
        public string Color;  // colour column; null = FixedColour
        public string Size;
        public string Time;
        public string EndTime; // used with LayerTimeBehaviour.Span
        public LayerTimeBehaviour TimeBehaviour = LayerTimeBehaviour.Follow;
        public string Glyph;
        public (string label, int glyph)[] GlyphShapes;
        public int DefaultGlyph = PreparedViews.GlyphCircle;
        public Color32? FixedColour; // null = the layer's default colour
        public string Label;   // label column (LFR-17)
    }

    /// <summary>Column indices of a PreparedLayer in one dataset (-1 = none).</summary>
    public struct ResolvedLayer
    {
        public int X, Y, Z, Color, Size, Time, EndTime, Glyph, Label;
    }

    /// <summary>
    /// The three one-click views for the current participant and part, built from the datasets the
    /// WAM converter writes as {participant}_part{part}_{kind}.cdataset. No Unity UI types,
    /// so StudySelfTests can check it.
    /// </summary>
    public static class PreparedViews
    {
        public const string KindMovement = "movement";
        public const string KindCombined = "combined";
        public const string KindMoles = "moles";
        public const string Extension = ".cdataset";

        // Indices into ScatterplotUI.k_GlyphShapeNames { Circle, Square, Triangle, Diamond, Cross, Star }.
        public const int GlyphCircle = 0;
        public const int GlyphStar = 5;

        public static readonly IReadOnlyList<PreparedView> All = new List<PreparedView>
        {
            new PreparedView
            {
                Id = "controller-path",
                Title = "Controller path",
                Kind = KindMovement,
                X = "ControllerX", Y = "ControllerY", Z = "ControllerZ",
                Color = "ControllerSpeed",
                Time = "Time",
            },
            new PreparedView
            {
                Id = "targets-cursor",
                Title = "Targets and cursor",
                Kind = KindCombined,
                X = "X", Y = "Y", Z = "Time", // Time on Z spreads the path in depth
                Color = "MoleOrderLabel",
                Glyph = "RowType",
                GlyphShapes = new[] { ("Cursor", GlyphCircle), ("Mole", GlyphStar) },
            },
            new PreparedView
            {
                Id = "accuracy-target",
                Title = "Accuracy per target",
                Kind = KindMoles,
                X = "MoleX", Y = "MoleY", Z = "SpawnTime",
                Color = "HitOffset",
                Size = "ReactionTimeOrLifetime",
            },
        };

        // Fixed colours of the layered views (PlayerProfile.Palette cyan and orange: far apart, both bright on black).
        static readonly Color32 k_CursorColour = new Color32(64, 179, 217, 255);
        static readonly Color32 k_MoleColour = new Color32(242, 166, 38, 255);

        static PreparedLayer CursorLayer(string name, int part, Color32 colour) => new PreparedLayer
        {
            Name = name,
            Kind = KindMovement,
            Part = part,
            X = "LaserX", Y = "LaserY", Z = "Time", // the laser dot on the wall, time in depth (as combined's X/Y/Time)
            Time = "Time",
            FixedColour = colour,
        };

        static PreparedLayer MoleLayer(LayerTimeBehaviour behaviour) => new PreparedLayer
        {
            Name = "Moles",
            Kind = KindMoles,
            X = "MoleX", Y = "MoleY", Z = "SpawnTime",
            Time = "SpawnTime",
            EndTime = behaviour == LayerTimeBehaviour.Span ? "EndTime" : null,
            TimeBehaviour = behaviour,
            DefaultGlyph = GlyphStar,
            FixedColour = k_MoleColour,
            Label = "MoleOrder",
        };

        /// <summary>
        /// LFR-27 / LFR-34: views that build a layer stack. Kept apart from <see cref="All"/> (the
        /// three original buttons and the files the Session status checks).
        /// </summary>
        public static readonly IReadOnlyList<PreparedView> Layered = new List<PreparedView>
        {
            new PreparedView
            {
                Id = "targets-cursor-layers",
                Title = "Targets and cursor (layers)",
                Layers = new[] { CursorLayer("Cursor", 0, k_CursorColour), MoleLayer(LayerTimeBehaviour.Always) },
            },
            new PreparedView
            {
                Id = "targets-until-hit",
                Title = "Targets until hit (layers)",
                Layers = new[] { CursorLayer("Cursor", 0, k_CursorColour), MoleLayer(LayerTimeBehaviour.Span) },
            },
            new PreparedView
            {
                Id = "part1-part2",
                Title = "Part 1 and part 2",
                Layers = new[] { CursorLayer("Part 1", 1, k_CursorColour), CursorLayer("Part 2", 2, k_MoleColour) },
            },
        };

        // PF-05 to PF-07: analysis views on the converter's DATA-01 columns (facilitator buttons, Q6).
        static PreparedLayer PathLayer(string colour) => new PreparedLayer
        {
            Name = "Path",
            Kind = KindMovement,
            X = "LaserX", Y = "LaserY", Z = "Time", // the laser dot on the wall, time in depth (as the cursor layers)
            Color = colour,
            TimeBehaviour = LayerTimeBehaviour.Always, // no time column: the whole path at once
        };

        static PreparedLayer TargetLayer(string colour) => new PreparedLayer
        {
            Name = "Moles",
            Kind = KindMoles,
            X = "MoleX", Y = "MoleY", Z = "SpawnTime", // same frame as PathLayer
            Color = colour,
            TimeBehaviour = LayerTimeBehaviour.Always,
            DefaultGlyph = GlyphStar,
            FixedColour = colour == null ? k_MoleColour : (Color32?)null,
            Label = "MoleOrder",
        };

        /// <summary>Movement column of PF-05 (DATA-01): the HitOffset of the mole a row leads to; empty = not hit.</summary>
        public const string SegmentHitOffset = "SegmentHitOffset";
        /// <summary>Movement column of PF-06 (DATA-01): the ReactionTimeOrLifetime of the mole a row leads to.</summary>
        public const string SegmentTime = "SegmentTime";
        /// <summary>PF-06: how many of the slowest moles stay at full opacity.</summary>
        public const int SlowestCount = 3;

        /// <summary>
        /// PF-05 to PF-07: the analysis buttons (kept apart from <see cref="All"/> and
        /// <see cref="Layered"/>, whose lists and tests stay as they were). Built like the layered views.
        /// </summary>
        public static readonly IReadOnlyList<PreparedView> Analysis = new List<PreparedView>
        {
            new PreparedView
            {
                Id = "accuracy-path",
                Title = "Accuracy path",
                // Rows leading to an unhit mole have no SegmentHitOffset: skipped with the usual warning.
                Layers = new[] { PathLayer(SegmentHitOffset), TargetLayer(null) },
            },
            new PreparedView
            {
                Id = "where-slow",
                Title = "Where was I slow",
                Layers = new[] { PathLayer(SegmentTime), TargetLayer("ReactionTimeOrLifetime") },
                Highlight = new PreparedHighlight
                {
                    RankLayer = 1, RankColumn = "ReactionTimeOrLifetime", KeyColumn = "MoleOrder", Count = SlowestCount,
                },
            },
            new PreparedView
            {
                Id = "hit-position",
                Title = "Hit position",
                Layers = new[]
                {
                    new PreparedLayer
                    {
                        Name = "Hits",
                        Kind = KindMoles,
                        // Z = MoleOrder: the hits in game order front to back, so hits on the same
                        // spot of the mole do not hide each other and each point stays one mole.
                        // Unhit moles have no HitOffsetX/Y and are skipped by the empty-cell rule.
                        X = "HitOffsetX", Y = "HitOffsetY", Z = "MoleOrder",
                        Color = "MoleOrderLabel",
                        TimeBehaviour = LayerTimeBehaviour.Always,
                        Label = "MoleOrder",
                    },
                },
            },
        };

        /// <summary>
        /// PF-06: the <paramref name="count"/> values of <paramref name="keyColumn"/> on the rows with
        /// the highest <paramref name="rankColumn"/>; ties go to the lower key. Rows without a rank or
        /// key value are left out; a key is listed once. Fewer rows give fewer keys. Null when a
        /// column is missing (<paramref name="missing"/> lists them, "MoleOrder, ReactionTimeOrLifetime")
        /// or the dataset is null ("(no dataset)").
        /// </summary>
        public static List<float> TopKeys(DatasetColumnar ds, string rankColumn, string keyColumn, int count, out string missing)
        {
            missing = "";
            if (ds == null)
            {
                missing = "(no dataset)";
                return null;
            }
            var absent = new List<string>();
            int rank = Resolve(ds, rankColumn, absent);
            int key = Resolve(ds, keyColumn, absent);
            if (string.IsNullOrEmpty(rankColumn) || string.IsNullOrEmpty(keyColumn) || absent.Count > 0)
            {
                missing = absent.Count > 0 ? string.Join(", ", absent) : "(no column named)";
                return null;
            }

            var rows = new List<(float rank, float key)>();
            for (int r = 0; r < ds.RowCount; r++)
            {
                if (!ds.HasValue(r, rank) || !ds.HasValue(r, key)) continue;
                float rv = ds.GetNumericValue(r, rank), kv = ds.GetNumericValue(r, key);
                if (float.IsFinite(rv) && float.IsFinite(kv)) rows.Add((rv, kv));
            }
            rows.Sort((a, b) => a.rank != b.rank ? b.rank.CompareTo(a.rank) : a.key.CompareTo(b.key));

            var keys = new List<float>();
            foreach (var row in rows)
            {
                if (keys.Count >= count) break;
                if (!keys.Contains(row.key)) keys.Add(row.key);
            }
            return keys;
        }

        /// <summary>
        /// PF-06: before anything changes, the highlight's columns exist (RankColumn and KeyColumn on
        /// the rank layer's file, KeyColumn on every other file). False with the same message
        /// CheckLayers gives ("P07_part1_movement.cdataset has no column MoleOrder: re-run the converter").
        /// </summary>
        public static bool CheckHighlight(PreparedView view, IReadOnlyList<string> files, out string error)
        {
            error = null;
            PreparedHighlight h = view?.Highlight;
            if (h == null) return true;
            if (view.Layers == null || files == null || files.Count != view.Layers.Length || h.RankLayer < 0 || h.RankLayer >= files.Count)
            {
                error = "(no layers)";
                return false;
            }
            for (int i = 0; i < files.Count; i++)
            {
                DatasetColumnar ds = DatasetRegistry.Get(files[i]);
                if (ds == null)
                {
                    error = $"Could not load {files[i]} (see Console)";
                    return false;
                }
                var absent = new List<string>();
                if (i == h.RankLayer) Resolve(ds, h.RankColumn, absent);
                Resolve(ds, h.KeyColumn, absent);
                if (absent.Count > 0)
                {
                    error = $"{files[i]} has no column {string.Join(", ", absent)}: re-run the converter";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Participant id as the session log names it ("07", "P07", "p07" → "P07"; only letters,
        /// digits, '-' and '_' kept; empty → "Punknown"). Taken from StudyLogWriter.BuildFileName so
        /// both stay identical: its name is "{id}_part{part}_{yyyyMMdd_HHmmss}.csv", so the fixed-length
        /// suffix is cut off.
        /// </summary>
        public static string NormaliseParticipant(string participantId)
        {
            DateTime stamp = new DateTime(2000, 1, 1);
            string name = StudyLogWriter.BuildFileName(participantId, 1, stamp);
            string suffix = "_part1_" + stamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
            return name.EndsWith(suffix, StringComparison.Ordinal) ? name.Substring(0, name.Length - suffix.Length) : name;
        }

        /// <summary>"07", 1, "movement" → "P07_part1_movement.cdataset".</summary>
        public static string FileFor(string participantId, int part, string kind)
        {
            return NormaliseParticipant(participantId) + "_part" + part.ToString(CultureInfo.InvariantCulture) +
                   "_" + (kind ?? "") + Extension;
        }

        /// <summary>
        /// The files of <see cref="All"/> for this participant and part that are not in
        /// <paramref name="available"/> (compared ignoring case, like ApplyPreparedView), in view order
        /// and without duplicates.
        /// </summary>
        public static List<string> MissingFiles(string participantId, int part, IEnumerable<string> available)
        {
            var have = new HashSet<string>(available ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (PreparedView v in All)
            {
                string file = FileFor(participantId, part, v.Kind);
                if (!have.Contains(file) && !missing.Contains(file)) missing.Add(file);
            }
            return missing;
        }

        /// <summary>The file a layer reads: its fixed part, else <paramref name="selectedPart"/>.</summary>
        public static string FileFor(string participantId, int selectedPart, PreparedLayer layer)
        {
            int part = layer != null && layer.Part > 0 ? layer.Part : selectedPart;
            return FileFor(participantId, part, layer?.Kind);
        }

        /// <summary>The files a layered view needs that are not in <paramref name="available"/> (ignoring case), in layer order.</summary>
        public static List<string> MissingFiles(PreparedView view, string participantId, int part, IEnumerable<string> available)
        {
            var have = new HashSet<string>(available ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            if (view?.Layers == null) return missing;
            foreach (PreparedLayer layer in view.Layers)
            {
                string file = FileFor(participantId, part, layer);
                if (!have.Contains(file) && !missing.Contains(file)) missing.Add(file);
            }
            return missing;
        }

        /// <summary>The view with this id (in All, Layered or Analysis), or null.</summary>
        public static PreparedView Find(string id)
        {
            foreach (PreparedView v in All)
                if (v.Id == id) return v;
            foreach (PreparedView v in Layered)
                if (v.Id == id) return v;
            foreach (PreparedView v in Analysis)
                if (v.Id == id) return v;
            return null;
        }

        /// <summary>
        /// Column names → indices in <paramref name="ds"/> (-1 for a null field). False when the view or
        /// dataset is null or any named column is missing; <paramref name="missing"/> then lists the
        /// missing names ("MoleX, MoleY"). Indices of the columns that were found are still set.
        /// </summary>
        public static bool TryResolve(PreparedView v, DatasetColumnar ds,
                                      out int x, out int y, out int z, out int color, out int size,
                                      out int time, out int glyph, out string missing)
        {
            x = y = z = color = size = time = glyph = -1;
            missing = "";
            if (v == null)
            {
                missing = "(no view)";
                return false;
            }
            if (ds == null)
            {
                missing = "(no dataset)";
                return false;
            }

            var absent = new List<string>();
            x = Resolve(ds, v.X, absent);
            y = Resolve(ds, v.Y, absent);
            z = Resolve(ds, v.Z, absent);
            color = Resolve(ds, v.Color, absent);
            size = Resolve(ds, v.Size, absent);
            time = Resolve(ds, v.Time, absent);
            glyph = Resolve(ds, v.Glyph, absent);

            missing = string.Join(", ", absent);
            return absent.Count == 0;
        }

        /// <summary>
        /// TryResolve for one layer of a layered view: every named column (EndTime and Label too)
        /// must exist in <paramref name="ds"/>; <paramref name="missing"/> lists the absent names.
        /// </summary>
        public static bool TryResolve(PreparedLayer layer, DatasetColumnar ds, out ResolvedLayer columns, out string missing)
        {
            columns = new ResolvedLayer { X = -1, Y = -1, Z = -1, Color = -1, Size = -1, Time = -1, EndTime = -1, Glyph = -1, Label = -1 };
            missing = "";
            if (layer == null)
            {
                missing = "(no layer)";
                return false;
            }
            if (ds == null)
            {
                missing = "(no dataset)";
                return false;
            }

            var absent = new List<string>();
            columns.X = Resolve(ds, layer.X, absent);
            columns.Y = Resolve(ds, layer.Y, absent);
            columns.Z = Resolve(ds, layer.Z, absent);
            columns.Color = Resolve(ds, layer.Color, absent);
            columns.Size = Resolve(ds, layer.Size, absent);
            columns.Time = Resolve(ds, layer.Time, absent);
            columns.EndTime = Resolve(ds, layer.EndTime, absent);
            columns.Glyph = Resolve(ds, layer.Glyph, absent);
            columns.Label = Resolve(ds, layer.Label, absent);

            missing = string.Join(", ", absent);
            return absent.Count == 0;
        }

        /// <summary>
        /// Before a layered view changes anything: every layer's file (files[i] for view.Layers[i])
        /// loads and has the view's columns. False with a message naming the file and the missing
        /// columns ("P07_part1_moles.cdataset has no column EndTime").
        /// </summary>
        public static bool CheckLayers(PreparedView view, IReadOnlyList<string> files, out string error)
        {
            error = null;
            if (view?.Layers == null || view.Layers.Length == 0 || files == null || files.Count != view.Layers.Length)
            {
                error = "(no layers)";
                return false;
            }
            for (int i = 0; i < view.Layers.Length; i++)
            {
                DatasetColumnar ds = DatasetRegistry.Get(files[i]);
                if (ds == null)
                {
                    error = $"Could not load {files[i]} (see Console)";
                    return false;
                }
                if (!TryResolve(view.Layers[i], ds, out _, out string missing))
                {
                    error = $"{files[i]} has no column {missing}: re-run the converter";
                    return false;
                }
            }
            return CheckHighlight(view, files, out error);
        }

        /// <summary>
        /// Builds a layered view's stack on <paramref name="m"/> (LFR-27): the first file replaces the
        /// plot (RequestLoadDatasetRpc, decision E4), each further file is added as a dataset layer, and
        /// each layer is configured through the manager's requests (mappings inside EditLayerScope,
        /// layer settings by id). The first layer ends up active, playback paused at the start.
        /// Returns null on success, else an error (the stack may then be partly built; the caller's
        /// undo step restores it). The caller wraps this in one facilitator change.
        /// </summary>
        public static string ApplyLayers(MultiplayerScatterplotManager m, PreparedView view, IReadOnlyList<string> files)
        {
            if (m == null || view?.Layers == null || files == null || files.Count != view.Layers.Length || files.Count == 0)
                return "(no layers)";

            m.RequestLoadDatasetRpc(files[0]); // synchronous on the host
            if (m.LoadedDataset == null || !SameFile(m.CurrentDatasetName, files[0]))
                return $"Could not load {files[0]} (see Console)";
            string firstId = m.ActiveLayerId;
            var ids = new List<string> { firstId };
            string error = ConfigureLayer(m, firstId, view.Layers[0]);
            if (error != null) return error;

            for (int i = 1; i < view.Layers.Length; i++)
            {
                string before = m.ActiveLayerId;
                m.RequestAddDatasetLayerRpc(files[i]);
                PlotLayer added = m.ActiveLayer;
                if (added == null || added.Id == before || added.Dataset == null || !SameFile(added.DatasetName, files[i]))
                    return $"Could not add {files[i]} as a layer (see Console)";
                ids.Add(added.Id);
                error = ConfigureLayer(m, added.Id, view.Layers[i]);
                if (error != null) return error;
            }

            if (view.Highlight != null)
            {
                error = ApplyHighlight(m, view.Highlight, ids);
                if (error != null) return error;
            }

            m.RequestSetActiveLayerRpc(firstId);
            if (m.IsPlaying) m.RequestPlaybackToggleRpc(false);
            m.RequestTimeScrubRpc(0f);
            return null;
        }

        /// <summary>
        /// PF-06 after the layers are configured (their filters cleared): the keys of the slowest
        /// rows from the rank layer, then on every layer a filter on KeyColumn with one range
        /// [key, key] per key (the first filter entry, through the range editor's request).
        /// </summary>
        static string ApplyHighlight(MultiplayerScatterplotManager m, PreparedHighlight h, IReadOnlyList<string> ids)
        {
            if (h.RankLayer < 0 || h.RankLayer >= ids.Count) return "(no rank layer)";
            PlotLayer rankLayer = null;
            foreach (PlotLayer l in m.Layers)
                if (l.Id == ids[h.RankLayer]) rankLayer = l;
            List<float> keys = TopKeys(rankLayer?.Dataset, h.RankColumn, h.KeyColumn, h.Count, out string missing);
            if (keys == null) return $"{rankLayer?.DatasetName ?? "(rank layer)"} has no column {missing}: re-run the converter";
            if (keys.Count == 0) return null; // nothing to rank: every row stays at full opacity

            var ranges = keys.ConvertAll(k => new MultiplayerScatterplotManager.FilterRange { min = k, max = k });
            foreach (string id in ids)
            {
                using (m.EditLayerScope(id))
                {
                    PlotLayer layer = m.EditLayer;
                    int column = layer.Dataset != null ? layer.Dataset.GetColumnIndex(h.KeyColumn) : -1;
                    if (column < 0) return $"{layer.DatasetName} has no column {h.KeyColumn}: re-run the converter";
                    m.RequestFilterIndexRpc(column);
                    if (!m.RequestSetFilterRangesRpc(ranges))
                        return $"{layer.DatasetName}: could not filter {h.KeyColumn} (needs a numeric column)";
                }
            }
            return null;
        }

        /// <summary>"12, 7, 30": the keys TopKeys picked, for the status line.</summary>
        public static string KeyText(IReadOnlyList<float> keys)
        {
            if (keys == null) return "";
            var parts = new List<string>();
            foreach (float k in keys) parts.Add(k.ToString("0.##", CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }

        static bool SameFile(string a, string b) =>
            string.Equals(DatasetRegistry.Normalise(a), DatasetRegistry.Normalise(b), StringComparison.OrdinalIgnoreCase);

        static string ConfigureLayer(MultiplayerScatterplotManager m, string id, PreparedLayer v)
        {
            ResolvedLayer c;
            using (m.EditLayerScope(id))
            {
                PlotLayer layer = m.EditLayer;
                if (!m.HasEditLayer) return "(layer " + id + " missing)";
                if (!TryResolve(v, layer.Dataset, out c, out string missing))
                    return $"{layer.DatasetName} has no column {missing}: re-run the converter";

                if (m.Filters.Count > 0) m.RequestClearFiltersRpc(); // a view shows every row
                if (c.X >= 0) m.RequestXColumnRpc(c.X);
                if (c.Y >= 0) m.RequestYColumnRpc(c.Y);
                if (c.Z >= 0) m.RequestZColumnRpc(c.Z);
                m.RequestColorColumnRpc(c.Color);
                if (c.Size >= 0)
                {
                    m.RequestSizeColumnRpc(c.Size);
                    m.RequestSizeMappedRpc(true);
                }
                else
                {
                    m.RequestSizeMappedRpc(false);
                }
                m.RequestTimeColumnRpc(c.Time);
                m.RequestGlyphColumnRpc(c.Glyph);
                if (c.Glyph >= 0 && v.GlyphShapes != null)
                    foreach ((string label, int shape) in v.GlyphShapes)
                        m.RequestGlyphAssignmentRpc(label, shape);
            }

            // Fixed colour after the colour column: RequestSetLayerColourRpc also sets the column to -1.
            if (c.Color < 0 && v.FixedColour.HasValue) m.RequestSetLayerColourRpc(id, v.FixedColour.Value);
            m.RequestSetLayerGlyphRpc(id, v.DefaultGlyph);
            m.RequestSetLayerTimeBehaviourRpc(id, v.TimeBehaviour);
            m.RequestSetLayerEndTimeColumnRpc(id, c.EndTime);
            m.RequestSetLayerLabelColumnRpc(id, c.Label);
            if (!string.IsNullOrEmpty(v.Name)) m.RequestRenameLayerRpc(id, v.Name);
            return null;
        }

        static int Resolve(DatasetColumnar ds, string column, List<string> absent)
        {
            if (string.IsNullOrEmpty(column)) return -1;
            int index = ds.ColumnMapping != null ? ds.GetColumnIndex(column) : -1;
            if (index < 0 && !absent.Contains(column)) absent.Add(column);
            return index;
        }
    }
}
