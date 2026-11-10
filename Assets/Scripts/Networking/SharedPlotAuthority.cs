using System;
using System.Collections.Generic;
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

        /// <summary>Whether every layer's dataset matches the host's (name + file hash). Always Match offline/host.</summary>
        public static DatasetMatch DatasetStatus { get; private set; } = DatasetMatch.Match;

        /// <summary>The host's main dataset: its active layer's (a computed one's source's), else the first named ("" offline or on an empty plot).</summary>
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
            s_Told.Clear();
            OnPresenterModeChanged = null;
            OnDatasetStatusChanged = null;
        }

        internal static void SetPresenterMode(bool on)
        {
            if (PresenterMode == on) return;
            PresenterMode = on;
            OnPresenterModeChanged?.Invoke();
        }

        // Dataset names this player has already been told they lack (once per dataset per session).
        static readonly HashSet<string> s_Told = new();

        /// <summary>
        /// Records the stack's status and the host's main dataset, and tells the player once per
        /// dataset when they lack one ("You need …"). problems: the datasets that are missing or
        /// differ here; null means hostDatasetName when status is Missing or Mismatch.
        /// Returns the names told about now (the caller reports them to the host).
        /// </summary>
        internal static List<string> SetDatasetStatus(DatasetMatch status, string hostDatasetName, IReadOnlyList<string> problems = null)
        {
            hostDatasetName ??= "";
            if (problems == null && (status == DatasetMatch.Missing || status == DatasetMatch.Mismatch))
                problems = new[] { hostDatasetName };

            List<string> told = TakeNotYetTold(s_Told, problems);
            foreach (string name in told)
                SessionService.Instance?.PostMessage($"You need {Path.GetFileNameWithoutExtension(name)} (same version as the host)");

            if (status != DatasetStatus || hostDatasetName != HostDatasetName)
            {
                DatasetStatus = status;
                HostDatasetName = hostDatasetName;
                OnDatasetStatusChanged?.Invoke();
            }
            return told;
        }

        /// <summary>The names in names that are not in told yet (non-empty, distinct, in order); adds them to told.</summary>
        public static List<string> TakeNotYetTold(ISet<string> told, IEnumerable<string> names)
        {
            var fresh = new List<string>();
            if (names == null)
                return fresh;
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(name) && told.Add(name))
                    fresh.Add(name);
            }
            return fresh;
        }

        /// <summary>Back to offline defaults (session left or ended).</summary>
        internal static void ResetToOffline()
        {
            SetPresenterMode(false);
            SetDatasetStatus(DatasetMatch.Match, "");
            s_Told.Clear();
        }
    }
}
