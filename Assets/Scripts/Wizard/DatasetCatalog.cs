using System;
using System.Collections.Generic;
using System.IO;

namespace DataViz.Wizard
{
    /// <summary>
    /// The .cdataset files the data scene can load (ColumnarBinaryImporter.DataFolder), with their
    /// header summaries cached per file version so the dataset cards don't re-read files.
    /// </summary>
    public static class DatasetCatalog
    {
        public const string Extension = ".cdataset";

        static readonly Dictionary<string, (DateTime written, DatasetSummary summary)> s_Headers = new();

        /// <summary>File names (e.g. "clusters.cdataset"), sorted by name.</summary>
        public static List<string> Files() => DatasetStore.Files();

        /// <summary>"name" or "name.cdataset" to the file name, or null when no such file exists.</summary>
        public static string Resolve(string datasetName)
        {
            if (string.IsNullOrWhiteSpace(datasetName))
                return null;
            string file = Path.GetFileName(datasetName.Trim());
            if (!file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                file += Extension;
            return DatasetStore.Exists(file) ? file : null;
        }

        /// <summary>Names, types and row count only (fast for any file size). Null if unreadable.</summary>
        public static DatasetSummary Header(string file)
        {
            DateTime written = File.GetLastWriteTimeUtc(Path.Combine(ColumnarBinaryImporter.DataFolder, file));
            if (s_Headers.TryGetValue(file, out var cached) && cached.written == written)
                return cached.summary;

            DatasetSummary summary = ColumnarBinaryImporter.ReadSummary(file, includeCellStats: false);
            s_Headers[file] = (written, summary);
            return summary;
        }

        /// <summary>Header plus empty-cell counts and sample values, for the column steps.</summary>
        public static DatasetSummary Full(string file) => ColumnarBinaryImporter.ReadSummary(file);
    }
}
