using System;
using DataViz.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Wizard
{
    /// <summary>
    /// Small uGUI factory for the wizard panel: rounded panels, text, buttons, check toggles and a
    /// scrolling list, coloured from MenuTheme so the wizard matches the CUBIA menus (translucent
    /// light panels, accent outlines, line icons). Rounded shapes use generated 9-slice sprites so
    /// nothing depends on editor-only built-in UI sprites.
    /// </summary>
    public static class WizardUI
    {
        public enum ButtonStyle { Primary, Secondary, Quiet }

        // Font sizes in canvas units, relative to the theme (the wizard canvas is viewed from ~1.4 m).
        public static float TitleSize => Theme.TitleFontSize * 1.5f;
        public static float BodySize => Theme.BodyFontSize * 1.4f;
        public static float SmallSize => Theme.BodyFontSize * 1.1f;

        public static MenuTheme Theme => MenuTheme.Load();
        public static WizardAssets Assets => WizardAssets.Load();

        /// <summary>Card / row background on top of the panel.</summary>
        public static Color CardColor => new Color(1f, 1f, 1f, 0.55f);
        public static Color AccentSoft => WithAlpha(Theme.Accent, 0.14f);

        const int k_SpriteSize = 64;
        const float k_SpriteRadius = 28f;
        static Sprite s_Rounded;
        static Sprite s_Outline;

        public static Sprite Rounded => s_Rounded ??= MakeRoundedSprite(0f);
        public static Sprite Outline => s_Outline ??= MakeRoundedSprite(4f);

        public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        public static LayoutElement Size(Component c, float preferredWidth = -1f, float preferredHeight = -1f,
                                         float flexibleWidth = -1f, float flexibleHeight = -1f)
        {
            var le = c.GetComponent<LayoutElement>();
            if (le == null)
                le = c.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = preferredWidth;
            le.preferredHeight = preferredHeight;
            le.minWidth = preferredWidth;
            le.minHeight = preferredHeight;
            le.flexibleWidth = flexibleWidth;
            le.flexibleHeight = flexibleHeight;
            return le;
        }

        /// <summary>Rounded rectangle. radius in canvas units.</summary>
        public static Image RoundedImage(Transform parent, string name, Color color, float radius = 16f, bool outline = false)
        {
            var img = Rect(name, parent).gameObject.AddComponent<Image>();
            img.sprite = outline ? Outline : Rounded;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = k_SpriteRadius / Mathf.Max(1f, radius);
            img.color = color;
            img.raycastTarget = !outline;
            return img;
        }

        /// <summary>Accent outline filling its parent, e.g. to mark a selected card.</summary>
        public static Image OutlineOverlay(Transform parent, Color color, float radius = 16f)
        {
            Image img = RoundedImage(parent, "Outline", color, radius, outline: true);
            Stretch(img.rectTransform);
            img.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            return img;
        }

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color,
                                           FontStyles style = FontStyles.Normal,
                                           TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var tmp = Rect("Text", parent).gameObject.AddComponent<TextMeshProUGUI>();
            if (Assets.Font != null)
                tmp.font = Assets.Font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.fontStyle = style;
            tmp.alignment = align;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            // Not every TMP font has the ellipsis glyph (Inter's SDF asset doesn't).
            tmp.overflowMode = tmp.font != null && tmp.font.HasCharacter('\u2026', false, true)
                ? TextOverflowModes.Ellipsis
                : TextOverflowModes.Truncate;
            tmp.raycastTarget = false;
            return tmp;
        }

        public static Image Icon(Transform parent, Sprite sprite, Color color, float size)
        {
            var img = Rect("Icon", parent).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.preserveAspect = true;
            img.raycastTarget = false;
            Size(img, size, size);
            img.gameObject.SetActive(sprite != null);
            return img;
        }

        public static HorizontalLayoutGroup Row(Transform parent, string name, float spacing, RectOffset padding = null,
                                                TextAnchor align = TextAnchor.MiddleLeft)
        {
            var row = Rect(name, parent).gameObject.AddComponent<HorizontalLayoutGroup>();
            Configure(row, spacing, padding, align);
            return row;
        }

        public static VerticalLayoutGroup Column(Transform parent, string name, float spacing, RectOffset padding = null,
                                                 TextAnchor align = TextAnchor.UpperLeft)
        {
            var col = Rect(name, parent).gameObject.AddComponent<VerticalLayoutGroup>();
            Configure(col, spacing, padding, align);
            return col;
        }

        public static void Configure(HorizontalOrVerticalLayoutGroup group, float spacing, RectOffset padding, TextAnchor align)
        {
            group.spacing = spacing;
            group.padding = padding ?? new RectOffset();
            group.childAlignment = align;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
        }

        /// <summary>Flexible empty space in a row.</summary>
        public static void Spacer(Transform parent) => Size(Rect("Spacer", parent), flexibleWidth: 1f);

        public static Button Button(Transform parent, string label, ButtonStyle style, Action onClick,
                                    Sprite icon = null, bool iconAfterLabel = false)
        {
            Color fill = style switch
            {
                ButtonStyle.Primary => Theme.Accent,
                ButtonStyle.Secondary => CardColor,
                _ => new Color(1f, 1f, 1f, 0f),
            };
            Color ink = style == ButtonStyle.Primary ? Color.white : Theme.Accent;

            Image bg = RoundedImage(parent, label, fill, 18f);
            if (style == ButtonStyle.Secondary)
                OutlineOverlay(bg.transform, Theme.PanelOutline, 18f);

            var layout = bg.gameObject.AddComponent<HorizontalLayoutGroup>();
            Configure(layout, 12f, new RectOffset(28, 28, 0, 0), TextAnchor.MiddleCenter);
            Size(bg, -1f, 76f);

            if (!iconAfterLabel)
                Icon(bg.transform, icon, ink, 34f);
            TextMeshProUGUI text = Text(bg.transform, label, BodySize, ink, FontStyles.Bold, TextAlignmentOptions.Center);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            if (iconAfterLabel)
                Icon(bg.transform, icon, ink, 34f);

            var button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.colors = PressColors();
            if (onClick != null)
                button.onClick.AddListener(() => onClick());
            return button;
        }

        /// <summary>Tint-transition colours shared by buttons, cards and rows.</summary>
        public static ColorBlock PressColors() => new ColorBlock
        {
            normalColor = Color.white,
            highlightedColor = new Color(0.9f, 0.94f, 1f, 1f),
            pressedColor = new Color(0.78f, 0.84f, 0.95f, 1f),
            selectedColor = Color.white,
            disabledColor = new Color(1f, 1f, 1f, 0.35f),
            colorMultiplier = 1f,
            fadeDuration = 0.08f,
        };

        /// <summary>
        /// Check box toggle (rounded box with an accent check) and an optional label, laid out in a
        /// row. The whole row is the hit area.
        /// </summary>
        public static Toggle CheckToggle(Transform parent, string label, bool isOn, Action<bool> onChanged)
        {
            Image hit = RoundedImage(parent, "Toggle " + label, new Color(1f, 1f, 1f, 0f), 14f);
            var layout = hit.gameObject.AddComponent<HorizontalLayoutGroup>();
            Configure(layout, 12f, new RectOffset(12, 12, 8, 8), TextAnchor.MiddleLeft);

            Image box = CheckBox(hit.transform, out Image check);
            if (!string.IsNullOrEmpty(label))
                Text(hit.transform, label, SmallSize, Theme.Text).textWrappingMode = TextWrappingModes.NoWrap;

            return MakeToggle(hit.gameObject, box, check, isOn, onChanged);
        }

        /// <summary>The rounded box of a check toggle; check is the graphic shown while on.</summary>
        public static Image CheckBox(Transform parent, out Image check)
        {
            Image box = RoundedImage(parent, "Box", Color.white, 10f);
            box.raycastTarget = false; // the toggle's row is the hit area
            Size(box, 44f, 44f);
            OutlineOverlay(box.transform, Theme.Accent, 10f);

            if (Assets.Check != null)
            {
                check = Icon(box.transform, Assets.Check, Theme.Accent, 34f);
            }
            else
            {
                check = RoundedImage(box.transform, "Fill", Theme.Accent, 6f);
                check.raycastTarget = false;
            }
            check.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Stretch(check.rectTransform, 6f);
            return box;
        }

        /// <summary>Adds a Toggle to a row whose background is its hit area.</summary>
        public static Toggle MakeToggle(GameObject row, Graphic target, Graphic check, bool isOn, Action<bool> onChanged)
        {
            var toggle = row.AddComponent<Toggle>();
            toggle.targetGraphic = target;
            toggle.graphic = check;
            toggle.colors = PressColors();
            toggle.SetIsOnWithoutNotify(isOn);
            check.canvasRenderer.SetAlpha(isOn ? 1f : 0f); // no fade-in on creation
            if (onChanged != null)
                toggle.onValueChanged.AddListener(v => onChanged(v));
            return toggle;
        }

        /// <summary>Pill with short text, e.g. a column type or "Setup saved".</summary>
        public static Image Chip(Transform parent, string text, Color fill, Color ink, float width = -1f)
        {
            Image chip = RoundedImage(parent, "Chip " + text, fill, 20f);
            chip.raycastTarget = false;
            var layout = chip.gameObject.AddComponent<HorizontalLayoutGroup>();
            Configure(layout, 0f, new RectOffset(16, 16, 4, 4), TextAnchor.MiddleCenter);
            Size(chip, width, 40f);
            TextMeshProUGUI label = Text(chip.transform, text, SmallSize * 0.9f, ink, FontStyles.Bold, TextAlignmentOptions.Center);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return chip;
        }

        /// <summary>
        /// Vertical scroll list filling its parent: viewport (RectMask2D), auto-sizing content and
        /// a scrollbar that works with the ray. Returns the content to add rows to.
        /// </summary>
        public static RectTransform ScrollList(Transform parent, out ScrollRect scroll)
        {
            RectTransform root = Rect("Scroll", parent);
            scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = true;

            // Transparent graphic so ray drags anywhere on the list scroll it.
            var dragArea = root.gameObject.AddComponent<Image>();
            dragArea.color = new Color(1f, 1f, 1f, 0f);

            RectTransform viewport = Rect("Viewport", root);
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-34f, 0f);
            viewport.pivot = new Vector2(0f, 1f);

            RectTransform content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var list = content.gameObject.AddComponent<VerticalLayoutGroup>();
            Configure(list, 12f, new RectOffset(0, 8, 0, 0), TextAnchor.UpperLeft);
            list.childForceExpandWidth = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Image track = RoundedImage(root, "Scrollbar", WithAlpha(Theme.TextMuted, 0.12f), 10f);
            RectTransform trackRt = track.rectTransform;
            trackRt.anchorMin = new Vector2(1f, 0f);
            trackRt.anchorMax = Vector2.one;
            trackRt.pivot = new Vector2(1f, 0.5f);
            trackRt.sizeDelta = new Vector2(20f, 0f);
            trackRt.anchoredPosition = Vector2.zero;

            RectTransform slide = Rect("Sliding Area", trackRt);
            Stretch(slide);
            Image handle = RoundedImage(slide, "Handle", WithAlpha(Theme.Accent, 0.6f), 10f);
            Stretch(handle.rectTransform);

            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handle.rectTransform;
            bar.targetGraphic = handle;
            bar.colors = PressColors();

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return content;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                GameObject child = t.GetChild(i).gameObject;
                child.SetActive(false); // leave layout now; Destroy only runs at the end of the frame
                UnityEngine.Object.Destroy(child);
            }
        }

        /// <summary>64 px 9-slice rounded rectangle (filled, or a ring of the given thickness).</summary>
        static Sprite MakeRoundedSprite(float ringThickness)
        {
            var tex = new Texture2D(k_SpriteSize, k_SpriteSize, TextureFormat.RGBA32, false)
            {
                name = ringThickness > 0f ? "WizardOutline" : "WizardRounded",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };

            float half = k_SpriteSize * 0.5f;
            var pixels = new Color32[k_SpriteSize * k_SpriteSize];
            for (int y = 0; y < k_SpriteSize; y++)
                for (int x = 0; x < k_SpriteSize; x++)
                {
                    // Signed distance to the rounded rectangle's edge (negative inside).
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - k_SpriteRadius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - k_SpriteRadius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - k_SpriteRadius;

                    float a = Mathf.Clamp01(0.5f - d);
                    if (ringThickness > 0f)
                        a *= Mathf.Clamp01(d + ringThickness + 0.5f);
                    pixels[y * k_SpriteSize + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            float border = k_SpriteRadius + 2f;
            Sprite sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, k_SpriteSize, k_SpriteSize), new Vector2(0.5f, 0.5f),
                                          100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }
    }
}
