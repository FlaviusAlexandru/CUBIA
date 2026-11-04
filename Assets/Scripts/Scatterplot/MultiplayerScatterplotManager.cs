using System;
using System.Collections.Generic;
using System.IO;
using DataViz.Flow;
using DataViz.Networking;
using DataViz.Wizard;
using UnityEngine;

namespace DataViz
{
    public class MultiplayerScatterplotManager : MonoBehaviour
    {
        public static MultiplayerScatterplotManager Instance { get; private set; }

        [Header("Dataset Settings")]
        public string CurrentDatasetName = "test.csv";

        [Header("Visualization Settings")]
        public int XColumnIndex = 0;
        public int YColumnIndex = 1;
        public int ZColumnIndex = 2;
        public int ColorColumnIndex = -1;
        public float PointSize = 0.1f;
        public const float MaxPointSize = 0.1f; // the point size slider's 100% maps to this

        [Header("Temporal Settings")]
        public int TimeColumnIndex = -1;
        [Range(0f, 1f)]
        public float TimeScrub = 0f;
        public bool IsPlaying = false;
        public float PlaybackSpeed = 0.25f; // scrub units (0-1) per second

        [Header("Interaction Settings")]
        public bool ShowTooltips = false;

        //<summary>
        // Shuffle locks. Every field Shuffle can change (ShuffleLocks.ShuffleOrder: X, Y, Z, Size,
        // Colour, Glyph, Time) is Locked or Unlocked; Shuffle only re-picks unlocked fields. Locks
        // affect Shuffle and nothing else. They are stored PER DATASET and persisted to disk next
        // to the blacklist and bookmarks (scatterplot_shuffle_lock_prefs.json). Defaults: X/Y/Z
        // unlocked, everything else locked (ShuffleLocks.DefaultLocked). Size can only be unlocked
        // while SizeMapped is on; while Fixed it always counts as locked.
        // XAxisLocked/YAxisLocked/ZAxisLocked, RequestSetAxisLockedRpc and IsAxisLocked are thin
        // wrappers over the X/Y/Z locks, so existing callers keep working.
        //</summary>
        private readonly Dictionary<string, Dictionary<MappingTarget, bool>> m_ShuffleLocksByDataset = new();

        // Used while no dataset is loaded, so lock calls never touch a real dataset's entry.
        private readonly Dictionary<MappingTarget, bool> m_NoDatasetShuffleLocks = ShuffleLocks.CreateDefaults();

        public bool XAxisLocked
        {
            get => IsFieldLocked(MappingTarget.X);
            set => RequestSetFieldLockedRpc(MappingTarget.X, value);
        }

        public bool YAxisLocked
        {
            get => IsFieldLocked(MappingTarget.Y);
            set => RequestSetFieldLockedRpc(MappingTarget.Y, value);
        }

        public bool ZAxisLocked
        {
            get => IsFieldLocked(MappingTarget.Z);
            set => RequestSetFieldLockedRpc(MappingTarget.Z, value);
        }

        public void RequestSetAxisLockedRpc(int axis, bool locked)
        {
            if (TryGetAxisTarget(axis, out MappingTarget target))
                RequestSetFieldLockedRpc(target, locked);
        }

        public bool IsAxisLocked(int axis) => TryGetAxisTarget(axis, out MappingTarget target) && IsFieldLocked(target);

        private static bool TryGetAxisTarget(int axis, out MappingTarget target)
        {
            switch (axis)
            {
                case 0: target = MappingTarget.X; return true;
                case 1: target = MappingTarget.Y; return true;
                case 2: target = MappingTarget.Z; return true;
                default: target = MappingTarget.X; return false;
            }
        }

        /// <summary>
        /// True when Shuffle keeps this field's label. A field Shuffle never touches (Filter), and
        /// Size while Fixed, always count as locked.
        /// </summary>
        public bool IsFieldLocked(MappingTarget target)
        {
            if (!CanUnlockField(target))
                return true;

            return GetShuffleLocksForCurrentDataset().TryGetValue(target, out bool locked)
                ? locked
                : ShuffleLocks.DefaultLocked(target);
        }

        /// <summary>Whether the field can be unlocked right now: any shuffled field, Size only while Mapped.</summary>
        public bool CanUnlockField(MappingTarget target) =>
            ShuffleLocks.IsShuffled(target) && (target != MappingTarget.Size || SizeMapped);

        /// <summary>
        /// True when every field of the island that can be unlocked right now is locked. The
        /// island's Lock all / Unlock all button uses this to decide which of the two it does.
        /// </summary>
        public bool IsIslandLocked(ShuffleIsland island)
        {
            foreach (MappingTarget target in ShuffleLocks.FieldsIn(island))
            {
                if (CanUnlockField(target) && !IsFieldLocked(target))
                    return false;
            }

            return true;
        }

        /// <summary>True when every shuffle field that can be unlocked right now is locked.</summary>
        public bool AreAllFieldsLocked()
        {
            foreach (ShuffleIsland island in ShuffleLocks.Islands)
            {
                if (!IsIslandLocked(island))
                    return false;
            }

            return true;
        }

        public void RequestSetFieldLockedRpc(MappingTarget target, bool locked)
        {
            if (SetFieldLocked(target, locked))
                OnShuffleLocksChanged();
        }

        public void RequestSetIslandLockedRpc(ShuffleIsland island, bool locked)
        {
            bool changed = false;
            foreach (MappingTarget target in ShuffleLocks.FieldsIn(island))
                changed |= SetFieldLocked(target, locked);

            if (changed)
                OnShuffleLocksChanged();
        }

        public void RequestSetAllLockedRpc(bool locked)
        {
            bool changed = false;
            foreach (MappingTarget target in ShuffleLocks.ShuffleOrder)
                changed |= SetFieldLocked(target, locked);

            if (changed)
                OnShuffleLocksChanged();
        }

        // Stores one lock without saving or raising events. Unlocking is refused for a field that
        // can't be unlocked right now (Size while Fixed). Returns true when the stored value changed.
        private bool SetFieldLocked(MappingTarget target, bool locked)
        {
            if (!ShuffleLocks.IsShuffled(target))
                return false;

            if (!locked && !CanUnlockField(target))
                return false;

            Dictionary<MappingTarget, bool> locks = GetShuffleLocksForCurrentDataset();
            if (locks.TryGetValue(target, out bool current) && current == locked)
                return false;

            locks[target] = locked;
            return true;
        }

        private void OnShuffleLocksChanged()
        {
            if (HasNamedDataset)
                SaveShuffleLockPrefs();

            OnPlotSettingsChanged?.Invoke();
        }

        // LoadedDataset is a serialized field, so before the first real load it can be an empty
        // placeholder with no name; locks set then must not be stored under an empty key.
        private bool HasNamedDataset => LoadedDataset != null && !string.IsNullOrEmpty(LoadedDataset.Name);

        private Dictionary<MappingTarget, bool> GetShuffleLocksForCurrentDataset()
        {
            if (!HasNamedDataset)
                return m_NoDatasetShuffleLocks;

            string key = LoadedDataset.Name;

            if (!m_ShuffleLocksByDataset.TryGetValue(key, out Dictionary<MappingTarget, bool> locks))
            {
                locks = ShuffleLocks.CreateDefaults();
                m_ShuffleLocksByDataset[key] = locks;
            }

            return locks;
        }

        // Shuffle lock file: per dataset, one entry per field (field name + Locked).
        [Serializable]
        private class FieldLockPref
        {
            public string Field;
            public bool Locked;
        }

        [Serializable]
        private class DatasetShuffleLockPrefs
        {
            public string DatasetName;
            public List<FieldLockPref> Fields = new();
        }

        [Serializable]
        private class ShuffleLockPrefsFile
        {
            public List<DatasetShuffleLockPrefs> Datasets = new();
        }

        private string ShuffleLockPrefsFilePath => Path.Combine(Application.persistentDataPath, "scatterplot_shuffle_lock_prefs.json");

        private void LoadShuffleLockPrefs()
        {
            try
            {
                if (!File.Exists(ShuffleLockPrefsFilePath))
                    return;

                string json = File.ReadAllText(ShuffleLockPrefsFilePath);
                ShuffleLockPrefsFile data = JsonUtility.FromJson<ShuffleLockPrefsFile>(json);

                if (data?.Datasets == null)
                    return;

                foreach (DatasetShuffleLockPrefs entry in data.Datasets)
                {
                    if (string.IsNullOrEmpty(entry.DatasetName) || entry.Fields == null)
                        continue;

                    // Start from the defaults so a field missing from the file keeps its default.
                    Dictionary<MappingTarget, bool> locks = ShuffleLocks.CreateDefaults();
                    foreach (FieldLockPref field in entry.Fields)
                    {
                        if (Enum.TryParse(field.Field, out MappingTarget target) && ShuffleLocks.IsShuffled(target))
                            locks[target] = field.Locked;
                    }

                    m_ShuffleLocksByDataset[entry.DatasetName] = locks;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MultiplayerScatterplotManager] Failed to load shuffle lock prefs from {ShuffleLockPrefsFilePath}: {e.Message}");
            }
        }

        private void SaveShuffleLockPrefs()
        {
            try
            {
                ShuffleLockPrefsFile data = new ShuffleLockPrefsFile();

                foreach (KeyValuePair<string, Dictionary<MappingTarget, bool>> kvp in m_ShuffleLocksByDataset)
                {
                    if (string.IsNullOrEmpty(kvp.Key))
                        continue;

                    DatasetShuffleLockPrefs entry = new DatasetShuffleLockPrefs { DatasetName = kvp.Key };
                    bool allDefaults = true;
                    foreach (MappingTarget target in ShuffleLocks.ShuffleOrder)
                    {
                        bool locked = kvp.Value.TryGetValue(target, out bool stored) ? stored : ShuffleLocks.DefaultLocked(target);
                        allDefaults &= locked == ShuffleLocks.DefaultLocked(target);
                        entry.Fields.Add(new FieldLockPref { Field = target.ToString(), Locked = locked });
                    }

                    if (allDefaults)
                        continue; // a dataset never changed from the defaults needs no entry

                    data.Datasets.Add(entry);
                }

                File.WriteAllText(ShuffleLockPrefsFilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MultiplayerScatterplotManager] Failed to save shuffle lock prefs to {ShuffleLockPrefsFilePath}: {e.Message}");
            }
        }

        // Bookmarks (per dataset, ordered: the bookmark palette shows its blocks in bookmark order)
        // and the shuffle blacklist (per dataset; a blacklisted column stays choosable by hand) are
        // stored by ColumnPrefsStore, which the import wizard writes too. Both getters return
        // copies; changes are saved back through ColumnPrefsStore.SetBookmarks/SetBlacklist.
        private HashSet<string> GetBlacklistForCurrentDataset() =>
            LoadedDataset == null ? new HashSet<string>() : ColumnPrefsStore.GetBlacklist(LoadedDataset.Name);

        private List<string> GetBookmarksForCurrentDataset() =>
            LoadedDataset == null ? new List<string>() : ColumnPrefsStore.GetBookmarks(LoadedDataset.Name);

        //<summary>
        // Filtering will be used to chose a subset of the data to be displayed. Users will be able to chose a column "Filter label" and index/value "Filter Value"
        //to enable, for example, filtering by participant ID or a specific category.
        //This will help make the datasets less overwhelming and more manageable for users to explore.
        //Filtering is a list of FilterEntry (column + optional value). An entry with an empty Label
        //("Any value" in the UI) keeps every row that HAS a value in that column; a non-empty Label
        //keeps only rows with exactly that value. Several entries combine with OR: a row is shown at
        //full alpha when it passes at least one entry. No entries = no filtering. Everything else is
        //dimmed by ScatterplotVisualizer.
        //FilterColumnIndex/FilterLabel are the FIRST entry, kept for the existing Filter Column /
        //Filter Value dropdowns (which edit that first entry). The bookmark palette's Filter slot
        //appends entries via RequestAddFilterColumnRpc.
        //</summary>
        [Serializable]
        public class FilterEntry
        {
            public int ColumnIndex = -1;
            public string Label = "";
            // Value ranges on a numeric or DateTime column. When there are any, the entry keeps the
            // rows whose value lies in at least one range (Label is then empty and ignored).
            // Set only through RequestSetFilterRangesRpc, which validates the list.
            public List<FilterRange> Ranges = new();
        }

        [Serializable]
        public class FilterRange
        {
            public float min;
            public float max;
        }

        public const int MaxFilterRanges = 5;

        [Header("Filtering Settings")]
        public List<FilterEntry> Filters = new();

        public int FilterColumnIndex => Filters.Count > 0 ? Filters[0].ColumnIndex : -1;
        public string FilterLabel => Filters.Count > 0 ? (Filters[0].Label ?? "") : "";

        //<summary>
        // Size encoding. Fixed (default, SizeMapped == false): every point uses PointSize, exactly as
        // before. Mapped: each point is scaled by its normalised value in SizeColumnIndex, from
        // SizeMappedMinScale x PointSize (column minimum) to SizeMappedMaxScale x PointSize (maximum),
        // so the point-size slider still sets the overall size. Rows with no value in the size
        // column, or no size column chosen, keep PointSize.
        //</summary>
        [Header("Size Settings")]
        public bool SizeMapped = false;
        public int SizeColumnIndex = -1;
        public const float SizeMappedMinScale = 0.25f;
        public const float SizeMappedMaxScale = 1.75f;

        //<summary>
        // Glyphs let users manually assign a specific shape/mesh to a label value on a chosen
        // column (e.g. "Gender" -> "Female" = square), independent of whether that column is
        // one of the plotted X/Y/Z axes - mirrors how ColorColumnIndex is already decoupled
        // from the axes. GlyphAssignments is the manual label -> glyph mapping; unassigned
        // labels fall back to glyph 0 (the base circle) in ScatterplotVisualizer.
        // Rows with no value in the glyph column keep the default glyph.
        //</summary>
        [Header("Glyph Settings")]
        public int GlyphColumnIndex = -1;
        public List<GlyphAssignment> GlyphAssignments = new();

        [System.Serializable]
        public class GlyphAssignment
        {
            public string Label;
            public int GlyphIndex;
        }

        //<summary>
        // Glyph ranges, for numeric and DateTime glyph columns: every row whose value lies in
        // [min, max] of a range draws with that range's glyph. Kept sorted by min and never
        // overlapping; two ranges may share one edge value, which then belongs to the lower range.
        // Each assignable shape (every k_GlyphShapeNames entry except the default Circle) is used
        // by at most one range, so there are at most MaxGlyphRanges. A per-value GlyphAssignment
        // overrides a range for that exact value; rows outside every range, or with no value,
        // keep the default glyph. Cleared when the glyph column changes or a dataset loads.
        // Set only through RequestSetGlyphRangesRpc, which validates the whole list.
        //</summary>
        public List<GlyphRange> GlyphRanges = new();
        public const int MaxGlyphRanges = 5;

        [System.Serializable]
        public class GlyphRange
        {
            public int glyphIndex;
            public float min;
            public float max;
        }

        [Header("Loaded Dataset")]
        public DatasetColumnar LoadedDataset;

        /// <summary>
        /// One shuffle's worth of field selection (column index per shuffle-lockable field, -1 =
        /// none), saved so "back"/"forward" can re-apply a previous combination instead of
        /// generating a new random one.
        /// </summary>
        private struct ShuffleState
        {
            public int X;
            public int Y;
            public int Z;
            public int Size;
            public int Colour;
            public int Glyph;
            public int Time;

            public bool SameAs(ShuffleState other) =>
                X == other.X && Y == other.Y && Z == other.Z && Size == other.Size &&
                Colour == other.Colour && Glyph == other.Glyph && Time == other.Time;
        }

        /// <summary>
        /// Per-dataset shuffle history + position, so navigating back/forward
        /// through past shuffles works independently for each dataset and
        /// survives switching between datasets within the same session.
        /// In-memory only - intentionally not persisted to disk, per-session
        /// is the current scope.
        /// </summary>
        private class ShuffleHistory
        {
            public List<ShuffleState> States = new();
            public int CurrentIndex = -1; // -1 = no shuffle applied yet for this dataset
        }

        private readonly Dictionary<string, ShuffleHistory> m_ShuffleHistoryByDataset = new();

        private ShuffleHistory GetOrCreateShuffleHistory()
        {
            if (LoadedDataset == null)
                return null;

            string key = LoadedDataset.Name;

            if (!m_ShuffleHistoryByDataset.TryGetValue(key, out ShuffleHistory history))
            {
                history = new ShuffleHistory();
                m_ShuffleHistoryByDataset[key] = history;
            }

            return history;
        }

        private void ApplyShuffleState(ShuffleState state)
        {
            ApplyShuffleFields(state);
            OnPlotSettingsChanged?.Invoke();
        }

        // Writes every field of a ShuffleState onto the plot settings without raising events.
        // A changed Time column restarts the scrub and stops playback, like RequestTimeColumnRpc.
        private void ApplyShuffleFields(ShuffleState state)
        {
            bool timeChanged = TimeColumnIndex != state.Time;

            XColumnIndex = state.X;
            YColumnIndex = state.Y;
            ZColumnIndex = state.Z;
            SizeColumnIndex = state.Size;
            ColorColumnIndex = state.Colour;
            if (GlyphColumnIndex != state.Glyph)
                GlyphRanges.Clear(); // ranges are values of the old glyph column
            GlyphColumnIndex = state.Glyph;
            TimeColumnIndex = state.Time;

            if (timeChanged)
            {
                TimeScrub = 0f;
                IsPlaying = false;
            }
        }

        private ShuffleState CaptureShuffleState() => new ShuffleState
        {
            X = XColumnIndex,
            Y = YColumnIndex,
            Z = ZColumnIndex,
            Size = SizeColumnIndex,
            Colour = ColorColumnIndex,
            Glyph = GlyphColumnIndex,
            Time = TimeColumnIndex
        };

        private static int GetShuffleField(ShuffleState state, MappingTarget target)
        {
            switch (target)
            {
                case MappingTarget.X: return state.X;
                case MappingTarget.Y: return state.Y;
                case MappingTarget.Z: return state.Z;
                case MappingTarget.Size: return state.Size;
                case MappingTarget.Colour: return state.Colour;
                case MappingTarget.Glyph: return state.Glyph;
                case MappingTarget.Time: return state.Time;
                default: return -1;
            }
        }

        private static void SetShuffleField(ref ShuffleState state, MappingTarget target, int columnIndex)
        {
            switch (target)
            {
                case MappingTarget.X: state.X = columnIndex; break;
                case MappingTarget.Y: state.Y = columnIndex; break;
                case MappingTarget.Z: state.Z = columnIndex; break;
                case MappingTarget.Size: state.Size = columnIndex; break;
                case MappingTarget.Colour: state.Colour = columnIndex; break;
                case MappingTarget.Glyph: state.Glyph = columnIndex; break;
                case MappingTarget.Time: state.Time = columnIndex; break;
            }
        }

        public event Action OnDatasetLoaded;
        public event Action OnPlotSettingsChanged;

        /// <summary>
        /// Raised when the bookmarked columns change: a bookmark added/removed, or a dataset
        /// loaded (each dataset has its own bookmarks). Read GetBookmarkedColumns() for the list.
        /// </summary>
        public event Action OnBookmarksChanged;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
                return;
            }

            LoadShuffleLockPrefs();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            Debug.Log($"[MultiplayerScatterplotManager] OnDestroy called, clearing Instance {GetEntityId()}");
        }

        private void Start()
        {
            // Launched from the Start scene (import wizard or a default-on-load dataset): load that
            // dataset, with the wizard's first X/Y/Z (FirstAxisMapping) set before the plot builds.
            if (!string.IsNullOrEmpty(LaunchContext.DatasetName))
            {
                if (LaunchContext.CameFromWizard)
                {
                    LaunchContext.CameFromWizard = false; // one-shot: later loads behave as before
                    int[] axes = FirstAxisMapping.ForDatasetFile(LaunchContext.DatasetName);
                    if (axes != null)
                    {
                        XColumnIndex = axes[0];
                        YColumnIndex = axes[1];
                        ZColumnIndex = axes[2];
                    }
                }

                LoadLocalDataset(LaunchContext.DatasetName);
                return;
            }

            // Check for columnar processed datasets first
            List<string> columnarFiles = DatasetStore.Files();
            if (columnarFiles.Count > 0)
            {
                CurrentDatasetName = columnarFiles[0];
                LoadLocalDataset(CurrentDatasetName);
                return;
            }

            // Fallback to CSV files in DataCSV
            string csvDataPath = Path.Combine(Application.streamingAssetsPath, "DataCSV");

            if (Directory.Exists(csvDataPath))
            {
                string[] csvFiles = Directory.GetFiles(csvDataPath, "*.csv");

                if (csvFiles.Length > 0)
                {
                    string csvFileName = Path.GetFileName(csvFiles[0]);
                    CurrentDatasetName = csvFileName.Replace(".csv", ".cdataset");
                    LoadLocalDataset(CurrentDatasetName);
                    return;
                }
            }

            // Final fallback to regular streaming assets
            string[] streamingCsvFiles = DatasetStore.IsPacked ? new string[0] : Directory.GetFiles(Application.streamingAssetsPath, "*.csv");

            if (streamingCsvFiles.Length > 0)
            {
                CurrentDatasetName = Path.GetFileName(streamingCsvFiles[0]).Replace(".csv", ".cdataset");
                LoadLocalDataset(CurrentDatasetName);
            }
            else
            {
                Debug.LogError("No dataset files found in any expected directory.");
            }
        }

        public void LoadLocalDataset(string fileName)
        {
            // Try to load as columnar dataset first
            if (!fileName.EndsWith(".cdataset"))
            {
                fileName = fileName.Replace(".csv", ".cdataset").Replace(".dataset", ".cdataset");
            }

            LoadedDataset = ColumnarBinaryImporter.Load(fileName);

            if (LoadedDataset != null)
            {
                CurrentDatasetName = fileName;

                Debug.Log(
                    $"Loaded columnar dataset '{LoadedDataset.Name}' " +
                    $"({LoadedDataset.RowCount} rows, {LoadedDataset.ColumnCount} columns)"
                );

                OnDatasetLoaded?.Invoke();
                OnBookmarksChanged?.Invoke();
                OnPlotSettingsChanged?.Invoke();
            }
            else
            {
                Debug.LogError(
                    $"[MultiplayerScatterplotManager] FALLING BACK TO SLOW CSV IMPORT\n" +
                    $"Columnar binary dataset failed to load: '{fileName}'\n" +
                    $"This dataset will be parsed via the legacy row-based CSVImporter, " +
                    $"which is significantly slower and was NOT the intended fast path.\n" +
                    $"Check that a matching .cdataset file exists in " +
                    $"Assets/StreamingAssets/DataCSV/ProcessedData/ (run preprocess_csv_columnar.py if not)."
                );

                // Fallback to CSV importer
                string csvFileName = fileName.Replace(".cdataset", ".csv");
                string csvPath = Path.Combine(Application.streamingAssetsPath, "DataCSV", csvFileName);
                Dataset legacyDataset = CSVImporter.Load(csvPath);

                if (legacyDataset != null)
                {
                    // Convert to columnar format (simple wrapper for now)
                    LoadedDataset = ConvertToColumnar(legacyDataset);
                    CurrentDatasetName = csvFileName;

                    Debug.Log(
                        $"Loaded legacy CSV dataset as columnar '{LoadedDataset.Name}' " +
                        $"({LoadedDataset.RowCount} rows, {LoadedDataset.ColumnCount} columns)"
                    );

                    OnDatasetLoaded?.Invoke();
                    OnBookmarksChanged?.Invoke();
                    OnPlotSettingsChanged?.Invoke();
                }
                else
                {
                    Debug.LogError($"Failed to load dataset: {fileName}");
                }
            }
        }

        private DatasetColumnar ConvertToColumnar(Dataset legacyDataset)
        {
            // Simple conversion from legacy Dataset to DatasetColumnar
            // This is a temporary compatibility layer
            DatasetColumnar columnar = new DatasetColumnar(legacyDataset.Name, legacyDataset.RowCount, legacyDataset.ColumnCount);

            // Copy column metadata
            foreach (var legacyColumn in legacyDataset.Columns)
            {
                columnar.Columns.Add(legacyColumn);
                columnar.ColumnMapping[legacyColumn.Name] = columnar.Columns.Count - 1;
            }

            // Extract column data from rows
            for (int colIndex = 0; colIndex < legacyDataset.ColumnCount; colIndex++)
            {
                DatasetColumn column = legacyDataset.Columns[colIndex];

                if (column.IsNumeric)
                {
                    float[] numericData = new float[legacyDataset.RowCount];
                    for (int rowIndex = 0; rowIndex < legacyDataset.RowCount; rowIndex++)
                    {
                        numericData[rowIndex] = legacyDataset.Rows[rowIndex].GetNumericValue(colIndex);
                    }
                    columnar.SetNumericColumn(colIndex, numericData);
                }
                else if (column.IsCategorical)
                {
                    // Build category table
                    string[] categoryTable = new string[column.UniqueCount];
                    int idx = 0;
                    foreach (string cat in column.UniqueValues)
                    {
                        categoryTable[idx++] = cat;
                    }

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

        public void RequestLoadDatasetRpc(string fileName)
        {
            if (ForwardToHost(PlotOp.LoadDataset, text: fileName)) return;

            LoadLocalDataset(fileName);

            if (LoadedDataset != null)
            {
                XColumnIndex = 0;
                YColumnIndex = Mathf.Clamp(
                    1,
                    0,
                    LoadedDataset.ColumnCount - 1
                );

                ZColumnIndex = Mathf.Clamp(
                    2,
                    0,
                    LoadedDataset.ColumnCount - 1
                );

                ColorColumnIndex = -1;
                TimeColumnIndex = -1;
                TimeScrub = 0f;
                IsPlaying = false;
                GlyphColumnIndex = -1;
                GlyphAssignments.Clear();
                GlyphRanges.Clear();
                // Filter/size column indices from the previous dataset would point at unrelated
                // columns here (and filtering is active for any entry), so reset them.
                Filters.Clear();
                SizeColumnIndex = -1;

                OnPlotSettingsChanged?.Invoke();
            }
        }

        public void RequestXColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.XColumn, columnIndex)) return;

            XColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestYColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.YColumn, columnIndex)) return;

            YColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestZColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.ZColumn, columnIndex)) return;

            ZColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestColorColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.ColourColumn, columnIndex)) return;

            ColorColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestPointSizeRpc(float size)
        {
            if (ForwardToHost(PlotOp.PointSize, floatArg: size)) return;

            PointSize = Mathf.Clamp(size, 0f, MaxPointSize);
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestTimeColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.TimeColumn, columnIndex)) return;

            TimeColumnIndex = columnIndex;
            IsPlaying = false; // stop any running playback when the column changes
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestTimeScrubRpc(float normalizedValue)
        {
            if (ForwardToHost(PlotOp.TimeScrub, floatArg: normalizedValue)) return;

            TimeScrub = Mathf.Clamp01(normalizedValue);
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestPlaybackToggleRpc(bool isPlaying)
        {
            if (ForwardToHost(PlotOp.Playback, boolArg: isPlaying)) return;

            IsPlaying = isPlaying;

            // If pressing play after having reached the end, loop back to the start
            if (IsPlaying && TimeScrub >= 1f)
            {
                TimeScrub = 0f;
            }

            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestTooltipsToggleRpc(bool showTooltips)
        {
            ShowTooltips = showTooltips;
            // Usually, we would refresh the toggle when switching datasets, but user testing found this to be annoying, so we don't refresh the toggle anymore.
            //Code stays commented out for posterity, in case we want to re-enable this behavior in the future.
            //OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Dropdown path: sets the column of the FIRST filter entry (creating it if there is none).
        /// columnIndex &lt; 0 ("None") removes the first entry; any further entries stay active.
        /// </summary>
        public void RequestFilterIndexRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.FilterColumn, columnIndex)) return;

            if (columnIndex < 0)
            {
                if (Filters.Count > 0)
                    Filters.RemoveAt(0);
            }
            else if (Filters.Count == 0)
            {
                Filters.Add(new FilterEntry { ColumnIndex = columnIndex, Label = "" });
            }
            else if (Filters[0].ColumnIndex != columnIndex)
            {
                // A value picked for the old column means nothing for the new one:
                // fall back to "Any value" (empty Label).
                Filters[0].ColumnIndex = columnIndex;
                Filters[0].Label = "";
                Filters[0].Ranges.Clear(); // ranges are in the old column's units
            }

            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>Dropdown path: sets the value of the FIRST filter entry (no-op without one).</summary>
        public void RequestFilterLabelRpc(string filterLabel)
        {
            if (ForwardToHost(PlotOp.FilterLabel, text: filterLabel)) return;

            if (Filters.Count == 0)
                return;

            Filters[0].Label = filterLabel ?? "";
            Filters[0].Ranges.Clear(); // a picked value replaces any ranges
            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Palette path: appends a filter entry for a column (OR-combined with the others).
        /// An identical entry (same column and value) is not added twice. Returns true when added.
        /// </summary>
        public bool RequestAddFilterColumnRpc(int columnIndex, string filterLabel = "")
        {
            if (LoadedDataset == null || columnIndex < 0 || columnIndex >= LoadedDataset.ColumnCount)
                return false;

            filterLabel ??= "";
            if (Filters.Exists(f => f.ColumnIndex == columnIndex && f.Label == filterLabel && f.Ranges.Count == 0))
                return false;

            if (ForwardToHost(PlotOp.AddFilter, columnIndex, text: filterLabel)) return true;

            Filters.Add(new FilterEntry { ColumnIndex = columnIndex, Label = filterLabel });
            OnPlotSettingsChanged?.Invoke();
            return true;
        }

        public void RequestRemoveFilterRpc(int filterIndex)
        {
            if (ForwardToHost(PlotOp.RemoveFilter, filterIndex)) return;

            if (filterIndex < 0 || filterIndex >= Filters.Count)
                return;

            Filters.RemoveAt(filterIndex);
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestClearFiltersRpc()
        {
            if (ForwardToHost(PlotOp.ClearFilters)) return;

            Filters.Clear();
            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Range editor path: sets the value ranges of the FIRST filter entry (the Filter Column
        /// dropdown's), which must be on a numeric or DateTime column. A row then passes that entry
        /// when its value lies in any range (both ends inclusive); overlapping ranges are fine. An
        /// empty list goes back to "Any value". Returns whether the ranges were applied.
        /// </summary>
        public bool RequestSetFilterRangesRpc(List<FilterRange> ranges)
        {
            if (!ValidateFilterRanges(ranges, out string error))
            {
                Debug.LogWarning($"[MultiplayerScatterplotManager] Filter ranges rejected: {error}");
                return false;
            }

            if (NetworkScatterplotState.TryForwardToHost(PlotRequest.ForFilterRanges(ranges))) return true;

            DatasetColumn column = Filters.Count > 0 ? LoadedDataset?.GetColumn(Filters[0].ColumnIndex) : null;
            if (column == null || !column.IsNumeric)
            {
                Debug.LogWarning("[MultiplayerScatterplotManager] Filter ranges need a numeric or DateTime filter column.");
                return false;
            }

            FilterEntry entry = Filters[0];
            entry.Label = "";
            entry.Ranges = new List<FilterRange>();
            if (ranges != null)
            {
                foreach (FilterRange range in ranges)
                    entry.Ranges.Add(new FilterRange { min = range.min, max = range.max });
            }
            entry.Ranges.Sort((a, b) => a.min != b.min ? a.min.CompareTo(b.min) : a.max.CompareTo(b.max));

            OnPlotSettingsChanged?.Invoke();
            return true;
        }

        /// <summary>At most MaxFilterRanges ranges, each with finite bounds and min &lt;= max.</summary>
        public static bool ValidateFilterRanges(List<FilterRange> ranges, out string error)
        {
            error = null;
            if (ranges == null)
                return true;
            if (ranges.Count > MaxFilterRanges)
            {
                error = $"{ranges.Count} ranges, at most {MaxFilterRanges} allowed";
                return false;
            }
            for (int i = 0; i < ranges.Count; i++)
            {
                FilterRange r = ranges[i];
                if (r == null || !float.IsFinite(r.min) || !float.IsFinite(r.max))
                {
                    error = $"range {i} is missing or has a non-finite bound";
                    return false;
                }
                if (r.min > r.max)
                {
                    error = $"range {i} has min {r.min} above max {r.max}";
                    return false;
                }
            }
            return true;
        }

        public void RequestSizeColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.SizeColumn, columnIndex)) return;

            SizeColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestSizeMappedRpc(bool mapped)
        {
            if (ForwardToHost(PlotOp.SizeMapped, boolArg: mapped)) return;

            SizeMapped = mapped;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestGlyphColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.GlyphColumn, columnIndex)) return;

            // Ranges are values of the old glyph column: a new label starts with none.
            if (columnIndex != GlyphColumnIndex)
                GlyphRanges.Clear();

            GlyphColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Manually assigns (or re-assigns) a glyph to a specific label value on the
        /// active glyph column, e.g. RequestGlyphAssignmentRpc("Female", 1).
        /// </summary>
        public void RequestGlyphAssignmentRpc(string label, int glyphIndex)
        {
            if (string.IsNullOrEmpty(label))
                return;

            if (ForwardToHost(PlotOp.GlyphAssignment, glyphIndex, text: label)) return;

            GlyphAssignment existing = GlyphAssignments.Find(a => a.Label == label);
            if (existing != null)
            {
                existing.GlyphIndex = glyphIndex;
            }
            else
            {
                GlyphAssignments.Add(new GlyphAssignment { Label = label, GlyphIndex = glyphIndex });
            }

            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Looks up the manually-assigned glyph for a label, or defaultGlyph if the
        /// label has no assignment yet (e.g. never touched by a user this session).
        /// </summary>
        public int GetGlyphForLabel(string label, int defaultGlyph = 0)
        {
            if (string.IsNullOrEmpty(label))
                return defaultGlyph;

            GlyphAssignment match = GlyphAssignments.Find(a => a.Label == label);
            return match != null ? match.GlyphIndex : defaultGlyph;
        }

        /// <summary>
        /// Glyph for one row of the glyph column: its value's own assignment if it has one,
        /// otherwise the glyph of the range its numeric value falls in (pass NaN for a
        /// categorical column), otherwise defaultGlyph. Rows with no value (hasValue false)
        /// always get defaultGlyph.
        /// </summary>
        public int GetGlyphForRow(string label, bool hasValue, float numericValue, int defaultGlyph = 0)
        {
            if (!hasValue)
                return defaultGlyph;

            GlyphAssignment match = string.IsNullOrEmpty(label)
                ? null
                : GlyphAssignments.Find(a => a.Label == label);
            if (match != null)
                return match.GlyphIndex;

            int rangeIndex = GetGlyphRangeIndex(numericValue);
            return rangeIndex >= 0 ? GlyphRanges[rangeIndex].glyphIndex : defaultGlyph;
        }

        /// <summary>
        /// Index in GlyphRanges of the range containing value (min and max inclusive), or -1.
        /// GlyphRanges is sorted by min, so a value on an edge two ranges share resolves to
        /// the lower range.
        /// </summary>
        public int GetGlyphRangeIndex(float value)
        {
            if (float.IsNaN(value))
                return -1;

            for (int i = 0; i < GlyphRanges.Count; i++)
            {
                if (value >= GlyphRanges[i].min && value <= GlyphRanges[i].max)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Replaces every glyph range with ranges (null or empty clears them). Rejected, with
        /// nothing changed, when ValidateGlyphRanges fails: overlapping ranges, a shape used
        /// twice, the default Circle or an unknown shape, min > max, or more than
        /// MaxGlyphRanges ranges. Returns whether the ranges were applied.
        /// </summary>
        public bool RequestSetGlyphRangesRpc(List<GlyphRange> ranges)
        {
            if (!ValidateGlyphRanges(ranges, out string error))
            {
                Debug.LogWarning($"[MultiplayerScatterplotManager] Glyph ranges rejected: {error}");
                return false;
            }

            if (NetworkScatterplotState.TryForwardToHost(PlotRequest.ForGlyphRanges(ranges))) return true;

            GlyphRanges = new List<GlyphRange>();
            if (ranges != null)
            {
                foreach (GlyphRange range in ranges)
                    GlyphRanges.Add(new GlyphRange { glyphIndex = range.glyphIndex, min = range.min, max = range.max });
            }
            GlyphRanges.Sort(CompareGlyphRanges);

            OnPlotSettingsChanged?.Invoke();
            return true;
        }

        /// <summary>Orders ranges by min, then max.</summary>
        public static int CompareGlyphRanges(GlyphRange a, GlyphRange b)
        {
            int byMin = a.min.CompareTo(b.min);
            return byMin != 0 ? byMin : a.max.CompareTo(b.max);
        }

        /// <summary>
        /// True when two ranges share more than one edge value. Ranges that only touch
        /// (a.max == b.min) do not overlap; two identical ranges always do.
        /// </summary>
        public static bool GlyphRangesOverlap(GlyphRange a, GlyphRange b)
        {
            if (a.min == b.min && a.max == b.max)
                return true;

            return a.min < b.max && b.min < a.max;
        }

        /// <summary>Checks a full list of glyph ranges against the rules on GlyphRanges.</summary>
        public static bool ValidateGlyphRanges(List<GlyphRange> ranges, out string error)
        {
            error = null;
            if (ranges == null || ranges.Count == 0)
                return true;

            if (ranges.Count > MaxGlyphRanges)
            {
                error = $"{ranges.Count} ranges, at most {MaxGlyphRanges} allowed";
                return false;
            }

            HashSet<int> usedGlyphs = new HashSet<int>();
            for (int i = 0; i < ranges.Count; i++)
            {
                GlyphRange range = ranges[i];
                if (range == null)
                {
                    error = $"range {i} is null";
                    return false;
                }
                if (range.glyphIndex < 1 || range.glyphIndex >= ScatterplotUI.k_GlyphShapeNames.Length)
                {
                    error = $"range {i} uses glyph {range.glyphIndex}, which cannot be assigned to a range";
                    return false;
                }
                if (float.IsNaN(range.min) || float.IsNaN(range.max) || float.IsInfinity(range.min) || float.IsInfinity(range.max))
                {
                    error = $"range {i} has a non-finite bound";
                    return false;
                }
                if (range.min > range.max)
                {
                    error = $"range {i} has min {range.min} above max {range.max}";
                    return false;
                }
                if (!usedGlyphs.Add(range.glyphIndex))
                {
                    error = $"glyph {ScatterplotUI.k_GlyphShapeNames[range.glyphIndex]} is used by more than one range";
                    return false;
                }
            }

            for (int i = 0; i < ranges.Count; i++)
            {
                for (int j = i + 1; j < ranges.Count; j++)
                {
                    if (GlyphRangesOverlap(ranges[i], ranges[j]))
                    {
                        error = $"ranges {i} and {j} overlap";
                        return false;
                    }
                }
            }

            return true;
        }


        /// <summary>
        /// Adds or removes a column from the shuffle blacklist by name. Does
        /// NOT affect manual selection via a dropdown - a blacklisted column
        /// remains fully choosable by hand, it's only skipped by the random
        /// shuffle picker.
        /// </summary>
        public void RequestSetColumnBlacklistedRpc(string columnName, bool blacklisted)
        {
            if (string.IsNullOrEmpty(columnName))
                return;

            HashSet<string> blacklist = GetBlacklistForCurrentDataset();

            if (blacklisted)
            {
                blacklist.Add(columnName);
            }
            else
            {
                blacklist.Remove(columnName);
            }

            if (LoadedDataset != null)
                ColumnPrefsStore.SetBlacklist(LoadedDataset.Name, blacklist);
            OnPlotSettingsChanged?.Invoke();
        }

        public bool IsColumnBlacklisted(string columnName)
        {
            if (string.IsNullOrEmpty(columnName))
                return false;

            return GetBlacklistForCurrentDataset().Contains(columnName);
        }

        /// <summary>
        /// Adds or removes a column bookmark by name (per-dataset). Bookmarks are persisted
        /// separately to disk so they survive restarts.
        /// </summary>
        public void RequestSetColumnBookmarkedRpc(string columnName, bool bookmarked)
        {
            if (string.IsNullOrEmpty(columnName))
                return;

            List<string> bookmarks = GetBookmarksForCurrentDataset();

            if (bookmarked)
            {
                if (!bookmarks.Contains(columnName))
                    bookmarks.Add(columnName); // appended: palette order = bookmark order
            }
            else
            {
                bookmarks.Remove(columnName);
            }

            if (LoadedDataset != null)
                ColumnPrefsStore.SetBookmarks(LoadedDataset.Name, bookmarks);
            OnBookmarksChanged?.Invoke();
            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// The current dataset's bookmarked column names, in bookmark order. Only names that
        /// exist in the loaded dataset are returned (a bookmark file can outlive a column).
        /// </summary>
        public List<string> GetBookmarkedColumns()
        {
            List<string> result = new List<string>();
            if (LoadedDataset == null)
                return result;

            foreach (string columnName in GetBookmarksForCurrentDataset())
            {
                if (LoadedDataset.ColumnMapping.ContainsKey(columnName))
                    result.Add(columnName);
            }

            return result;
        }

        public bool IsColumnBookmarked(string columnName)
        {
            if (string.IsNullOrEmpty(columnName))
                return false;

            return GetBookmarksForCurrentDataset().Contains(columnName);
        }

        /// <summary>
        /// Call after bookmarks or the blacklist were changed through ColumnPrefsStore from outside
        /// (the import wizard run in place), so the palette and menus refresh.
        /// </summary>
        public void NotifyColumnPrefsChanged()
        {
            OnBookmarksChanged?.Invoke();
            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Shuffle: gives every UNLOCKED field (see IsFieldLocked) a new random label, walking the
        /// fields in the fixed order ShuffleLocks.ShuffleOrder (X, Y, Z, Size, Colour, Glyph, Time).
        /// Locked fields keep their label. Rules:
        ///   - a field only draws labels it accepts (ColumnMappingRules.Accepts): X/Y/Z/Size/Time
        ///     numeric or DateTime, Colour any label, Glyph categorical. Size only while Mapped.
        ///   - X, Y, Z and Size never share a label (locked ones are reserved first). Colour and
        ///     Glyph may repeat an axis label.
        ///   - this dataset's blacklisted labels are skipped for every field.
        ///   - a field with no valid label left keeps its current one; the others still shuffle.
        /// X/Y/Z/Size picks stay weighted by value range (MaxValue - MinValue): in high-dimensional
        /// data that mixes a few meaningful columns with many near-constant ones, a wider range
        /// tends to mean "carries real structure". Colour, Glyph and Time pick uniformly.
        /// Each press is recorded in this dataset's shuffle history (all seven fields), preceded by
        /// the settings it replaced when those were not already the current entry, so << always
        /// returns to what was shown before the press.
        /// </summary>
        public void RequestShuffleColumnsRpc()
        {
            if (NetworkScatterplotState.TryForwardToHost(PlotRequest.ForShuffle(LockedFieldMask(), GetBlacklistForCurrentDataset()))) return;

            if (LoadedDataset == null || LoadedDataset.ColumnCount == 0)
                return;

            int columnCount = LoadedDataset.ColumnCount;

            // Only this dataset's own blacklist entries apply.
            HashSet<int> blacklisted = new HashSet<int>();
            foreach (string blacklistedName in m_ShuffleBlacklistOverride ?? GetBlacklistForCurrentDataset())
            {
                if (LoadedDataset.ColumnMapping.TryGetValue(blacklistedName, out int blacklistedIdx))
                    blacklisted.Add(blacklistedIdx);
            }

            ShuffleState before = CaptureShuffleState();
            ShuffleState next = before;

            // X, Y, Z and Size share one pool of labels: a label one of them holds is off-limits to
            // the others. Locked fields keep theirs, so those are reserved before anything is picked.
            // Size's label only counts while Size is Mapped (it isn't shown otherwise).
            HashSet<int> axisLabelsInUse = new HashSet<int>();
            // Current labels of the unlocked X/Y/Z/Size fields: avoided while there is an
            // alternative, so an earlier field doesn't take the label a later one may fall back on.
            HashSet<int> unlockedAxisLabels = new HashSet<int>();
            foreach (MappingTarget target in ShuffleLocks.ShuffleOrder)
            {
                if (!ShuffleLocks.IsAxisLike(target))
                    continue;

                int current = GetShuffleField(before, target);
                if (current < 0 || (target == MappingTarget.Size && !SizeMapped))
                    continue;

                if (IsShuffleFieldLocked(target))
                    axisLabelsInUse.Add(current);
                else
                    unlockedAxisLabels.Add(current);
            }

            List<int> candidates = new List<int>();
            List<int> preferred = new List<int>();

            foreach (MappingTarget target in ShuffleLocks.ShuffleOrder)
            {
                if (IsShuffleFieldLocked(target))
                    continue;

                bool axisLike = ShuffleLocks.IsAxisLike(target);
                int current = GetShuffleField(before, target);

                candidates.Clear();
                preferred.Clear();
                for (int i = 0; i < columnCount; i++)
                {
                    if (i == current || blacklisted.Contains(i))
                        continue;
                    if (axisLike && axisLabelsInUse.Contains(i))
                        continue;
                    if (!ColumnMappingRules.Accepts(target, LoadedDataset.GetColumn(i), this))
                        continue;

                    candidates.Add(i);
                    if (!axisLike || !unlockedAxisLabels.Contains(i))
                        preferred.Add(i);
                }

                List<int> pool = preferred.Count > 0 ? preferred : candidates;
                int picked = current; // no valid label left: keep the current one
                if (pool.Count > 0)
                    picked = axisLike ? WeightedRandomPick(pool) : pool[UnityEngine.Random.Range(0, pool.Count)];

                if (axisLike)
                {
                    unlockedAxisLabels.Remove(current);
                    if (picked >= 0)
                        axisLabelsInUse.Add(picked);
                }

                SetShuffleField(ref next, target, picked);
            }

            if (next.SameAs(before))
            {
                // Everything locked (or nothing valid to pick): nothing changes, nothing recorded.
                Debug.Log("[MultiplayerScatterplotManager] Shuffle: no unlocked field could change.");
                return;
            }

            ApplyShuffleFields(next);

            // Save into this dataset's shuffle history. If the user had navigated back and is now
            // shuffling again, this is a new branch - discard the old "forward" states, same as
            // browser back/forward or standard undo/redo semantics. The settings this press
            // replaced are recorded first unless they already are the current entry (e.g. the
            // first shuffle, or after the user changed a mapping by hand).
            ShuffleHistory history = GetOrCreateShuffleHistory();
            if (history != null)
            {
                if (history.CurrentIndex < history.States.Count - 1)
                {
                    history.States.RemoveRange(history.CurrentIndex + 1, history.States.Count - history.CurrentIndex - 1);
                }

                if (history.CurrentIndex < 0 || !history.States[history.CurrentIndex].SameAs(before))
                    history.States.Add(before);

                history.States.Add(next);
                history.CurrentIndex = history.States.Count - 1;
            }

            Debug.Log(
                $"[MultiplayerScatterplotManager] Shuffled -> " +
                $"X={ColumnNameOrNone(XColumnIndex)}, " +
                $"Y={ColumnNameOrNone(YColumnIndex)}, " +
                $"Z={ColumnNameOrNone(ZColumnIndex)}, " +
                $"Size={ColumnNameOrNone(SizeColumnIndex)}, " +
                $"Color={ColumnNameOrNone(ColorColumnIndex)}, " +
                $"Glyph={ColumnNameOrNone(GlyphColumnIndex)}, " +
                $"Time={ColumnNameOrNone(TimeColumnIndex)}"
            );

            OnPlotSettingsChanged?.Invoke();
        }

        private string ColumnNameOrNone(int columnIndex) =>
            columnIndex >= 0 && LoadedDataset != null ? LoadedDataset.GetColumn(columnIndex)?.Name ?? "None" : "None";

        /// <summary>
        /// Steps back to the previous shuffle result for the current dataset,
        /// if one exists. Does nothing (no-op, logged) if already at the
        /// oldest saved shuffle or if this dataset has never been shuffled.
        /// </summary>
        public void RequestShuffleBackwardRpc()
        {
            if (ForwardToHost(PlotOp.ShuffleBackward)) return;

            ShuffleHistory history = GetOrCreateShuffleHistory();

            if (history == null || history.CurrentIndex <= 0)
            {
                Debug.Log("[MultiplayerScatterplotManager] ShuffleBackward: nothing further back to go to.");
                return;
            }

            history.CurrentIndex--;
            ApplyShuffleState(history.States[history.CurrentIndex]);
        }

        /// <summary>
        /// Steps forward to a more recent shuffle result for the current
        /// dataset, if the user had previously navigated backward. Does
        /// nothing if already at the newest saved shuffle.
        /// </summary>
        public void RequestShuffleForwardRpc()
        {
            if (ForwardToHost(PlotOp.ShuffleForward)) return;

            ShuffleHistory history = GetOrCreateShuffleHistory();

            if (history == null || history.CurrentIndex >= history.States.Count - 1)
            {
                Debug.Log("[MultiplayerScatterplotManager] ShuffleForward: nothing further forward to go to.");
                return;
            }

            history.CurrentIndex++;
            ApplyShuffleState(history.States[history.CurrentIndex]);
        }

        /// <summary>
        /// Picks one column index from pool, weighted by ColumnShuffleWeight. Falls back to a
        /// uniform pick if every weight is zero. pool must not be empty.
        /// </summary>
        private int WeightedRandomPick(List<int> pool)
        {
            float totalWeight = 0f;
            foreach (int columnIndex in pool)
                totalWeight += ColumnShuffleWeight(columnIndex);

            if (totalWeight <= 0f)
                return pool[UnityEngine.Random.Range(0, pool.Count)];

            float roll = UnityEngine.Random.Range(0f, totalWeight);
            float cumulative = 0f;

            foreach (int columnIndex in pool)
            {
                cumulative += ColumnShuffleWeight(columnIndex);
                if (roll <= cumulative)
                    return columnIndex;
            }

            return pool[pool.Count - 1];
        }

        // Numeric (incl. DateTime) columns weigh their value range, so wide-range columns are
        // picked more often than near-constant ones; anything else gets a flat weight.
        private float ColumnShuffleWeight(int columnIndex)
        {
            DatasetColumn col = LoadedDataset?.GetColumn(columnIndex);

            if (col == null)
                return 0f;

            if (col.IsNumeric)
                return Mathf.Max(0.0001f, col.MaxValue - col.MinValue);

            return 1f;
        }

        // ---- Multiplayer -------------------------------------------------------------------------
        // Every shared Request...Rpc starts with a ForwardToHost guard: on a connected non-host
        // client the change is sent to the host (NetworkScatterplotState), which applies it and
        // replicates the result to everyone, so the method returns without applying it locally.
        // Offline and on the host the guard is false and the method behaves exactly as before.
        // Local-only state (bookmarks, blacklist, shuffle locks, tooltips) is never forwarded.

        private static bool ForwardToHost(PlotOp op, int intArg = 0, float floatArg = 0f, bool boolArg = false, string text = null) =>
            NetworkScatterplotState.TryForwardToHost(new PlotRequest(op, intArg, floatArg, boolArg, text));

        /// <summary>
        /// Client side: shows the host's shared settings without forwarding anything, then raises
        /// OnPlotSettingsChanged like a local change. The caller loads the host's dataset first.
        /// </summary>
        public void ApplyNetworkSnapshot(PlotSnapshot snapshot, float timeScrub)
        {
            snapshot.WriteTo(this, timeScrub);
            OnPlotSettingsChanged?.Invoke();
        }

        // Set only while the host runs a shuffle a client asked for: that client's shuffle locks
        // (one bit per MappingTarget) and blacklist replace the host's own.
        private int? m_ShuffleLockMaskOverride;
        private IReadOnlyCollection<string> m_ShuffleBlacklistOverride;

        /// <summary>Host side: RequestShuffleColumnsRpc with a client's shuffle locks and blacklist.</summary>
        public void RequestShuffleColumnsForClient(int lockedFieldMask, IReadOnlyCollection<string> blacklist)
        {
            m_ShuffleLockMaskOverride = lockedFieldMask;
            m_ShuffleBlacklistOverride = blacklist;
            try
            {
                RequestShuffleColumnsRpc();
            }
            finally
            {
                m_ShuffleLockMaskOverride = null;
                m_ShuffleBlacklistOverride = null;
            }
        }

        private int LockedFieldMask()
        {
            int mask = 0;
            foreach (MappingTarget target in ShuffleLocks.ShuffleOrder)
            {
                if (IsFieldLocked(target))
                    mask |= 1 << (int)target;
            }

            return mask;
        }

        private bool IsShuffleFieldLocked(MappingTarget target) =>
            m_ShuffleLockMaskOverride is int mask
                ? !CanUnlockField(target) || (mask & (1 << (int)target)) != 0
                : IsFieldLocked(target);

        private void Update()
        {
            if (!IsPlaying || TimeColumnIndex < 0)
                return;

            TimeScrub += PlaybackSpeed * Time.deltaTime;

            if (TimeScrub >= 1f)
            {
                TimeScrub = 1f;
                IsPlaying = false; // stop at the end rather than looping silently
            }

            OnPlotSettingsChanged?.Invoke();
        }

    }
}