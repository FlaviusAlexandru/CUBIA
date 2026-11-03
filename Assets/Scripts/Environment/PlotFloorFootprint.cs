using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// Feeds the StudioFloor shader the table position (centre of the distance rings) and the plot's
    /// world bounds (for the footprint drawn under it). The footprint is a depth cue that works at any
    /// plot size or height, unlike shadows, which URP only draws within its shadow distance.
    /// Also keeps the ring labels and scale posts centred on the table when the table is moved.
    /// </summary>
    [ExecuteAlways]                // so the footprint and rings also show in the Scene view
    [DefaultExecutionOrder(1000)] // after the plot has been moved/scaled this frame
    public class PlotFloorFootprint : MonoBehaviour
    {
        [Tooltip("The plot's bounds collider (ScatterplotVisualizer's BoxCollider). Found automatically if empty.")]
        public Collider m_PlotBounds;

        [Tooltip("Centre of the floor's distance rings. Usually the table root. Defaults to this transform.")]
        public Transform m_RingCentre;

        [Tooltip("Optional: ring labels and scale posts, kept on the floor under the ring centre.")]
        public Transform m_Markers;

        static readonly int s_Centre = Shader.PropertyToID("_CubiaStudioCenter");
        static readonly int s_BoundsMin = Shader.PropertyToID("_CubiaPlotBoundsMin");
        static readonly int s_BoundsMax = Shader.PropertyToID("_CubiaPlotBoundsMax");

        void Awake()
        {
            if (m_RingCentre == null) m_RingCentre = transform;
            if (m_PlotBounds == null)
            {
                var plot = FindAnyObjectByType<CoordinateSystemInteractable>(FindObjectsInactive.Include);
                if (plot != null) m_PlotBounds = plot.GetComponent<Collider>();
            }
        }

        void LateUpdate()
        {
            Vector3 c = m_RingCentre.position;
            Shader.SetGlobalVector(s_Centre, new Vector4(c.x, c.y, c.z, 1f));

            if (m_Markers != null)
                m_Markers.position = new Vector3(c.x, m_Markers.position.y, c.z);

            if (m_PlotBounds != null && m_PlotBounds.gameObject.activeInHierarchy)
            {
                Bounds b = m_PlotBounds.bounds;
                Shader.SetGlobalVector(s_BoundsMin, new Vector4(b.min.x, b.min.y, b.min.z, 1f));
                Shader.SetGlobalVector(s_BoundsMax, new Vector4(b.max.x, b.max.y, b.max.z, 1f));
            }
            else
            {
                Shader.SetGlobalVector(s_BoundsMin, Vector4.zero);
            }
        }

        void OnDisable() => Shader.SetGlobalVector(s_BoundsMin, Vector4.zero);
    }
}
