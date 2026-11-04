using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// World-space palette with one LabelBlock per bookmarked label of the loaded dataset, in
    /// bookmark order. Only bookmarked labels are shown. Rebuilt whenever the manager raises
    /// OnBookmarksChanged (bookmark added/removed, dataset loaded), and on enable - it lives on
    /// the SummonedMenuCanvas, so it is summoned and dismissed together with the menu.
    ///
    /// Blocks are laid out in a grid under m_BlockRoot. m_BlockRoot's scale is compensated so
    /// that it has world scale 1 even under the tiny canvas scale; cell sizes are in meters.
    /// When a block is grabbed it leaves the palette and the palette spawns a replacement
    /// in the same cell (see LabelBlock), so the palette always keeps its block.
    /// </summary>
    public class BookmarkPalette : MonoBehaviour
    {
        public MultiplayerScatterplotManager m_Manager;

        [Tooltip("LabelBlock prefab spawned per bookmarked label.")]
        public LabelBlock m_BlockPrefab;

        [Tooltip("Parent of the spawned blocks. Its scale is reset so it has world scale 1.")]
        public Transform m_BlockRoot;

        [Tooltip("Shown when no labels are bookmarked. Optional.")]
        public TMP_Text m_EmptyText;

        [Header("Layout (meters, in m_BlockRoot space)")]
        public int m_Columns = 6;
        public Vector2 m_CellSize = new Vector2(0.105f, 0.04f);
        [Tooltip("Offset of the first (top-left) cell centre from m_BlockRoot.")]
        public Vector3 m_FirstCellOffset = new Vector3(-0.2625f, 0.035f, -0.01f);

        private readonly List<LabelBlock> m_Blocks = new();

        /// <summary>Blocks currently sitting on the palette, in bookmark order (for tests/inspection).</summary>
        public IReadOnlyList<LabelBlock> Blocks => m_Blocks;

        private bool m_Subscribed;

        private void OnEnable()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance;

            if (m_Manager != null && !m_Subscribed)
            {
                m_Manager.OnBookmarksChanged += Rebuild;
                m_Subscribed = true;
            }

            Rebuild();
        }

        private void OnDisable()
        {
            if (m_Manager != null && m_Subscribed)
            {
                m_Manager.OnBookmarksChanged -= Rebuild;
                m_Subscribed = false;
            }
        }

        /// <summary>Destroys the palette's own blocks (never held ones) and spawns one per bookmark.</summary>
        public void Rebuild()
        {
            foreach (LabelBlock block in m_Blocks)
            {
                if (block != null && block.IsInPalette)
                    DestroyBlock(block);
            }
            m_Blocks.Clear();

            if (m_BlockRoot == null)
                m_BlockRoot = transform;

            NormalizeBlockRootScale();
            CentreBlockRoot();

            List<string> bookmarks = (m_Manager != null) ? m_Manager.GetBookmarkedColumns() : new List<string>();

            if (m_EmptyText != null)
                m_EmptyText.gameObject.SetActive(bookmarks.Count == 0);

            if (m_BlockPrefab == null)
                return;

            for (int i = 0; i < bookmarks.Count; i++)
                m_Blocks.Add(CreateBlock(bookmarks[i], i));
        }

        /// <summary>Called by a LabelBlock the moment it is grabbed off the palette.</summary>
        public void OnBlockTakenFromPalette(LabelBlock taken)
        {
            int index = m_Blocks.IndexOf(taken);
            if (index < 0)
                return;

            m_Blocks[index] = CreateBlock(taken.ColumnName, index);
        }

        private LabelBlock CreateBlock(string columnName, int index)
        {
            LabelBlock block = Instantiate(m_BlockPrefab, m_BlockRoot);
            Transform t = block.transform;
            t.localPosition = CellPosition(index);
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            DatasetColumn column = m_Manager != null && m_Manager.LoadedDataset != null
                ? m_Manager.LoadedDataset.GetColumn(columnName)
                : null;

            block.Setup(columnName, column);
            block.Owner = this;
            block.PaletteIndex = index;
            return block;
        }

        private Vector3 CellPosition(int index)
        {
            int columns = Mathf.Max(1, m_Columns);
            int col = index % columns;
            int row = index / columns;
            return m_FirstCellOffset + new Vector3(col * m_CellSize.x, -row * m_CellSize.y, 0f);
        }

        /// <summary>
        /// Puts the block grid's origin at the panel's centre, whatever the panel's pivot. The tab
        /// menu gives the palette strip a top pivot, which used to push the first row above the strip.
        /// </summary>
        private void CentreBlockRoot()
        {
            if (m_BlockRoot == transform || !(transform is RectTransform rect))
                return;

            Vector2 centre = rect.rect.center;
            m_BlockRoot.localPosition = new Vector3(centre.x, centre.y, m_BlockRoot.localPosition.z);
        }

        private void NormalizeBlockRootScale()
        {
            Transform parent = m_BlockRoot.parent;
            if (parent == null)
            {
                m_BlockRoot.localScale = Vector3.one;
                return;
            }

            Vector3 p = parent.lossyScale;
            m_BlockRoot.localScale = new Vector3(
                Mathf.Approximately(p.x, 0f) ? 1f : 1f / p.x,
                Mathf.Approximately(p.y, 0f) ? 1f : 1f / p.y,
                Mathf.Approximately(p.z, 0f) ? 1f : 1f / p.z);
        }

        private static void DestroyBlock(LabelBlock block)
        {
            // Deactivate first so it unregisters from the interaction manager this frame.
            block.gameObject.SetActive(false);
            Destroy(block.gameObject);
        }
    }
}
