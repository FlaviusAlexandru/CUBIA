using System.Collections.Generic;
using DataViz.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// Builds a <see cref="VRKeyboard"/> with <see cref="PanelKit"/> widgets inside a parent rect
    /// (at least 960 x 420 units). The keyboard object is created inactive and stays so until
    /// VRKeyboard.Open, because VRKeyboard hooks up its keys in Awake.
    /// </summary>
    public static class VRKeyboardFactory
    {
        public const float Width = 960f;
        public const float Height = 420f;

        public static VRKeyboard Build(Transform parent, string name)
        {
            MenuTheme theme = PanelKit.Theme;
            RectTransform root = PanelKit.Fill(parent, name);
            root.gameObject.SetActive(false);
            VRKeyboard kb = root.gameObject.AddComponent<VRKeyboard>();

            kb.m_PromptText = PanelKit.Label(root, "Prompt", "", 0f, 0f, Width, 34f, theme.BodyFontSize, theme.TextMuted);
            PanelKit.Panel(root, "EntryBackground", 0f, -40f, Width, 56f, new Color(1f, 1f, 1f, 0.9f), theme.PanelOutline, 14f)
                .raycastTarget = false;
            kb.m_EntryText = PanelKit.Label(root, "Entry", "", 18f, -40f, Width - 36f, 56f, 32f, theme.Text);

            const float step = 92f, keyW = 84f, keyH = 50f, top = -110f, rowStep = 58f;
            string[] rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            kb.m_Keys = new List<VRKeyboard.Key>();

            void AddKey(string label, string value, float col, int row, float width)
            {
                // Keys sit directly under the keyboard root: VRKeyboard hides "rows" by their wrapper parent otherwise.
                Button key = PanelKit.Button(root, "Key_" + label, label, null, col * step, top - row * rowStep, width, keyH,
                    PanelKit.Style.Secondary, 24f);
                kb.m_Keys.Add(new VRKeyboard.Key { Button = key, Label = PanelKit.LabelOf(key), Value = value });
            }

            for (int r = 0; r < 3; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    AddKey(rows[r][c].ToString(), rows[r][c].ToString(), c + (r == 2 ? 0.5f : 0f), r, keyW);
            AddKey("Del", VRKeyboard.k_Backspace, 9.5f, 2, keyW);

            kb.m_ShiftButton = PanelKit.Button(root, "Key_Shift", "ABC", null, 0f, top - 3 * rowStep, keyW + step * 0.5f, keyH,
                PanelKit.Style.Secondary, 20f);
            kb.m_ShiftLabel = PanelKit.LabelOf(kb.m_ShiftButton);
            for (int c = 0; c < rows[3].Length; c++)
                AddKey(rows[3][c].ToString(), rows[3][c].ToString(), c + 1.5f, 3, keyW);
            AddKey("Space", VRKeyboard.k_Space, 8.5f, 3, keyW + step);

            // Recent codes: each button gets its own wrapper so VRKeyboard can hide them one by one.
            float bottom = top - 4 * rowStep - 8f;
            RectTransform recent = PanelKit.Rect(root, "Recent", 0f, bottom, 640f, keyH);
            kb.m_SuggestionsRoot = recent.gameObject;
            PanelKit.Label(recent, "Caption", "Recent", 0f, 0f, 90f, keyH, theme.BodyFontSize * 0.85f, theme.TextMuted);
            kb.m_SuggestionButtons = new List<Button>();
            kb.m_SuggestionLabels = new List<TMP_Text>();
            for (int i = 0; i < 4; i++)
            {
                RectTransform slot = PanelKit.Rect(recent, "RecentSlot" + i, 90f + i * 136f, 0f, 128f, keyH);
                Button b = PanelKit.Button(slot, "RecentCode", "", null, 0f, 0f, 128f, keyH, PanelKit.Style.Quiet, 20f);
                // Quiet buttons have no label when created empty; add one for the code text.
                TMP_Text label = PanelKit.Label(b.transform, "Label", "", 0f, 0f, 128f, keyH, 20f, theme.Accent, TextAlignmentOptions.Center);
                kb.m_SuggestionButtons.Add(b);
                kb.m_SuggestionLabels.Add(label);
            }

            kb.m_CancelButton = PanelKit.Button(root, "Cancel", "Cancel", null, Width - 290f, bottom, 140f, keyH, PanelKit.Style.Secondary);
            kb.m_SubmitButton = PanelKit.Button(root, "Submit", "OK", null, Width - 140f, bottom, 140f, keyH, PanelKit.Style.Primary);
            kb.m_SubmitLabel = PanelKit.LabelOf(kb.m_SubmitButton);
            return kb;
        }
    }
}
