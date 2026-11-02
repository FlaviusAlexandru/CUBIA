using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz
{
    /// <summary>
    /// Glyph range editor on the summoned menu's Glyph Controls island: assigns glyphs to value
    /// ranges of a numeric or DateTime glyph column (categorical columns keep the per-value
    /// picker only).
    ///
    /// Add glyph adds a row (shape picker + two-handle range bar over the column's min..max);
    /// each shape can be used by one row only, so Add glyph greys out at
    /// MultiplayerScatterplotManager.MaxGlyphRanges rows. All edits are a draft kept here: the
    /// plot only changes on Confirm (RequestSetGlyphRangesRpc), and Cancel goes back to the
    /// committed ranges. While two draft ranges overlap, both bars turn red, a warning shows,
    /// Confirm is disabled and the points inside the overlap are hidden through
    /// ScatterplotVisualizer.SetGlyphPreviewHiddenIntervals. Ranges may share one edge value.
    /// With Prevent overlap on, a handle stops at the edge of the neighbouring range.
    /// The draft resets when the glyph column changes or a dataset loads.
    /// </summary>
    public class GlyphRangeEditor : MonoBehaviour
    {
        [Header("References")]
        public MultiplayerScatterplotManager m_Manager;
        [Tooltip("Plot whose overlap preview this editor drives. Leave empty to use the scene's visualizer.")]
        public ScatterplotVisualizer m_Visualizer;

        [Header("Controls")]
        public Toggle m_PreventOverlapToggle;
        public Button m_AddGlyphButton;
        public Button m_ConfirmButton;
        public Button m_CancelButton;
        [Tooltip("Rows are created here from the row template, one per range, top to bottom.")]
        public RectTransform m_RowsContainer;
        public GlyphRangeRow m_RowTemplate;
        public TextMeshProUGUI m_OverlapWarningText;
        [Tooltip("Shown instead of the range controls when the glyph column is not numeric or DateTime.")]
        public TextMeshProUGUI m_HintText;
        [Tooltip("Hidden while the glyph column is not numeric or DateTime.")]
        public GameObject[] m_RangeOnlyObjects;

        [Header("Layout")]
        public float m_RowSpacing = 46f;
        [Range(0f, 1f)]
        public float m_DisabledButtonAlpha = 0.35f;

        [Header("Colours")]
        public Color m_TrackColor = new Color(0.18f, 0.18f, 0.18f, 1f);
        public Color m_FillColor = new Color(0.373f, 0.839f, 0.784f, 1f);
        public Color m_OverlapTrackColor = new Color(0.45f, 0.08f, 0.08f, 1f);
        public Color m_OverlapFillColor = new Color(0.95f, 0.25f, 0.25f, 1f);

        public const string k_OverlapWarning = "Ranges overlap \u2014 adjust them to continue";

        /// <summary>One draft range and the row that edits it.</summary>
        private class DraftRange
        {
            public int GlyphIndex;
            public GlyphRangeRow Row;
            public bool Overlapping;
        }

        private readonly List<DraftRange> m_Draft = new();
        private List<MultiplayerScatterplotManager.GlyphRange> m_CommittedSnapshot = new();
        private bool m_DraftDirty;
        private bool m_PreviewDirty;
        private bool m_Started;

        // Glyph column the draft belongs to.
        private DatasetColumnar m_Dataset;
        private int m_Column = int.MinValue;
        private bool m_ColumnIsRangeable;
        private float m_Min;
        private float m_Max;

        // Value label formatting.
        private int m_Decimals;
        private bool m_IsDateTime;
        private bool m_DateIsAbsolute;   // CSVImporter: absolute Unix seconds (UTC)
        private bool m_HasDateBase;      // .cdataset: seconds after the column's earliest timestamp
        private DateTime m_DateBase;
        private string m_DateFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>Number of draft rows. Exposed for inspection/tests.</summary>
        public int DraftCount => m_Draft.Count;
        public bool DraftHasOverlap { get; private set; }
        public bool DraftDirty => m_DraftDirty;
        public GlyphRangeRow GetDraftRow(int index) => m_Draft[index].Row;
        public int GetDraftGlyph(int index) => m_Draft[index].GlyphIndex;

        private void Start()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance;
            if (m_Visualizer == null)
                m_Visualizer = FindAnyObjectByType<ScatterplotVisualizer>();

            if (m_RowTemplate != null)
                m_RowTemplate.gameObject.SetActive(false);

            if (m_AddGlyphButton != null)
                m_AddGlyphButton.onClick.AddListener(OnAddGlyphClicked);
            if (m_ConfirmButton != null)
                m_ConfirmButton.onClick.AddListener(OnConfirmClicked);
            if (m_CancelButton != null)
                m_CancelButton.onClick.AddListener(OnCancelClicked);
            if (m_PreventOverlapToggle != null)
                m_PreventOverlapToggle.onValueChanged.AddListener(_ => RefreshAll());

            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged += OnManagerChanged;
                m_Manager.OnDatasetLoaded += OnManagerChanged;
            }

            m_Started = true;
            ResetDraftFromCommitted();
        }

        private void OnDestroy()
        {
            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged -= OnManagerChanged;
                m_Manager.OnDatasetLoaded -= OnManagerChanged;
            }
        }

        private void OnEnable()
        {
            if (m_Started)
                m_PreviewDirty = true; // re-apply the overlap preview of a kept draft
        }

        private void OnDisable()
        {
            // The menu was closed: don't leave points hidden behind a draft nobody can see.
            if (m_Visualizer != null)
                m_Visualizer.SetGlyphPreviewHiddenIntervals(null);
        }

        private void LateUpdate()
        {
            if (m_PreviewDirty)
            {
                m_PreviewDirty = false;
                ApplyPreview();
            }
        }

        public bool PreventOverlap => m_PreventOverlapToggle != null && m_PreventOverlapToggle.isOn;

        #region Manager sync

        private void OnManagerChanged()
        {
            if (m_Manager == null)
                return;

            bool contextChanged = m_Manager.LoadedDataset != m_Dataset || m_Manager.GlyphColumnIndex != m_Column;
            bool committedChanged = !SameRanges(m_Manager.GlyphRanges, m_CommittedSnapshot);

            if (contextChanged || (committedChanged && !m_DraftDirty))
                ResetDraftFromCommitted();
            else if (committedChanged)
                m_CommittedSnapshot = CopyRanges(m_Manager.GlyphRanges); // keep the user's draft
        }

        /// <summary>Throws away the draft and shows the manager's committed ranges again.</summary>
        private void ResetDraftFromCommitted()
        {
            foreach (DraftRange range in m_Draft)
            {
                if (range.Row != null)
                    Destroy(range.Row.gameObject);
            }
            m_Draft.Clear();

            m_Dataset = m_Manager != null ? m_Manager.LoadedDataset : null;
            m_Column = m_Manager != null ? m_Manager.GlyphColumnIndex : -1;

            DatasetColumn column = (m_Dataset != null && m_Column >= 0 && m_Column < m_Dataset.ColumnCount)
                ? m_Dataset.Columns[m_Column]
                : null;
            m_ColumnIsRangeable = column != null && column.IsNumeric;

            if (m_ColumnIsRangeable)
            {
                m_Min = column.MinValue;
                m_Max = Mathf.Max(column.MinValue, column.MaxValue);
                ResolveValueFormat(column);

                foreach (MultiplayerScatterplotManager.GlyphRange range in m_Manager.GlyphRanges)
                    AddDraftRow(range.glyphIndex, range.min, range.max);
            }

            m_CommittedSnapshot = CopyRanges(m_Manager != null ? m_Manager.GlyphRanges : null);
            m_DraftDirty = false;
            RefreshAll();
        }

        #endregion

        #region Buttons and rows

        private void OnAddGlyphClicked()
        {
            if (!m_ColumnIsRangeable || m_Draft.Count >= MultiplayerScatterplotManager.MaxGlyphRanges)
                return;

            int glyph = FirstUnusedGlyph(null);
            if (glyph < 0)
                return;

            float low = m_Min;
            float high = m_Max;
            // With Prevent overlap on, start the new range in the widest free gap instead of on
            // top of the others (full span when nothing is free).
            if (PreventOverlap && m_Draft.Count > 0 && TryFindWidestGap(out float gapLow, out float gapHigh))
            {
                low = gapLow;
                high = gapHigh;
            }

            AddDraftRow(glyph, low, high);
            m_DraftDirty = true;
            RefreshAll();
        }

        private void OnConfirmClicked()
        {
            if (m_Manager == null || !m_ColumnIsRangeable || DraftHasOverlap)
                return;

            List<MultiplayerScatterplotManager.GlyphRange> ranges = BuildDraftRanges();

            // The manager raises OnPlotSettingsChanged; with the draft no longer dirty that
            // rebuilds the rows from the committed (sorted) ranges.
            m_DraftDirty = false;
            if (!m_Manager.RequestSetGlyphRangesRpc(ranges))
            {
                m_DraftDirty = true;
                RefreshAll();
            }
        }

        private void OnCancelClicked()
        {
            ResetDraftFromCommitted();
        }

        private void AddDraftRow(int glyphIndex, float low, float high)
        {
            if (m_RowTemplate == null || m_RowsContainer == null)
                return;

            GlyphRangeRow row = Instantiate(m_RowTemplate, m_RowsContainer, false);
            row.name = $"GlyphRangeRow{m_Draft.Count + 1}";
            row.gameObject.SetActive(true);
            row.SetBounds(m_Min, m_Max);
            row.SetValues(Mathf.Clamp(low, m_Min, m_Max), Mathf.Clamp(high, m_Min, m_Max));

            row.ShapeChanged += OnRowShapeChanged;
            row.LowChanged += OnRowLowChanged;
            row.HighChanged += OnRowHighChanged;
            row.RemoveClicked += OnRowRemoveClicked;

            m_Draft.Add(new DraftRange { GlyphIndex = glyphIndex, Row = row });
        }

        private DraftRange FindDraft(GlyphRangeRow row) => m_Draft.Find(d => d.Row == row);

        private void OnRowShapeChanged(GlyphRangeRow row, int glyphIndex)
        {
            DraftRange draft = FindDraft(row);
            if (draft == null || draft.GlyphIndex == glyphIndex)
                return;

            // The picker only offers unused shapes, but never let two rows share one.
            if (m_Draft.Exists(d => d != draft && d.GlyphIndex == glyphIndex))
            {
                RefreshAll();
                return;
            }

            draft.GlyphIndex = glyphIndex;
            m_DraftDirty = true;
            RefreshAll();
        }

        private void OnRowLowChanged(GlyphRangeRow row, float value)
        {
            DraftRange draft = FindDraft(row);
            if (draft == null)
                return;

            if (PreventOverlap)
            {
                float bound = LowerNeighbourEdge(draft);
                if (value < bound)
                    row.SetValues(bound, row.High);
            }

            m_DraftDirty = true;
            RefreshAll();
        }

        private void OnRowHighChanged(GlyphRangeRow row, float value)
        {
            DraftRange draft = FindDraft(row);
            if (draft == null)
                return;

            if (PreventOverlap)
            {
                float bound = UpperNeighbourEdge(draft);
                if (value > bound)
                    row.SetValues(row.Low, bound);
            }

            m_DraftDirty = true;
            RefreshAll();
        }

        private void OnRowRemoveClicked(GlyphRangeRow row)
        {
            DraftRange draft = FindDraft(row);
            if (draft == null)
                return;

            m_Draft.Remove(draft);
            Destroy(row.gameObject);
            m_DraftDirty = true;
            RefreshAll();
        }

        #endregion

        #region Rules

        // Prevent overlap: the low handle stops at the high edge of the nearest range below this
        // one. Ranges that already overlap this one are ignored, so an overlap made with Prevent
        // overlap off can still be dragged apart.
        private float LowerNeighbourEdge(DraftRange draft)
        {
            float bound = m_Min;
            float currentLow = PreviousLow(draft);
            foreach (DraftRange other in m_Draft)
            {
                if (other == draft)
                    continue;
                float otherHigh = other.Row.High;
                if (otherHigh <= currentLow && otherHigh > bound)
                    bound = otherHigh;
            }
            return bound;
        }

        private float UpperNeighbourEdge(DraftRange draft)
        {
            float bound = m_Max;
            float currentHigh = PreviousHigh(draft);
            foreach (DraftRange other in m_Draft)
            {
                if (other == draft)
                    continue;
                float otherLow = other.Row.Low;
                if (otherLow >= currentHigh && otherLow < bound)
                    bound = otherLow;
            }
            return bound;
        }

        // Handle positions before the drag event that is being processed (the slider already
        // holds the new value), remembered by RefreshAll.
        private readonly Dictionary<GlyphRangeRow, Vector2> m_LastValues = new();
        private float PreviousLow(DraftRange draft) =>
            m_LastValues.TryGetValue(draft.Row, out Vector2 v) ? v.x : draft.Row.Low;
        private float PreviousHigh(DraftRange draft) =>
            m_LastValues.TryGetValue(draft.Row, out Vector2 v) ? v.y : draft.Row.High;

        private int FirstUnusedGlyph(DraftRange except)
        {
            for (int glyph = 1; glyph < ScatterplotUI.k_GlyphShapeNames.Length; glyph++)
            {
                if (!m_Draft.Exists(d => d != except && d.GlyphIndex == glyph))
                    return glyph;
            }
            return -1;
        }

        private bool TryFindWidestGap(out float gapLow, out float gapHigh)
        {
            List<Vector2> taken = new List<Vector2>();
            foreach (DraftRange d in m_Draft)
                taken.Add(new Vector2(d.Row.Low, d.Row.High));
            taken.Sort((a, b) => a.x.CompareTo(b.x));

            gapLow = gapHigh = m_Min;
            float best = 0f;
            float cursor = m_Min;
            foreach (Vector2 interval in taken)
            {
                if (interval.x - cursor > best)
                {
                    best = interval.x - cursor;
                    gapLow = cursor;
                    gapHigh = interval.x;
                }
                cursor = Mathf.Max(cursor, interval.y);
            }
            if (m_Max - cursor > best)
            {
                best = m_Max - cursor;
                gapLow = cursor;
                gapHigh = m_Max;
            }
            return best > 0f;
        }

        private List<MultiplayerScatterplotManager.GlyphRange> BuildDraftRanges()
        {
            List<MultiplayerScatterplotManager.GlyphRange> ranges = new List<MultiplayerScatterplotManager.GlyphRange>();
            foreach (DraftRange d in m_Draft)
            {
                ranges.Add(new MultiplayerScatterplotManager.GlyphRange
                {
                    glyphIndex = d.GlyphIndex,
                    min = d.Row.Low,
                    max = d.Row.High
                });
            }
            return ranges;
        }

        private static List<MultiplayerScatterplotManager.GlyphRange> CopyRanges(List<MultiplayerScatterplotManager.GlyphRange> source)
        {
            List<MultiplayerScatterplotManager.GlyphRange> copy = new List<MultiplayerScatterplotManager.GlyphRange>();
            if (source == null)
                return copy;
            foreach (MultiplayerScatterplotManager.GlyphRange r in source)
                copy.Add(new MultiplayerScatterplotManager.GlyphRange { glyphIndex = r.glyphIndex, min = r.min, max = r.max });
            return copy;
        }

        private static bool SameRanges(List<MultiplayerScatterplotManager.GlyphRange> a, List<MultiplayerScatterplotManager.GlyphRange> b)
        {
            int countA = a != null ? a.Count : 0;
            int countB = b != null ? b.Count : 0;
            if (countA != countB)
                return false;
            for (int i = 0; i < countA; i++)
            {
                if (a[i].glyphIndex != b[i].glyphIndex || a[i].min != b[i].min || a[i].max != b[i].max)
                    return false;
            }
            return true;
        }

        #endregion

        #region Refresh

        /// <summary>Re-applies every rule to the rows, buttons, warning and preview.</summary>
        private void RefreshAll()
        {
            bool showRanges = m_ColumnIsRangeable;
            if (m_RangeOnlyObjects != null)
            {
                foreach (GameObject go in m_RangeOnlyObjects)
                {
                    if (go != null)
                        go.SetActive(showRanges);
                }
            }
            if (m_HintText != null)
                m_HintText.gameObject.SetActive(!showRanges);

            // Overlaps (ranges that only share an edge value are fine).
            foreach (DraftRange d in m_Draft)
                d.Overlapping = false;

            List<Vector2> hidden = new List<Vector2>();
            for (int i = 0; i < m_Draft.Count; i++)
            {
                for (int j = i + 1; j < m_Draft.Count; j++)
                {
                    MultiplayerScatterplotManager.GlyphRange a = ToRange(m_Draft[i]);
                    MultiplayerScatterplotManager.GlyphRange b = ToRange(m_Draft[j]);
                    if (!MultiplayerScatterplotManager.GlyphRangesOverlap(a, b))
                        continue;

                    m_Draft[i].Overlapping = true;
                    m_Draft[j].Overlapping = true;
                    hidden.Add(new Vector2(Mathf.Max(a.min, b.min), Mathf.Min(a.max, b.max)));
                }
            }
            DraftHasOverlap = hidden.Count > 0;
            m_PreviewHidden = hidden;
            m_PreviewDirty = true;

            m_LastValues.Clear();
            for (int i = 0; i < m_Draft.Count; i++)
            {
                DraftRange d = m_Draft[i];
                GlyphRangeRow row = d.Row;

                RectTransform rect = row.transform as RectTransform;
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -i * m_RowSpacing);

                List<int> options = new List<int>();
                for (int glyph = 1; glyph < ScatterplotUI.k_GlyphShapeNames.Length; glyph++)
                {
                    if (glyph == d.GlyphIndex || !m_Draft.Exists(o => o != d && o.GlyphIndex == glyph))
                        options.Add(glyph);
                }
                row.SetShapeOptions(options, d.GlyphIndex);

                row.SetColors(d.Overlapping ? m_OverlapTrackColor : m_TrackColor,
                              d.Overlapping ? m_OverlapFillColor : m_FillColor);
                row.SetValueTexts(FormatValue(row.Low), FormatValue(row.High));
                m_LastValues[row] = new Vector2(row.Low, row.High);
            }

            if (m_OverlapWarningText != null)
            {
                m_OverlapWarningText.text = k_OverlapWarning;
                m_OverlapWarningText.gameObject.SetActive(showRanges && DraftHasOverlap);
            }

            SetButtonEnabled(m_AddGlyphButton,
                showRanges && m_Draft.Count < MultiplayerScatterplotManager.MaxGlyphRanges && FirstUnusedGlyph(null) >= 0);
            SetButtonEnabled(m_ConfirmButton, showRanges && m_DraftDirty && !DraftHasOverlap);
            SetButtonEnabled(m_CancelButton, showRanges && m_DraftDirty);
        }

        private static MultiplayerScatterplotManager.GlyphRange ToRange(DraftRange d) =>
            new MultiplayerScatterplotManager.GlyphRange { glyphIndex = d.GlyphIndex, min = d.Row.Low, max = d.Row.High };

        // Greyed out = not interactable and faded.
        private void SetButtonEnabled(Button button, bool enabled)
        {
            if (button == null)
                return;

            button.interactable = enabled;

            Transform root = button.transform.parent != null ? button.transform.parent : button.transform;
            CanvasGroup group = root.GetComponent<CanvasGroup>();
            if (group == null)
                group = root.gameObject.AddComponent<CanvasGroup>();
            group.alpha = enabled ? 1f : m_DisabledButtonAlpha;
        }

        private List<Vector2> m_PreviewHidden = new();

        private void ApplyPreview()
        {
            if (m_Visualizer == null)
                return;
            m_Visualizer.SetGlyphPreviewHiddenIntervals(isActiveAndEnabled && m_ColumnIsRangeable ? m_PreviewHidden : null);
        }

        #endregion

        #region Value labels

        private void ResolveValueFormat(DatasetColumn column)
        {
            float span = m_Max - m_Min;
            m_Decimals = span <= 0f ? 2 : Mathf.Clamp(Mathf.CeilToInt(-Mathf.Log10(span / 100f)), 0, 4);

            m_IsDateTime = column.Type == DataValueType.DateTime;
            m_DateIsAbsolute = false;
            m_HasDateBase = false;
            if (!m_IsDateTime)
                return;

            m_DateFormat = span >= 2f * 86400f ? "yyyy-MM-dd"
                         : span >= 3600f ? "yyyy-MM-dd HH:mm"
                         : "yyyy-MM-dd HH:mm:ss";

            // CSVImporter stores absolute Unix seconds; preprocess_csv_columnar.py (.cdataset) stores
            // seconds after the column's earliest timestamp, which is not in the file, so it is read
            // back from the source CSV.
            if (column.MinValue > 1e8f)
                m_DateIsAbsolute = true;
            else
                m_HasDateBase = TryReadEarliestTimestamp(column.Name, m_Column, out m_DateBase);
        }

        public string FormatValue(float value)
        {
            if (!m_IsDateTime)
                return value.ToString("F" + m_Decimals);

            DateTime time;
            if (m_DateIsAbsolute)
                time = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value * 1000.0)).LocalDateTime;
            else if (m_HasDateBase)
                time = m_DateBase.AddSeconds(value);
            else
                return "+" + TimeSpan.FromSeconds(Math.Round(value)).ToString(); // elapsed, base unknown

            return time.ToString(m_DateFormat, CultureInfo.InvariantCulture);
        }

        private bool TryReadEarliestTimestamp(string columnName, int columnIndex, out DateTime earliest)
        {
            earliest = default;
            if (m_Manager == null || string.IsNullOrEmpty(m_Manager.CurrentDatasetName))
                return false;

            string csvPath = Path.Combine(Application.streamingAssetsPath, "DataCSV",
                Path.GetFileNameWithoutExtension(m_Manager.CurrentDatasetName) + ".csv");
            try
            {
                if (!File.Exists(csvPath))
                    return false;

                using StreamReader reader = new StreamReader(csvPath);
                string header = reader.ReadLine();
                if (header == null)
                    return false;

                char delimiter = DetectDelimiter(header);
                List<string> headers = SplitCsvLine(header, delimiter);
                int csvColumn = headers.FindIndex(h => string.Equals(h.Trim().Trim('"'), columnName, StringComparison.OrdinalIgnoreCase));
                if (csvColumn < 0 && m_Dataset != null && headers.Count == m_Dataset.ColumnCount)
                    csvColumn = columnIndex;
                if (csvColumn < 0)
                    return false;

                bool found = false;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    List<string> cells = SplitCsvLine(line, delimiter);
                    if (csvColumn >= cells.Count)
                        continue;
                    if (DateTime.TryParse(cells[csvColumn].Trim().Trim('"'), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)
                        && (!found || t < earliest))
                    {
                        earliest = t;
                        found = true;
                    }
                }
                return found;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GlyphRangeEditor] Could not read timestamps from '{csvPath}': {e.Message}");
                return false;
            }
        }

        private static char DetectDelimiter(string header)
        {
            char best = ',';
            int bestCount = 0;
            foreach (char c in new[] { ',', ';', '\t', '|' })
            {
                int count = header.Split(c).Length - 1;
                if (count > bestCount)
                {
                    bestCount = count;
                    best = c;
                }
            }
            return best;
        }

        private static List<string> SplitCsvLine(string line, char delimiter)
        {
            List<string> cells = new List<string>();
            bool inQuotes = false;
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                    inQuotes = !inQuotes;
                else if (c == delimiter && !inQuotes)
                {
                    cells.Add(line.Substring(start, i - start));
                    start = i + 1;
                }
            }
            cells.Add(line.Substring(start));
            return cells;
        }

        #endregion
    }
}
