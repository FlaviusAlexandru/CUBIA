using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace DataViz
{
    /// <summary>
    /// Minimum viable implementation: lets users grab, undock, and uniformly
    /// scale the coordinate system (scatterplot) using XR Interaction
    /// Toolkit's built-in grab + two-handed scale support.
    /// </summary>
    [RequireComponent(typeof(XRGrabInteractable))]
    public class CoordinateSystemInteractable : MonoBehaviour
    {
        [Header("References")]
        public ScatterplotVisualizer m_Visualizer;

        [Tooltip("Transform to re-parent to when resetting the plot's position. Leave empty to use whatever parent this object had at Awake.")]
        public Transform m_DockParent;

        [Header("Scale Bounds")]
        [Tooltip("Smallest allowed scale multiplier relative to the docked scale (e.g. 0.01 = 1% size, fits in a palm).")]
        public float m_MinScale = 0.01f;

        [Tooltip("Largest allowed scale multiplier relative to the docked scale (e.g. 100 = walk-in-the-data size).")]
        public float m_MaxScale = 100f;

        public bool IsDocked => m_IsDocked;

        // [Multiplayer] Raised at the end of ResetPosition(), so NetworkPlotTransform can sync the reset.
        public event System.Action OnResetPerformed;

        private XRGrabInteractable m_GrabInteractable;
        private XRGeneralGrabTransformer m_GrabTransformer;

        private Transform m_OriginalParent;
        private Vector3 m_DockedLocalPosition;
        private Quaternion m_DockedLocalRotation;
        private Vector3 m_DockedLocalScale;

        private bool m_IsDocked = true;

        private void Awake()
        {
            m_GrabInteractable = GetComponent<XRGrabInteractable>();

            // XRGrabInteractable auto-adds an XRGeneralGrabTransformer by
            // default (its "Add Default Grab Transformers" option) - grab
            // that existing one rather than adding a second, redundant one.
            m_GrabTransformer = GetComponent<XRGeneralGrabTransformer>();
            if (m_GrabTransformer == null)
            {
                m_GrabTransformer = gameObject.AddComponent<XRGeneralGrabTransformer>();
            }

            // Confirmed min/max scale ratio fields from Unity's XRI docs.
            m_GrabTransformer.minimumScaleRatio = m_MinScale;
            m_GrabTransformer.maximumScaleRatio = m_MaxScale;

            // IMPORTANT - MANUAL STEP REQUIRED:
            // Unity's own docs state two-handed scaling is "disabled by
            // default" on this component, but I don't have a confirmed exact
            // C# property name for that toggle (rather than guess wrong a
            // 4th time this conversation). Open this GameObject's
            // "XR General Grab Transformer" component in the Inspector and
            // enable whichever field controls two-handed scaling - it should
            // be visible near the scale-ratio fields you're already setting
            // above. Once you find the exact field name, it can be set here
            // in code too if you'd rather not rely on the Inspector value.

            m_GrabInteractable.trackPosition = true;
            m_GrabInteractable.trackRotation = true;
            m_GrabInteractable.throwOnDetach = false;

            // Remember the starting transform state so ResetPosition() can
            // restore it exactly. Nothing moves or resizes the plot between
            // Awake and the first frame (the table/prefab place it), so this
            // is the true starting pose.
            m_OriginalParent = m_DockParent != null ? m_DockParent : transform.parent;
            m_DockedLocalPosition = transform.localPosition;
            m_DockedLocalRotation = transform.localRotation;
            m_DockedLocalScale = transform.localScale;
        }

        private void Start()
        {
            if (m_Visualizer == null)
            {
                m_Visualizer = GetComponent<ScatterplotVisualizer>();
            }

            m_GrabInteractable.selectEntered.AddListener(OnGrabbed);
        }

        private void OnDestroy()
        {
            if (m_GrabInteractable != null)
            {
                m_GrabInteractable.selectEntered.RemoveListener(OnGrabbed);
            }
        }

        private void OnGrabbed(SelectEnterEventArgs args)
        {
            if (!m_IsDocked)
                return;

            // Undock on first grab: detach from the table parent so it's
            // free to move in world space. SetParent(null, true) preserves
            // its current world position/rotation/scale - no visual snap.
            transform.SetParent(null, true);
            m_IsDocked = false;

            Debug.Log("[CoordinateSystemInteractable] Grabbed and undocked.");
        }

        /// <summary>
        /// Snaps the coordinate system back to where it started: the parent,
        /// local position, rotation and scale it had at Awake. If any
        /// interactor is holding it, that grab is cancelled first through the
        /// XR interaction manager, so the hand stops driving the transform and
        /// the plot doesn't jump back to it on the next frame. This is the one
        /// way to reset the plot (wired to the menu's Reset position button).
        /// </summary>
        public void ResetPosition()
        {
            ReleaseFromInteractors();

            transform.SetParent(m_OriginalParent, false);
            transform.localPosition = m_DockedLocalPosition;
            transform.localRotation = m_DockedLocalRotation;
            transform.localScale = m_DockedLocalScale;
            m_IsDocked = true;

            Debug.Log("[CoordinateSystemInteractable] Reset to starting position.");
            OnResetPerformed?.Invoke(); // [Multiplayer]
        }

        private void ReleaseFromInteractors()
        {
            if (m_GrabInteractable == null || !m_GrabInteractable.isSelected)
                return;

            XRInteractionManager manager = m_GrabInteractable.interactionManager;
            if (manager == null)
            {
                Debug.LogWarning("[CoordinateSystemInteractable] Plot is held but has no interaction manager; can't release it before resetting.", this);
                return;
            }

            // Cancels every interactor selecting the plot (two-handed grabs
            // included). XRGrabInteractable.Drop() runs synchronously here.
            manager.CancelInteractableSelection((IXRSelectInteractable)m_GrabInteractable);
        }

        /// <summary>
        /// Sets a uniform scale multiplier directly (e.g. from a UI slider),
        /// clamped to the configured min/max bounds. Independent of whether
        /// currently grabbed.
        /// </summary>
        public void SetScale(float scaleMultiplier)
        {
            float clamped = Mathf.Clamp(scaleMultiplier, m_MinScale, m_MaxScale);
            transform.localScale = m_DockedLocalScale * clamped;
        }
    }
}