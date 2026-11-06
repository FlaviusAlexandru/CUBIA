using System.Collections.Generic;
using System.Text;

namespace DataViz.Study
{
    /// <summary>RFC 4180 field escaping for the study CSV.</summary>
    public static class CsvUtil
    {
        /// <summary>Quotes the field if it contains a comma, quote, CR or LF; inner quotes are doubled. Null becomes "".</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            bool needsQuotes = value.IndexOfAny(k_Special) >= 0;
            if (!needsQuotes)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>Escapes every field and joins them with commas (no line terminator).</summary>
        public static string JoinRow(IEnumerable<string> fields)
        {
            var sb = new StringBuilder();
            bool first = true;
            if (fields != null)
            {
                foreach (string field in fields)
                {
                    if (!first)
                        sb.Append(',');
                    sb.Append(Escape(field));
                    first = false;
                }
            }
            return sb.ToString();
        }

        static readonly char[] k_Special = { ',', '"', '\r', '\n' };
    }
}
