using System;
using System.Globalization;

namespace DataViz.MenuUI
{
    /// <summary>
    /// What the Map tab's axis controls show (spec "axis scales and real proportions", ASR-08 and
    /// ASR-14): the aspect button, and the Axes overlay's rows. Pure, so it is self-tested; AxesPanel
    /// only copies a View onto its widgets and turns clicks into manager requests.
    /// </summary>
    public static class AxesPanelModel
    {
        public static readonly string[] ModeNames = { "Auto", "Include 0", "Limits", "Zoom" };
        public const string ReverseText = "Reverse";
        public const string GroupText = "In group";
        public const string NotNumericText = "not a numeric axis";

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        public sealed class AxisRow
        {
            public string Name = "";
            public bool Numeric;          // a numeric axis the aligned layers map: range, reverse and group apply
            public int Mode;              // (int)AxisRangeMode, highlighted
            public bool Reversed, InGroup;
            public string LimitsText = "";
        }

        public sealed class View
        {
            public AxisAspect Aspect;
            public string AspectText = "";
            public bool PaddingZero, PaddingDefault;   // which padding button is highlighted (neither for another value)
            public readonly AxisRow[] Rows = { new AxisRow(), new AxisRow(), new AxisRow() };
            public byte FlattenedMask;
            public string Warning = "";               // "" when nothing is flattened
            public string FixText = "";               // "Fit Z on its own"
        }

        public static View Build(AxisSettings settings, AxisBounds data, byte flattenedMask)
        {
            settings ??= AxisSettings.CreateDefault();
            var v = new View
            {
                Aspect = settings.AspectMode,
                AspectText = AxisSettings.AspectText(settings.AspectMode),
                PaddingZero = settings.Padding == 0f,
                PaddingDefault = settings.Padding == AxisSettings.DefaultPadding,
                FlattenedMask = flattenedMask,
            };

            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale scale = data.Get(axis);
                AxisRow row = v.Rows[axis];
                row.Name = AxisSettings.AxisName(axis);
                row.Numeric = scale.Mapped && !scale.Categorical;
                row.Mode = (int)settings.Mode(axis);
                row.Reversed = settings.Reversed(axis);
                row.InGroup = settings.InGroup(axis);
                row.LimitsText = row.Numeric ? LimitsText(settings, data, axis) : NotNumericText;
            }

            string names = MaskNames(flattenedMask);
            if (names.Length > 0)
            {
                bool one = (flattenedMask & (flattenedMask - 1)) == 0;
                v.Warning = $"{names} {(one ? "is" : "are")} drawn at under 5 % of the cube";
                v.FixText = $"Fit {names} on {(one ? "its" : "their")} own";
            }
            return v;
        }

        /// <summary>"0 to 6": the stored limits when there are any, else the data range.</summary>
        public static string LimitsText(AxisSettings settings, AxisBounds data, int axis)
        {
            LimitsOrData(settings, data, axis, out float min, out float max);
            return Number(min) + " to " + Number(max);
        }

        /// <summary>The stored limits when usable (min &lt; max), else the axis's data range.</summary>
        public static void LimitsOrData(AxisSettings settings, AxisBounds data, int axis, out float min, out float max)
        {
            if (settings != null && settings.LimitMin(axis) < settings.LimitMax(axis))
            {
                min = settings.LimitMin(axis);
                max = settings.LimitMax(axis);
                return;
            }
            AxisScale scale = data.Get(axis);
            min = Math.Min(scale.Min, scale.Max);
            max = Math.Max(scale.Min, scale.Max);
        }

        /// <summary>What the VR keyboard opens with: "min max".</summary>
        public static string KeyboardText(AxisSettings settings, AxisBounds data, int axis)
        {
            LimitsOrData(settings, data, axis, out float min, out float max);
            return Number(min) + " " + Number(max);
        }

        /// <summary>
        /// Two numbers separated by spaces (also accepts "to" or a comma between them), invariant
        /// culture. False with a message for anything else or two equal numbers. Returned smallest first.
        /// </summary>
        public static bool TryParseLimits(string text, out float min, out float max, out string error)
        {
            min = max = 0f;
            error = null;
            string[] parts = (text ?? "").Replace(" to ", " ").Replace(',', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !float.TryParse(parts[0], NumberStyles.Float, k_Inv, out float a) ||
                !float.TryParse(parts[1], NumberStyles.Float, k_Inv, out float b) ||
                !float.IsFinite(a) || !float.IsFinite(b))
            {
                error = "Type two numbers with a space between them, e.g. 0 6";
                return false;
            }
            if (a == b)
            {
                error = "The two limits must be different";
                return false;
            }
            min = Math.Min(a, b);
            max = Math.Max(a, b);
            return true;
        }

        /// <summary>"Z", "Y and Z", "X, Y and Z"; "" for none.</summary>
        public static string MaskNames(byte mask)
        {
            var names = new System.Collections.Generic.List<string>();
            for (int axis = 0; axis < 3; axis++)
                if ((mask & (1 << axis)) != 0)
                    names.Add(AxisSettings.AxisName(axis));
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
        }

        /// <summary>Changes when anything the controls show changes (settings, flattened axes, data range).</summary>
        public static int Signature(AxisSettings settings, AxisBounds data, byte flattenedMask)
        {
            var h = new HashCode();
            h.Add(settings?.Hash() ?? 0);
            h.Add(flattenedMask);
            for (int axis = 0; axis < 3; axis++)
            {
                AxisScale s = data.Get(axis);
                h.Add(s.Mapped);
                h.Add(s.Categorical);
                h.Add(s.Min);
                h.Add(s.Max);
            }
            return h.ToHashCode();
        }

        public static string Number(float v) => v.ToString("0.###", k_Inv);
    }
}
