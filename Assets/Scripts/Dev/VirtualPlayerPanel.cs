#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataViz.Flow;
using DataViz.Lobby;
using DataViz.Networking;
using DataViz.Wizard;
using Unity.Multiplayer.PlayMode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DataViz.Dev
{
    /// <summary>
    /// Editor-only test driver for Multiplayer Play Mode virtual players, which run without a headset
    /// (see VirtualPlayerXrGuard). An IMGUI window walks the multiplayer flow without VR input:
    /// Start scene → data scene → lobby → Host / Join code → Start session → Leave. A simple
    /// desktop camera (hold right mouse to look, WASD to move, Q/E down/up) lets the player look
    /// around so its avatar moves for the other players.
    ///
    /// Appears in virtual players only; set EditorPrefs "CUBIA.Dev.PanelInMainEditor" to show it in
    /// the main editor too. Compiled out of builds.
    /// </summary>
    public sealed class VirtualPlayerPanel : MonoBehaviour
    {
        const string k_MainEditorPref = "CUBIA.Dev.PanelInMainEditor";
        const float k_MoveSpeed = 1.5f;
        const float k_LookSpeed = 0.15f;

        Rect m_Window = new(10, 10, 320, 10);
        string m_JoinCode = "";
        string m_Status = "";
        bool m_Busy;
        int m_DatasetIndex;
        List<string> m_Datasets;
        float m_Pitch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (CurrentPlayer.IsMainEditor && !UnityEditor.EditorPrefs.GetBool(k_MainEditorPref, false)) return;
            var go = new GameObject("[Virtual Player Panel]");
            DontDestroyOnLoad(go);
            go.AddComponent<VirtualPlayerPanel>();
        }

        // ------------------------------------------------------------------ desktop camera

        void Update()
        {
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin == null || origin.Camera == null) return;
            Transform cam = origin.Camera.transform;
            Transform rig = origin.transform;

            // Without XR the floor-origin camera can sit at the floor; lift it to standing height.
            if (cam.position.y - rig.position.y < 0.5f)
                cam.localPosition = new Vector3(cam.localPosition.x, 1.6f, cam.localPosition.z);

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue() * k_LookSpeed;
                rig.Rotate(0f, delta.x, 0f, Space.World);
                m_Pitch = Mathf.Clamp(m_Pitch - delta.y, -80f, 80f);
                cam.localRotation = Quaternion.Euler(m_Pitch, 0f, 0f);
            }

            Keyboard keys = Keyboard.current;
            if (keys == null || GUIUtility.keyboardControl != 0) return;
            Vector3 move = Vector3.zero;
            if (keys.wKey.isPressed) move += Flat(cam.forward);
            if (keys.sKey.isPressed) move -= Flat(cam.forward);
            if (keys.dKey.isPressed) move += Flat(cam.right);
            if (keys.aKey.isPressed) move -= Flat(cam.right);
            if (keys.eKey.isPressed) move += Vector3.up;
            if (keys.qKey.isPressed) move -= Vector3.up;
            rig.position += move * (k_MoveSpeed * Time.unscaledDeltaTime);
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }

        // ------------------------------------------------------------------ panel

        void OnGUI()
        {
            m_Window = GUILayout.Window(0x43554249, m_Window, DrawWindow, "CUBIA virtual player");
        }

        void DrawWindow(int id)
        {
            SessionService session = SessionService.Instance;
            MultiplayerFlow flow = MultiplayerFlow.Instance;
            bool inSession = session != null && session.IsInSession;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            GUILayout.Label($"Scene: {scene}");
            if (session == null)
                GUILayout.Label("No SessionService (network object missing)");
            else
            {
                GUILayout.Label($"Session: {session.State}{(inSession ? (session.IsHost ? " · host" : " · joiner") : "")}" +
                                (string.IsNullOrEmpty(session.JoinCode) ? "" : $" · code {session.JoinCode}"));
                if (inSession)
                {
                    GUILayout.Label($"Mode: {session.Mode} · dataset: {session.SessionDatasetName}");
                    foreach (PlayerInfo p in session.Players)
                        GUILayout.Label($"  • {p.Name}{(p.IsHost ? " (host)" : "")}");
                }
            }

            GUI.enabled = !m_Busy && !SceneFlow.IsLoading;

            if (scene == SceneNames.Start)
                DrawDatasetPicker();
            else if (scene == SceneNames.Data)
            {
                if (!inSession && GUILayout.Button("Enter lobby")) flow?.EnterLobby();
                if (inSession && GUILayout.Button(session.IsHost ? "End session → lobby" : "Leave → lobby")) flow?.LeaveToLobby();
            }
            else if (scene == SceneNames.Lobby && session != null)
            {
                if (!inSession)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Host remote")) Host(session, flow, SessionMode.Remote);
                    if (GUILayout.Button("Host co-located")) Host(session, flow, SessionMode.CoLocated);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    m_JoinCode = GUILayout.TextField(m_JoinCode, 12, GUILayout.Width(120));
                    if (GUILayout.Button("Join")) Run(() => session.JoinAsync(m_JoinCode.Trim().ToUpperInvariant()), "Join");
                    GUILayout.EndHorizontal();
                    if (session.RecentCodes.Count > 0 && GUILayout.Button($"Use last code {session.RecentCodes[0]}"))
                        m_JoinCode = session.RecentCodes[0];

                    if (GUILayout.Button("Back to data")) flow?.BackToData();
                }
                else
                {
                    if (session.IsHost && GUILayout.Button("Start session")) flow?.StartSession();
                    if (GUILayout.Button(session.IsHost ? "End session" : "Leave")) Run(session.LeaveAsync, "Leave");
                }
            }

            GUI.enabled = true;
            if (m_Status.Length > 0) GUILayout.Label(m_Status);
            GUILayout.Label("Right mouse: look · WASD: move · Q/E: down/up");
            GUI.DragWindow();
        }

        void DrawDatasetPicker()
        {
            m_Datasets ??= DatasetCatalog.Files();
            if (m_Datasets.Count == 0)
            {
                GUILayout.Label("No .cdataset files in the data folder");
                return;
            }
            m_DatasetIndex = Mathf.Clamp(m_DatasetIndex, 0, m_Datasets.Count - 1);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(24))) m_DatasetIndex = (m_DatasetIndex + m_Datasets.Count - 1) % m_Datasets.Count;
            GUILayout.Label(m_Datasets[m_DatasetIndex]);
            if (GUILayout.Button(">", GUILayout.Width(24))) m_DatasetIndex = (m_DatasetIndex + 1) % m_Datasets.Count;
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Skip wizard → open data scene"))
            {
                LaunchContext.DatasetName = m_Datasets[m_DatasetIndex];
                LaunchContext.CameFromWizard = true;
                SceneFlow.Load(SceneNames.Data);
            }
        }

        void Host(SessionService session, MultiplayerFlow flow, SessionMode mode)
        {
            string dataset = flow != null ? flow.HostDatasetName : "";
            Run(() => session.HostAsync(mode, dataset), "Host");
        }

        async void Run(Func<Task> operation, string label)
        {
            m_Busy = true;
            m_Status = $"{label}…";
            try
            {
                await operation();
                m_Status = $"{label} done";
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                m_Status = $"{label} failed: {e.Message}";
            }
            finally
            {
                m_Busy = false;
            }
        }
    }
}
#endif
