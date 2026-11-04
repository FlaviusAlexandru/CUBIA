using System.Collections.Generic;
using DataViz.Flow;
using DataViz.Lobby;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// The slim session strip in the data scene, under the menu's tile row (built into the menu's
    /// "SessionStripAnchor" by Tools/CUBIA/Multiplayer/5 Move Network To Bootstrap). Shows the join
    /// code, a dot per player (bigger while speaking), mute, presenter mode (host only) and Leave,
    /// which ends the session for this player and returns them to the lobby. Builds its widgets
    /// in Awake to fill its own rect; hidden while offline. Exempt from presenter mode.
    /// </summary>
    [RequireComponent(typeof(RectTransform), typeof(PresenterModeExempt))]
    public class SessionStripUI : MonoBehaviour
    {
        const float k_SpeakingScale = 1.35f;

        [SerializeField] UiIcons m_Icons = new();

        GameObject m_Content;
        TMP_Text m_Code;
        readonly List<Image> m_Dots = new();
        Button m_Mute, m_Presenter, m_Leave;

        SessionService m_Session;
        VoiceService m_Voice;
        bool m_Leaving;

        void Awake() => Build();

        void OnEnable()
        {
            SharedPlotAuthority.OnPresenterModeChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            SharedPlotAuthority.OnPresenterModeChanged -= Refresh;
            Bind(null, null);
        }

        void Update()
        {
            if (SessionService.Instance != m_Session || VoiceService.Instance != m_Voice)
            {
                Bind(SessionService.Instance, VoiceService.Instance);
                Refresh();
            }
        }

        void Bind(SessionService session, VoiceService voice)
        {
            if (m_Session != null)
            {
                m_Session.OnStateChanged -= Refresh;
                m_Session.OnPlayersChanged -= Refresh;
            }
            if (m_Voice != null)
            {
                m_Voice.OnVoiceStateChanged -= Refresh;
                m_Voice.OnSpeakingChanged -= OnSpeakingChanged;
            }
            m_Session = session;
            m_Voice = voice;
            if (session != null)
            {
                session.OnStateChanged += Refresh;
                session.OnPlayersChanged += Refresh;
            }
            if (voice != null)
            {
                voice.OnVoiceStateChanged += Refresh;
                voice.OnSpeakingChanged += OnSpeakingChanged;
            }
        }

        void OnSpeakingChanged(ulong clientId, bool speaking) => Refresh();

        void Refresh()
        {
            bool inSession = m_Session != null && m_Session.IsInSession;
            m_Content.SetActive(inSession);
            if (!inSession) return;

            m_Code.text = $"Code  <b>{m_Session.JoinCode}</b>";

            IReadOnlyList<PlayerInfo> players = m_Session.Players;
            for (int i = 0; i < m_Dots.Count; i++)
            {
                bool show = i < players.Count;
                m_Dots[i].gameObject.SetActive(show);
                if (!show) continue;
                Color c = players[i].Colour;
                c.a = 1f;
                m_Dots[i].color = c;
                bool speaking = m_Voice != null && m_Voice.IsSpeaking(players[i].ClientId);
                m_Dots[i].rectTransform.localScale = Vector3.one * (speaking ? k_SpeakingScale : 1f);
            }

            bool voiceOn = m_Voice != null && m_Voice.VoiceEnabled;
            m_Mute.gameObject.SetActive(voiceOn);
            if (voiceOn)
            {
                bool muted = m_Voice.IsMuted;
                Image icon = PanelKit.IconOf(m_Mute);
                if (icon != null) icon.sprite = muted ? m_Icons.MicOff : m_Icons.Mic;
                PanelKit.SetHighlighted(m_Mute, !muted);
            }

            m_Presenter.gameObject.SetActive(m_Session.IsHost);
            PanelKit.SetHighlighted(m_Presenter, SharedPlotAuthority.PresenterMode);
            PanelKit.LabelOf(m_Leave).text = m_Session.IsHost ? "End" : "Leave";
            m_Leave.interactable = !m_Leaving;
        }

        void Build()
        {
            MenuTheme theme = PanelKit.Theme;
            var rect = (RectTransform)transform;
            float w = Mathf.Max(rect.rect.width, 600f);
            float h = Mathf.Max(rect.rect.height, 56f);
            float pad = h * 0.14f, bh = h - 2f * pad;

            m_Content = PanelKit.Fill(transform, "Content").gameObject;
            Transform strip = PanelKit.Panel(m_Content.transform, "Strip", 0f, 0f, w, h, theme.PanelColor, theme.PanelOutline, h * 0.3f).transform;
            // The panel is laid out top-left; stretch it so it follows the anchor's size.
            var stripRect = (RectTransform)strip;
            stripRect.anchorMin = Vector2.zero;
            stripRect.anchorMax = Vector2.one;
            stripRect.offsetMin = stripRect.offsetMax = Vector2.zero;

            float x = pad * 2f;
            if (m_Icons.Users != null)
            {
                PanelKit.Icon(strip, "SessionIcon", m_Icons.Users, x, -(h - bh * 0.7f) * 0.5f, bh * 0.7f, theme.IconTint);
                x += bh * 0.7f + pad;
            }
            m_Code = PanelKit.Label(strip, "Code", "", x, 0f, bh * 4.2f, h, theme.BodyFontSize, theme.Text);
            m_Code.richText = true;
            x += bh * 4.2f + pad;

            float dot = bh * 0.45f;
            for (int i = 0; i < SessionService.MaxPlayers; i++)
            {
                Image d = PanelKit.Dot(strip, "PlayerDot" + i, x, -(h - dot) * 0.5f, dot, Color.white);
                d.rectTransform.pivot = new Vector2(0.5f, 0.5f); // scale around the centre while speaking
                d.rectTransform.anchoredPosition += new Vector2(dot * 0.5f, -dot * 0.5f);
                m_Dots.Add(d);
                x += dot + pad;
            }

            // Right-aligned buttons: Leave, presenter, mute.
            float right = w - pad;
            m_Leave = PanelKit.Button(strip, "Leave", "Leave", m_Icons.Leave, right - bh * 3.2f, -pad, bh * 3.2f, bh, PanelKit.Style.Secondary);
            right -= bh * 3.2f + pad;
            m_Presenter = PanelKit.Button(strip, "Presenter", "Presenter", m_Icons.Presenter, right - bh * 4.2f, -pad, bh * 4.2f, bh,
                PanelKit.Style.Secondary);
            right -= bh * 4.2f + pad;
            m_Mute = PanelKit.Button(strip, "Mute", "", m_Icons.MicOff, right - bh, -pad, bh, bh, PanelKit.Style.Secondary);

            foreach (Button b in new[] { m_Leave, m_Presenter, m_Mute })
            {
                // Keep the buttons pinned to the right edge when the anchor is resized.
                var rt = (RectTransform)b.transform;
                Vector2 pos = rt.anchoredPosition;
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(pos.x - w, pos.y);
            }

            m_Mute.onClick.AddListener(() => m_Voice?.SetMuted(!m_Voice.IsMuted));
            m_Presenter.onClick.AddListener(() => SharedPlotAuthority.RequestSetPresenterMode(!SharedPlotAuthority.PresenterMode));
            m_Leave.onClick.AddListener(Leave);
            m_Content.SetActive(false);
        }

        void Leave()
        {
            if (m_Leaving || MultiplayerFlow.Instance == null) return;
            m_Leaving = true;
            Refresh();
            MultiplayerFlow.Instance.LeaveToLobby();
        }
    }
}
