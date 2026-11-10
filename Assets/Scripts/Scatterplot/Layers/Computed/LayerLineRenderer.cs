using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DataViz
{
    /// <summary>
    /// Draws every computed layer of the plot as LineRenderers under the visualizer (LFR-04, 21, 22).
    /// Adds itself to each ScatterplotVisualizer at runtime (see LayerLineRendererBootstrap below), so no scene edit
    /// is needed. Listens to ScatterplotVisualizer.OnFrameBuilt; a layer's shape is recalculated only
    /// when ComputedLayerBuilder.Signature changes (never on a scrub frame), while colour, opacity,
    /// width and visibility are re-applied every frame because that is cheap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LayerLineRenderer : MonoBehaviour
    {
        [Tooltip("Optional. When empty a material is found at runtime: URP Particles/Unlit, Sprites/Default, " +
                 "UI/Default, the material of any LineRenderer in the scene, then a copy of the visualizer's axis material.")]
        [SerializeField] Material m_LineMaterial;

        // Half the length of each of the three marker strokes, as a fraction of the axis length.
        const float k_MarkerHalfLength = 0.03f;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int s_Color = Shader.PropertyToID("_Color");

        sealed class Entry
        {
            public bool Built;
            public int Signature;
            public ComputedShape Shape;
            public string Status = "";
            public GameObject Root;
            public readonly List<LineRenderer> Lines = new();
        }

        ScatterplotVisualizer m_Visualizer;
        Transform m_Container;
        Material m_Material;
        bool m_OwnsMaterial, m_MaterialResolved;
        float m_LastScale = -1f;
        readonly Dictionary<string, Entry> m_Entries = new();
        readonly HashSet<string> m_Seen = new();
        MaterialPropertyBlock m_Block;

        /// <summary>How many shape calculations ran (diagnostics: stays still while scrubbing).</summary>
        public int RecalculationCount { get; private set; }

        /// <summary>The last status of a computed layer ("" when fine, e.g. "source missing"); "" for an unknown id.</summary>
        public string GetStatus(string layerId) =>
            layerId != null && m_Entries.TryGetValue(layerId, out Entry e) ? e.Status : "";

        void OnEnable()
        {
            if (m_Visualizer == null)
                m_Visualizer = GetComponent<ScatterplotVisualizer>();
            if (m_Visualizer == null)
            {
                enabled = false;
                return;
            }
            m_Visualizer.OnFrameBuilt += Refresh;
            Refresh(m_Visualizer.LastFrame);
        }

        void OnDisable()
        {
            if (m_Visualizer != null)
                m_Visualizer.OnFrameBuilt -= Refresh;
        }

        void OnDestroy()
        {
            foreach (Entry e in m_Entries.Values)
                DestroyEntry(e);
            m_Entries.Clear();
            if (m_Container != null)
                Destroy(m_Container.gameObject);
            if (m_OwnsMaterial && m_Material != null)
                Destroy(m_Material);
        }

        void LateUpdate()
        {
            // LineRenderer widths ignore the transform's scale; follow the plot when it is grabbed and scaled.
            float scale = transform.lossyScale.x;
            if (!Mathf.Approximately(scale, m_LastScale))
            {
                m_LastScale = scale;
                ApplyStyleToAll();
            }
        }

        void Refresh(PlotFrame frame)
        {
            MultiplayerScatterplotManager manager = m_Visualizer.m_Manager != null ? m_Visualizer.m_Manager : MultiplayerScatterplotManager.Instance;
            LayerStack stack = manager != null ? manager.Stack : null;
            AxisBounds bounds = frame != null ? frame.Bounds : new AxisBounds();

            m_Seen.Clear();
            if (stack != null)
            {
                foreach (PlotLayer layer in stack.Layers)
                {
                    if (layer == null || layer.Kind != LayerKind.Computed || string.IsNullOrEmpty(layer.Id))
                        continue;
                    m_Seen.Add(layer.Id);

                    if (!m_Entries.TryGetValue(layer.Id, out Entry entry))
                    {
                        entry = new Entry();
                        m_Entries.Add(layer.Id, entry);
                    }

                    int signature = ComputedLayerBuilder.Signature(layer, stack, bounds);
                    if (!entry.Built || signature != entry.Signature)
                    {
                        Recalculate(entry, layer, stack, bounds);
                        entry.Signature = signature;
                    }
                    ApplyStyle(entry, layer);
                }
            }

            // Layers that are gone (removed, or replaced by a snapshot without them).
            List<string> stale = null;
            foreach (string id in m_Entries.Keys)
            {
                if (!m_Seen.Contains(id))
                    (stale ??= new List<string>()).Add(id);
            }
            if (stale != null)
            {
                foreach (string id in stale)
                {
                    DestroyEntry(m_Entries[id]);
                    m_Entries.Remove(id);
                }
            }
        }

        void Recalculate(Entry entry, PlotLayer layer, LayerStack stack, AxisBounds bounds)
        {
            RecalculationCount++;
            entry.Built = true;
            try
            {
                entry.Shape = ComputedLayerBuilder.Build(layer, stack, bounds, out _, out string status);
                entry.Status = status ?? "";
            }
            catch (System.Exception e)
            {
                // A broken computed layer must not take the plot down (AT-10: no error in the console
                // on a normal source removal; anything that does land here is a real bug).
                Debug.LogException(e);
                entry.Shape = default;
                entry.Status = "error";
            }

            ComputedShape shape = entry.Shape;
            if (!shape.Valid)
            {
                SetLineCount(entry, layer, 0);
                return;
            }

            if (shape.IsMarker)
            {
                SetLineCount(entry, layer, 3);
                for (int axis = 0; axis < 3; axis++)
                {
                    Vector3 offset = Vector3.zero;
                    offset[axis] = k_MarkerHalfLength;
                    LineRenderer line = entry.Lines[axis];
                    line.loop = false;
                    line.positionCount = 2;
                    line.SetPosition(0, m_Visualizer.CubeToLocal(shape.Marker - offset));
                    line.SetPosition(1, m_Visualizer.CubeToLocal(shape.Marker + offset));
                }
                return;
            }

            SetLineCount(entry, layer, 1);
            LineRenderer polyline = entry.Lines[0];
            polyline.loop = shape.Closed;
            var positions = new Vector3[shape.Polyline.Length];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = m_Visualizer.CubeToLocal(shape.Polyline[i]);
            polyline.positionCount = positions.Length;
            polyline.SetPositions(positions);
        }

        void ApplyStyleToAll()
        {
            MultiplayerScatterplotManager manager = m_Visualizer != null && m_Visualizer.m_Manager != null ? m_Visualizer.m_Manager : MultiplayerScatterplotManager.Instance;
            LayerStack stack = manager != null ? manager.Stack : null;
            if (stack == null)
                return;
            foreach (KeyValuePair<string, Entry> pair in m_Entries)
            {
                PlotLayer layer = stack.Find(pair.Key);
                if (layer != null)
                    ApplyStyle(pair.Value, layer);
            }
        }

        void ApplyStyle(Entry entry, PlotLayer layer)
        {
            if (entry.Root == null)
                return;

            bool show = layer.Visible && entry.Shape.Valid;
            if (entry.Root.activeSelf != show)
                entry.Root.SetActive(show);
            if (!show)
                return;

            Color colour = layer.FixedColour;
            colour.a *= Mathf.Clamp(layer.Opacity, 0.05f, 1f);
            float width = Mathf.Max(0.0001f, layer.LineWidth) * Mathf.Abs(transform.lossyScale.x);

            m_Block ??= new MaterialPropertyBlock();
            m_Block.Clear();
            // Colour goes through the property block, not vertex colours: not every shader in the
            // fallback chain reads vertex colours, and the ones that do would square the colour.
            m_Block.SetColor(s_BaseColor, colour);
            m_Block.SetColor(s_Color, colour);
            foreach (LineRenderer line in entry.Lines)
            {
                line.startWidth = width;
                line.endWidth = width;
                line.SetPropertyBlock(m_Block);
            }
        }

        void SetLineCount(Entry entry, PlotLayer layer, int count)
        {
            if (entry.Root == null)
            {
                entry.Root = new GameObject("Computed " + layer.Id);
                entry.Root.transform.SetParent(Container(), false);
            }
            entry.Root.name = "Computed " + layer.Id + " (" + LayerRules.ComputedKindName(layer.ComputedKind) + ")";

            while (entry.Lines.Count < count)
            {
                var go = new GameObject("Line " + entry.Lines.Count);
                go.transform.SetParent(entry.Root.transform, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.alignment = LineAlignment.View;
                line.numCornerVertices = 2;
                line.numCapVertices = 2;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.startColor = Color.white;
                line.endColor = Color.white;
                line.sharedMaterial = LineMaterial();
                entry.Lines.Add(line);
            }
            for (int i = 0; i < entry.Lines.Count; i++)
            {
                bool on = i < count;
                if (entry.Lines[i].gameObject.activeSelf != on)
                    entry.Lines[i].gameObject.SetActive(on);
            }
        }

        Transform Container()
        {
            if (m_Container == null)
            {
                // Same local space as CubeToLocal (the visualizer's own transform).
                m_Container = new GameObject("Computed Layers").transform;
                m_Container.SetParent(transform, false);
            }
            return m_Container;
        }

        static void DestroyEntry(Entry entry)
        {
            if (entry.Root != null)
                Destroy(entry.Root);
            entry.Root = null;
            entry.Lines.Clear();
        }

        /// <summary>
        /// PLAN.md risk 8: Shader.Find returns null on a Quest build when the shader was stripped, so
        /// try several, ending with materials that certainly exist in the build (another LineRenderer's,
        /// then the visualizer's axis material). Resolved once.
        /// </summary>
        Material LineMaterial()
        {
            if (m_MaterialResolved)
                return m_Material;
            m_MaterialResolved = true;

            if (m_LineMaterial != null)
            {
                m_Material = m_LineMaterial;
                return m_Material;
            }

            foreach (string name in new[] { "Universal Render Pipeline/Particles/Unlit", "Sprites/Default", "UI/Default" })
            {
                Shader shader = Shader.Find(name);
                if (shader == null)
                    continue;
                m_Material = new Material(shader) { name = "Computed Layer Line (" + name + ")" };
                MakeTransparent(m_Material);
                m_OwnsMaterial = true;
                return m_Material;
            }

            foreach (LineRenderer other in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include))
            {
                if (other.sharedMaterial != null && !other.transform.IsChildOf(transform))
                {
                    m_Material = new Material(other.sharedMaterial) { name = "Computed Layer Line (copy)" };
                    m_OwnsMaterial = true;
                    return m_Material;
                }
            }

            if (m_Visualizer != null && m_Visualizer.m_AxisMaterial != null)
            {
                m_Material = new Material(m_Visualizer.m_AxisMaterial) { name = "Computed Layer Line (axis copy)" };
                MakeTransparent(m_Material);
                m_OwnsMaterial = true;
                return m_Material;
            }

            Debug.LogWarning("[LayerLineRenderer] No material for computed layer lines could be found; they are drawn with Unity's default.");
            return null;
        }

        // URP's transparent surface settings; harmless on shaders without these properties.
        static void MakeTransparent(Material m)
        {
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
        }
    }

    // ---- bootstrap: add the component to every visualizer, no scene edit -------------------

    /// <summary>Adds a LayerLineRenderer to every ScatterplotVisualizer after each scene load (the StudyDashboardBootstrap pattern).</summary>
    static class LayerLineRendererBootstrap
    {
        sealed class Runner : MonoBehaviour { }
        static Runner s_Runner;

        // Statics survive play mode with domain reload disabled; start clean each time.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Runner = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            AttachAll();
            Schedule(); // again next frame, for visualizers created during the first scene's Awake/Start
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            AttachAll();
            Schedule();
        }

        static void Schedule()
        {
            if (s_Runner == null)
            {
                var host = new GameObject("[Computed Layer Lines Bootstrap]") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(host);
                s_Runner = host.AddComponent<Runner>();
            }
            s_Runner.StartCoroutine(AttachNextFrame());
        }

        static IEnumerator AttachNextFrame()
        {
            yield return null;
            AttachAll();
        }

        static void AttachAll()
        {
            foreach (ScatterplotVisualizer visualizer in Object.FindObjectsByType<ScatterplotVisualizer>(FindObjectsInactive.Include))
            {
                if (visualizer != null && visualizer.GetComponent<LayerLineRenderer>() == null)
                    visualizer.gameObject.AddComponent<LayerLineRenderer>();
            }
        }
    }
}
