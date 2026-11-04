using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DataViz.Flow
{
    /// <summary>
    /// Per-dataset bookmarks (ordered: bookmark order = palette order) and shuffle blacklist,
    /// stored in scatterplot_bookmark_prefs.json and scatterplot_column_prefs.json under
    /// Application.persistentDataPath. Same file format MultiplayerScatterplotManager wrote before
    /// this class existed, so existing prefs keep loading.
    ///
    /// Dataset keys are the dataset name without extension (DatasetColumnar.Name). Callers may pass
    /// a file name ("foo.cdataset", "foo.csv"); the known extensions are stripped.
    /// Getters return copies: change them, then hand them back through the setters (which save).
    /// </summary>
    public static class ColumnPrefsStore
    {
        [Serializable]
        class DatasetColumnPrefs
        {
            public string DatasetName;
            public List<string> BlacklistedColumns = new();
        }

        [Serializable]
        class ColumnPrefsFile
        {
            public List<DatasetColumnPrefs> Datasets = new();
        }

        [Serializable]
        class DatasetBookmarkPrefs
        {
            public string DatasetName;
            public List<string> BookmarkedColumns = new();
        }

        [Serializable]
        class BookmarkPrefsFile
        {
            public List<DatasetBookmarkPrefs> Datasets = new();
        }

        static readonly string[] k_KnownExtensions = { ".cdataset", ".csv", ".dataset" };

        static Dictionary<string, HashSet<string>> s_Blacklists;
        static Dictionary<string, List<string>> s_Bookmarks;

        static string BlacklistPath => Path.Combine(Application.persistentDataPath, "scatterplot_column_prefs.json");
        static string BookmarkPath => Path.Combine(Application.persistentDataPath, "scatterplot_bookmark_prefs.json");

        /// <summary>The prefs key for a dataset or file name.</summary>
        public static string KeyFor(string datasetName)
        {
            if (string.IsNullOrEmpty(datasetName))
                return "";
            string name = Path.GetFileName(datasetName);
            foreach (string ext in k_KnownExtensions)
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - ext.Length);
            return name;
        }

        public static List<string> GetBookmarks(string datasetName)
        {
            EnsureLoaded();
            return s_Bookmarks.TryGetValue(KeyFor(datasetName), out List<string> list) ? new List<string>(list) : new List<string>();
        }

        public static void SetBookmarks(string datasetName, IReadOnlyList<string> orderedColumns)
        {
            string key = KeyFor(datasetName);
            if (key.Length == 0)
                return;
            EnsureLoaded();

            var ordered = new List<string>();
            if (orderedColumns != null)
                foreach (string column in orderedColumns)
                    if (!string.IsNullOrEmpty(column) && !ordered.Contains(column))
                        ordered.Add(column);

            s_Bookmarks[key] = ordered;
            SaveBookmarks();
        }

        public static HashSet<string> GetBlacklist(string datasetName)
        {
            EnsureLoaded();
            return s_Blacklists.TryGetValue(KeyFor(datasetName), out HashSet<string> set) ? new HashSet<string>(set) : new HashSet<string>();
        }

        public static void SetBlacklist(string datasetName, IEnumerable<string> columns)
        {
            string key = KeyFor(datasetName);
            if (key.Length == 0)
                return;
            EnsureLoaded();

            var set = new HashSet<string>();
            if (columns != null)
                foreach (string column in columns)
                    if (!string.IsNullOrEmpty(column))
                        set.Add(column);

            s_Blacklists[key] = set;
            SaveBlacklists();
        }

        /// <summary>True when any bookmark or blacklist entry is stored for the dataset.</summary>
        public static bool HasSavedSetup(string datasetName)
        {
            EnsureLoaded();
            string key = KeyFor(datasetName);
            return (s_Bookmarks.TryGetValue(key, out List<string> b) && b.Count > 0)
                || (s_Blacklists.TryGetValue(key, out HashSet<string> s) && s.Count > 0);
        }

        static void EnsureLoaded()
        {
            if (s_Blacklists != null)
                return;

            s_Blacklists = new Dictionary<string, HashSet<string>>();
            s_Bookmarks = new Dictionary<string, List<string>>();

            ColumnPrefsFile columns = ReadJson<ColumnPrefsFile>(BlacklistPath);
            if (columns?.Datasets != null)
                foreach (DatasetColumnPrefs entry in columns.Datasets)
                    if (!string.IsNullOrEmpty(entry.DatasetName))
                        s_Blacklists[entry.DatasetName] = new HashSet<string>(entry.BlacklistedColumns ?? new List<string>());

            BookmarkPrefsFile bookmarks = ReadJson<BookmarkPrefsFile>(BookmarkPath);
            if (bookmarks?.Datasets != null)
                foreach (DatasetBookmarkPrefs entry in bookmarks.Datasets)
                {
                    if (string.IsNullOrEmpty(entry.DatasetName) || entry.BookmarkedColumns == null)
                        continue;
                    var ordered = new List<string>();
                    foreach (string column in entry.BookmarkedColumns)
                        if (!string.IsNullOrEmpty(column) && !ordered.Contains(column))
                            ordered.Add(column);
                    s_Bookmarks[entry.DatasetName] = ordered;
                }
        }

        static void SaveBlacklists()
        {
            var data = new ColumnPrefsFile();
            foreach (KeyValuePair<string, HashSet<string>> kvp in s_Blacklists)
                if (kvp.Value.Count > 0) // empty entries are not persisted
                    data.Datasets.Add(new DatasetColumnPrefs { DatasetName = kvp.Key, BlacklistedColumns = new List<string>(kvp.Value) });
            WriteJson(BlacklistPath, data);
        }

        static void SaveBookmarks()
        {
            var data = new BookmarkPrefsFile();
            foreach (KeyValuePair<string, List<string>> kvp in s_Bookmarks)
                if (kvp.Value.Count > 0)
                    data.Datasets.Add(new DatasetBookmarkPrefs { DatasetName = kvp.Key, BookmarkedColumns = new List<string>(kvp.Value) });
            WriteJson(BookmarkPath, data);
        }

        internal static T ReadJson<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ColumnPrefsStore] Failed to read {path}: {e.Message}");
                return null;
            }
        }

        internal static void WriteJson(string path, object data)
        {
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ColumnPrefsStore] Failed to write {path}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The single "default on load" dataset (at most one, as a .cdataset file name), stored in
    /// cubia_launch_prefs.json under Application.persistentDataPath. Empty = none: show the wizard.
    /// </summary>
    public static class LaunchPrefs
    {
        [Serializable]
        class LaunchPrefsFile
        {
            public string DefaultDataset = "";
        }

        static string s_DefaultDataset;

        static string FilePath => Path.Combine(Application.persistentDataPath, "cubia_launch_prefs.json");

        public static string DefaultDataset
        {
            get
            {
                s_DefaultDataset ??= ColumnPrefsStore.ReadJson<LaunchPrefsFile>(FilePath)?.DefaultDataset ?? "";
                return s_DefaultDataset;
            }
            set
            {
                s_DefaultDataset = value ?? "";
                ColumnPrefsStore.WriteJson(FilePath, new LaunchPrefsFile { DefaultDataset = s_DefaultDataset });
            }
        }
    }
}
