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
    /// filters [{col, value, ranges:[[min,max],...]}], playing, scrub.
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

            sb.Append(",\"filters\":[");
            List<PlotStateDiff.FilterInfo> filters = PlotStateDiff.Filters(s);
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

            sb.Append(",\"playing\":").Append(s.IsPlaying ? "true" : "false");
            sb.Append(",\"scrub\":"); AppendNumber(sb, s.TimeScrub, "0.00");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Stack-aware overload: datasetOf resolves a layer's dataset file name (may return null).
        /// WP1 body: the legacy keys for the active layer only; WP7 adds "active" and "layers".
        /// </summary>
        public static string Write(PlotSnapshot s, System.Func<string, DatasetColumnar> datasetOf)
        {
            if (s == null)
                return "{}";
            return Write(s, datasetOf?.Invoke(s.DatasetName ?? ""));
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
