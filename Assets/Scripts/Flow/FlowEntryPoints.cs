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

    /// <summary>
    /// STUB — owner: lobby agent. Called from the Settings panel and the wrist menu (menu agent).
    /// Shows the "Go to Multiplayer?" ConfirmDialog, then goes to the lobby.
    /// </summary>
    public static class MultiplayerEntry
    {
        public static void RequestEnterLobby() => Debug.Log("[MultiplayerEntry] not implemented yet");
    }

    /// <summary>
    /// STUB — owner: lobby agent. Small reusable world-space modal (title, message, two buttons).
    /// Cancel is the default; a ray click outside cancels.
    /// </summary>
    public static class ConfirmDialog
    {
        public static void Show(string title, string message, string confirmLabel, string cancelLabel,
                                Action onConfirm, Action onCancel = null)
        {
            Debug.Log($"[ConfirmDialog] not implemented yet: {title}");
        }
    }
}
