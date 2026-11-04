using System;
using System.Threading.Tasks;
using DataViz.Flow;
using DataViz.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Lobby
{
    /// <summary>
    /// The lobby's one large panel. Builds itself (<see cref="LobbyPanelLayout"/>) on a world canvas
    /// under this object and drives it from SessionService, VoiceService and the lobby's TableFrame:
    ///
    /// Setup: name (VR keyboard) and colour, Remote / Co-located, a mic test, Host or Join (keyboard
    /// with recent codes). Session: the host sees the join code, the players arriving and Start
    /// session; a joiner sees "Waiting for host to start", whether they have the host's dataset and
    /// can re-run the import wizard. Remote sessions get a mic toggle (voice is on, mic muted at
    /// start); co-located ones "Align table here". Back returns to the data scene offline.
    /// </summary>
    public class LobbyPanel : MonoBehaviour
    {
        const string k_ModePrefKey = "cubia.mp.mode";

        [SerializeField] UiIcons m_Icons = new();
        [Tooltip("Metres per canvas unit; the layout is 1400 x 860 units.")]
        [SerializeField] float m_MetresPerUnit = 0.0014f;
        [Tooltip("How long a status message stays, in seconds.")]
        [SerializeField] float m_StatusSeconds = 12f;

        LobbyWidgets m_W;
        SessionService m_Session;
        VoiceService m_Voice;
        TableFrame m_Table;

        bool m_Busy;
        string m_Message = "";
        float m_MessageUntil;

        bool InSession => m_Session != null && m_Session.IsInSession;
        bool Connecting => m_Busy || (m_Session != null && m_Session.State == SessionState.Connecting);
        bool KeyboardOpen => m_W.Keyboard.IsOpen;

        static SessionMode PreferredMode =>
            PlayerPrefs.GetInt(k_ModePrefKey, (int)SessionMode.Remote) == (int)SessionMode.CoLocated ? SessionMode.CoLocated : SessionMode.Remote;

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            Canvas canvas = PanelKit.WorldCanvas("LobbyCanvas", transform, LobbyPanelLayout.Size, m_MetresPerUnit);
            m_W = LobbyPanelLayout.Build(canvas.transform, m_Icons);
            Wire();
        }

        void Start()
        {
            PlayerProfile.OnChanged += Refresh;
            m_W.Keyboard.OnClosed += Refresh;
            string pending = MultiplayerFlow.Instance != null ? MultiplayerFlow.Instance.TakeLobbyMessage() : "";
            if (pending.Length > 0) SetStatus(pending);
            BindServices();
            Refresh();
        }

        void OnDestroy()
        {
            PlayerProfile.OnChanged -= Refresh;
            if (m_W != null) m_W.Keyboard.OnClosed -= Refresh;
            BindSession(null);
            BindVoice(null);
            BindTable(null);
        }

        void Update()
        {
            if (BindServices()) Refresh();
            if (m_Message.Length > 0 && Time.unscaledTime > m_MessageUntil)
            {
                m_Message = "";
                RefreshStatus();
            }
        }

        void Wire()
        {
            OnClick(m_W.Back, () => MultiplayerFlow.Instance?.BackToData());
            OnClick(m_W.NameButton, () => m_W.Keyboard.Open("Your name", PlayerProfile.Name, VRKeyboard.Layout.Text, 24, "Save",
                name => PlayerProfile.Name = name));
            for (int i = 0; i < m_W.Swatches.Count; i++)
            {
                int index = i;
                OnClick(m_W.Swatches[i], () => PlayerProfile.ColourIndex = index);
            }
            OnClick(m_W.RemoteButton, () => SetPreferredMode(SessionMode.Remote));
            OnClick(m_W.CoLocatedButton, () => SetPreferredMode(SessionMode.CoLocated));
            OnClick(m_W.MicTestButton, () => m_W.MicMeter.SetRunning(!m_W.MicMeter.IsRunning));
            OnClick(m_W.HostButton, Host);
            OnClick(m_W.JoinButton, OpenJoinKeyboard);

            OnClick(m_W.StartButton, () => MultiplayerFlow.Instance?.StartSession());
            OnClick(m_W.WizardButton, ImportWizardEntry.RunForCurrentDataset);
            OnClick(m_W.MicButton, () => m_Voice?.SetMuted(!m_Voice.IsMuted));
            OnClick(m_W.AlignButton, ToggleAlign);
            OnClick(m_W.LeaveButton, () =>
            {
                if (m_Session != null) RunAsync(m_Session.LeaveAsync, "Couldn't leave cleanly");
            });
        }

        // ------------------------------------------------------------------ services

        /// <summary>Picks up services whose Instance appeared or changed. True when anything changed.</summary>
        bool BindServices()
        {
            bool changed = false;
            if (SessionService.Instance != m_Session) { BindSession(SessionService.Instance); changed = true; }
            if (VoiceService.Instance != m_Voice) { BindVoice(VoiceService.Instance); changed = true; }
            if (TableFrame.Instance != m_Table) { BindTable(TableFrame.Instance); changed = true; }
            return changed;
        }

        void BindSession(SessionService session)
        {
            if (m_Session != null)
            {
                m_Session.OnStateChanged -= OnSessionStateChanged;
                m_Session.OnPlayersChanged -= Refresh;
                m_Session.OnSessionMessage -= SetStatus;
            }
            m_Session = session;
            if (session == null) return;
            session.OnStateChanged += OnSessionStateChanged;
            session.OnPlayersChanged += Refresh;
            session.OnSessionMessage += SetStatus;
        }

        void BindVoice(VoiceService voice)
        {
            if (m_Voice != null)
            {
                m_Voice.OnVoiceStateChanged -= Refresh;
                m_Voice.OnSpeakingChanged -= m_W.Players.SetSpeaking;
            }
            m_Voice = voice;
            if (voice == null) return;
            voice.OnVoiceStateChanged += Refresh;
            voice.OnSpeakingChanged += m_W.Players.SetSpeaking;
        }

        void BindTable(TableFrame table)
        {
            if (m_Table != null) m_Table.OnAlignStepChanged -= Refresh;
            m_Table = table;
            if (table != null) table.OnAlignStepChanged += Refresh;
        }

        void OnSessionStateChanged()
        {
            if (InSession)
            {
                m_W.Keyboard.CloseSilently();
                m_W.MicMeter.SetRunning(false);
            }
            else
            {
                m_W.Players.ClearSpeaking();
                m_Table?.CancelAlign();
            }
            Refresh();
        }

        // ------------------------------------------------------------------ actions

        static void SetPreferredMode(SessionMode mode)
        {
            PlayerPrefs.SetInt(k_ModePrefKey, (int)mode);
            PlayerPrefs.Save();
        }

        void Host()
        {
            if (m_Session == null || InSession) return;
            SessionService session = m_Session;
            SessionMode mode = PreferredMode;
            string dataset = MultiplayerFlow.Instance != null ? MultiplayerFlow.Instance.HostDatasetName : "";
            RunAsync(() => session.HostAsync(mode, dataset), "Couldn't host");
        }

        void OpenJoinKeyboard()
        {
            if (m_Session == null || InSession) return;
            m_W.Keyboard.Open("Join code (6 letters and digits from the host)", "", VRKeyboard.Layout.JoinCode,
                VRKeyboard.CodeLength, "Join", Join, null, m_Session.RecentCodes);
        }

        void Join(string code)
        {
            if (m_Session == null || InSession) return;
            SessionService session = m_Session;
            SetStatus($"Joining {code}…");
            RunAsync(() => session.JoinAsync(code), "Couldn't join");
        }

        void ToggleAlign()
        {
            if (m_Table == null) return;
            if (m_Table.AlignStep == 0) m_Table.BeginAlign();
            else m_Table.CancelAlign();
        }

        async void RunAsync(Func<Task> operation, string failurePrefix)
        {
            m_Busy = true;
            Refresh();
            try
            {
                await operation();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null) SetStatus($"{failurePrefix}: {e.Message}");
            }
            finally
            {
                m_Busy = false;
                if (this != null) Refresh();
            }
        }

        // ------------------------------------------------------------------ status line

        void SetStatus(string message)
        {
            m_Message = message ?? "";
            m_MessageUntil = Time.unscaledTime + m_StatusSeconds;
            RefreshStatus();
        }

        void RefreshStatus()
        {
            string text = m_Message;
            if (text.Length == 0 && Connecting) text = "Connecting…";
            if (text.Length == 0 && m_Session == null) text = "Multiplayer is unavailable (no network object)";
            m_W.Status.text = text;
        }

        // ------------------------------------------------------------------ refresh

        void Refresh()
        {
            if (m_W == null) return;
            bool keyboard = KeyboardOpen;
            SetActive(m_W.SetupPage, !InSession && !keyboard);
            SetActive(m_W.SessionPage, InSession && !keyboard);
            if (InSession) RefreshSessionPage();
            else RefreshSetupPage();
            RefreshStatus();
        }

        void RefreshSetupPage()
        {
            bool idle = !Connecting && m_Session != null;

            PanelKit.LabelOf(m_W.NameButton).text = PlayerProfile.Name;
            m_W.NameButton.interactable = !Connecting;
            for (int i = 0; i < m_W.Swatches.Count; i++)
            {
                m_W.Swatches[i].interactable = !Connecting;
                SetActive(m_W.SwatchMarks[i], i == PlayerProfile.ColourIndex);
            }

            bool coLocated = PreferredMode == SessionMode.CoLocated;
            PanelKit.SetHighlighted(m_W.RemoteButton, !coLocated);
            PanelKit.SetHighlighted(m_W.CoLocatedButton, coLocated);
            m_W.ModeHint.text = coLocated
                ? "Same room: everyone aligns their table to one real table. No voice chat."
                : "Different places: voice chat, each with their own table.";

            bool hasMic = MicLevelMeter.HasMicrophone;
            m_W.MicTestButton.interactable = hasMic;
            PanelKit.LabelOf(m_W.MicTestButton).text = m_W.MicMeter.IsRunning ? "Stop test" : "Test mic";
            m_W.MicTestHint.text = hasMic ? "Speak and watch the bar move. The test stays on this PC." : "No microphone found.";

            m_W.HostButton.interactable = idle;
            m_W.JoinButton.interactable = idle;
            PanelKit.LabelOf(m_W.HostButton).text = Connecting ? "Connecting…" : "Host a session";
            string dataset = MultiplayerFlow.Instance != null ? MultiplayerFlow.Instance.HostDatasetName : "";
            m_W.HostHint.text = dataset.Length > 0 ? $"Runs your dataset: {DisplayName(dataset)}" : "";
        }

        void RefreshSessionPage()
        {
            bool isHost = m_Session.IsHost;
            bool coLocated = m_Session.Mode == SessionMode.CoLocated;
            MenuTheme theme = PanelKit.Theme;

            SetActive(m_W.HostBlock, isHost);
            SetActive(m_W.JoinerBlock, !isHost);
            SetActive(m_W.StartButton.gameObject, isHost);
            SetActive(m_W.SessionCode.gameObject, !isHost);

            m_W.JoinCode.text = string.IsNullOrEmpty(m_Session.JoinCode) ? "------" : m_Session.JoinCode;
            m_W.SessionCode.text = $"Session {m_Session.JoinCode}";

            if (!isHost)
            {
                m_W.WaitingTitle.text = $"Waiting for {HostName()} to start";
                RefreshDatasetCheck(theme);
            }

            m_W.PlayersCaption.text = $"Players  {m_Session.Players.Count}/{SessionService.MaxPlayers}";
            m_W.Players.SetPlayers(m_Session.Players);
            m_W.StartButton.interactable = !m_Busy;

            string dataset = m_Session.SessionDatasetName.Length > 0 ? " · " + DisplayName(m_Session.SessionDatasetName) : "";
            m_W.SessionInfo.text = (coLocated ? "Co-located" : "Remote · voice chat") + dataset;

            bool showMic = !coLocated && m_Voice != null && m_Voice.VoiceEnabled;
            SetActive(m_W.MicButton.gameObject, showMic);
            if (showMic)
            {
                bool muted = m_Voice.IsMuted;
                PanelKit.LabelOf(m_W.MicButton).text = muted ? "Mic muted" : "Mic on";
                Image icon = PanelKit.IconOf(m_W.MicButton);
                if (icon != null) icon.sprite = muted ? m_Icons.MicOff : m_Icons.Mic;
                PanelKit.SetHighlighted(m_W.MicButton, !muted);
            }

            bool showAlign = coLocated && m_Table != null;
            SetActive(m_W.AlignButton.gameObject, showAlign);
            SetActive(m_W.AlignHint.gameObject, showAlign);
            if (showAlign)
            {
                int step = m_Table.AlignStep;
                bool aligned = MultiplayerFlow.Instance != null && MultiplayerFlow.Instance.HasAlignedTable;
                PanelKit.LabelOf(m_W.AlignButton).text = step == 0 ? (aligned ? "Align again" : "Align table here") : "Cancel align";
                m_W.AlignHint.text = step switch
                {
                    1 => "Touch the left front corner of the real table with a controller tip and pull the trigger.",
                    2 => "Now the right front corner.",
                    _ => aligned
                        ? "Table aligned. It stays aligned in the session."
                        : "Line this table up with the real one before anyone sees data: touch its two front corners.",
                };
            }

            PanelKit.LabelOf(m_W.LeaveButton).text = isHost ? "End session" : "Leave";
            m_W.LeaveButton.interactable = !m_Busy;
        }

        void RefreshDatasetCheck(MenuTheme theme)
        {
            string name = m_Session.SessionDatasetName;
            DatasetMatch match = DatasetHash.Compare(name, m_Session.SessionDatasetHash);
            bool ok = match == DatasetMatch.Match;
            m_W.DatasetIcon.sprite = ok ? m_Icons.Check : m_Icons.Warning;
            m_W.DatasetIcon.enabled = match != DatasetMatch.Unknown && m_W.DatasetIcon.sprite != null;
            m_W.DatasetIcon.color = ok ? theme.IconTint : PanelKit.Warning;
            m_W.DatasetCheck.color = ok ? theme.Text : PanelKit.Warning;
            m_W.DatasetCheck.text = match switch
            {
                DatasetMatch.Match => $"You have {DisplayName(name)}",
                DatasetMatch.Missing => $"You need {DisplayName(name)}",
                DatasetMatch.Mismatch => $"You need {DisplayName(name)} (same version as the host)",
                _ => "The host hasn't shared a dataset name",
            };
        }

        string HostName()
        {
            foreach (PlayerInfo p in m_Session.Players)
                if (p.IsHost && !string.IsNullOrWhiteSpace(p.Name)) return p.Name;
            return m_Session.HostName.Length > 0 ? m_Session.HostName : "the host";
        }

        static string DisplayName(string datasetFile) => System.IO.Path.GetFileNameWithoutExtension(datasetFile);

        // ------------------------------------------------------------------ helpers

        void OnClick(Button button, Action action)
        {
            button.onClick.AddListener(() =>
            {
                action();
                Refresh();
            });
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf != active) go.SetActive(active);
        }
    }
}
