using System;
using System.Collections.Generic;
using System.IO;
using DataViz.Flow;
using DataViz.Networking;
using UnityEngine;

namespace DataViz
{
    // Shuffle: field locks (per dataset, persisted), the shuffle blacklist, the per-dataset shuffle
    // history and the shuffle itself, moved here unchanged from MultiplayerScatterplotManager.cs
    // when the manager was split for layers. They act on the edit layer through the forwarders.
    // Layer shuffle unlocks (LFR-32, B14) are declared at the bottom; WP9 owns this file.
    public partial class MultiplayerScatterplotManager
    {
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

        // Before the first load (or on an empty plot) there is no dataset, or one with no name;
        // locks set then must not be stored under an empty key.
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

        // The shuffle blacklist is stored per dataset by ColumnPrefsStore (see the bookmarks note in
        // MultiplayerScatterplotManager.cs). Returns a copy.
        private HashSet<string> GetBlacklistForCurrentDataset() =>
            LoadedDataset == null ? new HashSet<string>() : ColumnPrefsStore.GetBlacklist(LoadedDataset.Name);

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
            if (RefuseLayerEdit()) return;

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
            if (RefuseLayerEdit()) return;

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
            if (RefuseLayerEdit()) return;

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

        // ---- Layer shuffle unlocks (LFR-32) ----------------------------------------------------
        // Which non-base layers Shuffle may change, besides Layers[0]. Local to this player, like the
        // field locks, in memory only, default false. WP1 stores the flag; WP9 makes Shuffle use it.
        private readonly HashSet<string> m_ShuffleUnlockedLayers = new();

        /// <summary>True when the local player has unlocked this layer for Shuffle. The base layer always shuffles; computed layers never do.</summary>
        public bool IsLayerShuffleUnlocked(string layerId) =>
            !string.IsNullOrEmpty(layerId) && m_ShuffleUnlockedLayers.Contains(layerId);

        /// <summary>Local only (never forwarded). Raises OnLayersChanged when the flag changed.</summary>
        public void RequestSetLayerShuffleUnlockedRpc(string layerId, bool unlocked)
        {
            if (string.IsNullOrEmpty(layerId))
                return;

            bool changed = unlocked ? m_ShuffleUnlockedLayers.Add(layerId) : m_ShuffleUnlockedLayers.Remove(layerId);
            if (changed)
                OnLayersChanged?.Invoke();
        }
    }
}
