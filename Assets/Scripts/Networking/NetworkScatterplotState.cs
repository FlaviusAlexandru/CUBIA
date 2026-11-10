using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// The shared plot, on an in-scene NetworkObject (created by Tools/CUBIA/Multiplayer/1 Setup Network).
    ///
    /// Host: watches MultiplayerScatterplotManager and publishes a PlotSnapshot whenever a shared
    /// setting really changed (late joiners get it automatically). Clients: apply every published
    /// snapshot to their manager, and send their own edits to the host (TryForwardToHost, called
    /// from the manager's Request…Rpc methods) instead of applying them. Last change wins.
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
        string m_HashedDatasetName;
        string m_DatasetHash = "";

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
                if (m_Manager != null)
                {
                    m_Manager.OnPlotSettingsChanged += OnManagerChanged;
                    m_Manager.OnDatasetLoaded += OnManagerChanged;
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
                m_Manager.OnDatasetLoaded -= OnManagerChanged;
                m_Manager.OnDatasetLoaded -= OnClientDatasetLoaded;
            }

            // The manager keeps whatever it shows now: leaving returns to a local copy of the shared state.
            m_HashedDatasetName = null;
            SharedPlotAuthority.ResetToOffline();
            SessionService.Instance?.SetPlayers(System.Array.Empty<PlayerInfo>());
            SessionService.Instance?.SetLastChangedBy("");
        }

        // ---------------------------------------------------------------- host: publish

        void OnManagerChanged() => PublishSnapshot(recordChanger: true);

        void PublishSnapshot(bool recordChanger)
        {
            // When NGO loads the data scene this object spawns before the manager's Start has loaded a
            // dataset; publishing the empty start-up state would only flash "You need …" on clients.
            if (m_Manager.LoadedDataset == null)
                return;

            PlotSnapshot next = PlotSnapshot.Capture(m_Manager);
            if (next.DatasetName != m_HashedDatasetName)
            {
                m_HashedDatasetName = next.DatasetName;
                m_DatasetHash = DatasetHash.Of(next.DatasetName);
                SharedPlotAuthority.SetDatasetStatus(DatasetMatch.Match, next.DatasetName);
            }

            // Every dataset and subset layer carries its file's hash (DatasetHash.Of caches per file).
            foreach (LayerSnapshot layer in next.Layers)
            {
                if (layer.Kind == (byte)LayerKind.Computed || string.IsNullOrEmpty(layer.DatasetName))
                    continue;
                layer.DatasetHash = layer.DatasetName == m_HashedDatasetName ? m_DatasetHash : DatasetHash.Of(layer.DatasetName);
            }
            next.Published = true;

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
                    return;
            }

            m_Plot.Value = next;
            if (recordChanger)
                m_LastChangedBy.Value = new FixedString128Bytes(PlayerName(m_RequestSender ?? NetworkManager.LocalClientId));
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
            try { Apply(request); }
            finally { m_RequestSender = null; }
        }

        // Runs the same manager call the client made; on the host it applies normally and OnManagerChanged publishes it.
        // Layer-scoped legacy ops run inside EditLayerScope(r.Layer), so they land on the layer the client
        // was editing even if the host switched layers since; a request for a layer that no longer exists
        // is dropped. Plot-level ops ignore r.Layer; layer ops take it as their explicit id.
        void Apply(PlotRequest r)
        {
            MultiplayerScatterplotManager m = m_Manager;
            if (ApplyPlotOrLayerOp(m, r))
                return;

            if (!string.IsNullOrEmpty(r.Layer) && m.Stack.Find(r.Layer) == null)
            {
                Debug.Log($"[NetworkScatterplotState] Dropped {r.Op}: layer '{r.Layer}' no longer exists.");
                return;
            }

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
                }
            }
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
                case PlotOp.Shuffle: m.RequestShuffleColumnsForClient(r.Int, r.Blacklist); return true;
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

        // After an NGO scene load this object spawns before the manager's Start, which then loads its own
        // start-up dataset over the host's: put the host's plot back whenever the dataset drifts away.
        void OnClientDatasetLoaded()
        {
            PlotSnapshot shared = m_Plot.Value;
            if (m_Manager.CurrentDatasetName != shared.DatasetName && SharedPlotAuthority.DatasetStatus == DatasetMatch.Match)
                ApplyToManager(shared);
        }

        void ApplyToManager(PlotSnapshot snapshot)
        {
            DatasetMatch status = DatasetHash.Compare(snapshot.DatasetName, snapshot.DatasetHash);
            SharedPlotAuthority.SetDatasetStatus(status, snapshot.DatasetName);
            if (status != DatasetMatch.Match || m_Manager == null)
                return; // without the host's exact dataset, column indices would mean nothing here

            if (m_Manager.LoadedDataset == null || m_Manager.CurrentDatasetName != snapshot.DatasetName)
                m_Manager.LoadLocalDataset(snapshot.DatasetName);

            m_Manager.ApplyNetworkSnapshot(snapshot, snapshot.ScrubAt(NetworkManager.ServerTime.Time));
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
