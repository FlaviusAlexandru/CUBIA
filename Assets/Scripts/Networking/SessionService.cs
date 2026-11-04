using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Create/join/leave through Unity Multiplayer Services sessions with Relay; the session drives
    /// NGO's NetworkManager.Singleton (host or client). Players and LastChangedBy are fed by
    /// NetworkScatterplotState. Errors never throw into the UI: they arrive as OnSessionMessage.
    /// The app works offline exactly as before until someone hosts.
    /// </summary>
    public class SessionService : MonoBehaviour
    {
        public static SessionService Instance { get; private set; }

        public const int MaxPlayers = 4;

        public SessionState State { get; private set; } = SessionState.Offline;
        public SessionMode Mode { get; private set; } = SessionMode.Remote;

        /// <summary>6-character join code while in a session, else empty.</summary>
        public string JoinCode { get; private set; } = "";

        /// <summary>Stable id of the current session (used e.g. as the Vivox channel name). Empty offline.</summary>
        public string SessionId { get; private set; } = "";

        /// <summary>Dataset the session runs (the host's, as MultiplayerScatterplotManager names it); empty offline or if unknown.</summary>
        public string SessionDatasetName { get; private set; } = "";

        /// <summary>DatasetHash of the host's dataset file, so joiners can check theirs before the session starts.</summary>
        public string SessionDatasetHash { get; private set; } = "";

        /// <summary>The host's player name when the session was created; empty offline.</summary>
        public string HostName { get; private set; } = "";

        public bool IsInSession => State == SessionState.Hosting || State == SessionState.Client;
        public bool IsHost => State == SessionState.Hosting;

        /// <summary>Connected players, host first. Empty offline.</summary>
        public IReadOnlyList<PlayerInfo> Players => m_Players;
        readonly List<PlayerInfo> m_Players = new();

        /// <summary>Name of the player who last changed the shared plot ("" if none yet).</summary>
        public string LastChangedBy { get; private set; } = "";

        /// <summary>Most recent join codes, newest first (max 5), persisted.</summary>
        public IReadOnlyList<string> RecentCodes => m_RecentCodes;
        readonly List<string> m_RecentCodes = new();

        public event Action OnStateChanged;      // State/Mode/JoinCode/SessionId changed
        public event Action OnPlayersChanged;    // Players or LastChangedBy changed
        /// <summary>User-facing one-line messages: "Session ended", connection errors, dataset mismatch...</summary>
        public event Action<string> OnSessionMessage;

        const string k_ModeProperty = "mode";
        const string k_DatasetProperty = "dataset";
        const string k_DatasetHashProperty = "datasetHash";
        const string k_HostNameProperty = "host";
        const string k_RecentCodesKey = "cubia.mp.recentCodes";
        const int k_MaxRecentCodes = 5;

        ISession m_Session;
        NetworkManager m_Network; // the NetworkManager whose OnClientStopped we listen to
        bool m_Leaving;           // set while we leave on purpose, so the resulting disconnect isn't "Session ended"

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            LoadRecentCodes();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            DetachSession();
            Instance = null;
        }

        public Task HostAsync(SessionMode mode) => HostAsync(mode, "");

        /// <summary>Hosts a session that runs datasetName; joiners see it (and whether they have it) before the session starts.</summary>
        public async Task HostAsync(SessionMode mode, string datasetName)
        {
            if (!CanStart()) return;
            SetState(SessionState.Connecting, mode, "", "");

            try
            {
                await UgsBootstrap.EnsureSignedInAsync();
                var options = new SessionOptions
                {
                    MaxPlayers = MaxPlayers,
                    IsPrivate = true, // joinable by code only, never listed
                }.WithRelayNetwork(new RelayNetworkOptions(RelayProtocol.WSS)); // WebSocket over 443: gets through eduroam/campus firewalls that block UDP
                AddMemberProperty(options, k_ModeProperty, ((int)mode).ToString());
                AddMemberProperty(options, k_DatasetProperty, datasetName);
                AddMemberProperty(options, k_DatasetHashProperty, DatasetHash.Of(datasetName));
                AddMemberProperty(options, k_HostNameProperty, PlayerProfile.Name);

                IHostSession session = await MultiplayerService.Instance.CreateSessionAsync(options);
                AttachSession(session, SessionState.Hosting, mode);
            }
            catch (Exception e)
            {
                await FailAsync("Couldn't start a session", e);
            }
        }

        public async Task JoinAsync(string joinCode)
        {
            string code = (joinCode ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0) { PostMessage("Enter a join code"); return; }
            if (!CanStart()) return;
            SetState(SessionState.Connecting, Mode, "", "");

            try
            {
                await UgsBootstrap.EnsureSignedInAsync();
                ISession session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code, new JoinSessionOptions());
                RememberCode(code);
                AttachSession(session, SessionState.Client, ReadMode(session));
            }
            catch (Exception e)
            {
                await FailAsync($"Couldn't join {code}", e);
            }
        }

        /// <summary>Leaves (a host ends the session for everyone). The local plot keeps its current state.</summary>
        public async Task LeaveAsync()
        {
            if (!IsInSession || m_Leaving) return;

            m_Leaving = true;
            ISession session = DetachSession();
            try
            {
                if (session.IsHost)
                    await session.AsHost().DeleteAsync(); // clients get Deleted / a disconnect → "Session ended"
                else
                    await session.LeaveAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionService] Leaving the session failed: {e.Message}");
            }
            finally
            {
                ShutdownNetwork();
                m_Leaving = false;
                SetOffline();
            }
        }

        // ---------------------------------------------------------------- fed by NetworkScatterplotState

        internal void SetPlayers(IReadOnlyList<PlayerInfo> players)
        {
            m_Players.Clear();
            m_Players.AddRange(players);
            OnPlayersChanged?.Invoke();
        }

        internal void SetLastChangedBy(string playerName)
        {
            playerName ??= "";
            if (LastChangedBy == playerName) return;
            LastChangedBy = playerName;
            OnPlayersChanged?.Invoke();
        }

        internal void PostMessage(string message) => OnSessionMessage?.Invoke(message);

        // ---------------------------------------------------------------- session lifecycle

        bool CanStart()
        {
            if (State != SessionState.Offline) { PostMessage("Already in a session"); return false; }
            if (NetworkManager.Singleton == null) { PostMessage("Multiplayer is not set up in this scene"); return false; }

            // Relay runs over WebSockets (see HostAsync); the transport must match on host and joiners alike.
            if (NetworkManager.Singleton.NetworkConfig.NetworkTransport is Unity.Netcode.Transports.UTP.UnityTransport transport)
                transport.UseWebSockets = true;
            return true;
        }

        void AttachSession(ISession session, SessionState state, SessionMode mode)
        {
            m_Session = session;
            m_Session.Deleted += OnSessionLost;
            m_Session.RemovedFromSession += OnSessionLost;
            m_Network = NetworkManager.Singleton;
            m_Network.OnClientStopped += OnNetworkStopped; // also raised on the host when it stops
            SessionDatasetName = ReadProperty(session, k_DatasetProperty);
            SessionDatasetHash = ReadProperty(session, k_DatasetHashProperty);
            HostName = ReadProperty(session, k_HostNameProperty);
            SetState(state, mode, session.Code ?? "", session.Id ?? "");
        }

        /// <summary>Unsubscribes from the current session and returns it (null if none).</summary>
        ISession DetachSession()
        {
            ISession session = m_Session;
            if (session != null)
            {
                session.Deleted -= OnSessionLost;
                session.RemovedFromSession -= OnSessionLost;
            }
            if (m_Network != null)
                m_Network.OnClientStopped -= OnNetworkStopped;
            m_Session = null;
            m_Network = null;
            return session;
        }

        void OnSessionLost() => _ = EndAsync("Session ended");
        void OnNetworkStopped(bool wasHost) => _ = EndAsync("Session ended");

        // The host left, the connection dropped or we were removed: back to offline, keeping the plot as it is.
        async Task EndAsync(string message)
        {
            if (m_Leaving || !IsInSession) return;

            ISession session = DetachSession();
            SetOffline();
            PostMessage(message);

            try
            {
                if (session != null) await session.LeaveAsync(); // stops the session's network handler
            }
            catch (Exception e)
            {
                Debug.Log($"[SessionService] Cleanup after session end: {e.Message}");
            }
            ShutdownNetwork();
        }

        async Task FailAsync(string what, Exception e)
        {
            Debug.LogWarning($"[SessionService] {what}: {e}");
            ISession session = DetachSession();
            try
            {
                if (session != null) await session.LeaveAsync();
            }
            catch (Exception cleanup)
            {
                Debug.Log($"[SessionService] Cleanup after failure: {cleanup.Message}");
            }
            ShutdownNetwork();
            SetOffline();
            PostMessage($"{what}: {e.Message}");
        }

        // Normally the session stops NGO itself; this only catches a NetworkManager left running.
        static void ShutdownNetwork()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network != null && network.IsListening && !network.ShutdownInProgress)
                network.Shutdown();
        }

        static SessionMode ReadMode(ISession session) =>
            int.TryParse(ReadProperty(session, k_ModeProperty), out int value) && Enum.IsDefined(typeof(SessionMode), value)
                ? (SessionMode)value
                : SessionMode.Remote;

        static string ReadProperty(ISession session, string key) =>
            session.Properties.TryGetValue(key, out SessionProperty property) ? property.Value ?? "" : "";

        // Session properties are visible to members only; empty values are left out.
        static void AddMemberProperty(SessionOptions options, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
                options.SessionProperties[key] = new SessionProperty(value, VisibilityPropertyOptions.Member);
        }

        void SetOffline()
        {
            SessionDatasetName = SessionDatasetHash = HostName = "";
            SetState(SessionState.Offline, Mode, "", "");
            if (m_Players.Count > 0 || LastChangedBy.Length > 0)
            {
                m_Players.Clear();
                LastChangedBy = "";
                OnPlayersChanged?.Invoke();
            }
        }

        void SetState(SessionState state, SessionMode mode, string joinCode, string sessionId)
        {
            if (State == state && Mode == mode && JoinCode == joinCode && SessionId == sessionId) return;
            State = state;
            Mode = mode;
            JoinCode = joinCode;
            SessionId = sessionId;
            OnStateChanged?.Invoke();
        }

        // ---------------------------------------------------------------- recent codes

        void LoadRecentCodes()
        {
            m_RecentCodes.Clear();
            foreach (string code in PlayerPrefs.GetString(k_RecentCodesKey, "").Split(','))
            {
                if (code.Length > 0 && m_RecentCodes.Count < k_MaxRecentCodes) m_RecentCodes.Add(code);
            }
        }

        void RememberCode(string code)
        {
            m_RecentCodes.Remove(code);
            m_RecentCodes.Insert(0, code);
            if (m_RecentCodes.Count > k_MaxRecentCodes)
                m_RecentCodes.RemoveRange(k_MaxRecentCodes, m_RecentCodes.Count - k_MaxRecentCodes);
            PlayerPrefs.SetString(k_RecentCodesKey, string.Join(",", m_RecentCodes));
            PlayerPrefs.Save();
        }
    }
}
