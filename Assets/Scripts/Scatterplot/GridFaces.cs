using Unity.XR.CoreUtils;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// PF-03 (pilot 2): where the three grid planes go. Each plane orientation (perpendicular to
    /// X, Y or Z) can sit on either of two opposite cube faces; the grid goes on the face farther
    /// from the participant's start viewpoint, so the planes "complete the cube" behind the data
    /// instead of lying along the red, green and blue axes in front of it. Worked out once, from
    /// the start position (fixed for now, as asked), in the plot's axes space where the cube spans
    /// 0..length on each axis.
    /// </summary>
    public static class GridFaces
    {
        /// <summary>Without a viewpoint: back wall (z = length), far side (x = length), floor (y = 0).</summary>
        public static readonly Vector3Int Default = new Vector3Int(1, 0, 1);

        /// <summary>Eye height used while the headset is not tracking yet (camera at the rig's floor).</summary>
        public const float StandingEyeHeight = 1.6f;

        /// <summary>
        /// Per axis, 1 when the plane perpendicular to it goes at coordinate length, 0 when at 0:
        /// the side farther from viewer (axes space). A viewer exactly halfway keeps Default's side.
        /// </summary>
        public static Vector3Int FarSides(Vector3 viewer, float length)
        {
            float half = length * 0.5f;
            int Side(float v, int tie) => v < half ? 1 : v > half ? 0 : tie;
            return new Vector3Int(Side(viewer.x, Default.x), Side(viewer.y, Default.y), Side(viewer.z, Default.z));
        }

        /// <summary>
        /// The participant's start viewpoint in world space: the XR Origin's position (the start
        /// position; head tracking may not have started yet) raised by the camera's height above it,
        /// or StandingEyeHeight while that is below 0.5 m. False when the scene has no XR Origin.
        /// </summary>
        public static bool TryStartViewpoint(out Vector3 world)
        {
            world = default;
            XROrigin origin = Object.FindAnyObjectByType<XROrigin>();
            if (origin == null)
                return false;
            Transform rig = origin.transform;
            float height = StandingEyeHeight;
            Camera cam = origin.Camera != null ? origin.Camera : Camera.main;
            if (cam != null)
            {
                float camHeight = rig.InverseTransformPoint(cam.transform.position).y;
                if (camHeight > 0.5f)
                    height = camHeight;
            }
            world = rig.position + rig.up * height;
            return true;
        }
    }
}
