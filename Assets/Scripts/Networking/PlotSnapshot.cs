using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Manager = DataViz.MultiplayerScatterplotManager;

namespace DataViz.Networking
{
    /// <summary>
    /// Everything about the plot that all players share, as the host last published it
    /// (NetworkScatterplotState's NetworkVariable). Menus, bookmarks, blacklist, shuffle locks and
    /// tooltips are deliberately not in here: they stay local.
    ///
    /// Time during playback is not streamed: TimeScrub is the scrub value at server time
    /// ScrubServerTime, and everyone advances it locally (ScrubAt) the way the manager's Update does.
    /// Instances are treated as immutable once published; build a new one for every change.
    /// </summary>
    public sealed class PlotSnapshot : INetworkSerializable, IEquatable<PlotSnapshot>
    {
        public string DatasetName = "";
        public string DatasetHash = "";

        public int XColumn, YColumn, ZColumn;
        public int ColourColumn = -1;
        public int TimeColumn = -1;
        public float PointSize;
        public bool SizeMapped;
        public int SizeColumn = -1;

        public int[] FilterColumns = Array.Empty<int>();
        public string[] FilterLabels = Array.Empty<string>();
        // Filter value ranges, flattened: entry i owns FilterRangeCounts[i] consecutive (min, max) pairs.
        public int[] FilterRangeCounts = Array.Empty<int>();
        public float[] FilterRangeMins = Array.Empty<float>();
        public float[] FilterRangeMaxs = Array.Empty<float>();

        public int GlyphColumn = -1;
        public string[] GlyphLabels = Array.Empty<string>();
        public int[] GlyphIndices = Array.Empty<int>();
        public int[] RangeGlyphs = Array.Empty<int>();
        public float[] RangeMins = Array.Empty<float>();
        public float[] RangeMaxs = Array.Empty<float>();

        public bool IsPlaying;
        public float PlaybackSpeed;
        public float TimeScrub;
        public double ScrubServerTime;

        /// <summary>Copies the shared settings out of the manager. DatasetHash and ScrubServerTime are left for the caller.</summary>
        public static PlotSnapshot Capture(Manager m) => new PlotSnapshot
        {
            DatasetName = m.CurrentDatasetName ?? "",
            XColumn = m.XColumnIndex,
            YColumn = m.YColumnIndex,
            ZColumn = m.ZColumnIndex,
            ColourColumn = m.ColorColumnIndex,
            TimeColumn = m.TimeColumnIndex,
            PointSize = m.PointSize,
            SizeMapped = m.SizeMapped,
            SizeColumn = m.SizeColumnIndex,
            FilterColumns = m.Filters.Select(f => f.ColumnIndex).ToArray(),
            FilterLabels = m.Filters.Select(f => f.Label ?? "").ToArray(),
            FilterRangeCounts = m.Filters.Select(f => f.Ranges.Count).ToArray(),
            FilterRangeMins = m.Filters.SelectMany(f => f.Ranges).Select(r => r.min).ToArray(),
            FilterRangeMaxs = m.Filters.SelectMany(f => f.Ranges).Select(r => r.max).ToArray(),
            GlyphColumn = m.GlyphColumnIndex,
            GlyphLabels = m.GlyphAssignments.Select(a => a.Label ?? "").ToArray(),
            GlyphIndices = m.GlyphAssignments.Select(a => a.GlyphIndex).ToArray(),
            RangeGlyphs = m.GlyphRanges.Select(r => r.glyphIndex).ToArray(),
            RangeMins = m.GlyphRanges.Select(r => r.min).ToArray(),
            RangeMaxs = m.GlyphRanges.Select(r => r.max).ToArray(),
            IsPlaying = m.IsPlaying,
            PlaybackSpeed = m.PlaybackSpeed,
            TimeScrub = m.TimeScrub,
        };

        /// <summary>Writes the shared settings into the manager's fields (no events; the manager raises them).</summary>
        public void WriteTo(Manager m, float timeScrub)
        {
            m.XColumnIndex = XColumn;
            m.YColumnIndex = YColumn;
            m.ZColumnIndex = ZColumn;
            m.ColorColumnIndex = ColourColumn;
            m.TimeColumnIndex = TimeColumn;
            m.PointSize = PointSize;
            m.SizeMapped = SizeMapped;
            m.SizeColumnIndex = SizeColumn;

            m.Filters.Clear();
            int rangeCursor = 0;
            for (int i = 0; i < FilterColumns.Length; i++)
            {
                var entry = new Manager.FilterEntry { ColumnIndex = FilterColumns[i], Label = FilterLabels[i] };
                int count = i < FilterRangeCounts.Length ? FilterRangeCounts[i] : 0;
                for (int k = 0; k < count && rangeCursor < FilterRangeMins.Length; k++, rangeCursor++)
                    entry.Ranges.Add(new Manager.FilterRange { min = FilterRangeMins[rangeCursor], max = FilterRangeMaxs[rangeCursor] });
                m.Filters.Add(entry);
            }

            m.GlyphColumnIndex = GlyphColumn;
            m.GlyphAssignments.Clear();
            for (int i = 0; i < GlyphLabels.Length; i++)
                m.GlyphAssignments.Add(new Manager.GlyphAssignment { Label = GlyphLabels[i], GlyphIndex = GlyphIndices[i] });
            m.GlyphRanges = ToGlyphRanges(RangeGlyphs, RangeMins, RangeMaxs);

            m.IsPlaying = IsPlaying;
            m.PlaybackSpeed = PlaybackSpeed;
            m.TimeScrub = Mathf.Clamp01(timeScrub);
        }

        /// <summary>The scrub value at serverTime, advancing during playback exactly like the manager's Update.</summary>
        public float ScrubAt(double serverTime)
        {
            if (!IsPlaying || TimeColumn < 0)
                return TimeScrub;
            return Mathf.Min(1f, TimeScrub + PlaybackSpeed * (float)(serverTime - ScrubServerTime));
        }

        /// <summary>Equal in everything except the time scrub and when it was taken.</summary>
        public bool SamePlotAs(PlotSnapshot o) =>
            o != null &&
            DatasetName == o.DatasetName && DatasetHash == o.DatasetHash &&
            XColumn == o.XColumn && YColumn == o.YColumn && ZColumn == o.ZColumn &&
            ColourColumn == o.ColourColumn && TimeColumn == o.TimeColumn &&
            PointSize == o.PointSize && SizeMapped == o.SizeMapped && SizeColumn == o.SizeColumn &&
            FilterColumns.SequenceEqual(o.FilterColumns) && FilterLabels.SequenceEqual(o.FilterLabels) &&
            FilterRangeCounts.SequenceEqual(o.FilterRangeCounts) &&
            FilterRangeMins.SequenceEqual(o.FilterRangeMins) && FilterRangeMaxs.SequenceEqual(o.FilterRangeMaxs) &&
            GlyphColumn == o.GlyphColumn &&
            GlyphLabels.SequenceEqual(o.GlyphLabels) && GlyphIndices.SequenceEqual(o.GlyphIndices) &&
            RangeGlyphs.SequenceEqual(o.RangeGlyphs) && RangeMins.SequenceEqual(o.RangeMins) && RangeMaxs.SequenceEqual(o.RangeMaxs) &&
            IsPlaying == o.IsPlaying && PlaybackSpeed == o.PlaybackSpeed;

        public bool Equals(PlotSnapshot o) =>
            SamePlotAs(o) && TimeScrub == o.TimeScrub && ScrubServerTime == o.ScrubServerTime;

        public override bool Equals(object obj) => Equals(obj as PlotSnapshot);

        public override int GetHashCode() => HashCode.Combine(DatasetName, XColumn, YColumn, ZColumn, TimeScrub, ScrubServerTime);

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            Wire.Str(s, ref DatasetName);
            Wire.Str(s, ref DatasetHash);
            s.SerializeValue(ref XColumn);
            s.SerializeValue(ref YColumn);
            s.SerializeValue(ref ZColumn);
            s.SerializeValue(ref ColourColumn);
            s.SerializeValue(ref TimeColumn);
            s.SerializeValue(ref PointSize);
            s.SerializeValue(ref SizeMapped);
            s.SerializeValue(ref SizeColumn);
            Wire.Array(s, ref FilterColumns);
            Wire.Strings(s, ref FilterLabels);
            Wire.Array(s, ref FilterRangeCounts);
            Wire.Array(s, ref FilterRangeMins);
            Wire.Array(s, ref FilterRangeMaxs);
            s.SerializeValue(ref GlyphColumn);
            Wire.Strings(s, ref GlyphLabels);
            Wire.Array(s, ref GlyphIndices);
            Wire.Array(s, ref RangeGlyphs);
            Wire.Array(s, ref RangeMins);
            Wire.Array(s, ref RangeMaxs);
            s.SerializeValue(ref IsPlaying);
            s.SerializeValue(ref PlaybackSpeed);
            s.SerializeValue(ref TimeScrub);
            s.SerializeValue(ref ScrubServerTime);
        }

        internal static List<Manager.GlyphRange> ToGlyphRanges(int[] glyphs, float[] mins, float[] maxs)
        {
            var ranges = new List<Manager.GlyphRange>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
                ranges.Add(new Manager.GlyphRange { glyphIndex = glyphs[i], min = mins[i], max = maxs[i] });
            return ranges;
        }
    }

    /// <summary>The shared-plot changes a client can ask the host to make (one per forwarded manager Request…Rpc).</summary>
    public enum PlotOp : byte
    {
        LoadDataset, XColumn, YColumn, ZColumn, ColourColumn, PointSize,
        TimeColumn, TimeScrub, Playback,
        FilterColumn, FilterLabel, AddFilter, RemoveFilter, ClearFilters,
        SizeColumn, SizeMapped,
        GlyphColumn, GlyphAssignment, GlyphRanges,
        Shuffle, ShuffleBackward, ShuffleForward,
        FilterRanges,
    }

    /// <summary>
    /// One forwarded manager call: the op plus its arguments. Shuffle carries the requesting
    /// client's own locks (bit per MappingTarget) and blacklist, so the host shuffles the way
    /// that client would have.
    /// </summary>
    public struct PlotRequest : INetworkSerializable
    {
        public PlotOp Op;
        public int Int;
        public float Float;
        public bool Bool;
        public string Text;
        public int[] RangeGlyphs;
        public float[] RangeMins;
        public float[] RangeMaxs;

        public PlotRequest(PlotOp op, int intArg = 0, float floatArg = 0f, bool boolArg = false, string text = null)
        {
            Op = op;
            Int = intArg;
            Float = floatArg;
            Bool = boolArg;
            Text = text ?? "";
            RangeGlyphs = Array.Empty<int>();
            RangeMins = Array.Empty<float>();
            RangeMaxs = Array.Empty<float>();
        }

        public static PlotRequest ForGlyphRanges(List<Manager.GlyphRange> ranges)
        {
            var request = new PlotRequest(PlotOp.GlyphRanges);
            if (ranges != null)
            {
                request.RangeGlyphs = ranges.Select(r => r.glyphIndex).ToArray();
                request.RangeMins = ranges.Select(r => r.min).ToArray();
                request.RangeMaxs = ranges.Select(r => r.max).ToArray();
            }
            return request;
        }

        /// <summary>Filter ranges travel in RangeMins/RangeMaxs (RangeGlyphs stays empty).</summary>
        public static PlotRequest ForFilterRanges(List<Manager.FilterRange> ranges)
        {
            var request = new PlotRequest(PlotOp.FilterRanges);
            if (ranges != null)
            {
                request.RangeMins = ranges.Select(r => r.min).ToArray();
                request.RangeMaxs = ranges.Select(r => r.max).ToArray();
            }
            return request;
        }

        public static PlotRequest ForShuffle(int lockedFieldMask, IEnumerable<string> blacklist) =>
            new PlotRequest(PlotOp.Shuffle, lockedFieldMask, text: string.Join("\n", blacklist));

        public List<Manager.GlyphRange> GlyphRanges => PlotSnapshot.ToGlyphRanges(RangeGlyphs, RangeMins, RangeMaxs);

        public List<Manager.FilterRange> FilterRanges =>
            RangeMins.Zip(RangeMaxs, (min, max) => new Manager.FilterRange { min = min, max = max }).ToList();

        public string[] Blacklist => string.IsNullOrEmpty(Text) ? Array.Empty<string>() : Text.Split('\n');

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Op);
            s.SerializeValue(ref Int);
            s.SerializeValue(ref Float);
            s.SerializeValue(ref Bool);
            Wire.Str(s, ref Text);
            Wire.Array(s, ref RangeGlyphs);
            Wire.Array(s, ref RangeMins);
            Wire.Array(s, ref RangeMaxs);
        }
    }

    /// <summary>One entry of the networked player list (host first).</summary>
    public struct PlayerEntry : INetworkSerializable, IEquatable<PlayerEntry>
    {
        public ulong ClientId;
        public FixedString128Bytes Name;
        public Color Colour;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ClientId);
            s.SerializeValue(ref Name);
            s.SerializeValue(ref Colour);
        }

        public bool Equals(PlayerEntry o) => ClientId == o.ClientId && Name.Equals(o.Name) && Colour == o.Colour;
        public override bool Equals(object obj) => obj is PlayerEntry o && Equals(o);
        public override int GetHashCode() => ClientId.GetHashCode();
    }

    /// <summary>Null-safe serialisation helpers (NGO's own string/array writers reject null).</summary>
    static class Wire
    {
        public static void Str<T>(BufferSerializer<T> s, ref string value) where T : IReaderWriter
        {
            value ??= "";
            s.SerializeValue(ref value);
        }

        public static void Array<T, TItem>(BufferSerializer<T> s, ref TItem[] values) where T : IReaderWriter where TItem : unmanaged, IComparable, IConvertible, IComparable<TItem>, IEquatable<TItem>
        {
            values ??= System.Array.Empty<TItem>();
            s.SerializeValue(ref values);
        }

        public static void Strings<T>(BufferSerializer<T> s, ref string[] values) where T : IReaderWriter
        {
            int count = values?.Length ?? 0;
            s.SerializeValue(ref count);
            if (s.IsReader)
                values = new string[count];
            for (int i = 0; i < count; i++)
            {
                string item = values[i] ?? "";
                s.SerializeValue(ref item);
                values[i] = item;
            }
        }
    }
}
