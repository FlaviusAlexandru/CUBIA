using System;
using System.Collections.Generic;
using System.IO;
using DataViz.Flow;
using DataViz.Networking;
using DataViz.Wizard;
using UnityEngine;
using UnityEngine.Serialization;

namespace DataViz
{
    // The manager is split into partial files so later work packages own disjoint files:
    //   MultiplayerScatterplotManager.cs         fields, forwarders, the legacy Request…Rpc methods, load, lifecycle
    //   MultiplayerScatterplotManager.Layers.cs  layer stack, edit-layer scope, layer requests, events, bounds, clock
    //   MultiplayerScatterplotManager.Shuffle.cs shuffle locks, blacklist, history and the shuffle itself
    public partial class MultiplayerScatterplotManager : MonoBehaviour
    {
        public static MultiplayerScatterplotManager Instance { get; private set; }

        //<summary>
        // Layers. The plot is a LayerStack (see the .Layers.cs part). The per-layer settings that used
        // to be public fields here (CurrentDatasetName, XColumnIndex, …, Filters, GlyphRanges,
        // LoadedDataset) are now properties that forward to EditLayer: the layer named by an open
        // EditLayerScope, else the active layer. Only playback (TimeScrub, IsPlaying, PlaybackSpeed,
        // PlaybackRate) and ShowTooltips stay plot-level fields.
        // The scene still holds the old field values; the m_Initial… seeds below pick them up through
        // FormerlySerializedAs, and Awake builds the base layer "L1" from them.
        //</summary>

        [Header("Dataset Settings (base layer seed)")]
        [FormerlySerializedAs("CurrentDatasetName")]
        [SerializeField] private string m_InitialDatasetName = "test.csv";

        [Header("Visualization Settings (base layer seed)")]
        [FormerlySerializedAs("XColumnIndex")]
        [SerializeField] private int m_InitialXColumn = 0;
        [FormerlySerializedAs("YColumnIndex")]
        [SerializeField] private int m_InitialYColumn = 1;
        [FormerlySerializedAs("ZColumnIndex")]
        [SerializeField] private int m_InitialZColumn = 2;
        [FormerlySerializedAs("ColorColumnIndex")]
        [SerializeField] private int m_InitialColorColumn = -1;
        [FormerlySerializedAs("PointSize")]
        [SerializeField] private float m_InitialPointSize = 0.1f;
        public const float MaxPointSize = 0.1f; // the point size slider's 100% maps to this

        [Header("Temporal Settings")]
        [FormerlySerializedAs("TimeColumnIndex")]
        [SerializeField] private int m_InitialTimeColumn = -1;
        [Range(0f, 1f)]
        public float TimeScrub = 0f;
        public bool IsPlaying = false;
        public float PlaybackSpeed = 0.25f; // scrub units (0-1) per second; 1x for time columns that aren't seconds-like

        /// <summary>
        /// User-facing playback rate multiplier (shared). 1x is real time for seconds-like time
        /// columns, otherwise PlaybackSpeed; see EffectiveScrubPerSecond.
        /// </summary>
        public float PlaybackRate = 1f;

        /// <summary>The rates offered in the UI (pilot finding P3).</summary>
        public static readonly float[] PlaybackRateOptions = { 0.25f, 0.5f, 1f, 1.5f, 2f };

        public const float MinPlaybackRate = 0.1f;
        public const float MaxPlaybackRate = 4f;

        // Real-time 1x only up to this time range; a longer recording (e.g. days of DateTime data)
        // would barely move, so it falls back to PlaybackSpeed.
        private const float k_MaxRealTimeRangeSeconds = 3600f;

        [Header("Interaction Settings")]
        public bool ShowTooltips = false;

        [Header("Base layer seed: filters, size, glyphs")]
        [FormerlySerializedAs("Filters")]
        [SerializeField] private List<FilterEntry> m_InitialFilters = new();
        [FormerlySerializedAs("SizeMapped")]
        [SerializeField] private bool m_InitialSizeMapped = false;
        [FormerlySerializedAs("SizeColumnIndex")]
        [SerializeField] private int m_InitialSizeColumn = -1;
        [FormerlySerializedAs("GlyphColumnIndex")]
        [SerializeField] private int m_InitialGlyphColumn = -1;
        [FormerlySerializedAs("GlyphAssignments")]
        [SerializeField] private List<GlyphAssignment> m_InitialGlyphAssignments = new();
        [FormerlySerializedAs("GlyphRanges")]
        [SerializeField] private List<GlyphRange> m_InitialGlyphRanges = new();

        // ---- Forwarders: per-layer settings of the edit layer --------------------------------
        // Getters read MappingLayer (the edit layer, or a computed edit layer's source, so menus
        // show something sensible); setters write the edit layer. On an empty plot they act on a
        // private placeholder layer, so nothing throws; every mutating request returns early then.

        /// <summary>Dataset file name of the edit layer; "" when there is none (never null).</summary>
        public string CurrentDatasetName
        {
            get => MappingLayer.DatasetName ?? "";
            set => EditLayer.DatasetName = value ?? "";
        }

        public int XColumnIndex { get => MappingLayer.XColumnIndex; set => EditLayer.XColumnIndex = value; }
        public int YColumnIndex { get => MappingLayer.YColumnIndex; set => EditLayer.YColumnIndex = value; }
        public int ZColumnIndex { get => MappingLayer.ZColumnIndex; set => EditLayer.ZColumnIndex = value; }
        public int ColorColumnIndex { get => MappingLayer.ColorColumnIndex; set => EditLayer.ColorColumnIndex = value; }
        public float PointSize { get => MappingLayer.PointSize; set => EditLayer.PointSize = value; }
        public int TimeColumnIndex { get => MappingLayer.TimeColumnIndex; set => EditLayer.TimeColumnIndex = value; }

        /// <summary>
        /// The edit layer's loaded dataset. Null before the first load, on an empty plot, and on a
        /// layer whose dataset could not be loaded. (Before layers this was a serialised field that
        /// held an empty placeholder until the first load.)
        /// </summary>
        public DatasetColumnar LoadedDataset
        {
            get => MappingLayer.Dataset;
            private set => EditLayer.Dataset = value;
        }

        //<summary>
        // Bookmarks (per dataset, ordered: the bookmark palette shows its blocks in bookmark order)
        // and the shuffle blacklist (per dataset; a blacklisted column stays choosable by hand) are
        // stored by ColumnPrefsStore, which the import wizard writes too. Both getters return
        // copies; changes are saved back through ColumnPrefsStore.SetBookmarks/SetBlacklist. They
        // follow the edit layer's dataset. (The blacklist lives in the .Shuffle.cs part.)
        //</summary>
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
        //dimmed by ScatterplotVisualizer (a Subset layer's filters remove rows instead).
        //FilterColumnIndex/FilterLabel are the FIRST entry, kept for the existing Filter Column /
        //Filter Value dropdowns (which edit that first entry). The bookmark palette's Filter slot
        //appends entries via RequestAddFilterColumnRpc. Per layer: these forward to the edit layer.
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

        public List<FilterEntry> Filters
        {
            get => MappingLayer.Filters;
            set => EditLayer.Filters = value ?? new List<FilterEntry>();
        }

        public int FilterColumnIndex => MappingLayer.FilterColumnIndex;
        public string FilterLabel => MappingLayer.FilterLabel;

        //<summary>
        // Size encoding. Fixed (default, SizeMapped == false): every point uses PointSize, exactly as
        // before. Mapped: each point is scaled by its normalised value in SizeColumnIndex, from
        // SizeMappedMinScale x PointSize (column minimum) to SizeMappedMaxScale x PointSize (maximum),
        // so the point-size slider still sets the overall size. Rows with no value in the size
        // column, or no size column chosen, keep PointSize.
        //</summary>
        public bool SizeMapped { get => MappingLayer.SizeMapped; set => EditLayer.SizeMapped = value; }
        public int SizeColumnIndex { get => MappingLayer.SizeColumnIndex; set => EditLayer.SizeColumnIndex = value; }
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
        public int GlyphColumnIndex { get => MappingLayer.GlyphColumnIndex; set => EditLayer.GlyphColumnIndex = value; }

        public List<GlyphAssignment> GlyphAssignments
        {
            get => MappingLayer.GlyphAssignments;
            set => EditLayer.GlyphAssignments = value ?? new List<GlyphAssignment>();
        }

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
        public List<GlyphRange> GlyphRanges
        {
            get => MappingLayer.GlyphRanges;
            set => EditLayer.GlyphRanges = value ?? new List<GlyphRange>();
        }

        public const int MaxGlyphRanges = 5;

        [System.Serializable]
        public class GlyphRange
        {
            public int glyphIndex;
            public float min;
            public float max;
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

            EnsureStack();
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
            try
            {
                LoadStartupDataset();
            }
            finally
            {
                HasInitialised = true;
            }
        }

        private void LoadStartupDataset()
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

        /// <summary>
        /// Loads fileName (re-read from disk, columnar first, then the CSV fallback) into the edit
        /// layer, keeping its mappings, and raises OnDatasetLoaded, OnBookmarksChanged and
        /// OnPlotSettingsChanged. Creates the base layer first when the plot is empty. On failure
        /// the layer's dataset becomes null and no event is raised (as before layers).
        /// </summary>
        public void LoadLocalDataset(string fileName)
        {
            PlotLayer target;
            if (Stack.Count == 0)
            {
                target = NewBaseLayer();
                Stack.ResetTo(target);
            }
            else if (!HasEditLayer)
            {
                Debug.LogWarning($"[MultiplayerScatterplotManager] LoadLocalDataset({fileName}): the layer being edited no longer exists.");
                return;
            }
            else if (EditLayer.Kind == LayerKind.Computed)
            {
                PostPlotMessage("A computed layer has no dataset of its own", PlotMessageKind.Refused);
                return;
            }
            else
            {
                target = EditLayer;
            }

            LoadDatasetInto(target, fileName);
        }

        public void RequestLoadDatasetRpc(string fileName)
        {
            if (ForwardPlotOp(PlotOp.LoadDataset, text: fileName)) return;

            // "New plot": the stack becomes its first layer alone (that object is kept, so PointSize
            // and SizeMapped survive as before), loaded with fileName and reset below. Adding a
            // dataset on top of the plot is RequestAddDatasetLayerRpc.
            bool structureChanged = ResetStackForNewPlot(out PlotLayer baseLayer);
            bool loaded = LoadDatasetInto(baseLayer, fileName);

            if (loaded)
            {
                PlotLayer layer = baseLayer;
                layer.XColumnIndex = 0;
                layer.YColumnIndex = Mathf.Clamp(
                    1,
                    0,
                    layer.Dataset.ColumnCount - 1
                );

                layer.ZColumnIndex = Mathf.Clamp(
                    2,
                    0,
                    layer.Dataset.ColumnCount - 1
                );

                layer.ColorColumnIndex = -1;
                layer.TimeColumnIndex = -1;
                TimeScrub = 0f;
                IsPlaying = false;
                layer.GlyphColumnIndex = -1;
                layer.GlyphAssignments.Clear();
                layer.GlyphRanges.Clear();
                // Filter/size column indices from the previous dataset would point at unrelated
                // columns here (and filtering is active for any entry), so reset them.
                layer.Filters.Clear();
                layer.SizeColumnIndex = -1;
                Stack.Touch();

                if (structureChanged)
                    OnLayersChanged?.Invoke();
                OnPlotSettingsChanged?.Invoke();
            }
            else if (structureChanged)
            {
                RaiseChanged(true);
            }
        }

        public void RequestXColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.XColumn, columnIndex)) return;
            if (RefuseLayerEdit() || !CheckAxisMapping(0, columnIndex)) return;

            XColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestYColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.YColumn, columnIndex)) return;
            if (RefuseLayerEdit() || !CheckAxisMapping(1, columnIndex)) return;

            YColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestZColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.ZColumn, columnIndex)) return;
            if (RefuseLayerEdit() || !CheckAxisMapping(2, columnIndex)) return;

            ZColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestColorColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.ColourColumn, columnIndex)) return;
            if (RefuseLayerEdit()) return;

            ColorColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestPointSizeRpc(float size)
        {
            if (ForwardToHost(PlotOp.PointSize, floatArg: size)) return;
            if (RefuseLayerEdit()) return;

            PointSize = Mathf.Clamp(size, 0f, MaxPointSize);
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestTimeColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.TimeColumn, columnIndex)) return;
            if (RefuseLayerEdit()) return;

            TimeColumnIndex = columnIndex;
            IsPlaying = false; // stop any running playback when the column changes
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestTimeScrubRpc(float normalizedValue)
        {
            if (ForwardPlotOp(PlotOp.TimeScrub, floatArg: normalizedValue)) return;

            TimeScrub = Mathf.Clamp01(normalizedValue);
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestPlaybackToggleRpc(bool isPlaying)
        {
            if (ForwardPlotOp(PlotOp.Playback, boolArg: isPlaying)) return;

            IsPlaying = isPlaying;

            // If pressing play after having reached the end, loop back to the start
            if (IsPlaying && TimeScrub >= 1f)
            {
                TimeScrub = 0f;
            }

            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>Sets PlaybackRate, clamped to [MinPlaybackRate, MaxPlaybackRate]. Shared; the scrub position is kept.</summary>
        public void RequestPlaybackRateRpc(float rate)
        {
            if (ForwardPlotOp(PlotOp.PlaybackRate, floatArg: rate)) return;

            if (!float.IsFinite(rate))
                return;

            PlaybackRate = Mathf.Clamp(rate, MinPlaybackRate, MaxPlaybackRate);
            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Scrub units (0-1) per second that playback advances: the 1x base times PlaybackRate.
        /// 1x is real time (1 / clock range in seconds) when every layer's time column is
        /// seconds-like (DateTime, stored as seconds, or a numeric column named "...time..."),
        /// otherwise PlaybackSpeed. The clock range is GetClockRange (one layer: its column's range).
        /// </summary>
        public float EffectiveScrubPerSecond()
        {
            float baseSpeed = PlaybackSpeed;
            if (GetClockRange(out float min, out float max) && PlotClock.SecondsLike(Layers))
            {
                float range = max - min;
                if (range > 0f && range <= k_MaxRealTimeRangeSeconds)
                    baseSpeed = 1f / range;
            }

            return baseSpeed * PlaybackRate;
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
            if (RefuseLayerEdit()) return;

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
            if (RefuseLayerEdit()) return;

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
            if (RefuseLayerEdit()) return false;

            Filters.Add(new FilterEntry { ColumnIndex = columnIndex, Label = filterLabel });
            OnPlotSettingsChanged?.Invoke();
            return true;
        }

        public void RequestRemoveFilterRpc(int filterIndex)
        {
            if (ForwardToHost(PlotOp.RemoveFilter, filterIndex)) return;
            if (RefuseLayerEdit()) return;

            if (filterIndex < 0 || filterIndex >= Filters.Count)
                return;

            Filters.RemoveAt(filterIndex);
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestClearFiltersRpc()
        {
            if (ForwardToHost(PlotOp.ClearFilters)) return;
            if (RefuseLayerEdit()) return;

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

            if (NetworkScatterplotState.TryForwardToHost(PlotRequest.ForFilterRanges(ranges, EditLayerRequestId))) return true;
            if (RefuseLayerEdit()) return false;

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
            if (RefuseLayerEdit()) return;

            SizeColumnIndex = columnIndex;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestSizeMappedRpc(bool mapped)
        {
            if (ForwardToHost(PlotOp.SizeMapped, boolArg: mapped)) return;
            if (RefuseLayerEdit()) return;

            SizeMapped = mapped;
            OnPlotSettingsChanged?.Invoke();
        }

        public void RequestGlyphColumnRpc(int columnIndex)
        {
            if (ForwardToHost(PlotOp.GlyphColumn, columnIndex)) return;
            if (RefuseLayerEdit()) return;

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
            if (RefuseLayerEdit()) return;

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
        /// Looks up the manually-assigned glyph for a label on the edit layer, or defaultGlyph if
        /// the label has no assignment yet (e.g. never touched by a user this session).
        /// </summary>
        public int GetGlyphForLabel(string label, int defaultGlyph = 0) =>
            MappingLayer.GetGlyphForLabel(label, defaultGlyph);

        /// <summary>
        /// Glyph for one row of the edit layer's glyph column: its value's own assignment if it
        /// has one, otherwise the glyph of the range its numeric value falls in (pass NaN for a
        /// categorical column), otherwise defaultGlyph. Rows with no value (hasValue false)
        /// always get defaultGlyph.
        /// </summary>
        public int GetGlyphForRow(string label, bool hasValue, float numericValue, int defaultGlyph = 0) =>
            MappingLayer.GetGlyphForRow(label, hasValue, numericValue, defaultGlyph);

        /// <summary>
        /// Index in the edit layer's GlyphRanges of the range containing value (min and max
        /// inclusive), or -1. GlyphRanges is sorted by min, so a value on an edge two ranges
        /// share resolves to the lower range.
        /// </summary>
        public int GetGlyphRangeIndex(float value) => MappingLayer.GetGlyphRangeIndex(value);

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

            if (NetworkScatterplotState.TryForwardToHost(PlotRequest.ForGlyphRanges(ranges, EditLayerRequestId))) return true;
            if (RefuseLayerEdit()) return false;

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

        // ---- Multiplayer -------------------------------------------------------------------------
        // Every shared Request…Rpc starts with a forward guard: on a connected non-host client the
        // change is sent to the host (NetworkScatterplotState), which applies it and replicates the
        // result to everyone, so the method returns without applying it locally. Offline and on the
        // host the guard is false and the method behaves exactly as before. Layer-scoped requests
        // carry the edit layer's id (ForwardToHost); plot-level ones carry none (ForwardPlotOp).
        // Local-only state (bookmarks, blacklist, shuffle locks, tooltips) is never forwarded.

        private bool ForwardToHost(PlotOp op, int intArg = 0, float floatArg = 0f, bool boolArg = false, string text = null) =>
            NetworkScatterplotState.TryForwardToHost(new PlotRequest(op, intArg, floatArg, boolArg, text, EditLayerRequestId));

        private static bool ForwardPlotOp(PlotOp op, int intArg = 0, float floatArg = 0f, bool boolArg = false, string text = null) =>
            NetworkScatterplotState.TryForwardToHost(new PlotRequest(op, intArg, floatArg, boolArg, text));

        /// <summary>
        /// Client side (and undo): shows the snapshot's shared state without forwarding anything:
        /// rebuilds the layer stack (PlotSnapshot.WriteTo), then raises the events in RaiseChanged
        /// order, never part-way through. OnDatasetLoaded fires only when the active dataset changed.
        /// </summary>
        public void ApplyNetworkSnapshot(PlotSnapshot snapshot, float timeScrub)
        {
            int before = LayerLevelSignature();
            snapshot.WriteTo(this, timeScrub);
            TrimDatasets();
            RaiseChanged(LayerLevelSignature() != before);
        }

        private void Update()
        {
            if (!IsPlaying || !AnyLayerHasTimeColumn())
                return;

            TimeScrub += EffectiveScrubPerSecond() * Time.deltaTime;

            if (TimeScrub >= 1f)
            {
                TimeScrub = 1f;
                IsPlaying = false; // stop at the end rather than looping silently
            }

            OnPlotSettingsChanged?.Invoke();
        }

        // ---- seeds ----------------------------------------------------------------------------

        // A base layer built from the scene-serialised seeds (what the plot showed before layers).
        private PlotLayer SeedLayer(string id) => new PlotLayer
        {
            Id = id,
            Name = string.IsNullOrEmpty(m_InitialDatasetName) ? "Layer 1" : Path.GetFileNameWithoutExtension(m_InitialDatasetName),
            Kind = LayerKind.Dataset,
            DatasetName = m_InitialDatasetName ?? "",
            XColumnIndex = m_InitialXColumn,
            YColumnIndex = m_InitialYColumn,
            ZColumnIndex = m_InitialZColumn,
            ColorColumnIndex = m_InitialColorColumn,
            PointSize = m_InitialPointSize,
            TimeColumnIndex = m_InitialTimeColumn,
            SizeMapped = m_InitialSizeMapped,
            SizeColumnIndex = m_InitialSizeColumn,
            GlyphColumnIndex = m_InitialGlyphColumn,
            Filters = PlotLayer.CopyFilters(m_InitialFilters),
            GlyphAssignments = PlotLayer.CopyAssignments(m_InitialGlyphAssignments),
            GlyphRanges = PlotLayer.CopyGlyphRanges(m_InitialGlyphRanges),
        };
    }
}