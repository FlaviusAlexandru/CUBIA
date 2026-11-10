using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace DataViz
{
    public class ScatterplotVisualizer : MonoBehaviour
    {
        [Header("References")]
        public MultiplayerScatterplotManager m_Manager;
        public GameObject m_AxisLabelPrefab; // TMPro in world space
        public CoordinateSystemInteractable m_CoordinateSystemInteractable; // For grab/scaling functionality

        [Header("Visualization Settings")]
        public float m_AxisLength = 1.0f; // Size of the 3D grid
        public Material m_PointMaterial; // Shared material to clone
        public Material m_AxisMaterial;

        [Header("Filtering Settings")]
        [Tooltip("Alpha applied to points that don't match the active filter. They stay in the " +
                 "buffer (so tooltips/positions are unaffected) but fade toward transparent instead " +
                 "of being removed from the plot.")]
        [Range(0f, 1f)]
        public float m_FilteredOutAlpha = 0.08f;

        [Header("Time Animation Settings")]
        [Tooltip("Alpha of points outside the current time window while a time column is set. They stay " +
                 "drawn as faint context (pilot finding P4) but are not hoverable. A row also dimmed by a " +
                 "value filter takes the lower of the two alphas.")]
        [Range(0f, 1f)]
        public float m_TimeOutsideAlpha = 0.18f;

        [Tooltip("Continuous time columns only: length of the full-opacity trail behind the current time, " +
                 "as a fraction of the time column's range.")]
        [Range(0f, 1f)]
        public float m_TimeTrailFraction = 0.02f;

        [Header("Axis Label Settings")]
        [Tooltip("Font size of the axis name + range labels (was 3).")]
        public float m_AxisLabelFontSize = 7f;
        [Tooltip("Distance from the axis to its name label, in plot units. Must be larger than the tick label offset.")]
        public float m_AxisLabelOffset = 0.11f;
        [Tooltip("Distance from the X/Z axis to its value (tick) labels on the floor, in plot units.")]
        public float m_TickLabelOffset = 0.045f;

        // Labels on the plot floor sit just above y = 0 so they don't z-fight the grid or sink into the table.
        private const float k_FloorLift = 0.003f;
        // TextMeshPro faces -Z and reads along +X. Flat on the floor, reading along +X, facing up:
        private static readonly Quaternion k_FlatAlongX = Quaternion.Euler(90f, 0f, 0f);
        // Flat on the floor beside the Z axis, top of the text toward the plot (+X), so it reads
        // the right way up for someone standing at the plot's front-left:
        private static readonly Quaternion k_FlatAlongZ = Quaternion.Euler(90f, 90f, 0f);
        // Upright beside the Y axis, reading bottom-to-top:
        private static readonly Quaternion k_UprightAlongY = Quaternion.Euler(0f, 0f, 90f);

        // Parent container for points
        private Transform m_PointsContainer;
        private Transform m_AxesContainer;

        private List<GameObject> m_ActivePoints = new();
        private List<GameObject> m_ActiveAxes = new();

        public ScatterplotVFXRenderer m_GPUPoints;
        public GPUPointInteractable m_GPUInteractable;

        public VisualizationGridSettings m_GridSettings;

        private void Start()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance;

            if (m_Manager != null) { m_Manager.OnPlotSettingsChanged += RegeneratePlot; }

            // Create containers
            m_PointsContainer = new GameObject("Points").transform;
            m_PointsContainer.SetParent(transform, false);
            m_PointsContainer.localPosition = new Vector3(-m_AxisLength / 2f, -m_AxisLength / 2f, -m_AxisLength / 2f);

            m_AxesContainer = new GameObject("Axes").transform;
            m_AxesContainer.SetParent(transform, false);
            m_AxesContainer.localPosition = new Vector3(-m_AxisLength / 2f, -m_AxisLength / 2f, -m_AxisLength / 2f);

            // Default material if none assigned
            if (m_PointMaterial == null) { m_PointMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")); }
            if (m_AxisMaterial == null)
            {
                m_AxisMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                m_AxisMaterial.color = Color.white;
            }

            if (m_GPUPoints == null) { m_GPUPoints = GetComponent<ScatterplotVFXRenderer>(); }
            if (m_GPUInteractable == null) { m_GPUInteractable = GetComponent<GPUPointInteractable>(); }
            if (m_GPUInteractable != null) { m_GPUInteractable.m_Visualizer = this; }

            if (m_GPUPoints == null)
            {
                Debug.LogError(
                    "ScatterplotVisualizer: No ScatterplotInstancedRenderer found."
                );
            }

            if (m_GPUInteractable == null)
            {
                Debug.LogWarning(
                    "ScatterplotVisualizer: No GPUPointInteractable found."
                );
            }

            if (m_GridSettings == null)
            {
                m_GridSettings = GetComponent<VisualizationGridSettings>();
            }
            if (m_GridSettings == null)
            {
                Debug.LogWarning("ScatterplotVisualizer: No VisualizationGridSettings found. Using default settings.");
            }

            // Find coordinate system interactable if not assigned
            if (m_CoordinateSystemInteractable == null)
            {
                m_CoordinateSystemInteractable = GetComponent<CoordinateSystemInteractable>();
            }

            RegeneratePlot();
        }

        private void OnEnable()
        {
            Debug.Log($"Visualizer enabled {GetEntityId()}");
        }

        private void OnDestroy()
        {
            Debug.Log($"Visualizer destroyed {GetEntityId()}");
            if (m_Manager != null) { m_Manager.OnPlotSettingsChanged -= RegeneratePlot; }
        }

        public void RegeneratePlot()
        {
            ClearPoints();
            ClearAxes();

            Debug.Log($"[ScatterplotVisualizer] Plot Regeneration Called");

            var frame = new PlotFrame();

            if (m_Manager == null || m_Manager.LoadedDataset == null)
            {
                PublishFrame(frame, null);
                return;
            }

            DatasetColumnar dataset = m_Manager.LoadedDataset;
            int xCol = m_Manager.XColumnIndex;
            int yCol = m_Manager.YColumnIndex;
            int zCol = m_Manager.ZColumnIndex;
            int colorCol = m_Manager.ColorColumnIndex;
            float pointSize = m_Manager.PointSize;

            // 1. Build Grid and Axes Visuals (from the active layer, as before layers; WP2 moves this to the shared bounds)
            BuildAxesAndGrid(dataset, xCol, yCol, zCol);

            DatasetColumn colorColumn = dataset.GetColumn(colorCol);
            if (colorColumn != null)
            {
                Debug.Log($"[Color Debug] Col Name: '{colorColumn.Name}' | Index: {colorCol} | Min: {colorColumn.MinValue} | Max: {colorColumn.MaxValue} | IsNumeric: {colorColumn.IsNumeric}");
            }
            else
            {
                Debug.LogWarning($"[Color Debug] colorColumn is NULL! colorCol index was: {colorCol}");
            }

            // 2. One concatenated buffer for every visible point layer, in stack order. Each layer's
            // per-row work is LayerPointBuilder.Append; positions come from the layer's axis frame
            // (shared bounds, or its own range when Normalised). Rows outside the time window stay
            // in the buffer at m_TimeOutsideAlpha (pilot finding P4); rows rejected by a value filter
            // stay at m_FilteredOutAlpha, so row counts/indices stay stable for GPUPointInteractable.
            LayerStack stack = m_Manager.Stack;
            frame.StackVersion = stack.Version;
            frame.Bounds = m_Manager.SharedBounds;
            frame.Budget = m_Manager.PointBudget;
            frame.ClockActive = m_Manager.GetClockRange(out frame.ClockMin, out frame.ClockMax);
            frame.ClockTime = frame.ClockActive ? PlotClock.TimeAt(m_Manager.TimeScrub, frame.ClockMin, frame.ClockMax) : 0f;

            var settings = new LayerBuildSettings
            {
                Origin = m_PointsContainer.localPosition,
                AxisLength = m_AxisLength,
                FilteredOutAlpha = m_FilteredOutAlpha,
                TimeOutsideAlpha = m_TimeOutsideAlpha,
                TimeTrailFraction = m_TimeTrailFraction,
                GlyphPreviewHidden = m_GlyphPreviewHiddenIntervals,
                GlyphPreviewLayerId = m_Manager.EditLayer.Id,
            };

            var buffers = new PointBuffers();
            bool anyTimeActive = false;
            bool anySizeMapped = false;
            foreach (PlotLayer layer in stack.Layers)
            {
                var layerFrame = new LayerFrame
                {
                    Layer = layer,
                    Dataset = layer.Kind == LayerKind.Computed ? (stack.Find(layer.SourceLayerId) ?? layer).Dataset : layer.Dataset,
                    Axes = LayerBounds.FrameInStack(layer, stack, frame.Bounds),
                };
                frame.Layers.Add(layerFrame);

                if (!layer.Visible || !layer.IsPointLayer || layer.LocalUnavailable || layer.Dataset == null)
                    continue;

                layerFrame.BufferStart = buffers.Count;
                layerFrame.Info = LayerPointBuilder.Append(layer, stack, layerFrame.Axes, frame.Budget - buffers.Count,
                    frame.ClockActive, frame.ClockMin, frame.ClockMax, frame.ClockTime, settings, buffers);
                layerFrame.BufferCount = buffers.Count - layerFrame.BufferStart;

                DatasetColumn layerTime = layer.Dataset.GetColumn(layer.TimeColumnIndex);
                anyTimeActive |= frame.ClockActive && layerTime != null && layerTime.IsNumeric;
                anySizeMapped |= layer.SizeMapped && layer.Dataset.GetColumn(layer.SizeColumnIndex) is DatasetColumn sizeColumn && sizeColumn.IsNumeric;
            }
            frame.TotalPoints = buffers.Count;

            m_GPUPoints.BuildAdvanced(
                buffers.Positions,
                buffers.Colors,
                pointSize,
                null, // glossiness - not yet exposed as a mapped dimension
                null, // metallic - not yet exposed as a mapped dimension
                buffers.Glyphs,
                buffers.Sizes // per point; equals pointSize everywhere in Fixed size mode
            );

            LastBuiltPositions = buffers.Positions;
            LastBuiltPointSizes = anySizeMapped ? buffers.Sizes : null;
            LastBuiltGlyphIndices = buffers.Glyphs;
            LastBuiltRowIndices = buffers.Rows;
            LastBuiltColors = buffers.Colors;
            LastBuiltInTimeWindow = anyTimeActive ? buffers.InTimeWindow : null;

            // Also update the interactable with point data. While a time column is set it only gets
            // the rows inside the time window, so the faded context rows can't be hovered/tooltipped
            // (as before, when they were not drawn at all).
            if (m_GPUInteractable != null)
            {
                if (anyTimeActive)
                {
                    m_HoverPositions.Clear();
                    m_HoverColors.Clear();
                    m_HoverRowIndices.Clear();
                    for (int i = 0; i < buffers.Count; i++)
                    {
                        if (!buffers.InTimeWindow[i])
                            continue;
                        m_HoverPositions.Add(buffers.Positions[i]);
                        m_HoverColors.Add(buffers.Colors[i]);
                        m_HoverRowIndices.Add(buffers.Rows[i]);
                    }
                    m_GPUInteractable.SetPointData(m_HoverPositions, m_HoverColors, pointSize, m_HoverRowIndices);
                }
                else
                {
                    m_GPUInteractable.SetPointData(buffers.Positions, buffers.Colors, pointSize, buffers.Rows);
                }
            }

            PublishFrame(frame, buffers);
        }

        private void PublishFrame(PlotFrame frame, PointBuffers buffers)
        {
            if (buffers == null)
            {
                LastBuiltPositions = null;
                LastBuiltPointSizes = null;
                LastBuiltGlyphIndices = null;
                LastBuiltRowIndices = null;
                LastBuiltColors = null;
                LastBuiltInTimeWindow = null;
            }

            LastFrame = frame;
            try
            {
                OnFrameBuilt?.Invoke(frame);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e); // a broken overlay must not stop the plot
            }
        }

        /// <summary>
        /// What the last RegeneratePlot built: bounds, clock, and per layer its axis frame, buffer
        /// slice and draw info. Never null after Start (an empty frame when nothing is loaded).
        /// </summary>
        public PlotFrame LastFrame { get; private set; } = new PlotFrame();

        /// <summary>Raised at the end of every RegeneratePlot with LastFrame. Overlays (lines, labels, legend) subscribe here.</summary>
        public event System.Action<PlotFrame> OnFrameBuilt;

        /// <summary>Unit-cube coordinates (0..1 per axis) → this visualizer's local space, where the points live.</summary>
        public Vector3 CubeToLocal(Vector3 unit)
        {
            Vector3 origin = m_PointsContainer != null
                ? m_PointsContainer.localPosition
                : new Vector3(-m_AxisLength / 2f, -m_AxisLength / 2f, -m_AxisLength / 2f);
            return origin + unit * m_AxisLength;
        }


        private void BuildAxesAndGrid(DatasetColumnar dataset, int xCol, int yCol, int zCol)
        {
            // Build X, Y, Z Axis cylinders
            CreateAxisLine(Vector3.zero, new Vector3(m_AxisLength, 0, 0), Color.red, "X-Axis");
            CreateAxisLine(Vector3.zero, new Vector3(0, m_AxisLength, 0), Color.green, "Y-Axis");
            CreateAxisLine(Vector3.zero, new Vector3(0, 0, m_AxisLength), Color.blue, "Z-Axis");

            if (m_GridSettings != null && m_GridSettings.ShowGridPlanes)
            {
                CreateGridPlaneXY();
                CreateGridPlaneXZ();
                CreateGridPlaneYZ();
            }
            if (m_GridSettings != null && m_GridSettings.ShowTickMarks)
            {
                CreateTickMarks();
            }
            if (m_GridSettings != null && m_GridSettings.ShowValueLabels)
            {
                CreateTickLabels(
                    dataset,
                    xCol,
                    yCol,
                    zCol);
            }


            // Build Wireframe cube around scatterplot boundaries
            CreateGridLine(new Vector3(m_AxisLength, 0, 0), new Vector3(m_AxisLength, m_AxisLength, 0));
            CreateGridLine(new Vector3(0, m_AxisLength, 0), new Vector3(m_AxisLength, m_AxisLength, 0));
            CreateGridLine(new Vector3(0, 0, m_AxisLength), new Vector3(m_AxisLength, 0, m_AxisLength));
            CreateGridLine(new Vector3(m_AxisLength, 0, 0), new Vector3(m_AxisLength, 0, m_AxisLength));

            CreateGridLine(new Vector3(0, m_AxisLength, 0), new Vector3(0, m_AxisLength, m_AxisLength));
            CreateGridLine(new Vector3(0, 0, m_AxisLength), new Vector3(0, m_AxisLength, m_AxisLength));

            CreateGridLine(new Vector3(m_AxisLength, m_AxisLength, 0), new Vector3(m_AxisLength, m_AxisLength, m_AxisLength));
            CreateGridLine(new Vector3(0, m_AxisLength, m_AxisLength), new Vector3(m_AxisLength, m_AxisLength, m_AxisLength));
            CreateGridLine(new Vector3(m_AxisLength, 0, m_AxisLength), new Vector3(m_AxisLength, m_AxisLength, m_AxisLength));

            // Axis labels: names and ranges, laid along each axis. X and Z lie flat on
            // the plot's floor (just above y = 0) so they never sink into the table;
            // Y stands upright and reads bottom-to-top beside the Y axis.
            if (xCol >= 0 && xCol < dataset.ColumnCount)
                CreateAxisLabel(dataset.Columns[xCol], new Vector3(m_AxisLength / 2f, k_FloorLift, -m_AxisLabelOffset), k_FlatAlongX, "X_Label");

            if (yCol >= 0 && yCol < dataset.ColumnCount)
                CreateAxisLabel(dataset.Columns[yCol], new Vector3(-m_AxisLabelOffset, m_AxisLength / 2f, 0), k_UprightAlongY, "Y_Label");

            if (zCol >= 0 && zCol < dataset.ColumnCount)
                CreateAxisLabel(dataset.Columns[zCol], new Vector3(-m_AxisLabelOffset, k_FloorLift, m_AxisLength / 2f), k_FlatAlongZ, "Z_Label");
        }


        private void CreateGridPlaneXY()
        {
            float spacing =
                m_GridSettings.MinorGridSpacing;

            float major =
                m_GridSettings.MajorGridSpacing;

            for (float x = 0; x <= m_AxisLength; x += spacing)
            {
                bool isMajor = Mathf.Approximately(x % major, 0f);
                CreateGridLine(
                    new Vector3(x, 0, 0),
                    new Vector3(x, m_AxisLength, 0),
                    isMajor
                );
            }

            for (float y = 0; y <= m_AxisLength; y += spacing)
            {
                bool isMajor = Mathf.Approximately(y % major, 0f);
                CreateGridLine(
                    new Vector3(0, y, 0),
                    new Vector3(m_AxisLength, y, 0),
                    isMajor
                );
            }
        }

        private void CreateGridPlaneXZ()
        {
            float spacing =
                m_GridSettings.MinorGridSpacing;
            float major =
                m_GridSettings.MajorGridSpacing;

            for (float x = 0; x <= m_AxisLength; x += spacing)
            {
                bool isMajor = Mathf.Approximately(x % major, 0f);
                CreateGridLine(
                    new Vector3(x, 0, 0),
                    new Vector3(x, 0, m_AxisLength),
                    isMajor
                );
            }

            for (float z = 0; z <= m_AxisLength; z += spacing)
            {
                bool isMajor = Mathf.Approximately(z % major, 0f);
                CreateGridLine(
                    new Vector3(0, 0, z),
                    new Vector3(m_AxisLength, 0, z),
                    isMajor
                );
            }
        }

        private void CreateGridPlaneYZ()
        {
            float spacing =
                m_GridSettings.MinorGridSpacing;
            float major =
                m_GridSettings.MajorGridSpacing;
            for (float y = 0; y <= m_AxisLength; y += spacing)
            {
                bool isMajor = Mathf.Approximately(y % major, 0f);
                CreateGridLine(
                    new Vector3(0, y, 0),
                    new Vector3(0, y, m_AxisLength),
                    isMajor
                );
            }
            for (float z = 0; z <= m_AxisLength; z += spacing)
            {
                bool isMajor = Mathf.Approximately(z % major, 0f);
                CreateGridLine(
                    new Vector3(0, 0, z),
                    new Vector3(0, m_AxisLength, z),
                    isMajor
                );
            }
        }

        private void CreateTickMarks()
        {
            float spacing =
                m_GridSettings.MajorGridSpacing;

            float tickSize = 0.02f;

            // X Axis
            for (
                float x = 0;
                x <= m_AxisLength;
                x += spacing
            )
            {
                CreateGridLine(
                    new Vector3(x, 0, 0),
                    new Vector3(x, -tickSize, 0),
                    true
                );
            }

            // Y Axis
            for (
                float y = 0;
                y <= m_AxisLength;
                y += spacing
            )
            {
                CreateGridLine(
                    new Vector3(0, y, 0),
                    new Vector3(-tickSize, y, 0),
                    true
                );
            }

            // Z Axis
            for (
                float z = 0;
                z <= m_AxisLength;
                z += spacing
            )
            {
                CreateGridLine(
                    new Vector3(0, 0, z),
                    new Vector3(-tickSize, 0, z),
                    true
                );
            }
        }

        private void CreateTickLabel(
            string text,
            Vector3 position,
            Quaternion rotation)
        {
            GameObject labelObj =
                new GameObject("TickLabel");

            labelObj.transform.SetParent(
                m_AxesContainer,
                false
            );

            labelObj.transform.localPosition =
                position;

            labelObj.transform.localRotation =
                rotation;

            labelObj.transform.localScale =
                Vector3.one * 0.01f;

            Canvas canvas =
                labelObj.AddComponent<Canvas>();

            canvas.renderMode =
                RenderMode.WorldSpace;

            GameObject textObj =
                new GameObject("Text");

            textObj.transform.SetParent(
                labelObj.transform,
                false
            );

            textObj.transform.localPosition =
                Vector3.zero;

            TextMeshPro tmp =
                textObj.AddComponent<TextMeshPro>();

            tmp.text = text;
            tmp.fontSize = 14f;
            tmp.alignment =
                TextAlignmentOptions.Center;

            m_ActiveAxes.Add(labelObj);
        }

        private void CreateTickLabels(
            DatasetColumnar dataset,
            int xCol,
            int yCol,
            int zCol)
        {
            float spacing =
                m_GridSettings.MajorGridSpacing;

            // X axis labels
            if (xCol >= 0 && xCol < dataset.ColumnCount)
            {
                DatasetColumn col =
                    dataset.Columns[xCol];

                for (float x = 0; x <= m_AxisLength; x += spacing)
                {
                    float value =
                        col.MinValue +
                        (x / m_AxisLength) *
                        (col.MaxValue - col.MinValue);

                    Debug.Log($"[ScatterplotVisualizer] X Axis: {col.Name} | Min={col.MinValue} | Max={col.MaxValue}");

                    CreateTickLabel(
                        value.ToString("F1"),
                        new Vector3(
                            x,
                            k_FloorLift,
                            -m_TickLabelOffset
                        ),
                        k_FlatAlongX
                    );
                }
            }

            // Y axis labels
            if (yCol >= 0 && yCol < dataset.ColumnCount)
            {
                DatasetColumn col =
                    dataset.Columns[yCol];

                for (float y = 0; y <= m_AxisLength; y += spacing)
                {
                    float value =
                        col.MinValue +
                        (y / m_AxisLength) *
                        (col.MaxValue - col.MinValue);

                    Debug.Log($"[ScatterplotVisualizer] Y Axis: {col.Name} | Min={col.MinValue} | Max={col.MaxValue}");

                    CreateTickLabel(
                        value.ToString("F1"),
                        new Vector3(
                            -0.05f,
                            y,
                            0
                        ),
                        Quaternion.identity
                    );
                }
            }

            // Z axis labels
            if (zCol >= 0 && zCol < dataset.ColumnCount)
            {
                DatasetColumn col =
                    dataset.Columns[zCol];

                for (float z = 0; z <= m_AxisLength; z += spacing)
                {
                    float value =
                        col.MinValue +
                        (z / m_AxisLength) *
                        (col.MaxValue - col.MinValue);
                    Debug.Log($"[ScatterplotVisualizer] Z Axis: {col.Name} | Min={col.MinValue} | Max={col.MaxValue}");

                    CreateTickLabel(
                        value.ToString("F1"),
                        new Vector3(
                            -m_TickLabelOffset,
                            k_FloorLift,
                            z
                        ),
                        k_FlatAlongZ
                    );
                }
            }
        }

        private void CreateAxisLine(Vector3 start, Vector3 end, Color color, string name)
        {
            GameObject lineObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            lineObj.name = name;
            lineObj.transform.SetParent(m_AxesContainer, false);

            // Set scale and rotation to bridge start and end
            Vector3 direction = end - start;
            float distance = direction.magnitude;
            lineObj.transform.localPosition = start + direction / 2f;
            lineObj.transform.localScale = new Vector3(0.01f, distance / 2f, 0.01f);
            lineObj.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);

            // Set color
            Renderer r = lineObj.GetComponent<Renderer>();
            if (r != null)
            {
                Material m = Instantiate(m_AxisMaterial);
                m.color = color;
                r.sharedMaterial = m;
            }

            // Remove collider so it doesn't block rays
            Destroy(lineObj.GetComponent<Collider>());

            m_ActiveAxes.Add(lineObj);
        }

        private void CreateGridLine(Vector3 start, Vector3 end, bool major = false)
        {
            GameObject lineObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            lineObj.name = "GridLine";
            lineObj.transform.SetParent(m_AxesContainer, false);

            Vector3 direction = end - start;
            float distance = direction.magnitude;
            lineObj.transform.localPosition = start + direction / 2f;
            float thickness = major ? 0.006f : 0.002f; // Major lines are thicker
            lineObj.transform.localScale = new Vector3(thickness, distance / 2f, thickness); // Thinner than axes
            lineObj.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);

            Renderer r = lineObj.GetComponent<Renderer>();
            if (r != null)
            {
                Material m = Instantiate(m_AxisMaterial);
                if (m_GridSettings != null) { m.color = major ? m_GridSettings.MajorGridColor : m_GridSettings.MinorGridColor; }
                else { m.color = new Color(1f, 1f, 1f, 0.1f); } // Default minor grid color
                r.sharedMaterial = m;
            }

            Destroy(lineObj.GetComponent<Collider>());

            m_ActiveAxes.Add(lineObj);
        }

        private void CreateAxisLabel(DatasetColumn column, Vector3 position, Quaternion rotation, string name)
        {
            GameObject labelObj = new GameObject(name);
            labelObj.transform.SetParent(m_AxesContainer, false);
            labelObj.transform.localPosition = position;
            labelObj.transform.localRotation = rotation;
            labelObj.transform.localScale = Vector3.one * 0.03f;

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(labelObj.transform, false);

            // --- USE TextMeshPro (3D) INSTEAD OF TextMeshProUGUI ---
            TextMeshPro tmpText = textObj.AddComponent<TextMeshPro>();
            tmpText.fontSize = m_AxisLabelFontSize;
            tmpText.fontStyle = FontStyles.Bold;
            tmpText.alignment = TextAlignmentOptions.Center;
            tmpText.color = Color.white;
            // One line along the axis: no wrapping, and a rect as long as the axis.
            tmpText.textWrappingMode = TextWrappingModes.NoWrap;
            tmpText.rectTransform.sizeDelta = new Vector2(m_AxisLength / 0.03f, 4f);

            if (column.IsNumeric)
            {
                tmpText.text = $"{column.Name}  [{column.MinValue:F1} to {column.MaxValue:F1}]";
            }
            else
            {
                tmpText.text = $"{column.Name}  [{column.UniqueCount} cats]";
            }

            m_ActiveAxes.Add(labelObj);
        }
        /// <summary>
        /// Resolves dataset row i's value on a given column to the same string label
        /// used throughout the UI (categorical: the category's UniqueValues entry;
        /// numeric: float.ToString(), matching ScatterplotUI.PopulateFilterIndexDropdown's
        /// formatting). Shared by filter matching, glyph lookup, and GPUPointInteractable's
        /// tooltip so all three always agree on what a row's "label" is for a given column.
        /// Returns null when the cell has no value (see DatasetColumnar.HasValue), so a
        /// missing cell never matches a picked value or a per-value glyph. (LayerRows.RowLabel.)
        /// </summary>
        public string GetRowLabel(
            DatasetColumnar dataset,
            int rowIndex,
            int columnIndex,
            DatasetColumn column,
            List<string> categoryTable) =>
            LayerRows.RowLabel(dataset, rowIndex, columnIndex, column, categoryTable);

        /// <summary>
        /// Per-point sizes passed to the renderer by the last RegeneratePlot, or null when no drawn
        /// layer was in Mapped size mode. Exposed for inspection/tests only.
        /// </summary>
        public List<float> LastBuiltPointSizes { get; private set; }

        /// <summary>
        /// Position, glyph index, dataset row and colour of each point passed to the renderer by the
        /// last RegeneratePlot, in buffer order (all layers concatenated; LastFrame.FrameOfPoint
        /// tells which layer a buffer index belongs to). Exposed for inspection, tests and overlays.
        /// </summary>
        public IReadOnlyList<Vector3> LastBuiltPositions { get; private set; }
        public List<int> LastBuiltGlyphIndices { get; private set; }
        public List<int> LastBuiltRowIndices { get; private set; }
        public List<Color> LastBuiltColors { get; private set; }

        /// <summary>
        /// Parallel to LastBuiltRowIndices: true when that point is inside the current time window
        /// (drawn at full alpha, hoverable), false when it is faded context. Null when no drawn layer
        /// has an active time column (every point counts as inside). Valid until the next RegeneratePlot.
        /// </summary>
        public List<bool> LastBuiltInTimeWindow { get; private set; }

        /// <summary>True when the point at bufferIndex (LastBuiltRowIndices order) is inside the current time window.</summary>
        public bool IsPointInTimeWindow(int bufferIndex)
        {
            List<bool> mask = LastBuiltInTimeWindow;
            if (mask == null)
                return true;
            return bufferIndex >= 0 && bufferIndex < mask.Count && mask[bufferIndex];
        }

        // Reused every regenerate (it runs every frame during playback): the in-window subset handed
        // to GPUPointInteractable.
        private readonly List<Vector3> m_HoverPositions = new();
        private readonly List<Color> m_HoverColors = new();
        private readonly List<int> m_HoverRowIndices = new();

        /// <summary>
        /// Glyph for dataset row rowIndex on the edit layer's glyph column: per-value assignment,
        /// else the glyph range its numeric value falls in, else glyph 0 (see
        /// MultiplayerScatterplotManager.GetGlyphForRow). Shared with GPUPointInteractable's tooltip.
        /// </summary>
        public int GetRowGlyph(
            DatasetColumnar dataset,
            int rowIndex,
            int glyphCol,
            DatasetColumn glyphColumn,
            List<string> categoryTable)
        {
            bool hasValue = dataset.HasValue(rowIndex, glyphCol);
            string label = GetRowLabel(dataset, rowIndex, glyphCol, glyphColumn, categoryTable);
            float numericValue = (hasValue && glyphColumn.IsNumeric)
                ? dataset.GetNumericValue(rowIndex, glyphCol)
                : float.NaN;
            return m_Manager.GetGlyphForRow(label, hasValue, numericValue, 0);
        }

        /// <summary>GetRowGlyph for a given layer (its glyph assignments and ranges, fallback glyph 0).</summary>
        public int GetRowGlyph(
            PlotLayer layer,
            DatasetColumnar dataset,
            int rowIndex,
            int glyphCol,
            DatasetColumn glyphColumn,
            List<string> categoryTable) =>
            LayerPointBuilder.RowGlyph(layer, dataset, rowIndex, glyphCol, glyphColumn, categoryTable, 0);

        // Glyph-column value intervals (x = low, y = high, both inclusive) hidden by the glyph
        // range editor's overlap preview. Empty = no preview.
        private readonly List<Vector2> m_GlyphPreviewHiddenIntervals = new();

        public IReadOnlyList<Vector2> GlyphPreviewHiddenIntervals => m_GlyphPreviewHiddenIntervals;

        /// <summary>
        /// Preview hook for the glyph range editor: hides (alpha 0) the points whose glyph-column
        /// value lies in any of intervals, without committing anything. Null or empty shows them
        /// again. Rebuilds the plot only when the intervals actually change.
        /// </summary>
        public void SetGlyphPreviewHiddenIntervals(List<Vector2> intervals)
        {
            int count = intervals != null ? intervals.Count : 0;
            bool same = count == m_GlyphPreviewHiddenIntervals.Count;
            for (int i = 0; same && i < count; i++)
                same = intervals[i] == m_GlyphPreviewHiddenIntervals[i];
            if (same)
                return;

            m_GlyphPreviewHiddenIntervals.Clear();
            if (intervals != null)
                m_GlyphPreviewHiddenIntervals.AddRange(intervals);

            if (m_PointsContainer != null) // Start has run
                RegeneratePlot();
        }

        /// <summary>One resolved filter entry, snapshotted once per regenerate.</summary>
        public class ActiveFilter : LayerRows.ResolvedFilter
        {
        }

        /// <summary>Resolves the edit layer's filters (m_Manager.Filters) against the dataset, skipping out-of-range columns.</summary>
        public List<ActiveFilter> BuildActiveFilters(DatasetColumnar dataset)
        {
            if (m_Manager == null || dataset == null || m_Manager.Filters == null)
                return new List<ActiveFilter>();

            return LayerRows.ResolveFilters<ActiveFilter>(m_Manager.Filters, dataset, warnUntyped: true);
        }

        /// <summary>Resolves a given layer's filters against dataset (null = the layer's own).</summary>
        public List<ActiveFilter> BuildActiveFilters(PlotLayer layer, DatasetColumnar dataset) =>
            LayerRows.ResolveFilters<ActiveFilter>(layer?.Filters, dataset ?? layer?.Dataset, warnUntyped: true);

        /// <summary>
        /// OR over all filters: true when the row passes at least one of them, or when there are
        /// no filters at all.
        /// </summary>
        public bool RowPassesFilters(DatasetColumnar dataset, int rowIndex, List<ActiveFilter> filters) =>
            LayerRows.PassesFilters(dataset, rowIndex, filters);

        /// <summary>
        /// Mapped size for one row: baseSize x lerp(SizeMappedMinScale, SizeMappedMaxScale, normalised
        /// value), so the column's largest value draws biggest. Rows with no value keep baseSize.
        /// </summary>
        public float GetMappedPointSize(DatasetColumnar dataset, int rowIndex, int sizeCol, float baseSize) =>
            LayerPointBuilder.MappedPointSize(dataset, rowIndex, sizeCol, baseSize);

        /// <summary>
        /// One filter entry: with ranges, the row's value must lie in at least one of them (a row
        /// with no value never does); otherwise the label rule below.
        /// </summary>
        public bool RowMatchesFilter(DatasetColumnar dataset, int rowIndex, ActiveFilter filter) =>
            LayerRows.MatchesFilter(dataset, rowIndex, filter);

        /// <summary>
        /// Checks whether dataset row i passes a filter. With no value picked (empty filterLabel /
        /// "Any value") a row passes when it has a value in the filter column; with a value picked
        /// it must have exactly that value.
        /// </summary>
        public bool RowMatchesFilter(
            DatasetColumnar dataset,
            int rowIndex,
            int filterCol,
            DatasetColumn filterColumn,
            List<string> filterCategoryTable,
            string filterLabel) =>
            LayerRows.MatchesFilter(dataset, rowIndex, filterCol, filterColumn, filterCategoryTable, filterLabel);


        private void ClearPoints()
        {
            foreach (var p in m_ActivePoints)
            {
                if (p != null) Destroy(p);
            }

            m_ActivePoints.Clear();

            if (m_GPUPoints != null)
            {
                m_GPUPoints.Clear();
            }
        }

        private void ClearAxes()
        {
            foreach (var a in m_ActiveAxes)
            {
                if (a != null) Destroy(a);
            }
            m_ActiveAxes.Clear();
        }
    }
}