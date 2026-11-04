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

        public static MenuTheme Load()
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
