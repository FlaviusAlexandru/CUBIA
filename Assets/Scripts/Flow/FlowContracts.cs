using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataViz.Flow
{
    // Shared contracts for the "four-tile menu, import wizard and multiplayer lobby" spec.
    // Owned by the integrator: agents may ADD members, never rename or remove one.

    /// <summary>Scene names, as listed in Build Settings (Start is build index 0).</summary>
    public static class SceneNames
    {
        public const string Start = "Start";        // import wizard (feature 2)
        public const string Data = "SampleScene";   // the plot (existing scene)
        public const string Lobby = "Lobby";        // multiplayer lobby (feature 3)
    }

    /// <summary>
    /// What the data scene should load when it starts. Set by the wizard (or the default-on-load
    /// check) before loading the data scene. Empty DatasetName = keep the manager's own default,
    /// so opening SampleScene directly in the editor still works as today.
    /// </summary>
    public static class LaunchContext
    {
        public static string DatasetName = "";
        public static bool CameFromWizard;
    }
}
