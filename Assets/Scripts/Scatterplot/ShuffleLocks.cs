using System.Collections.Generic;

namespace DataViz
{
    /// <summary>
    /// The UI islands the shuffle-lockable fields belong to. Used by the Preferences panel's
    /// Shuffle section to lock or unlock a whole island at once.
    /// </summary>
    public enum ShuffleIsland
    {
        Axis,   // Axis Controls: X, Y, Z
        Point,  // Point Controls: Colour, Size
        Glyph,  // Glyph Controls: Glyph
        Time    // Time Controls: Time
    }

    /// <summary>
    /// Which fields Shuffle can change, in which order it walks them, their default lock state
    /// and which island each belongs to. Kept free of scene objects so the rules can be read by
    /// both MultiplayerScatterplotManager (the shuffle itself) and ShuffleLockPanel (the UI).
    ///
    ///   Island  Field   Default    Shuffle picks from (ColumnMappingRules.Accepts)
    ///   Axis    X Y Z   Unlocked   numeric or DateTime
    ///   Point   Colour  Locked     any label
    ///   Point   Size    Locked     numeric or DateTime, only while Size is Mapped
    ///   Glyph   Glyph   Locked     categorical
    ///   Time    Time    Locked     numeric or DateTime
    /// Filter is never shuffled.
    /// </summary>
    public static class ShuffleLocks
    {
        /// <summary>Every shuffle-lockable field, in the order Shuffle picks them.</summary>
        public static readonly MappingTarget[] ShuffleOrder =
        {
            MappingTarget.X,
            MappingTarget.Y,
            MappingTarget.Z,
            MappingTarget.Size,
            MappingTarget.Colour,
            MappingTarget.Glyph,
            MappingTarget.Time
        };

        public static readonly ShuffleIsland[] Islands =
        {
            ShuffleIsland.Axis,
            ShuffleIsland.Point,
            ShuffleIsland.Glyph,
            ShuffleIsland.Time
        };

        private static readonly MappingTarget[] k_AxisFields = { MappingTarget.X, MappingTarget.Y, MappingTarget.Z };
        private static readonly MappingTarget[] k_PointFields = { MappingTarget.Colour, MappingTarget.Size };
        private static readonly MappingTarget[] k_GlyphFields = { MappingTarget.Glyph };
        private static readonly MappingTarget[] k_TimeFields = { MappingTarget.Time };

        /// <summary>True for every field Shuffle can change (all but Filter).</summary>
        public static bool IsShuffled(MappingTarget target) => target != MappingTarget.Filter;

        /// <summary>X, Y, Z and Size: these four never share a label after a shuffle.</summary>
        public static bool IsAxisLike(MappingTarget target) =>
            target == MappingTarget.X || target == MappingTarget.Y ||
            target == MappingTarget.Z || target == MappingTarget.Size;

        /// <summary>X, Y and Z start unlocked; every other field starts locked.</summary>
        public static bool DefaultLocked(MappingTarget target) =>
            !(target == MappingTarget.X || target == MappingTarget.Y || target == MappingTarget.Z);

        public static ShuffleIsland IslandOf(MappingTarget target)
        {
            switch (target)
            {
                case MappingTarget.Colour:
                case MappingTarget.Size:
                    return ShuffleIsland.Point;
                case MappingTarget.Glyph:
                    return ShuffleIsland.Glyph;
                case MappingTarget.Time:
                    return ShuffleIsland.Time;
                default:
                    return ShuffleIsland.Axis;
            }
        }

        public static IReadOnlyList<MappingTarget> FieldsIn(ShuffleIsland island)
        {
            switch (island)
            {
                case ShuffleIsland.Point: return k_PointFields;
                case ShuffleIsland.Glyph: return k_GlyphFields;
                case ShuffleIsland.Time: return k_TimeFields;
                default: return k_AxisFields;
            }
        }

        public static string DisplayName(MappingTarget target) => target.ToString();

        public static string DisplayName(ShuffleIsland island) => island + " Controls";

        /// <summary>A fresh lock map holding every field's default state.</summary>
        public static Dictionary<MappingTarget, bool> CreateDefaults()
        {
            Dictionary<MappingTarget, bool> locks = new Dictionary<MappingTarget, bool>();
            foreach (MappingTarget target in ShuffleOrder)
                locks[target] = DefaultLocked(target);
            return locks;
        }
    }
}
