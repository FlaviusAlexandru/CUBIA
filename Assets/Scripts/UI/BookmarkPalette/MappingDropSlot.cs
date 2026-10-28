using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz
{
    /// <summary>
    /// A drop target for LabelBlocks, sitting next to a mapping dropdown on the summoned menu.
    /// Dropping a block whose column the Target accepts (see ColumnMappingRules) assigns that
    /// column through MultiplayerScatterplotManager - the dropdowns then re-sync on their own
    /// via OnPlotSettingsChanged.
    ///
    /// Proximity-based rather than an XRSocketInteractor: a socket would try to keep the block,
    /// but here the block is consumed on drop, and the slot lives on a 0.00047-scaled UI canvas.
    /// A block counts as "over" the slot when its position, in the slot's RectTransform space,
    /// lies inside the rect (plus m_RectPadding) and within m_DropDepthMeters of the canvas plane.
    ///
    /// While any block is held, valid slots show m_ValidColor (m_HoverColor when the block is
    /// over them) and invalid ones m_InvalidColor; otherwise m_IdleColor.
    /// For the Filter target, m_ChipsText lists the active filters and m_ClearButton clears them.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MappingDropSlot : MonoBehaviour
    {
        public MultiplayerScatterplotManager m_Manager;
        public MappingTarget m_Target;

        [Header("Visuals")]
        public Graphic m_Background;
        public TMP_Text m_CaptionText;
        public Color m_IdleColor = new Color(0.35f, 0.35f, 0.35f, 0.6f);
        public Color m_ValidColor = new Color(0.20f, 0.75f, 0.35f, 0.85f);
        public Color m_HoverColor = new Color(0.45f, 1.00f, 0.55f, 1.00f);
        public Color m_InvalidColor = new Color(0.25f, 0.25f, 0.25f, 0.35f);

        [Header("Hit test")]
        [Tooltip("Extra margin around the rect, in the slot's own (canvas) units.")]
        public float m_RectPadding = 20f;
        [Tooltip("How far in front of / behind the canvas plane a block may be and still count, in meters.")]
        public float m_DropDepthMeters = 0.15f;

        [Header("Filter target only")]
        [Tooltip("Shows the active filters as chips (text). Optional.")]
        public TMP_Text m_ChipsText;
        [Tooltip("\"Clear all filters\" button. Optional.")]
        public Button m_ClearButton;

        private static readonly List<MappingDropSlot> s_ActiveSlots = new();
        public static IReadOnlyList<MappingDropSlot> ActiveSlots => s_ActiveSlots;

        private RectTransform m_Rect;

        /// <summary>Last highlight state, for inspection/tests: 0 idle, 1 valid, 2 hover, 3 invalid.</summary>
        public int HighlightState { get; private set; }

        private void Awake()
        {
            m_Rect = (RectTransform)transform;
            if (m_ClearButton != null)
                m_ClearButton.onClick.AddListener(OnClearClicked);
        }

        private void OnEnable()
        {
            if (!s_ActiveSlots.Contains(this))
                s_ActiveSlots.Add(this);

            ResolveManager();
            if (m_Manager != null)
                m_Manager.OnPlotSettingsChanged += RefreshChips;

            RefreshChips();
            UpdateHighlight();
        }

        private void OnDisable()
        {
            s_ActiveSlots.Remove(this);
            if (m_Manager != null)
                m_Manager.OnPlotSettingsChanged -= RefreshChips;
        }

        private void OnDestroy()
        {
            if (m_ClearButton != null)
                m_ClearButton.onClick.RemoveListener(OnClearClicked);
        }

        private void ResolveManager()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance;
        }

        private void Update()
        {
            UpdateHighlight();
        }

        /// <summary>The nearest active slot whose drop volume contains worldPosition, or null.</summary>
        public static MappingDropSlot FindSlotAt(Vector3 worldPosition)
        {
            MappingDropSlot best = null;
            float bestDistance = float.MaxValue;

            foreach (MappingDropSlot slot in s_ActiveSlots)
            {
                if (slot == null || !slot.isActiveAndEnabled || !slot.Contains(worldPosition))
                    continue;

                float distance = Vector3.Distance(slot.transform.position, worldPosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = slot;
                }
            }

            return best;
        }

        public bool Contains(Vector3 worldPosition)
        {
            if (m_Rect == null)
                m_Rect = (RectTransform)transform;

            Vector3 local = m_Rect.InverseTransformPoint(worldPosition);
            Rect r = m_Rect.rect;
            r.xMin -= m_RectPadding;
            r.yMin -= m_RectPadding;
            r.xMax += m_RectPadding;
            r.yMax += m_RectPadding;

            if (!r.Contains(new Vector2(local.x, local.y)))
                return false;

            float depthMeters = Mathf.Abs(local.z * m_Rect.lossyScale.z);
            return depthMeters <= m_DropDepthMeters;
        }

        public bool Accepts(string columnName)
        {
            ResolveManager();
            return ColumnMappingRules.Accepts(m_Target, columnName, m_Manager);
        }

        /// <summary>Assigns the block's column if this slot accepts it. Returns true on success.</summary>
        public bool TryDrop(LabelBlock block)
        {
            if (block == null)
                return false;

            ResolveManager();
            return ColumnMappingRules.TryApply(m_Target, block.ColumnName, m_Manager);
        }

        private void UpdateHighlight()
        {
            IReadOnlyList<LabelBlock> held = LabelBlock.HeldBlocks;
            int state = 0;

            if (held.Count > 0)
            {
                state = 3;
                foreach (LabelBlock block in held)
                {
                    if (block == null || !Accepts(block.ColumnName))
                        continue;

                    state = Contains(block.transform.position) ? 2 : 1;
                    if (state == 2)
                        break;
                }
            }

            HighlightState = state;

            if (m_Background != null)
            {
                m_Background.color = state switch
                {
                    1 => m_ValidColor,
                    2 => m_HoverColor,
                    3 => m_InvalidColor,
                    _ => m_IdleColor
                };
            }
        }

        private void RefreshChips()
        {
            if (m_ChipsText == null || m_Target != MappingTarget.Filter)
                return;

            ResolveManager();
            if (m_Manager == null || m_Manager.LoadedDataset == null || m_Manager.Filters.Count == 0)
            {
                m_ChipsText.text = "No filters";
                if (m_ClearButton != null) m_ClearButton.interactable = false;
                return;
            }

            StringBuilder sb = new StringBuilder();
            foreach (MultiplayerScatterplotManager.FilterEntry entry in m_Manager.Filters)
            {
                DatasetColumn column = m_Manager.LoadedDataset.GetColumn(entry.ColumnIndex);
                if (column == null)
                    continue;

                if (sb.Length > 0)
                    sb.Append("  <color=#888888>OR</color>  ");

                sb.Append("<mark=#33333380>[");
                sb.Append(column.Name);
                if (!string.IsNullOrEmpty(entry.Label))
                    sb.Append(" = ").Append(entry.Label);
                sb.Append("]</mark>");
            }

            m_ChipsText.text = sb.ToString();
            if (m_ClearButton != null) m_ClearButton.interactable = true;
        }

        private void OnClearClicked()
        {
            ResolveManager();
            if (m_Manager != null)
                m_Manager.RequestClearFiltersRpc();
        }
    }
}
