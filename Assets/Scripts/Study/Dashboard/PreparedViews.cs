using System;
using System.Collections.Generic;
using System.Globalization;

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
    }

    /// <summary>
    /// The three one-click views for the current participant and part, built from the datasets the
    /// WAM converter writes as {participant}_part{part}_{kind}.cdataset. Plain C#: no Unity UI types,
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

        /// <summary>The view with this id, or null.</summary>
        public static PreparedView Find(string id)
        {
            foreach (PreparedView v in All)
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

        static int Resolve(DatasetColumnar ds, string column, List<string> absent)
        {
            if (string.IsNullOrEmpty(column)) return -1;
            int index = ds.ColumnMapping != null ? ds.GetColumnIndex(column) : -1;
            if (index < 0 && !absent.Contains(column)) absent.Add(column);
            return index;
        }
    }
}
