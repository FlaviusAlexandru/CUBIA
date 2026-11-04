using DataViz.Lobby;
using Unity.Netcode;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Creates the persistent network object once, whichever scene starts first (wizard, data scene,
    /// lobby). It is the prefab Resources/CUBIANetwork (NetworkManager with scene management on,
    /// UnityTransport, SessionService, VoiceService), made from the data scene's old "Network"
    /// object by Tools/CUBIA/Multiplayer/5 Move Network To Bootstrap. NetworkManager puts its own
    /// GameObject in DontDestroyOnLoad, so NGO, the session and voice survive every scene change.
    ///
    /// A scene that still has its own NetworkManager (before menu 5 ran) keeps using that one.
    /// Either way the object also gets the <see cref="MultiplayerFlow"/> that moves between the
    /// data scene and the lobby.
    /// </summary>
    public static class NetworkBootstrap
    {
        public const string PrefabResourcePath = "CUBIANetwork";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureNetworkObject()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
            {
                var prefab = Resources.Load<GameObject>(PrefabResourcePath);
                if (prefab == null)
                {
                    Debug.LogWarning($"[NetworkBootstrap] Resources/{PrefabResourcePath} not found and no NetworkManager in the scene: " +
                                     "multiplayer is unavailable. Run Tools/CUBIA/Multiplayer/5 Move Network To Bootstrap.");
                    return;
                }
                GameObject instance = Object.Instantiate(prefab);
                instance.name = prefab.name;
                network = instance.GetComponent<NetworkManager>();
            }

            if (!network.TryGetComponent(out MultiplayerFlow _))
                network.gameObject.AddComponent<MultiplayerFlow>();
        }
    }
}
