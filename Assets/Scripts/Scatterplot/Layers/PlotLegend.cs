using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DataViz
{
    /// <summary>
    /// The plot legend (LFR-18, and the Normalised mark of LFR-10): one world-space text block beside
    /// the cube with one entry per visible layer: name, glyph, colour mapping (column name or
    /// "fixed"), "Normalised", "source missing", "not available here", followed by the layer's
    /// warnings (point budget, skipped rows, label limit, computed-layer status). Hidden when the plot
    /// has one layer that is not normalised and has nothing to warn about, so a one-layer plot looks
    /// as before (R2). Adds itself to each ScatterplotVisualizer at runtime (PlotLegendBootstrap
    /// below). The text is built by the static BuildLines / BuildLine; the TextMeshPro text is only
    /// reassigned when it changed, so playback frames cost a string compare.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlotLegend : MonoBehaviour
    {
        [Tooltip("TextMeshPro font size of the legend (its transform is scaled by Legend Scale, as the axis titles are).")]
        [SerializeField] float m_FontSize = 4f;
        [Tooltip("Local scale of the legend object, in plot units.")]
        [SerializeField] float m_LegendScale = 0.03f;
        [Tooltip("Gap between the cube's right edge and the legend, in plot units.")]
        [SerializeField] float m_Margin = 0.06f;

        const string k_WarningColour = "#FFB347";
        const string k_UnavailableColour = "#8C8C8C";
        const string k_WarningIndent = "    ";

        /// <summary>One legend line. Text is what the line says (without colours).</summary>
        public struct LegendLine
        {
            public string LayerId;
            public string Name;      // layer name (entry lines only)
            public string Details;   // after "Name: " on an entry line; the warning on a warning line
            public bool Warning;

            public string Text => Warning ? Details : Name + ": " + Details;
        }

        // ---- pure text building (tested in LayerOverlaySelfTests) ------------------------------

        /// <summary>
        /// The legend, top to bottom: per visible layer (stack order) its entry line, then one line per
        /// warning line of warningOf(layer) (null or "" = none). Empty when the legend is hidden: no
        /// visible layer, or a single layer that is not normalised, is available and has no warning.
        /// </summary>
        public static List<LegendLine> BuildLines(LayerStack stack, Func<PlotLayer, string> warningOf)
        {
            var lines = new List<LegendLine>();
            if (stack == null || stack.Count == 0)
                return lines;

            bool anyMark = false;
            foreach (PlotLayer layer in stack.Layers)
            {
                if (layer == null || !layer.Visible)
                    continue;

                lines.Add(new LegendLine { LayerId = layer.Id, Name = layer.Name, Details = BuildLine(layer, stack) });
                anyMark |= IsNormalised(layer, stack) || layer.LocalUnavailable || ComputedLayerBuilder.IsSourceMissing(layer, stack);

                string warning = warningOf?.Invoke(layer);
                if (string.IsNullOrEmpty(warning))
                    continue;
                anyMark = true;
                foreach (string w in warning.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(w))
                        lines.Add(new LegendLine { LayerId = layer.Id, Details = w.Trim(), Warning = true });
                }
            }

            if (stack.Count == 1 && !anyMark)
                lines.Clear();
            return lines;
        }

        /// <summary>
        /// One layer's entry after its name: glyph ("Star", "glyph by &lt;column&gt;", or "line" for a
        /// computed layer), "colour &lt;column&gt;" or "colour fixed", then "Normalised", "source
        /// missing" and "not available here" where they apply. Example: "Star, colour MoleOrderLabel, Normalised".
        /// </summary>
        public static string BuildLine(PlotLayer layer, LayerStack stack)
        {
            if (layer == null)
                return "";
            var sb = new StringBuilder();
            DatasetColumnar dataset = layer.Kind == LayerKind.Computed
                ? (stack?.Find(layer.SourceLayerId) ?? layer).Dataset
                : layer.Dataset;

            if (layer.Kind == LayerKind.Computed)
            {
                sb.Append("line");
            }
            else
            {
                string glyphColumn = ColumnName(dataset, layer.GlyphColumnIndex);
                if (glyphColumn != null)
                    sb.Append("glyph by ").Append(glyphColumn);
                else
                    sb.Append(GlyphName(layer.DefaultGlyph));
            }

            // A computed layer is always drawn in its fixed colour.
            string colourColumn = layer.Kind == LayerKind.Computed ? null : ColumnName(dataset, layer.ColorColumnIndex);
            sb.Append(", colour ").Append(colourColumn ?? "fixed");

            if (IsNormalised(layer, stack))
                sb.Append(", Normalised");
            if (ComputedLayerBuilder.IsSourceMissing(layer, stack))
                sb.Append(", ").Append(ComputedLayerBuilder.StatusSourceMissing);
            if (layer.LocalUnavailable)
                sb.Append(", not available here");
            return sb.ToString();
        }

        /// <summary>
        /// A computed layer's status as a legend warning ("&lt;name&gt;: not enough points"), without
        /// "source missing" (the entry line already says it). Null when nothing is left.
        /// </summary>
        public static string ComputedStatusWarning(PlotLayer layer, string status)
        {
            if (layer == null || string.IsNullOrEmpty(status))
                return null;
            var parts = new List<string>();
            foreach (string part in status.Split(','))
            {
                string p = part.Trim();
                if (p.Length > 0 && p != ComputedLayerBuilder.StatusSourceMissing)
                    parts.Add(p);
            }
            return parts.Count == 0 ? null : layer.Name + ": " + string.Join(", ", parts);
        }

        // A computed layer is placed in its source's frame, so it is as normalised as its source
        // (its own copied flag once the source is gone).
        static bool IsNormalised(PlotLayer layer, LayerStack stack) =>
            layer.Kind == LayerKind.Computed ? (stack?.Find(layer.SourceLayerId) ?? layer).Normalised : layer.Normalised;

        static string ColumnName(DatasetColumnar dataset, int columnIndex)
        {
            if (columnIndex < 0)
                return null;
            DatasetColumn column = dataset?.GetColumn(columnIndex);
            if (column == null)
                return dataset == null ? "column " + columnIndex : null;
            return column.Name;
        }

        static string GlyphName(int glyph)
        {
            string[] names = ScatterplotUI.k_GlyphShapeNames;
            return glyph >= 0 && glyph < names.Length ? names[glyph] : "glyph " + glyph;
        }

        // ---- component ---------------------------------------------------------------------------

        ScatterplotVisualizer m_Visualizer;
        LayerLabelRenderer m_Labels;
        LayerLineRenderer m_Lines;
        GameObject m_Root;
        TextMeshPro m_Text;
        PlotFrame m_Frame;   // a new object per build, so it is safe to keep (unlike the LastBuilt* lists)
        bool m_Dirty;
        string m_Shown = "";

        void OnEnable()
        {
            if (m_Visualizer == null)
                m_Visualizer = GetComponent<ScatterplotVisualizer>();
            if (m_Visualizer == null)
            {
                enabled = false;
                return;
            }
            m_Visualizer.OnFrameBuilt += OnFrameBuilt;
            OnFrameBuilt(m_Visualizer.LastFrame);
        }

        void OnDisable()
        {
            if (m_Visualizer != null)
                m_Visualizer.OnFrameBuilt -= OnFrameBuilt;
            if (m_Root != null)
                m_Root.SetActive(false);
            m_Shown = "";
        }

        void OnDestroy()
        {
            if (m_Root != null)
                Destroy(m_Root);
        }

        void OnFrameBuilt(PlotFrame frame)
        {
            m_Frame = frame;
            m_Dirty = true;
        }

        // Built after every OnFrameBuilt handler of this frame has run, so the label limit warnings and
        // the computed-layer statuses of the same build are in.
        void LateUpdate()
        {
            if (!m_Dirty)
                return;
            m_Dirty = false;

            MultiplayerScatterplotManager manager = m_Visualizer.m_Manager != null ? m_Visualizer.m_Manager : MultiplayerScatterplotManager.Instance;
            LayerStack stack = manager != null ? manager.Stack : null;
            if (m_Labels == null)
                m_Labels = GetComponent<LayerLabelRenderer>();
            if (m_Lines == null)
                m_Lines = GetComponent<LayerLineRenderer>();

            List<LegendLine> lines = BuildLines(stack, WarningOf);
            string text = lines.Count == 0 ? "" : RichText(lines, stack);
            if (text == m_Shown)
                return;
            m_Shown = text;

            if (text.Length == 0)
            {
                if (m_Root != null)
                    m_Root.SetActive(false);
                return;
            }

            EnsureText();
            m_Root.transform.localPosition = m_Visualizer.CubeToLocal(new Vector3(1f, 1f, 0f)) + new Vector3(m_Margin, 0f, 0f);
            m_Text.text = text;
            if (!m_Root.activeSelf)
                m_Root.SetActive(true);
        }

        string WarningOf(PlotLayer layer)
        {
            var parts = new List<string>(3);
            LayerFrame layerFrame = m_Frame?.Find(layer.Id);
            if (layerFrame != null && !string.IsNullOrEmpty(layerFrame.Info.Warning))
                parts.Add(layerFrame.Info.Warning);
            string labels = m_Labels != null ? m_Labels.GetWarning(layer) : null;
            if (!string.IsNullOrEmpty(labels))
                parts.Add(labels);
            if (layer.Kind == LayerKind.Computed && m_Lines != null)
            {
                string status = ComputedStatusWarning(layer, m_Lines.GetStatus(layer.Id));
                if (status != null)
                    parts.Add(status);
            }
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        // Layer names in their fixed colour (when the layer is drawn in it), warnings in amber,
        // unavailable layers in grey. User text is wrapped in noparse so a name cannot inject tags.
        static string RichText(List<LegendLine> lines, LayerStack stack)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    sb.Append('\n');
                LegendLine line = lines[i];
                if (line.Warning)
                {
                    sb.Append("<color=").Append(k_WarningColour).Append('>').Append(k_WarningIndent)
                      .Append("<noparse>").Append(line.Details).Append("</noparse></color>");
                    continue;
                }

                PlotLayer layer = stack?.Find(line.LayerId);
                bool unavailable = layer != null && layer.LocalUnavailable;
                bool fixedColour = layer != null && (layer.Kind == LayerKind.Computed || layer.ColorColumnIndex < 0);
                if (unavailable)
                    sb.Append("<color=").Append(k_UnavailableColour).Append('>');
                else if (fixedColour)
                    sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(layer.FixedColour)).Append('>');
                sb.Append("<b><noparse>").Append(line.Name).Append("</noparse></b>");
                if (fixedColour && !unavailable)
                    sb.Append("</color>");
                sb.Append(": <noparse>").Append(line.Details).Append("</noparse>");
                if (unavailable)
                    sb.Append("</color>");
            }
            return sb.ToString();
        }

        void EnsureText()
        {
            if (m_Root != null)
                return;
            // Upright beside the cube's right edge, top aligned with the cube's top, facing the
            // front (-Z) like the axis titles; it turns with the plot when the plot is grabbed.
            m_Root = new GameObject("Plot Legend");
            m_Root.transform.SetParent(transform, false);
            m_Root.transform.localRotation = Quaternion.identity;
            m_Root.transform.localScale = Vector3.one * m_LegendScale;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(m_Root.transform, false);
            m_Text = textGo.AddComponent<TextMeshPro>();
            m_Text.richText = true;
            m_Text.fontSize = m_FontSize;
            m_Text.alignment = TextAlignmentOptions.TopLeft;
            m_Text.textWrappingMode = TextWrappingModes.NoWrap;
            m_Text.overflowMode = TextOverflowModes.Overflow;
            m_Text.rectTransform.pivot = new Vector2(0f, 1f);
            m_Text.rectTransform.sizeDelta = new Vector2(30f, 20f);
            m_Text.color = Color.white;
        }
    }

    // ---- bootstrap: add the component to every visualizer, no scene edit -------------------

    /// <summary>Adds a PlotLegend to every ScatterplotVisualizer after each scene load (the StudyDashboardBootstrap pattern).</summary>
    static class PlotLegendBootstrap
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
                var host = new GameObject("[Plot Legend Bootstrap]") { hideFlags = HideFlags.HideInHierarchy };
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
                if (visualizer != null && visualizer.GetComponent<PlotLegend>() == null)
                    visualizer.gameObject.AddComponent<PlotLegend>();
            }
        }
    }
}
