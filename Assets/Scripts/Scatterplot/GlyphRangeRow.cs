using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz
{
    /// <summary>
    /// One row of the glyph range editor (see GlyphRangeEditor): a shape picker, a two-handle
    /// range bar over the glyph column's min..max with the current low/high value shown next to
    /// each handle, and a remove button.
    ///
    /// The range bar is two Sliders on one track. Only their handles take pointer input (the
    /// track and fill are not raycast targets), so each handle drags its own slider. The low
    /// handle can never pass the high one. The row only shows values and reports changes; the
    /// editor owns the draft, the overlap rules and Prevent overlap clamping.
    /// </summary>
    public class GlyphRangeRow : MonoBehaviour
    {
        [Header("Controls")]
        public TMP_Dropdown m_ShapeDropdown;
        public RectTransform m_Bar;          // the track's rect; handles and value labels follow it
        public Image m_Track;
        public Image m_RangeFill;            // fills the track between the two handles
        public Slider m_LowSlider;
        public Slider m_HighSlider;
        public TextMeshProUGUI m_LowValueText;   // sits just left of the low handle, above the bar
        public TextMeshProUGUI m_HighValueText;  // sits just right of the high handle, above the bar
        public Button m_RemoveButton;

        [Tooltip("Horizontal gap between a handle's centre and its value label.")]
        public float m_ValueTextGap = 4f;

        /// <summary>Raised with the glyph index the user picked in the shape dropdown.</summary>
        public event Action<GlyphRangeRow, int> ShapeChanged;
        /// <summary>Raised with the low handle's new value (already capped at the high value).</summary>
        public event Action<GlyphRangeRow, float> LowChanged;
        /// <summary>Raised with the high handle's new value (already capped at the low value).</summary>
        public event Action<GlyphRangeRow, float> HighChanged;
        public event Action<GlyphRangeRow> RemoveClicked;

        public float Low => m_LowSlider != null ? m_LowSlider.value : 0f;
        public float High => m_HighSlider != null ? m_HighSlider.value : 0f;

        // Glyph index behind each shape dropdown option.
        private readonly List<int> m_OptionGlyphs = new();
        private bool m_Wired;
        private bool m_Suppress;

        private void Awake()
        {
            Wire();
        }

        private void Wire()
        {
            if (m_Wired)
                return;
            m_Wired = true;

            if (m_ShapeDropdown != null)
                m_ShapeDropdown.onValueChanged.AddListener(OnShapeDropdownChanged);
            if (m_LowSlider != null)
                m_LowSlider.onValueChanged.AddListener(OnLowSliderChanged);
            if (m_HighSlider != null)
                m_HighSlider.onValueChanged.AddListener(OnHighSliderChanged);
            if (m_RemoveButton != null)
                m_RemoveButton.onClick.AddListener(() => RemoveClicked?.Invoke(this));
        }

        /// <summary>Sets the bar's value span (the glyph column's min..max) without raising events.</summary>
        public void SetBounds(float min, float max)
        {
            Wire();
            m_Suppress = true;
            foreach (Slider slider in new[] { m_LowSlider, m_HighSlider })
            {
                if (slider == null)
                    continue;
                slider.wholeNumbers = false;
                // Widen before narrowing so the setters never see min > max.
                slider.minValue = Mathf.Min(slider.minValue, min);
                slider.maxValue = max;
                slider.minValue = min;
            }
            m_Suppress = false;
            UpdateVisuals();
        }

        /// <summary>Moves both handles without raising events.</summary>
        public void SetValues(float low, float high)
        {
            Wire();
            if (m_LowSlider != null)
                m_LowSlider.SetValueWithoutNotify(low);
            if (m_HighSlider != null)
                m_HighSlider.SetValueWithoutNotify(Mathf.Max(high, Low));
            UpdateVisuals();
        }

        /// <summary>
        /// Offers exactly the given glyphs in the shape dropdown (names from
        /// ScatterplotUI.k_GlyphShapeNames) with selectedGlyph selected, without raising events.
        /// </summary>
        public void SetShapeOptions(List<int> glyphs, int selectedGlyph)
        {
            Wire();
            if (m_ShapeDropdown == null)
                return;

            bool sameOptions = glyphs.Count == m_OptionGlyphs.Count;
            for (int i = 0; sameOptions && i < glyphs.Count; i++)
                sameOptions = glyphs[i] == m_OptionGlyphs[i];

            if (!sameOptions)
            {
                m_OptionGlyphs.Clear();
                m_OptionGlyphs.AddRange(glyphs);

                List<string> names = new List<string>(glyphs.Count);
                foreach (int glyph in glyphs)
                    names.Add(ScatterplotUI.k_GlyphShapeNames[glyph]);

                m_ShapeDropdown.ClearOptions();
                m_ShapeDropdown.AddOptions(names);
            }

            int index = m_OptionGlyphs.IndexOf(selectedGlyph);
            m_ShapeDropdown.SetValueWithoutNotify(Mathf.Max(0, index));
            m_ShapeDropdown.RefreshShownValue();
        }

        /// <summary>Glyph indices currently offered by the shape dropdown, in option order.</summary>
        public IReadOnlyList<int> ShapeOptionGlyphs => m_OptionGlyphs;

        public void SetColors(Color trackColor, Color fillColor)
        {
            if (m_Track != null)
                m_Track.color = trackColor;
            if (m_RangeFill != null)
                m_RangeFill.color = fillColor;
        }

        public void SetValueTexts(string low, string high)
        {
            if (m_LowValueText != null)
                m_LowValueText.text = low;
            if (m_HighValueText != null)
                m_HighValueText.text = high;
            UpdateVisuals();
        }

        private void OnShapeDropdownChanged(int optionIndex)
        {
            if (optionIndex < 0 || optionIndex >= m_OptionGlyphs.Count)
                return;
            ShapeChanged?.Invoke(this, m_OptionGlyphs[optionIndex]);
        }

        private void OnLowSliderChanged(float value)
        {
            if (m_Suppress)
                return;

            // The low handle stops at the high handle.
            if (value > High)
            {
                value = High;
                m_LowSlider.SetValueWithoutNotify(value);
            }

            UpdateVisuals();
            LowChanged?.Invoke(this, value);
        }

        private void OnHighSliderChanged(float value)
        {
            if (m_Suppress)
                return;

            // The high handle stops at the low handle.
            if (value < Low)
            {
                value = Low;
                m_HighSlider.SetValueWithoutNotify(value);
            }

            UpdateVisuals();
            HighChanged?.Invoke(this, value);
        }

        private float Normalized(float value)
        {
            if (m_LowSlider == null)
                return 0f;
            float span = m_LowSlider.maxValue - m_LowSlider.minValue;
            return span > 0f ? Mathf.Clamp01((value - m_LowSlider.minValue) / span) : 0f;
        }

        /// <summary>Places the fill between the handles and the value labels beside them.</summary>
        public void UpdateVisuals()
        {
            if (m_LowSlider == null || m_HighSlider == null)
                return;

            float lowNorm = Normalized(Low);
            float highNorm = Normalized(High);

            if (m_RangeFill != null)
            {
                RectTransform fill = m_RangeFill.rectTransform;
                fill.anchorMin = new Vector2(lowNorm, 0f);
                fill.anchorMax = new Vector2(highNorm, 1f);
                fill.offsetMin = Vector2.zero;
                fill.offsetMax = Vector2.zero;
            }

            // When the handles sit on top of each other only the upper slider can be grabbed:
            // put the one that can still move apart on top (low near the right end, high otherwise).
            Transform upper = lowNorm > 0.5f ? m_LowSlider.transform : m_HighSlider.transform;
            Transform lower = upper == m_LowSlider.transform ? m_HighSlider.transform : m_LowSlider.transform;
            if (upper.parent == lower.parent && upper.GetSiblingIndex() < lower.GetSiblingIndex())
                upper.SetSiblingIndex(lower.GetSiblingIndex());

            if (m_Bar == null)
                return;

            float width = m_Bar.rect.width;
            PlaceValueText(m_LowValueText, lowNorm * width - m_ValueTextGap, true, width);
            PlaceValueText(m_HighValueText, highNorm * width + m_ValueTextGap, false, width);
        }

        // Low label ends at x (right-aligned), high label starts at x; both are kept inside the bar.
        private static void PlaceValueText(TextMeshProUGUI text, float x, bool endsAtX, float barWidth)
        {
            if (text == null)
                return;

            RectTransform rect = text.rectTransform;
            float textWidth = Mathf.Min(text.GetPreferredValues(text.text).x + 2f, barWidth);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0f);
            rect.sizeDelta = new Vector2(textWidth, rect.sizeDelta.y);

            float left = endsAtX ? x - textWidth : x;
            left = Mathf.Clamp(left, 0f, Mathf.Max(0f, barWidth - textWidth));
            rect.anchoredPosition = new Vector2(left, 2f);
        }
    }
}
