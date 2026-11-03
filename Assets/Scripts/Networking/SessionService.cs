using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// STUB — owner: session/state agent. Public API below is the contract other agents compile
    /// against; keep every signature. Wraps create/join/leave through Unity Multiplayer Services
    /// sessions with Relay, and drives NGO's NetworkManager (host or client).
    /// The app must work offline exactly as before until someone hosts.
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

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public Task HostAsync(SessionMode mode) { OnSessionMessage?.Invoke("Multiplayer not implemented yet"); return Task.CompletedTask; }
        public Task JoinAsync(string joinCode) { OnSessionMessage?.Invoke("Multiplayer not implemented yet"); return Task.CompletedTask; }
        public Task LeaveAsync() => Task.CompletedTask;

        // Silence unused-event warnings in the stub.
        void RaiseAll() { OnStateChanged?.Invoke(); OnPlayersChanged?.Invoke(); }
    }
}
