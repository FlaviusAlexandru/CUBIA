using DataViz.Lobby;
using DataViz.Networking;
using UnityEngine;

namespace DataViz.Flow
{
    /// <summary>
    /// The Multiplayer button (Settings tab and wrist menu): asks "Go to Multiplayer?" and then
    /// takes the player to the lobby, remembering their plot for when they come back.
    /// </summary>
    public static class MultiplayerEntry
    {
        public static void RequestEnterLobby()
        {
            if (MultiplayerFlow.IsActiveScene(SceneNames.Lobby))
                return;
            if (SessionService.Instance != null && SessionService.Instance.IsInSession)
            {
                Debug.Log("[MultiplayerEntry] Already in a session; use the session strip to leave.");
                return;
            }
            if (MultiplayerFlow.Instance == null)
            {
                Debug.LogWarning("[MultiplayerEntry] Multiplayer is unavailable (no network bootstrap). " +
                                 "Run Tools/CUBIA/Multiplayer/5 Move Network To Bootstrap.");
                return;
            }

            ConfirmDialog.Show("Go to Multiplayer?",
                "You'll leave this view and enter the lobby. Your plot stays as it is.",
                "Go to lobby", "Cancel",
                () => MultiplayerFlow.Instance?.EnterLobby());
        }
    }
}
