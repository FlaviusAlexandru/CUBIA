using DataViz.Flow;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DataViz.MenuUI.EditorTools
{
    /// <summary>
    /// Undo-aware hierarchy helpers shared by the menu builders. Everything is find-or-create and
    /// sets absolute values, so running a builder twice gives the same scene.
    /// </summary>
    internal static class MenuBuildUtil
    {
        public const string UndoName = "Build Tab Menu";

        /// <summary>The canvas with this name (inactive ones included) in the active scene, else in
        /// any open scene, or null.</summary>
        public static Transform FindCanvas(string name)
        {
            Transform fallback = null;
            foreach (Canvas canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                if (canvas.name != name || !canvas.gameObject.scene.IsValid())
                    continue;
                if (canvas.gameObject.scene == SceneManager.GetActiveScene())
                    return canvas.transform;
                fallback ??= canvas.transform;
            }
            return fallback;
        }

        /// <summary>Direct child of the first parent (in order) that has one with this name.</summary>
        public static Transform FindChildIn(string name, params Transform[] parents)
        {
            foreach (Transform parent in parents)
            {
                Transform child = parent != null ? parent.Find(name) : null;
                if (child != null)
                    return child;
            }
            return null;
        }

        public static RectTransform GetOrCreateRect(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
                return (RectTransform)existing;

            var go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static T GetOrAdd<T>(GameObject go) where T : Component
        {
            T component = go.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(go);
        }

        /// <summary>Instantiates the template under parent as name, unless that child already exists.</summary>
        public static RectTransform GetOrClone(Transform parent, string name, RectTransform template)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
                return (RectTransform)existing;

            RectTransform clone = Object.Instantiate(template, parent, false);
            clone.name = name;
            Undo.RegisterCreatedObjectUndo(clone.gameObject, UndoName);
            return clone;
        }

        /// <summary>Re-parents (keeping the same object, so references survive) at unit scale.</summary>
        public static void Reparent(Transform child, Transform parent)
        {
            if (child.parent != parent)
                Undo.SetTransformParent(child, parent, UndoName);
            Undo.RecordObject(child, UndoName);
            child.localScale = Vector3.one;
            child.localRotation = Quaternion.identity;
            Vector3 p = child.localPosition;
            child.localPosition = new Vector3(p.x, p.y, 0f);
        }

        /// <summary>Puts rt's top-left corner at (left, top) below its parent's top-left, any pivot.</summary>
        public static void PlaceTopLeft(RectTransform rt, float left, float top)
        {
            Undo.RecordObject(rt, UndoName);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            Vector2 size = rt.sizeDelta;
            rt.anchoredPosition = new Vector2(left + rt.pivot.x * size.x, -(top + (1f - rt.pivot.y) * size.y));
        }

        /// <summary>Point-anchored at the parent's centre with the given pivot, position and size.</summary>
        public static void SetCentred(RectTransform rt, Vector2 pivot, Vector2 position, Vector2 size)
        {
            Undo.RecordObject(rt, UndoName);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            rt.localScale = Vector3.one;
        }

        public static void Stretch(RectTransform rt)
        {
            Undo.RecordObject(rt, UndoName);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }

        public static void StylePanel(GameObject go, MenuTheme theme)
        {
            Image image = GetOrAdd<Image>(go);
            Undo.RecordObject(image, UndoName);
            theme.StylePanel(image);
        }

        /// <summary>Column heading at (left, top) inside a panel.</summary>
        public static void Heading(Transform panel, string name, string text, float left, float top, MenuTheme theme)
        {
            RectTransform rt = GetOrCreateRect(panel, name);
            var tmp = GetOrAdd<TextMeshProUGUI>(rt.gameObject);
            Undo.RecordObject(tmp, UndoName);
            theme.StyleText(tmp, muted: true);
            tmp.text = text;
            tmp.fontSize = theme.BodyFontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(360f, 40f);
            PlaceTopLeft(rt, left, top);
        }

        /// <summary>
        /// The rows were styled for dark panels: light text that sits directly on a panel (labels,
        /// headings, notes) is switched to the theme's dark text. Text inside a control (dropdown
        /// caption, button label, slider value) keeps its colour, and so does deliberately coloured
        /// text such as warnings, which is never light grey. Row labels drawn on a grey bookmark
        /// drop slot also stay light.
        /// </summary>
        public static void RecolourLightText(Transform root, MenuTheme theme)
        {
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.GetComponentInParent<Selectable>(true) != null || SitsOnDropSlot(text.transform))
                    continue;
                Color c = text.color;
                bool lightGrey = c.r > 0.6f && c.g > 0.6f && c.b > 0.6f &&
                                 Mathf.Abs(c.r - c.g) < 0.1f && Mathf.Abs(c.g - c.b) < 0.1f;
                if (!lightGrey)
                    continue;
                Undo.RecordObject(text, UndoName);
                text.color = new Color(theme.Text.r, theme.Text.g, theme.Text.b, c.a);
            }
        }

        private static bool SitsOnDropSlot(Transform text)
        {
            foreach (Transform sibling in text.parent)
                if (sibling.GetComponent<MappingDropSlot>() != null)
                    return true;
            return false;
        }

        public static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf == active) return;
            Undo.RecordObject(go, UndoName);
            go.SetActive(active);
        }
    }
}
