using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Adds the networking objects to the open scene. Idempotent: run it again and it only fills
    /// in what is missing. Marks the scene dirty; saving is left to the user. Does not set a
    /// player prefab (the presence setup menu does that).
    /// </summary>
    static class NetworkSetupMenu
    {
        const string k_PrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";

        [MenuItem("Tools/CUBIA/Multiplayer/1 Setup Network")]
        static void SetupNetwork()
        {
            NetworkManager manager = Object.FindAnyObjectByType<NetworkManager>(FindObjectsInactive.Include);
            GameObject network = manager != null ? manager.gameObject : FindOrCreate("Network");

            if (manager == null)
                manager = Undo.AddComponent<NetworkManager>(network);
            UnityTransport transport = GetOrAdd<UnityTransport>(network);
            GetOrAdd<SessionService>(network);

            Undo.RecordObject(manager, "Setup Network");
            manager.NetworkConfig ??= new NetworkConfig();
            manager.NetworkConfig.NetworkTransport = transport;

            var prefabs = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_PrefabsListPath);
            if (prefabs == null)
                Debug.LogWarning($"[NetworkSetupMenu] {k_PrefabsListPath} not found; NetworkPrefabs list left unchanged.");
            else if (!manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(prefabs))
                manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(prefabs);
            EditorUtility.SetDirty(manager);

            // The shared plot state must not sit under the NetworkManager's GameObject.
            var state = Object.FindAnyObjectByType<NetworkScatterplotState>(FindObjectsInactive.Include);
            GameObject stateObject = state != null ? state.gameObject : FindOrCreate("NetworkScatterplotState");
            GetOrAdd<NetworkObject>(stateObject);
            GetOrAdd<NetworkScatterplotState>(stateObject);

            EditorSceneManager.MarkSceneDirty(network.scene);
            Debug.Log("[NetworkSetupMenu] Network setup done (save the scene to keep it).");
        }

        static GameObject FindOrCreate(string name)
        {
            GameObject existing = GameObject.Find(name);
            if (existing != null)
                return existing;

            var created = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(created, "Create " + name);
            return created;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component =>
            go.TryGetComponent(out T existing) ? existing : Undo.AddComponent<T>(go);
    }
}
