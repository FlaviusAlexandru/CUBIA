using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz.Networking.EditorTools
{
    /// <summary>
    /// Idempotent scene wiring for voice: adds a VoiceService to the "Network" GameObject created by
    /// "Tools/CUBIA/Multiplayer/1 Setup Network". Marks the scene dirty; does not save.
    /// </summary>
    static class VoiceSetupMenu
    {
        const string k_NetworkObjectName = "Network";

        [MenuItem("Tools/CUBIA/Multiplayer/3 Setup Voice")]
        static void SetupVoice()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[CUBIA Voice Setup] Exit Play mode before running this.");
                return;
            }

            GameObject network = FindNetworkObject();
            if (network == null)
            {
                Debug.LogError($"[CUBIA Voice Setup] No GameObject named \"{k_NetworkObjectName}\" in the open scene(s). " +
                               "Run \"Tools/CUBIA/Multiplayer/1 Setup Network\" first.");
                return;
            }

            var existing = Object.FindObjectsByType<VoiceService>(FindObjectsInactive.Include);
            foreach (var v in existing)
            {
                if (v.gameObject != network)
                    Debug.LogWarning($"[CUBIA Voice Setup] Another VoiceService exists on \"{v.gameObject.name}\"; " +
                                     "only one is used at runtime (singleton). Consider removing it.", v);
            }

            if (network.GetComponent<VoiceService>() != null)
            {
                Debug.Log("[CUBIA Voice Setup] VoiceService already present on \"Network\". Nothing to do.", network);
                return;
            }

            Undo.AddComponent<VoiceService>(network);
            EditorSceneManager.MarkSceneDirty(network.scene);
            Selection.activeGameObject = network;
            Debug.Log("[CUBIA Voice Setup] Added VoiceService to \"Network\". Scene marked dirty (not saved).", network);
        }

        static GameObject FindNetworkObject()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name == k_NetworkObjectName) return root;
                }
            }
            // Fallback: not a root object.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t.name == k_NetworkObjectName && !EditorUtility.IsPersistent(t.gameObject)) return t.gameObject;
            }
            return null;
        }
    }
}
