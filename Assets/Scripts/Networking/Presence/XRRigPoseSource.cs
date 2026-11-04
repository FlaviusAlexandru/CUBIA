using Unity.XR.CoreUtils;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Reads the local player's head, hand and pointing-ray poses from the scene's XR rig
    /// ("XR Origin Hands (XR Rig)"). The rig's XRInputModalityManager activates either the
    /// "Left/Right Controller" or the "Left/Right Hand" object, so each side uses whichever is active.
    /// </summary>
    public sealed class XRRigPoseSource
    {
        sealed class Side
        {
            public Transform Controller;      // controller root (TrackedPoseDriver)
            public Transform ControllerTip;   // poke point at the controller's tip
            public LineRenderer ControllerRay;
            public Transform Hand;            // tracked-hand root (no pose of its own)
            public Transform HandAim;         // tracked-hand aim pose
            public LineRenderer HandRay;
        }

        public Transform Head { get; private set; }

        /// <summary>False once the rig was destroyed (e.g. by a scene change); call Find again.</summary>
        public bool IsValid => Head != null;
        readonly Side m_Left;
        readonly Side m_Right;

        XRRigPoseSource(Transform head, Side left, Side right)
        {
            Head = head;
            m_Left = left;
            m_Right = right;
        }

        /// <summary>Finds the rig in the loaded scenes, or null when there is none.</summary>
        public static XRRigPoseSource Find()
        {
            var origin = Object.FindAnyObjectByType<XROrigin>();
            if (origin == null || origin.Camera == null) return null;

            Transform root = origin.transform;
            return new XRRigPoseSource(origin.Camera.transform, FindSide(root, "Left"), FindSide(root, "Right"));
        }

        /// <summary>
        /// Pose of the active controller/hand on one side, and the end point of its interactor
        /// line while that line is visible. False when neither controller nor hand is active.
        /// </summary>
        public bool TryGetHand(bool left, out Pose pose, out bool rayVisible, out Vector3 rayEnd)
        {
            Side side = left ? m_Left : m_Right;
            pose = default;
            rayVisible = false;
            rayEnd = default;

            Transform source;
            LineRenderer ray;
            if (IsActive(side.Controller)) { source = side.Controller; ray = side.ControllerRay; }
            else if (IsActive(side.Hand) && side.HandAim != null) { source = side.HandAim; ray = side.HandRay; }
            else return false;

            pose = new Pose(source.position, source.rotation);

            if (ray != null && ray.enabled && ray.gameObject.activeInHierarchy && ray.positionCount >= 2)
            {
                Vector3 end = ray.GetPosition(ray.positionCount - 1);
                rayEnd = ray.useWorldSpace ? end : ray.transform.TransformPoint(end);
                rayVisible = true;
            }
            return true;
        }

        /// <summary>World position of the tip of the active controller on one side (used to touch table corners).</summary>
        public bool TryGetControllerTip(bool left, out Vector3 tip)
        {
            Side side = left ? m_Left : m_Right;
            tip = default;
            if (!IsActive(side.Controller)) return false;
            tip = (side.ControllerTip != null ? side.ControllerTip : side.Controller).position;
            return true;
        }

        static bool IsActive(Transform t) => t != null && t.gameObject.activeInHierarchy;

        static Side FindSide(Transform rigRoot, string prefix)
        {
            var side = new Side
            {
                Controller = FindDeep(rigRoot, prefix + " Controller"),
                Hand = FindDeep(rigRoot, prefix + " Hand"),
            };
            if (side.Controller != null)
            {
                side.ControllerTip = FindDeep(side.Controller, "Poke Point");
                side.ControllerRay = FindRay(side.Controller);
            }
            if (side.Hand != null)
            {
                side.HandAim = FindDeep(side.Hand, "Aim Pose");
                side.HandRay = FindRay(side.Hand);
            }
            return side;
        }

        static LineRenderer FindRay(Transform sideRoot)
        {
            Transform nearFar = FindDeep(sideRoot, "Near-Far Interactor");
            return nearFar != null ? nearFar.GetComponentInChildren<LineRenderer>(true) : null;
        }

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
