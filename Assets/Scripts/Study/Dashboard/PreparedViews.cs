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

        /// <summary>The view with this id (in All or Layered), or null.</summary>
        public static PreparedView Find(string id)
        {
            foreach (PreparedView v in All)
                if (v.Id == id) return v;
            foreach (PreparedView v in Layered)
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
            return true;
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
            string error = ConfigureLayer(m, firstId, view.Layers[0]);
            if (error != null) return error;

            for (int i = 1; i < view.Layers.Length; i++)
            {
                string before = m.ActiveLayerId;
                m.RequestAddDatasetLayerRpc(files[i]);
                PlotLayer added = m.ActiveLayer;
                if (added == null || added.Id == before || added.Dataset == null || !SameFile(added.DatasetName, files[i]))
                    return $"Could not add {files[i]} as a layer (see Console)";
                error = ConfigureLayer(m, added.Id, view.Layers[i]);
                if (error != null) return error;
            }

            m.RequestSetActiveLayerRpc(firstId);
            if (m.IsPlaying) m.RequestPlaybackToggleRpc(false);
            m.RequestTimeScrubRpc(0f);
            return null;
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
