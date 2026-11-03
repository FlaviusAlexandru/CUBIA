using System;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// STUB — owner: presence/spatial agent. Lives on the DataVisualizationTable root.
    /// Every networked transform (plot, heads, hands, rays) is sent in this table's local frame,
    /// so where each player puts their own virtual table decides where the others appear.
    /// Place table: the local player grabs and moves their own table (local only, never synced).
    /// Align (co-located): touch two corners of the real table with a controller; the virtual
    /// table snaps onto it.
    /// </summary>
    public class TableFrame : MonoBehaviour
    {
        public static TableFrame Instance { get; private set; }

        /// <summary>The transform that defines the shared frame (the table root).</summary>
        public Transform Anchor => transform;

        public bool IsPlacing { get; private set; }

        /// <summary>0 = not aligning, 1 = waiting for corner 1, 2 = waiting for corner 2.</summary>
        public int AlignStep { get; private set; }

        public event Action OnPlacingChanged;
        public event Action OnAlignStepChanged;
        /// <summary>Raised whenever the local table pose changes (placed or aligned).</summary>
        public event Action OnTableMoved;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        public Vector3 WorldToTable(Vector3 world) => Anchor.InverseTransformPoint(world);
        public Vector3 TableToWorld(Vector3 local) => Anchor.TransformPoint(local);
        public Quaternion WorldToTable(Quaternion world) => Quaternion.Inverse(Anchor.rotation) * world;
        public Quaternion TableToWorld(Quaternion local) => Anchor.rotation * local;

        public void SetPlacing(bool placing) { }
        public void BeginAlign() { }
        public void CancelAlign() { }
        /// <summary>Restore the table to its scene-authored pose.</summary>
        public void ResetTablePose() { }

        static void Unused(TableFrame t) { t.OnPlacingChanged?.Invoke(); t.OnAlignStepChanged?.Invoke(); t.OnTableMoved?.Invoke(); }
    }
}
