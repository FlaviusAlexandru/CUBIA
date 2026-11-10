using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DataViz.Networking;

namespace DataViz.Study
{
    /// <summary>
    /// Compact single-line JSON of the plot state for the PlotState CSV column, with column NAMES
    /// so a row can be read without the dataset. Hand-written (JsonUtility cannot do nulls or
    /// nested arrays) and culture-invariant.
    /// Keys: ds, x, y, z, color, size (null unless size is mapped), time, glyph, pointSize,
    /// filters [{col, value, ranges:[[min,max],...]}], playing, scrub. These describe the active
    /// layer; the stack overload adds "active" (its id) and "layers" (see below).
    /// </summary>
    public static class PlotStateJson
    {
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        public static string Write(PlotSnapshot s, DatasetColumnar dataset)
        {
            if (s == null)
                return "{}";

            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendActive(sb, s, dataset);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Stack-aware overload (what the logger writes). datasetOf resolves a layer's dataset file
        /// name to the loaded dataset (may return null; columns are then "#index"). The keys of the
        /// other overload for the active layer, then "active":"L2" and "layers":[…] with one object
        /// per layer in stack order: id, name, kind (Dataset/Subset/Computed), visible, ds, x, y, z,
        /// norm, color, fixed ("#RRGGBB[AA]"), size (null unless mapped), pointSize, opacity,
        /// glyph (column), glyph0 (default glyph index), time, timeMode (Follow/Always/Span), end,
        /// label, filters, src (source layer id or null), hideInSrc, computed (kind name or null),
        /// axis ("Auto"/"X"/"Y"/"Z"), order (column, "Auto" or null), flag, value, span, lineWidth.
        /// Column names come from each layer's own dataset (a computed layer: its source's).
        /// </summary>
        public static string Write(PlotSnapshot s, System.Func<string, DatasetColumnar> datasetOf)
        {
            if (s == null)
                return "{}";

            var sb = new StringBuilder(256 + 384 * (s.Layers?.Length ?? 0));
            sb.Append('{');
            LayerSnapshot active = s.ActiveLayer;
            AppendActive(sb, s, PlotStateDiff.DatasetOfLayer(s, active, datasetOf));
            sb.Append(",\"active\":"); AppendString(sb, s.ActiveLayerId ?? "");
            sb.Append(",\"layers\":[");
            bool first = true;
            foreach (LayerSnapshot layer in s.Layers ?? System.Array.Empty<LayerSnapshot>())
            {
                if (layer == null)
                    continue;
                if (!first) sb.Append(',');
                first = false;
                AppendLayer(sb, layer, PlotStateDiff.DatasetOfLayer(s, layer, datasetOf));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // The pre-layer keys (no braces), read from the active layer through the flat members.
        static void AppendActive(StringBuilder sb, PlotSnapshot s, DatasetColumnar dataset)
        {
            sb.Append("\"ds\":"); AppendString(sb, s.DatasetName ?? "");
            sb.Append(",\"x\":"); AppendColumn(sb, s.XColumn, dataset);
            sb.Append(",\"y\":"); AppendColumn(sb, s.YColumn, dataset);
            sb.Append(",\"z\":"); AppendColumn(sb, s.ZColumn, dataset);
            sb.Append(",\"color\":"); AppendColumn(sb, s.ColourColumn, dataset);
            sb.Append(",\"size\":");
            if (s.SizeMapped) AppendColumn(sb, s.SizeColumn, dataset);
            else sb.Append("null");
            sb.Append(",\"time\":"); AppendColumn(sb, s.TimeColumn, dataset);
            sb.Append(",\"glyph\":"); AppendColumn(sb, s.GlyphColumn, dataset);
            sb.Append(",\"pointSize\":"); AppendNumber(sb, s.PointSize, "0.###");
            sb.Append(",\"filters\":"); AppendFilters(sb, PlotStateDiff.Filters(s), dataset);
            sb.Append(",\"playing\":").Append(s.IsPlaying ? "true" : "false");
            sb.Append(",\"scrub\":"); AppendNumber(sb, s.TimeScrub, "0.00");
        }

        static void AppendLayer(StringBuilder sb, LayerSnapshot l, DatasetColumnar dataset)
        {
            var kind = (LayerKind)l.Kind;
            bool computed = kind == LayerKind.Computed;
            sb.Append("{\"id\":"); AppendString(sb, l.Id ?? "");
            sb.Append(",\"name\":"); AppendString(sb, l.Name ?? "");
            sb.Append(",\"kind\":"); AppendString(sb, kind.ToString());
            sb.Append(",\"visible\":").Append(l.Visible ? "true" : "false");
            sb.Append(",\"ds\":"); AppendString(sb, l.DatasetName ?? "");
            sb.Append(",\"x\":"); AppendColumn(sb, l.XColumn, dataset);
            sb.Append(",\"y\":"); AppendColumn(sb, l.YColumn, dataset);
            sb.Append(",\"z\":"); AppendColumn(sb, l.ZColumn, dataset);
            sb.Append(",\"norm\":").Append(l.Normalised ? "true" : "false");
            sb.Append(",\"color\":"); AppendColumn(sb, l.ColourColumn, dataset);
            sb.Append(",\"fixed\":"); AppendString(sb, PlotStateDiff.ColourText(l.FixedColour));
            sb.Append(",\"size\":");
            if (l.SizeMapped) AppendColumn(sb, l.SizeColumn, dataset);
            else sb.Append("null");
            sb.Append(",\"pointSize\":"); AppendNumber(sb, l.PointSize, "0.###");
            sb.Append(",\"opacity\":"); AppendNumber(sb, l.Opacity, "0.##");
            sb.Append(",\"glyph\":"); AppendColumn(sb, l.GlyphColumn, dataset);
            sb.Append(",\"glyph0\":").Append(l.DefaultGlyph.ToString(k_Inv));
            sb.Append(",\"time\":"); AppendColumn(sb, l.TimeColumn, dataset);
            sb.Append(",\"timeMode\":"); AppendString(sb, PlotStateDiff.TimeBehaviourText(l.TimeBehaviour));
            sb.Append(",\"end\":"); AppendColumn(sb, l.EndTimeColumn, dataset);
            sb.Append(",\"label\":"); AppendColumn(sb, l.LabelColumn, dataset);
            sb.Append(",\"filters\":"); AppendFilters(sb, PlotStateDiff.Filters(l), dataset);
            sb.Append(",\"src\":");
            if (string.IsNullOrEmpty(l.SourceLayerId)) sb.Append("null");
            else AppendString(sb, l.SourceLayerId);
            sb.Append(",\"hideInSrc\":").Append(l.HiddenInSource ? "true" : "false");
            sb.Append(",\"computed\":");
            if (computed) AppendString(sb, LayerRules.ComputedKindName((ComputedLayerKind)l.ComputedKind));
            else sb.Append("null");
            sb.Append(",\"axis\":");
            if (computed) AppendString(sb, PlotStateDiff.AxisText(l.DriverAxis));
            else sb.Append("null");
            sb.Append(",\"order\":");
            if (!computed) sb.Append("null");
            else if (l.OrderColumn < 0) AppendString(sb, "Auto");
            else AppendColumn(sb, l.OrderColumn, dataset);
            sb.Append(",\"flag\":").Append(l.Smoothed ? "true" : "false");
            sb.Append(",\"value\":"); AppendRangeValue(sb, l.RefValue);
            sb.Append(",\"span\":"); AppendNumber(sb, l.SmoothSpan, "0.###");
            sb.Append(",\"lineWidth\":"); AppendNumber(sb, l.LineWidth, "0.####");
            sb.Append('}');
        }

        static void AppendFilters(StringBuilder sb, List<PlotStateDiff.FilterInfo> filters, DatasetColumnar dataset)
        {
            sb.Append('[');
            for (int i = 0; i < filters.Count; i++)
            {
                PlotStateDiff.FilterInfo f = filters[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"col\":"); AppendColumn(sb, f.Column, dataset);
                sb.Append(",\"value\":"); AppendString(sb, f.Label ?? "");
                sb.Append(",\"ranges\":[");
                for (int k = 0; k < f.Ranges.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append('[');
                    AppendRangeValue(sb, f.Ranges[k].min);
                    sb.Append(',');
                    AppendRangeValue(sb, f.Ranges[k].max);
                    sb.Append(']');
                }
                sb.Append("]}");
            }
            sb.Append(']');
        }

        // -1 is JSON null; otherwise the name (or "#index" when it cannot be resolved).
        static void AppendColumn(StringBuilder sb, int index, DatasetColumnar dataset)
        {
            if (index < 0)
                sb.Append("null");
            else
                AppendString(sb, PlotStateDiff.ColumnName(index, dataset));
        }

        static void AppendNumber(StringBuilder sb, float value, string format)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                sb.Append("null"); // not representable in JSON
            else
                sb.Append(value.ToString(format, k_Inv));
        }

        static void AppendRangeValue(StringBuilder sb, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                sb.Append("null");
            else
                sb.Append(PlotStateDiff.FormatFloat(value));
        }

        /// <summary>JSON string literal with RFC 8259 escaping (quotes, backslash, control chars).</summary>
        internal static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", k_Inv));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
