using System.Collections.Generic;
using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Lobby
{
    /// <summary>References to everything <see cref="LobbyPanelLayout"/> builds, for <see cref="LobbyPanel"/> to drive.</summary>
    public class LobbyWidgets
    {
        public Button Back;
        public TMP_Text Status;

        // Setup page (offline)
        public GameObject SetupPage;
        public Button NameButton;
        public readonly List<Button> Swatches = new();
        public readonly List<GameObject> SwatchMarks = new();
        public Button RemoteButton, CoLocatedButton;
        public TMP_Text ModeHint;
        public Button MicTestButton;
        public MicLevelMeter MicMeter;
        public TMP_Text MicTestHint;
        public Button HostButton, JoinButton;
        public TMP_Text HostHint;

        // Session page (hosting or joined, before and while waiting for Start)
        public GameObject SessionPage;
        public GameObject HostBlock, JoinerBlock;
        public TMP_Text JoinCode;
        public TMP_Text WaitingTitle, DatasetCheck;
        public Image DatasetIcon;
        public Button WizardButton;
        public TMP_Text PlayersCaption;
        public PlayerListView Players;
        public Button StartButton;
        public TMP_Text SessionCode, SessionInfo;
        public Button MicButton, AlignButton, LeaveButton;
        public TMP_Text AlignHint;

        // Keyboard page
        public VRKeyboard Keyboard;
    }

    /// <summary>
    /// Builds the lobby's one large panel with <see cref="PanelKit"/>: a header (Back, title), a body
    /// with the setup page, the session page or the keyboard, and a status line. Canvas units are
    /// millimetres at the default scale; the body is laid out as two 620-unit columns.
    /// </summary>
    public static class LobbyPanelLayout
    {
        public static readonly Vector2 Size = new(1400f, 860f);

        const float k_BodyX = 40f, k_BodyY = -130f, k_BodyW = 1320f, k_BodyH = 640f;
        const float k_RightX = 700f, k_ColumnW = 620f;
        const float k_RowH = 64f;

        public static LobbyWidgets Build(Transform canvas, UiIcons icons)
        {
            MenuTheme theme = PanelKit.Theme;
            var w = new LobbyWidgets();

            Transform panel = PanelKit.Panel(canvas, "Panel", 0f, 0f, Size.x, Size.y, theme.PanelColor, theme.PanelOutline, 36f).transform;

            w.Back = PanelKit.Button(panel, "Back", "Back", icons.Back, 32f, -28f, 180f, k_RowH, PanelKit.Style.Secondary);
            PanelKit.Label(panel, "Title", "Multiplayer", 240f, -24f, 700f, 72f, theme.TitleFontSize * 1.2f, theme.Text);
            PanelKit.Divider(panel, "HeaderDivider", 32f, -112f, Size.x - 64f, 2f);

            RectTransform body = PanelKit.Rect(panel, "Body", k_BodyX, k_BodyY, k_BodyW, k_BodyH);
            w.SetupPage = BuildSetupPage(body, w, icons, theme);
            w.SessionPage = BuildSessionPage(body, w, icons, theme);

            RectTransform keyboardArea = PanelKit.Rect(body, "KeyboardArea", (k_BodyW - VRKeyboardFactory.Width) * 0.5f, -20f,
                VRKeyboardFactory.Width, VRKeyboardFactory.Height);
            w.Keyboard = VRKeyboardFactory.Build(keyboardArea, "Keyboard");

            w.Status = PanelKit.Label(panel, "Status", "", k_BodyX, -Size.y + 64f, k_BodyW, 44f, theme.BodyFontSize, theme.TextMuted);
            return w;
        }

        // ------------------------------------------------------------------ setup page

        static GameObject BuildSetupPage(Transform body, LobbyWidgets w, UiIcons icons, MenuTheme theme)
        {
            RectTransform page = PanelKit.Fill(body, "SetupPage");
            float caption = theme.BodyFontSize;
            const float valueX = 130f;

            PanelKit.Label(page, "NameCaption", "Name", 0f, 0f, valueX, k_RowH, caption, theme.TextMuted);
            w.NameButton = PanelKit.Button(page, "Name", "Player", null, valueX, 0f, 440f, k_RowH, PanelKit.Style.Secondary);

            PanelKit.Label(page, "ColourCaption", "Colour", 0f, -84f, valueX, k_RowH, caption, theme.TextMuted);
            for (int i = 0; i < PlayerProfile.Palette.Length; i++)
            {
                RectTransform swatch = PanelKit.Rect(page, "Swatch" + i, valueX + i * 60f, -90f, 52f, 52f);
                Image fill = swatch.gameObject.AddComponent<Image>();
                fill.sprite = PanelKit.CircleSprite;
                fill.color = PlayerProfile.Palette[i];
                var button = swatch.gameObject.AddComponent<Button>();
                button.targetGraphic = fill;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                w.Swatches.Add(button);
                w.SwatchMarks.Add(PanelKit.Dot(swatch, "Selected", 16f, -16f, 20f, Color.white).gameObject);
            }

            PanelKit.Label(page, "ModeCaption", "Mode", 0f, -168f, valueX, k_RowH, caption, theme.TextMuted);
            w.RemoteButton = PanelKit.Button(page, "Remote", "Remote", null, valueX, -168f, 214f, k_RowH, PanelKit.Style.Secondary);
            w.CoLocatedButton = PanelKit.Button(page, "CoLocated", "Co-located", null, valueX + 226f, -168f, 214f, k_RowH, PanelKit.Style.Secondary);
            w.ModeHint = PanelKit.Label(page, "ModeHint", "", valueX, -240f, 480f, 60f, caption * 0.85f, theme.TextMuted,
                TextAlignmentOptions.TopLeft, wrap: true);

            PanelKit.Label(page, "MicCaption", "Mic", 0f, -324f, valueX, k_RowH, caption, theme.TextMuted);
            w.MicTestButton = PanelKit.Button(page, "MicTest", "Test mic", icons.Mic, valueX, -324f, 200f, k_RowH, PanelKit.Style.Secondary);
            Image bar = PanelKit.Panel(page, "MicLevel", valueX + 216f, -340f, 224f, 32f, new Color(1f, 1f, 1f, 0.9f), theme.PanelOutline, 16f);
            bar.raycastTarget = false;
            RectTransform fillRect = PanelKit.Fill(bar.transform, "Fill");
            fillRect.offsetMin = new Vector2(4f, 4f);
            fillRect.offsetMax = new Vector2(-4f, -4f);
            Image level = fillRect.gameObject.AddComponent<Image>();
            level.sprite = PanelKit.RoundedSprite;
            level.type = Image.Type.Sliced;
            level.pixelsPerUnitMultiplier = 24f / 12f;
            level.color = theme.Accent;
            level.raycastTarget = false;
            w.MicMeter = bar.gameObject.AddComponent<MicLevelMeter>();
            w.MicMeter.Init(fillRect);
            fillRect.anchorMax = new Vector2(0f, 1f);
            w.MicTestHint = PanelKit.Label(page, "MicHint", "", valueX, -396f, 480f, 60f, caption * 0.85f, theme.TextMuted,
                TextAlignmentOptions.TopLeft, wrap: true);

            PanelKit.Divider(page, "ColumnDivider", 650f, 0f, 2f, 480f);

            w.HostButton = PanelKit.Button(page, "Host", "Host a session", icons.Host, k_RightX, 0f, k_ColumnW, 110f,
                PanelKit.Style.Primary, theme.TitleFontSize * 0.8f);
            w.HostHint = PanelKit.Label(page, "HostHint", "", k_RightX, -118f, k_ColumnW, 50f, caption * 0.85f, theme.TextMuted);
            w.JoinButton = PanelKit.Button(page, "Join", "Join with a code", icons.Join, k_RightX, -196f, k_ColumnW, 110f,
                PanelKit.Style.Secondary, theme.TitleFontSize * 0.8f);
            PanelKit.Label(page, "Help",
                "Up to 4 players. The host's dataset drives the session, so everyone needs the same file. " +
                "Microphones start muted.",
                k_RightX, -330f, k_ColumnW, 120f, caption * 0.85f, theme.TextMuted, TextAlignmentOptions.TopLeft, wrap: true);
            return page.gameObject;
        }

        // ------------------------------------------------------------------ session page

        static GameObject BuildSessionPage(Transform body, LobbyWidgets w, UiIcons icons, MenuTheme theme)
        {
            RectTransform page = PanelKit.Fill(body, "SessionPage");
            float caption = theme.BodyFontSize;

            // Left column, top: the host sees the join code, a joiner the waiting state and dataset check.
            RectTransform host = PanelKit.Rect(page, "HostBlock", 0f, 0f, k_ColumnW, 200f);
            PanelKit.Label(host, "CodeCaption", "Join code", 0f, 0f, k_ColumnW, 36f, caption, theme.TextMuted);
            w.JoinCode = PanelKit.Label(host, "JoinCode", "------", 0f, -36f, k_ColumnW, 104f, 84f, theme.Accent);
            w.JoinCode.characterSpacing = 12f;
            PanelKit.Label(host, "ShareHint", "Share this code. Players can also join after you start.", 0f, -146f, k_ColumnW, 40f,
                caption * 0.85f, theme.TextMuted);
            w.HostBlock = host.gameObject;

            RectTransform joiner = PanelKit.Rect(page, "JoinerBlock", 0f, 0f, k_ColumnW, 200f);
            w.WaitingTitle = PanelKit.Label(joiner, "Waiting", "", 0f, 0f, k_ColumnW, 56f, theme.TitleFontSize * 0.8f, theme.Text);
            w.DatasetIcon = PanelKit.Icon(joiner, "DatasetIcon", null, 0f, -70f, 36f, theme.IconTint);
            w.DatasetCheck = PanelKit.Label(joiner, "DatasetCheck", "", 48f, -64f, k_ColumnW - 48f, 48f, caption, theme.Text);
            w.WizardButton = PanelKit.Button(joiner, "Wizard", "Run import wizard", icons.Wizard, 0f, -126f, 340f, k_RowH,
                PanelKit.Style.Secondary);
            w.JoinerBlock = joiner.gameObject;

            w.PlayersCaption = PanelKit.Label(page, "PlayersCaption", "Players", 0f, -230f, k_ColumnW, 40f, caption, theme.TextMuted);
            w.Players = BuildPlayerList(page, 0f, -276f, k_ColumnW, theme);

            PanelKit.Divider(page, "ColumnDivider", 650f, 0f, 2f, 520f);

            // Right column: Start (host) or the code (joiner), session info, mic or align, leave.
            w.StartButton = PanelKit.Button(page, "Start", "Start session", icons.Presenter, k_RightX, 0f, k_ColumnW, 110f,
                PanelKit.Style.Primary, theme.TitleFontSize * 0.8f);
            w.SessionCode = PanelKit.Label(page, "SessionCode", "", k_RightX, 0f, k_ColumnW, 110f, theme.TitleFontSize * 0.8f, theme.Text);
            w.SessionInfo = PanelKit.Label(page, "SessionInfo", "", k_RightX, -124f, k_ColumnW, 44f, caption, theme.TextMuted);
            w.MicButton = PanelKit.Button(page, "Mic", "Mic muted", icons.MicOff, k_RightX, -184f, 300f, k_RowH, PanelKit.Style.Secondary);
            w.AlignButton = PanelKit.Button(page, "Align", "Align table here", icons.Align, k_RightX, -184f, 300f, k_RowH,
                PanelKit.Style.Secondary);
            w.AlignHint = PanelKit.Label(page, "AlignHint", "", k_RightX, -258f, k_ColumnW, 90f, caption * 0.85f, theme.TextMuted,
                TextAlignmentOptions.TopLeft, wrap: true);
            w.LeaveButton = PanelKit.Button(page, "Leave", "Leave", icons.Leave, k_RightX + k_ColumnW - 300f, -k_BodyH + k_RowH + 16f,
                300f, k_RowH, PanelKit.Style.Secondary);
            return page.gameObject;
        }

        /// <summary>A PlayerListView whose row template is a colour dot, the name and a "host" tag.</summary>
        static PlayerListView BuildPlayerList(Transform parent, float x, float y, float width, MenuTheme theme)
        {
            const float rowH = 48f;
            RectTransform list = PanelKit.Rect(parent, "PlayerList", x, y, width, rowH * SessionService.MaxPlayers);
            RectTransform row = PanelKit.Rect(list, "PlayerRowTemplate", 0f, 0f, width, rowH - 6f);
            PanelKit.Dot(row, "Dot", 6f, -9f, 24f, Color.white);
            PanelKit.Label(row, "Name", "Player", 44f, 0f, width - 160f, rowH - 6f, theme.BodyFontSize, theme.Text);
            PanelKit.Label(row, "HostTag", "host", width - 110f, 0f, 100f, rowH - 6f, theme.BodyFontSize * 0.8f, theme.Accent,
                TextAlignmentOptions.MidlineRight);
            row.gameObject.SetActive(false);

            var view = list.gameObject.AddComponent<PlayerListView>();
            view.m_RowTemplate = row;
            view.m_RowSpacing = rowH;
            return view;
        }
    }
}
