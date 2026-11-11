using System;
using System.Collections.Generic;
using System.Globalization;

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

        /// <summary>
        /// LFR-32: the ids of the layers Shuffle changes, in stack order. The first layer always
        /// (Layers[0]), plus every other layer isUnlocked says yes to. Computed layers never: they
        /// take their mapping from their source. The first layer being computed is skipped, not
        /// replaced by the next one.
        /// </summary>
        public static List<string> LayersToShuffle(IReadOnlyList<PlotLayer> layers, Func<string, bool> isUnlocked)
        {
            List<string> ids = new List<string>();
            if (layers == null)
                return ids;

            for (int i = 0; i < layers.Count; i++)
            {
                PlotLayer layer = layers[i];
                if (layer == null || !layer.IsPointLayer || string.IsNullOrEmpty(layer.Id))
                    continue;
                if (i == 0 || (isUnlocked != null && isUnlocked(layer.Id)))
                    ids.Add(layer.Id);
            }

            return ids;
        }

        // A client's shuffle request lists its layers in PlotRequest.Layer, comma-joined. Field locks
        // are per dataset, so each entry carries that layer's lock mask: "L1:7,L3:5". An entry
        // without ":mask" uses the request's Int (one mask for every layer, as before layers).

        /// <summary>"id:mask" entries for PlotRequest.ForShuffle's layerIds, comma-joined.</summary>
        public static string FormatLayerEntries(IReadOnlyList<string> layerIds, IReadOnlyList<int> lockMasks)
        {
            if (layerIds == null || layerIds.Count == 0)
                return "";

            List<string> entries = new List<string>(layerIds.Count);
            for (int i = 0; i < layerIds.Count; i++)
            {
                string entry = layerIds[i];
                if (lockMasks != null && i < lockMasks.Count)
                    entry += ":" + lockMasks[i].ToString(CultureInfo.InvariantCulture);
                entries.Add(entry);
            }

            return string.Join(",", entries);
        }

        /// <summary>
        /// Splits one PlotRequest.LayerIds entry into the layer id and its lock mask (defaultMask
        /// when the entry has none or an unreadable one). False for an empty entry.
        /// </summary>
        public static bool ParseLayerEntry(string entry, int defaultMask, out string layerId, out int lockMask)
        {
            layerId = "";
            lockMask = defaultMask;
            if (string.IsNullOrWhiteSpace(entry))
                return false;

            int colon = entry.IndexOf(':');
            if (colon < 0)
            {
                layerId = entry.Trim();
                return layerId.Length > 0;
            }

            layerId = entry.Substring(0, colon).Trim();
            if (!int.TryParse(entry.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out lockMask))
                lockMask = defaultMask;
            return layerId.Length > 0;
        }
    }
}
