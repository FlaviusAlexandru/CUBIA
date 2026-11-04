using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz.Flow
{
    /// <summary>
    /// STUB — owner: wizard agent. Single place that changes scenes, with a fade so VR users never
    /// see a hitch. Offline scene changes only; inside a multiplayer session the lobby agent loads
    /// scenes through NGO's scene manager instead.
    /// </summary>
    public static class SceneFlow
    {
        public static bool IsLoading { get; private set; }

        /// <summary>Raised right before a scene change starts (e.g. to save state).</summary>
        public static event Action<string> OnBeforeLoad;

        public static void Load(string sceneName)
        {
            OnBeforeLoad?.Invoke(sceneName);
            SceneManager.LoadScene(sceneName);
        }
    }
}
