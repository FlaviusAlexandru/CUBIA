using System;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// STUB — owner: voice agent. Vivox voice for a session. Joins a channel named after
    /// SessionService.SessionId when a session starts and leaves when it ends (subscribe to
    /// SessionService.OnStateChanged). Microphone muted at start. Positional channel in Remote mode;
    /// in CoLocated mode voice is disabled by default (people hear each other in the room).
    /// Each participant's Vivox display name is "p{NGO ClientId}" so presence can map speakers.
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

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public void SetMuted(bool muted) { }
        public void SetVoiceEnabled(bool enabled) { }
        public bool IsSpeaking(ulong clientId) => false;

        void Unused() { OnVoiceStateChanged?.Invoke(); OnSpeakingChanged?.Invoke(0, false); }
    }
}
