using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz
{
    /// <summary>
    /// Text labels next to a layer's points, from the layer's label column (LFR-17, e.g. mole
    /// numbers). Adds itself to each ScatterplotVisualizer at runtime (see LayerLabelRendererBootstrap
    /// below), so no scene edit is needed.
    ///
    /// Per layer with LabelColumnIndex >= 0 it keeps a pool of world-space TextMeshPro labels, at
    /// most MaxLabelsPerLayer (beyond that an even spread of the labelled points, and a warning the
    /// legend shows). The pool is filled only when the layer's slice of the point buffer or its label
    /// column changes (a signature over the slice), never on a playback frame: then only each label's
    /// visibility (in the time window) and alpha (the point's) are re-applied.
    /// The text building is in the static functions LabelText, ChooseLabelled and LimitWarning.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LayerLabelRenderer : MonoBehaviour
    {
        /// <summary>Most labels one layer shows (PLAN.md WP4).</summary>
        public const int MaxLabelsPerLayer = 200;

        [Tooltip("TextMeshPro font size of a label (the label's transform is scaled by Label Scale, as the axis titles are).")]
        [SerializeField] float m_FontSize = 3.5f;
        [Tooltip("Local scale of each label object, in plot units.")]
        [SerializeField] float m_LabelScale = 0.03f;
        [Tooltip("Gap between a point and the start of its label, in plot units (to the viewer's right).")]
        [SerializeField] float m_Offset = 0.012f;

        // Points sampled across a slice for its signature (besides the labelled points themselves).
        const int k_SignatureSamples = 64;
        // A label whose point is fainter than this is hidden (glyph preview hides points with alpha 0).
        const float k_MinAlpha = 0.01f;

        sealed class Label
        {
            public GameObject Go;
            public TextMeshPro Tmp;
            public int BufferIndex;
            public float Alpha = -1f;
        }

        sealed class Entry
        {
            public bool Built;
            public int Signature;
            public GameObject Root;
            public readonly List<Label> Labels = new();
            public readonly List<int> Chosen = new();   // buffer indices picked by ChooseLabelled
            public int Used;                            // Labels[0..Used) are in use
            public int Total;                           // labelled points in the slice
        }

        ScatterplotVisualizer m_Visualizer;
        Transform m_Container;
        readonly Dictionary<string, Entry> m_Entries = new();
        readonly HashSet<string> m_InFrame = new();
        readonly HashSet<string> m_Shown = new();

        /// <summary>How many times a layer's pool was refilled (diagnostics: stays still while scrubbing).</summary>
        public int RebuildCount { get; private set; }

        /// <summary>The label limit warning of a layer (LimitWarning), or null when it shows every labelled point.</summary>
        public string GetWarning(PlotLayer layer)
        {
            if (layer == null || string.IsNullOrEmpty(layer.Id) || !m_Entries.TryGetValue(layer.Id, out Entry e) || !e.Built)
                return null;
            return LimitWarning(layer.Name, e.Chosen.Count, e.Total);
        }

        // ---- pure text building (tested in LayerOverlaySelfTests) ------------------------------

        /// <summary>
        /// The label of one row: the cell of the label column as the rest of the UI writes it
        /// (LayerRows.RowLabel: category name, or the number's ToString()). Null for an empty cell,
        /// an empty category name, a missing column or no dataset (no label).
        /// </summary>
        public static string LabelText(DatasetColumnar dataset, int row, int labelColumn)
        {
            if (dataset == null || labelColumn < 0 || labelColumn >= dataset.ColumnCount)
                return null;
            DatasetColumn column = dataset.GetColumn(labelColumn);
            if (column == null)
                return null;
            string[] table = column.IsCategorical ? dataset.GetCategoryTable(labelColumn) : null;
            string text = LayerRows.RowLabel(dataset, row, labelColumn, column, table != null ? new List<string>(table) : null);
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// Picks which points of a buffer slice get a label: those whose row has a value in the label
        /// column. More than max: max of them, spread evenly over the slice.
        /// Fills into with buffer indices (cleared first) and returns how many points were labelled in all.
        /// </summary>
        public static int ChooseLabelled(IReadOnlyList<int> rows, int start, int count, DatasetColumnar dataset, int labelColumn,
                                         int max, List<int> into)
        {
            into.Clear();
            if (rows == null || dataset == null || labelColumn < 0 || labelColumn >= dataset.ColumnCount || max <= 0)
                return 0;
            int end = Mathf.Min(start + count, rows.Count);

            int total = 0;
            for (int i = start; i < end; i++)
            {
                if (dataset.HasValue(rows[i], labelColumn))
                    total++;
            }

            // The k-th label goes to the labelled point with ordinal floor(k * total / max): exactly
            // max labels, spread evenly over the slice (all of them when total <= max).
            int ordinal = 0;
            long next = 0;
            for (int i = start; i < end && into.Count < max; i++)
            {
                if (!dataset.HasValue(rows[i], labelColumn))
                    continue;
                if (ordinal == next)
                {
                    into.Add(i);
                    next = total > max ? (long)into.Count * total / max : into.Count;
                }
                ordinal++;
            }
            return total;
        }

        /// <summary>The legend warning when a layer has more labelled points than it shows; null otherwise.</summary>
        public static string LimitWarning(string layerName, int shown, int total)
        {
            if (total <= shown)
                return null;
            return $"{layerName}: labels on {shown} of {total} points (at most {MaxLabelsPerLayer} per layer)";
        }

        // ---- component ---------------------------------------------------------------------------

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
            if (m_Container != null)
                m_Container.gameObject.SetActive(true);
            // The bootstrap adds this after the visualizer's first build, and an idle plot may not
            // rebuild for a while. LastFrame and the LastBuilt* lists are published together and no
            // build is running here, so they agree with each other (no references are kept).
            Refresh(m_Visualizer.LastFrame);
        }

        void OnDisable()
        {
            if (m_Visualizer != null)
                m_Visualizer.OnFrameBuilt -= Refresh;
            if (m_Container != null)
                m_Container.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            m_Entries.Clear();
            if (m_Container != null)
                Destroy(m_Container.gameObject);
        }

        // Labels face the viewer (rotation only; their positions change only on a rebuild).
        void LateUpdate()
        {
            if (m_Shown.Count == 0)
                return;
            Camera cam = Camera.main;
            if (cam == null)
                return;
            Quaternion facing = cam.transform.rotation;
            foreach (string id in m_Shown)
            {
                if (!m_Entries.TryGetValue(id, out Entry entry))
                    continue;
                for (int k = 0; k < entry.Used; k++)
                {
                    Label label = entry.Labels[k];
                    if (label.Go.activeSelf)
                        label.Go.transform.rotation = facing;
                }
            }
        }

        void Refresh(PlotFrame frame)
        {
            IReadOnlyList<Vector3> positions = m_Visualizer.LastBuiltPositions;
            List<int> rows = m_Visualizer.LastBuiltRowIndices;
            List<bool> inWindow = m_Visualizer.LastBuiltInTimeWindow;   // null = every point in the window
            List<Color> colours = m_Visualizer.LastBuiltColors;

            m_InFrame.Clear();
            m_Shown.Clear();
            if (frame != null && positions != null && rows != null)
            {
                foreach (LayerFrame layerFrame in frame.Layers)
                {
                    PlotLayer layer = layerFrame.Layer;
                    if (layer == null || string.IsNullOrEmpty(layer.Id))
                        continue;
                    m_InFrame.Add(layer.Id);
                    if (!HasLabels(layerFrame, positions.Count, rows.Count))
                        continue;

                    if (!m_Entries.TryGetValue(layer.Id, out Entry entry))
                    {
                        entry = new Entry();
                        m_Entries.Add(layer.Id, entry);
                    }

                    int signature = Signature(layerFrame, positions, rows, entry.Chosen);
                    if (!entry.Built || signature != entry.Signature)
                    {
                        Rebuild(entry, layerFrame, positions, rows);
                        entry.Signature = Signature(layerFrame, positions, rows, entry.Chosen);
                    }

                    if (!entry.Root.activeSelf)
                        entry.Root.SetActive(true);
                    ApplyVisibility(entry, inWindow, colours);
                    m_Shown.Add(layer.Id);
                }
            }

            // Layers that show no labels now: hide (keep the pool for when they come back), or
            // drop the entry when the layer is gone from the plot.
            List<string> gone = null;
            foreach (KeyValuePair<string, Entry> pair in m_Entries)
            {
                if (m_Shown.Contains(pair.Key))
                    continue;
                if (!m_InFrame.Contains(pair.Key))
                {
                    (gone ??= new List<string>()).Add(pair.Key);
                    continue;
                }
                pair.Value.Built = false;   // its slice may differ when it comes back
                if (pair.Value.Root != null && pair.Value.Root.activeSelf)
                    pair.Value.Root.SetActive(false);
            }
            if (gone != null)
            {
                foreach (string id in gone)
                {
                    if (m_Entries[id].Root != null)
                        Destroy(m_Entries[id].Root);
                    m_Entries.Remove(id);
                }
            }
        }

        // A drawn point layer (visible, available, with points) that names a label column.
        static bool HasLabels(LayerFrame layerFrame, int positionCount, int rowCount)
        {
            PlotLayer layer = layerFrame.Layer;
            return layer.Visible && layer.IsPointLayer && !layer.LocalUnavailable && layer.LabelColumnIndex >= 0
                   && layerFrame.Dataset != null && layerFrame.BufferCount > 0
                   && layerFrame.BufferStart + layerFrame.BufferCount <= positionCount
                   && layerFrame.BufferStart + layerFrame.BufferCount <= rowCount;
        }

        // Equal while the slice holds the same rows at the same places with the same label column:
        // the dataset instance, label column and slice bounds, rows and positions sampled across
        // the slice, and those of the labelled points themselves. Playback (alpha-only rebuilds)
        // keeps it unchanged.
        static int Signature(LayerFrame layerFrame, IReadOnlyList<Vector3> positions, List<int> rows, List<int> chosen)
        {
            var h = new System.HashCode();
            h.Add(RuntimeHelpers.GetHashCode(layerFrame.Dataset));
            h.Add(layerFrame.Layer.LabelColumnIndex);
            int start = layerFrame.BufferStart, count = layerFrame.BufferCount;
            h.Add(start);
            h.Add(count);
            int samples = Mathf.Min(count, k_SignatureSamples);
            for (int s = 0; s < samples; s++)
            {
                int i = start + (int)((long)s * count / samples);
                h.Add(rows[i]);
                h.Add(positions[i]);
            }
            foreach (int i in chosen)
            {
                if (i < 0 || i >= positions.Count || i >= rows.Count)
                {
                    h.Add(-1);
                    continue;
                }
                h.Add(rows[i]);
                h.Add(positions[i]);
            }
            return h.ToHashCode();
        }

        void Rebuild(Entry entry, LayerFrame layerFrame, IReadOnlyList<Vector3> positions, List<int> rows)
        {
            RebuildCount++;
            entry.Built = true;
            PlotLayer layer = layerFrame.Layer;
            DatasetColumnar dataset = layerFrame.Dataset;
            int labelColumn = layer.LabelColumnIndex;

            entry.Total = ChooseLabelled(rows, layerFrame.BufferStart, layerFrame.BufferCount, dataset, labelColumn,
                MaxLabelsPerLayer, entry.Chosen);

            if (entry.Root == null)
            {
                entry.Root = new GameObject();
                entry.Root.transform.SetParent(Container(), false);
            }
            entry.Root.name = "Labels " + layer.Id;

            // Text is built once per pool fill: a category table copy per fill, not per label.
            DatasetColumn column = dataset.GetColumn(labelColumn);
            string[] table = column != null && column.IsCategorical ? dataset.GetCategoryTable(labelColumn) : null;
            List<string> tableList = table != null ? new List<string>(table) : null;

            int used = 0;
            foreach (int index in entry.Chosen)
            {
                string text = column != null ? LayerRows.RowLabel(dataset, rows[index], labelColumn, column, tableList) : null;
                if (string.IsNullOrEmpty(text))
                    continue;
                Label label = LabelAt(entry, used++);
                label.BufferIndex = index;
                label.Go.transform.localPosition = positions[index];
                if (label.Tmp.text != text)
                    label.Tmp.text = text;
            }

            entry.Used = used;
            for (int k = 0; k < entry.Labels.Count; k++)
            {
                if (k >= used && entry.Labels[k].Go.activeSelf)
                    entry.Labels[k].Go.SetActive(false);
            }
        }

        static void ApplyVisibility(Entry entry, List<bool> inWindow, List<Color> colours)
        {
            for (int k = 0; k < entry.Used; k++)
            {
                Label label = entry.Labels[k];
                int i = label.BufferIndex;
                bool inTime = inWindow == null || (i < inWindow.Count && inWindow[i]);
                float alpha = colours != null && i < colours.Count ? colours[i].a : 1f;
                bool show = inTime && alpha > k_MinAlpha;
                if (label.Go.activeSelf != show)
                    label.Go.SetActive(show);
                if (show && !Mathf.Approximately(label.Alpha, alpha))
                {
                    // The label fades with its point (layer opacity, filter dimming).
                    label.Alpha = alpha;
                    label.Tmp.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
                }
            }
        }

        Label LabelAt(Entry entry, int k)
        {
            while (entry.Labels.Count <= k)
            {
                var go = new GameObject("Label " + entry.Labels.Count);
                go.transform.SetParent(entry.Root.transform, false);
                go.transform.localScale = Vector3.one * m_LabelScale;

                // The text sits on a child, shifted along the label's own x (the viewer's right once
                // LateUpdate turns the label to the camera), so it starts beside the point, not on it.
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(go.transform, false);
                textGo.transform.localPosition = new Vector3(m_Offset / Mathf.Max(m_LabelScale, 0.0001f), 0f, 0f);
                TextMeshPro tmp = textGo.AddComponent<TextMeshPro>();
                tmp.fontSize = m_FontSize;
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                tmp.textWrappingMode = TextWrappingModes.NoWrap;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.rectTransform.pivot = new Vector2(0f, 0.5f);
                tmp.rectTransform.sizeDelta = new Vector2(10f, 2f);
                tmp.color = Color.white;

                Camera cam = Camera.main;
                if (cam != null)
                    go.transform.rotation = cam.transform.rotation;
                entry.Labels.Add(new Label { Go = go, Tmp = tmp });
            }
            Label label = entry.Labels[k];
            if (!label.Go.activeSelf)
                label.Go.SetActive(true);
            return label;
        }

        Transform Container()
        {
            if (m_Container == null)
            {
                // Same local space as LastBuiltPositions and CubeToLocal (the visualizer's own transform).
                m_Container = new GameObject("Layer Labels").transform;
                m_Container.SetParent(transform, false);
            }
            return m_Container;
        }
    }

    // ---- bootstrap: add the component to every visualizer, no scene edit -------------------

    /// <summary>Adds a LayerLabelRenderer to every ScatterplotVisualizer after each scene load (the StudyDashboardBootstrap pattern).</summary>
    static class LayerLabelRendererBootstrap
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
                var host = new GameObject("[Layer Labels Bootstrap]") { hideFlags = HideFlags.HideInHierarchy };
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
                if (visualizer != null && visualizer.GetComponent<LayerLabelRenderer>() == null)
                    visualizer.gameObject.AddComponent<LayerLabelRenderer>();
            }
        }
    }
}
