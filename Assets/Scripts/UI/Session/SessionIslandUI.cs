using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// Drives the summoned menu's Session island (built by Tools > CUBIA > Multiplayer >
    /// 4 Setup Session Island from duplicated menu rows).
    ///
    /// Offline page: name (VR keyboard) and colour swatches (PlayerProfile, remembered), mode
    /// Remote / Co-located (remembered), Host session, Join (VR keyboard for the 6-character code,
    /// with recent codes). Session page: join code, player list, "Last change: name", Leave,
    /// microphone and voice toggles, Place table, Align (co-located only, with the corner prompts)
    /// and Presenter mode (host only). A status line shows SessionService.OnSessionMessage and
    /// call failures.
    ///
    /// Every service is optional: SessionService, VoiceService and TableFrame are picked up
    /// whenever their Instance appears (or changes) and their controls are hidden or disabled
    /// while missing. Lives on an always-active object (not on the summoned canvas, which is
    /// inactive until summoned) so messages and state are tracked from the start.
    /// </summary>
    public class SessionIslandUI : MonoBehaviour
    {
        private const string k_ModePrefKey = "cubia.mp.mode";
        private const string k_Dash = "—";

        [Header("Pages")]
        public GameObject m_OfflinePage;
        public GameObject m_SessionPage;
        public VRKeyboard m_Keyboard;

        [Header("Offline page")]
        public Button m_NameButton;
        public TMP_Text m_NameButtonLabel;
        public List<Button> m_ColourSwatches = new();
        public List<Image> m_ColourFills = new();
        public List<GameObject> m_ColourSelectedMarks = new();
        [Tooltip("On = Co-located, off = Remote.")]
        public Toggle m_CoLocatedToggle;
        public TMP_Text m_ModeLabel;
        public TMP_Text m_ModeHint;
        public Button m_HostButton;
        public TMP_Text m_HostButtonLabel;
        public Button m_JoinButton;

        [Header("Session page")]
        public TMP_Text m_JoinCodeText;
        public TMP_Text m_RoleText;
        public PlayerListView m_PlayerList;
        public TMP_Text m_LastChangedText;
        public Button m_LeaveButton;

        [Header("Session page - right column (hidden rows close up)")]
        public List<RectTransform> m_SessionRows = new();
        public float m_SessionRowSpacing = 8f;
        public GameObject m_MicRow;
        public Toggle m_MicToggle;
        public TMP_Text m_MicLabel;
        public GameObject m_VoiceRow;
        public Toggle m_VoiceToggle;
        public TMP_Text m_VoiceLabel;
        public GameObject m_PlaceTableRow;
        public Toggle m_PlaceTableToggle;
        public TMP_Text m_PlaceTableLabel;
        public GameObject m_PresenterRow;
        public Toggle m_PresenterToggle;
        public TMP_Text m_PresenterLabel;
        public GameObject m_AlignRow;
        public Button m_AlignButton;
        public TMP_Text m_AlignButtonLabel;
        public TMP_Text m_AlignHintText;

        [Header("Status")]
        public TMP_Text m_StatusText;
        [Tooltip("How long a session message stays on the status line, in seconds.")]
        public float m_StatusSeconds = 15f;

        private SessionService m_Session;
        private VoiceService m_Voice;
        private TableFrame m_Table;

        private bool m_Started;
        private bool m_Refreshing;
        private bool m_Busy;
        private bool m_LastInSession;
        private string m_Message = "";
        private float m_MessageUntil;
        private float m_RowsTop = float.NaN;

        private bool InSession => m_Session != null && m_Session.IsInSession;
        private bool KeyboardOpen => m_Keyboard != null && m_Keyboard.IsOpen;
        private bool CoLocatedPref => PlayerPrefs.GetInt(k_ModePrefKey, (int)SessionMode.Remote) == (int)SessionMode.CoLocated;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            if (m_Keyboard != null)
            {
                m_Keyboard.gameObject.SetActive(false);
                m_Keyboard.OnClosed += RefreshAll;
            }

            AddClick(m_NameButton, OpenNameKeyboard);
            for (int i = 0; i < m_ColourSwatches.Count; i++)
            {
                int index = i;
                AddClick(m_ColourSwatches[i], () => PlayerProfile.ColourIndex = index);
            }
            AddToggle(m_CoLocatedToggle, on =>
            {
                PlayerPrefs.SetInt(k_ModePrefKey, on ? (int)SessionMode.CoLocated : (int)SessionMode.Remote);
                PlayerPrefs.Save();
            });
            AddClick(m_HostButton, Host);
            AddClick(m_JoinButton, OpenJoinKeyboard);

            AddClick(m_LeaveButton, Leave);
            AddToggle(m_MicToggle, on => { if (m_Voice != null) m_Voice.SetMuted(!on); });
            AddToggle(m_VoiceToggle, on => { if (m_Voice != null) m_Voice.SetVoiceEnabled(on); });
            AddToggle(m_PlaceTableToggle, on => { if (m_Table != null) m_Table.SetPlacing(on); });
            AddToggle(m_PresenterToggle, SharedPlotAuthority.RequestSetPresenterMode);
            AddClick(m_AlignButton, OnAlignClicked);

            PlayerProfile.OnChanged += RefreshAll;
            SharedPlotAuthority.OnPresenterModeChanged += RefreshAll;
            SharedPlotAuthority.OnDatasetStatusChanged += RefreshAll;

            m_Started = true;
            BindServices(force: true);
            RefreshAll();
        }

        private void OnDestroy()
        {
            PlayerProfile.OnChanged -= RefreshAll;
            SharedPlotAuthority.OnPresenterModeChanged -= RefreshAll;
            SharedPlotAuthority.OnDatasetStatusChanged -= RefreshAll;
            if (m_Keyboard != null)
                m_Keyboard.OnClosed -= RefreshAll;
            BindSession(null);
            BindVoice(null);
            BindTable(null);
        }

        private void Update()
        {
            if (!m_Started)
                return;

            BindServices(force: false);

            if (!string.IsNullOrEmpty(m_Message) && Time.unscaledTime > m_MessageUntil)
            {
                m_Message = "";
                RefreshStatus();
            }
        }

        // ------------------------------------------------------------------ service binding

        private void BindServices(bool force)
        {
            bool changed = false;

            SessionService session = SessionService.Instance;
            if (force || !ReferenceEquals(session, m_Session) && session != m_Session)
            {
                BindSession(session);
                changed = true;
            }

            VoiceService voice = VoiceService.Instance;
            if (force || !ReferenceEquals(voice, m_Voice) && voice != m_Voice)
            {
                BindVoice(voice);
                changed = true;
            }

            TableFrame table = TableFrame.Instance;
            if (force || !ReferenceEquals(table, m_Table) && table != m_Table)
            {
                BindTable(table);
                changed = true;
            }

            if (changed && !force)
                RefreshAll();
        }

        private void BindSession(SessionService session)
        {
            if (!ReferenceEquals(m_Session, null))
            {
                m_Session.OnStateChanged -= OnSessionStateChanged;
                m_Session.OnPlayersChanged -= RefreshSessionPage;
                m_Session.OnSessionMessage -= SetStatus;
            }

            m_Session = session;

            if (session != null)
            {
                session.OnStateChanged += OnSessionStateChanged;
                session.OnPlayersChanged += RefreshSessionPage;
                session.OnSessionMessage += SetStatus;
            }
        }

        private void BindVoice(VoiceService voice)
        {
            if (!ReferenceEquals(m_Voice, null))
            {
                m_Voice.OnVoiceStateChanged -= RefreshVoice;
                m_Voice.OnSpeakingChanged -= OnSpeakingChanged;
            }

            m_Voice = voice;

            if (voice != null)
            {
                voice.OnVoiceStateChanged += RefreshVoice;
                voice.OnSpeakingChanged += OnSpeakingChanged;
            }
        }

        private void BindTable(TableFrame table)
        {
            if (!ReferenceEquals(m_Table, null))
            {
                m_Table.OnPlacingChanged -= RefreshTable;
                m_Table.OnAlignStepChanged -= RefreshTable;
            }

            m_Table = table;

            if (table != null)
            {
                table.OnPlacingChanged += RefreshTable;
                table.OnAlignStepChanged += RefreshTable;
            }
        }

        private void OnSessionStateChanged()
        {
            bool inSession = InSession;
            if (inSession != m_LastInSession)
            {
                m_LastInSession = inSession;
                if (inSession && m_Keyboard != null)
                    m_Keyboard.CloseSilently();
                if (!inSession && m_PlayerList != null)
                    m_PlayerList.ClearSpeaking();
            }
            RefreshAll();
        }

        private void OnSpeakingChanged(ulong clientId, bool speaking)
        {
            if (m_PlayerList != null)
                m_PlayerList.SetSpeaking(clientId, speaking);
        }

        // ------------------------------------------------------------------ actions

        private void OpenNameKeyboard()
        {
            if (m_Keyboard == null || InSession)
                return;

            m_Keyboard.Open("Your name", PlayerProfile.Name, VRKeyboard.Layout.Text, 24, "Save",
                name => PlayerProfile.Name = name);
            RefreshAll();
        }

        private void OpenJoinKeyboard()
        {
            if (m_Keyboard == null || m_Session == null || InSession)
                return;

            m_Keyboard.Open("Join code (6 letters/digits from the host)", "", VRKeyboard.Layout.JoinCode,
                VRKeyboard.CodeLength, "Connect", Join, null, m_Session.RecentCodes);
            RefreshAll();
        }

        private void Host()
        {
            if (m_Session == null || InSession)
                return;

            SessionMode mode = CoLocatedPref ? SessionMode.CoLocated : SessionMode.Remote;
            SessionService session = m_Session;
            RunAsync(() => session.HostAsync(mode), "Couldn't host");
        }

        private void Join(string code)
        {
            if (m_Session == null || InSession)
                return;

            SessionService session = m_Session;
            SetStatus($"Joining {code}…");
            RunAsync(() => session.JoinAsync(code), "Couldn't join");
        }

        private void Leave()
        {
            if (m_Session == null)
                return;

            SessionService session = m_Session;
            RunAsync(session.LeaveAsync, "Couldn't leave cleanly");
        }

        private void OnAlignClicked()
        {
            if (m_Table == null)
                return;

            if (m_Table.AlignStep == 0) m_Table.BeginAlign();
            else m_Table.CancelAlign();
            RefreshTable();
        }

        private async void RunAsync(Func<Task> operation, string failurePrefix)
        {
            m_Busy = true;
            RefreshAll();

            try
            {
                Task task = operation();
                if (task != null)
                    await task;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null)
                    SetStatus($"{failurePrefix}: {e.Message}");
            }
            finally
            {
                m_Busy = false;
                if (this != null)
                    RefreshAll();
            }
        }

        // ------------------------------------------------------------------ status line

        /// <summary>Shows a one-line message on the island for m_StatusSeconds.</summary>
        public void SetStatus(string message)
        {
            m_Message = message ?? "";
            m_MessageUntil = Time.unscaledTime + Mathf.Max(1f, m_StatusSeconds);
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (m_StatusText == null)
                return;

            string text = m_Message;

            if (string.IsNullOrEmpty(text) && InSession && SharedPlotAuthority.DatasetStatus is DatasetMatch.Missing or DatasetMatch.Mismatch)
            {
                string dataset = string.IsNullOrEmpty(SharedPlotAuthority.HostDatasetName) ? "the host's dataset" : SharedPlotAuthority.HostDatasetName;
                text = $"You need {dataset} (same version as the host)";
            }

            if (string.IsNullOrEmpty(text) && (m_Busy || (m_Session != null && m_Session.State == SessionState.Connecting)))
                text = "Connecting…";

            if (string.IsNullOrEmpty(text) && m_Session == null)
                text = "Multiplayer is unavailable (no session service in this scene)";

            m_StatusText.text = text;
        }

        // ------------------------------------------------------------------ refresh

        public void RefreshAll()
        {
            if (!m_Started)
                return;

            RefreshPages();
            RefreshOfflinePage();
            RefreshSessionPage();
            RefreshVoice();
            RefreshTable();
            RefreshPresenter();
            LayoutSessionRows();
            RefreshStatus();
        }

        private void RefreshPages()
        {
            bool inSession = InSession;
            if (inSession && KeyboardOpen)
                m_Keyboard.CloseSilently();

            SetActive(m_OfflinePage, !inSession && !KeyboardOpen);
            SetActive(m_SessionPage, inSession);
        }

        private void RefreshOfflinePage()
        {
            bool connecting = m_Busy || (m_Session != null && m_Session.State == SessionState.Connecting);
            bool available = m_Session != null;

            if (m_NameButtonLabel != null)
                m_NameButtonLabel.text = PlayerProfile.Name;
            SetInteractable(m_NameButton, !connecting && m_Keyboard != null);

            int selected = PlayerProfile.ColourIndex;
            for (int i = 0; i < m_ColourSwatches.Count; i++)
            {
                SetInteractable(m_ColourSwatches[i], !connecting);
                if (i < m_ColourFills.Count && m_ColourFills[i] != null && i < PlayerProfile.Palette.Length)
                    m_ColourFills[i].color = PlayerProfile.Palette[i];
                if (i < m_ColourSelectedMarks.Count)
                    SetActive(m_ColourSelectedMarks[i], i == selected);
            }

            bool coLocated = CoLocatedPref;
            SetToggle(m_CoLocatedToggle, coLocated);
            SetInteractable(m_CoLocatedToggle, !connecting);
            if (m_ModeLabel != null)
                m_ModeLabel.text = coLocated ? "Mode: Co-located" : "Mode: Remote";
            if (m_ModeHint != null)
                m_ModeHint.text = coLocated
                    ? "Same room: everyone aligns their table to one real table"
                    : "Different places: voice chat, each with their own table";

            SetInteractable(m_HostButton, available && !connecting);
            SetInteractable(m_JoinButton, available && !connecting && m_Keyboard != null);
            if (m_HostButtonLabel != null)
                m_HostButtonLabel.text = connecting ? "Connecting…" : "Host session";
        }

        private void RefreshSessionPage()
        {
            if (!m_Started)
                return;

            bool inSession = InSession;

            if (m_JoinCodeText != null)
                m_JoinCodeText.text = inSession && !string.IsNullOrEmpty(m_Session.JoinCode) ? m_Session.JoinCode : k_Dash;

            if (m_RoleText != null)
            {
                if (inSession)
                {
                    string role = m_Session.IsHost ? "You are hosting" : "Joined";
                    string mode = m_Session.Mode == SessionMode.CoLocated ? "Co-located" : "Remote";
                    string text = $"{role} · {mode} · {m_Session.Players.Count}/{SessionService.MaxPlayers} players";
                    if (SharedPlotAuthority.PresenterMode)
                        text += m_Session.IsHost ? " · Presenter mode" : " · Presenter mode — view only";
                    m_RoleText.text = text;
                }
                else
                {
                    m_RoleText.text = "";
                }
            }

            if (m_PlayerList != null)
                m_PlayerList.SetPlayers(inSession ? m_Session.Players : Array.Empty<PlayerInfo>());

            if (m_LastChangedText != null)
            {
                string who = inSession ? m_Session.LastChangedBy : "";
                m_LastChangedText.text = string.IsNullOrEmpty(who) ? "Last change: " + k_Dash : "Last change: " + who;
            }

            SetInteractable(m_LeaveButton, inSession && !m_Busy);
            RefreshStatus();
        }

        private void RefreshVoice()
        {
            if (!m_Started)
                return;

            bool show = InSession && m_Voice != null;
            SetActive(m_MicRow, show);
            SetActive(m_VoiceRow, show);

            if (show)
            {
                bool enabledVoice = m_Voice.VoiceEnabled;
                SetToggle(m_VoiceToggle, enabledVoice);
                SetToggle(m_MicToggle, enabledVoice && !m_Voice.IsMuted);
                SetInteractable(m_MicToggle, enabledVoice);

                if (m_VoiceLabel != null)
                    m_VoiceLabel.text = enabledVoice ? "Voice chat: on" : "Voice chat: off";
                if (m_MicLabel != null)
                    m_MicLabel.text = !enabledVoice ? "Microphone (voice is off)"
                        : m_Voice.IsMuted ? "Microphone: muted" : "Microphone: on";
            }

            LayoutSessionRows();
        }

        private void RefreshTable()
        {
            if (!m_Started)
                return;

            bool show = InSession && m_Table != null;
            SetActive(m_PlaceTableRow, show);

            bool showAlign = show && m_Session.Mode == SessionMode.CoLocated;
            SetActive(m_AlignRow, showAlign);

            if (show)
            {
                SetToggle(m_PlaceTableToggle, m_Table.IsPlacing);
                if (m_PlaceTableLabel != null)
                    m_PlaceTableLabel.text = m_Table.IsPlacing ? "Place table: grab it to move" : "Place table";

                int step = m_Table.AlignStep;
                if (m_AlignButtonLabel != null)
                    m_AlignButtonLabel.text = step == 0 ? "Align to real table" : "Cancel align";
                if (m_AlignHintText != null)
                    m_AlignHintText.text = step switch
                    {
                        1 => "Touch corner 1 of the real table",
                        2 => "Touch corner 2 of the real table",
                        _ => "Touch two corners of the real table with a controller",
                    };
            }

            LayoutSessionRows();
        }

        private void RefreshPresenter()
        {
            bool show = InSession && m_Session.IsHost;
            SetActive(m_PresenterRow, show);
            if (show)
            {
                bool on = SharedPlotAuthority.PresenterMode;
                SetToggle(m_PresenterToggle, on);
                if (m_PresenterLabel != null)
                    m_PresenterLabel.text = on ? "Presenter mode: only you edit" : "Presenter mode: off";
            }
        }

        /// <summary>Stacks the visible right-column rows from the top so hidden ones leave no gap.</summary>
        private void LayoutSessionRows()
        {
            if (m_SessionRows.Count == 0)
                return;

            if (float.IsNaN(m_RowsTop))
            {
                m_RowsTop = float.MinValue;
                foreach (RectTransform row in m_SessionRows)
                    if (row != null)
                        m_RowsTop = Mathf.Max(m_RowsTop, row.anchoredPosition.y + row.rect.height * (1f - row.pivot.y));
                if (m_RowsTop == float.MinValue)
                    return;
            }

            float y = m_RowsTop;
            foreach (RectTransform row in m_SessionRows)
            {
                if (row == null || !row.gameObject.activeSelf)
                    continue;

                float h = row.rect.height;
                row.anchoredPosition = new Vector2(row.anchoredPosition.x, y - h * (1f - row.pivot.y));
                y -= h + m_SessionRowSpacing;
            }
        }

        // ------------------------------------------------------------------ helpers

        private void AddClick(Button button, Action action)
        {
            if (button != null)
                button.onClick.AddListener(() => { action(); RefreshAll(); });
        }

        private void AddToggle(Toggle toggle, Action<bool> action)
        {
            if (toggle == null)
                return;

            toggle.onValueChanged.AddListener(on =>
            {
                if (m_Refreshing)
                    return;
                action(on);
                RefreshAll(); // a refused change snaps the toggle back
            });
        }

        private void SetToggle(Toggle toggle, bool on)
        {
            if (toggle == null || toggle.isOn == on)
                return;

            // isOn (not SetIsOnWithoutNotify) so BooleanToggleVisualsController moves its knob.
            m_Refreshing = true;
            try { toggle.isOn = on; }
            finally { m_Refreshing = false; }
        }

        private static void SetInteractable(Selectable selectable, bool interactable)
        {
            if (selectable != null && selectable.interactable != interactable)
                selectable.interactable = interactable;
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }
    }
}
