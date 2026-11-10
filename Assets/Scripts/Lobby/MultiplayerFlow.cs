using System.Collections;
using System.Threading.Tasks;
using DataViz.Flow;
using DataViz.Networking;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz.Lobby
{
    /// <summary>
    /// Moves the player between the data scene and the lobby, and carries what must survive that:
    /// the player's own offline plot (captured on the way into the lobby, restored on Back or after
    /// leaving a session), the table pose aligned in a co-located lobby, and a message for the lobby
    /// ("Session ended"). Lives on the persistent network object (added by <see cref="NetworkBootstrap"/>).
    ///
    /// Offline scene changes go through SceneFlow; inside a session the host's NGO scene manager
    /// loads the data scene for everyone (Start session), and late joiners are taken into it by NGO's
    /// client synchronisation.
    /// </summary>
    public class MultiplayerFlow : MonoBehaviour
    {
        public static MultiplayerFlow Instance { get; private set; }

        const float k_RestoreTimeout = 15f;

        SessionService m_Session;
        bool m_WasInSession;
        bool m_LeavingOnPurpose;
        string m_LobbyMessage = "";

        PlotSnapshot m_OfflinePlot;
        bool m_RestorePlotOnDataLoad;

        bool m_HasAlignedTable;
        Pose m_AlignedTableInRig; // table pose relative to the XR origin, i.e. to the real room
        TableFrame m_LobbyTable;

        /// <summary>True when the player came to the lobby from a plot that Back restores.</summary>
        public bool HasOfflinePlot => m_OfflinePlot != null;

        /// <summary>Dataset a session hosted from the lobby runs: the main dataset of the plot the player came from, else the launch choice.</summary>
        public string HostDatasetName
        {
            get
            {
                string main = NetworkScatterplotState.MainDatasetName(m_OfflinePlot);
                return main.Length > 0 ? main : LaunchContext.DatasetName ?? "";
            }
        }

        /// <summary>True once the table was aligned to the real table in this lobby visit or an earlier one.</summary>
        public bool HasAlignedTable => m_HasAlignedTable;

        public static bool IsActiveScene(string sceneName) => SceneManager.GetActiveScene().name == sceneName;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            BindSession(null);
            BindLobbyTable(null);
        }

        // The session service sits on the same persistent object, but bind lazily in case it is added later.
        void Update()
        {
            if (m_Session == null && SessionService.Instance != null)
                BindSession(SessionService.Instance);
        }

        // ------------------------------------------------------------------ transitions

        /// <summary>Data scene → lobby (after the ConfirmDialog). Remembers the current plot for Back.</summary>
        public void EnterLobby()
        {
            if (m_Session != null && m_Session.IsInSession) return;

            // The whole layer stack, an emptied one too (Q13), once Start has loaded the plot.
            MultiplayerScatterplotManager manager = MultiplayerScatterplotManager.Instance;
            if (manager != null && (manager.HasInitialised || manager.LoadedDataset != null))
                m_OfflinePlot = PlotSnapshot.Capture(manager);
            SceneFlow.Load(SceneNames.Lobby);
        }

        /// <summary>Lobby → data scene, offline: leaves any session and puts the player's own plot back.</summary>
        public async void BackToData()
        {
            await LeaveSessionAsync();
            RestoreOfflinePlotOnNextLoad();
            SceneFlow.Load(SceneNames.Data);
        }

        /// <summary>Host in the lobby: loads the data scene for everyone, with the host's plot as the shared one.</summary>
        public void StartSession()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (m_Session == null || !m_Session.IsHost || network == null) return;
            if (network.SceneManager == null)
            {
                m_Session.PostMessage("Can't start: NGO scene management is off (run Tools/CUBIA/Multiplayer/5)");
                return;
            }

            RestoreOfflinePlotOnNextLoad();
            SceneEventProgressStatus status = network.SceneManager.LoadScene(SceneNames.Data, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                m_RestorePlotOnDataLoad = false;
                m_Session.PostMessage($"Couldn't start the session ({status})");
            }
        }

        /// <summary>Data scene during a session → lobby. A host leaving ends the session for everyone.</summary>
        public async void LeaveToLobby()
        {
            await LeaveSessionAsync();
            SceneFlow.Load(SceneNames.Lobby);
        }

        /// <summary>Returns (and clears) the message the lobby should show when it opens.</summary>
        public string TakeLobbyMessage()
        {
            string message = m_LobbyMessage;
            m_LobbyMessage = "";
            return message;
        }

        async Task LeaveSessionAsync()
        {
            if (m_Session == null || !m_Session.IsInSession) return;
            m_LeavingOnPurpose = true;
            try { await m_Session.LeaveAsync(); }
            finally { m_LeavingOnPurpose = false; }
        }

        void RestoreOfflinePlotOnNextLoad()
        {
            if (m_OfflinePlot == null) return;
            string main = NetworkScatterplotState.MainDatasetName(m_OfflinePlot);
            if (main.Length > 0) LaunchContext.DatasetName = main;
            LaunchContext.CameFromWizard = false;
            m_RestorePlotOnDataLoad = true;
        }

        // ------------------------------------------------------------------ session tracking

        void BindSession(SessionService session)
        {
            if (m_Session != null)
            {
                m_Session.OnStateChanged -= OnSessionStateChanged;
                m_Session.OnSessionMessage -= OnSessionMessage;
            }
            m_Session = session;
            if (session == null) return;
            session.OnStateChanged += OnSessionStateChanged;
            session.OnSessionMessage += OnSessionMessage;
            m_WasInSession = session.IsInSession;
        }

        void OnSessionStateChanged()
        {
            bool inSession = m_Session.IsInSession;
            if (inSession && !m_WasInSession && !m_Session.IsHost)
            {
                // A joiner's data scene should start with the host's dataset rather than its own default.
                if (m_Session.SessionDatasetName.Length > 0)
                {
                    LaunchContext.DatasetName = m_Session.SessionDatasetName;
                    LaunchContext.CameFromWizard = false;
                }
                // A late joiner may already be in the data scene by the time the join completes.
                if (IsActiveScene(SceneNames.Data))
                    ApplyAlignedTable();
            }

            bool endedUnexpectedly = m_WasInSession && !inSession && !m_LeavingOnPurpose;
            m_WasInSession = inSession;

            // The host left or the connection dropped while looking at the shared plot: back to the lobby.
            if (endedUnexpectedly && IsActiveScene(SceneNames.Data))
            {
                if (m_LobbyMessage.Length == 0) m_LobbyMessage = "The session ended";
                SceneFlow.Load(SceneNames.Lobby);
            }
        }

        // Session messages raised outside the lobby (e.g. "Session ended" in the data scene) are shown when it opens.
        void OnSessionMessage(string message)
        {
            if (!IsActiveScene(SceneNames.Lobby))
                m_LobbyMessage = message ?? "";
        }

        // ------------------------------------------------------------------ scene arrival

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;

            if (scene.name == SceneNames.Lobby)
            {
                BindLobbyTable(TableFrame.Instance);
            }
            else if (scene.name == SceneNames.Data)
            {
                BindLobbyTable(null);
                ApplyAlignedTable();

                bool following = m_Session != null && m_Session.IsInSession && !m_Session.IsHost;
                if (m_RestorePlotOnDataLoad && !following)
                    StartCoroutine(RestorePlot(m_OfflinePlot));
                m_RestorePlotOnDataLoad = false;
            }
        }

        /// <summary>
        /// Applies the offline plot (the whole layer stack) once the manager's Start has loaded its
        /// start-up dataset. Each layer resolves its own dataset; one whose file is gone is kept but
        /// hidden. If every dataset of a non-empty plot is gone, what Start loaded is kept instead.
        /// </summary>
        static IEnumerator RestorePlot(PlotSnapshot snapshot)
        {
            float giveUp = Time.unscaledTime + k_RestoreTimeout;
            MultiplayerScatterplotManager manager;
            while ((manager = MultiplayerScatterplotManager.Instance) == null || !manager.HasInitialised)
            {
                if (Time.unscaledTime > giveUp) yield break;
                yield return null;
            }

            bool namesAny = false, hasAny = false;
            foreach (LayerSnapshot layer in snapshot.Layers)
            {
                if (layer == null || string.IsNullOrEmpty(layer.DatasetName)) continue;
                namesAny = true;
                hasAny |= DatasetHash.Of(layer.DatasetName).Length > 0;
            }
            if (namesAny && !hasAny) yield break; // files gone: keep what loaded

            manager.ApplyNetworkSnapshot(snapshot, snapshot.TimeScrub);
        }

        // ------------------------------------------------------------------ co-located table

        void BindLobbyTable(TableFrame table)
        {
            if (m_LobbyTable != null) m_LobbyTable.OnTableMoved -= RememberAlignedTable;
            m_LobbyTable = table;
            if (table != null) table.OnTableMoved += RememberAlignedTable;
        }

        // The lobby table only moves by Align, so every move is an alignment to the real table.
        void RememberAlignedTable()
        {
            Transform rig = RigTransform();
            if (rig == null || m_LobbyTable == null) return;
            Transform table = m_LobbyTable.transform;
            m_AlignedTableInRig = new Pose(rig.InverseTransformPoint(table.position), Quaternion.Inverse(rig.rotation) * table.rotation);
            m_HasAlignedTable = true;
        }

        void ApplyAlignedTable()
        {
            bool coLocated = m_Session != null && m_Session.IsInSession && m_Session.Mode == SessionMode.CoLocated;
            Transform rig = RigTransform();
            if (!coLocated || !m_HasAlignedTable || rig == null || TableFrame.Instance == null) return;
            TableFrame.Instance.SetPose(new Pose(rig.TransformPoint(m_AlignedTableInRig.position), rig.rotation * m_AlignedTableInRig.rotation));
        }

        static Transform RigTransform()
        {
            var origin = FindAnyObjectByType<XROrigin>();
            return origin != null ? origin.transform : null;
        }
    }
}
