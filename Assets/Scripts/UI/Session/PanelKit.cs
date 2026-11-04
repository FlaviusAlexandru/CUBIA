using DataViz.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// Builds the light, rounded uGUI widgets used by the lobby, the session strip and the confirm
    /// dialog at runtime, coloured from <see cref="MenuTheme"/>. Everything is placed top-left
    /// anchored in canvas units (1 unit = 1 mm on the world canvases made here), so layouts read
    /// like a sketch: x to the right, y downwards (negative).
    ///
    /// The rounded-rectangle, outline and circle sprites are generated once in memory (so corner radii
    /// can be chosen per widget and nothing needs a serialized asset); colours and font come from the theme.
    /// </summary>
    public static class PanelKit
    {
        public enum Style { Primary, Secondary, Quiet }

        /// <summary>Amber used for warnings ("You need …"); not part of MenuTheme.</summary>
        public static readonly Color Warning = new(0.80f, 0.45f, 0.05f, 1f);

        const int k_SpriteSize = 64;
        const float k_SpriteCorner = 24f; // corner radius baked into the rounded sprites (px)

        static Sprite s_Rounded, s_Outline, s_Circle;

        public static MenuTheme Theme => MenuTheme.Load();

        // ------------------------------------------------------------------ canvases

        /// <summary>A world-space canvas of size (canvas units) at metresPerUnit, usable by XR rays and the mouse.</summary>
        public static Canvas WorldCanvas(string name, Transform parent, Vector2 size, float metresPerUnit = 0.001f)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster), typeof(TrackedDeviceGraphicRaycaster));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * metresPerUnit;

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 2f;
            return canvas;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>An empty rect at (x, y) from the parent's top-left, w by h units.</summary>
        public static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Place(rt, x, y, w, h);
            return rt;
        }

        /// <summary>An empty rect filling its parent.</summary>
        public static RectTransform Fill(Transform parent, string name)
        {
            RectTransform rt = Rect(parent, name, 0f, 0f, 0f, 0f);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        public static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);
        }

        // ------------------------------------------------------------------ widgets

        /// <summary>A thin horizontal or vertical separator line in the theme's divider colour.</summary>
        public static Image Divider(Transform parent, string name, float x, float y, float w, float h)
        {
            RectTransform rt = Rect(parent, name, x, y, w, h);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = Theme.Divider;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A rounded panel; outline (if given) is drawn as a thin rounded ring on top of the fill.</summary>
        public static Image Panel(Transform parent, string name, float x, float y, float w, float h,
                                  Color fill, Color? outline = null, float radius = 24f)
        {
            RectTransform rt = Rect(parent, name, x, y, w, h);
            Image image = AddImage(rt.gameObject, RoundedSprite, fill, radius);
            image.raycastTarget = true; // panels swallow rays so nothing behind them is clicked
            if (outline.HasValue)
            {
                Image ring = AddImage(Fill(rt, "Outline").gameObject, OutlineSprite, outline.Value, radius);
                ring.raycastTarget = false;
            }
            return image;
        }

        public static TMP_Text Label(Transform parent, string name, string text, float x, float y, float w, float h,
                                     float fontSize, Color colour, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft,
                                     bool wrap = false)
        {
            RectTransform rt = Rect(parent, name, x, y, w, h);
            var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Theme.Font != null) label.font = Theme.Font;
            label.text = text;
            label.fontSize = fontSize;
            label.color = colour;
            label.alignment = align;
            label.raycastTarget = false;
            label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            label.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            return label;
        }

        public static Image Icon(Transform parent, string name, Sprite sprite, float x, float y, float size, Color tint)
        {
            RectTransform rt = Rect(parent, name, x, y, size, size);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = tint;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.enabled = sprite != null;
            return image;
        }

        public static Image Dot(Transform parent, string name, float x, float y, float size, Color colour)
        {
            RectTransform rt = Rect(parent, name, x, y, size, size);
            Image image = AddImage(rt.gameObject, CircleSprite, colour, 0f);
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// A rounded button with an optional line icon left of the label (icon only when text is empty).
        /// Primary: accent fill, white text. Secondary: panel fill with accent outline. Quiet: text only.
        /// </summary>
        public static Button Button(Transform parent, string name, string text, Sprite icon, float x, float y, float w, float h,
                                    Style style, float fontSize = 0f)
        {
            MenuTheme theme = Theme;
            if (fontSize <= 0f) fontSize = theme.BodyFontSize;

            RectTransform rt = Rect(parent, name, x, y, w, h);
            Color fill = style switch
            {
                Style.Primary => theme.Accent,
                Style.Secondary => new Color(1f, 1f, 1f, 0.92f),
                _ => new Color(1f, 1f, 1f, 0f),
            };
            Color ink = style == Style.Primary ? Color.white : theme.Accent;
            Image background = AddImage(rt.gameObject, RoundedSprite, fill, h * 0.3f);
            if (style == Style.Secondary)
                AddImage(Fill(rt, "Outline").gameObject, OutlineSprite, theme.PanelOutline, h * 0.3f).raycastTarget = false;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(0.90f, 0.93f, 0.98f, 1f),
                pressedColor = new Color(0.78f, 0.83f, 0.92f, 1f),
                selectedColor = Color.white, // no sticky highlight after a ray click
                disabledColor = new Color(1f, 1f, 1f, 0.45f),
                colorMultiplier = 1f,
                fadeDuration = 0.08f,
            };

            bool hasText = !string.IsNullOrEmpty(text);
            float iconSize = Mathf.Min(h * 0.55f, fontSize * 1.4f);
            if (icon != null)
            {
                float iconX = hasText ? h * 0.3f : (w - iconSize) * 0.5f;
                Icon(rt, "Icon", icon, iconX, -(h - iconSize) * 0.5f, iconSize, ink);
            }
            if (hasText)
            {
                float left = icon != null ? h * 0.3f + iconSize + 10f : 12f;
                Label(rt, "Label", text, left, 0f, w - left - 12f, h, fontSize, ink,
                    icon != null ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Center);
            }
            return button;
        }

        /// <summary>The button's label, if it has one.</summary>
        public static TMP_Text LabelOf(Button button) => button.transform.Find("Label")?.GetComponent<TMP_Text>();

        /// <summary>The button's icon image, if it has one.</summary>
        public static Image IconOf(Button button) => button.transform.Find("Icon")?.GetComponent<Image>();

        /// <summary>Recolours a button between Primary and Secondary look (e.g. a selected segment).</summary>
        public static void SetHighlighted(Button button, bool primary)
        {
            MenuTheme theme = Theme;
            ((Image)button.targetGraphic).color = primary ? theme.Accent : new Color(1f, 1f, 1f, 0.92f);
            Color ink = primary ? Color.white : theme.Accent;
            TMP_Text label = LabelOf(button);
            if (label != null) label.color = ink;
            Image icon = IconOf(button);
            if (icon != null) icon.color = ink;
        }

        // ------------------------------------------------------------------ sprites

        public static Sprite RoundedSprite => s_Rounded != null ? s_Rounded : s_Rounded = MakeSprite(Shape.Rounded);
        public static Sprite OutlineSprite => s_Outline != null ? s_Outline : s_Outline = MakeSprite(Shape.Outline);
        public static Sprite CircleSprite => s_Circle != null ? s_Circle : s_Circle = MakeSprite(Shape.Circle);

        static Image AddImage(GameObject go, Sprite sprite, Color colour, float radius)
        {
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            if (sprite != CircleSprite)
            {
                image.type = UnityEngine.UI.Image.Type.Sliced;
                // The sprite's corner is k_SpriteCorner px = k_SpriteCorner units; scale it to the wanted radius.
                image.pixelsPerUnitMultiplier = k_SpriteCorner / Mathf.Max(1f, radius);
            }
            return image;
        }

        enum Shape { Rounded, Outline, Circle }

        // White anti-aliased shapes; tinted by Image.color.
        static Sprite MakeSprite(Shape shape)
        {
            const int n = k_SpriteSize;
            const float stroke = 2.5f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "PanelKit_" + shape,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            float radius = shape == Shape.Circle ? n * 0.5f : k_SpriteCorner;
            var pixels = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // Signed distance to a rounded square filling the texture (negative inside).
                float px = Mathf.Abs(x + 0.5f - n * 0.5f) - (n * 0.5f - radius);
                float py = Mathf.Abs(y + 0.5f - n * 0.5f) - (n * 0.5f - radius);
                float outside = new Vector2(Mathf.Max(px, 0f), Mathf.Max(py, 0f)).magnitude + Mathf.Min(Mathf.Max(px, py), 0f) - radius;
                float alpha = Mathf.Clamp01(0.5f - outside);
                if (shape == Shape.Outline)
                    alpha *= Mathf.Clamp01(0.5f + outside + stroke); // keep only the band just inside the edge
                pixels[y * n + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            Vector4 border = shape == Shape.Circle ? Vector4.zero : Vector4.one * k_SpriteCorner;
            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, border);
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }
    }
}
