using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DataViz.Study
{
    /// <summary>
    /// Appends <see cref="StudyLogRow"/>s to one CSV file (UTF-8 without BOM). The header is written
    /// only when the file is empty, and every row is flushed to disk so a crash loses at most the
    /// row being written. Plain C#, no Unity types, so it can be exercised by the editor self-tests.
    /// </summary>
    public sealed class StudyLogWriter : IDisposable
    {
        public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";
        public const string SessionTimeFormat = "0.00";

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        FileStream m_Stream;
        StreamWriter m_Writer;

        public string FilePath { get; }

        /// <summary>Opens (or creates) the file for append. Throws on IO errors; the caller reports them.</summary>
        public StudyLogWriter(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path is empty", nameof(filePath));

            FilePath = filePath;
            m_Stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            try
            {
                m_Writer = new StreamWriter(m_Stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = false };
                if (m_Stream.Length == 0)
                    WriteLine(CsvUtil.JoinRow(StudyLogRow.Header));
            }
            catch
            {
                m_Writer?.Dispose();
                m_Stream.Dispose();
                m_Writer = null;
                m_Stream = null;
                throw;
            }
        }

        public bool IsOpen => m_Writer != null;

        /// <summary>Writes one row and flushes it to disk. Throws ObjectDisposedException when closed, IOException on disk errors.</summary>
        public void WriteRow(StudyLogRow row)
        {
            if (row == null)
                throw new ArgumentNullException(nameof(row));
            if (m_Writer == null)
                throw new ObjectDisposedException(nameof(StudyLogWriter));

            WriteLine(FormatRow(row));
        }

        /// <summary>The CSV line for a row (no terminator), formatted with the invariant culture.</summary>
        public static string FormatRow(StudyLogRow row)
        {
            return CsvUtil.JoinRow(new[]
            {
                row.Timestamp.ToString(TimestampFormat, k_Inv),
                row.SessionTime.ToString(SessionTimeFormat, k_Inv),
                row.ParticipantId ?? "",
                row.Part.ToString(k_Inv),
                SourceName(row.Source),
                row.Action ?? "",
                row.OldValue ?? "",
                row.NewValue ?? "",
                row.RequestText ?? "",
                row.Tag == RequestTag.None ? "" : row.Tag.ToString(),
                row.PlotState ?? "",
            });
        }

        public static string SourceName(ChangeSource source)
        {
            switch (source)
            {
                case ChangeSource.Facilitator: return "facilitator";
                case ChangeSource.Participant: return "participant";
                case ChangeSource.Request: return "request";
                case ChangeSource.Marker: return "marker";
                default: return source.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// P{id}_part{part}_{yyyyMMdd_HHmmss}.csv. The id keeps only letters, digits, '-' and '_',
        /// and a leading "P" is not doubled ("P07" and "07" both give "P07").
        /// </summary>
        public static string BuildFileName(string participantId, int part, DateTime now)
        {
            var sb = new StringBuilder();
            foreach (char c in (participantId ?? "").Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
            }

            string id = sb.ToString();
            if (id.Length > 0 && (id[0] == 'P' || id[0] == 'p'))
                id = id.Substring(1);
            if (id.Length == 0)
                id = "unknown";

            return "P" + id + "_part" + part.ToString(k_Inv) + "_" + now.ToString("yyyyMMdd_HHmmss", k_Inv) + ".csv";
        }

        void WriteLine(string line)
        {
            m_Writer.WriteLine(line);
            m_Writer.Flush();
            m_Stream.Flush(true); // force through the OS cache: a crashed session must keep its rows
        }

        public void Dispose()
        {
            try
            {
                m_Writer?.Dispose(); // also disposes m_Stream
            }
            finally
            {
                m_Stream?.Dispose();
                m_Writer = null;
                m_Stream = null;
            }
        }
    }
}
