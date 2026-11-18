using System;
using DataViz.Networking;
using UnityEngine;

namespace DataViz
{
    // Axis settings (spec "axis scales and real proportions"): the plot-level shape of the shared
    // axes. Every request forwards to the host like the layer requests, changes Axes, and raises
    // OnPlotSettingsChanged; SharedBounds then recomputes (its signature includes Axes.Hash()).
    public partial class MultiplayerScatterplotManager
    {
        [NonSerialized] private AxisSettings m_Axes = AxisSettings.CreateDefault();
        private AxisBounds m_DataBounds;
        private byte m_FlattenedAxes;

        /// <summary>The plot's axis settings. Read-only for callers: change them through the requests below.</summary>
        public AxisSettings Axes => m_Axes ??= AxisSettings.CreateDefault();

        /// <summary>The shared bounds before the axis settings (the aligned layers' data range): what "Use data range" prefills.</summary>
        public AxisBounds DataBounds
        {
            get
            {
                _ = SharedBounds; // refreshes the cache
                return m_DataBounds;
            }
        }

        /// <summary>ASR-14: bit per axis that Proportional draws shorter than 5 % of the edge (from the last SharedBounds).</summary>
        public byte FlattenedAxes
        {
            get
            {
                _ = SharedBounds;
                return SharedBoundsOverride != null ? (byte)0 : m_FlattenedAxes;
            }
        }

        /// <summary>Replaces the settings without raising anything (snapshots, saved views, undo; the caller raises).</summary>
        public void RestoreAxes(AxisSettings settings)
        {
            m_Axes = (settings ?? AxisSettings.CreateDefault()).Clone().Sanitised();
        }

        private static bool ForwardAxisOp(PlotRequest request) => NetworkScatterplotState.TryForwardToHost(request);

        private void AxesChanged()
        {
            m_Axes.Sanitised();
            RaiseChanged(false);
        }

        /// <summary>Proportional or Fit each axis (ASR-01).</summary>
        public void RequestSetAxisAspectRpc(AxisAspect aspect)
        {
            if (ForwardAxisOp(new PlotRequest(PlotOp.SetAxisAspect, (int)aspect))) return;
            if (!Enum.IsDefined(typeof(AxisAspect), aspect) || Axes.AspectMode == aspect) return;
            Axes.Aspect = (byte)aspect;
            AxesChanged();
        }

        /// <summary>
        /// One axis's range mode. min and max are used by Limits and Zoom (swapped when reversed);
        /// Limits or Zoom with min == max, or a non-finite value, is refused. Other modes keep the
        /// stored limits, so switching back to Limits restores them.
        /// </summary>
        public void RequestSetAxisRangeRpc(int axis, AxisRangeMode mode, float min, float max)
        {
            var request = new PlotRequest(PlotOp.SetAxisRange, axis, (float)mode);
            request.RangeMins = new[] { min };
            request.RangeMaxs = new[] { max };
            if (ForwardAxisOp(request)) return;
            if (axis < 0 || axis > 2 || !Enum.IsDefined(typeof(AxisRangeMode), mode)) return;

            if (mode == AxisRangeMode.Limits || mode == AxisRangeMode.Zoom)
            {
                if (!float.IsFinite(min) || !float.IsFinite(max) || min == max)
                {
                    PostPlotMessage($"{AxisSettings.AxisName(axis)} limits need two different numbers", PlotMessageKind.Refused);
                    RaiseChanged(false);
                    return;
                }
                if (min > max)
                    (min, max) = (max, min);
                Axes.LimitMins[axis] = min;
                Axes.LimitMaxs[axis] = max;
            }
            Axes.Modes[axis] = (byte)mode;
            AxesChanged();
        }

        public void RequestSetAxisReverseRpc(int axis, bool reversed)
        {
            if (ForwardAxisOp(new PlotRequest(PlotOp.SetAxisReverse, axis, boolArg: reversed))) return;
            if (axis < 0 || axis > 2 || Axes.Reversed(axis) == reversed) return;
            Axes.ReverseMask = (byte)(reversed ? Axes.ReverseMask | (1 << axis) : Axes.ReverseMask & ~(1 << axis));
            AxesChanged();
        }

        /// <summary>Puts an axis in or out of the proportional group (ASR-01, and the ASR-14 fix).</summary>
        public void RequestSetAxisGroupRpc(int axis, bool inGroup)
        {
            if (ForwardAxisOp(new PlotRequest(PlotOp.SetAxisGroup, axis, boolArg: inGroup))) return;
            if (axis < 0 || axis > 2 || Axes.InGroup(axis) == inGroup) return;
            Axes.GroupMask = (byte)(inGroup ? Axes.GroupMask | (1 << axis) : Axes.GroupMask & ~(1 << axis));
            AxesChanged();
        }

        /// <summary>Fraction of the span added on each side, clamped to 0..0.5.</summary>
        public void RequestSetAxisPaddingRpc(float padding)
        {
            if (ForwardAxisOp(new PlotRequest(PlotOp.SetAxisPadding, floatArg: padding))) return;
            if (!float.IsFinite(padding)) return;
            float clamped = Mathf.Clamp(padding, 0f, AxisSettings.MaxPadding);
            if (Axes.Padding == clamped) return;
            Axes.Padding = clamped;
            AxesChanged();
        }

        /// <summary>Back to a new plot's settings (ASR-02).</summary>
        public void RequestResetAxesRpc()
        {
            if (ForwardAxisOp(new PlotRequest(PlotOp.ResetAxes))) return;
            AxisSettings fresh = AxisSettings.CreateDefault();
            if (Axes.SameAs(fresh)) return;
            m_Axes = fresh;
            AxesChanged();
        }
    }
}
