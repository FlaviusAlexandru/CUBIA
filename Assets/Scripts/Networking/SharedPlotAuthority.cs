using System;

namespace DataViz.Networking
{
    /// <summary>
    /// STUB — owner: session/state agent. Static facade over the networked plot state, so UI and
    /// presence code never need a reference to the NetworkBehaviour. Offline: everything editable,
    /// presenter mode off, dataset Match.
    /// </summary>
    public static class SharedPlotAuthority
    {
        /// <summary>Host-owned flag: while on, only the host may change the plot (mappings, filters,
        /// glyphs, time, shuffle, dataset, plot transform).</summary>
        public static bool PresenterMode { get; private set; }

        /// <summary>False on a non-host client while presenter mode is on.</summary>
        public static bool LocalPlayerCanEdit => true;

        /// <summary>Whether the local dataset matches the host's (name + file hash). Always Match offline/host.</summary>
        public static DatasetMatch DatasetStatus { get; private set; } = DatasetMatch.Match;

        /// <summary>Dataset name the host has loaded ("" offline).</summary>
        public static string HostDatasetName { get; private set; } = "";

        public static event Action OnPresenterModeChanged;
        public static event Action OnDatasetStatusChanged;

        /// <summary>Host only; ignored elsewhere.</summary>
        public static void RequestSetPresenterMode(bool on) { }

        static void RaiseAll() { OnPresenterModeChanged?.Invoke(); OnDatasetStatusChanged?.Invoke(); }
    }
}
