using System;
using UnityEngine;

namespace DataViz.Flow
{
    /// <summary>
    /// STUB — owner: wizard agent. Called from the Settings panel (menu agent).
    /// </summary>
    public static class ImportWizardEntry
    {
        /// <summary>Settings > Change dataset…: back to the wizard's dataset step.</summary>
        public static void OpenChangeDataset() => Debug.Log("[ImportWizardEntry] not implemented yet");

        /// <summary>Settings > Run import wizard: re-run the column steps for the current dataset
        /// (also allowed in the lobby and during a session; prefs are local).</summary>
        public static void RunForCurrentDataset() => Debug.Log("[ImportWizardEntry] not implemented yet");
    }
}
