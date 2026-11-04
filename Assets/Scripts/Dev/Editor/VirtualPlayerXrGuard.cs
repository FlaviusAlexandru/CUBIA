using Unity.Multiplayer.PlayMode;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine;

namespace DataViz.Dev
{
    /// <summary>
    /// Keeps OpenXR off in Multiplayer Play Mode virtual players. Every MPPM clone shares the main
    /// project's XR settings, so without this a clone starts its own OpenXR session on the same
    /// SteamVR/VIVE runtime as the main editor; the runtime only serves one app, so the clone's
    /// session is shut down (and can disturb the main editor's). Virtual players run on the desktop
    /// and are driven through <see cref="VirtualPlayerPanel"/> instead.
    ///
    /// Only the in-memory settings are changed (never marked dirty or saved), so the shared asset
    /// on disk, and the main editor, keep "Initialize XR on Startup".
    /// </summary>
    [InitializeOnLoad]
    static class VirtualPlayerXrGuard
    {
        static VirtualPlayerXrGuard()
        {
            if (CurrentPlayer.IsMainEditor) return;
            EditorApplication.delayCall += DisableXrOnStart;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) DisableXrOnStart();
            };
        }

        static void DisableXrOnStart()
        {
            Disable(BuildTargetGroup.Standalone);
            Disable(EditorUserBuildSettings.selectedBuildTargetGroup);
        }

        static void Disable(BuildTargetGroup group)
        {
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            if (settings == null || !settings.InitManagerOnStart) return;
            settings.InitManagerOnStart = false;
            Debug.Log($"[CUBIA] Virtual player: XR start-up disabled for {group} (desktop mode, use the Virtual Player panel).");
        }
    }
}
