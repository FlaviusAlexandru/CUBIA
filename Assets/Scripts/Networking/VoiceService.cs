using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Vivox;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Vivox voice for a session. Joins a channel named after SessionService.SessionId when a
    /// session starts and leaves (and logs out) when it ends. Nothing Vivox-related is touched
    /// while offline: no initialisation, no login, no microphone access.
    ///
    /// Remote mode: positional (3D) channel, voice on by default. The listener/speaker position
    /// is the local camera expressed in table-local coordinates (TableFrame), so distances agree
    /// across players whose virtual tables are placed differently.
    ///
    /// CoLocated mode: voice off by default and the channel is NOT joined at all (people hear each
    /// other in the room; not joining avoids echo, needless mic capture and bandwidth). Enabling
    /// voice in CoLocated mode joins a non-positional group channel.
    ///
    /// The microphone always starts muted. Each participant's Vivox display name is
    /// "p{NGO ClientId}" so presence can map speakers to players (IsSpeaking / OnSpeakingChanged).
    /// Public API never throws; all async failures are logged.
    /// </summary>
    public class VoiceService : MonoBehaviour
    {
        public static VoiceService Instance { get; private set; }

        /// <summary>Voice on/off for this player in the current session.</summary>
        public bool VoiceEnabled { get; private set; }
        public bool IsMuted { get; private set; } = true;
        public bool IsInChannel { get; private set; }

        public event Action OnVoiceStateChanged; // VoiceEnabled / IsMuted / IsInChannel changed
        /// <summary>(NGO client id, isSpeaking)</summary>
        public event Action<ulong, bool> OnSpeakingChanged;

        [Header("Positional audio (Remote mode, metres in table space)")]
        [Tooltip("Beyond this distance a speaker is inaudible.")]
        [SerializeField] int m_AudibleDistance = 12;
        [Tooltip("Within this distance a speaker is heard at full volume.")]
        [SerializeField] int m_ConversationalDistance = 2;
        [Tooltip("How strongly volume fades with distance (1 = natural).")]
        [SerializeField] float m_FadeIntensity = 1f;
        [Tooltip("Seconds between 3D position updates sent to Vivox.")]
        [SerializeField] float m_PositionInterval = 0.1f;
        [Tooltip("Seconds to wait for the NGO client id before giving up on voice.")]
        [SerializeField] float m_ClientIdTimeout = 30f;
        [SerializeField] bool m_VerboseLogging = false;

        /// <summary>Display-name prefix; presence parses "p{clientId}".</summary>
        public const string DisplayNamePrefix = "p";

        /// <summary>Name of the Vivox channel currently joined, or empty.</summary>
        public string CurrentChannelName => m_JoinedChannel ?? "";

        /// <summary>Parses a Vivox display name "p{id}" into an NGO client id.</summary>
        public static bool TryParseClientId(string displayName, out ulong clientId)
        {
            clientId = 0;
            return !string.IsNullOrEmpty(displayName)
                && displayName.Length > DisplayNamePrefix.Length
                && displayName.StartsWith(DisplayNamePrefix, StringComparison.Ordinal)
                && ulong.TryParse(displayName.Substring(DisplayNamePrefix.Length), out clientId);
        }

        /// <summary>Builds a Vivox-legal channel name (allowed chars, short) from a session id.</summary>
        public static string ChannelNameFor(string sessionId)
        {
            const string allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.";
            var sb = new StringBuilder("cubia_");
            foreach (char c in sessionId ?? "")
            {
                if (allowed.IndexOf(c) >= 0) sb.Append(c);
                if (sb.Length >= 64) break; // Vivox allows 200; keep the full URI comfortably short
            }
            if (sb.Length == "cubia_".Length)
                sb.Append(((uint)(sessionId ?? "").GetHashCode()).ToString("x8"));
            return sb.ToString();
        }

        // ---------------- state ----------------
        SessionService m_Session;
        string m_SessionId = "";          // session we are (logically) in, "" offline
        SessionMode m_SessionMode = SessionMode.Remote;
        bool m_UserChoseVoice;            // user toggled voice this session → don't re-default on mode change

        bool m_Reconciling;
        bool m_Dirty;
        bool m_Destroyed;

        bool m_VivoxHooked;
        string m_LoggedInAs;              // display name we are logged in with, null if not
        string m_JoinedChannel;           // channel fully joined, null if none
        bool m_JoinedPositional;
        float m_NextPositionTime;

        readonly Dictionary<ulong, bool> m_Speaking = new();
        readonly Dictionary<VivoxParticipant, Action> m_ParticipantHandlers = new();

        int m_MainThreadId;
        readonly ConcurrentQueue<Action> m_MainThreadQueue = new();

        // ---------------- public API ----------------

        public void SetMuted(bool muted)
        {
            RunOnMainThread(() =>
            {
                if (IsMuted == muted) { ApplyInputMute(); return; }
                IsMuted = muted;
                ApplyInputMute();
                RaiseVoiceStateChanged();
            });
        }

        public void SetVoiceEnabled(bool enabled)
        {
            RunOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(m_SessionId))
                {
                    Log("SetVoiceEnabled ignored: not in a session.");
                    return;
                }
                m_UserChoseVoice = true;
                if (VoiceEnabled == enabled) return;
                VoiceEnabled = enabled;
                if (!enabled && !IsMuted) IsMuted = true; // re-enabling voice starts muted again
                ApplyInputMute();
                RaiseVoiceStateChanged();
                RequestReconcile();
            });
        }

        public bool IsSpeaking(ulong clientId) => m_Speaking.TryGetValue(clientId, out bool s) && s;

        // ---------------- lifecycle ----------------

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            m_MainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        void Start() => TryHookSession();

        void Update()
        {
            while (m_MainThreadQueue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { Debug.LogException(e, this); }
            }

            if (m_Session == null) TryHookSession();

            if (m_JoinedPositional && m_JoinedChannel != null && Time.unscaledTime >= m_NextPositionTime)
            {
                m_NextPositionTime = Time.unscaledTime + Mathf.Max(0.02f, m_PositionInterval);
                UpdatePosition();
            }
        }

        void OnDestroy()
        {
            m_Destroyed = true;
            if (m_Session != null) m_Session.OnStateChanged -= HandleSessionStateChanged;
            m_Session = null;

            if (Instance == this) Instance = null;

            var vivox = VivoxService.Instance;
            ClearParticipants(raise: false);
            if (vivox != null && m_VivoxHooked)
            {
                vivox.ParticipantAddedToChannel -= HandleParticipantAdded;
                vivox.ParticipantRemovedFromChannel -= HandleParticipantRemoved;
                m_VivoxHooked = false;
            }
            if (vivox != null && m_LoggedInAs != null)
            {
                m_LoggedInAs = null;
                m_JoinedChannel = null;
                _ = SafeAsync(vivox.LogoutAsync, "logout on destroy");
            }
        }

        void TryHookSession()
        {
            var s = SessionService.Instance;
            if (s == null || s == m_Session) return;
            if (m_Session != null) m_Session.OnStateChanged -= HandleSessionStateChanged;
            m_Session = s;
            m_Session.OnStateChanged += HandleSessionStateChanged;
            HandleSessionStateChanged();
        }

        // ---------------- session tracking ----------------

        void HandleSessionStateChanged()
        {
            if (!IsMainThread()) { RunOnMainThread(HandleSessionStateChanged); return; }
            if (m_Session == null || m_Destroyed) return;

            bool inSession = m_Session.IsInSession && !string.IsNullOrEmpty(m_Session.SessionId);
            string newId = inSession ? m_Session.SessionId : "";
            SessionMode newMode = m_Session.Mode;
            bool changed = false;

            if (newId != m_SessionId)
            {
                // New session, session switch, or left.
                m_SessionId = newId;
                m_SessionMode = newMode;
                m_UserChoseVoice = false;
                VoiceEnabled = inSession && newMode == SessionMode.Remote;
                IsMuted = true;
                changed = true;
            }
            else if (inSession && newMode != m_SessionMode)
            {
                // Mode can arrive after the session id on clients.
                m_SessionMode = newMode;
                if (!m_UserChoseVoice)
                {
                    VoiceEnabled = newMode == SessionMode.Remote;
                    IsMuted = true;
                }
                changed = true;
            }

            if (changed)
            {
                ApplyInputMute();
                RaiseVoiceStateChanged();
                RequestReconcile();
            }
        }

        // ---------------- reconcile loop ----------------
        // One async loop at a time drives Vivox toward the desired state; any change while it runs
        // sets m_Dirty and the loop re-evaluates. This serialises login/join/leave/logout.

        void RequestReconcile()
        {
            m_Dirty = true;
            if (m_Reconciling) return;
            _ = ReconcileLoopAsync();
        }

        async Task ReconcileLoopAsync()
        {
            m_Reconciling = true;
            try
            {
                int guard = 0;
                while (m_Dirty && !m_Destroyed && guard++ < 16)
                {
                    m_Dirty = false;
                    try { await ReconcileOnceAsync(); }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[VoiceService] Voice unavailable: {e.Message}", this);
                        Log(e.ToString());
                        // Don't spin; a later state change will retry.
                        if (!m_Dirty) break;
                    }
                }
            }
            finally
            {
                m_Reconciling = false;
            }
        }

        bool SessionStillWanted(string sessionId) => !m_Destroyed && m_SessionId == sessionId && !string.IsNullOrEmpty(sessionId);

        async Task ReconcileOnceAsync()
        {
            string sessionId = m_SessionId;
            bool inSession = !string.IsNullOrEmpty(sessionId);
            bool wantChannel = inSession && VoiceEnabled;
            bool positional = m_SessionMode == SessionMode.Remote;
            string targetChannel = wantChannel ? ChannelNameFor(sessionId) : null;

            var vivox = VivoxService.Instance;

            // 1) Leave a channel that is no longer wanted (or whose type changed).
            if (m_JoinedChannel != null && (m_JoinedChannel != targetChannel || m_JoinedPositional != positional))
            {
                string leaving = m_JoinedChannel;
                SetJoined(null, false);
                ClearParticipants(raise: true);
                if (vivox != null && vivox.IsLoggedIn)
                {
                    Log($"Leaving channel {leaving}");
                    await SafeAsync(() => vivox.LeaveChannelAsync(leaving), "leave channel");
                }
            }

            // 2) Out of session → log out entirely and reset.
            if (!inSession)
            {
                if (vivox != null && (m_LoggedInAs != null || vivox.IsLoggedIn))
                {
                    m_LoggedInAs = null;
                    Log("Logging out of Vivox");
                    await SafeAsync(vivox.LogoutAsync, "logout");
                }
                ApplyInputMute();
                return;
            }

            if (!wantChannel || m_JoinedChannel != null) return;

            // 3) Bring up UGS + Vivox (first time only).
            await UgsBootstrap.EnsureSignedInAsync();
            if (!SessionStillWanted(sessionId)) { m_Dirty = true; return; }

            vivox = VivoxService.Instance;
            if (vivox == null)
            {
                Debug.LogError("[VoiceService] VivoxService is not available. Is Vivox enabled and the project linked " +
                               "(Project Settings > Services > Vivox)?", this);
                return;
            }
            if (vivox.InitializationState != VivoxInitializationState.Initialized)
            {
                Log("Initialising Vivox");
                await vivox.InitializeAsync();
                if (!SessionStillWanted(sessionId)) { m_Dirty = true; return; }
            }
            HookVivox(vivox);
            ApplyInputMute(); // mute before we ever transmit

            // 4) Need our NGO client id for the display name.
            ulong? clientId = await WaitForClientIdAsync(sessionId);
            if (clientId == null)
            {
                if (SessionStillWanted(sessionId))
                    Debug.LogWarning("[VoiceService] Timed out waiting for the network connection; voice not started.", this);
                else m_Dirty = true;
                return;
            }

            string displayName = DisplayNamePrefix + clientId.Value;
            if (vivox.IsLoggedIn && m_LoggedInAs != displayName)
            {
                Log($"Re-login: was {m_LoggedInAs}, now {displayName}");
                await vivox.LogoutAsync();
                m_LoggedInAs = null;
            }
            if (!vivox.IsLoggedIn)
            {
                Log($"Logging in to Vivox as {displayName}");
                await vivox.LoginAsync(new LoginOptions { DisplayName = displayName });
                if (!vivox.IsLoggedIn) throw new InvalidOperationException("Vivox login did not complete.");
            }
            m_LoggedInAs = displayName;
            if (!SessionStillWanted(sessionId) || !VoiceEnabled) { m_Dirty = true; return; }

            // 5) Join.
            ApplyInputMute();
            if (positional)
            {
                var props = new Channel3DProperties(
                    Mathf.Max(1, m_AudibleDistance),
                    Mathf.Clamp(m_ConversationalDistance, 0, Mathf.Max(1, m_AudibleDistance)),
                    Mathf.Max(0f, m_FadeIntensity),
                    AudioFadeModel.InverseByDistance);
                Log($"Joining positional channel {targetChannel}");
                await vivox.JoinPositionalChannelAsync(targetChannel, ChatCapability.AudioOnly, props);
            }
            else
            {
                Log($"Joining group channel {targetChannel}");
                await vivox.JoinGroupChannelAsync(targetChannel, ChatCapability.AudioOnly);
            }

            SetJoined(targetChannel, positional);
            RegisterExistingParticipants(vivox);
            ApplyInputMute();
            if (positional) { m_NextPositionTime = 0f; UpdatePosition(); }

            // Something changed while joining → loop will leave/rejoin as needed.
            if (!SessionStillWanted(sessionId) || !VoiceEnabled || m_SessionMode == SessionMode.Remote != positional)
                m_Dirty = true;
        }

        async Task<ulong?> WaitForClientIdAsync(string sessionId)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, m_ClientIdTimeout);
            while (SessionStillWanted(sessionId))
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsListening && (nm.IsServer || nm.IsConnectedClient))
                    return nm.LocalClientId;
                if (Time.realtimeSinceStartup > deadline) return null;
                await Task.Delay(100);
            }
            return null;
        }

        void SetJoined(string channel, bool positional)
        {
            m_JoinedChannel = channel;
            m_JoinedPositional = channel != null && positional;
            bool inChannel = channel != null;
            if (IsInChannel != inChannel)
            {
                IsInChannel = inChannel;
                RaiseVoiceStateChanged();
            }
        }

        // ---------------- mic ----------------

        /// <summary>Mic transmits only when in a channel, voice is enabled and the user unmuted.</summary>
        void ApplyInputMute()
        {
            var vivox = VivoxService.Instance;
            // Never touch Vivox before it was initialised by us: Mute/Unmute would trigger init.
            if (vivox == null || vivox.InitializationState != VivoxInitializationState.Initialized) return;
            bool shouldMute = IsMuted || !VoiceEnabled || string.IsNullOrEmpty(m_SessionId);
            try
            {
                if (shouldMute) { if (!vivox.IsInputDeviceMuted) vivox.MuteInputDevice(); }
                else if (vivox.IsInputDeviceMuted) vivox.UnmuteInputDevice();
            }
            catch (Exception e) { Debug.LogWarning($"[VoiceService] Mic mute change failed: {e.Message}", this); }
        }

        // ---------------- 3D position ----------------

        void UpdatePosition()
        {
            var vivox = VivoxService.Instance;
            if (vivox == null || !vivox.IsLoggedIn || m_JoinedChannel == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            Transform t = cam.transform;
            Vector3 pos = t.position, fwd = t.forward, up = t.up;
            var frame = TableFrame.Instance;
            if (frame != null)
            {
                pos = frame.WorldToTable(pos);
                Quaternion local = frame.WorldToTable(t.rotation);
                fwd = local * Vector3.forward;
                up = local * Vector3.up;
            }
            try { vivox.Set3DPosition(pos, pos, fwd, up, m_JoinedChannel); }
            catch (Exception e) { Log($"Set3DPosition failed: {e.Message}"); }
        }

        // ---------------- speaking ----------------

        void HookVivox(IVivoxService vivox)
        {
            if (m_VivoxHooked) return;
            vivox.ParticipantAddedToChannel += HandleParticipantAdded;
            vivox.ParticipantRemovedFromChannel += HandleParticipantRemoved;
            m_VivoxHooked = true;
        }

        void RegisterExistingParticipants(IVivoxService vivox)
        {
            try
            {
                if (m_JoinedChannel != null && vivox.ActiveChannels.TryGetValue(m_JoinedChannel, out var list))
                    foreach (var p in list) HandleParticipantAdded(p);
            }
            catch (Exception e) { Log($"Participant scan failed: {e.Message}"); }
        }

        void HandleParticipantAdded(VivoxParticipant p)
        {
            if (!IsMainThread()) { RunOnMainThread(() => HandleParticipantAdded(p)); return; }
            if (p == null || m_ParticipantHandlers.ContainsKey(p)) return;
            if (m_JoinedChannel != null && p.ChannelName != m_JoinedChannel) return;
            if (!TryParseClientId(p.DisplayName, out ulong id))
            {
                Log($"Participant '{p.DisplayName}' has no client id; ignored for speaking.");
                return;
            }
            Action handler = () => RunOnMainThread(() => UpdateSpeaking(id, p.SpeechDetected));
            p.ParticipantSpeechDetected += handler;
            m_ParticipantHandlers[p] = handler;
            UpdateSpeaking(id, p.SpeechDetected);
        }

        void HandleParticipantRemoved(VivoxParticipant p)
        {
            if (!IsMainThread()) { RunOnMainThread(() => HandleParticipantRemoved(p)); return; }
            if (p == null || !m_ParticipantHandlers.TryGetValue(p, out var handler)) return;
            p.ParticipantSpeechDetected -= handler;
            m_ParticipantHandlers.Remove(p);
            if (TryParseClientId(p.DisplayName, out ulong id)) UpdateSpeaking(id, false);
        }

        void UpdateSpeaking(ulong id, bool speaking)
        {
            bool was = IsSpeaking(id);
            if (speaking) m_Speaking[id] = true; else m_Speaking.Remove(id);
            if (was != speaking) Raise(() => OnSpeakingChanged?.Invoke(id, speaking));
        }

        void ClearParticipants(bool raise)
        {
            foreach (var kv in m_ParticipantHandlers)
            {
                try { kv.Key.ParticipantSpeechDetected -= kv.Value; } catch { /* participant already gone */ }
            }
            m_ParticipantHandlers.Clear();
            if (m_Speaking.Count == 0) return;
            var ids = new List<ulong>(m_Speaking.Keys);
            m_Speaking.Clear();
            if (raise)
                foreach (var id in ids) Raise(() => OnSpeakingChanged?.Invoke(id, false));
        }

        // ---------------- helpers ----------------

        void RaiseVoiceStateChanged() => Raise(() => OnVoiceStateChanged?.Invoke());

        void Raise(Action a)
        {
            try { a(); } catch (Exception e) { Debug.LogException(e, this); }
        }

        bool IsMainThread() => Thread.CurrentThread.ManagedThreadId == m_MainThreadId;

        void RunOnMainThread(Action a)
        {
            if (IsMainThread()) a();
            else m_MainThreadQueue.Enqueue(a);
        }

        async Task SafeAsync(Func<Task> op, string what)
        {
            try { await op(); }
            catch (Exception e) { Debug.LogWarning($"[VoiceService] {what} failed: {e.Message}", this); }
        }

        void Log(string msg)
        {
            if (m_VerboseLogging) Debug.Log("[VoiceService] " + msg, this);
        }
    }
}
