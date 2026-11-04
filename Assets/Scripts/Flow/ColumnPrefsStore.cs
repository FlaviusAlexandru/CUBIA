using System.Collections.Generic;

namespace DataViz.Flow
{
    /// <summary>
    /// STUB — owner: wizard agent. Per-dataset bookmarks (ordered) and blacklist, in the same JSON
    /// files MultiplayerScatterplotManager uses today (scatterplot_bookmark_prefs.json,
    /// scatterplot_column_prefs.json), so existing prefs survive. The wizard agent moves the
    /// manager's JSON code here and makes the manager use it.
    /// </summary>
    public static class ColumnPrefsStore
    {
        public static List<string> GetBookmarks(string datasetName) => new();
        public static void SetBookmarks(string datasetName, IReadOnlyList<string> orderedColumns) { }
        public static HashSet<string> GetBlacklist(string datasetName) => new();
        public static void SetBlacklist(string datasetName, IEnumerable<string> columns) { }
    }

    /// <summary>
    /// STUB — owner: wizard agent. The single "default on load" dataset (at most one), stored in
    /// cubia_launch_prefs.json under Application.persistentDataPath. Empty = none: show the wizard.
    /// </summary>
    public static class LaunchPrefs
    {
        public static string DefaultDataset { get => ""; set { } }
    }
}
