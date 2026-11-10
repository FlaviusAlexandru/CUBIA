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
    /// One layer of a PlotSnapshot: everything a PlotLayer holds except the loaded dataset (which
    /// each peer resolves by DatasetName through DatasetRegistry) and LocalUnavailable (local).
    /// Treated as immutable once in a published snapshot.
    /// </summary>
    public sealed class LayerSnapshot : INetworkSerializable
    {
        public string Id = "", Name = "";
        public byte Kind;
        public bool Visible = true;
        public string DatasetName = "", DatasetHash = "", SourceLayerId = "";
        public int XColumn, YColumn, ZColumn;
        public bool Normalised;
        public int ColourColumn = -1;
        public uint FixedColour = PackColour(new Color32(0, 255, 255, 255));   // RGBA32, see PackColour
        public float PointSize;
        public bool SizeMapped;
        public int SizeColumn = -1;
        public float Opacity = 1f;
        public int DefaultGlyph, GlyphColumn = -1;
        public string[] GlyphLabels = Array.Empty<string>();
        public int[] GlyphIndices = Array.Empty<int>();
        public int[] RangeGlyphs = Array.Empty<int>();
        public float[] RangeMins = Array.Empty<float>();
        public float[] RangeMaxs = Array.Empty<float>();
        public int[] FilterColumns = Array.Empty<int>();
        public string[] FilterLabels = Array.Empty<string>();
        // Filter value ranges, flattened: entry i owns FilterRangeCounts[i] consecutive (min, max) pairs.
        public int[] FilterRangeCounts = Array.Empty<int>();
        public float[] FilterRangeMins = Array.Empty<float>();
        public float[] FilterRangeMaxs = Array.Empty<float>();
        public int TimeColumn = -1;
        public byte TimeBehaviour;
        public int EndTimeColumn = -1;
        public int LabelColumn = -1;
        public bool HiddenInSource;
        public byte ComputedKind;
        public int DriverAxis = -1, OrderColumn = -1;
        public bool Smoothed;
        public float RefValue, SmoothSpan = 0.1f, LineWidth = 0.006f;

        /// <summary>r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24.</summary>
        public static uint PackColour(Color32 c) => (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));

        public static Color32 UnpackColour(uint v) =>
            new Color32((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF));

        /// <summary>Copies a layer. DatasetHash is left "" for the publisher to fill.</summary>
        public static LayerSnapshot From(PlotLayer layer)
        {
            var s = new LayerSnapshot();
            if (layer == null)
                return s;

            s.Id = layer.Id ?? "";
            s.Name = layer.Name ?? "";
            s.Kind = (byte)layer.Kind;
            s.Visible = layer.Visible;
            s.DatasetName = layer.DatasetName ?? "";
            s.SourceLayerId = layer.SourceLayerId ?? "";
            s.XColumn = layer.XColumnIndex;
            s.YColumn = layer.YColumnIndex;
            s.ZColumn = layer.ZColumnIndex;
            s.Normalised = layer.Normalised;
            s.ColourColumn = layer.ColorColumnIndex;
            s.FixedColour = PackColour(layer.FixedColour);
            s.PointSize = layer.PointSize;
            s.SizeMapped = layer.SizeMapped;
            s.SizeColumn = layer.SizeColumnIndex;
            s.Opacity = layer.Opacity;
            s.DefaultGlyph = layer.DefaultGlyph;
            s.GlyphColumn = layer.GlyphColumnIndex;
            s.GlyphLabels = layer.GlyphAssignments.Select(a => a.Label ?? "").ToArray();
            s.GlyphIndices = layer.GlyphAssignments.Select(a => a.GlyphIndex).ToArray();
            s.RangeGlyphs = layer.GlyphRanges.Select(r => r.glyphIndex).ToArray();
            s.RangeMins = layer.GlyphRanges.Select(r => r.min).ToArray();
            s.RangeMaxs = layer.GlyphRanges.Select(r => r.max).ToArray();
            s.FilterColumns = layer.Filters.Select(f => f.ColumnIndex).ToArray();
            s.FilterLabels = layer.Filters.Select(f => f.Label ?? "").ToArray();
            s.FilterRangeCounts = layer.Filters.Select(f => f.Ranges?.Count ?? 0).ToArray();
            s.FilterRangeMins = layer.Filters.Where(f => f.Ranges != null).SelectMany(f => f.Ranges).Select(r => r.min).ToArray();
            s.FilterRangeMaxs = layer.Filters.Where(f => f.Ranges != null).SelectMany(f => f.Ranges).Select(r => r.max).ToArray();
            s.TimeColumn = layer.TimeColumnIndex;
            s.TimeBehaviour = (byte)layer.TimeBehaviour;
            s.EndTimeColumn = layer.EndTimeColumnIndex;
            s.LabelColumn = layer.LabelColumnIndex;
            s.HiddenInSource = layer.HiddenInSource;
            s.ComputedKind = (byte)layer.ComputedKind;
            s.DriverAxis = layer.DriverAxis;
            s.OrderColumn = layer.OrderColumnIndex;
            s.Smoothed = layer.Smoothed;
            s.RefValue = layer.RefValue;
            s.SmoothSpan = layer.SmoothSpan;
            s.LineWidth = layer.LineWidth;
            return s;
        }

        /// <summary>A new PlotLayer with these settings. Dataset is left null (the manager resolves it); LocalUnavailable false.</summary>
        public PlotLayer ToLayer()
        {
            var layer = new PlotLayer
            {
                Id = Id ?? "",
                Name = Name ?? "",
                Kind = (LayerKind)Kind,
                Visible = Visible,
                DatasetName = DatasetName ?? "",
                SourceLayerId = SourceLayerId ?? "",
                XColumnIndex = XColumn,
                YColumnIndex = YColumn,
                ZColumnIndex = ZColumn,
                Normalised = Normalised,
                ColorColumnIndex = ColourColumn,
                FixedColour = UnpackColour(FixedColour),
                PointSize = PointSize,
                SizeMapped = SizeMapped,
                SizeColumnIndex = SizeColumn,
                Opacity = Opacity,
                DefaultGlyph = DefaultGlyph,
                GlyphColumnIndex = GlyphColumn,
                TimeColumnIndex = TimeColumn,
                TimeBehaviour = (LayerTimeBehaviour)TimeBehaviour,
                EndTimeColumnIndex = EndTimeColumn,
                LabelColumnIndex = LabelColumn,
                HiddenInSource = HiddenInSource,
                ComputedKind = (ComputedLayerKind)ComputedKind,
                DriverAxis = DriverAxis,
                OrderColumnIndex = OrderColumn,
                Smoothed = Smoothed,
                RefValue = RefValue,
                SmoothSpan = SmoothSpan,
                LineWidth = LineWidth,
            };

            int rangeCursor = 0;
            for (int i = 0; i < Len(FilterColumns); i++)
            {
                var entry = new Manager.FilterEntry { ColumnIndex = FilterColumns[i], Label = i < Len(FilterLabels) ? FilterLabels[i] ?? "" : "" };
                int count = i < Len(FilterRangeCounts) ? FilterRangeCounts[i] : 0;
                for (int k = 0; k < count && rangeCursor < Len(FilterRangeMins) && rangeCursor < Len(FilterRangeMaxs); k++, rangeCursor++)
                    entry.Ranges.Add(new Manager.FilterRange { min = FilterRangeMins[rangeCursor], max = FilterRangeMaxs[rangeCursor] });
                layer.Filters.Add(entry);
            }

            int glyphs = Math.Min(Len(GlyphLabels), Len(GlyphIndices));
            for (int i = 0; i < glyphs; i++)
                layer.GlyphAssignments.Add(new Manager.GlyphAssignment { Label = GlyphLabels[i], GlyphIndex = GlyphIndices[i] });
            layer.GlyphRanges = PlotSnapshot.ToGlyphRanges(RangeGlyphs, RangeMins, RangeMaxs);
            return layer;
        }

        /// <summary>Equal in every field.</summary>
        public bool SameAs(LayerSnapshot o) =>
            o != null &&
            Id == o.Id && Name == o.Name && Kind == o.Kind && Visible == o.Visible &&
            DatasetName == o.DatasetName && DatasetHash == o.DatasetHash && SourceLayerId == o.SourceLayerId &&
            XColumn == o.XColumn && YColumn == o.YColumn && ZColumn == o.ZColumn && Normalised == o.Normalised &&
            ColourColumn == o.ColourColumn && FixedColour == o.FixedColour &&
            PointSize == o.PointSize && SizeMapped == o.SizeMapped && SizeColumn == o.SizeColumn && Opacity == o.Opacity &&
            DefaultGlyph == o.DefaultGlyph && GlyphColumn == o.GlyphColumn &&
            Seq(GlyphLabels, o.GlyphLabels) && Seq(GlyphIndices, o.GlyphIndices) &&
            Seq(RangeGlyphs, o.RangeGlyphs) && Seq(RangeMins, o.RangeMins) && Seq(RangeMaxs, o.RangeMaxs) &&
            Seq(FilterColumns, o.FilterColumns) && Seq(FilterLabels, o.FilterLabels) && Seq(FilterRangeCounts, o.FilterRangeCounts) &&
            Seq(FilterRangeMins, o.FilterRangeMins) && Seq(FilterRangeMaxs, o.FilterRangeMaxs) &&
            TimeColumn == o.TimeColumn && TimeBehaviour == o.TimeBehaviour && EndTimeColumn == o.EndTimeColumn &&
            LabelColumn == o.LabelColumn && HiddenInSource == o.HiddenInSource &&
            ComputedKind == o.ComputedKind && DriverAxis == o.DriverAxis && OrderColumn == o.OrderColumn &&
            Smoothed == o.Smoothed && RefValue == o.RefValue && SmoothSpan == o.SmoothSpan && LineWidth == o.LineWidth;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            Wire.Str(s, ref Id);
            Wire.Str(s, ref Name);
            s.SerializeValue(ref Kind);
            s.SerializeValue(ref Visible);
            Wire.Str(s, ref DatasetName);
            Wire.Str(s, ref DatasetHash);
            Wire.Str(s, ref SourceLayerId);
            s.SerializeValue(ref XColumn);
            s.SerializeValue(ref YColumn);
            s.SerializeValue(ref ZColumn);
            s.SerializeValue(ref Normalised);
            s.SerializeValue(ref ColourColumn);
            s.SerializeValue(ref FixedColour);
            s.SerializeValue(ref PointSize);
            s.SerializeValue(ref SizeMapped);
            s.SerializeValue(ref SizeColumn);
            s.SerializeValue(ref Opacity);
            s.SerializeValue(ref DefaultGlyph);
            s.SerializeValue(ref GlyphColumn);
            Wire.Strings(s, ref GlyphLabels);
            Wire.Array(s, ref GlyphIndices);
            Wire.Array(s, ref RangeGlyphs);
            Wire.Array(s, ref RangeMins);
            Wire.Array(s, ref RangeMaxs);
            Wire.Array(s, ref FilterColumns);
            Wire.Strings(s, ref FilterLabels);
            Wire.Array(s, ref FilterRangeCounts);
            Wire.Array(s, ref FilterRangeMins);
            Wire.Array(s, ref FilterRangeMaxs);
            s.SerializeValue(ref TimeColumn);
            s.SerializeValue(ref TimeBehaviour);
            s.SerializeValue(ref EndTimeColumn);
            s.SerializeValue(ref LabelColumn);
            s.SerializeValue(ref HiddenInSource);
            s.SerializeValue(ref ComputedKind);
            s.SerializeValue(ref DriverAxis);
            s.SerializeValue(ref OrderColumn);
            s.SerializeValue(ref Smoothed);
            s.SerializeValue(ref RefValue);
            s.SerializeValue(ref SmoothSpan);
            s.SerializeValue(ref LineWidth);
        }

        static int Len<T>(T[] a) => a?.Length ?? 0;

        static bool Seq<T>(T[] a, T[] b) => (a ?? Array.Empty<T>()).SequenceEqual(b ?? Array.Empty<T>());
    }

    /// <summary>
    /// Everything about the plot that all players share, as the host last published it
    /// (NetworkScatterplotState's NetworkVariable): the layer stack, the active layer, the shared
    /// bounds and playback. Menus, bookmarks, blacklist, shuffle locks and tooltips are deliberately
    /// not in here: they stay local.
    ///
    /// The flat members from before layers (DatasetName, XColumn, FilterColumns, …) are properties
    /// over ActiveLayer: the layer whose id is ActiveLayerId, else Layers[0]. On an empty snapshot
    /// they read as defaults, and setting one creates layer "L1" first, so old code and tests that
    /// build `new PlotSnapshot { XColumn = 0, … }` keep working.
    ///
    /// Time during playback is not streamed: TimeScrub is the scrub value at server time
    /// ScrubServerTime, and everyone advances it locally (ScrubAt) the way the manager's Update does.
    /// Instances are treated as immutable once published; build a new one for every change.
    /// </summary>
    public sealed class PlotSnapshot : INetworkSerializable, IEquatable<PlotSnapshot>
    {
        static readonly LayerSnapshot s_Empty = new LayerSnapshot();

        public LayerSnapshot[] Layers = Array.Empty<LayerSnapshot>();
        public string ActiveLayerId = "";
        public int NextLayerNumber = 1;

        /// <summary>Set by the host when publishing, so a client can tell "the host emptied the plot" from "nothing published yet".</summary>
        public bool Published;

        /// <summary>Shared numeric bounds per axis (bit 0 X, 1 Y, 2 Z set in BoundsValid when that axis has a numeric shared scale).</summary>
        public Vector3 BoundsMin, BoundsMax;
        public byte BoundsValid;

        /// <summary>True when some layer has a time column (filled by Capture; ScrubAt also checks the layers).</summary>
        public bool HasTimeColumn;

        public bool IsPlaying;
        public float PlaybackSpeed;
        public float PlaybackRate = 1f;
        // The host's effective scrub units per second (manager.EffectiveScrubPerSecond), used by ScrubAt;
        // 0 in a snapshot not taken by Capture, which then falls back to PlaybackSpeed x PlaybackRate.
        public float ScrubPerSecond;
        public float TimeScrub;
        public double ScrubServerTime;

        /// <summary>The layer with ActiveLayerId, else Layers[0], else null.</summary>
        public LayerSnapshot ActiveLayer
        {
            get
            {
                if (Layers == null || Layers.Length == 0)
                    return null;
                foreach (LayerSnapshot l in Layers)
                {
                    if (l != null && l.Id == ActiveLayerId)
                        return l;
                }
                return Layers[0];
            }
        }

        public LayerSnapshot FindLayer(string id) => Layers?.FirstOrDefault(l => l != null && l.Id == id);

        LayerSnapshot Read => ActiveLayer ?? s_Empty;

        // Setters on an empty snapshot create "L1" so the flat API keeps working.
        LayerSnapshot Write
        {
            get
            {
                LayerSnapshot active = ActiveLayer;
                if (active != null)
                    return active;
                active = new LayerSnapshot { Id = "L1" };
                Layers = new[] { active };
                ActiveLayerId = "L1";
                NextLayerNumber = Math.Max(NextLayerNumber, 2);
                return active;
            }
        }

        // ---- flat members (active layer) -------------------------------------------------
        public string DatasetName { get => Read.DatasetName; set => Write.DatasetName = value ?? ""; }
        public string DatasetHash { get => Read.DatasetHash; set => Write.DatasetHash = value ?? ""; }
        public int XColumn { get => Read.XColumn; set => Write.XColumn = value; }
        public int YColumn { get => Read.YColumn; set => Write.YColumn = value; }
        public int ZColumn { get => Read.ZColumn; set => Write.ZColumn = value; }
        public int ColourColumn { get => Read.ColourColumn; set => Write.ColourColumn = value; }
        public int TimeColumn { get => Read.TimeColumn; set => Write.TimeColumn = value; }
        public float PointSize { get => Read.PointSize; set => Write.PointSize = value; }
        public bool SizeMapped { get => Read.SizeMapped; set => Write.SizeMapped = value; }
        public int SizeColumn { get => Read.SizeColumn; set => Write.SizeColumn = value; }
        public int[] FilterColumns { get => Read.FilterColumns; set => Write.FilterColumns = value ?? Array.Empty<int>(); }
        public string[] FilterLabels { get => Read.FilterLabels; set => Write.FilterLabels = value ?? Array.Empty<string>(); }
        public int[] FilterRangeCounts { get => Read.FilterRangeCounts; set => Write.FilterRangeCounts = value ?? Array.Empty<int>(); }
        public float[] FilterRangeMins { get => Read.FilterRangeMins; set => Write.FilterRangeMins = value ?? Array.Empty<float>(); }
        public float[] FilterRangeMaxs { get => Read.FilterRangeMaxs; set => Write.FilterRangeMaxs = value ?? Array.Empty<float>(); }
        public int GlyphColumn { get => Read.GlyphColumn; set => Write.GlyphColumn = value; }
        public string[] GlyphLabels { get => Read.GlyphLabels; set => Write.GlyphLabels = value ?? Array.Empty<string>(); }
        public int[] GlyphIndices { get => Read.GlyphIndices; set => Write.GlyphIndices = value ?? Array.Empty<int>(); }
        public int[] RangeGlyphs { get => Read.RangeGlyphs; set => Write.RangeGlyphs = value ?? Array.Empty<int>(); }
        public float[] RangeMins { get => Read.RangeMins; set => Write.RangeMins = value ?? Array.Empty<float>(); }
        public float[] RangeMaxs { get => Read.RangeMaxs; set => Write.RangeMaxs = value ?? Array.Empty<float>(); }

        /// <summary>Copies the shared state out of the manager: every layer, the active id, the id counter, the shared bounds and playback. Layer hashes and ScrubServerTime are left for the caller.</summary>
        public static PlotSnapshot Capture(Manager m)
        {
            var s = new PlotSnapshot
            {
                Layers = m.Layers.Select(LayerSnapshot.From).ToArray(),
                ActiveLayerId = m.ActiveLayerId ?? "",
                NextLayerNumber = m.Stack.NextLayerNumber,
                IsPlaying = m.IsPlaying,
                PlaybackSpeed = m.PlaybackSpeed,
                PlaybackRate = m.PlaybackRate,
                ScrubPerSecond = m.EffectiveScrubPerSecond(),
                TimeScrub = m.TimeScrub,
            };
            s.HasTimeColumn = s.Layers.Any(l => l.TimeColumn >= 0);
            s.SetBounds(m.SharedBounds);
            return s;
        }

        /// <summary>
        /// Rebuilds the manager's stack from Layers and writes playback (no events; the manager
        /// raises them). Each layer's dataset is the manager's current instance when the layer
        /// keeps its id and dataset name, else DatasetRegistry.Get; a point layer whose dataset
        /// cannot be loaded, or whose published hash differs from the local file, gets LocalUnavailable
        /// (and no dataset). NextLayerNumber becomes max(current, snapshot).
        /// </summary>
        public void WriteTo(Manager m, float timeScrub)
        {
            var layers = new List<PlotLayer>(Layers?.Length ?? 0);
            if (Layers != null)
            {
                foreach (LayerSnapshot ls in Layers)
                {
                    if (ls == null)
                        continue;
                    PlotLayer layer = ls.ToLayer();
                    PlotLayer existing = m.Stack.Find(ls.Id);
                    if (!string.IsNullOrEmpty(layer.DatasetName))
                    {
                        // LNFR-04: a layer whose file here is not the host's version is never drawn
                        // from the local file. Snapshots without hashes (Capture: undo, offline plot) skip the check.
                        bool otherVersion = !global::DataViz.Networking.DatasetHash.MatchesLocal(layer.DatasetName, ls.DatasetHash);
                        layer.Dataset = otherVersion ? null
                            : existing != null && existing.DatasetName == layer.DatasetName && existing.Dataset != null
                                ? existing.Dataset
                                : DatasetRegistry.Get(layer.DatasetName);
                        layer.LocalUnavailable = otherVersion || (layer.IsPointLayer && layer.Dataset == null);
                    }
                    layers.Add(layer);
                }
            }

            m.Stack.Restore(layers, ActiveLayerId, NextLayerNumber);

            m.IsPlaying = IsPlaying;
            m.PlaybackSpeed = PlaybackSpeed;
            m.PlaybackRate = PlaybackRate;
            m.TimeScrub = Mathf.Clamp01(timeScrub);
        }

        /// <summary>Stores the numeric axes of bounds in BoundsMin/BoundsMax/BoundsValid (categorical axes are not carried).</summary>
        public void SetBounds(AxisBounds bounds)
        {
            BoundsValid = 0;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale scale = bounds.Get(axis);
                if (!scale.Mapped || scale.Categorical)
                    continue;
                min[axis] = scale.Min;
                max[axis] = scale.Max;
                BoundsValid |= (byte)(1 << axis);
            }
            BoundsMin = min;
            BoundsMax = max;
        }

        /// <summary>The carried bounds as an AxisBounds (numeric axes only; the others not Mapped). False when no axis is valid.</summary>
        public bool TryGetBounds(out AxisBounds bounds)
        {
            bounds = new AxisBounds();
            for (int axis = 0; axis < 3; axis++)
            {
                if ((BoundsValid & (1 << axis)) != 0)
                    bounds.Set(axis, AxisScale.Numeric(BoundsMin[axis], BoundsMax[axis]));
            }
            return BoundsValid != 0;
        }

        /// <summary>The scrub value at serverTime, advancing during playback exactly like the manager's Update.</summary>
        public float ScrubAt(double serverTime)
        {
            if (!IsPlaying || !(HasTimeColumn || (Layers != null && Layers.Any(l => l != null && l.TimeColumn >= 0))))
                return TimeScrub;
            return Mathf.Min(1f, TimeScrub + EffectiveScrubPerSecond * (float)(serverTime - ScrubServerTime));
        }

        /// <summary>Scrub units per second during playback (ScrubPerSecond, or PlaybackSpeed x PlaybackRate without it).</summary>
        public float EffectiveScrubPerSecond => ScrubPerSecond > 0f ? ScrubPerSecond : PlaybackSpeed * PlaybackRate;

        /// <summary>Equal in everything except the time scrub and when it was taken.</summary>
        public bool SamePlotAs(PlotSnapshot o)
        {
            if (o == null)
                return false;

            int n = Layers?.Length ?? 0;
            if (n != (o.Layers?.Length ?? 0))
                return false;
            for (int i = 0; i < n; i++)
            {
                if (Layers[i] == null ? o.Layers[i] != null : !Layers[i].SameAs(o.Layers[i]))
                    return false;
            }

            return ActiveLayerId == o.ActiveLayerId && NextLayerNumber == o.NextLayerNumber &&
                   Published == o.Published &&
                   BoundsValid == o.BoundsValid && BoundsMin == o.BoundsMin && BoundsMax == o.BoundsMax &&
                   HasTimeColumn == o.HasTimeColumn &&
                   IsPlaying == o.IsPlaying && PlaybackSpeed == o.PlaybackSpeed &&
                   PlaybackRate == o.PlaybackRate && ScrubPerSecond == o.ScrubPerSecond;
        }

        public bool Equals(PlotSnapshot o) =>
            SamePlotAs(o) && TimeScrub == o.TimeScrub && ScrubServerTime == o.ScrubServerTime;

        public override bool Equals(object obj) => Equals(obj as PlotSnapshot);

        public override int GetHashCode() => HashCode.Combine(DatasetName, XColumn, YColumn, ZColumn, Layers?.Length ?? 0, TimeScrub, ScrubServerTime);

        /// <summary>Plot-level fields, then the layer count, then each layer. Both peers run the same build, so no version byte.</summary>
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            Wire.Str(s, ref ActiveLayerId);
            s.SerializeValue(ref NextLayerNumber);
            s.SerializeValue(ref Published);
            s.SerializeValue(ref BoundsMin);
            s.SerializeValue(ref BoundsMax);
            s.SerializeValue(ref BoundsValid);
            s.SerializeValue(ref HasTimeColumn);
            s.SerializeValue(ref IsPlaying);
            s.SerializeValue(ref PlaybackSpeed);
            s.SerializeValue(ref PlaybackRate);
            s.SerializeValue(ref ScrubPerSecond);
            s.SerializeValue(ref TimeScrub);
            s.SerializeValue(ref ScrubServerTime);

            int count = Layers?.Length ?? 0;
            s.SerializeValue(ref count);
            if (s.IsReader)
                Layers = new LayerSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                Layers[i] ??= new LayerSnapshot();
                Layers[i].NetworkSerialize(s);
            }
        }

        internal static List<Manager.GlyphRange> ToGlyphRanges(int[] glyphs, float[] mins, float[] maxs)
        {
            int n = Math.Min(glyphs?.Length ?? 0, Math.Min(mins?.Length ?? 0, maxs?.Length ?? 0));
            var ranges = new List<Manager.GlyphRange>(n);
            for (int i = 0; i < n; i++)
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
        PlaybackRate, // appended: existing op values stay unchanged
        // Layer ops (appended). Arguments: PlotRequest.Layer is the layer id unless noted.
        AddDatasetLayer,        // Text = file
        AddSubsetLayer,         // Layer = source, Int = filter column, Text = label
        AddComputedLayer,       // Layer = source, Int = (int)ComputedLayerKind
        RemoveLayer,
        DuplicateLayer,
        SetActiveLayer,
        RenameLayer,            // Text = name
        ReorderLayer,           // Int = new index
        SetLayerVisible,        // Bool
        SetLayerNormalised,     // Bool
        SetSubsetHiddenInSource,// Bool
        SetComputedFlag,        // Bool
        SetLayerOpacity,        // Float
        SetComputedValue,       // Float
        SetComputedSpan,        // Float
        SetLayerLineWidth,      // Float
        SetLayerColour,         // Int = packed RGBA (LayerSnapshot.PackColour)
        SetLayerGlyph,          // Int
        SetLayerTimeBehaviour,  // Int = (int)LayerTimeBehaviour
        SetLayerEndTimeColumn,  // Int
        SetLayerLabelColumn,    // Int
        SetComputedAxis,        // Int
        SetComputedColumn,      // Int
    }

    /// <summary>
    /// One forwarded manager call: the op plus its arguments. Layer names the layer it applies to
    /// ("" = the host's active layer; for Shuffle the comma-joined ids of the layers to shuffle).
    /// Shuffle carries the requesting client's own locks (bit per MappingTarget) and blacklist, so
    /// the host shuffles the way that client would have.
    /// </summary>
    public struct PlotRequest : INetworkSerializable
    {
        public PlotOp Op;
        public int Int;
        public float Float;
        public bool Bool;
        public string Text;
        public string Layer;
        public int[] RangeGlyphs;
        public float[] RangeMins;
        public float[] RangeMaxs;

        public PlotRequest(PlotOp op, int intArg = 0, float floatArg = 0f, bool boolArg = false, string text = null, string layer = null)
        {
            Op = op;
            Int = intArg;
            Float = floatArg;
            Bool = boolArg;
            Text = text ?? "";
            Layer = layer ?? "";
            RangeGlyphs = Array.Empty<int>();
            RangeMins = Array.Empty<float>();
            RangeMaxs = Array.Empty<float>();
        }

        public static PlotRequest ForGlyphRanges(List<Manager.GlyphRange> ranges, string layer = null)
        {
            var request = new PlotRequest(PlotOp.GlyphRanges, layer: layer);
            if (ranges != null)
            {
                request.RangeGlyphs = ranges.Select(r => r.glyphIndex).ToArray();
                request.RangeMins = ranges.Select(r => r.min).ToArray();
                request.RangeMaxs = ranges.Select(r => r.max).ToArray();
            }
            return request;
        }

        /// <summary>Filter ranges travel in RangeMins/RangeMaxs (RangeGlyphs stays empty).</summary>
        public static PlotRequest ForFilterRanges(List<Manager.FilterRange> ranges, string layer = null)
        {
            var request = new PlotRequest(PlotOp.FilterRanges, layer: layer);
            if (ranges != null)
            {
                request.RangeMins = ranges.Select(r => r.min).ToArray();
                request.RangeMaxs = ranges.Select(r => r.max).ToArray();
            }
            return request;
        }

        /// <summary>layerIds: comma-joined ids of the layers to shuffle ("" = whatever the host decides).</summary>
        public static PlotRequest ForShuffle(int lockedFieldMask, IEnumerable<string> blacklist, string layerIds = null) =>
            new PlotRequest(PlotOp.Shuffle, lockedFieldMask, text: string.Join("\n", blacklist ?? Array.Empty<string>()), layer: layerIds);

        public List<Manager.GlyphRange> GlyphRanges => PlotSnapshot.ToGlyphRanges(RangeGlyphs, RangeMins, RangeMaxs);

        public List<Manager.FilterRange> FilterRanges =>
            (RangeMins ?? Array.Empty<float>()).Zip(RangeMaxs ?? Array.Empty<float>(), (min, max) => new Manager.FilterRange { min = min, max = max }).ToList();

        public string[] Blacklist => string.IsNullOrEmpty(Text) ? Array.Empty<string>() : Text.Split('\n');

        /// <summary>Layer split on ',' (Shuffle); empty when Layer is "".</summary>
        public string[] LayerIds => string.IsNullOrEmpty(Layer) ? Array.Empty<string>() : Layer.Split(',');

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Op);
            s.SerializeValue(ref Int);
            s.SerializeValue(ref Float);
            s.SerializeValue(ref Bool);
            Wire.Str(s, ref Text);
            Wire.Str(s, ref Layer);
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
