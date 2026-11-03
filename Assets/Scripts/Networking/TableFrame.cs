using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace DataViz.Networking
{
    /// <summary>
    /// Lives on the DataVisualizationTable root.
    /// Every networked transform (plot, heads, hands, rays) is sent in this table's local frame,
    /// so where each player puts their own virtual table decides where the others appear.
    /// Place table: the local player grabs and moves their own table (local only, never synced).
    /// Align (co-located): touch two corners of the real table with a controller; the virtual
    /// table snaps onto it. The corner input itself comes from <see cref="TableAlignTool"/>.
    /// </summary>
    public class TableFrame : MonoBehaviour
    {
        public static TableFrame Instance { get; private set; }

        [Tooltip("Grab used while placing the table. Kept disabled except during Place table, so the table can't be bumped by accident.")]
        [SerializeField] XRGrabInteractable m_PlaceGrab;

        [Tooltip("Collider of the table top. Its two front corners (local -Z edge) are the Align corners 1 (-X) and 2 (+X).")]
        [SerializeField] BoxCollider m_TableTop;

        [Tooltip("Corner touches closer together than this (metres, horizontally) are rejected as a mis-touch.")]
        [SerializeField] float m_MinCornerDistance = 0.3f;

        /// <summary>The transform that defines the shared frame (the table root).</summary>
        public Transform Anchor => transform;

        public bool IsPlacing { get; private set; }

        /// <summary>0 = not aligning, 1 = waiting for corner 1, 2 = waiting for corner 2.</summary>
        public int AlignStep { get; private set; }

        public event Action OnPlacingChanged;
        public event Action OnAlignStepChanged;
        /// <summary>Raised whenever the local table pose changes (placed or aligned).</summary>
        public event Action OnTableMoved;

        Pose m_AuthoredPose;
        Vector3 m_FirstCorner;
        // Anchor-local midpoint of the front edge's top (between Align corners 1 and 2).
        Vector3 m_FrontEdgeMidLocal;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            m_AuthoredPose = new Pose(transform.position, transform.rotation);
            m_FrontEdgeMidLocal = ComputeFrontEdgeMidLocal();

            if (m_PlaceGrab != null)
            {
                m_PlaceGrab.enabled = false;
                m_PlaceGrab.selectExited.AddListener(OnPlaceGrabReleased);
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (m_PlaceGrab != null) m_PlaceGrab.selectExited.RemoveListener(OnPlaceGrabReleased);
        }

        public Vector3 WorldToTable(Vector3 world) => Anchor.InverseTransformPoint(world);
        public Vector3 TableToWorld(Vector3 local) => Anchor.TransformPoint(local);
        public Quaternion WorldToTable(Quaternion world) => Quaternion.Inverse(Anchor.rotation) * world;
        public Quaternion TableToWorld(Quaternion local) => Anchor.rotation * local;

        public void SetPlacing(bool placing)
        {
            if (IsPlacing == placing) return;
            if (placing) CancelAlign();
            else ReleasePlaceGrab();

            IsPlacing = placing;
            if (m_PlaceGrab != null) m_PlaceGrab.enabled = placing;
            else if (placing) Debug.LogWarning("[TableFrame] No place grab assigned; run Tools/CUBIA/Multiplayer/2 Setup Presence.", this);

            OnPlacingChanged?.Invoke();
        }

        public void BeginAlign()
        {
            SetPlacing(false);
            SetAlignStep(1);
        }

        public void CancelAlign() => SetAlignStep(0);

        /// <summary>
        /// Feeds one touched corner (world position of the controller tip) into the Align flow.
        /// Called by <see cref="TableAlignTool"/>; ignored when not aligning.
        /// </summary>
        public void SubmitAlignCorner(Vector3 worldPoint)
        {
            switch (AlignStep)
            {
                case 1:
                    m_FirstCorner = worldPoint;
                    SetAlignStep(2);
                    break;
                case 2:
                    if (AlignToCorners(m_FirstCorner, worldPoint))
                        SetAlignStep(0);
                    else
                        SetAlignStep(1); // mis-touch: start over from corner 1
                    break;
            }
        }

        /// <summary>
        /// Snaps the table so its front edge (corner 1 → corner 2) lies on the two touched points.
        /// Uses the edge midpoint and horizontal direction only, so the table stays level and a
        /// real table of a different length still lines up at its centre.
        /// </summary>
        public bool AlignToCorners(Vector3 corner1World, Vector3 corner2World)
        {
            Vector3 edge = corner2World - corner1World;
            edge.y = 0f;
            if (edge.magnitude < m_MinCornerDistance)
            {
                Debug.LogWarning($"[TableFrame] Align corners are only {edge.magnitude:0.00} m apart; touch the two ends of the table's long front edge.", this);
                return false;
            }

            Vector3 right = edge.normalized;
            // Left-handed: right x up = forward, so the table's local +X runs corner 1 → corner 2.
            Quaternion rotation = Quaternion.LookRotation(Vector3.Cross(right, Vector3.up), Vector3.up);
            Vector3 mid = (corner1World + corner2World) * 0.5f;
            Vector3 position = mid - rotation * Vector3.Scale(m_FrontEdgeMidLocal, transform.lossyScale);

            ReleasePlaceGrab();
            transform.SetPositionAndRotation(position, rotation);
            OnTableMoved?.Invoke();
            return true;
        }

        /// <summary>Restore the table to its scene-authored pose.</summary>
        public void ResetTablePose()
        {
            ReleasePlaceGrab();
            transform.SetPositionAndRotation(m_AuthoredPose.position, m_AuthoredPose.rotation);
            OnTableMoved?.Invoke();
        }

        void SetAlignStep(int step)
        {
            if (AlignStep == step) return;
            AlignStep = step;
            OnAlignStepChanged?.Invoke();
        }

        void OnPlaceGrabReleased(SelectExitEventArgs args)
        {
            if (!m_PlaceGrab.isSelected) OnTableMoved?.Invoke();
        }

        void ReleasePlaceGrab()
        {
            if (m_PlaceGrab == null || !m_PlaceGrab.isSelected) return;
            m_PlaceGrab.interactionManager?.CancelInteractableSelection((IXRSelectInteractable)m_PlaceGrab);
        }

        Vector3 ComputeFrontEdgeMidLocal()
        {
            if (m_TableTop == null) return Vector3.zero;

            // Front edge midpoint on the top face, in the table top's own space, then into anchor space.
            Vector3 half = m_TableTop.size * 0.5f;
            Vector3 topLocal = m_TableTop.center + new Vector3(0f, half.y, -half.z);
            return transform.InverseTransformPoint(m_TableTop.transform.TransformPoint(topLocal));
        }
    }
}
