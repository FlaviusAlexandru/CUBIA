using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Flow
{
    /// <summary>
    /// One look for every CUBIA panel (summoned menu, wizard, lobby): translucent light panels with
    /// rounded corners, an accent for the active/selected state, dark text, font sizes and the tile
    /// size of the four-tile menu. The asset lives at Resources/CUBIAMenuTheme.asset so any scene can
    /// load it with MenuTheme.Load(); Tools > CUBIA > Menu > Build Tab Menu creates it if missing.
    ///
    /// Panels built by other features should call StylePanel/StyleText rather than copying colours,
    /// so a later retune of this asset restyles everything at once.
    ///
    /// Dark at runtime (PF-02, pilot 2): in Play mode Load() returns a dark copy of the asset
    /// (Dark(): dark panels, light text, same accent, sprites and font), so the white plot text
    /// reads against dark menus and a dark studio. Edit mode (menu builders, self-tests) still gets
    /// the asset itself, so nothing baked into scenes changes; DarkStudio recolours the menus that
    /// were baked into the scenes with the light colours (RemapToDark) when a scene loads.
    /// </summary>
    [CreateAssetMenu(menuName = "CUBIA/Menu Theme", fileName = "CUBIAMenuTheme")]
    public class MenuTheme : ScriptableObject
    {
        public const string ResourceName = "CUBIAMenuTheme";

        [Header("Colours")]
        public Color PanelColor = new Color(0.93f, 0.95f, 0.98f, 0.82f);
        public Color PanelOutline = new Color(0.55f, 0.75f, 0.95f, 0.9f);
        public Color Accent = new Color(0.20f, 0.55f, 0.95f, 1f);
        public Color Text = new Color(0.10f, 0.13f, 0.18f, 1f);
        public Color TextMuted = new Color(0.35f, 0.40f, 0.48f, 1f);
        public Color IconTint = new Color(0.15f, 0.40f, 0.75f, 1f);
        [Tooltip("Face of the active (open) tile; inactive tiles use PanelColor.")]
        public Color TileActiveColor = new Color(1f, 1f, 1f, 0.95f);
        [Tooltip("Thin separators inside panels.")]
        public Color Divider = new Color(0.10f, 0.13f, 0.18f, 0.18f);
        [Tooltip("Fill of PanelKit's secondary buttons.")]
        public Color ButtonColor = new Color(1f, 1f, 1f, 0.92f);
        [Tooltip("Colour multiplier of PanelKit buttons (above 1 lets a dark button brighten when highlighted).")]
        public float ButtonColorMultiplier = 1f;
        public Color ButtonHighlightedTint = new Color(0.90f, 0.93f, 0.98f, 1f);
        public Color ButtonPressedTint = new Color(0.78f, 0.83f, 0.92f, 1f);

        [Header("Sprites")]
        [Tooltip("9-sliced rounded rectangle used for panel and tile faces.")]
        public Sprite PanelSprite;
        [Tooltip("9-sliced rounded outline matching PanelSprite (accent ring of the active tile).")]
        public Sprite OutlineSprite;

        [Header("Text")]
        public TMP_FontAsset Font;
        public float TitleFontSize = 36f;
        public float BodyFontSize = 22f;

        [Header("Tiles (canvas units)")]
        public Vector2 TileSize = new Vector2(280f, 190f);
        public float TileIconSize = 84f;

        static MenuTheme s_Cached;
        static MenuTheme s_Defaults;
        static MenuTheme s_Dark;

        /// <summary>False to keep the light theme in Play mode (default true: PF-02).</summary>
        public static bool DarkInPlayMode = true;

        /// <summary>The theme to build with: the asset (light) in edit mode, its dark copy in Play mode.</summary>
        public static MenuTheme Load()
        {
            MenuTheme light = LoadLight();
            if (!DarkInPlayMode || !Application.isPlaying)
                return light;
            if (s_Dark == null || s_Dark.name != light.name + " (dark)")
                s_Dark = Dark(light);
            return s_Dark;
        }

        /// <summary>The asset as saved (the light theme), or defaults until the asset exists.</summary>
        public static MenuTheme LoadLight()
        {
            if (s_Cached == null)
                s_Cached = Resources.Load<MenuTheme>(ResourceName);
            if (s_Cached != null)
                return s_Cached;

            // Defaults until the asset exists; not cached as s_Cached so a later-created asset is found.
            if (s_Defaults == null)
                s_Defaults = CreateInstance<MenuTheme>();
            return s_Defaults;
        }

        // ---- dark variant (PF-02) ----------------------------------------------------------------

        public static readonly Color DarkPanel = new Color(0.12f, 0.14f, 0.19f, 0.94f);
        public static readonly Color DarkPanelOutline = new Color(0.32f, 0.46f, 0.66f, 0.9f);
        public static readonly Color DarkText = new Color(0.96f, 0.97f, 0.99f, 1f);
        public static readonly Color DarkTextMuted = new Color(0.66f, 0.71f, 0.79f, 1f);
        public static readonly Color DarkIconTint = new Color(0.50f, 0.72f, 1f, 1f);
        public static readonly Color DarkTileActive = new Color(0.22f, 0.27f, 0.36f, 0.96f);
        public static readonly Color DarkDivider = new Color(0.96f, 0.97f, 0.99f, 0.18f);
        public static readonly Color DarkButton = new Color(0.20f, 0.24f, 0.31f, 0.96f);

        // None of the dark colours may equal a light theme colour (RemapToDark runs again on
        // graphics that are already dark): e.g. the dark text is not the light panel's RGB.

        /// <summary>A dark copy of light: dark panels and buttons, light text; accent, sprites, font and sizes kept.</summary>
        public static MenuTheme Dark(MenuTheme light)
        {
            MenuTheme dark = light != null ? Instantiate(light) : CreateInstance<MenuTheme>();
            dark.name = (light != null ? light.name : "MenuTheme") + " (dark)";
            dark.hideFlags = HideFlags.DontSave;
            dark.PanelColor = DarkPanel;
            dark.PanelOutline = DarkPanelOutline;
            dark.Text = DarkText;
            dark.TextMuted = DarkTextMuted;
            dark.IconTint = DarkIconTint;
            dark.TileActiveColor = DarkTileActive;
            dark.Divider = DarkDivider;
            dark.ButtonColor = DarkButton;
            // Dark buttons brighten when pointed at: the tints are scaled by the multiplier, and
            // normal / selected divide it out again.
            dark.ButtonColorMultiplier = 1.5f;
            dark.ButtonHighlightedTint = new Color(1f, 1f, 1f, 1f);
            dark.ButtonPressedTint = new Color(0.9f, 0.9f, 0.9f, 1f);
            return dark;
        }

        /// <summary>The ColorBlock of a PanelKit button for this theme (light: the pre-PF-02 block, unchanged).</summary>
        public ColorBlock ButtonColors()
        {
            float m = Mathf.Max(0.01f, ButtonColorMultiplier);
            var normal = new Color(1f / m, 1f / m, 1f / m, 1f);
            return new ColorBlock
            {
                normalColor = normal,
                highlightedColor = ButtonHighlightedTint,
                pressedColor = ButtonPressedTint,
                selectedColor = normal, // no sticky highlight after a ray click
                disabledColor = new Color(normal.r, normal.g, normal.b, 0.45f),
                colorMultiplier = m,
                fadeDuration = 0.08f,
            };
        }

        /// <summary>
        /// The colour a graphic baked with the light theme should have in the dark theme, or c when
        /// it is not one of the light theme's colours (accent, data colours, widget colours stay).
        /// Whole-colour matches first (panel face, active tile, button fill), then text-like colours
        /// by RGB with their own alpha kept (hints at 80 %, dividers at 18 %).
        /// </summary>
        public static Color RemapToDark(Color c, MenuTheme light, MenuTheme dark)
        {
            if (light == null || dark == null)
                return c;
            if (Near(c, light.PanelColor, true)) return dark.PanelColor;
            if (Near(c, light.TileActiveColor, true)) return dark.TileActiveColor;
            if (Near(c, light.ButtonColor, true)) return dark.ButtonColor;
            if (Near(c, light.Text, false)) return WithAlpha(dark.Text, c.a);
            if (Near(c, light.TextMuted, false)) return WithAlpha(dark.TextMuted, c.a);
            if (Near(c, light.IconTint, false)) return WithAlpha(dark.IconTint, c.a);
            if (Near(c, light.PanelOutline, false)) return WithAlpha(dark.PanelOutline, c.a);
            if (Near(c, light.PanelColor, false)) return WithAlpha(dark.PanelColor, c.a);
            return c;
        }

        static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        // Colours round-trip through 8-bit serialisation: compare with a 1.5/255 tolerance.
        static bool Near(Color a, Color b, bool alphaToo)
        {
            const float tol = 1.5f / 255f;
            return Mathf.Abs(a.r - b.r) <= tol && Mathf.Abs(a.g - b.g) <= tol && Mathf.Abs(a.b - b.b) <= tol &&
                   (!alphaToo || Mathf.Abs(a.a - b.a) <= tol);
        }

        /// <summary>Rounded translucent light panel background.</summary>
        public void StylePanel(Image image)
        {
            if (image == null) return;
            image.sprite = PanelSprite;
            image.type = PanelSprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.pixelsPerUnitMultiplier = 1f;
            image.color = PanelColor;
        }

        /// <summary>Theme font and colour; muted for secondary text such as headings and hints.</summary>
        public void StyleText(TMP_Text text, bool muted = false)
        {
            if (text == null) return;
            if (Font != null) text.font = Font;
            text.color = muted ? TextMuted : Text;
        }
    }
}
