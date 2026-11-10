using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// Session cache of loaded datasets, keyed by file name, so several layers on one file share
    /// one DatasetColumnar instance. Names are normalised like the manager always did: ".csv" and
    /// ".dataset" become ".cdataset". Load reads the file again (what MultiplayerScatterplotManager
    /// .LoadLocalDataset always did); Get returns the cached instance and only reads on first use.
    /// Register pins an in-memory dataset (tests, fixtures): Load and Get then return it without
    /// touching the disk, and Trim never drops it.
    /// </summary>
    public static class DatasetRegistry
    {
        sealed class Entry
        {
            public DatasetColumnar Dataset;
            public string LoadedName; // what CurrentDatasetName becomes: the .cdataset name, or the .csv name after the CSV fallback
            public bool Pinned;
        }

        static readonly Dictionary<string, Entry> s_Entries = new(StringComparer.Ordinal);

        /// <summary>"x.csv" / "x.dataset" → "x.cdataset"; null → "".</summary>
        public static string Normalise(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "";
            if (fileName.EndsWith(".cdataset"))
                return fileName;
            return fileName.Replace(".csv", ".cdataset").Replace(".dataset", ".cdataset");
        }

        /// <summary>The cached dataset for fileName, loading it on first use (quietly: a missing file returns null without an error log).</summary>
        public static DatasetColumnar Get(string fileName) => Get(fileName, out _);

        /// <summary>Get, plus the name the dataset was loaded under (the .csv name after the CSV fallback).</summary>
        public static DatasetColumnar Get(string fileName, out string loadedName)
        {
            string key = Normalise(fileName);
            loadedName = key;
            if (key.Length == 0)
                return null;

            if (s_Entries.TryGetValue(key, out Entry entry) && entry.Dataset != null)
            {
                loadedName = entry.LoadedName;
                return entry.Dataset;
            }

            if (!FileExists(key))
                return null;

            return Load(fileName, out loadedName);
        }

        /// <summary>True when fileName is in the cache.</summary>
        public static bool IsLoaded(string fileName) =>
            s_Entries.TryGetValue(Normalise(fileName), out Entry entry) && entry.Dataset != null;

        /// <summary>
        /// Reads fileName from disk (columnar first, then the slow CSV fallback with its error log,
        /// exactly as LoadLocalDataset did before layers) and replaces the cache entry. A pinned
        /// entry is returned as is. Returns null, and drops the entry, when both reads fail.
        /// </summary>
        public static DatasetColumnar Load(string fileName, out string loadedName)
        {
            string key = Normalise(fileName);
            loadedName = key;
            if (key.Length == 0)
                return null;

            if (s_Entries.TryGetValue(key, out Entry pinned) && pinned.Pinned)
            {
                loadedName = pinned.LoadedName;
                return pinned.Dataset;
            }

            DatasetColumnar dataset = ColumnarBinaryImporter.Load(key);
            if (dataset != null)
            {
                Debug.Log($"Loaded columnar dataset '{dataset.Name}' ({dataset.RowCount} rows, {dataset.ColumnCount} columns)");
                s_Entries[key] = new Entry { Dataset = dataset, LoadedName = key };
                return dataset;
            }

            Debug.LogError(
                $"[MultiplayerScatterplotManager] FALLING BACK TO SLOW CSV IMPORT\n" +
                $"Columnar binary dataset failed to load: '{key}'\n" +
                $"This dataset will be parsed via the legacy row-based CSVImporter, " +
                $"which is significantly slower and was NOT the intended fast path.\n" +
                $"Check that a matching .cdataset file exists in " +
                $"Assets/StreamingAssets/DataCSV/ProcessedData/ (run preprocess_csv_columnar.py if not)."
            );

            string csvFileName = key.Replace(".cdataset", ".csv");
            string csvPath = Path.Combine(Application.streamingAssetsPath, "DataCSV", csvFileName);
            Dataset legacyDataset = CSVImporter.Load(csvPath);
            if (legacyDataset == null)
            {
                Debug.LogError($"Failed to load dataset: {key}");
                s_Entries.Remove(key);
                return null;
            }

            dataset = ConvertToColumnar(legacyDataset);
            Debug.Log($"Loaded legacy CSV dataset as columnar '{dataset.Name}' ({dataset.RowCount} rows, {dataset.ColumnCount} columns)");
            loadedName = csvFileName;
            s_Entries[key] = new Entry { Dataset = dataset, LoadedName = csvFileName };
            return dataset;
        }

        /// <summary>Pins an in-memory dataset under fileName (normalised). Tests must Unregister it afterwards.</summary>
        public static void Register(string fileName, DatasetColumnar dataset)
        {
            string key = Normalise(fileName);
            if (key.Length == 0 || dataset == null)
                return;
            s_Entries[key] = new Entry { Dataset = dataset, LoadedName = key, Pinned = true };
        }

        public static void Unregister(string fileName) => s_Entries.Remove(Normalise(fileName));

        /// <summary>
        /// Drops every unpinned entry whose name is not in keep, so a dataset no layer uses any more
        /// can be collected (one dataset can be 1M rows). Names are normalised before comparing.
        /// </summary>
        public static void Trim(IEnumerable<string> keep)
        {
            var kept = new HashSet<string>(StringComparer.Ordinal);
            if (keep != null)
            {
                foreach (string name in keep)
                    kept.Add(Normalise(name));
            }

            var drop = new List<string>();
            foreach (KeyValuePair<string, Entry> kv in s_Entries)
            {
                if (!kv.Value.Pinned && !kept.Contains(kv.Key))
                    drop.Add(kv.Key);
            }

            foreach (string key in drop)
                s_Entries.Remove(key);
        }

        static bool FileExists(string columnarName)
        {
            try
            {
                if (DatasetStore.Exists(columnarName))
                    return true;
                string csvPath = Path.Combine(Application.streamingAssetsPath, "DataCSV", columnarName.Replace(".cdataset", ".csv"));
                return !DatasetStore.IsPacked && File.Exists(csvPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Moved unchanged from MultiplayerScatterplotManager: wraps a legacy row-based Dataset.
        static DatasetColumnar ConvertToColumnar(Dataset legacyDataset)
        {
            DatasetColumnar columnar = new DatasetColumnar(legacyDataset.Name, legacyDataset.RowCount, legacyDataset.ColumnCount);

            foreach (var legacyColumn in legacyDataset.Columns)
            {
                columnar.Columns.Add(legacyColumn);
                columnar.ColumnMapping[legacyColumn.Name] = columnar.Columns.Count - 1;
            }

            for (int colIndex = 0; colIndex < legacyDataset.ColumnCount; colIndex++)
            {
                DatasetColumn column = legacyDataset.Columns[colIndex];

                if (column.IsNumeric)
                {
                    float[] numericData = new float[legacyDataset.RowCount];
                    for (int rowIndex = 0; rowIndex < legacyDataset.RowCount; rowIndex++)
                        numericData[rowIndex] = legacyDataset.Rows[rowIndex].GetNumericValue(colIndex);
                    columnar.SetNumericColumn(colIndex, numericData);
                }
                else if (column.IsCategorical)
                {
                    string[] categoryTable = new string[column.UniqueCount];
                    int idx = 0;
                    foreach (string cat in column.UniqueValues)
                        categoryTable[idx++] = cat;

                    int[] categoryIndices = new int[legacyDataset.RowCount];
                    for (int rowIndex = 0; rowIndex < legacyDataset.RowCount; rowIndex++)
                    {
                        string rawValue = legacyDataset.Rows[rowIndex].GetRawValue(colIndex);
                        int catIndex = Array.IndexOf(categoryTable, rawValue);
                        categoryIndices[rowIndex] = catIndex >= 0 ? catIndex : 0;
                    }

                    columnar.SetCategoricalColumn(colIndex, categoryIndices, categoryTable);
                }
            }

            columnar.FinalizeDatasetMetadata();
            return columnar;
        }
    }
}
