using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DataViz.Study
{
    /// <summary>
    /// The dashboard's Axes block (spec "axis scales and real proportions", ASR-09, 13, 14), built in
    /// code like DashboardLayersSection so FacilitatorDashboard.uxml stays unchanged.
    ///
    /// Top row: shape (Proportional / Fit each axis), padding, Reset axes. One row per axis: range
    /// mode (Auto, Include zero, Limits, Zoom), min and max (Limits and Zoom only), "Use data range",
    /// Reverse and "In group" (the proportional group). A warning line names axes Proportional
    /// flattens below 5 % of the edge, with a one-tap "Fit … on its own". Range, reverse and group are
    /// disabled on an axis whose shared scale is categorical or unmapped.
    ///
    /// Every change goes through the Change delegate (the controller's Facilitate): one undo step and
    /// one plot-level log row, whose old/new text is what PlotStateDiff.DescribeAxes writes for the
    /// same change, so facilitator and participant rows read the same. The Set… methods are what the
    /// controls call; self-tests call them directly (the controls need a panel to raise events).
    /// </summary>
    public sealed class DashboardAxesSection
    {
        /// <summary>action, old, new, apply (always an undo step; plot-level, so no layer).</summary>
        public delegate void ChangeHandler(string action, string oldValue, string newValue, Action apply);

        static readonly float[] k_Paddings = { 0f, 0.05f, 0.1f };
        static readonly AxisRangeMode[] k_Modes = { AxisRangeMode.Auto, AxisRangeMode.IncludeZero, AxisRangeMode.Limits, AxisRangeMode.Zoom };

        readonly MultiplayerScatterplotManager m_Manager;
        readonly ChangeHandler m_Change;

        readonly DropdownField m_Aspect, m_Padding;
        readonly DropdownField[] m_Modes = new DropdownField[3];
        readonly FloatField[] m_Mins = new FloatField[3], m_Maxs = new FloatField[3];
        readonly Button[] m_UseData = new Button[3];
        readonly Toggle[] m_Reverse = new Toggle[3], m_Group = new Toggle[3];
        readonly Label[] m_DataText = new Label[3];
        readonly VisualElement m_WarningRow;
        readonly Label m_Warning, m_Status;
        readonly Button[] m_FitAlone = new Button[3];

        int m_Signature;
        bool m_Updating;

        /// <summary>The last status text (a refused entry); for tests.</summary>
        public string StatusText => m_Status?.text ?? "";

        public DashboardAxesSection(VisualElement host, MultiplayerScatterplotManager manager, ChangeHandler change)
        {
            m_Manager = manager;
            m_Change = change;
            if (host == null)
                return; // headless (self-tests): only the Set… methods are used
            host.Clear();

            var header = new Label("Axes");
            header.AddToClassList("dash-section__header");
            host.Add(header);
            host.Add(Note("Proportional keeps real proportions between axes in the group (R coord_fixed); Fit each axis stretches every axis to the cube."));

            VisualElement top = Row();
            m_Aspect = Dropdown("Shape", new List<string> { AxisSettings.AspectText(AxisAspect.Proportional), AxisSettings.AspectText(AxisAspect.FitEach) },
                i => SetAspect(i == 1 ? AxisAspect.FitEach : AxisAspect.Proportional));
            m_Aspect.AddToClassList("dash-grow");
            top.Add(m_Aspect);
            var paddings = new List<string>();
            foreach (float p in k_Paddings) paddings.Add(AxisSettings.PaddingText(p));
            m_Padding = Dropdown("Padding", paddings, i => { if (i >= 0 && i < k_Paddings.Length) SetPadding(k_Paddings[i]); });
            m_Padding.tooltip = "Margin on each side of the data, as a share of its span (R's default is 5 %)";
            top.Add(m_Padding);
            top.Add(ButtonOf("Reset axes", ResetAxes, "Proportional, every axis in the group, Auto, no reverse, 5 % padding"));
            host.Add(top);

            var modeNames = new List<string>();
            foreach (AxisRangeMode mode in k_Modes) modeNames.Add(AxisSettings.ModeText(mode));
            for (int axis = 0; axis < 3; axis++)
            {
                int a = axis;
                VisualElement row = Row();
                var name = new Label(AxisSettings.AxisName(a));
                name.style.minWidth = 18;
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(name);

                m_Modes[a] = Dropdown("", modeNames, i => { if (i >= 0 && i < k_Modes.Length) SetMode(a, k_Modes[i]); });
                m_Modes[a].tooltip = "Auto: the data range. Include zero: widened to 0. Limits: rows outside are dropped (R scale limits). Zoom: rows outside hidden, trendlines use all rows (R coord_cartesian).";
                row.Add(m_Modes[a]);

                m_Mins[a] = new FloatField("Min") { isDelayed = true };
                m_Mins[a].RegisterValueChangedCallback(evt => { if (!m_Updating) SetLimits(a, evt.newValue, m_Maxs[a].value); });
                m_Mins[a].style.minWidth = 110;
                row.Add(m_Mins[a]);
                m_Maxs[a] = new FloatField("Max") { isDelayed = true };
                m_Maxs[a].RegisterValueChangedCallback(evt => { if (!m_Updating) SetLimits(a, m_Mins[a].value, evt.newValue); });
                m_Maxs[a].style.minWidth = 110;
                row.Add(m_Maxs[a]);

                m_UseData[a] = ButtonOf("Use data range", () => UseDataRange(a), "Sets the limits to the data's own range");
                row.Add(m_UseData[a]);

                m_Reverse[a] = ToggleOf("Reverse", v => SetReverse(a, v));
                row.Add(m_Reverse[a]);
                m_Group[a] = ToggleOf("In group", v => SetGroup(a, v));
                m_Group[a].tooltip = "Axes in the group share one scale when the shape is Proportional";
                row.Add(m_Group[a]);
                host.Add(row);

                m_DataText[a] = Note("");
                m_DataText[a].style.marginLeft = 22;
                host.Add(m_DataText[a]);
            }

            m_WarningRow = Row();
            m_Warning = new Label();
            m_Warning.AddToClassList("dash-grow");
            m_Warning.style.color = new Color(0.95f, 0.7f, 0.3f);
            m_Warning.style.whiteSpace = WhiteSpace.Normal;
            m_WarningRow.Add(m_Warning);
            for (int axis = 0; axis < 3; axis++)
            {
                int a = axis;
                m_FitAlone[a] = ButtonOf($"Fit {AxisSettings.AxisName(a)} on its own", () => SetGroup(a, false),
                    "Takes the axis out of the proportional group so it fills the cube again");
                m_WarningRow.Add(m_FitAlone[a]);
            }
            host.Add(m_WarningRow);

            m_Status = Note("");
            host.Add(m_Status);

            Refresh(true);
        }

        // ================================================================== refresh

        /// <summary>Re-reads the manager when its settings or the data ranges changed (or always with force). Cheap otherwise.</summary>
        public void Refresh(bool force)
        {
            if (m_Manager == null || m_Aspect == null)
                return;
            int signature = Signature();
            if (!force && signature == m_Signature)
                return;
            m_Signature = signature;

            AxisSettings s = m_Manager.Axes;
            AxisBounds data = m_Manager.DataBounds;
            m_Updating = true;
            try
            {
                m_Aspect.SetValueWithoutNotify(AxisSettings.AspectText(s.AspectMode));
                m_Padding.SetValueWithoutNotify(AxisSettings.PaddingText(s.Padding));
                if (!m_Padding.choices.Contains(m_Padding.value))
                {
                    var choices = new List<string>(m_Padding.choices) { m_Padding.value };
                    m_Padding.choices = choices; // a value set elsewhere (VR, a saved view) stays visible
                }

                for (int axis = 0; axis < 3; axis++)
                {
                    AxisScale scale = data.Get(axis);
                    bool numeric = scale.Mapped && !scale.Categorical;
                    AxisRangeMode mode = s.Mode(axis);
                    bool limits = mode == AxisRangeMode.Limits || mode == AxisRangeMode.Zoom;

                    m_Modes[axis].SetValueWithoutNotify(AxisSettings.ModeText(mode));
                    m_Modes[axis].SetEnabled(numeric);
                    m_Mins[axis].SetValueWithoutNotify(s.LimitMin(axis));
                    m_Maxs[axis].SetValueWithoutNotify(s.LimitMax(axis));
                    m_Mins[axis].SetEnabled(numeric && limits);
                    m_Maxs[axis].SetEnabled(numeric && limits);
                    m_UseData[axis].SetEnabled(numeric && limits);
                    m_Reverse[axis].SetValueWithoutNotify(s.Reversed(axis));
                    m_Reverse[axis].SetEnabled(numeric);
                    m_Group[axis].SetValueWithoutNotify(s.InGroup(axis));
                    m_Group[axis].SetEnabled(numeric && s.AspectMode == AxisAspect.Proportional);
                    m_DataText[axis].text = !scale.Mapped ? "not mapped"
                        : scale.Categorical ? "categorical: axis settings do not apply"
                        : "data " + AxisSettings.Number(scale.Min) + " to " + AxisSettings.Number(scale.Max);
                }

                string warning = WarningText(m_Manager.FlattenedAxes);
                m_Warning.text = warning;
                Show(m_WarningRow, warning.Length > 0);
                for (int axis = 0; axis < 3; axis++)
                    Show(m_FitAlone[axis], (m_Manager.FlattenedAxes & (1 << axis)) != 0);
            }
            finally
            {
                m_Updating = false;
            }
        }

        int Signature()
        {
            var h = new HashCode();
            h.Add(m_Manager.Axes.Hash());
            h.Add(m_Manager.FlattenedAxes);
            AxisBounds data = m_Manager.DataBounds;
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale scale = data.Get(axis);
                h.Add(scale.Mapped);
                h.Add(scale.Categorical);
                h.Add(scale.Min);
                h.Add(scale.Max);
            }
            return h.ToHashCode();
        }

        /// <summary>ASR-14: "Z is drawn at under 5 % of the cube in Proportional." ("" when nothing is flattened).</summary>
        public static string WarningText(byte flattenedMask)
        {
            string axes = AxisSettings.MaskText(flattenedMask);
            if (flattenedMask == 0)
                return "";
            bool one = (flattenedMask & (flattenedMask - 1)) == 0;
            return $"{axes} {(one ? "is" : "are")} drawn at under 5 % of the cube in Proportional: probably another unit. Fit it on its own?";
        }

        // ================================================================== changes (what the controls call)

        public void SetAspect(AxisAspect aspect)
        {
            AxisSettings before = m_Manager.Axes;
            if (before.AspectMode == aspect) return;
            Run(StudyActions.SetAxisAspect, AxisSettings.AspectText(before.AspectMode), AxisSettings.AspectText(aspect),
                () => m_Manager.RequestSetAxisAspectRpc(aspect));
        }

        public void SetPadding(float padding)
        {
            AxisSettings before = m_Manager.Axes;
            float clamped = Mathf.Clamp(padding, 0f, AxisSettings.MaxPadding);
            if (before.Padding.Equals(clamped)) return;
            Run(StudyActions.SetAxisPadding, AxisSettings.PaddingText(before.Padding), AxisSettings.PaddingText(clamped),
                () => m_Manager.RequestSetAxisPaddingRpc(clamped));
        }

        /// <summary>
        /// A new range mode. Limits or Zoom keep the stored limits when they are usable, else take the
        /// axis's data range (so picking Limits never starts on an empty window).
        /// </summary>
        public void SetMode(int axis, AxisRangeMode mode)
        {
            AxisSettings before = m_Manager.Axes;
            if (axis < 0 || axis > 2 || before.Mode(axis) == mode) return;
            float min = before.LimitMin(axis), max = before.LimitMax(axis);
            if ((mode == AxisRangeMode.Limits || mode == AxisRangeMode.Zoom) && !(min < max) && !DataRange(axis, out min, out max))
            {
                SetStatus($"{AxisSettings.AxisName(axis)} has no numeric data range to start the limits from");
                Refresh(true);
                return;
            }
            ApplyRange(axis, mode, min, max);
        }

        /// <summary>New limits for an axis in Limits or Zoom (swapped when given the wrong way round; equal or non-finite values are refused here, without a row).</summary>
        public void SetLimits(int axis, float min, float max)
        {
            AxisSettings before = m_Manager.Axes;
            if (axis < 0 || axis > 2) return;
            AxisRangeMode mode = before.Mode(axis);
            if (mode != AxisRangeMode.Limits && mode != AxisRangeMode.Zoom) return;
            if (!float.IsFinite(min) || !float.IsFinite(max) || min == max)
            {
                SetStatus($"{AxisSettings.AxisName(axis)} limits need two different numbers");
                Refresh(true);
                return;
            }
            if (min > max) (min, max) = (max, min);
            if (before.LimitMin(axis).Equals(min) && before.LimitMax(axis).Equals(max)) { Refresh(true); return; }
            ApplyRange(axis, mode, min, max);
        }

        /// <summary>Sets the limits to the axis's data range (Limits and Zoom only).</summary>
        public void UseDataRange(int axis)
        {
            if (!DataRange(axis, out float min, out float max) || !(min < max))
            {
                SetStatus($"{AxisSettings.AxisName(axis)} has no numeric data range");
                return;
            }
            SetLimits(axis, min, max);
        }

        public void SetReverse(int axis, bool reversed)
        {
            AxisSettings before = m_Manager.Axes;
            if (axis < 0 || axis > 2 || before.Reversed(axis) == reversed) return;
            string name = AxisSettings.AxisName(axis) + ": ";
            Run(StudyActions.SetAxisReverse, name + BoolText(before.Reversed(axis)), name + BoolText(reversed),
                () => m_Manager.RequestSetAxisReverseRpc(axis, reversed));
        }

        public void SetGroup(int axis, bool inGroup)
        {
            AxisSettings before = m_Manager.Axes;
            if (axis < 0 || axis > 2 || before.InGroup(axis) == inGroup) return;
            byte after = (byte)(inGroup ? before.GroupMask | (1 << axis) : before.GroupMask & ~(1 << axis));
            Run(StudyActions.SetAxisGroup, AxisSettings.MaskText(before.GroupMask), AxisSettings.MaskText(after),
                () => m_Manager.RequestSetAxisGroupRpc(axis, inGroup));
        }

        public void ResetAxes()
        {
            if (m_Manager.Axes.SameAs(AxisSettings.CreateDefault())) return;
            Run(StudyActions.ResetAxes, "", "", () => m_Manager.RequestResetAxesRpc());
        }

        void ApplyRange(int axis, AxisRangeMode mode, float min, float max)
        {
            AxisSettings before = m_Manager.Axes;
            AxisSettings after = before.Clone();
            after.Modes[axis] = (byte)mode;
            if (mode == AxisRangeMode.Limits || mode == AxisRangeMode.Zoom)
            {
                after.LimitMins[axis] = min;
                after.LimitMaxs[axis] = max;
            }
            Run(StudyActions.SetAxisRange, before.RangeText(axis), after.RangeText(axis),
                () => m_Manager.RequestSetAxisRangeRpc(axis, mode, min, max));
        }

        void Run(string action, string oldValue, string newValue, Action apply)
        {
            SetStatus("");
            m_Change?.Invoke(action, oldValue, newValue, apply);
            Refresh(true); // shows what the manager kept
        }

        bool DataRange(int axis, out float min, out float max)
        {
            AxisScale scale = m_Manager.DataBounds.Get(axis);
            min = Mathf.Min(scale.Min, scale.Max);
            max = Mathf.Max(scale.Min, scale.Max);
            return scale.Mapped && !scale.Categorical;
        }

        void SetStatus(string text)
        {
            if (m_Status != null) m_Status.text = text;
        }

        static string BoolText(bool value) => value ? "True" : "False";

        // ================================================================== helpers

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList("dash-row");
            return row;
        }

        static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("dash-note");
            return label;
        }

        static Button ButtonOf(string text, Action click, string tooltip)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            button.AddToClassList("dash-button");
            return button;
        }

        DropdownField Dropdown(string label, List<string> choices, Action<int> handler)
        {
            var field = new DropdownField(label) { choices = choices };
            field.RegisterValueChangedCallback(_ => { if (!m_Updating) handler(field.index); });
            return field;
        }

        Toggle ToggleOf(string label, Action<bool> handler)
        {
            var toggle = new Toggle(label);
            toggle.RegisterValueChangedCallback(evt => { if (!m_Updating) handler(evt.newValue); });
            return toggle;
        }

        static void Show(VisualElement e, bool visible) => e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
