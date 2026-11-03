using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DataViz.Networking.EditorTools
{
    /// <summary>
    /// Tools > CUBIA > Multiplayer > 4 Setup Session Island.
    ///
    /// Builds the Session island on SummonedMenuCanvas by DUPLICATING the menu's existing pieces
    /// (an island background and title, the Shuffle-locks buttons, the Tooltips toggle row, the
    /// island headings and divider) so it matches the look, then wires SessionIslandUI, VRKeyboard
    /// and PlayerListView, and adds a PresenterModeUIGate (with a "Presenter mode — view only"
    /// note) to the summoned and wrist menus.
    ///
    /// Idempotent: an existing island is rebuilt in place (its position is kept), and the
    /// controller, gates and notes are reused. Marks the scene dirty; does not save.
    /// The summoned menu shows every island at once, so being on the canvas is what makes the
    /// island reachable - there is no island list to register it in.
    /// </summary>
    public static class SessionIslandSetupMenu
    {
        private const string k_MenuPath = "Tools/CUBIA/Multiplayer/4 Setup Session Island";
        private const string k_SummonedCanvasName = "SummonedMenuCanvas";
        private const string k_WristCanvasName = "WristUIHolderCanvas";
        private const string k_IslandName = "SessionIslandBackground";
        private const string k_ControllerName = "SessionIslandController";
        private const string k_NoteName = "PresenterModeNote";
        private const string k_NoteText = "Presenter mode — view only";

        // Free area right of the Glyph ranges island, below Preferences, above the bookmark palette.
        private static readonly Vector2 k_DefaultCenter = new(531f, -530f);
        private static readonly Vector2 k_IslandSize = new(1010f, 464f); // canvas units

        private static readonly Color k_TextColour = new(0.894f, 0.894f, 0.894f, 1f);
        private static readonly Color k_DimColour = new(0.70f, 0.72f, 0.76f, 1f);
        private static readonly Color k_StatusColour = new(1.00f, 0.84f, 0.50f, 1f);
        private static readonly Color k_NoteColour = new(1.00f, 0.78f, 0.35f, 1f);

        private class Templates
        {
            public RectTransform Background, Title, Button, ToggleRow, Label, Divider;
            public Sprite Rounded, Circle;
        }

        [MenuItem(k_MenuPath)]
        public static void Setup()
        {
            Canvas summoned = FindSceneCanvas(k_SummonedCanvasName);
            if (summoned == null)
            {
                Debug.LogError($"[SessionIslandSetup] No '{k_SummonedCanvasName}' in the open scene - nothing done.");
                return;
            }

            if (!TryGetTemplates(summoned.transform, out Templates tpl, out string missing))
            {
                Debug.LogError($"[SessionIslandSetup] Template objects missing under {k_SummonedCanvasName}: {missing}. Nothing done.");
                return;
            }

            s_CurrentTemplates = tpl;
            Undo.SetCurrentGroupName("Setup Session Island");
            int undoGroup = Undo.GetCurrentGroup();

            SessionIslandUI ui = GetOrCreateController(summoned);
            RectTransform island = BuildIsland(summoned, tpl, ui);

            SetupGate(summoned, tpl, new[]
            {
                island.gameObject,
                FindDeep(summoned.transform, "ShuffleLocksSection")?.gameObject,
                FindDeep(summoned.transform, "BlacklistColumnDropdown")?.gameObject,
                FindDeep(summoned.transform, "BookmarkColumnDropdown")?.gameObject,
                FindDeep(summoned.transform, "TooltipsToggle")?.gameObject,
                FindDeep(summoned.transform, "BookmarkPalettePanel")?.gameObject,
                FindDeep(summoned.transform, "TooltipAnchor")?.gameObject,
            });

            Canvas wrist = FindSceneCanvas(k_WristCanvasName);
            if (wrist != null)
            {
                SetupGate(wrist, tpl, new[]
                {
                    FindDeep(wrist.transform, "TooltipsToggle")?.gameObject,
                    FindDeep(wrist.transform, "TooltipAnchor")?.gameObject,
                });
            }
            else
            {
                Debug.LogWarning($"[SessionIslandSetup] No '{k_WristCanvasName}' found - wrist menu not gated.");
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(summoned.gameObject.scene);
            Selection.activeGameObject = island.gameObject;
            Debug.Log("[SessionIslandSetup] Session island built and wired; presenter-mode gates added. Scene marked dirty (not saved).");
        }

        // ------------------------------------------------------------------ island

        private static RectTransform BuildIsland(Canvas canvas, Templates tpl, SessionIslandUI ui)
        {
            Transform canvasT = canvas.transform;
            Vector2 center = k_DefaultCenter;

            Transform existing = canvasT.Find(k_IslandName);
            if (existing != null)
            {
                center = ((RectTransform)existing).anchoredPosition;
                Undo.DestroyObjectImmediate(existing.gameObject);
            }

            // Background: a copy of an existing island background, emptied.
            var island = (RectTransform)Object.Instantiate(tpl.Background, canvasT, false);
            island.name = k_IslandName;
            for (int i = island.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(island.GetChild(i).gameObject);

            Vector3 s = tpl.Background.localScale;
            float sx = Mathf.Approximately(s.x, 0f) ? 1f : s.x;
            float sy = Mathf.Approximately(s.y, 0f) ? 1f : s.y;
            island.anchorMin = island.anchorMax = island.pivot = new Vector2(0.5f, 0.5f);
            island.localScale = new Vector3(sx, sy, 1f);
            island.localRotation = Quaternion.identity;
            island.sizeDelta = new Vector2(k_IslandSize.x / sx, k_IslandSize.y / sy);
            island.anchoredPosition3D = new Vector3(center.x, center.y, 0f);

            Transform tooltipAnchor = canvasT.Find("TooltipAnchor");
            if (tooltipAnchor != null)
                island.SetSiblingIndex(tooltipAnchor.GetSiblingIndex());

            // Title, in the island's own (scaled) space like every other island title.
            var title = (RectTransform)Object.Instantiate(tpl.Title, island, false);
            title.name = "Text (TMP)";
            title.GetComponent<TMP_Text>().text = "Session";
            Vector2 local = island.sizeDelta;
            title.anchoredPosition = new Vector2(
                -local.x * 0.5f + 24f / sx + title.sizeDelta.x * title.pivot.x,
                local.y * 0.5f - 10f / sy - title.sizeDelta.y * (1f - title.pivot.y));

            // Content in canvas units (inverse of the background's non-uniform scale), like ShuffleLocksSection.
            var content = NewRect("SessionContent", island);
            content.anchorMin = content.anchorMax = content.pivot = new Vector2(0f, 1f);
            content.localScale = new Vector3(1f / sx, 1f / sy, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = k_IslandSize;

            RectTransform offline = NewStretch("OfflinePage", content);
            RectTransform session = NewStretch("SessionPage", content);
            RectTransform keyboard = NewStretch("SessionKeyboard", content);

            BuildOfflinePage(offline, tpl, ui);
            BuildSessionPage(session, tpl, ui);
            BuildKeyboard(keyboard, tpl, ui);

            ui.m_StatusText = MakeLabel(content, "SessionStatus", "", new Vector2(24f, -424f), new Vector2(962f, 28f), 15f,
                TextAlignmentOptions.MidlineLeft, k_StatusColour);

            ui.m_OfflinePage = offline.gameObject;
            ui.m_SessionPage = session.gameObject;
            offline.gameObject.SetActive(true);
            session.gameObject.SetActive(false);
            keyboard.gameObject.SetActive(false);

            Undo.RegisterCreatedObjectUndo(island.gameObject, "Setup Session Island");
            EditorUtility.SetDirty(ui);
            return island;
        }

        private static void BuildOfflinePage(RectTransform page, Templates tpl, SessionIslandUI ui)
        {
            MakeLabel(page, "NameCaption", "Name", new Vector2(24f, -62f), new Vector2(80f, 44f), 18f);
            var name = MakeButton(page, tpl, "SessionNameButton", "Player", new Vector2(104f, -62f), new Vector2(300f, 44f), 18f);
            ui.m_NameButton = name.button;
            ui.m_NameButtonLabel = name.label;

            MakeLabel(page, "ColourCaption", "Colour", new Vector2(430f, -62f), new Vector2(80f, 44f), 18f);
            ui.m_ColourSwatches = new List<Button>();
            ui.m_ColourFills = new List<Image>();
            ui.m_ColourSelectedMarks = new List<GameObject>();
            for (int i = 0; i < PlayerProfile.Palette.Length; i++)
            {
                var sw = MakeButton(page, tpl, "SessionColourSwatch" + i, "", new Vector2(510f + i * 60f, -62f), new Vector2(52f, 44f), 16f);
                Transform front = sw.button.transform.Find("Button Front") ?? sw.button.transform;

                Image fill = MakeImage(front, "SwatchFill", tpl.Rounded, PlayerProfile.Palette[i], Vector2.zero, Vector2.zero);
                var fillRt = fill.rectTransform;
                fillRt.anchorMin = Vector2.zero;
                fillRt.anchorMax = Vector2.one;
                fillRt.offsetMin = new Vector2(6f, 6f);
                fillRt.offsetMax = new Vector2(-6f, -6f);
                fillRt.SetSiblingIndex(0); // under the poke fill and text

                Image mark = MakeImage(front, "SwatchSelected", tpl.Circle, Color.white, Vector2.zero, new Vector2(14f, 14f));
                var markRt = mark.rectTransform;
                markRt.anchorMin = markRt.anchorMax = new Vector2(0.5f, 0.5f);
                markRt.anchoredPosition = Vector2.zero;
                mark.gameObject.SetActive(i == PlayerProfile.ColourIndex);

                ui.m_ColourSwatches.Add(sw.button);
                ui.m_ColourFills.Add(fill);
                ui.m_ColourSelectedMarks.Add(mark.gameObject);
            }

            var mode = MakeToggleRow(page, tpl, "SessionModeToggle", "Mode: Remote", new Vector2(24f, -128f), new Vector2(460f, 50f));
            ui.m_CoLocatedToggle = mode.toggle;
            ui.m_ModeLabel = mode.label;
            ui.m_ModeHint = MakeLabel(page, "SessionModeHint", "", new Vector2(24f, -180f), new Vector2(700f, 24f), 14f,
                TextAlignmentOptions.MidlineLeft, k_DimColour);

            var host = MakeButton(page, tpl, "SessionHostButton", "Host session", new Vector2(24f, -230f), new Vector2(220f, 52f), 18f);
            ui.m_HostButton = host.button;
            ui.m_HostButtonLabel = host.label;
            var join = MakeButton(page, tpl, "SessionJoinButton", "Join with code", new Vector2(264f, -230f), new Vector2(220f, 52f), 18f);
            ui.m_JoinButton = join.button;

            TMP_Text help = MakeLabel(page, "SessionOfflineHelp",
                "Host to get a join code to share, or join with someone else's code. Up to 4 players; everyone needs the same dataset.",
                new Vector2(24f, -300f), new Vector2(960f, 56f), 14f, TextAlignmentOptions.TopLeft, k_DimColour);
            help.textWrappingMode = TextWrappingModes.Normal;
        }

        private static void BuildSessionPage(RectTransform page, Templates tpl, SessionIslandUI ui)
        {
            MakeLabel(page, "JoinCodeCaption", "Join code", new Vector2(24f, -56f), new Vector2(200f, 24f), 16f);
            ui.m_JoinCodeText = MakeLabel(page, "SessionJoinCode", "------", new Vector2(24f, -80f), new Vector2(360f, 56f), 44f,
                TextAlignmentOptions.MidlineLeft, Color.white);
            ui.m_JoinCodeText.characterSpacing = 8f;
            ui.m_RoleText = MakeLabel(page, "SessionRole", "", new Vector2(24f, -140f), new Vector2(470f, 24f), 14f,
                TextAlignmentOptions.MidlineLeft, k_DimColour);

            MakeLabel(page, "PlayersCaption", "Players", new Vector2(24f, -170f), new Vector2(200f, 24f), 16f);
            RectTransform list = NewRect("SessionPlayerList", page);
            Place(list, page, new Vector2(24f, -196f), new Vector2(470f, 4 * 34f));
            RectTransform row = NewRect("PlayerRowTemplate", list);
            Place(row, list, new Vector2(0f, 0f), new Vector2(470f, 30f));
            Image dot = MakeImage(row, "Dot", tpl.Circle, PlayerProfile.Palette[4], new Vector2(6f, -6f), new Vector2(18f, 18f));
            dot.raycastTarget = false;
            MakeLabel(row, "Name", "Player", new Vector2(34f, 0f), new Vector2(330f, 30f), 18f, TextAlignmentOptions.MidlineLeft, Color.white);
            MakeLabel(row, "HostTag", "host", new Vector2(370f, 0f), new Vector2(90f, 30f), 14f, TextAlignmentOptions.MidlineLeft, k_NoteColour);
            row.gameObject.SetActive(false);
            PlayerListView listView = list.gameObject.AddComponent<PlayerListView>();
            listView.m_RowTemplate = row;
            listView.m_RowSpacing = 34f;
            ui.m_PlayerList = listView;

            ui.m_LastChangedText = MakeLabel(page, "SessionLastChanged", "Last change: —", new Vector2(24f, -336f), new Vector2(470f, 24f), 14f,
                TextAlignmentOptions.MidlineLeft, k_DimColour);
            var leave = MakeButton(page, tpl, "SessionLeaveButton", "Leave session", new Vector2(24f, -368f), new Vector2(220f, 46f), 18f);
            ui.m_LeaveButton = leave.button;

            var divider = (RectTransform)Object.Instantiate(tpl.Divider, page, false);
            divider.name = "SessionDivider";
            Place(divider, page, new Vector2(505f, -56f), new Vector2(2f, 358f));

            const float x = 526f;
            var mic = MakeToggleRow(page, tpl, "SessionMicToggle", "Microphone: muted", new Vector2(x, -56f), new Vector2(460f, 50f));
            var voice = MakeToggleRow(page, tpl, "SessionVoiceToggle", "Voice chat: on", new Vector2(x, -114f), new Vector2(460f, 50f));
            var place = MakeToggleRow(page, tpl, "SessionPlaceTableToggle", "Place table", new Vector2(x, -172f), new Vector2(460f, 50f));
            var presenter = MakeToggleRow(page, tpl, "SessionPresenterToggle", "Presenter mode: off", new Vector2(x, -230f), new Vector2(460f, 50f));

            RectTransform alignRow = NewRect("SessionAlignRow", page);
            Place(alignRow, page, new Vector2(x, -288f), new Vector2(460f, 78f));
            var align = MakeButton(alignRow, tpl, "SessionAlignButton", "Align to real table", new Vector2(0f, 0f), new Vector2(220f, 46f), 16f);
            ui.m_AlignHintText = MakeLabel(alignRow, "SessionAlignHint", "", new Vector2(0f, -50f), new Vector2(460f, 24f), 14f,
                TextAlignmentOptions.MidlineLeft, k_NoteColour);

            ui.m_MicRow = mic.root.gameObject; ui.m_MicToggle = mic.toggle; ui.m_MicLabel = mic.label;
            ui.m_VoiceRow = voice.root.gameObject; ui.m_VoiceToggle = voice.toggle; ui.m_VoiceLabel = voice.label;
            ui.m_PlaceTableRow = place.root.gameObject; ui.m_PlaceTableToggle = place.toggle; ui.m_PlaceTableLabel = place.label;
            ui.m_PresenterRow = presenter.root.gameObject; ui.m_PresenterToggle = presenter.toggle; ui.m_PresenterLabel = presenter.label;
            ui.m_AlignRow = alignRow.gameObject; ui.m_AlignButton = align.button; ui.m_AlignButtonLabel = align.label;
            ui.m_SessionRows = new List<RectTransform> { mic.root, voice.root, place.root, presenter.root, alignRow };
            ui.m_SessionRowSpacing = 8f;
        }

        private static void BuildKeyboard(RectTransform page, Templates tpl, SessionIslandUI ui)
        {
            VRKeyboard kb = page.gameObject.AddComponent<VRKeyboard>();
            ui.m_Keyboard = kb;

            kb.m_PromptText = MakeLabel(page, "KeyboardPrompt", "", new Vector2(24f, -56f), new Vector2(700f, 28f), 18f);
            Image entryBg = MakeImage(page, "KeyboardEntryBackground", tpl.Rounded, new Color(0f, 0f, 0f, 0.35f), new Vector2(24f, -88f), new Vector2(660f, 48f));
            entryBg.raycastTarget = false;
            kb.m_EntryText = MakeLabel(page, "KeyboardEntry", "", new Vector2(38f, -88f), new Vector2(636f, 48f), 30f,
                TextAlignmentOptions.MidlineLeft, Color.white);

            const float x0 = 69f, step = 88f, keyW = 80f, keyH = 48f;
            float[] rowY = { -150f, -204f, -258f, -312f };
            string[] rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            kb.m_Keys = new List<VRKeyboard.Key>();

            void AddKey(string name, string label, string value, float col, int r, float width)
            {
                var key = MakeButton(page, tpl, "Key_" + name, label, new Vector2(x0 + col * step, rowY[r]), new Vector2(width, keyH), 20f);
                kb.m_Keys.Add(new VRKeyboard.Key { Button = key.button, Label = key.label, Value = value });
            }

            for (int r = 0; r < 3; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    AddKey(rows[r][c].ToString(), rows[r][c].ToString(), rows[r][c].ToString(), c, r, keyW);
            AddKey("Backspace", "Del", VRKeyboard.k_Backspace, 9, 2, keyW);

            var shift = MakeButton(page, tpl, "Key_Shift", "ABC", new Vector2(x0, rowY[3]), new Vector2(keyW, keyH), 16f);
            kb.m_ShiftButton = shift.button;
            kb.m_ShiftLabel = shift.label;
            for (int c = 0; c < rows[3].Length; c++)
                AddKey(rows[3][c].ToString(), rows[3][c].ToString(), rows[3][c].ToString(), c + 1, 3, keyW);
            AddKey("Space", "Space", VRKeyboard.k_Space, 8, 3, keyW + step);

            RectTransform recent = NewStretch("KeyboardRecent", page);
            kb.m_SuggestionsRoot = recent.gameObject;
            MakeLabel(recent, "RecentCaption", "Recent:", new Vector2(24f, -370f), new Vector2(80f, 44f), 16f);
            kb.m_SuggestionButtons = new List<Button>();
            kb.m_SuggestionLabels = new List<TMP_Text>();
            for (int i = 0; i < 5; i++)
            {
                var b = MakeButton(recent, tpl, "RecentCode" + i, "", new Vector2(104f + i * 118f, -370f), new Vector2(110f, 44f), 16f);
                kb.m_SuggestionButtons.Add(b.button);
                kb.m_SuggestionLabels.Add(b.label);
            }

            var cancel = MakeButton(page, tpl, "KeyboardCancel", "Cancel", new Vector2(712f, -370f), new Vector2(130f, 44f), 16f);
            var submit = MakeButton(page, tpl, "KeyboardSubmit", "OK", new Vector2(854f, -370f), new Vector2(130f, 44f), 16f);
            kb.m_CancelButton = cancel.button;
            kb.m_SubmitButton = submit.button;
            kb.m_SubmitLabel = submit.label;

            EditorUtility.SetDirty(kb);
        }

        // ------------------------------------------------------------------ controller & gates

        private static SessionIslandUI GetOrCreateController(Canvas summoned)
        {
            // Always-active home next to ScatterplotUI (the summoned canvas is inactive until summoned).
            ScatterplotUI scatterUI = Object.FindObjectsByType<ScatterplotUI>(FindObjectsInactive.Include)
                .FirstOrDefault(u => u.gameObject.scene == summoned.gameObject.scene);
            Transform parent = scatterUI != null && scatterUI.transform.parent != null
                ? scatterUI.transform.parent
                : summoned.transform.parent;

            SessionIslandUI ui = Object.FindObjectsByType<SessionIslandUI>(FindObjectsInactive.Include)
                .FirstOrDefault(u => u.gameObject.scene == summoned.gameObject.scene);
            if (ui != null)
            {
                Undo.RecordObject(ui, "Setup Session Island");
                return ui;
            }

            var go = new GameObject(k_ControllerName);
            Undo.RegisterCreatedObjectUndo(go, "Setup Session Island");
            if (parent != null)
                Undo.SetTransformParent(go.transform, parent, "Setup Session Island");
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return Undo.AddComponent<SessionIslandUI>(go);
        }

        private static void SetupGate(Canvas canvas, Templates tpl, IEnumerable<GameObject> excluded)
        {
            PresenterModeUIGate gate = canvas.GetComponent<PresenterModeUIGate>();
            if (gate == null)
                gate = Undo.AddComponent<PresenterModeUIGate>(canvas.gameObject);
            else
                Undo.RecordObject(gate, "Setup Session Island");

            Transform noteT = canvas.transform.Find(k_NoteName);
            TMP_Text note;
            if (noteT == null)
            {
                var rt = (RectTransform)Object.Instantiate(tpl.Label, canvas.transform, false);
                rt.name = k_NoteName;
                Undo.RegisterCreatedObjectUndo(rt.gameObject, "Setup Session Island");
                note = rt.GetComponent<TMP_Text>();
            }
            else
            {
                note = noteT.GetComponent<TMP_Text>();
                Undo.RecordObject(noteT, "Setup Session Island");
                if (note != null) Undo.RecordObject(note, "Setup Session Island");
            }

            if (note != null)
            {
                var canvasRect = ((RectTransform)canvas.transform).rect;
                var rt = note.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;
                rt.sizeDelta = new Vector2(700f, 36f);
                rt.anchoredPosition3D = new Vector3(0f, canvasRect.yMax + 24f, 0f);
                note.text = k_NoteText;
                note.fontSize = 24f;
                note.alignment = TextAlignmentOptions.Center;
                note.color = k_NoteColour;
                note.raycastTarget = false;
                note.gameObject.SetActive(false);
            }

            gate.m_ViewOnlyNote = note != null ? note.gameObject : null;
            gate.m_Excluded = excluded.Where(g => g != null).Distinct().ToList();
            EditorUtility.SetDirty(gate);
        }

        // ------------------------------------------------------------------ templates

        private static bool TryGetTemplates(Transform canvas, out Templates tpl, out string missing)
        {
            var missingList = new List<string>();
            RectTransform Req(string path)
            {
                var t = canvas.Find(path) as RectTransform;
                if (t == null) missingList.Add(path);
                return t;
            }

            tpl = new Templates
            {
                Background = Req("TimeControlsPanelBackground"),
                Title = Req("TimeControlsPanelBackground/Text (TMP)"),
                Button = Req("PreferencesControlsPanelBackground/ShuffleLocksSection/UnlockAllButton"),
                ToggleRow = Req("DatasetControlPanelBackground/TooltipsToggle"),
                Label = Req("PreferencesControlsPanelBackground/ShuffleLocksSection/AxisIslandHeading"),
                Divider = Req("PreferencesControlsPanelBackground/ShuffleLocksSection/Divider"),
            };

            if (tpl.Button != null)
            {
                Button b = tpl.Button.GetComponentInChildren<Button>(true);
                Image front = b != null ? b.transform.Find("Button Front")?.GetComponent<Image>() : null;
                tpl.Rounded = front != null ? front.sprite : null;
                if (b == null || tpl.Rounded == null) missingList.Add("UnlockAllButton's Button / Button Front sprite");
            }
            if (tpl.ToggleRow != null)
            {
                if (tpl.ToggleRow.GetComponentInChildren<Toggle>(true) == null || tpl.ToggleRow.Find("Label") == null)
                    missingList.Add("TooltipsToggle's Toggle / Label");
                Transform knob = FindDeep(tpl.ToggleRow, "Knob");
                tpl.Circle = knob != null ? knob.GetComponent<Image>()?.sprite : null;
                if (tpl.Circle == null) missingList.Add("TooltipsToggle's Knob sprite");
            }
            if (tpl.Title != null && tpl.Title.GetComponent<TMP_Text>() == null) missingList.Add("island title TMP");
            if (tpl.Label != null && tpl.Label.GetComponent<TMP_Text>() == null) missingList.Add("AxisIslandHeading TMP");

            missing = string.Join(", ", missingList);
            return missingList.Count == 0;
        }

        // ------------------------------------------------------------------ builders

        private static (RectTransform root, Button button, TMP_Text label) MakeButton(Transform parent, Templates tpl, string name,
            string text, Vector2 topLeft, Vector2 size, float fontSize)
        {
            var root = (RectTransform)Object.Instantiate(tpl.Button, parent, false);
            root.name = name;
            Place(root, parent, topLeft, size);

            // The row's own (hidden) caption isn't used on the island.
            Transform caption = root.Find("Label");
            if (caption != null)
                Object.DestroyImmediate(caption.gameObject);

            Button button = root.GetComponentInChildren<Button>(true);
            ClearPersistent(button.onClick);
            button.interactable = true;
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = text;
                label.fontSize = fontSize;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.overflowMode = TextOverflowModes.Ellipsis;
            }
            return (root, button, label);
        }

        private static (RectTransform root, Toggle toggle, TMP_Text label) MakeToggleRow(Transform parent, Templates tpl, string name,
            string text, Vector2 topLeft, Vector2 size)
        {
            var root = (RectTransform)Object.Instantiate(tpl.ToggleRow, parent, false);
            root.name = name;
            Place(root, parent, topLeft, size);

            Toggle toggle = root.GetComponentInChildren<Toggle>(true);
            ClearPersistent(toggle.onValueChanged);
            toggle.group = null;
            toggle.isOn = false;
            toggle.interactable = true;

            TMP_Text label = root.Find("Label").GetComponent<TMP_Text>();
            label.text = text;
            label.fontSize = 16f;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            var lrt = label.rectTransform;
            lrt.anchorMax = new Vector2(0.82f, lrt.anchorMax.y);
            // The template's 18-unit-high rect is shorter than a 16pt line; with Ellipsis that hides the text.
            lrt.sizeDelta = new Vector2(lrt.sizeDelta.x, 30f);
            return (root, toggle, label);
        }

        private static TMP_Text MakeLabel(Transform parent, string name, string text, Vector2 topLeft, Vector2 size, float fontSize,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft, Color? colour = null)
        {
            // Template: the Shuffle-locks island headings.
            Templates tpl = s_CurrentTemplates;
            var rt = (RectTransform)Object.Instantiate(tpl.Label, parent, false);
            rt.name = name;
            Place(rt, parent, topLeft, size);
            TMP_Text t = rt.GetComponent<TMP_Text>();
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = alignment;
            t.color = colour ?? k_TextColour;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private static Image MakeImage(Transform parent, string name, Sprite sprite, Color colour, Vector2 topLeft, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            Place(rt, parent, topLeft, size);
            Image img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            img.color = colour;
            img.raycastTarget = false;
            return img;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        private static RectTransform NewStretch(string name, Transform parent)
        {
            RectTransform rt = NewRect(name, parent);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        /// <summary>Top-left anchored placement in the parent's units; keeps the object's own pivot.</summary>
        private static void Place(RectTransform rt, Transform parent, Vector2 topLeft, Vector2 size)
        {
            if (rt.parent != parent)
                rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = size;
            Vector2 p = rt.pivot;
            rt.anchoredPosition3D = new Vector3(topLeft.x + size.x * p.x, topLeft.y - size.y * (1f - p.y), 0f);
        }

        private static void ClearPersistent(UnityEventBase ev)
        {
            for (int i = ev.GetPersistentEventCount() - 1; i >= 0; i--)
                UnityEventTools.RemovePersistentListener(ev, i);
        }

        private static Canvas FindSceneCanvas(string name) =>
            Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c.name == name && c.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(c))
                .OrderByDescending(c => c.gameObject.scene == EditorSceneManager.GetActiveScene())
                .FirstOrDefault();

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                Transform hit = FindDeep(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        // MakeLabel is called from deep inside the builders; the templates are set once per run.
        private static Templates s_CurrentTemplates;

        [MenuItem(k_MenuPath, true)]
        private static bool Validate() => !EditorApplication.isPlaying;
    }
}
