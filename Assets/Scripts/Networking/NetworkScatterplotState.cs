using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// The shared plot, on an in-scene NetworkObject (created by Tools/CUBIA/Multiplayer/1 Setup Network).
    ///
    /// Host: watches MultiplayerScatterplotManager and publishes a PlotSnapshot (the whole layer
    /// stack, LFR-30) whenever a shared setting really changed (late joiners get it automatically).
    /// Every layer that names a dataset carries the host's file hash. Clients: apply every published
    /// snapshot to their manager, hiding (LocalUnavailable) each layer whose file they lack or have
    /// in another version, and send their own edits to the host (TryForwardToHost, called from the
    /// manager's Request…Rpc methods) instead of applying them. Last change wins. The active layer
    /// is part of the snapshot, so it is shared by all players (Q15).
    /// Also carries the presenter-mode flag, who changed the plot last, and the player list.
    /// Offline (not spawned) this component does nothing.
    /// </summary>
    public class NetworkScatterplotState : NetworkBehaviour
    {
        public static NetworkScatterplotState Instance { get; private set; }

        // During playback the host's scrub drifts slightly from the published extrapolation;
        // republish only when it is off by more than this (scrub units, 0.01 = 40 ms at default speed)
        // or by more than k_PlaybackDriftSeconds of playback, whichever is smaller (slow real-time playback).
        const float k_PlaybackDriftTolerance = 0.01f;
        const float k_PlaybackDriftSeconds = 0.04f;

        readonly NetworkVariable<PlotSnapshot> m_Plot = new(new PlotSnapshot());
        readonly NetworkVariable<bool> m_PresenterMode = new();
        readonly NetworkVariable<FixedString128Bytes> m_LastChangedBy = new();
        NetworkList<PlayerEntry> m_Players;

        MultiplayerScatterplotManager m_Manager;
        ulong? m_RequestSender;   // set while the host applies a client's request

        // Host: dataset name -> file hash, so publishing (every manager event, also during playback)
        // does not touch the file system. Cleared when a dataset is (re)loaded.
        readonly Dictionary<string, string> m_HashByName = new();
        bool m_HasPublished;

        // Client: true while a snapshot is being applied (its own events must not re-apply it), and
        // the local stack's shape right after the last apply (see OnClientDatasetLoaded).
        bool m_Applying;
        int m_AppliedSignature;

        /// <summary>True offline and on the host; false on a connected client.</summary>
        public static bool IsLocalHostOrOffline => Instance == null || !Instance.IsSpawned || Instance.IsServer;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            m_Players = new NetworkList<PlayerEntry>(); // allocates native memory, so not a field initializer
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        /// <summary>
        /// Called at the top of each shared manager Request…Rpc. On a connected client it sends the
        /// change to the host (or drops it in presenter mode / without the host's dataset) and returns
        /// true, so the manager does not apply it locally. Offline and on the host returns false.
        /// </summary>
        public static bool TryForwardToHost(PlotRequest request)
        {
            if (IsLocalHostOrOffline)
                return false;

            bool datasetOk = SharedPlotAuthority.DatasetStatus == DatasetMatch.Match || request.Op == PlotOp.LoadDataset;
            if (SharedPlotAuthority.LocalPlayerCanEdit && datasetOk)
                Instance.SubmitRequestRpc(request);
            return true;
        }

        /// <summary>Host only; ignored elsewhere.</summary>
        public void SetPresenterMode(bool on)
        {
            if (IsSpawned && IsServer)
                m_PresenterMode.Value = on;
        }

        public override void OnNetworkSpawn()
        {
            m_Manager = MultiplayerScatterplotManager.Instance;

            m_Players.OnListChanged += OnPlayersChanged;
            m_LastChangedBy.OnValueChanged += OnLastChangedByChanged;
            m_PresenterMode.OnValueChanged += OnPresenterModeChanged;
            PlayerProfile.OnChanged += SendProfile;

            if (IsServer)
            {
                m_Players.Clear();
                m_PresenterMode.Value = false;
                m_LastChangedBy.Value = default;
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
                m_HashByName.Clear();
                m_HasPublished = false;
                if (m_Manager != null)
                {
                    m_Manager.OnPlotSettingsChanged += OnManagerChanged;
                    m_Manager.OnDatasetLoaded += OnManagerDatasetLoaded;
                    PublishSnapshot(recordChanger: false);
                }
            }
            else
            {
                m_Plot.OnValueChanged += OnPlotChanged;
                if (m_Manager != null)
                    m_Manager.OnDatasetLoaded += OnClientDatasetLoaded;
                ApplyToManager(m_Plot.Value);
            }

            SendProfile();
            OnPlayersChanged(default);
            OnLastChangedByChanged(default, m_LastChangedBy.Value);
            SharedPlotAuthority.SetPresenterMode(m_PresenterMode.Value);
        }

        public override void OnNetworkDespawn()
        {
            m_Players.OnListChanged -= OnPlayersChanged;
            m_LastChangedBy.OnValueChanged -= OnLastChangedByChanged;
            m_PresenterMode.OnValueChanged -= OnPresenterModeChanged;
            m_Plot.OnValueChanged -= OnPlotChanged;
            PlayerProfile.OnChanged -= SendProfile;
            if (NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged -= OnManagerChanged;
                m_Manager.OnDatasetLoaded -= OnManagerDatasetLoaded;
                m_Manager.OnDatasetLoaded -= OnClientDatasetLoaded;

                // Back to bounds computed from the local layers from the next redraw on. Nothing is
                // raised here: despawn often comes with the scene unloading under the listeners.
                m_Manager.SharedBoundsOverride = null;
            }

            // The manager keeps whatever it shows now: leaving returns to a local copy of the shared state.
            m_HashByName.Clear();
            SharedPlotAuthority.ResetToOffline();
            SessionService.Instance?.SetPlayers(System.Array.Empty<PlayerInfo>());
            SessionService.Instance?.SetLastChangedBy("");
        }

        // ---------------------------------------------------------------- host: publish

        void OnManagerChanged() => PublishSnapshot(recordChanger: true);

        // A (re)load may have replaced a file on disk: hash again.
        void OnManagerDatasetLoaded()
        {
            m_HashByName.Clear();
            PublishSnapshot(recordChanger: true);
        }

        // The manager's Start sets HasInitialised after its first load without raising anything, so
        // a start-up load that failed (or found no file) would otherwise publish nothing until the next change.
        void Update()
        {
            if (!m_HasPublished && IsSpawned && IsServer && m_Manager != null && m_Manager.HasInitialised)
                PublishSnapshot(recordChanger: false);
        }

        void PublishSnapshot(bool recordChanger)
        {
            if (m_Manager == null)
                return;
            // When NGO loads the data scene this object spawns before the manager's Start has loaded a
            // dataset; publishing that seed state would only flash "You need …" on clients. After Start
            // an empty plot is published like any other (Q13: a plot may be emptied).
            if (!m_Manager.HasInitialised && m_Manager.LoadedDataset == null)
                return;

            PlotSnapshot next = PlotSnapshot.Capture(m_Manager);
            foreach (LayerSnapshot layer in next.Layers)
            {
                if (!string.IsNullOrEmpty(layer.DatasetName))
                    layer.DatasetHash = HostHash(layer.DatasetName);
            }
            next.Published = true;
            SharedPlotAuthority.SetDatasetStatus(DatasetMatch.Match, MainDatasetName(next));

            double now = NetworkManager.ServerTime.Time;
            next.ScrubServerTime = now;

            // Most manager events change nothing shared (bookmarks, locks, playback ticks): skip those.
            PlotSnapshot current = m_Plot.Value;
            if (current.SamePlotAs(next))
            {
                float tolerance = Mathf.Clamp(next.EffectiveScrubPerSecond * k_PlaybackDriftSeconds, 1e-5f, k_PlaybackDriftTolerance);
                bool sameTime = next.IsPlaying
                    ? Mathf.Abs(current.ScrubAt(now) - next.TimeScrub) < tolerance
                    : current.TimeScrub == next.TimeScrub;
                if (sameTime)
                {
                    m_HasPublished = true;
                    return;
                }
            }

            m_Plot.Value = next;
            m_HasPublished = true;
            if (recordChanger)
                m_LastChangedBy.Value = new FixedString128Bytes(PlayerName(m_RequestSender ?? NetworkManager.LocalClientId));
        }

        string HostHash(string datasetName)
        {
            if (!m_HashByName.TryGetValue(datasetName, out string hash))
                m_HashByName[datasetName] = hash = DatasetHash.Of(datasetName);
            return hash;
        }

        /// <summary>
        /// The dataset a snapshot is mainly about (status line, session dataset, offline restore):
        /// the active layer's, a computed active layer's source's (or its own once frozen), else
        /// the first layer that names one; "" for an empty plot.
        /// </summary>
        public static string MainDatasetName(PlotSnapshot snapshot)
        {
            LayerSnapshot active = snapshot?.ActiveLayer;
            if (active == null)
                return "";
            if (!string.IsNullOrEmpty(active.DatasetName))
                return active.DatasetName;
            LayerSnapshot source = active.Kind == (byte)LayerKind.Computed ? snapshot.FindLayer(active.SourceLayerId) : null;
            if (source != null && !string.IsNullOrEmpty(source.DatasetName))
                return source.DatasetName;
            foreach (LayerSnapshot layer in snapshot.Layers)
            {
                if (layer != null && !string.IsNullOrEmpty(layer.DatasetName))
                    return layer.DatasetName;
            }
            return "";
        }

        [Rpc(SendTo.Server)]
        void SubmitRequestRpc(PlotRequest request, RpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (m_PresenterMode.Value && sender != NetworkManager.LocalClientId)
            {
                Debug.Log($"[NetworkScatterplotState] Presenter mode: ignored {request.Op} from {PlayerName(sender)}.");
                return;
            }
            if (m_Manager == null)
                return;

            m_RequestSender = sender;
            try { ApplyRequest(m_Manager, request); }
            finally { m_RequestSender = null; }
        }

        /// <summary>
        /// Host side of a client's request: runs the same manager call the client made; it applies
        /// normally and OnManagerChanged publishes it. Layer-scoped legacy ops run inside
        /// EditLayerScope(r.Layer), so they land on the layer the client was editing even if the host
        /// switched layers since. A request naming a layer that no longer exists (removed while the
        /// request was on its way) is dropped: false, nothing changes, no events. Plot-level ops
        /// ignore r.Layer; Shuffle reads it as a list of ids.
        /// </summary>
        public static bool ApplyRequest(MultiplayerScatterplotManager m, PlotRequest r)
        {
            if (m == null)
                return false;

            bool plotLevel = r.Op is PlotOp.LoadDataset or PlotOp.TimeScrub or PlotOp.Playback or PlotOp.PlaybackRate
                or PlotOp.Shuffle or PlotOp.ShuffleBackward or PlotOp.ShuffleForward or PlotOp.AddDatasetLayer;
            if (!plotLevel && !string.IsNullOrEmpty(r.Layer) && m.Stack.Find(r.Layer) == null)
            {
                Debug.Log($"[NetworkScatterplotState] Dropped {r.Op}: layer '{r.Layer}' no longer exists.");
                return false;
            }

            if (ApplyPlotOrLayerOp(m, r))
                return true;

            using (m.EditLayerScope(r.Layer))
            {
                switch (r.Op)
                {
                    case PlotOp.XColumn: m.RequestXColumnRpc(r.Int); break;
                    case PlotOp.YColumn: m.RequestYColumnRpc(r.Int); break;
                    case PlotOp.ZColumn: m.RequestZColumnRpc(r.Int); break;
                    case PlotOp.ColourColumn: m.RequestColorColumnRpc(r.Int); break;
                    case PlotOp.PointSize: m.RequestPointSizeRpc(r.Float); break;
                    case PlotOp.TimeColumn: m.RequestTimeColumnRpc(r.Int); break;
                    case PlotOp.FilterColumn: m.RequestFilterIndexRpc(r.Int); break;
                    case PlotOp.FilterLabel: m.RequestFilterLabelRpc(r.Text); break;
                    case PlotOp.AddFilter: m.RequestAddFilterColumnRpc(r.Int, r.Text); break;
                    case PlotOp.RemoveFilter: m.RequestRemoveFilterRpc(r.Int); break;
                    case PlotOp.ClearFilters: m.RequestClearFiltersRpc(); break;
                    case PlotOp.SizeColumn: m.RequestSizeColumnRpc(r.Int); break;
                    case PlotOp.SizeMapped: m.RequestSizeMappedRpc(r.Bool); break;
                    case PlotOp.GlyphColumn: m.RequestGlyphColumnRpc(r.Int); break;
                    case PlotOp.GlyphAssignment: m.RequestGlyphAssignmentRpc(r.Text, r.Int); break;
                    case PlotOp.GlyphRanges: m.RequestSetGlyphRangesRpc(r.GlyphRanges); break;
                    case PlotOp.FilterRanges: m.RequestSetFilterRangesRpc(r.FilterRanges); break;
                    default: return false;
                }
            }
            return true;
        }

        // Plot-level ops (no layer scope; Shuffle's r.Layer lists layer ids, read by the manager) and
        // the layer ops (r.Layer is the explicit id). False for the layer-scoped legacy ops.
        static bool ApplyPlotOrLayerOp(MultiplayerScatterplotManager m, PlotRequest r)
        {
            switch (r.Op)
            {
                case PlotOp.LoadDataset: m.RequestLoadDatasetRpc(r.Text); return true;
                case PlotOp.TimeScrub: m.RequestTimeScrubRpc(r.Float); return true;
                case PlotOp.Playback: m.RequestPlaybackToggleRpc(r.Bool); return true;
                case PlotOp.PlaybackRate: m.RequestPlaybackRateRpc(r.Float); return true;
                case PlotOp.Shuffle: m.RequestShuffleColumnsForClient(r.Int, r.Blacklist, r.LayerIds); return true;
                case PlotOp.ShuffleBackward: m.RequestShuffleBackwardRpc(); return true;
                case PlotOp.ShuffleForward: m.RequestShuffleForwardRpc(); return true;

                case PlotOp.AddDatasetLayer: m.RequestAddDatasetLayerRpc(r.Text); return true;
                case PlotOp.AddSubsetLayer: m.RequestAddSubsetLayerRpc(r.Layer, r.Int, r.Text); return true;
                case PlotOp.AddComputedLayer: m.RequestAddComputedLayerRpc(r.Layer, (ComputedLayerKind)r.Int); return true;
                case PlotOp.RemoveLayer: m.RequestRemoveLayerRpc(r.Layer); return true;
                case PlotOp.DuplicateLayer: m.RequestDuplicateLayerRpc(r.Layer); return true;
                case PlotOp.SetActiveLayer: m.RequestSetActiveLayerRpc(r.Layer); return true;
                case PlotOp.RenameLayer: m.RequestRenameLayerRpc(r.Layer, r.Text); return true;
                case PlotOp.ReorderLayer: m.RequestReorderLayerRpc(r.Layer, r.Int); return true;
                case PlotOp.SetLayerVisible: m.RequestSetLayerVisibleRpc(r.Layer, r.Bool); return true;
                case PlotOp.SetLayerNormalised: m.RequestSetLayerNormalisedRpc(r.Layer, r.Bool); return true;
                case PlotOp.SetSubsetHiddenInSource: m.RequestSetSubsetHiddenInSourceRpc(r.Layer, r.Bool); return true;
                case PlotOp.SetComputedFlag: m.RequestSetComputedFlagRpc(r.Layer, r.Bool); return true;
                case PlotOp.SetLayerOpacity: m.RequestSetLayerOpacityRpc(r.Layer, r.Float); return true;
                case PlotOp.SetComputedValue: m.RequestSetComputedValueRpc(r.Layer, r.Float); return true;
                case PlotOp.SetComputedSpan: m.RequestSetComputedSpanRpc(r.Layer, r.Float); return true;
                case PlotOp.SetLayerLineWidth: m.RequestSetLayerLineWidthRpc(r.Layer, r.Float); return true;
                case PlotOp.SetLayerColour: m.RequestSetLayerColourRpc(r.Layer, LayerSnapshot.UnpackColour(unchecked((uint)r.Int))); return true;
                case PlotOp.SetLayerGlyph: m.RequestSetLayerGlyphRpc(r.Layer, r.Int); return true;
                case PlotOp.SetLayerTimeBehaviour: m.RequestSetLayerTimeBehaviourRpc(r.Layer, (LayerTimeBehaviour)r.Int); return true;
                case PlotOp.SetLayerEndTimeColumn: m.RequestSetLayerEndTimeColumnRpc(r.Layer, r.Int); return true;
                case PlotOp.SetLayerLabelColumn: m.RequestSetLayerLabelColumnRpc(r.Layer, r.Int); return true;
                case PlotOp.SetComputedAxis: m.RequestSetComputedAxisRpc(r.Layer, r.Int); return true;
                case PlotOp.SetComputedColumn: m.RequestSetComputedColumnRpc(r.Layer, r.Int); return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- client: apply

        void OnPlotChanged(PlotSnapshot previous, PlotSnapshot current) => ApplyToManager(current);

        // After an NGO scene load this object spawns before the manager's Start, which then loads its
        // own start-up dataset into the active layer: put the host's stack back when that changed it.
        // OnDatasetLoaded also fires when a snapshot switches the active layer (risk 3); that happens
        // inside ApplyToManager (m_Applying), and right after an apply the signature matches, so a
        // re-apply can never trigger another one.
        void OnClientDatasetLoaded()
        {
            if (m_Applying || m_Manager == null)
                return;
            PlotSnapshot shared = m_Plot.Value;
            if (shared != null && shared.Published && LocalStackSignature(m_Manager) != m_AppliedSignature)
                ApplyToManager(shared);
        }

        void ApplyToManager(PlotSnapshot snapshot)
        {
            StackDatasetCheck check = DatasetHash.CheckStack(snapshot, DatasetHash.Of);
            var problems = new List<string>(check.Missing);
            problems.AddRange(check.Mismatched);
            List<string> told = SharedPlotAuthority.SetDatasetStatus(check.Status, MainDatasetName(snapshot), problems);
            foreach (string name in told)
                ReportMissingDatasetRpc(ShortName(name), check.Mismatched.Contains(name));

            if (m_Manager == null || snapshot == null || !snapshot.Published)
                return; // nothing published yet: keep the local plot

            m_Applying = true;
            try { ApplySnapshot(m_Manager, snapshot, snapshot.ScrubAt(NetworkManager.ServerTime.Time), check); }
            finally { m_Applying = false; }
            m_AppliedSignature = LocalStackSignature(m_Manager);
        }

        /// <summary>
        /// Client side of LFR-30: shows the host's whole stack, whatever this peer has. Layers in
        /// check.Unavailable end up LocalUnavailable without data (WriteTo already does that for a
        /// missing or other-version file; this adds layers whose file the host lacks and computed
        /// layers on a hidden source). While some layer is hidden, the host's numeric axis bounds
        /// replace the local ones so the layers this peer has sit where the host draws them;
        /// categorical axes stay local (the snapshot does not carry them).
        /// </summary>
        public static void ApplySnapshot(MultiplayerScatterplotManager m, PlotSnapshot snapshot, float timeScrub, StackDatasetCheck check)
        {
            m.SharedBoundsOverride = null;
            m.ApplyNetworkSnapshot(snapshot, timeScrub);

            bool changed = false;
            LayerSnapshot[] layers = snapshot.Layers ?? Array.Empty<LayerSnapshot>();
            for (int i = 0; i < layers.Length && i < check.Unavailable.Length; i++)
            {
                if (!check.Unavailable[i] || layers[i] == null)
                    continue;
                PlotLayer layer = m.Stack.Find(layers[i].Id);
                if (layer == null || (layer.LocalUnavailable && layer.Dataset == null))
                    continue;
                layer.LocalUnavailable = true;
                layer.Dataset = null;
                changed = true;
            }

            bool anyHidden = false;
            foreach (PlotLayer layer in m.Layers)
                anyHidden |= layer.LocalUnavailable;
            if (anyHidden && snapshot.TryGetBounds(out AxisBounds host))
            {
                AxisBounds local = m.SharedBounds;
                AxisBounds merged = local;
                for (int axis = 0; axis < 3; axis++)
                {
                    if ((snapshot.BoundsValid & (1 << axis)) != 0)
                        merged.Set(axis, host.Get(axis));
                }
                if (!merged.SameAs(local))
                {
                    m.SharedBoundsOverride = merged;
                    changed = true;
                }
            }

            if (changed)
                m.NotifyLayersChanged();
        }

        // The local stack's shape: what a stray dataset load would change.
        static int LocalStackSignature(MultiplayerScatterplotManager m)
        {
            var h = new HashCode();
            h.Add(m.ActiveLayerId);
            foreach (PlotLayer layer in m.Layers)
            {
                h.Add(layer.Id);
                h.Add(layer.Kind);
                h.Add(layer.DatasetName);
                h.Add(layer.LocalUnavailable);
                h.Add(layer.Dataset != null);
            }
            return h.ToHashCode();
        }

        // Dataset names travel without extension and short enough for a FixedString128Bytes (UTF-8).
        static FixedString128Bytes ShortName(string datasetName)
        {
            string name = Path.GetFileNameWithoutExtension(datasetName ?? "");
            if (name.Length > 40) name = name.Substring(0, 40);
            return new FixedString128Bytes(name);
        }

        /// <summary>R7: a client tells the host which dataset it lacks; the host posts it (once per client and dataset).</summary>
        [Rpc(SendTo.Server)]
        void ReportMissingDatasetRpc(FixedString128Bytes dataset, bool otherVersion, RpcParams rpcParams = default)
        {
            string who = PlayerName(rpcParams.Receive.SenderClientId);
            SessionService.Instance?.PostMessage(otherVersion
                ? $"{who} does not have {dataset} (other version)"
                : $"{who} does not have {dataset}");
        }

        // ---------------------------------------------------------------- players, presenter, last change

        void SendProfile()
        {
            var name = new FixedString128Bytes(PlayerProfile.Name);
            if (IsServer)
                UpsertPlayer(NetworkManager.LocalClientId, name, PlayerProfile.Colour);
            else
                RegisterPlayerRpc(name, PlayerProfile.Colour);
        }

        [Rpc(SendTo.Server)]
        void RegisterPlayerRpc(FixedString128Bytes playerName, Color colour, RpcParams rpcParams = default) =>
            UpsertPlayer(rpcParams.Receive.SenderClientId, playerName, colour);

        void UpsertPlayer(ulong clientId, FixedString128Bytes playerName, Color colour)
        {
            var entry = new PlayerEntry { ClientId = clientId, Name = playerName, Colour = colour };
            for (int i = 0; i < m_Players.Count; i++)
            {
                if (m_Players[i].ClientId != clientId) continue;
                if (!m_Players[i].Equals(entry)) m_Players[i] = entry;
                return;
            }
            m_Players.Add(entry); // the host registers first, so it stays first
        }

        void OnClientDisconnected(ulong clientId)
        {
            for (int i = m_Players.Count - 1; i >= 0; i--)
            {
                if (m_Players[i].ClientId == clientId)
                    m_Players.RemoveAt(i);
            }
        }

        string PlayerName(ulong clientId)
        {
            foreach (PlayerEntry p in m_Players)
            {
                if (p.ClientId == clientId) return p.Name.ToString();
            }
            return "Player " + clientId;
        }

        void OnPlayersChanged(NetworkListEvent<PlayerEntry> _)
        {
            var players = new List<PlayerInfo>(m_Players.Count);
            foreach (PlayerEntry p in m_Players)
            {
                players.Add(new PlayerInfo
                {
                    ClientId = p.ClientId,
                    Name = p.Name.ToString(),
                    Colour = p.Colour,
                    IsHost = p.ClientId == NetworkManager.ServerClientId,
                });
            }
            SessionService.Instance?.SetPlayers(players);
        }

        void OnLastChangedByChanged(FixedString128Bytes previous, FixedString128Bytes current) =>
            SessionService.Instance?.SetLastChangedBy(current.ToString());

        void OnPresenterModeChanged(bool previous, bool current) => SharedPlotAuthority.SetPresenterMode(current);
    }
}
