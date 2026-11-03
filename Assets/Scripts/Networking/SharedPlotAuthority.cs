using System;
using System.IO;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Static facade over the networked plot state, so UI and presence code never need a reference
    /// to the NetworkBehaviour. Offline: everything editable, presenter mode off, dataset Match.
    /// NetworkScatterplotState feeds it while spawned and resets it on despawn.
    /// </summary>
    public static class SharedPlotAuthority
    {
        /// <summary>Host-owned flag: while on, only the host may change the plot (mappings, filters,
        /// glyphs, time, shuffle, dataset, plot transform).</summary>
        public static bool PresenterMode { get; private set; }

        /// <summary>False on a non-host client while presenter mode is on.</summary>
        public static bool LocalPlayerCanEdit => !PresenterMode || NetworkScatterplotState.IsLocalHostOrOffline;

        /// <summary>Whether the local dataset matches the host's (name + file hash). Always Match offline/host.</summary>
        public static DatasetMatch DatasetStatus { get; private set; } = DatasetMatch.Match;

        /// <summary>Dataset name the host has loaded ("" offline).</summary>
        public static string HostDatasetName { get; private set; } = "";

        public static event Action OnPresenterModeChanged;
        public static event Action OnDatasetStatusChanged;

        /// <summary>Host only; ignored elsewhere.</summary>
        public static void RequestSetPresenterMode(bool on) => NetworkScatterplotState.Instance?.SetPresenterMode(on);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode without a domain reload keeps statics and stale subscribers.
            PresenterMode = false;
            DatasetStatus = DatasetMatch.Match;
            HostDatasetName = "";
            OnPresenterModeChanged = null;
            OnDatasetStatusChanged = null;
        }

        internal static void SetPresenterMode(bool on)
        {
            if (PresenterMode == on) return;
            PresenterMode = on;
            OnPresenterModeChanged?.Invoke();
        }

        /// <summary>Records the host's dataset and how it compares locally; tells the player when they lack it.</summary>
        internal static void SetDatasetStatus(DatasetMatch status, string hostDatasetName)
        {
            hostDatasetName ??= "";
            if (status == DatasetStatus && hostDatasetName == HostDatasetName) return;

            DatasetStatus = status;
            HostDatasetName = hostDatasetName;

            if (status == DatasetMatch.Missing || status == DatasetMatch.Mismatch)
                SessionService.Instance?.PostMessage($"You need {Path.GetFileNameWithoutExtension(hostDatasetName)} (same version as the host)");

            OnDatasetStatusChanged?.Invoke();
        }

        /// <summary>Back to offline defaults (session left or ended).</summary>
        internal static void ResetToOffline()
        {
            SetPresenterMode(false);
            SetDatasetStatus(DatasetMatch.Match, "");
        }
    }
}
