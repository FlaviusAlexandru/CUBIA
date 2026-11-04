using DataViz.Flow;
using DataViz.Networking;
using UnityEditor;
using UnityEngine;
using static DataViz.MenuUI.EditorTools.MenuBuildUtil;

namespace DataViz.MenuUI.EditorTools
{
    /// <summary>
    /// Part of Build Tab Menu: the wrist panel keeps only quick actions (&lt;&lt; Shuffle &gt;&gt; and
    /// Reset position) on one themed QuickActionsPanel; every other wrist copy is reachable from the
    /// summoned menu, so the old wrist islands are deleted. The quick-action rows are the existing
    /// wrist objects, so their MirroredUIControls and WristShortcutMenu entries keep working;
    /// shortcut entries and gate exclusions that pointed at deleted objects are dropped.
    /// Idempotent like the rest of the builder.
    /// </summary>
    internal static class WristQuickActionsBuilder
    {
        private const string k_CanvasName = "WristUIHolderCanvas";
        public const string PanelName = "QuickActionsPanel";

        private static readonly string[] k_OldIslands =
        {
            "AxisControlPanelBackground", "DatasetControlPanelBackground",
            "PointControlPanelBackground", "TimeControlsPanelBackground"
        };

        // Panel units; the panel has the old islands' 0.6 scale so rows keep their wrist size.
        private const float k_PanelScale = 0.6f;
        private static readonly Vector2 k_PanelPosition = new(-260f, 205f);
        private static readonly Vector2 k_PanelSize = new(452f, 130f);

        public static void Build(MenuTheme theme)
        {
            Transform canvas = FindCanvas(k_CanvasName);
            if (canvas == null)
            {
                Debug.LogWarning($"[TabMenuSetup] No '{k_CanvasName}'; wrist quick actions skipped.");
                return;
            }

            RectTransform panel = GetOrCreateRect(canvas, PanelName);
            SetCentred(panel, new Vector2(0.5f, 0.5f), k_PanelPosition, k_PanelSize);
            panel.localScale = Vector3.one * k_PanelScale;
            StylePanel(panel.gameObject, theme);

            // Same << Shuffle >> offsets as the Map panel; Reset position's button starts at 280.
            const float shuffleLeft = -16f, top = 15f;
            Move(canvas, panel, "ShuffleBackwardButton", shuffleLeft + 17f, top);
            Move(canvas, panel, "ShuffleButton", shuffleLeft, top);
            Move(canvas, panel, "ShuffleForwardButton", shuffleLeft + 208f, top);
            Move(canvas, panel, "ResetPositionButton", 205f, top);

            foreach (string name in k_OldIslands)
            {
                Transform island = canvas.Find(name);
                if (island != null)
                    Undo.DestroyObjectImmediate(island.gameObject);
            }

            var shortcuts = canvas.GetComponent<WristShortcutMenu>();
            if (shortcuts != null)
            {
                Undo.RecordObject(shortcuts, UndoName);
                shortcuts.m_ShortcutItems.RemoveAll(item => item.Target == null);
            }

            var gate = canvas.GetComponent<PresenterModeUIGate>();
            if (gate != null)
            {
                Undo.RecordObject(gate, UndoName);
                gate.m_Excluded.RemoveAll(go => go == null);
            }
        }

        /// <summary>Moves a wrist row out of whichever old island holds it (or finds it already moved).</summary>
        private static void Move(Transform canvas, RectTransform panel, string name, float left, float top)
        {
            Transform row = panel.Find(name);
            for (int i = 0; row == null && i < k_OldIslands.Length; i++)
                row = canvas.Find($"{k_OldIslands[i]}/{name}");

            if (row == null)
            {
                Debug.LogWarning($"[TabMenuSetup] Wrist row '{name}' not found; skipped.");
                return;
            }
            Reparent(row, panel);
            PlaceTopLeft((RectTransform)row, left, top);
        }
    }
}
