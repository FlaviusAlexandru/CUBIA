using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DataViz.Networking;
using UnityEngine;

namespace DataViz.Flow
{
    /// <summary>
    /// One saved view (LFR-33): the whole layer stack with every layer's settings, the active layer
    /// and the paused time. Columns are stored by name, not index, so a view still loads after the
    /// converter adds or reorders columns. Datasets are stored by file name.
    /// </summary>
    [Serializable]
    public sealed class SavedView
    {
        public string Name = "";
        public string SavedAt = "";            // ISO 8601, local time
        public string ActiveLayerId = "";
        public int NextLayerNumber = 1;
        public float TimeScrub;
        public float PlaybackRate = 1f;
        public List<SavedLayer> Layers = new();

        /// <summary>Axis settings (ASR-12). Views saved before the feature have HasAxes false and load with AxisSettings.CreateLegacy().</summary>
        public bool HasAxes;
        public AxisSettings Axes = AxisSettings.CreateLegacy();

        /// <summary>"P03_movement.cdataset + 2 more", for list entries.</summary>
        public string DatasetSummary
        {
            get
            {
                var files = new List<string>();
                foreach (SavedLayer l in Layers)
                    if (!string.IsNullOrEmpty(l.Layer?.DatasetName) && !files.Contains(l.Layer.DatasetName))
                        files.Add(l.Layer.DatasetName);
                if (files.Count == 0) return "(no dataset)";
                return files.Count == 1 ? files[0] : $"{files[0]} + {files.Count - 1} more";
            }
        }
    }

    /// <summary>A layer of a SavedView: its snapshot with column indices replaced by names.</summary>
    [Serializable]
    public sealed class SavedLayer
    {
        public LayerSnapshot Layer = new();
        public string X = "", Y = "", Z = "", Colour = "", Size = "", Glyph = "", Time = "", EndTime = "", Label = "", Order = "";
        public string[] Filters = Array.Empty<string>();
    }

    /// <summary>
    /// Converts between the manager's state and SavedView, and keeps the saved views in
    /// saved_views.json under Application.persistentDataPath (all datasets in one file).
    /// Loading replaces the whole stack, like undo. Only the host (or an offline plot) loads views,
    /// because the shared plot is the host's; the VR tab and the dashboard both call Load.
    /// </summary>
    public static class SavedViewStore
    {
        [Serializable]
        class SavedViewFile
        {
            public List<SavedView> Views = new();
        }

        static List<SavedView> s_Views;

        /// <summary>Tests point this at a temporary file; null = the real file.</summary>
        public static string PathOverride;

        static string FilePath => PathOverride ?? Path.Combine(Application.persistentDataPath, "saved_views.json");

        /// <summary>Raised after a view is saved or deleted.</summary>
        public static event Action OnChanged;

        /// <summary>Goes up on every save or delete, for UIs that poll instead of subscribing.</summary>
        public static int Version { get; private set; }

        // ------------------------------------------------------------------ store

        /// <summary>Copies of the saved views, newest first.</summary>
        public static List<SavedView> All()
        {
            EnsureLoaded();
            var list = new List<SavedView>(s_Views.Count);
            foreach (SavedView v in s_Views)
                list.Add(Clone(v));
            list.Sort((a, b) => string.CompareOrdinal(b.SavedAt, a.SavedAt));
            return list;
        }

        public static SavedView Find(string name)
        {
            EnsureLoaded();
            SavedView v = s_Views.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return v != null ? Clone(v) : null;
        }

        /// <summary>Stores view, replacing a view of the same name (case-insensitive), and writes the file.</summary>
        public static void Save(SavedView view)
        {
            if (view == null || string.IsNullOrWhiteSpace(view.Name)) return;
            EnsureLoaded();
            view.Name = view.Name.Trim();
            s_Views.RemoveAll(x => string.Equals(x.Name, view.Name, StringComparison.OrdinalIgnoreCase));
            s_Views.Add(Clone(view));
            Write();
            Version++;
            OnChanged?.Invoke();
        }

        public static bool Delete(string name)
        {
            EnsureLoaded();
            int removed = s_Views.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Write();
            Version++;
            OnChanged?.Invoke();
            return true;
        }

        /// <summary>"View 1", "View 2", …: the first number no saved view uses.</summary>
        public static string NextDefaultName()
        {
            EnsureLoaded();
            for (int n = 1; ; n++)
            {
                string name = "View " + n.ToString(CultureInfo.InvariantCulture);
                if (!s_Views.Exists(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return name;
            }
        }

        /// <summary>Drops the cache, so the next call reads the file again (tests).</summary>
        public static void Reload()
        {
            s_Views = null;
            Version++;
        }

        static void EnsureLoaded()
        {
            if (s_Views != null) return;
            s_Views = new List<SavedView>();
            try
            {
                if (!File.Exists(FilePath)) return;
                SavedViewFile file = JsonUtility.FromJson<SavedViewFile>(File.ReadAllText(FilePath));
                if (file?.Views != null)
                    s_Views.AddRange(file.Views.FindAll(v => v != null && !string.IsNullOrWhiteSpace(v.Name)));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SavedViewStore] Could not read {FilePath}: {e.Message}");
            }
        }

        static void Write()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonUtility.ToJson(new SavedViewFile { Views = s_Views }, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SavedViewStore] Could not write {FilePath}: {e.Message}");
            }
        }

        static SavedView Clone(SavedView v) => JsonUtility.FromJson<SavedView>(JsonUtility.ToJson(v));

        // ------------------------------------------------------------------ conversion (pure)

        /// <summary>
        /// A SavedView of snapshot. datasetOf returns the loaded dataset of a file name (null when
        /// not loaded: that layer's columns are then stored as "" and its indices kept as they are).
        /// The view is paused at the snapshot's time.
        /// </summary>
        public static SavedView FromSnapshot(PlotSnapshot snapshot, string name, Func<string, DatasetColumnar> datasetOf)
        {
            var view = new SavedView
            {
                Name = name ?? "",
                SavedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ActiveLayerId = snapshot?.ActiveLayerId ?? "",
                NextLayerNumber = snapshot?.NextLayerNumber ?? 1,
                TimeScrub = snapshot?.TimeScrub ?? 0f,
                PlaybackRate = snapshot?.PlaybackRate ?? 1f,
                HasAxes = snapshot != null,
                Axes = (snapshot?.Axes ?? AxisSettings.CreateLegacy()).Clone(),
            };
            if (snapshot?.Layers == null) return view;

            foreach (LayerSnapshot ls in snapshot.Layers)
            {
                if (ls == null) continue;
                DatasetColumnar d = ColumnDataset(ls, snapshot.Layers, datasetOf);
                var saved = new SavedLayer
                {
                    Layer = Copy(ls),
                    X = NameOf(d, ls.XColumn), Y = NameOf(d, ls.YColumn), Z = NameOf(d, ls.ZColumn),
                    Colour = NameOf(d, ls.ColourColumn), Size = NameOf(d, ls.SizeColumn), Glyph = NameOf(d, ls.GlyphColumn),
                    Time = NameOf(d, ls.TimeColumn), EndTime = NameOf(d, ls.EndTimeColumn), Label = NameOf(d, ls.LabelColumn),
                    Order = NameOf(d, ls.OrderColumn),
                    Filters = Array.ConvertAll(ls.FilterColumns ?? Array.Empty<int>(), c => NameOf(d, c)),
                };
                saved.Layer.DatasetHash = "";
                view.Layers.Add(saved);
            }
            return view;
        }

        /// <summary>
        /// The snapshot view stands for, with column indices looked up by name in today's datasets.
        /// Null with error set when a dataset is not loaded or a named column is missing.
        /// </summary>
        public static PlotSnapshot ToSnapshot(SavedView view, Func<string, DatasetColumnar> datasetOf, out string error)
        {
            error = null;
            if (view == null || view.Layers == null || view.Layers.Count == 0)
            {
                error = "The view has no layers";
                return null;
            }

            var layers = new LayerSnapshot[view.Layers.Count];
            var plain = new LayerSnapshot[view.Layers.Count];
            for (int i = 0; i < plain.Length; i++) plain[i] = view.Layers[i]?.Layer ?? new LayerSnapshot();

            for (int i = 0; i < view.Layers.Count; i++)
            {
                SavedLayer saved = view.Layers[i] ?? new SavedLayer();
                LayerSnapshot ls = Copy(saved.Layer);
                bool isPoint = (LayerKind)ls.Kind != LayerKind.Computed;
                if (isPoint && !string.IsNullOrEmpty(ls.DatasetName) && datasetOf(ls.DatasetName) == null)
                {
                    error = $"{ls.DatasetName} could not be loaded";
                    return null;
                }
                DatasetColumnar d = ColumnDataset(ls, plain, datasetOf);
                string missing = null;
                ls.XColumn = IndexOf(d, saved.X, ls.XColumn, ref missing);
                ls.YColumn = IndexOf(d, saved.Y, ls.YColumn, ref missing);
                ls.ZColumn = IndexOf(d, saved.Z, ls.ZColumn, ref missing);
                ls.ColourColumn = IndexOf(d, saved.Colour, ls.ColourColumn, ref missing);
                ls.SizeColumn = IndexOf(d, saved.Size, ls.SizeColumn, ref missing);
                ls.GlyphColumn = IndexOf(d, saved.Glyph, ls.GlyphColumn, ref missing);
                ls.TimeColumn = IndexOf(d, saved.Time, ls.TimeColumn, ref missing);
                ls.EndTimeColumn = IndexOf(d, saved.EndTime, ls.EndTimeColumn, ref missing);
                ls.LabelColumn = IndexOf(d, saved.Label, ls.LabelColumn, ref missing);
                ls.OrderColumn = IndexOf(d, saved.Order, ls.OrderColumn, ref missing);
                int[] filters = ls.FilterColumns ?? Array.Empty<int>();
                for (int f = 0; f < filters.Length; f++)
                {
                    string filterName = saved.Filters != null && f < saved.Filters.Length ? saved.Filters[f] : "";
                    filters[f] = IndexOf(d, filterName, filters[f], ref missing);
                }
                if (missing != null)
                {
                    string where = !string.IsNullOrEmpty(ls.DatasetName) ? ls.DatasetName : ls.Name;
                    error = $"{where} has no column {missing}";
                    return null;
                }
                ls.DatasetHash = "";
                layers[i] = ls;
            }

            return new PlotSnapshot
            {
                Layers = layers,
                ActiveLayerId = view.ActiveLayerId ?? "",
                NextLayerNumber = Math.Max(1, view.NextLayerNumber),
                IsPlaying = false,
                PlaybackRate = view.PlaybackRate > 0f ? view.PlaybackRate : 1f,
                TimeScrub = Mathf.Clamp01(view.TimeScrub),
                Axes = view.HasAxes && view.Axes != null ? view.Axes.Clone().Sanitised() : AxisSettings.CreateLegacy(),
            };
        }

        // ------------------------------------------------------------------ manager

        /// <summary>Saves the manager's current stack as name (replacing a view of that name). Returns the view.</summary>
        public static SavedView SaveCurrent(MultiplayerScatterplotManager m, string name)
        {
            if (m == null) return null;
            if (string.IsNullOrWhiteSpace(name)) name = NextDefaultName();
            SavedView view = FromSnapshot(PlotSnapshot.Capture(m), name.Trim(), file => DatasetFor(m, file));
            Save(view);
            return view;
        }

        /// <summary>
        /// Replaces the manager's stack with view (one rebuild). Returns null on success, else why
        /// not: not the host, presenter mode, a dataset that cannot be loaded or a missing column.
        /// The plot is left unchanged when it fails.
        /// </summary>
        public static string Load(MultiplayerScatterplotManager m, SavedView view)
        {
            PlotSnapshot snapshot = Prepare(m, view, out string error);
            if (snapshot == null) return error;
            m.ApplyNetworkSnapshot(snapshot, snapshot.TimeScrub);
            return null;
        }

        /// <summary>What Load would refuse with (null = it would load), without changing the plot.</summary>
        public static string CheckLoad(MultiplayerScatterplotManager m, SavedView view)
        {
            Prepare(m, view, out string error);
            return error;
        }

        static PlotSnapshot Prepare(MultiplayerScatterplotManager m, SavedView view, out string error)
        {
            error = m == null ? "No plot"
                : view == null ? "Saved view not found"
                : !NetworkScatterplotState.IsLocalHostOrOffline ? "Saved views can only be loaded on the host"
                : !SharedPlotAuthority.LocalPlayerCanEdit ? "Presenter mode is on"
                : null;
            if (error != null) return null;
            return ToSnapshot(view, file => DatasetFor(m, file), out error);
        }

        static DatasetColumnar DatasetFor(MultiplayerScatterplotManager m, string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            foreach (PlotLayer l in m.Layers)
                if (l.Dataset != null && string.Equals(DatasetRegistry.Normalise(l.DatasetName), DatasetRegistry.Normalise(file), StringComparison.OrdinalIgnoreCase))
                    return l.Dataset;
            return DatasetRegistry.Get(file);
        }

        // ------------------------------------------------------------------ helpers

        // The dataset a layer's column indices refer to: its own, or a computed layer's source's.
        static DatasetColumnar ColumnDataset(LayerSnapshot ls, LayerSnapshot[] all, Func<string, DatasetColumnar> datasetOf)
        {
            string file = ls.DatasetName;
            if ((LayerKind)ls.Kind == LayerKind.Computed || string.IsNullOrEmpty(file))
            {
                foreach (LayerSnapshot other in all)
                    if (other != null && other.Id == ls.SourceLayerId && !string.IsNullOrEmpty(other.DatasetName))
                        file = other.DatasetName;
            }
            return string.IsNullOrEmpty(file) ? null : datasetOf(file);
        }

        static string NameOf(DatasetColumnar d, int index) => index >= 0 ? d?.GetColumn(index)?.Name ?? "" : "";

        // A stored name wins; "" keeps the stored index (-1 = none, or a dataset that was not loaded at save time).
        static int IndexOf(DatasetColumnar d, string name, int fallback, ref string missing)
        {
            if (string.IsNullOrEmpty(name)) return fallback < 0 || d == null || fallback < d.ColumnCount ? fallback : -1;
            if (d == null) return fallback;
            int index = d.GetColumnIndex(name);
            if (index < 0) missing ??= name;
            return index;
        }

        static LayerSnapshot Copy(LayerSnapshot s) =>
            s == null ? new LayerSnapshot() : JsonUtility.FromJson<LayerSnapshot>(JsonUtility.ToJson(s));
    }
}
