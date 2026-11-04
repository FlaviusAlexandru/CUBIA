using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// A small world-space keyboard (A-Z, 0-9, backspace, space, shift, OK/Cancel) for the Session
    /// island, poked or ray-clicked through the normal XR UI input like every other menu button.
    /// One instance is reused for the player name (Layout.Text) and the 6-character join code
    /// (Layout.JoinCode: uppercase letters and digits only, exactly CodeLength characters, with up
    /// to m_SuggestionButtons.Count recent codes offered as one-click buttons).
    ///
    /// The keys are ordinary uGUI Buttons duplicated from the summoned menu's own buttons by
    /// Tools > CUBIA > Multiplayer > 4 Setup Session Island; each Key entry just says which character
    /// it types. While open, a desktop keyboard also types into it (handy with the XR Device Simulator).
    /// </summary>
    public class VRKeyboard : MonoBehaviour
    {
        public enum Layout { Text, JoinCode }

        [Serializable]
        public class Key
        {
            public Button Button;
            public TMP_Text Label;
            [Tooltip("What the key types: one character, \"\\b\" for backspace or \" \" for space.")]
            public string Value;
        }

        public const string k_Backspace = "\b";
        public const string k_Space = " ";
        public const int CodeLength = 6;

        [Header("Display")]
        public TMP_Text m_PromptText;
        public TMP_Text m_EntryText;

        [Header("Keys")]
        public List<Key> m_Keys = new();
        public Button m_ShiftButton;
        public TMP_Text m_ShiftLabel;
        public Button m_SubmitButton;
        public TMP_Text m_SubmitLabel;
        public Button m_CancelButton;

        [Header("Recent codes (JoinCode layout only)")]
        public GameObject m_SuggestionsRoot;
        public List<Button> m_SuggestionButtons = new();
        public List<TMP_Text> m_SuggestionLabels = new();

        [Header("Desktop")]
        [Tooltip("While open, characters typed on a physical keyboard are entered too.")]
        public bool m_AcceptPhysicalKeyboard = true;

        public bool IsOpen => m_OnSubmit != null && gameObject.activeSelf;
        public string Text => m_Text.ToString();
        public Layout CurrentLayout => m_Layout;

        /// <summary>Raised after the keyboard closes (submitted or cancelled).</summary>
        public event Action OnClosed;

        private readonly StringBuilder m_Text = new();
        private readonly List<string> m_Suggestions = new();
        private Layout m_Layout;
        private int m_MaxLength = 24;
        private bool m_Shift;
        private bool m_Initialised;
        private Action<string> m_OnSubmit;
        private Action m_OnCancel;
        private Keyboard m_HookedKeyboard;

        private void Awake() => EnsureInitialised();

        private void EnsureInitialised()
        {
            if (m_Initialised)
                return;
            m_Initialised = true;

            foreach (Key key in m_Keys)
            {
                if (key?.Button == null)
                    continue;
                Key captured = key;
                key.Button.onClick.AddListener(() => OnKeyPressed(captured));
            }

            if (m_ShiftButton != null)
                m_ShiftButton.onClick.AddListener(() => { m_Shift = !m_Shift; RefreshVisuals(); });
            if (m_SubmitButton != null)
                m_SubmitButton.onClick.AddListener(Submit);
            if (m_CancelButton != null)
                m_CancelButton.onClick.AddListener(Cancel);

            for (int i = 0; i < m_SuggestionButtons.Count; i++)
            {
                if (m_SuggestionButtons[i] == null)
                    continue;
                int index = i;
                m_SuggestionButtons[i].onClick.AddListener(() => OnSuggestionPressed(index));
            }
        }

        /// <summary>
        /// Shows the keyboard. onSubmit receives the cleaned text (trimmed name, or a valid code);
        /// onCancel runs when the player presses Cancel.
        /// </summary>
        public void Open(string prompt, string initialText, Layout layout, int maxLength, string submitLabel,
                         Action<string> onSubmit, Action onCancel = null, IReadOnlyList<string> suggestions = null)
        {
            EnsureInitialised();

            m_Layout = layout;
            m_MaxLength = layout == Layout.JoinCode ? CodeLength : Mathf.Max(1, maxLength);
            m_OnSubmit = onSubmit;
            m_OnCancel = onCancel;

            m_Text.Clear();
            foreach (char c in initialText ?? "")
                Append(c);

            m_Suggestions.Clear();
            if (layout == Layout.JoinCode && suggestions != null)
            {
                foreach (string s in suggestions)
                {
                    string clean = CleanCode(s);
                    if (clean.Length == CodeLength && !m_Suggestions.Contains(clean))
                        m_Suggestions.Add(clean);
                }
            }

            m_Shift = layout == Layout.Text && m_Text.Length == 0;

            if (m_PromptText != null)
                m_PromptText.text = prompt ?? "";
            if (m_SubmitLabel != null)
                m_SubmitLabel.text = string.IsNullOrEmpty(submitLabel) ? "OK" : submitLabel;

            gameObject.SetActive(true);
            RefreshVisuals();
        }

        /// <summary>Closes without submitting and runs the cancel callback.</summary>
        public void Cancel()
        {
            if (m_OnSubmit == null && !gameObject.activeSelf)
                return;

            Action cancel = m_OnCancel;
            CloseInternal();
            cancel?.Invoke();
        }

        /// <summary>Closes without running any callback (e.g. a session started meanwhile).</summary>
        public void CloseSilently()
        {
            if (m_OnSubmit == null && !gameObject.activeSelf)
                return;
            CloseInternal();
        }

        private void CloseInternal()
        {
            m_OnSubmit = null;
            m_OnCancel = null;
            gameObject.SetActive(false);
            OnClosed?.Invoke();
        }

        public void Submit()
        {
            if (!CanSubmit())
                return;

            string value = m_Layout == Layout.JoinCode ? CleanCode(m_Text.ToString()) : m_Text.ToString().Trim();
            Action<string> submit = m_OnSubmit;
            CloseInternal();
            submit?.Invoke(value);
        }

        private bool CanSubmit()
        {
            if (m_OnSubmit == null)
                return false;
            return m_Layout == Layout.JoinCode
                ? m_Text.Length == CodeLength
                : m_Text.ToString().Trim().Length > 0;
        }

        private void OnKeyPressed(Key key)
        {
            if (string.IsNullOrEmpty(key.Value))
                return;

            if (key.Value == k_Backspace)
            {
                Backspace();
            }
            else
            {
                char c = key.Value[0];
                if (char.IsLetter(c))
                    c = m_Shift || m_Layout == Layout.JoinCode ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
                bool added = Append(c);
                if (added && m_Layout == Layout.Text && char.IsLetter(c))
                    m_Shift = false; // one-shot shift, like a phone keyboard
                if (added && m_Layout == Layout.Text && c == ' ')
                    m_Shift = true;  // capitalise the next word
            }

            RefreshVisuals();
        }

        private void OnSuggestionPressed(int index)
        {
            if (index < 0 || index >= m_Suggestions.Count)
                return;

            m_Text.Clear();
            m_Text.Append(m_Suggestions[index]);
            RefreshVisuals();
        }

        private void Backspace()
        {
            if (m_Text.Length > 0)
                m_Text.Length--;
            if (m_Layout == Layout.Text && m_Text.Length == 0)
                m_Shift = true;
        }

        /// <summary>Appends c if the current layout allows it. Returns true when added.</summary>
        private bool Append(char c)
        {
            if (m_Text.Length >= m_MaxLength)
                return false;

            if (m_Layout == Layout.JoinCode)
            {
                c = char.ToUpperInvariant(c);
                if (!IsCodeChar(c))
                    return false;
            }
            else
            {
                if (char.IsControl(c))
                    return false;
                if (c == ' ' && (m_Text.Length == 0 || m_Text[m_Text.Length - 1] == ' '))
                    return false; // no leading or double spaces
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' || c == '\''))
                    return false;
            }

            m_Text.Append(c);
            return true;
        }

        private static bool IsCodeChar(char c) => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        /// <summary>Uppercases and strips everything but A-Z and 0-9.</summary>
        public static string CleanCode(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                char c = char.ToUpperInvariant(ch);
                if (IsCodeChar(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private void RefreshVisuals()
        {
            bool code = m_Layout == Layout.JoinCode;

            if (m_EntryText != null)
            {
                if (code)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < CodeLength; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(i < m_Text.Length ? m_Text[i] : '_');
                    }
                    m_EntryText.text = sb.ToString();
                }
                else
                {
                    m_EntryText.text = m_Text.Length < m_MaxLength
                        ? m_Text + "<color=#00000060>_</color>"
                        : m_Text.ToString();
                }
            }

            foreach (Key key in m_Keys)
            {
                if (key?.Button == null || string.IsNullOrEmpty(key.Value))
                    continue;

                if (key.Value == k_Space)
                    RowOf(key.Button).SetActive(!code);

                char c = key.Value[0];
                if (key.Label != null && key.Value.Length == 1 && char.IsLetter(c))
                    key.Label.text = (code || m_Shift ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToString();
            }

            if (m_ShiftButton != null)
                RowOf(m_ShiftButton).SetActive(!code);
            if (m_ShiftLabel != null)
                m_ShiftLabel.text = m_Shift ? "ABC" : "abc";

            if (m_SubmitButton != null)
                m_SubmitButton.interactable = CanSubmit();

            if (m_SuggestionsRoot != null)
                m_SuggestionsRoot.SetActive(code && m_Suggestions.Count > 0);

            for (int i = 0; i < m_SuggestionButtons.Count; i++)
            {
                bool show = code && i < m_Suggestions.Count;
                if (m_SuggestionButtons[i] != null)
                    RowOf(m_SuggestionButtons[i]).SetActive(show);
                if (show && i < m_SuggestionLabels.Count && m_SuggestionLabels[i] != null)
                    m_SuggestionLabels[i].text = m_Suggestions[i];
            }
        }

        // The duplicated menu buttons are a wrapper object holding the actual Button.
        private GameObject RowOf(Button button) =>
            button.transform.parent != null && button.transform.parent != transform
                ? button.transform.parent.gameObject
                : button.gameObject;

        // ---------------- desktop keyboard ----------------

        private void OnEnable()
        {
            if (!m_AcceptPhysicalKeyboard)
                return;
            m_HookedKeyboard = Keyboard.current;
            if (m_HookedKeyboard != null)
                m_HookedKeyboard.onTextInput += OnPhysicalTextInput;
        }

        private void OnDisable()
        {
            if (m_HookedKeyboard != null)
                m_HookedKeyboard.onTextInput -= OnPhysicalTextInput;
            m_HookedKeyboard = null;
        }

        private void OnPhysicalTextInput(char c)
        {
            if (!IsOpen)
                return;

            switch (c)
            {
                case '\b':
                    Backspace();
                    break;
                case '\r':
                case '\n':
                    Submit();
                    return;
                case '\u001b':
                    Cancel();
                    return;
                default:
                    Append(c);
                    break;
            }

            RefreshVisuals();
        }
    }
}
