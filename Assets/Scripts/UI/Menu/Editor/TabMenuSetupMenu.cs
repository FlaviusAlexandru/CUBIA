using System.Collections.Generic;
using System.Linq;
using DataViz.Flow;
using DataViz.Networking;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using static DataViz.MenuUI.EditorTools.MenuBuildUtil;

namespace DataViz.MenuUI.EditorTools
{
    /// <summary>
    /// Tools > CUBIA > Menu > Build Tab Menu: turns the summoned menu's hand-placed islands into the
    /// four-tile menu (Map, Animate, Filter, Settings) and the wrist panel into quick actions.
    ///
    /// The EXISTING row GameObjects are re-parented into the four panels, so every reference to them
    /// (ScatterplotUI, ShuffleLockPanel, GlyphRangeEditor, MappingDropSlot, wrist MirroredUIControls)
    /// keeps working; only the per-panel lock toggles and the three Settings buttons are new.
    /// Emptied islands are deleted. Idempotent: rows are looked up in the panels first, then in their
    /// old islands, and every position/style is set absolutely. Marks the scene dirty, never saves.
    ///
    /// Canvas units: 1 unit = 0.47 mm at the menu's scale; the canvas origin is summoned at eye height.
    /// </summary>
    public static class TabMenuSetupMenu
    {
        private const string k_CanvasName = "SummonedMenuCanvas";
        private const string k_ThemePath = "Assets/Resources/" + MenuTheme.ResourceName + ".asset";
        private const string k_PanelSpritePath = "Assets/VRTemplateAssets/Sprites/UI/Round Radius 10.png";
        private const string k_OutlineSpritePath = "Assets/VRTemplateAssets/Sprites/UI/Round Radius 10 Outline.png";
        private const string k_FontPath = "Assets/VRTemplateAssets/Fonts/Inter/Inter-Regular SDF.asset";

        // Layout (canvas units).
        private const float k_Width = 1240f;
        private const float k_TileGap = 40f;
        private const float k_TileRowTop = 140f;          // tile row centre lands just above eye height
        private const float k_SessionStripTop = -70f;     // lobby agent's strip, between tiles and panels
        private const float k_SessionStripHeight = 56f;
        private const float k_PanelTop = -146f;
        private const float k_RowPitch = 110f;
        private const float k_FirstRowTop = 80f;
        private const float k_LockOffset = 246f;          // lock toggle to the right of a 226-wide row
        private const float k_LockDrop = 20f;             // centres a 60-tall toggle on a 100-tall row

        // Old islands, in the state the hand-placed scene has them.
        private const string k_Axis = "AxisControlPanelBackground";
        private const string k_Point = "PointControlPanelBackground";
        private const string k_Dataset = "DatasetControlPanelBackground";
        private const string k_Time = "TimeControlsPanelBackground";
        private const string k_Prefs = "PreferencesControlsPanelBackground";
        private const string k_Glyph = "GlyphControlsPanelBackground";
        private const string k_GlyphRanges = "GlyphRangesPanelBackground";
        private const string k_FilterRanges = "FilterRangesPanelBackground";
        private static readonly string[] k_OldIslands =
            { k_Axis, k_Point, k_Dataset, k_Time, k_Prefs, k_Glyph, k_GlyphRanges, k_FilterRanges };

        private const string k_SessionIsland = "SessionIslandBackground"; // removed by the lobby feature
        private const string k_RetiredName = "RetiredControls";

        private class Context
        {
            public Transform Canvas;
            public MenuTheme Theme;
            public readonly List<Transform> Panels = new();
            public readonly List<GameObject> Excluded = new(); // stay usable in presenter mode
            public readonly List<string> Missing = new();
            public RectTransform Retired;
        }

        [MenuItem("Tools/CUBIA/Menu/Build Tab Menu")]
        private static void Build()
        {
            Transform canvas = FindCanvas(k_CanvasName);
            if (canvas == null)
            {
                Debug.LogError($"[TabMenuSetup] No '{k_CanvasName}' in the open scenes.");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            var ctx = new Context { Canvas = canvas, Theme = EnsureTheme() };
            ctx.Retired = GetOrCreateRect(canvas, k_RetiredName);
            SetActive(ctx.Retired.gameObject, false);

            RectTransform map = BuildMapPanel(ctx);
            RectTransform animate = BuildAnimatePanel(ctx);
            RectTransform filter = BuildFilterPanel(ctx);
            RectTransform settings = BuildSettingsPanel(ctx);
            BuildChrome(ctx, map, animate, filter, settings);
            RemoveOldIslands(ctx);
            ConfigurePresenterGate(ctx);
            WristQuickActionsBuilder.Build(ctx.Theme);

            foreach (Transform panel in ctx.Panels)
                RecolourLightText(panel, ctx.Theme);

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(canvas.gameObject.scene);

            if (ctx.Missing.Count > 0)
                Debug.LogWarning("[TabMenuSetup] Not found (skipped): " + string.Join(", ", ctx.Missing));
            Debug.Log("[TabMenuSetup] Tab menu built. Review, then save the scene.");
        }

        // ---------------------------------------------------------------- theme

        private static MenuTheme EnsureTheme()
        {
            var theme = AssetDatabase.LoadAssetAtPath<MenuTheme>(k_ThemePath);
            if (theme == null)
            {
                theme = ScriptableObject.CreateInstance<MenuTheme>();
                AssetDatabase.CreateAsset(theme, k_ThemePath);
            }

            if (theme.PanelSprite == null || theme.OutlineSprite == null || theme.Font == null)
            {
                Undo.RecordObject(theme, UndoName);
                if (theme.PanelSprite == null) theme.PanelSprite = AssetDatabase.LoadAssetAtPath<Sprite>(k_PanelSpritePath);
                if (theme.OutlineSprite == null) theme.OutlineSprite = AssetDatabase.LoadAssetAtPath<Sprite>(k_OutlineSpritePath);
                if (theme.Font == null) theme.Font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(k_FontPath);
                EditorUtility.SetDirty(theme);
                AssetDatabase.SaveAssetIfDirty(theme);
            }
            return theme;
        }

        // ---------------------------------------------------------------- panels

        private static RectTransform Panel(Context ctx, string name, float height)
        {
            RectTransform panel = GetOrCreateRect(ctx.Canvas, name);
            SetCentred(panel, new Vector2(0.5f, 1f), new Vector2(0f, k_PanelTop), new Vector2(k_Width, height));
            StylePanel(panel.gameObject, ctx.Theme);
            ctx.Panels.Add(panel);
            return panel;
        }

        /// <summary>Moves an existing row (found in the panels, the retired container or its old
        /// island) into panel at (left, top).</summary>
        private static RectTransform Row(Context ctx, Transform panel, string name, string oldIsland, float left, float top)
        {
            RectTransform row = FindRow(ctx, name, oldIsland);
            if (row == null) return null;
            Reparent(row, panel);
            PlaceTopLeft(row, left, top);
            return row;
        }

        private static RectTransform FindRow(Context ctx, string name, string oldIsland)
        {
            var parents = new List<Transform>(ctx.Panels) { ctx.Retired, ctx.Canvas.Find(oldIsland) };
            var row = (RectTransform)FindChildIn(name, parents.ToArray());
            if (row == null)
                ctx.Missing.Add($"{oldIsland}/{name}");
            return row;
        }

        private static float RowTop(int index) => k_FirstRowTop + index * k_RowPitch;

        private static RectTransform BuildMapPanel(Context ctx)
        {
            const float a = 40f, b = 450f, c = 860f;
            RectTransform panel = Panel(ctx, "Panel_Map", RowTop(3) + 40f);

            Heading(panel, "Heading_Axes", "Axes", a, 24f, ctx.Theme);
            Row(ctx, panel, "xDropDown", k_Axis, a, RowTop(0));
            Row(ctx, panel, "yDropDown", k_Axis, a, RowTop(1));
            Row(ctx, panel, "zDropDown", k_Axis, a, RowTop(2));
            LockToggle(ctx, panel, MappingTarget.X, a + k_LockOffset, RowTop(0) + k_LockDrop);
            LockToggle(ctx, panel, MappingTarget.Y, a + k_LockOffset, RowTop(1) + k_LockDrop);
            LockToggle(ctx, panel, MappingTarget.Z, a + k_LockOffset, RowTop(2) + k_LockDrop);

            Heading(panel, "Heading_Points", "Points", b, 24f, ctx.Theme);
            Row(ctx, panel, "ColorColumnDropdown", k_Point, b, RowTop(0));
            Row(ctx, panel, "SizeModeToggle", k_Point, b, RowTop(1));
            Row(ctx, panel, "PointSizeSlider", k_Point, b, RowTop(2));
            LockToggle(ctx, panel, MappingTarget.Colour, b + k_LockOffset, RowTop(0) + k_LockDrop);
            LockToggle(ctx, panel, MappingTarget.Size, b + k_LockOffset, RowTop(2) + k_LockDrop);

            // << Shuffle >>: the arrow rows' buttons fill the right two thirds of their 58-wide
            // rows, so these offsets butt the three visible buttons together (as on the old island).
            Heading(panel, "Heading_Explore", "Shuffle & position", c, 24f, ctx.Theme);
            float shuffleLeft = c - 36f;
            Row(ctx, panel, "ShuffleBackwardButton", k_Dataset, shuffleLeft + 17f, RowTop(0));
            Row(ctx, panel, "ShuffleButton", k_Dataset, shuffleLeft, RowTop(0));
            Row(ctx, panel, "ShuffleForwardButton", k_Dataset, shuffleLeft + 208f, RowTop(0));
            Row(ctx, panel, "ResetPositionButton", k_Point, shuffleLeft, RowTop(1));
            return panel;
        }

        private static RectTransform BuildAnimatePanel(Context ctx)
        {
            const float a = 40f, b = 450f, c = 860f;
            RectTransform panel = Panel(ctx, "Panel_Animate", RowTop(1) + 40f);

            Heading(panel, "Heading_TimeColumn", "Time column", a, 24f, ctx.Theme);
            Row(ctx, panel, "TimeColumnDropdown", k_Time, a, RowTop(0));
            LockToggle(ctx, panel, MappingTarget.Time, a + k_LockOffset, RowTop(0) + k_LockDrop);

            Heading(panel, "Heading_Scrub", "Scrub", b, 24f, ctx.Theme);
            Row(ctx, panel, "TimeScrubSlider", k_Time, b, RowTop(0));

            Heading(panel, "Heading_Playback", "Playback", c, 24f, ctx.Theme);
            Row(ctx, panel, "PlayPauseButton", k_Time, c - 36f, RowTop(0));
            return panel;
        }

        private static RectTransform BuildFilterPanel(Context ctx)
        {
            const float a = 40f, g = 660f;
            const float rangesTop = 500f;
            RectTransform panel = Panel(ctx, "Panel_Filter", rangesTop + 367f + 30f);

            Heading(panel, "Heading_Filter", "Filter", a, 24f, ctx.Theme);
            Row(ctx, panel, "FilterLabelDropdown", k_Prefs, a, RowTop(0));
            Row(ctx, panel, "FilterIndexDropdown", k_Prefs, a, RowTop(1));
            RectTransform chips = Row(ctx, panel, "FilterChipsText", k_Prefs, a, RowTop(2));
            if (chips != null)
            {
                var chipsText = chips.GetComponent<TMP_Text>();
                Undo.RecordObject(chipsText, UndoName);
                chipsText.alignment = TextAlignmentOptions.MidlineLeft;
            }
            Row(ctx, panel, "ClearFiltersButton", k_Prefs, a, RowTop(2) + 60f);

            Heading(panel, "Heading_Glyphs", "Glyphs", g, 24f, ctx.Theme);
            Row(ctx, panel, "GlyphColumnDropdown", k_Glyph, g, RowTop(0));
            Row(ctx, panel, "GlyphLabelDropdown", k_Glyph, g, RowTop(1));
            Row(ctx, panel, "GlyphShapeDropdown", k_Glyph, g, RowTop(2));
            LockToggle(ctx, panel, MappingTarget.Glyph, g + k_LockOffset, RowTop(0) + k_LockDrop);

            // The two range editors (600 x 367) side by side under their columns.
            Row(ctx, panel, "FilterRangeEditor", k_FilterRanges, 20f, rangesTop);
            Row(ctx, panel, "GlyphRangeEditor", k_GlyphRanges, 630f, rangesTop);
            return panel;
        }

        private static RectTransform BuildSettingsPanel(Context ctx)
        {
            const float a = 40f, b = 360f, c = 840f;
            RectTransform panel = Panel(ctx, "Panel_Settings", 70f + 642f + 40f);

            // Buttons are cloned from Reset position (already moved into the Map panel).
            RectTransform template = FindRow(ctx, "ResetPositionButton", k_Point);

            Heading(panel, "Heading_General", "General", a, 24f, ctx.Theme);
            if (template != null)
            {
                ctx.Excluded.Add(ActionButton(panel, template, "MultiplayerButton", "Multiplayer",
                    MenuActionButton.MenuAction.EnterMultiplayerLobby, a, RowTop(0)));
                ActionButton(panel, template, "ChangeDatasetButton", "Change dataset…",
                    MenuActionButton.MenuAction.ChangeDataset, a, RowTop(1));
                ctx.Excluded.Add(ActionButton(panel, template, "RunImportWizardButton", "Run import wizard",
                    MenuActionButton.MenuAction.RunImportWizard, a, RowTop(2)));
            }
            Row(ctx, panel, "TooltipsToggle", k_Dataset, a, RowTop(3));

            Heading(panel, "Heading_Columns", "Bookmarks & blacklist", b, 24f, ctx.Theme);
            Row(ctx, panel, "BookmarkColumnDropdown", k_Prefs, b, RowTop(0));
            Row(ctx, panel, "BlacklistColumnDropdown", k_Prefs, b, RowTop(1));

            Heading(panel, "Heading_ShuffleLocks", "Shuffle locks", c, 24f, ctx.Theme);
            RectTransform locks = Row(ctx, panel, "ShuffleLocksSection", k_Prefs, c, 70f);
            Transform divider = locks != null ? locks.Find("Divider") : null;
            if (divider != null)
            {
                var image = divider.GetComponent<Image>();
                Undo.RecordObject(image, UndoName);
                image.color = ctx.Theme.Divider;
            }

            // Dataset choice moved to the import wizard: the dropdown stays (ScatterplotUI and the
            // dataset sync still drive it) but is no longer shown.
            RectTransform dataset = FindRow(ctx, "DatasetDropdown", k_Dataset);
            if (dataset != null)
                Reparent(dataset, ctx.Retired);
            return panel;
        }

        /// <summary>A Shuffle lock toggle for one field, cloned from the Settings overview's X toggle.</summary>
        private static void LockToggle(Context ctx, Transform panel, MappingTarget target, float left, float top)
        {
            string name = $"LockToggle_{target}";
            var toggleRow = panel.Find(name) as RectTransform;
            if (toggleRow == null)
            {
                // The overview may already sit in a panel built later in this pass, so look it up
                // only when a clone is actually needed.
                var template = FindRow(ctx, "ShuffleLocksSection", k_Prefs)?.Find("XShuffleLockToggle") as RectTransform;
                if (template == null)
                    return;
                toggleRow = GetOrClone(panel, name, template);
            }
            PlaceTopLeft(toggleRow, left, top);

            var lockToggle = GetOrAdd<FieldLockToggle>(toggleRow.gameObject);
            Undo.RecordObject(lockToggle, UndoName);
            lockToggle.m_Target = target;
            lockToggle.m_Toggle = toggleRow.GetComponentInChildren<Toggle>(true);
            lockToggle.m_Label = toggleRow.Find("Label")?.GetComponent<TMP_Text>();
            lockToggle.m_Group = target == MappingTarget.Size ? GetOrAdd<CanvasGroup>(toggleRow.gameObject) : null;
            if (lockToggle.m_Label != null)
            {
                Undo.RecordObject(lockToggle.m_Label, UndoName);
                lockToggle.m_Label.text = $"{ShuffleLocks.DisplayName(target)}: Locked";
            }
            ctx.Excluded.Add(toggleRow.gameObject); // local preference, like the overview
        }

        private static GameObject ActionButton(Transform panel, RectTransform template, string name, string text,
                                               MenuActionButton.MenuAction action, float left, float top)
        {
            RectTransform row = GetOrClone(panel, name, template);
            PlaceTopLeft(row, left, top);

            // Full-width button (the template leaves its left third to a hidden label).
            var button = row.GetComponentInChildren<Button>(true);
            var buttonRect = (RectTransform)button.transform;
            Undo.RecordObject(buttonRect, UndoName);
            buttonRect.anchorMin = new Vector2(0f, buttonRect.anchorMin.y);
            buttonRect.anchorMax = new Vector2(1f, buttonRect.anchorMax.y);
            buttonRect.offsetMin = new Vector2(0f, buttonRect.offsetMin.y);
            buttonRect.offsetMax = new Vector2(0f, buttonRect.offsetMax.y);

            var label = button.GetComponentInChildren<TMP_Text>(true);
            Undo.RecordObject(label, UndoName);
            label.text = text;

            var actionButton = GetOrAdd<MenuActionButton>(button.gameObject);
            Undo.RecordObject(actionButton, UndoName);
            actionButton.m_Action = action;
            return row.gameObject;
        }

        // ---------------------------------------------------------------- tiles, palette, anchors

        private static void BuildChrome(Context ctx, RectTransform map, RectTransform animate,
                                        RectTransform filter, RectTransform settings)
        {
            MenuTheme theme = ctx.Theme;

            RectTransform bar = GetOrCreateRect(ctx.Canvas, "TileBar");
            SetCentred(bar, new Vector2(0.5f, 1f), new Vector2(0f, k_TileRowTop), new Vector2(k_Width, theme.TileSize.y));
            ctx.Excluded.Add(bar.gameObject); // opening a panel is always allowed

            var tabs = new List<MenuTab>
            {
                Tile(bar, 0, "Map", "map", map, true, theme),
                Tile(bar, 1, "Animate", "animate", animate, true, theme),
                Tile(bar, 2, "Filter", "filter", filter, true, theme),
                Tile(bar, 3, "Settings", "settings", settings, false, theme),
            };

            RectTransform strip = GetOrCreateRect(ctx.Canvas, "SessionStripAnchor");
            SetCentred(strip, new Vector2(0.5f, 1f), new Vector2(0f, k_SessionStripTop), new Vector2(k_Width, k_SessionStripHeight));

            Transform palette = ctx.Canvas.Find("BookmarkPalettePanel");
            if (palette != null)
            {
                var paletteRect = (RectTransform)palette;
                // Its y is set by MenuTabBar, under whichever panel is open.
                SetCentred(paletteRect, new Vector2(0.5f, 1f), new Vector2(0f, paletteRect.anchoredPosition.y), new Vector2(k_Width, paletteRect.sizeDelta.y));
                StylePanel(palette.gameObject, theme);
                RecolourLightText(palette, theme);
                palette.SetAsLastSibling(); // dragged blocks render over the panels
            }
            else
            {
                ctx.Missing.Add("BookmarkPalettePanel");
            }

            Transform note = ctx.Canvas.Find("PresenterModeNote");
            if (note != null)
            {
                var noteRect = (RectTransform)note;
                Undo.RecordObject(noteRect, UndoName);
                noteRect.anchoredPosition = new Vector2(0f, k_TileRowTop + 32f);
            }

            Transform session = ctx.Canvas.Find(k_SessionIsland);
            if (session != null)
                SetActive(session.gameObject, false);

            var tabBar = GetOrAdd<MenuTabBar>(bar.gameObject);
            Undo.RecordObject(tabBar, UndoName);
            tabBar.m_Tabs = tabs;
            tabBar.m_BookmarkPalette = palette as RectTransform;
            foreach (MenuTab tab in tabs)
            {
                Undo.RecordObject(tab, UndoName);
                foreach (Graphic graphic in tab.GetComponentsInChildren<Graphic>(true))
                    Undo.RecordObject(graphic, UndoName);
                Undo.RecordObject(tab.m_Panel.gameObject, UndoName);
            }
            if (palette != null)
            {
                Undo.RecordObject(palette.gameObject, UndoName);
                Undo.RecordObject(palette, UndoName);
            }
            tabBar.Show(tabs[0]); // edit-time state: Map open, so the scene shows a sensible layout
        }

        private static MenuTab Tile(RectTransform bar, int index, string label, string icon, RectTransform panel,
                                    bool showsPalette, MenuTheme theme)
        {
            RectTransform tile = GetOrCreateRect(bar, $"Tile_{label}");
            Undo.RecordObject(tile, UndoName);
            tile.pivot = new Vector2(0f, 1f);
            tile.sizeDelta = theme.TileSize;
            tile.localScale = Vector3.one;
            PlaceTopLeft(tile, index * (theme.TileSize.x + k_TileGap), 0f);

            var face = GetOrAdd<Image>(tile.gameObject);
            Undo.RecordObject(face, UndoName);
            theme.StylePanel(face);

            var button = GetOrAdd<Button>(tile.gameObject);
            Undo.RecordObject(button, UndoName);
            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(0.90f, 0.95f, 1f),
                pressedColor = new Color(0.80f, 0.87f, 0.98f),
                selectedColor = Color.white,
                disabledColor = new Color(0.78f, 0.78f, 0.78f, 0.5f),
                colorMultiplier = 1f,
                fadeDuration = 0.08f
            };

            RectTransform outlineRect = GetOrCreateRect(tile, "ActiveOutline");
            Stretch(outlineRect);
            var outline = GetOrAdd<Image>(outlineRect.gameObject);
            Undo.RecordObject(outline, UndoName);
            outline.sprite = theme.OutlineSprite;
            outline.type = Image.Type.Sliced;
            outline.raycastTarget = false;

            RectTransform iconRect = GetOrCreateRect(tile, "Icon");
            Undo.RecordObject(iconRect, UndoName);
            iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0.5f, 1f);
            iconRect.sizeDelta = Vector2.one * theme.TileIconSize;
            iconRect.anchoredPosition = new Vector2(0f, -26f);
            iconRect.localScale = Vector3.one;
            var iconImage = GetOrAdd<Image>(iconRect.gameObject);
            Undo.RecordObject(iconImage, UndoName);
            iconImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{MenuIconImportSettings.IconFolder}{icon}.svg");
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            RectTransform labelRect = GetOrCreateRect(tile, "Label");
            Undo.RecordObject(labelRect, UndoName);
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.sizeDelta = new Vector2(-20f, theme.TitleFontSize + 12f);
            labelRect.anchoredPosition = new Vector2(0f, 20f);
            labelRect.localScale = Vector3.one;
            var text = GetOrAdd<TextMeshProUGUI>(labelRect.gameObject);
            Undo.RecordObject(text, UndoName);
            theme.StyleText(text);
            text.text = label;
            text.fontSize = theme.TitleFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;

            var tab = GetOrAdd<MenuTab>(tile.gameObject);
            Undo.RecordObject(tab, UndoName);
            tab.m_Id = label;
            tab.m_Button = button;
            tab.m_Panel = panel;
            tab.m_ShowsBookmarkPalette = showsPalette;
            tab.m_Face = face;
            tab.m_ActiveOutline = outline;
            tab.m_Icon = iconImage;
            tab.m_Label = text;
            return tab;
        }

        // ---------------------------------------------------------------- cleanup and gate

        /// <summary>Deletes the old islands once only their backgrounds and headings are left; an
        /// island still holding anything else (a row this builder doesn't know) is kept and logged.</summary>
        private static void RemoveOldIslands(Context ctx)
        {
            foreach (string name in k_OldIslands)
            {
                Transform island = ctx.Canvas.Find(name);
                if (island == null) continue;

                bool onlyDecoration = island.GetComponentsInChildren<Component>(true).All(c =>
                    c is Transform || c is CanvasRenderer || c is Image || c is TextMeshProUGUI);
                if (onlyDecoration)
                    Undo.DestroyObjectImmediate(island.gameObject);
                else
                    Debug.LogWarning($"[TabMenuSetup] '{name}' still has unknown children; left in place.", island);
            }
        }

        /// <summary>The tiles, per-panel locks and the lobby / import-wizard buttons stay usable in
        /// presenter mode. Rows stay two levels under the canvas (canvas > panel > row), which is
        /// the gate's unit depth, so nothing else changes.</summary>
        private static void ConfigurePresenterGate(Context ctx)
        {
            var gate = ctx.Canvas.GetComponent<PresenterModeUIGate>();
            if (gate == null) return;

            Undo.RecordObject(gate, UndoName);
            gate.m_Excluded.RemoveAll(go => go == null);
            foreach (GameObject go in ctx.Excluded)
                if (!gate.m_Excluded.Contains(go))
                    gate.m_Excluded.Add(go);
        }
    }
}
