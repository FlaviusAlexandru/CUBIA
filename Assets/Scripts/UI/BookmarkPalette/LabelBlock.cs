using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace DataViz
{
    /// <summary>
    /// One bookmarked label (dataset column) as a grabbable block on the BookmarkPalette.
    ///
    /// Grab flow: the palette's block itself is what the hand picks up - at the moment it is
    /// grabbed, the palette spawns an identical replacement in the same place, so the palette
    /// visibly "keeps its block" while the hand holds a copy. On release the held copy asks
    /// MappingDropSlot for a slot under it: a valid slot assigns the label (and a short haptic
    /// pulse plays on the releasing controller); either way the copy is then destroyed.
    ///
    /// Works for both ray (far) and direct (near) selection - it is a plain XRGrabInteractable,
    /// which NearFarInteractor, XRRayInteractor and XRDirectInteractor can all select.
    /// </summary>
    [RequireComponent(typeof(XRGrabInteractable))]
    public class LabelBlock : MonoBehaviour
    {
        [Tooltip("TextMeshPro (3D) that shows the column name.")]
        public TMP_Text m_LabelText;

        [Tooltip("Renderer tinted by column type (numeric/DateTime vs categorical vs other).")]
        public Renderer m_BackgroundRenderer;

        public Color m_NumericColor = new Color(0.20f, 0.45f, 0.85f);
        public Color m_CategoricalColor = new Color(0.90f, 0.55f, 0.15f);
        public Color m_OtherColor = new Color(0.45f, 0.45f, 0.45f);

        [Header("Drop feedback")]
        [Range(0f, 1f)] public float m_HapticAmplitude = 0.5f;
        public float m_HapticDuration = 0.08f;

        /// <summary>Column name this block stands for.</summary>
        public string ColumnName { get; private set; }

        /// <summary>True while this block sits on the palette (not yet grabbed).</summary>
        public bool IsInPalette { get; private set; } = true;

        /// <summary>Palette that spawned this block (gets a callback when it is taken).</summary>
        public BookmarkPalette Owner { get; set; }

        /// <summary>Index of the palette cell this block was created in.</summary>
        public int PaletteIndex { get; set; }

        private static readonly List<LabelBlock> s_HeldBlocks = new();

        /// <summary>Blocks currently held by an interactor (what MappingDropSlot highlights for).</summary>
        public static IReadOnlyList<LabelBlock> HeldBlocks => s_HeldBlocks;

        /// <summary>Raised whenever a block is picked up or released/destroyed.</summary>
        public static event Action HeldBlocksChanged;

        private XRGrabInteractable m_Grab;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void Awake()
        {
            m_Grab = GetComponent<XRGrabInteractable>();
            m_Grab.selectEntered.AddListener(OnSelectEntered);
            m_Grab.selectExited.AddListener(OnSelectExited);
        }

        private void OnDestroy()
        {
            if (m_Grab != null)
            {
                m_Grab.selectEntered.RemoveListener(OnSelectEntered);
                m_Grab.selectExited.RemoveListener(OnSelectExited);
            }

            if (s_HeldBlocks.Remove(this))
                HeldBlocksChanged?.Invoke();
        }

        /// <summary>Sets the column this block represents and tints it by column type.</summary>
        public void Setup(string columnName, DatasetColumn column)
        {
            ColumnName = columnName;
            gameObject.name = $"LabelBlock_{columnName}";

            if (m_LabelText != null)
                m_LabelText.text = columnName;

            Color tint = m_OtherColor;
            if (column != null && column.IsNumeric) tint = m_NumericColor;
            else if (column != null && column.IsCategorical) tint = m_CategoricalColor;

            if (m_BackgroundRenderer != null)
            {
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                m_BackgroundRenderer.GetPropertyBlock(block);
                block.SetColor(BaseColorId, tint);
                block.SetColor(ColorId, tint);
                m_BackgroundRenderer.SetPropertyBlock(block);
            }
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (IsInPalette)
            {
                IsInPalette = false;
                // Detach from the palette (so a palette rebuild never destroys a held block)
                // and let it put a replacement in this cell.
                transform.SetParent(null, true);
                if (Owner != null)
                    Owner.OnBlockTakenFromPalette(this);
            }

            if (!s_HeldBlocks.Contains(this))
            {
                s_HeldBlocks.Add(this);
                HeldBlocksChanged?.Invoke();
            }
        }

        private void OnSelectExited(SelectExitEventArgs args)
        {
            // Still held by another interactor (e.g. hand-to-hand pass): keep it.
            if (m_Grab.isSelected)
                return;

            s_HeldBlocks.Remove(this);

            bool assigned = false;
            if (!args.isCanceled)
            {
                MappingDropSlot slot = MappingDropSlot.FindSlotAt(transform.position);
                if (slot != null)
                    assigned = slot.TryDrop(this);
            }

            if (assigned)
                SendHaptic(args.interactorObject);

            HeldBlocksChanged?.Invoke();

            // The copy is never left floating around: dropped on a valid slot it has done its
            // job, dropped anywhere else it "returns" (the palette already has its own block).
            Destroy(gameObject);
        }

        private void SendHaptic(IXRSelectInteractor interactor)
        {
            if (interactor is XRBaseInputInteractor inputInteractor)
            {
                inputInteractor.SendHapticImpulse(m_HapticAmplitude, m_HapticDuration);
                return;
            }

            // Fallback for interactors that are not input interactors: any haptic player up the rig.
            if (interactor is Component component)
            {
                var player = component.GetComponentInParent<UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics.HapticImpulsePlayer>();
                if (player != null)
                    player.SendHapticImpulse(m_HapticAmplitude, m_HapticDuration);
            }
        }
    }
}
