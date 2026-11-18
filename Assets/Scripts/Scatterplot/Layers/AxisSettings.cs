using System;
using System.Globalization;
using Unity.Netcode;
using UnityEngine;

namespace DataViz
{
    /// <summary>How one numeric axis picks its range (spec ASR-01). The byte values are stored in snapshots and saved views.</summary>
    public enum AxisRangeMode : byte
    {
        Auto = 0,          // the data range of the aligned layers
        IncludeZero = 1,   // R expand_limits(y = 0)
        Limits = 2,        // R scale_x_continuous(limits = ): rows outside are dropped, computed layers use kept rows
        Zoom = 3,          // R coord_cartesian(xlim = ): rows outside hidden, computed layers use all rows, lines clipped
    }

    /// <summary>Plot shape: Proportional (R coord_fixed / rgl aspect3d("iso")) or Fit each axis (today's stretched cube).</summary>
    public enum AxisAspect : byte
    {
        Proportional = 0,
        FitEach = 1,
    }

    /// <summary>What an axis scale does with values outside its clip range (AxisScale.Clip).</summary>
    public enum AxisClip : byte
    {
        None = 0,
        Drop = 1,   // Limits
        Zoom = 2,   // Zoom
    }

    /// <summary>
    /// The plot's axis settings (spec "axis scales and real proportions", ASR-01). Plot-level, not
    /// per layer: they shape the shared axes every aligned layer is drawn on. Own-scale layers take
    /// only Padding and Reverse (ASR-05). Categorical axes ignore everything here.
    /// Serialised with JsonUtility (saved views) and over the network (PlotSnapshot).
    /// </summary>
    [Serializable]
    public sealed class AxisSettings : INetworkSerializable
    {
        /// <summary>R's default expansion: 5 % of the span on each side.</summary>
        public const float DefaultPadding = 0.05f;
        public const float MaxPadding = 0.5f;

        /// <summary>ASR-14: a proportional axis drawn shorter than this fraction of the edge is "flattened".</summary>
        public const float FlattenedFraction = 0.05f;

        public byte Aspect;                    // AxisAspect
        public byte GroupMask = 7;             // bit per axis (0 X, 1 Y, 2 Z) in the proportional group
        public float Padding = DefaultPadding; // fraction of the span added on each side
        public byte[] Modes = new byte[3];     // AxisRangeMode per axis
        public float[] LimitMins = new float[3];
        public float[] LimitMaxs = new float[3];
        public byte ReverseMask;               // bit per axis

        /// <summary>A new plot (ASR-02): Proportional, all numeric axes in the group, Auto, no Reverse, 5 % padding.</summary>
        public static AxisSettings CreateDefault() => new AxisSettings();

        /// <summary>Today's look before this feature: Fit each axis, Auto, 0 % padding. Old saved views load with it (ASR-12).</summary>
        public static AxisSettings CreateLegacy() => new AxisSettings { Aspect = (byte)AxisAspect.FitEach, Padding = 0f };

        public AxisAspect AspectMode => (AxisAspect)Aspect;

        public AxisRangeMode Mode(int axis) =>
            axis >= 0 && axis < 3 && Modes != null && axis < Modes.Length ? (AxisRangeMode)Modes[axis] : AxisRangeMode.Auto;

        public float LimitMin(int axis) => axis >= 0 && LimitMins != null && axis < LimitMins.Length ? LimitMins[axis] : 0f;
        public float LimitMax(int axis) => axis >= 0 && LimitMaxs != null && axis < LimitMaxs.Length ? LimitMaxs[axis] : 0f;

        public bool Reversed(int axis) => axis >= 0 && axis < 3 && (ReverseMask & (1 << axis)) != 0;
        public bool InGroup(int axis) => axis >= 0 && axis < 3 && (GroupMask & (1 << axis)) != 0;

        /// <summary>Limits or Zoom with a usable range (min &lt; max).</summary>
        public bool HasLimits(int axis)
        {
            AxisRangeMode mode = Mode(axis);
            return (mode == AxisRangeMode.Limits || mode == AxisRangeMode.Zoom) && LimitMin(axis) < LimitMax(axis);
        }

        /// <summary>Fixes array lengths and out-of-range values (after JsonUtility or a hand-built instance).</summary>
        public AxisSettings Sanitised()
        {
            Modes = Resize(Modes);
            LimitMins = Resize(LimitMins);
            LimitMaxs = Resize(LimitMaxs);
            for (int i = 0; i < 3; i++)
                if (Modes[i] > (byte)AxisRangeMode.Zoom) Modes[i] = 0;
            if (Aspect > (byte)AxisAspect.FitEach) Aspect = 0;
            GroupMask &= 7;
            ReverseMask &= 7;
            Padding = float.IsFinite(Padding) ? Mathf.Clamp(Padding, 0f, MaxPadding) : DefaultPadding;
            return this;
        }

        public AxisSettings Clone()
        {
            var c = (AxisSettings)MemberwiseClone();
            c.Modes = (byte[])Resize(Modes).Clone();
            c.LimitMins = (float[])Resize(LimitMins).Clone();
            c.LimitMaxs = (float[])Resize(LimitMaxs).Clone();
            return c;
        }

        public bool SameAs(AxisSettings o)
        {
            if (o == null)
                return false;
            if (Aspect != o.Aspect || GroupMask != o.GroupMask || ReverseMask != o.ReverseMask || !Padding.Equals(o.Padding))
                return false;
            for (int i = 0; i < 3; i++)
            {
                if (Mode(i) != o.Mode(i) || !LimitMin(i).Equals(o.LimitMin(i)) || !LimitMax(i).Equals(o.LimitMax(i)))
                    return false;
            }
            return true;
        }

        public int Hash()
        {
            var h = new HashCode();
            h.Add(Aspect);
            h.Add(GroupMask);
            h.Add(ReverseMask);
            h.Add(Padding);
            for (int i = 0; i < 3; i++)
            {
                h.Add(Mode(i));
                h.Add(LimitMin(i));
                h.Add(LimitMax(i));
            }
            return h.ToHashCode();
        }

        // ---- text (log rows, buttons) ------------------------------------------------------

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        public static string AxisName(int axis) => axis == 0 ? "X" : axis == 1 ? "Y" : axis == 2 ? "Z" : "?";

        public static string AspectText(AxisAspect aspect) => aspect == AxisAspect.FitEach ? "Fit each axis" : "Proportional";

        public static string ModeText(AxisRangeMode mode)
        {
            switch (mode)
            {
                case AxisRangeMode.IncludeZero: return "Include zero";
                case AxisRangeMode.Limits: return "Limits";
                case AxisRangeMode.Zoom: return "Zoom";
                default: return "Auto";
            }
        }

        public static string Number(float v) => v.ToString("0.####", k_Inv);

        /// <summary>"X: Auto", "X: Limits 0..6", "Z: Zoom -1..1" (the range part of a SetAxisRange log row).</summary>
        public string RangeText(int axis)
        {
            AxisRangeMode mode = Mode(axis);
            string text = AxisName(axis) + ": " + ModeText(mode);
            if (mode == AxisRangeMode.Limits || mode == AxisRangeMode.Zoom)
                text += " " + Number(LimitMin(axis)) + ".." + Number(LimitMax(axis));
            return text;
        }

        public static string PaddingText(float padding) => Mathf.RoundToInt(padding * 100f).ToString(k_Inv) + " %";

        /// <summary>"X Y" (axes in the group), or "none".</summary>
        public static string MaskText(byte mask)
        {
            string text = "";
            for (int i = 0; i < 3; i++)
                if ((mask & (1 << i)) != 0)
                    text += (text.Length > 0 ? " " : "") + AxisName(i);
            return text.Length > 0 ? text : "none";
        }

        // ---- network --------------------------------------------------------------------------

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            Modes = Resize(Modes);
            LimitMins = Resize(LimitMins);
            LimitMaxs = Resize(LimitMaxs);
            s.SerializeValue(ref Aspect);
            s.SerializeValue(ref GroupMask);
            s.SerializeValue(ref Padding);
            s.SerializeValue(ref ReverseMask);
            for (int i = 0; i < 3; i++)
            {
                s.SerializeValue(ref Modes[i]);
                s.SerializeValue(ref LimitMins[i]);
                s.SerializeValue(ref LimitMaxs[i]);
            }
        }

        static T[] Resize<T>(T[] a)
        {
            if (a != null && a.Length == 3)
                return a;
            var r = new T[3];
            if (a != null)
                Array.Copy(a, r, Math.Min(3, a.Length));
            return r;
        }
    }

    /// <summary>
    /// Applies AxisSettings to the shared data bounds (spec ASR-03), per numeric axis in this order:
    /// data range, Include zero, Limits/Zoom, padding, Proportional (centred, the cube never changes
    /// shape), Reverse. Reverse is stored as Min &gt; Max, which AxisScale.Normalise and ValueAt already
    /// handle. Fit each axis + Auto + 0 % padding returns the data bounds unchanged (bit for bit).
    /// </summary>
    public static class AxisScaling
    {
        /// <summary>flattenedMask: bit per proportional axis drawn shorter than AxisSettings.FlattenedFraction of the edge (ASR-14).</summary>
        public static AxisBounds Apply(AxisBounds data, AxisSettings settings, out byte flattenedMask)
        {
            flattenedMask = 0;
            AxisBounds result = data;
            result.Settings = settings;
            if (settings == null)
                return result;

            var spans = new float[3];
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = data.Get(axis);
                if (!s.Mapped || s.Categorical)
                    continue;

                float min = Mathf.Min(s.Min, s.Max), max = Mathf.Max(s.Min, s.Max);
                switch (settings.Mode(axis))
                {
                    case AxisRangeMode.IncludeZero:
                        min = Mathf.Min(min, 0f);
                        max = Mathf.Max(max, 0f);
                        break;
                    case AxisRangeMode.Limits:
                    case AxisRangeMode.Zoom:
                        if (settings.HasLimits(axis))
                        {
                            min = settings.LimitMin(axis);
                            max = settings.LimitMax(axis);
                            s.Clip = settings.Mode(axis) == AxisRangeMode.Limits ? AxisClip.Drop : AxisClip.Zoom;
                            s.ClipMin = min;
                            s.ClipMax = max;
                        }
                        break;
                }

                float pad = (max - min) * settings.Padding;
                min -= pad;
                max += pad;
                s.Min = min;
                s.Max = max;
                spans[axis] = max - min;
                result.Set(axis, s);
            }

            if (settings.AspectMode == AxisAspect.Proportional)
            {
                float longest = 0f;
                for (int axis = 0; axis < 3; axis++)
                    if (InGroup(result, settings, axis))
                        longest = Mathf.Max(longest, spans[axis]);

                if (longest > 0f)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (!InGroup(result, settings, axis) || spans[axis] >= longest)
                            continue;
                        if (spans[axis] > 0f && spans[axis] / longest < AxisSettings.FlattenedFraction)
                            flattenedMask |= (byte)(1 << axis);
                        AxisScale s = result.Get(axis);
                        float centre = (s.Min + s.Max) * 0.5f;
                        s.Min = centre - longest * 0.5f;
                        s.Max = centre + longest * 0.5f;
                        result.Set(axis, s);
                    }
                }
            }

            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = result.Get(axis);
                if (!s.Mapped || s.Categorical || !settings.Reversed(axis))
                    continue;
                (s.Min, s.Max) = (s.Max, s.Min);
                result.Set(axis, s);
            }
            return result;
        }

        public static AxisBounds Apply(AxisBounds data, AxisSettings settings) => Apply(data, settings, out _);

        /// <summary>
        /// For bounds that are already final (a client showing the host's bounds): sets Settings and the
        /// Limits/Zoom clip ranges from settings, leaving Min/Max alone.
        /// </summary>
        public static AxisBounds MarkClips(AxisBounds bounds, AxisSettings settings)
        {
            bounds.Settings = settings;
            if (settings == null)
                return bounds;
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = bounds.Get(axis);
                if (!s.Mapped || s.Categorical || !settings.HasLimits(axis))
                    continue;
                s.Clip = settings.Mode(axis) == AxisRangeMode.Limits ? AxisClip.Drop : AxisClip.Zoom;
                s.ClipMin = settings.LimitMin(axis);
                s.ClipMax = settings.LimitMax(axis);
                bounds.Set(axis, s);
            }
            return bounds;
        }

        /// <summary>An own-scale (Normalised) layer's numeric range with the plot's padding and Reverse (ASR-05).</summary>
        public static AxisScale OwnScale(float lo, float hi, int axis, AxisSettings settings)
        {
            if (settings != null)
            {
                float pad = (hi - lo) * settings.Padding;
                lo -= pad;
                hi += pad;
                if (settings.Reversed(axis))
                    (lo, hi) = (hi, lo);
            }
            return AxisScale.Numeric(lo, hi);
        }

        static bool InGroup(AxisBounds bounds, AxisSettings settings, int axis)
        {
            AxisScale s = bounds.Get(axis);
            return settings.InGroup(axis) && s.Mapped && !s.Categorical;
        }
    }
}
