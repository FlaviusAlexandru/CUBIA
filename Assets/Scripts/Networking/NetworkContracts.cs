using System;
using UnityEngine;

namespace DataViz.Networking
{
    // Shared types for the multiplayer feature (spec: "Spec: multiplayer connectivity").
    // Owned by the integrator. Agents may ADD members here, but must not rename or remove any.

    /// <summary>Remote = people in different places; CoLocated = same room, tables aligned to one real table.</summary>
    public enum SessionMode { Remote = 0, CoLocated = 1 }

    public enum SessionState { Offline, Connecting, Hosting, Client }

    /// <summary>Result of comparing the host's dataset (name + hash) with the local file.</summary>
    public enum DatasetMatch { Unknown, Match, Missing, Mismatch }

    /// <summary>One connected player as shown in the Session island's player list.</summary>
    [Serializable]
    public struct PlayerInfo
    {
        public ulong ClientId;   // NGO client id
        public string Name;
        public Color Colour;
        public bool IsHost;
    }

    /// <summary>
    /// The local player's name and colour, chosen on the Session island before joining and
    /// remembered between runs (PlayerPrefs). Read by presence (nameplate) and voice.
    /// </summary>
    public static class PlayerProfile
    {
        const string k_NameKey = "cubia.mp.name";
        const string k_ColourKey = "cubia.mp.colour";

        /// <summary>Preset colours offered on the Session island.</summary>
        public static readonly Color[] Palette =
        {
            new Color(0.90f, 0.30f, 0.25f), // red
            new Color(0.95f, 0.65f, 0.15f), // orange
            new Color(0.95f, 0.85f, 0.25f), // yellow
            new Color(0.35f, 0.75f, 0.35f), // green
            new Color(0.25f, 0.70f, 0.85f), // cyan
            new Color(0.30f, 0.45f, 0.90f), // blue
            new Color(0.65f, 0.40f, 0.85f), // purple
            new Color(0.90f, 0.45f, 0.70f), // pink
        };

        public static event Action OnChanged;

        public static string Name
        {
            get
            {
                string n = PlayerPrefs.GetString(k_NameKey, "");
                return string.IsNullOrWhiteSpace(n) ? DefaultName : n;
            }
            set
            {
                string clean = (value ?? "").Trim();
                if (clean.Length > 24) clean = clean.Substring(0, 24);
                PlayerPrefs.SetString(k_NameKey, clean);
                PlayerPrefs.Save();
                OnChanged?.Invoke();
            }
        }

        public static int ColourIndex
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(k_ColourKey, 4), 0, Palette.Length - 1);
            set
            {
                PlayerPrefs.SetInt(k_ColourKey, Mathf.Clamp(value, 0, Palette.Length - 1));
                PlayerPrefs.Save();
                OnChanged?.Invoke();
            }
        }

        public static Color Colour => Palette[ColourIndex];

        static string DefaultName => "Player " + (Mathf.Abs(SystemInfo.deviceUniqueIdentifier.GetHashCode()) % 1000).ToString("000");
    }
}
