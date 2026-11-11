using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DataViz.Networking;
using DataViz.Wizard;
using UnityEngine;
using UnityEngine.UIElements;

namespace DataViz.Study
{
    /// <summary>Severity of a dashboard toast: sets its colour and how long it stays.</summary>
    public enum ToastKind
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// Desktop-only facilitator dashboard (WP2): a second view on MultiplayerScatterplotManager next
    /// to CUBIA's VR menu. It calls the same Request…Rpc methods as ScatterplotUI (same "None" = -1
    /// offsets, same filter/glyph value lists) and refreshes from OnDatasetLoaded /
    /// OnPlotSettingsChanged, so both views stay in sync. Every change goes through
    /// IStudySessionLogger.RunAsFacilitator so the logger can tell it apart from participant changes.
    /// Spawned by StudyDashboardBootstrap; never touches the VR menu or XR input.
    /// </summary>
    public sealed class DashboardController : MonoBehaviour
    {
        const string k_None = "None";
        const string k_RequestHint = "req-hint"; // optional hint label (not part of DashboardIds)
        const int k_MaxUndo = 50;
        const int k_RecentRows = 10;
        const float k_SliderCommitDelay = 0.4f;
        const float k_FlashSeconds = 4f;
        const string k_NoParticipants = "No participants yet";
        const int k_MaxVisibleToasts = 4;
        const int k_MaxQueuedToasts = 20; // older ones beyond this are dropped
        const int k_MaxToastChars = 400;   // full text stays in the Console
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        /// <summary>Set by StudyDashboardBootstrap: the runtime PanelSettings this dashboard owns.</summary>
        internal PanelSettings OwnedPanelSettings;

        UIDocument m_Document;
        IStudySessionLogger m_Logger;
        MultiplayerScatterplotManager m_Manager;
        bool m_Initialised;
        bool m_IsUpdatingUI;
        int m_FacilitatorDepth; // > 0 while one of our own manager calls runs

        // Frame
        VisualElement m_Root;
        Button m_MinimiseButton;
        VisualElement m_ToastArea;
        // Session
        DropdownField m_ParticipantField;
        TextField m_LogFolderField;
        DropdownField m_PartField;
        Button m_SessionButton, m_LogFolderApply, m_ImportButton;
        Label m_SessionTime, m_SessionStatus;
        // Prepared views (index i = PreparedViews.All[i])
        readonly Button[] m_ViewButtons = new Button[3];
        Label m_ViewsStatus;
        // Dataset
        DropdownField m_DatasetDropdown;
        Button m_DatasetRescan, m_DatasetLoad;
        // Mappings
        DropdownField m_X, m_Y, m_Z, m_Color, m_Size, m_Time, m_GlyphColumn;
        Slider m_PointSize;
        // Glyphs
        DropdownField m_GlyphLabel, m_GlyphShape;
        // Filters
        DropdownField m_FilterColumn, m_FilterValue;
        Button m_FilterClear;
        // Range sliders (numeric / DateTime columns), like the VR menu's GlyphRangeEditor
        DashboardRangeEditor m_GlyphRanges, m_FilterRanges;
        // Time
        Slider m_TimeScrub;
        Button m_PlayPause;
        DropdownField m_PlaybackRate;
        // View
        Toggle m_Tooltips;
        Button m_ResetPosition, m_Undo, m_Redo;
        // Layers (LFR-26) and the layered prepared views (LFR-27), both built in code
        DashboardLayersSection m_Layers;
        readonly List<Button> m_LayeredViewButtons = new();
        ScatterplotVisualizer m_Visualizer;
        readonly Dictionary<string, string> m_ToastedWarnings = new(); // layer id → warning last shown as a toast
        // Request log
        TextField m_RequestText;
        RadioButtonGroup m_TagGroup;
        RadioButton m_TagPossible, m_TagImpossible, m_TagPartly;
        Button m_LogButton, m_MarkerButton;
        VisualElement m_RecentList;
        Label m_RequestHint;
        // Status
        Label m_StatusLine;

        readonly List<string> m_MissingElements = new();
        readonly List<string> m_Columns = new();
        List<string> m_Datasets = new();
        readonly DashboardUndoHistory m_History = new DashboardUndoHistory(k_MaxUndo);
        readonly List<(string text, string cssClass)> m_Recent = new(); // newest first
        List<string> m_Participants = new(); // real labels only (no placeholder), sorted
        bool m_ImportShownRunning;

        /// <summary>One toast; Remaining counts down only while it is among the visible ones.</summary>
        sealed class Toast
        {
            public VisualElement Element;
            public float Remaining;
        }

        readonly List<Toast> m_Toasts = new(); // newest first

        // Value lists per column index (filter values / glyph labels), cleared on dataset load:
        // building them scans every row, and OnPlotSettingsChanged fires every frame during playback.
        readonly Dictionary<int, List<string>> m_ValueCache = new();
        int m_FilterValuesColumn = int.MinValue;
        int m_GlyphLabelsColumn = int.MinValue;

        sealed class SliderDrag
        {
            public bool Active;
            public bool CommitRequested;
            public float StartValue;
            public float LastChangeTime;
        }

        readonly SliderDrag m_PointDrag = new();
        readonly SliderDrag m_ScrubDrag = new();

        string m_SessionMessage;      // last session error / note (null = show file name)
        string m_LastChange;          // "participant SetColor"
        PlotSnapshot m_LastSnapshot;  // baseline for naming participant changes while not logging
        string m_Flash;
        float m_FlashUntil;
        float m_NextTick;

        bool IsLogging => m_Logger != null && m_Logger.IsLogging;

        // ================================================================== setup

        /// <summary>Wires the dashboard to its UI, logger and the plot. Called once by the bootstrap.</summary>
        public void Initialise(UIDocument document, IStudySessionLogger logger, MultiplayerScatterplotManager manager)
        {
            if (m_Initialised) return;
            m_Document = document;
            m_Logger = logger;
            m_Manager = manager;

            VisualElement rootVe = document != null ? document.rootVisualElement : null;
            if (rootVe == null)
            {
                Debug.LogError("[DashboardController] UIDocument has no root visual element; dashboard disabled.");
                return;
            }
            // The document's root covers the whole screen; only the panel itself may catch the mouse.
            rootVe.pickingMode = PickingMode.Ignore;

            QueryElements(rootVe);
            ConfigureStaticControls();
            RegisterCallbacks();

            if (m_Manager != null)
            {
                m_Manager.OnDatasetLoaded += OnDatasetLoaded;
                m_Manager.OnPlotSettingsChanged += OnPlotSettingsChanged;
                m_Manager.OnLayersChanged += OnLayersChanged;
                m_Manager.OnPlotMessage += OnPlotMessage;
            }
            HookVisualizer();
            if (m_Logger != null)
            {
                m_Logger.RowWritten += OnRowWritten;
                m_Logger.SessionStateChanged += RefreshSession;
                if (m_LogFolderField != null) m_LogFolderField.SetValueWithoutNotify(m_Logger.LogFolder ?? "");
                if (m_PartField != null && m_Logger.Part >= 1 && m_Logger.Part <= 2)
                    m_PartField.SetValueWithoutNotify(m_Logger.Part.ToString(k_Inv));
            }

            WamIaAutoImporter.Completed += OnImportCompleted;

            m_Initialised = true;
            RescanDatasets();
            // Meta files give the list at once; the converter's own list follows on Completed.
            RefreshParticipants(WamIaAutoImporter.Last?.participants);
            if (m_Logger != null && !string.IsNullOrEmpty(m_Logger.ParticipantId))
                SelectParticipant(PreparedViews.NormaliseParticipant(m_Logger.ParticipantId));
            OnDatasetLoaded();
            RefreshSession();
            RefreshViewsStatus();
            RebuildRecentList();
            UpdateUndoButton();
            UpdateImportButton();
        }

        void QueryElements(VisualElement r)
        {
            m_Root = Find<VisualElement>(r, DashboardIds.Root);
            Find<ScrollView>(r, DashboardIds.Body);
            m_MinimiseButton = Find<Button>(r, DashboardIds.MinimiseButton);
            m_ToastArea = Find<VisualElement>(r, DashboardIds.ToastArea);
            if (m_ToastArea != null) m_ToastArea.pickingMode = PickingMode.Ignore; // the gaps between toasts stay click-through

            m_ParticipantField = Find<DropdownField>(r, DashboardIds.ParticipantField);
            m_ImportButton = Find<Button>(r, DashboardIds.ImportButton);
            m_PartField = Find<DropdownField>(r, DashboardIds.PartField);
            m_SessionButton = Find<Button>(r, DashboardIds.SessionButton);
            m_SessionTime = Find<Label>(r, DashboardIds.SessionTime);
            m_LogFolderField = Find<TextField>(r, DashboardIds.LogFolderField);
            m_LogFolderApply = Find<Button>(r, DashboardIds.LogFolderApply);
            m_SessionStatus = Find<Label>(r, DashboardIds.SessionStatus);

            m_ViewButtons[0] = Find<Button>(r, DashboardIds.PreparedView1);
            m_ViewButtons[1] = Find<Button>(r, DashboardIds.PreparedView2);
            m_ViewButtons[2] = Find<Button>(r, DashboardIds.PreparedView3);
            m_ViewsStatus = Find<Label>(r, DashboardIds.ViewsStatus);

            m_DatasetDropdown = Find<DropdownField>(r, DashboardIds.DatasetDropdown);
            m_DatasetRescan = Find<Button>(r, DashboardIds.DatasetRescan);
            m_DatasetLoad = Find<Button>(r, DashboardIds.DatasetLoad);

            m_X = Find<DropdownField>(r, DashboardIds.XDropdown);
            m_Y = Find<DropdownField>(r, DashboardIds.YDropdown);
            m_Z = Find<DropdownField>(r, DashboardIds.ZDropdown);
            m_Color = Find<DropdownField>(r, DashboardIds.ColorDropdown);
            m_Size = Find<DropdownField>(r, DashboardIds.SizeDropdown);
            m_Time = Find<DropdownField>(r, DashboardIds.TimeDropdown);
            m_GlyphColumn = Find<DropdownField>(r, DashboardIds.GlyphColumnDropdown);
            m_PointSize = Find<Slider>(r, DashboardIds.PointSizeSlider);

            m_GlyphLabel = Find<DropdownField>(r, DashboardIds.GlyphLabelDropdown);
            m_GlyphShape = Find<DropdownField>(r, DashboardIds.GlyphShapeDropdown);

            m_FilterColumn = Find<DropdownField>(r, DashboardIds.FilterColumnDropdown);
            m_FilterValue = Find<DropdownField>(r, DashboardIds.FilterValueDropdown);
            m_FilterClear = Find<Button>(r, DashboardIds.FilterClear);
            VisualElement glyphRangesHost = Find<VisualElement>(r, DashboardIds.GlyphRanges);
            if (glyphRangesHost != null)
                m_GlyphRanges = new DashboardRangeEditor(glyphRangesHost, DashboardRangeEditor.RangeTarget.Glyph);
            VisualElement filterRangesHost = Find<VisualElement>(r, DashboardIds.FilterRanges);
            if (filterRangesHost != null)
                m_FilterRanges = new DashboardRangeEditor(filterRangesHost, DashboardRangeEditor.RangeTarget.Filter);

            m_TimeScrub = Find<Slider>(r, DashboardIds.TimeScrubSlider);
            m_PlayPause = Find<Button>(r, DashboardIds.PlayPauseButton);
            m_PlaybackRate = Find<DropdownField>(r, DashboardIds.PlaybackRateDropdown);

            m_Tooltips = Find<Toggle>(r, DashboardIds.TooltipsToggle);
            m_ResetPosition = Find<Button>(r, DashboardIds.ResetPositionButton);
            m_Undo = Find<Button>(r, DashboardIds.UndoButton);
            m_Redo = BuildRedoButton();

            m_RequestText = Find<TextField>(r, DashboardIds.RequestText);
            m_TagGroup = Find<RadioButtonGroup>(r, DashboardIds.TagGroup);
            m_TagPossible = Find<RadioButton>(r, DashboardIds.TagPossible);
            m_TagImpossible = Find<RadioButton>(r, DashboardIds.TagImpossible);
            m_TagPartly = Find<RadioButton>(r, DashboardIds.TagPartly);
            m_LogButton = Find<Button>(r, DashboardIds.LogButton);
            m_MarkerButton = Find<Button>(r, DashboardIds.MarkerButton);
            m_RecentList = Find<VisualElement>(r, DashboardIds.RecentList);
            m_RequestHint = r.Q<Label>(k_RequestHint); // optional

            m_StatusLine = Find<Label>(r, DashboardIds.StatusLine);

            BuildLayeredViewButtons();
            BuildLayersSection(r);

            if (m_MissingElements.Count > 0)
                Debug.LogWarning("[DashboardController] Missing UI elements (those controls are skipped): " +
                                 string.Join(", ", m_MissingElements));
        }

        // Redo sits right after Undo in the View section's button row (LFR-29; the UXML is not edited).
        Button BuildRedoButton()
        {
            if (m_Undo?.parent == null) return null;
            var redo = new Button { name = DashboardIds.RedoButton, text = "Redo (Ctrl+Y)", tooltip = "Redo the last undone facilitator change" };
            redo.AddToClassList("dash-button");
            VisualElement row = m_Undo.parent;
            row.Insert(row.IndexOf(m_Undo) + 1, redo);
            return redo;
        }

        // One button per PreparedViews.Layered, in a new row under the three view buttons.
        void BuildLayeredViewButtons()
        {
            VisualElement viewsRow = m_ViewButtons[2]?.parent ?? m_ViewButtons[0]?.parent;
            VisualElement section = viewsRow?.parent;
            if (section == null) return;
            var row = new VisualElement { name = DashboardIds.LayeredViews };
            row.AddToClassList("dash-row");
            row.AddToClassList("dash-views");
            foreach (PreparedView view in PreparedViews.Layered)
            {
                var button = new Button(() => ApplyPreparedView(view)) { text = view.Title, tooltip = LayeredTooltip(view) };
                button.AddToClassList("dash-button");
                button.AddToClassList("dash-view-button");
                row.Add(button);
                m_LayeredViewButtons.Add(button);
            }
            section.Insert(section.IndexOf(viewsRow) + 1, row);
        }

        static string LayeredTooltip(PreparedView view)
        {
            var parts = new List<string>();
            foreach (PreparedLayer layer in view.Layers)
            {
                string part = layer.Part > 0 ? layer.Part.ToString(k_Inv) : "{part}";
                parts.Add($"{layer.Name ?? layer.Kind}: {{participant}}_part{part}_{layer.Kind}{PreparedViews.Extension}");
            }
            return "Layers: " + string.Join("; ", parts);
        }

        // The Layers section goes right after the section that holds the dataset dropdown (B13).
        void BuildLayersSection(VisualElement root)
        {
            if (m_Manager == null) return;
            ScrollView body = root.Q<ScrollView>(DashboardIds.Body);
            VisualElement container = body?.contentContainer;
            VisualElement after = m_DatasetDropdown;
            while (after != null && after.parent != container) after = after.parent;

            var host = new VisualElement { name = DashboardIds.LayersSection };
            host.AddToClassList("dash-section");
            if (container == null)
            {
                Debug.LogWarning("[DashboardController] No dashboard body; Layers section not shown.");
                return;
            }
            if (after != null) container.Insert(container.IndexOf(after) + 1, host);
            else container.Add(host);
            m_Layers = new DashboardLayersSection(host, m_Manager, FacilitateLayer);
        }

        void HookVisualizer()
        {
            if (m_Visualizer != null) return;
            m_Visualizer = FindAnyObjectByType<ScatterplotVisualizer>();
            if (m_Visualizer != null) m_Visualizer.OnFrameBuilt += OnFrameBuilt;
        }

        T Find<T>(VisualElement root, string name) where T : VisualElement
        {
            T element = root.Q<T>(name);
            if (element == null) m_MissingElements.Add($"{name} ({typeof(T).Name})");
            return element;
        }

        void ConfigureStaticControls()
        {
            if (m_PartField != null)
            {
                m_PartField.choices = new List<string> { "1", "2" };
                if (m_PartField.index < 0) m_PartField.SetValueWithoutNotify("1");
            }
            if (m_GlyphShape != null)
                m_GlyphShape.choices = new List<string>(ScatterplotUI.k_GlyphShapeNames);
            if (m_PointSize != null)
            {
                m_PointSize.lowValue = 0f;
                m_PointSize.highValue = MultiplayerScatterplotManager.MaxPointSize;
            }
            if (m_TimeScrub != null)
            {
                m_TimeScrub.lowValue = 0f;
                m_TimeScrub.highValue = 1f;
            }
            for (int i = 0; i < m_ViewButtons.Length; i++)
            {
                if (m_ViewButtons[i] == null) continue;
                if (i < PreparedViews.All.Count)
                {
                    m_ViewButtons[i].text = PreparedViews.All[i].Title;
                    m_ViewButtons[i].tooltip = $"Loads {{participant}}_part{{part}}_{PreparedViews.All[i].Kind}{PreparedViews.Extension}";
                }
                else
                {
                    m_ViewButtons[i].style.display = DisplayStyle.None;
                }
            }
            if (m_PlaybackRate != null)
            {
                m_PlaybackRate.choices = MultiplayerScatterplotManager.PlaybackRateOptions.Select(r => FormatRate(r)).ToList();
                m_PlaybackRate.SetValueWithoutNotify(FormatRate(1f));
            }
            if (m_RequestText != null)
            {
                m_RequestText.multiline = false;
                m_RequestText.isDelayed = false;
            }
        }

        void RegisterCallbacks()
        {
            OnClick(m_MinimiseButton, ToggleMinimised);

            OnClick(m_SessionButton, ToggleSession);
            OnClick(m_LogFolderApply, ApplyLogFolder);
            OnClick(m_ImportButton, StartImport);
            // Not through OnChoice: these only change what the prepared views will load.
            m_ParticipantField?.RegisterValueChangedCallback(_ => RefreshViewsStatus());
            m_PartField?.RegisterValueChangedCallback(_ => RefreshViewsStatus());

            for (int i = 0; i < m_ViewButtons.Length && i < PreparedViews.All.Count; i++)
            {
                PreparedView view = PreparedViews.All[i];
                OnClick(m_ViewButtons[i], () => ApplyPreparedView(view));
            }

            OnClick(m_DatasetRescan, RescanDatasets);
            OnClick(m_DatasetLoad, LoadSelectedDataset);

            OnChoice(m_X, OnXChanged);
            OnChoice(m_Y, OnYChanged);
            OnChoice(m_Z, OnZChanged);
            OnChoice(m_Color, OnColorChanged);
            OnChoice(m_Size, OnSizeChanged);
            OnChoice(m_Time, OnTimeChanged);
            OnChoice(m_GlyphColumn, OnGlyphColumnChanged);
            RegisterSlider(m_PointSize, m_PointDrag, OnPointSizeDragged);

            // Picking a label only changes which label the shape dropdown edits (like ScatterplotUI).
            OnChoice(m_GlyphLabel, _ => RefreshGlyphShape());
            OnChoice(m_GlyphShape, OnGlyphShapeChanged);

            OnChoice(m_FilterColumn, OnFilterColumnChanged);
            OnChoice(m_FilterValue, OnFilterValueChanged);
            OnClick(m_FilterClear, ClearFilters);
            if (m_GlyphRanges != null) m_GlyphRanges.ApplyRequested += ApplyGlyphRanges;
            if (m_FilterRanges != null) m_FilterRanges.ApplyRequested += ApplyFilterRanges;

            RegisterSlider(m_TimeScrub, m_ScrubDrag, OnScrubDragged);
            OnClick(m_PlayPause, TogglePlayback);
            OnChoice(m_PlaybackRate, OnPlaybackRateChanged);

            m_Tooltips?.RegisterValueChangedCallback(evt =>
            {
                if (!m_IsUpdatingUI) SetTooltips(evt.newValue);
            });
            OnClick(m_ResetPosition, ResetPlotPosition);
            OnClick(m_Undo, Undo);
            OnClick(m_Redo, Redo);

            OnClick(m_LogButton, LogRequest);
            OnClick(m_MarkerButton, LogMarker);
        }

        static void OnClick(Button button, Action action)
        {
            if (button != null) button.clicked += action;
        }

        void OnChoice(DropdownField field, Action<int> handler)
        {
            if (field == null) return;
            field.RegisterValueChangedCallback(_ =>
            {
                if (m_IsUpdatingUI || m_Manager == null) return;
                handler(field.index);
            });
        }

        // Sliders apply every step without a row; one row is written when the drag ends
        // (pointer up / capture lost, or k_SliderCommitDelay without changes for keyboard steps).
        void RegisterSlider(Slider slider, SliderDrag drag, Action<float> onStep)
        {
            if (slider == null) return;
            slider.RegisterValueChangedCallback(evt =>
            {
                if (m_IsUpdatingUI || m_Manager == null) return;
                onStep(evt.newValue);
            });
            slider.RegisterCallback<PointerUpEvent>(_ => drag.CommitRequested = true, TrickleDown.TrickleDown);
            slider.RegisterCallback<PointerCaptureOutEvent>(_ => drag.CommitRequested = true, TrickleDown.TrickleDown);
        }

        void OnDestroy()
        {
            if (!ReferenceEquals(m_Manager, null))
            {
                m_Manager.OnDatasetLoaded -= OnDatasetLoaded;
                m_Manager.OnPlotSettingsChanged -= OnPlotSettingsChanged;
                m_Manager.OnLayersChanged -= OnLayersChanged;
                m_Manager.OnPlotMessage -= OnPlotMessage;
            }
            if (m_Visualizer != null) m_Visualizer.OnFrameBuilt -= OnFrameBuilt;
            if (m_Logger != null)
            {
                m_Logger.RowWritten -= OnRowWritten;
                m_Logger.SessionStateChanged -= RefreshSession;
            }
            WamIaAutoImporter.Completed -= OnImportCompleted;
            if (OwnedPanelSettings != null)
                Destroy(OwnedPanelSettings);
        }

        // ================================================================== per frame

        void Update()
        {
            if (!m_Initialised) return;

            HandleHotkeys();
            CheckSliderCommit(m_PointDrag, CommitPointSize);
            CheckSliderCommit(m_ScrubDrag, CommitScrub);
            UpdateToasts();
            // Runs also start from the bootstrap and other callers, so follow IsRunning here.
            if (m_ImportShownRunning != WamIaAutoImporter.IsRunning) UpdateImportButton();

            if (Time.unscaledTime >= m_NextTick)
            {
                m_NextTick = Time.unscaledTime + 1f;
                HookVisualizer(); // the plot may be spawned after the dashboard
                UpdateSessionTime();
                UpdateStatusLine();
            }
        }

        void HandleHotkeys()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f1Key.wasPressedThisFrame)
                ToggleMinimised();

            GetFocus(out bool anyFocused, out bool requestFocused, out bool otherTextFocused);
            if (otherTextFocused) return; // typing in the log folder field: keys belong to it

            if (keyboard.f9Key.wasPressedThisFrame)
                LogMarker();

            bool ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            if (ctrl && keyboard.zKey.wasPressedThisFrame)
            {
                if (shift) Redo();
                else Undo();
            }
            if (ctrl && keyboard.yKey.wasPressedThisFrame)
                Redo();

            bool enter = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            if (enter && (requestFocused || !anyFocused))
                LogRequest();
        }

        void GetFocus(out bool anyFocused, out bool requestFocused, out bool otherTextFocused)
        {
            Focusable focused = m_Document != null ? m_Document.rootVisualElement?.panel?.focusController?.focusedElement : null;
            anyFocused = focused != null;
            requestFocused = false;
            otherTextFocused = false;
            if (!(focused is VisualElement element)) return;

            // The focused element can be the field's inner text input, so walk up to the TextField.
            for (VisualElement e = element; e != null; e = e.parent)
            {
                if (m_RequestText != null && e == m_RequestText)
                {
                    requestFocused = true;
                    return;
                }
                if (e is TextField)
                {
                    otherTextFocused = true;
                    return;
                }
            }
        }

        void CheckSliderCommit(SliderDrag drag, Action commit)
        {
            if (!drag.Active)
            {
                drag.CommitRequested = false;
                return;
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            bool held = mouse != null && mouse.leftButton.isPressed;
            bool idle = !held && Time.unscaledTime - drag.LastChangeTime >= k_SliderCommitDelay;
            if (drag.CommitRequested || idle)
            {
                drag.CommitRequested = false;
                commit();
            }
        }

        // ================================================================== manager → UI

        void OnDatasetLoaded()
        {
            if (!m_Initialised || m_Manager == null) return;

            m_Columns.Clear();
            DatasetColumnar dataset = m_Manager.LoadedDataset;
            if (dataset != null)
                foreach (DatasetColumn column in dataset.Columns)
                    m_Columns.Add(column != null ? column.Name : "");

            m_ValueCache.Clear();
            m_FilterValuesColumn = int.MinValue;
            m_GlyphLabelsColumn = int.MinValue;

            m_IsUpdatingUI = true;
            try
            {
                SetChoices(m_X, new List<string>(m_Columns));
                SetChoices(m_Y, new List<string>(m_Columns));
                SetChoices(m_Z, new List<string>(m_Columns));
                SetChoices(m_Color, WithNone());
                SetChoices(m_Size, WithNone());
                SetChoices(m_Time, WithNone());
                SetChoices(m_GlyphColumn, WithNone());
                SetChoices(m_FilterColumn, WithNone());

                // Only here, not on every settings change, so a file picked but not yet loaded stays picked.
                int datasetIndex = m_Datasets.IndexOf(m_Manager.CurrentDatasetName ?? "");
                if (datasetIndex >= 0) SetIndex(m_DatasetDropdown, datasetIndex);
            }
            finally
            {
                m_IsUpdatingUI = false;
            }

            RefreshFromManager();
        }

        void OnPlotSettingsChanged()
        {
            if (!m_Initialised || m_Manager == null) return;
            TrackParticipantChange();
            RefreshFromManager();
        }

        void OnLayersChanged()
        {
            if (!m_Initialised || m_Manager == null) return;
            m_Layers?.Refresh(true);
        }

        void OnPlotMessage(string text, PlotMessageKind kind)
        {
            if (!m_Initialised) return;
            ShowToast(text, kind == PlotMessageKind.Info ? ToastKind.Info : ToastKind.Warning);
        }

        // Every rebuild (every frame during playback): only the warning lines, and a toast when a layer's warning changes.
        void OnFrameBuilt(PlotFrame frame)
        {
            if (!m_Initialised || m_Manager == null) return;
            LayerLineRenderer lines = LineRenderer();
            m_Layers?.UpdateWarnings(frame, lines);
            foreach (PlotLayer layer in m_Manager.Layers)
            {
                string warning = DashboardLayersSection.WarningFor(layer, m_Manager.Stack, frame, lines);
                m_ToastedWarnings.TryGetValue(layer.Id, out string shown);
                if (warning == (shown ?? "")) continue;
                m_ToastedWarnings[layer.Id] = warning;
                if (warning.Length > 0) ShowToast(warning, ToastKind.Warning); // warning lines already start with the layer name
            }
        }

        LayerLineRenderer LineRenderer() => m_Visualizer != null ? m_Visualizer.GetComponent<LayerLineRenderer>() : null;

        void RefreshFromManager()
        {
            if (!m_Initialised || m_Manager == null || m_IsUpdatingUI) return;
            MultiplayerScatterplotManager m = m_Manager;

            m_IsUpdatingUI = true;
            try
            {
                SetIndex(m_X, m.XColumnIndex);
                SetIndex(m_Y, m.YColumnIndex);
                SetIndex(m_Z, m.ZColumnIndex);
                SetIndex(m_Color, m.ColorColumnIndex + 1); // +1 for "None"
                SetIndex(m_Size, CurrentSizeColumn() + 1);
                SetIndex(m_Time, m.TimeColumnIndex + 1);
                SetIndex(m_GlyphColumn, m.GlyphColumnIndex + 1);

                if (m_PointSize != null && !m_PointDrag.Active)
                    m_PointSize.SetValueWithoutNotify(m.PointSize);
                if (m_PointSize != null)
                    SetLabel(m_PointSize, $"Point size {PointSizePercent(m_PointSize.value):F0}%");

                bool hasTime = m.TimeColumnIndex >= 0;
                if (m_TimeScrub != null)
                {
                    if (!m_ScrubDrag.Active) m_TimeScrub.SetValueWithoutNotify(m.TimeScrub);
                    SetLabel(m_TimeScrub, $"Scrub {m_TimeScrub.value * 100f:F0}%");
                    m_TimeScrub.SetEnabled(hasTime);
                }
                if (m_PlayPause != null)
                {
                    m_PlayPause.text = m.IsPlaying ? "Pause" : "Play";
                    m_PlayPause.SetEnabled(hasTime);
                }
                if (m_PlaybackRate != null)
                {
                    SetIndex(m_PlaybackRate, NearestRateIndex(m.PlaybackRate));
                    m_PlaybackRate.SetEnabled(hasTime);
                }

                m_Tooltips?.SetValueWithoutNotify(m.ShowTooltips);

                // Filters (first entry, like the VR menu's single filter row)
                SetIndex(m_FilterColumn, m.FilterColumnIndex + 1);
                PopulateFilterValues(m.FilterColumnIndex);
                if (m_FilterValue != null && m_FilterValue.choices != null)
                {
                    // Option 0 is "Any value" (empty label); search from 1 so a real value with that text still resolves.
                    int valueIndex = string.IsNullOrEmpty(m.FilterLabel) ? 0 : m_FilterValue.choices.IndexOf(m.FilterLabel, 1);
                    SetIndex(m_FilterValue, Mathf.Max(0, valueIndex));
                }
                m_FilterClear?.SetEnabled(m.Filters.Count > 0);

                // Glyphs
                PopulateGlyphLabels(m.GlyphColumnIndex);
                RefreshGlyphShape();

                // Range sliders: rebuild only when the column, dataset or committed ranges changed.
                m_GlyphRanges?.Sync(m);
                m_FilterRanges?.Sync(m);
                m_Layers?.Refresh(false); // cheap unless the stack changed
            }
            finally
            {
                m_IsUpdatingUI = false;
            }

            UpdateStatusLine();
        }

        int CurrentSizeColumn() =>
            m_Manager.SizeMapped && m_Manager.SizeColumnIndex >= 0 ? m_Manager.SizeColumnIndex : -1;

        void PopulateFilterValues(int columnIndex)
        {
            if (m_FilterValue == null || columnIndex == m_FilterValuesColumn) return;
            m_FilterValuesColumn = columnIndex;

            var options = new List<string> { ScatterplotUI.k_AnyFilterValueOption };
            options.AddRange(ColumnValues(columnIndex));
            m_FilterValue.choices = options;
            m_FilterValue.SetValueWithoutNotify(options[0]);
            m_FilterValue.SetEnabled(columnIndex >= 0);
        }

        void PopulateGlyphLabels(int columnIndex)
        {
            if (m_GlyphLabel == null || columnIndex == m_GlyphLabelsColumn) return;
            m_GlyphLabelsColumn = columnIndex;

            var options = new List<string>(ColumnValues(columnIndex));
            m_GlyphLabel.choices = options;
            m_GlyphLabel.SetValueWithoutNotify(options.Count > 0 ? options[0] : "");
            m_GlyphLabel.SetEnabled(options.Count > 0);
            m_GlyphShape?.SetEnabled(options.Count > 0);
        }

        /// <summary>
        /// Values of a column as ScatterplotUI lists them: numeric columns without UniqueValues are
        /// read from the rows (empty cells skipped), categorical ones use UniqueValues; both sorted
        /// numerically where possible. Same formatting so labels match what the manager compares.
        /// </summary>
        List<string> ColumnValues(int columnIndex)
        {
            DatasetColumnar dataset = m_Manager != null ? m_Manager.LoadedDataset : null;
            if (columnIndex < 0 || dataset == null) return new List<string>();
            if (m_ValueCache.TryGetValue(columnIndex, out List<string> cached)) return cached;

            var values = new List<string>();
            DatasetColumn column = dataset.GetColumn(columnIndex);
            if (column != null)
            {
                if (column.IsNumeric && column.UniqueValues.Count == 0)
                {
                    var unique = new HashSet<string>();
                    for (int row = 0; row < dataset.RowCount; row++)
                    {
                        if (!dataset.HasValue(row, columnIndex)) continue; // an empty cell's stored 0 is not a value
                        unique.Add(dataset.GetNumericValue(row, columnIndex).ToString());
                    }
                    values.AddRange(unique);
                }
                else if (column.UniqueValues != null && column.UniqueValues.Count > 0)
                {
                    values.AddRange(column.UniqueValues);
                }
                values.Sort(CompareValues);
            }

            m_ValueCache[columnIndex] = values;
            return values;
        }

        static int CompareValues(string a, string b)
        {
            if (float.TryParse(a, out float numA) && float.TryParse(b, out float numB))
                return numA.CompareTo(numB);
            return string.Compare(a, b, StringComparison.Ordinal);
        }

        string SelectedGlyphLabel()
        {
            if (m_GlyphLabel == null || m_GlyphLabel.choices == null || m_GlyphLabel.index < 0) return null;
            return m_GlyphLabel.value;
        }

        void RefreshGlyphShape()
        {
            if (m_GlyphShape == null || m_Manager == null) return;
            string label = SelectedGlyphLabel();
            if (string.IsNullOrEmpty(label)) return;

            int glyph = Mathf.Clamp(m_Manager.GetGlyphForLabel(label, 0), 0, ScatterplotUI.k_GlyphShapeNames.Length - 1);
            bool wasUpdating = m_IsUpdatingUI;
            m_IsUpdatingUI = true;
            SetIndex(m_GlyphShape, glyph);
            m_IsUpdatingUI = wasUpdating;
        }

        List<string> WithNone()
        {
            var list = new List<string>(m_Columns.Count + 1) { k_None };
            list.AddRange(m_Columns);
            return list;
        }

        // Only when the text changed: refreshes run every frame during playback.
        static void SetLabel(Slider slider, string text)
        {
            if (slider.label != text) slider.label = text;
        }

        static void SetChoices(DropdownField field, List<string> choices)
        {
            if (field == null) return;
            field.choices = choices;
            if (!choices.Contains(field.value))
                field.SetValueWithoutNotify(choices.Count > 0 ? choices[0] : "");
        }

        static void SetIndex(DropdownField field, int index)
        {
            if (field == null || field.choices == null) return;
            List<string> choices = field.choices;
            string value = index >= 0 && index < choices.Count ? choices[index] : "";
            if (field.value != value) field.SetValueWithoutNotify(value);
        }

        string ColumnName(int index) => index >= 0 && index < m_Columns.Count ? m_Columns[index] : k_None;

        // ================================================================== facilitator changes

        /// <summary>Pushes an undo snapshot (optional) and runs a manager call attributed to the facilitator.</summary>
        void Facilitate(string action, string oldValue, string newValue, Action apply, bool pushUndo = true)
        {
            if (m_Manager == null) return;
            if (pushUndo) PushUndo();
            RunAsFacilitator(action, oldValue, newValue, apply, true);
        }

        /// <summary>
        /// Facilitate for a layer change (DashboardLayersSection): <paramref name="layer"/> is the
        /// layer's name for the Layer column, null = the active layer after apply (AddLayer, DuplicateLayer).
        /// </summary>
        void FacilitateLayer(string action, string layer, string oldValue, string newValue, Action apply, bool pushUndo)
        {
            if (m_Manager == null) return;
            if (pushUndo) PushUndo();
            RunAsFacilitator(action, oldValue, newValue, apply, true, true, layer);
        }

        void RunAsFacilitator(string action, string oldValue, string newValue, Action apply, bool writeRow,
                              bool withLayer = false, string layer = null)
        {
            m_FacilitatorDepth++;
            try
            {
                if (m_Logger == null) apply();
                else if (withLayer) m_Logger.RunAsFacilitator(action, layer, oldValue ?? "", newValue ?? "", apply, writeRow);
                else m_Logger.RunAsFacilitator(action, oldValue ?? "", newValue ?? "", apply, writeRow);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Flash($"{action} failed: {e.Message}");
            }
            finally
            {
                m_FacilitatorDepth--;
            }

            // Without a session there are no logger rows, so remember the change ourselves.
            if (writeRow && !IsLogging) m_LastChange = $"facilitator {action}";
            if (m_Manager != null) m_LastSnapshot = PlotSnapshot.Capture(m_Manager);
            UpdateStatusLine();
        }

        void OnXChanged(int index)
        {
            if (index < 0) return;
            Facilitate(StudyActions.SetX, ColumnName(m_Manager.XColumnIndex), ColumnName(index),
                () => m_Manager.RequestXColumnRpc(index));
        }

        void OnYChanged(int index)
        {
            if (index < 0) return;
            Facilitate(StudyActions.SetY, ColumnName(m_Manager.YColumnIndex), ColumnName(index),
                () => m_Manager.RequestYColumnRpc(index));
        }

        void OnZChanged(int index)
        {
            if (index < 0) return;
            Facilitate(StudyActions.SetZ, ColumnName(m_Manager.ZColumnIndex), ColumnName(index),
                () => m_Manager.RequestZColumnRpc(index));
        }

        void OnColorChanged(int index)
        {
            if (index < 0) return;
            int column = index - 1; // 0 ("None") → -1
            Facilitate(StudyActions.SetColor, ColumnName(m_Manager.ColorColumnIndex), ColumnName(column),
                () => m_Manager.RequestColorColumnRpc(column));
        }

        void OnSizeChanged(int index)
        {
            if (index < 0) return;
            int column = index - 1; // "None" = fixed size (SizeMapped off)
            Facilitate(StudyActions.SetSize, ColumnName(CurrentSizeColumn()), ColumnName(column), () =>
            {
                if (column < 0)
                {
                    m_Manager.RequestSizeMappedRpc(false);
                }
                else
                {
                    m_Manager.RequestSizeColumnRpc(column);
                    m_Manager.RequestSizeMappedRpc(true);
                }
            });
        }

        void OnTimeChanged(int index)
        {
            if (index < 0) return;
            int column = index - 1;
            Facilitate(StudyActions.SetTime, ColumnName(m_Manager.TimeColumnIndex), ColumnName(column),
                () => m_Manager.RequestTimeColumnRpc(column));
        }

        void OnGlyphColumnChanged(int index)
        {
            if (index < 0) return;
            int column = index - 1;
            Facilitate(StudyActions.SetGlyphColumn, ColumnName(m_Manager.GlyphColumnIndex), ColumnName(column),
                () => m_Manager.RequestGlyphColumnRpc(column));
        }

        void OnGlyphShapeChanged(int index)
        {
            string label = SelectedGlyphLabel();
            if (index < 0 || string.IsNullOrEmpty(label)) return;
            string[] shapes = ScatterplotUI.k_GlyphShapeNames;
            int oldGlyph = m_Manager.GetGlyphForLabel(label, 0);
            string oldShape = oldGlyph >= 0 && oldGlyph < shapes.Length ? shapes[oldGlyph] : oldGlyph.ToString(k_Inv);
            string newShape = index < shapes.Length ? shapes[index] : index.ToString(k_Inv);
            Facilitate(StudyActions.AssignGlyph, $"{label}={oldShape}", $"{label}={newShape}",
                () => m_Manager.RequestGlyphAssignmentRpc(label, index));
        }

        void OnFilterColumnChanged(int index)
        {
            if (index < 0) return;
            int column = index - 1; // "None" removes the first filter entry
            Facilitate(StudyActions.SetFilterColumn, ColumnName(m_Manager.FilterColumnIndex), ColumnName(column),
                () => m_Manager.RequestFilterIndexRpc(column));
        }

        void OnFilterValueChanged(int index)
        {
            if (index < 0 || m_Manager.Filters.Count == 0 || m_FilterValue?.choices == null) return;
            string label = index == 0 ? "" : m_FilterValue.choices[index]; // "Any value" = empty label
            Facilitate(StudyActions.SetFilterValue, FilterValueName(m_Manager.FilterLabel), FilterValueName(label),
                () => m_Manager.RequestFilterLabelRpc(label));
        }

        static string FilterValueName(string label) =>
            string.IsNullOrEmpty(label) ? ScatterplotUI.k_AnyFilterValueOption : label;

        void ApplyGlyphRanges(List<MultiplayerScatterplotManager.GlyphRange> ranges)
        {
            if (m_Manager == null) return;
            string old = DashboardRangeEditor.Describe(m_Manager.GlyphRanges, true);
            bool applied = false;
            Facilitate(StudyActions.SetGlyphRanges, old, DashboardRangeEditor.Describe(ranges, true),
                () => applied = m_Manager.RequestSetGlyphRangesRpc(ranges));
            if (!applied)
            {
                m_GlyphRanges?.MarkApplyFailed();
                ShowToast("Glyph ranges were not applied (see the Console).", ToastKind.Warning);
            }
        }

        void ApplyFilterRanges(List<MultiplayerScatterplotManager.GlyphRange> ranges)
        {
            if (m_Manager == null || m_Manager.Filters.Count == 0) return;
            var current = m_Manager.Filters[0].Ranges.ConvertAll(r =>
                new MultiplayerScatterplotManager.GlyphRange { min = r.min, max = r.max });
            string old = current.Count > 0
                ? DashboardRangeEditor.Describe(current, false)
                : FilterValueName(m_Manager.FilterLabel);
            var filterRanges = ranges.ConvertAll(r => new MultiplayerScatterplotManager.FilterRange { min = r.min, max = r.max });
            bool applied = false;
            Facilitate(StudyActions.SetFilterRanges, old,
                filterRanges.Count > 0 ? DashboardRangeEditor.Describe(ranges, false) : ScatterplotUI.k_AnyFilterValueOption,
                () => applied = m_Manager.RequestSetFilterRangesRpc(filterRanges));
            if (!applied)
            {
                m_FilterRanges?.MarkApplyFailed();
                ShowToast("Filter ranges were not applied (see the Console).", ToastKind.Warning);
            }
        }

        void ClearFilters()
        {
            if (m_Manager == null || m_Manager.Filters.Count == 0) return;
            string old = string.Join("; ", m_Manager.Filters.Select(f =>
                $"{ColumnName(f.ColumnIndex)}={FilterValueName(f.Label)}"));
            Facilitate(StudyActions.ClearFilters, old, "", () => m_Manager.RequestClearFiltersRpc());
        }

        void TogglePlayback()
        {
            if (m_Manager == null) return;
            bool play = !m_Manager.IsPlaying;
            Facilitate(play ? StudyActions.Play : StudyActions.Pause, play ? "Paused" : "Playing", play ? "Playing" : "Paused",
                () => m_Manager.RequestPlaybackToggleRpc(play));
        }

        void OnPlaybackRateChanged(int index)
        {
            float[] options = MultiplayerScatterplotManager.PlaybackRateOptions;
            if (index < 0 || index >= options.Length) return;
            float rate = options[index];
            if (Mathf.Approximately(rate, m_Manager.PlaybackRate)) return;
            Facilitate(StudyActions.SetPlaybackRate, FormatRateValue(m_Manager.PlaybackRate), FormatRateValue(rate),
                () => m_Manager.RequestPlaybackRateRpc(rate));
        }

        /// <summary>Index of the option closest to <paramref name="rate"/> (a rate set elsewhere still shows something).</summary>
        static int NearestRateIndex(float rate)
        {
            float[] options = MultiplayerScatterplotManager.PlaybackRateOptions;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < options.Length; i++)
            {
                float distance = Mathf.Abs(options[i] - rate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        static string FormatRateValue(float rate) => rate.ToString("0.##", k_Inv);
        static string FormatRate(float rate) => FormatRateValue(rate) + "×";

        void SetTooltips(bool show)
        {
            if (m_Manager == null) return;
            // Tooltips are local and not in PlotSnapshot, so there is nothing to undo.
            Facilitate(StudyActions.Tooltips, OnOff(m_Manager.ShowTooltips), OnOff(show),
                () => m_Manager.RequestTooltipsToggleRpc(show), pushUndo: false);
            // The manager does not raise OnPlotSettingsChanged for tooltips.
            RefreshFromManager();
        }

        static string OnOff(bool value) => value ? "On" : "Off";

        void ResetPlotPosition()
        {
            var plot = FindAnyObjectByType<CoordinateSystemInteractable>();
            if (plot == null)
            {
                Flash("No plot found to reset");
                return;
            }
            // The plot transform is not in PlotSnapshot, so there is nothing to undo.
            Facilitate(StudyActions.ResetPosition, "", "", plot.ResetPosition, pushUndo: false);
        }

        // ---------------------------------------------------------------- sliders

        void OnPointSizeDragged(float value)
        {
            BeginDrag(m_PointDrag, m_Manager.PointSize);
            RunAsFacilitator(StudyActions.SetPointSize, FormatSize(m_PointDrag.StartValue), FormatSize(value),
                () => m_Manager.RequestPointSizeRpc(value), writeRow: false);
        }

        void CommitPointSize()
        {
            if (!EndDrag(m_PointDrag, m_PointSize, out float start, out float end)) return;
            RunAsFacilitator(StudyActions.SetPointSize, FormatSize(start), FormatSize(end),
                () => m_Manager.RequestPointSizeRpc(end), writeRow: true);
        }

        void OnScrubDragged(float value)
        {
            BeginDrag(m_ScrubDrag, m_Manager.TimeScrub);
            RunAsFacilitator(StudyActions.Scrub, FormatScrub(m_ScrubDrag.StartValue), FormatScrub(value),
                () => m_Manager.RequestTimeScrubRpc(value), writeRow: false);
        }

        void CommitScrub()
        {
            if (!EndDrag(m_ScrubDrag, m_TimeScrub, out float start, out float end)) return;
            RunAsFacilitator(StudyActions.Scrub, FormatScrub(start), FormatScrub(end),
                () => m_Manager.RequestTimeScrubRpc(end), writeRow: true);
        }

        void BeginDrag(SliderDrag drag, float currentValue)
        {
            if (!drag.Active)
            {
                drag.Active = true;
                drag.StartValue = currentValue;
                PushUndo(clearRedo: false); // one snapshot per drag, taken before the first step; redo goes only if the value changes
            }
            drag.LastChangeTime = Time.unscaledTime;
        }

        /// <summary>Ends a drag. False (and the drag's undo snapshot dropped) when the value ended where it started.</summary>
        bool EndDrag(SliderDrag drag, Slider slider, out float start, out float end)
        {
            start = drag.StartValue;
            end = slider != null ? slider.value : start;
            drag.Active = false;
            if (m_Manager == null) return false;
            if (Mathf.Approximately(start, end))
            {
                m_History.DropLastUndo();
                UpdateUndoButton();
                RefreshFromManager();
                return false;
            }
            m_History.ClearRedo();
            UpdateUndoButton();
            return true;
        }

        static float PointSizePercent(float size) => size / MultiplayerScatterplotManager.MaxPointSize * 100f;
        static string FormatSize(float size) => size.ToString("0.####", k_Inv);
        static string FormatScrub(float scrub) => scrub.ToString("0.###", k_Inv);

        // ---------------------------------------------------------------- dataset

        void RescanDatasets()
        {
            try
            {
                m_Datasets = DatasetCatalog.Files() ?? new List<string>();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                m_Datasets = new List<string>();
                Flash("Could not read the dataset folder");
            }

            m_Layers?.SetDatasets(m_Datasets);
            if (m_DatasetDropdown == null) return;
            string selected = m_DatasetDropdown.value;
            m_DatasetDropdown.choices = new List<string>(m_Datasets);
            int index = m_Datasets.IndexOf(selected ?? "");
            if (index < 0 && m_Manager != null) index = m_Datasets.IndexOf(m_Manager.CurrentDatasetName ?? "");
            if (index < 0 && m_Datasets.Count > 0) index = 0;
            m_DatasetDropdown.SetValueWithoutNotify(index >= 0 ? m_Datasets[index] : "");
            m_DatasetLoad?.SetEnabled(m_Datasets.Count > 0);
        }

        void LoadSelectedDataset()
        {
            if (m_Manager == null || m_DatasetDropdown == null) return;
            string file = m_DatasetDropdown.value;
            if (string.IsNullOrEmpty(file)) return;
            // A new plot: the whole stack becomes one layer on this file (decision E4).
            Facilitate(StudyActions.LoadDataset, m_Manager.CurrentDatasetName ?? "", file,
                () => m_Manager.RequestLoadDatasetRpc(file));
        }

        // ---------------------------------------------------------------- prepared views

        /// <summary>
        /// Loads {participant}_part{part}_{kind}.cdataset (participant and part from the Session
        /// fields, logging or not) and sets every mapping of the view in one facilitator change:
        /// one undo snapshot, one ApplyView row.
        /// </summary>
        void ApplyPreparedView(PreparedView view)
        {
            if (m_Manager == null || view == null) return;
            if (view.Layers != null)
            {
                ApplyLayeredView(view);
                return;
            }

            string participant = CurrentParticipant();
            if (participant.Length == 0)
            {
                SetViewsStatus("Pick a participant in Session first (none yet: run WAM, then Import new logs)");
                return;
            }
            int part = SelectedPart();

            string wanted = PreparedViews.FileFor(participant, part, view.Kind);
            string who = $"{PreparedViews.NormaliseParticipant(participant)} part {part.ToString(k_Inv)}";

            List<string> files;
            try
            {
                files = DatasetCatalog.Files() ?? new List<string>(); // fresh: the converter may have just run
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetViewsStatus("Could not read the dataset folder");
                return;
            }
            string file = files.FirstOrDefault(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase));
            string rescanHint = files.SequenceEqual(m_Datasets) ? "" : " · dataset list is out of date: press Rescan";
            if (file == null)
            {
                SetViewsStatus($"{who} not imported yet (missing {wanted}): press Import new logs{rescanHint}");
                return;
            }

            // Same dataset already loaded: check the columns before writing a row or an undo snapshot.
            bool reload = !string.Equals(m_Manager.CurrentDatasetName ?? "", file, StringComparison.Ordinal) ||
                          m_Manager.LoadedDataset == null;
            if (!reload && !PreparedViews.TryResolve(view, m_Manager.LoadedDataset,
                    out _, out _, out _, out _, out _, out _, out _, out string missingNow))
            {
                SetViewsStatus($"{file} has no column {missingNow}: re-run the converter");
                return;
            }

            string error = null;
            string oldValue = m_Manager.CurrentDatasetName ?? "";
            Facilitate(StudyActions.ApplyView, oldValue, view.Title, () =>
            {
                MultiplayerScatterplotManager m = m_Manager;
                if (reload)
                {
                    m.RequestLoadDatasetRpc(file); // synchronous on the host: LoadedDataset is set afterwards
                    if (m.LoadedDataset == null || !string.Equals(m.CurrentDatasetName ?? "", file, StringComparison.Ordinal))
                    {
                        error = $"Could not load {file} (see Console)";
                        return;
                    }
                }

                if (!PreparedViews.TryResolve(view, m.LoadedDataset, out int x, out int y, out int z,
                        out int color, out int size, out int time, out int glyph, out string missing))
                {
                    error = $"{file} has no column {missing}: re-run the converter";
                    return;
                }

                if (m.Filters.Count > 0) m.RequestClearFiltersRpc(); // a view shows every row
                if (x >= 0) m.RequestXColumnRpc(x);
                if (y >= 0) m.RequestYColumnRpc(y);
                if (z >= 0) m.RequestZColumnRpc(z);
                m.RequestColorColumnRpc(color);
                if (size >= 0)
                {
                    m.RequestSizeColumnRpc(size);
                    m.RequestSizeMappedRpc(true);
                }
                else
                {
                    m.RequestSizeMappedRpc(false);
                }
                m.RequestTimeColumnRpc(time); // also stops playback
                m.RequestGlyphColumnRpc(glyph);
                if (glyph >= 0 && view.GlyphShapes != null)
                    foreach ((string label, int shape) in view.GlyphShapes)
                        m.RequestGlyphAssignmentRpc(label, shape);
                if (m.IsPlaying) m.RequestPlaybackToggleRpc(false);
                m.RequestTimeScrubRpc(0f);
            });

            if (error != null)
            {
                SetViewsStatus(error);
                Flash($"{view.Title}: {error}");
            }
            else
            {
                SetViewsStatus($"{view.Title} · {file}{rescanHint}");
            }
        }

        /// <summary>
        /// LFR-27: a view with a layer stack. Every file (a layer's fixed part, else the Session part)
        /// must exist and have the view's columns before anything changes; then the stack is built in
        /// one facilitator change: one undo step, one ApplyView row (plot-level).
        /// </summary>
        void ApplyLayeredView(PreparedView view)
        {
            string participant = CurrentParticipant();
            if (participant.Length == 0)
            {
                SetViewsStatus("Pick a participant in Session first (none yet: run WAM, then Import new logs)");
                return;
            }
            int part = SelectedPart();

            List<string> available;
            try
            {
                available = DatasetCatalog.Files() ?? new List<string>();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetViewsStatus("Could not read the dataset folder");
                return;
            }
            string rescanHint = available.SequenceEqual(m_Datasets) ? "" : " · dataset list is out of date: press Rescan";

            List<string> missing = PreparedViews.MissingFiles(view, participant, part, available);
            if (missing.Count > 0)
            {
                SetViewsStatus($"{view.Title}: missing {string.Join(", ", missing)}: press Import new logs{rescanHint}");
                return;
            }
            var files = new List<string>();
            foreach (PreparedLayer layer in view.Layers)
            {
                string wanted = PreparedViews.FileFor(participant, part, layer);
                files.Add(available.First(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase)));
            }
            if (!PreparedViews.CheckLayers(view, files, out string problem))
            {
                SetViewsStatus($"{view.Title}: {problem}");
                return;
            }

            string error = null;
            Facilitate(StudyActions.ApplyView, m_Manager.CurrentDatasetName ?? "", view.Title,
                () => error = PreparedViews.ApplyLayers(m_Manager, view, files));
            if (error != null)
            {
                SetViewsStatus($"{view.Title}: {error}");
                Flash($"{view.Title}: {error}");
            }
            else
            {
                SetViewsStatus($"{view.Title} · {string.Join(" + ", files)}{rescanHint}");
            }
        }

        void SetViewsStatus(string text)
        {
            if (m_ViewsStatus != null) m_ViewsStatus.text = text;
        }

        // ---------------------------------------------------------------- undo

        // Undo and redo restore the whole stack (LFR-29). Facilitator-only (decision E6): participant
        // changes are never pushed, so an undo also drops what the participant did since.
        void PushUndo(bool clearRedo = true)
        {
            if (m_Manager == null) return;
            m_History.Push(PlotSnapshot.Capture(m_Manager), clearRedo);
            UpdateUndoButton();
        }

        void Undo() => StepHistory(redo: false);

        void Redo() => StepHistory(redo: true);

        void StepHistory(bool redo)
        {
            if (m_Manager == null) return;
            if (redo ? m_History.RedoCount == 0 : m_History.UndoCount == 0)
            {
                Flash(redo ? "Nothing to redo" : "Nothing to undo");
                return;
            }

            PlotSnapshot current = PlotSnapshot.Capture(m_Manager);
            string missing = null;
            bool Restore(PlotSnapshot snapshot)
            {
                missing = DashboardUndoHistory.MissingDataset(snapshot);
                if (missing != null) return false;
                RunAsFacilitator(redo ? StudyActions.Redo : StudyActions.Undo, "", "",
                    () => m_Manager.ApplyNetworkSnapshot(snapshot, snapshot.TimeScrub), writeRow: true);
                return true;
            }
            bool done = redo ? m_History.TryRedo(current, Restore) : m_History.TryUndo(current, Restore);
            if (!done && missing != null)
                Flash($"{(redo ? "Redo" : "Undo")} not possible: {missing} in that state could not be loaded");
            UpdateUndoButton();
        }

        void UpdateUndoButton()
        {
            m_Undo?.SetEnabled(m_History.UndoCount > 0);
            m_Redo?.SetEnabled(m_History.RedoCount > 0);
        }

        // ================================================================== session

        void ToggleSession()
        {
            if (m_Logger == null)
            {
                m_SessionMessage = "No logger available";
                RefreshSession();
                return;
            }

            if (m_Logger.IsLogging)
            {
                m_Logger.StopSession();
                m_SessionMessage = null;
            }
            else
            {
                string participant = SelectedParticipant();
                if (participant.Length == 0)
                {
                    m_SessionMessage = "No participant selected: run WAM, then Import new logs";
                    RefreshSession();
                    return;
                }
                m_SessionMessage = m_Logger.StartSession(participant, SelectedPart(), out string error) ? null : error;
            }
            RefreshSession();
        }

        void ApplyLogFolder()
        {
            if (m_Logger == null || m_LogFolderField == null) return;
            if (m_Logger.SetLogFolder((m_LogFolderField.value ?? "").Trim(), out string error))
            {
                m_LogFolderField.SetValueWithoutNotify(m_Logger.LogFolder ?? "");
                m_SessionMessage = "Log folder set";
            }
            else
            {
                m_SessionMessage = string.IsNullOrEmpty(error) ? "Log folder not usable" : error;
            }
            RefreshSession();
        }

        void RefreshSession()
        {
            if (!m_Initialised) return;
            bool logging = IsLogging;

            if (m_SessionButton != null)
            {
                m_SessionButton.text = logging ? "Stop logging" : "Start logging";
                // Stopping must always work; starting needs a participant from the WAM logs.
                m_SessionButton.SetEnabled(m_Logger != null && (logging || HasParticipants));
            }
            m_ParticipantField?.SetEnabled(!logging && HasParticipants);
            m_PartField?.SetEnabled(!logging);
            m_LogFolderField?.SetEnabled(!logging);
            m_LogFolderApply?.SetEnabled(!logging && m_Logger != null);

            if (m_SessionStatus != null)
            {
                string file = logging && !string.IsNullOrEmpty(m_Logger.CurrentFilePath)
                    ? Path.GetFileName(m_Logger.CurrentFilePath) : null;
                if (!string.IsNullOrEmpty(m_SessionMessage))
                    m_SessionStatus.text = file != null ? $"{m_SessionMessage} · {file}" : m_SessionMessage;
                else if (file != null)
                    m_SessionStatus.text = $"Logging to {file}";
                else
                    m_SessionStatus.text = HasParticipants ? "Not logging"
                        : "Not logging · no participants yet: run a WAM session, then press Import new logs";
            }
            for (int i = 0; i < m_ViewButtons.Length; i++)
                m_ViewButtons[i]?.SetEnabled(logging || HasParticipants);
            foreach (Button button in m_LayeredViewButtons)
                button.SetEnabled(logging || HasParticipants);

            m_LogButton?.SetEnabled(logging);
            m_MarkerButton?.SetEnabled(logging);
            if (m_RequestHint != null)
                m_RequestHint.style.display = logging ? DisplayStyle.None : DisplayStyle.Flex;

            // A fresh session starts with a fresh baseline for naming participant changes.
            if (m_Manager != null) m_LastSnapshot = PlotSnapshot.Capture(m_Manager);
            UpdateSessionTime();
        }

        void UpdateSessionTime()
        {
            if (m_SessionTime == null) return;
            double seconds = IsLogging ? Math.Max(0d, m_Logger.SessionTime) : 0d;
            int total = (int)seconds;
            m_SessionTime.text = $"{total / 60:00}:{total % 60:00}";
        }

        // ================================================================== participants + WAM import

        bool HasParticipants => m_Participants.Count > 0;

        /// <summary>The dropdown's participant label ("P07"), or "" when there is none (placeholder shown).</summary>
        string SelectedParticipant()
        {
            if (m_ParticipantField == null || !HasParticipants) return "";
            string value = (m_ParticipantField.value ?? "").Trim();
            return m_Participants.Contains(value) ? value : "";
        }

        /// <summary>The selected participant, or the logging session's one when nothing is selected.</summary>
        string CurrentParticipant()
        {
            string participant = SelectedParticipant();
            if (participant.Length == 0 && IsLogging)
                participant = m_Logger.ParticipantId ?? "";
            return participant;
        }

        int SelectedPart()
        {
            if (m_PartField != null && int.TryParse(m_PartField.value, NumberStyles.Integer, k_Inv, out int part))
                return part;
            return 1;
        }

        /// <summary>
        /// Rebuilds the participant choices: labels from the WAM Meta files plus <paramref name="extra"/>
        /// (the converter's list) plus the logging session's participant. Keeps the current selection
        /// when it still exists, otherwise picks the highest participant number. With no participants
        /// a single disabled "No participants yet" choice is shown.
        /// </summary>
        void RefreshParticipants(IEnumerable<string> extra)
        {
            var all = new List<string>(WamIaAutoImporter.ScanParticipants(WamIaAutoImporter.LogFolder));
            if (extra != null) all.AddRange(extra); // already labels: ParticipantLabel is not idempotent for "Pabc"
            if (IsLogging && !string.IsNullOrEmpty(m_Logger.ParticipantId))
                all.Add(PreparedViews.NormaliseParticipant(m_Logger.ParticipantId));

            string current = SelectedParticipant();
            m_Participants = WamIaAutoImporter.SortParticipants(all);

            if (m_ParticipantField != null)
            {
                if (m_Participants.Count == 0)
                {
                    m_ParticipantField.choices = new List<string> { k_NoParticipants };
                    m_ParticipantField.SetValueWithoutNotify(k_NoParticipants);
                }
                else
                {
                    m_ParticipantField.choices = new List<string>(m_Participants);
                    string pick = m_Participants.Contains(current) ? current : WamIaAutoImporter.DefaultParticipant(m_Participants);
                    m_ParticipantField.SetValueWithoutNotify(pick ?? m_Participants[0]);
                }
            }
            RefreshSession();
        }

        void SelectParticipant(string label)
        {
            if (m_ParticipantField == null || string.IsNullOrEmpty(label) || !m_Participants.Contains(label)) return;
            m_ParticipantField.SetValueWithoutNotify(label);
        }

        /// <summary>Which prepared-view files exist for the selected participant and part.</summary>
        void RefreshViewsStatus()
        {
            if (!m_Initialised) return;
            string participant = CurrentParticipant();
            if (participant.Length == 0)
            {
                SetViewsStatus("No participants yet: run a WAM session, then press Import new logs");
                return;
            }

            int part = SelectedPart();
            List<string> files;
            try
            {
                files = DatasetCatalog.Files() ?? new List<string>();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetViewsStatus("Could not read the dataset folder");
                return;
            }

            string who = $"{PreparedViews.NormaliseParticipant(participant)} part {part.ToString(k_Inv)}";
            List<string> missing = PreparedViews.MissingFiles(participant, part, files);
            if (missing.Count == 0)
                SetViewsStatus($"{who}: all views ready");
            else if (missing.Count >= PreparedViews.All.Count)
                SetViewsStatus($"{who} not imported yet: press Import new logs after the WAM session");
            else
                SetViewsStatus($"{who}: missing {string.Join(", ", missing)}");
        }

        void StartImport()
        {
            WamIaAutoImporter.Run(null); // results arrive through Completed (OnImportCompleted)
            UpdateImportButton();
        }

        void UpdateImportButton()
        {
            bool running = WamIaAutoImporter.IsRunning;
            m_ImportShownRunning = running;
            if (m_ImportButton == null) return;
            m_ImportButton.text = running ? "Importing…" : "Import new logs";
            m_ImportButton.SetEnabled(!running);
            m_ImportButton.tooltip = $"Converts new WAM logs from {WamIaAutoImporter.LogFolder}";
        }

        /// <summary>Toasts for what changed, then the participant, dataset and prepared-view lists.</summary>
        void OnImportCompleted(WamIaImportResult result)
        {
            if (!m_Initialised || result == null) return;

            // Pushed oldest-kind first so errors end up on top (newest on top).
            foreach (WamIaConverted c in result.converted)
                if (c != null) ShowToast("Imported " + WamIaAutoImporter.PartName(c.participant, c.part), ToastKind.Info);
            foreach (WamIaReplaced r in result.replaced)
                if (r != null) ShowToast(WamIaAutoImporter.ReplacedMessage(r), ToastKind.Warning);
            foreach (string warning in result.warnings)
                if (!string.IsNullOrEmpty(warning)) ShowToast(warning, ToastKind.Warning);
            foreach (string error in result.errors)
                if (!string.IsNullOrEmpty(error)) ShowToast(error, ToastKind.Error);

            RefreshParticipants(result.participants);
            if (result.HasChanges) RescanDatasets();
            RefreshViewsStatus();
            UpdateImportButton();
        }

        // ================================================================== request log

        void LogRequest()
        {
            if (m_Logger == null || m_RequestText == null) return;
            string text = (m_RequestText.value ?? "").Trim();
            if (text.Length == 0) return;
            if (!m_Logger.IsLogging)
            {
                Flash("Start logging to record requests");
                return;
            }
            m_Logger.LogRequest(text, SelectedTag());
            m_RequestText.SetValueWithoutNotify("");
            ClearTag();
        }

        void LogMarker()
        {
            if (m_Logger == null) return;
            if (!m_Logger.IsLogging)
            {
                Flash("Start logging to record markers");
                return;
            }
            m_Logger.LogMarker();
        }

        RequestTag SelectedTag()
        {
            if (m_TagPossible != null || m_TagImpossible != null || m_TagPartly != null)
            {
                if (m_TagPossible != null && m_TagPossible.value) return RequestTag.Possible;
                if (m_TagImpossible != null && m_TagImpossible.value) return RequestTag.Impossible;
                if (m_TagPartly != null && m_TagPartly.value) return RequestTag.Partly;
                return RequestTag.None;
            }
            // No individual radios found: fall back to the group's index (child order Possible, Impossible, Partly).
            if (m_TagGroup == null) return RequestTag.None;
            switch (m_TagGroup.value)
            {
                case 0: return RequestTag.Possible;
                case 1: return RequestTag.Impossible;
                case 2: return RequestTag.Partly;
                default: return RequestTag.None;
            }
        }

        void ClearTag()
        {
            if (m_TagPossible != null) m_TagPossible.value = false;
            if (m_TagImpossible != null) m_TagImpossible.value = false;
            if (m_TagPartly != null) m_TagPartly.value = false;
            if (m_TagPossible == null && m_TagImpossible == null && m_TagPartly == null && m_TagGroup != null)
                m_TagGroup.value = -1;
        }

        void OnRowWritten(StudyLogRow row)
        {
            if (row == null) return;

            m_Recent.Insert(0, FormatRow(row));
            if (m_Recent.Count > k_RecentRows) m_Recent.RemoveRange(k_RecentRows, m_Recent.Count - k_RecentRows);
            RebuildRecentList();

            if ((row.Source == ChangeSource.Facilitator || row.Source == ChangeSource.Participant) &&
                row.Action != StudyActions.SessionStart && row.Action != StudyActions.SessionStop)
                m_LastChange = $"{SourceName(row.Source)} {row.Action}";
            UpdateStatusLine();
        }

        static (string text, string cssClass) FormatRow(StudyLogRow row)
        {
            string time = row.Timestamp.ToString("HH:mm", k_Inv);
            switch (row.Source)
            {
                case ChangeSource.Request:
                    string tag = row.Tag != RequestTag.None ? $" [{row.Tag}]" : "";
                    return ($"{time} request \"{row.RequestText}\"{tag}", "dash-recent-row--request");
                case ChangeSource.Marker:
                    string note = string.IsNullOrEmpty(row.RequestText) ? "" : $" {row.RequestText}";
                    return ($"{time} marker{note}", "dash-recent-row--marker");
                default:
                    string value = string.IsNullOrEmpty(row.NewValue) ? "" : $" → {row.NewValue}";
                    return ($"{time} {SourceName(row.Source)} {row.Action}{value}", null);
            }
        }

        static string SourceName(ChangeSource source) => source.ToString().ToLowerInvariant();

        void RebuildRecentList()
        {
            if (m_RecentList == null) return;
            m_RecentList.Clear();
            if (m_Recent.Count == 0)
            {
                var empty = new Label("No rows yet");
                empty.AddToClassList("dash-recent-row");
                empty.AddToClassList("dash-note");
                m_RecentList.Add(empty);
                return;
            }
            foreach ((string text, string cssClass) in m_Recent)
            {
                var label = new Label(text);
                label.AddToClassList("dash-recent-row");
                if (cssClass != null) label.AddToClassList(cssClass);
                m_RecentList.Add(label);
            }
        }

        // ================================================================== status line

        /// <summary>
        /// Names participant changes while no session runs (the logger does this while logging).
        /// Changes made inside our own RunAsFacilitator are skipped; playback ticks are ignored.
        /// </summary>
        void TrackParticipantChange()
        {
            if (m_FacilitatorDepth > 0 || IsLogging) return;
            PlotSnapshot now = PlotSnapshot.Capture(m_Manager);
            if (m_LastSnapshot != null && PlotStateDiff.HasRelevantChange(m_LastSnapshot, now))
            {
                // The same naming the logger uses for participant rows (WP7), first change only.
                List<LayerChange> changes = PlotStateDiff.DescribeStack(m_LastSnapshot, now, DatasetOf);
                if (changes.Count > 0)
                {
                    LayerChange first = changes[0];
                    m_LastChange = string.IsNullOrEmpty(first.Layer)
                        ? $"participant {first.Action}"
                        : $"participant {first.Action} ({first.Layer})";
                }
                else
                {
                    m_LastChange = $"participant {StudyActions.Unknown}";
                }
            }
            m_LastSnapshot = now;
        }

        // Column names for DescribeStack: a layer's own dataset instance, else the registry's cached one.
        DatasetColumnar DatasetOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            foreach (PlotLayer layer in m_Manager.Layers)
                if (layer.Dataset != null && layer.DatasetName == fileName) return layer.Dataset;
            return DatasetRegistry.Get(fileName);
        }

        void Flash(string message)
        {
            m_Flash = message;
            m_FlashUntil = Time.unscaledTime + k_FlashSeconds;
            UpdateStatusLine();
        }

        void UpdateStatusLine()
        {
            if (m_StatusLine == null || m_Manager == null) return;

            DatasetColumnar dataset = m_Manager.LoadedDataset;
            string rows;
            if (dataset == null) rows = "No dataset loaded";
            else if (m_Manager.Filters.Count == 0) rows = $"{dataset.RowCount.ToString("N0", k_Inv)} rows shown";
            else rows = $"{dataset.RowCount.ToString("N0", k_Inv)} rows (filtered)"; // filtering itself stays in the renderer

            int layerCount = m_Manager.Layers.Count;
            if (layerCount != 1) rows = $"{rows} · {layerCount.ToString(k_Inv)} layers";
            string warning = FirstLayerWarning();
            if (warning != null) rows = $"{rows} · ⚠ {warning}";

            string text = m_LastChange != null ? $"{rows} · last change: {m_LastChange}" : rows;
            if (m_Flash != null)
            {
                if (Time.unscaledTime < m_FlashUntil) text = $"{text} · {m_Flash}";
                else m_Flash = null;
            }
            if (m_StatusLine.text != text) m_StatusLine.text = text;
        }

        // "<layer>: <first line of its warning>" for the first layer with one, else null.
        string FirstLayerWarning()
        {
            PlotFrame frame = m_Visualizer != null ? m_Visualizer.LastFrame : null;
            LayerLineRenderer lines = LineRenderer();
            foreach (PlotLayer layer in m_Manager.Layers)
            {
                string warning = DashboardLayersSection.WarningFor(layer, m_Manager.Stack, frame, lines);
                if (warning.Length == 0) continue;
                int newline = warning.IndexOf('\n');
                return $"{layer.Name}: {(newline >= 0 ? warning.Substring(0, newline) : warning)}";
            }
            return null;
        }

        // ================================================================== toasts

        /// <summary>
        /// Shows a toast at the top of the dashboard: newest on top, at most 4 visible (older ones
        /// wait), gone after 6 s (Info) / 12 s (Warning) / 20 s (Error) of being visible, or on click.
        /// Also shown while the dashboard is minimised (below the tab).
        /// </summary>
        public void ShowToast(string text, ToastKind kind)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (m_ToastArea == null)
            {
                Flash(text); // older layout without a toast area
                return;
            }

            string shown = text.Length > k_MaxToastChars ? text.Substring(0, k_MaxToastChars) + "…" : text;
            var element = new VisualElement { pickingMode = PickingMode.Position, tooltip = "Click to dismiss" };
            element.AddToClassList("dash-toast");
            element.AddToClassList(ToastClass(kind));
            var label = new Label(shown) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("dash-toast__text");
            element.Add(label);

            var toast = new Toast { Element = element, Remaining = ToastSeconds(kind) };
            element.RegisterCallback<ClickEvent>(_ => DismissToast(toast));

            m_Toasts.Insert(0, toast);
            m_ToastArea.Insert(0, element);
            while (m_Toasts.Count > k_MaxQueuedToasts)
                DismissToast(m_Toasts[m_Toasts.Count - 1], relayout: false);
            LayoutToasts();
        }

        static string ToastClass(ToastKind kind)
        {
            switch (kind)
            {
                case ToastKind.Warning: return "dash-toast--warning";
                case ToastKind.Error: return "dash-toast--error";
                default: return "dash-toast--info";
            }
        }

        static float ToastSeconds(ToastKind kind)
        {
            switch (kind)
            {
                case ToastKind.Warning: return 12f;
                case ToastKind.Error: return 20f;
                default: return 6f;
            }
        }

        void DismissToast(Toast toast, bool relayout = true)
        {
            if (toast == null || !m_Toasts.Remove(toast)) return;
            toast.Element.RemoveFromHierarchy();
            if (relayout) LayoutToasts();
        }

        void LayoutToasts()
        {
            for (int i = 0; i < m_Toasts.Count; i++)
                m_Toasts[i].Element.style.display = i < k_MaxVisibleToasts ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void UpdateToasts()
        {
            if (m_Toasts.Count == 0) return;
            float dt = Time.unscaledDeltaTime;
            List<Toast> expired = null;
            int visible = Mathf.Min(m_Toasts.Count, k_MaxVisibleToasts);
            for (int i = 0; i < visible; i++)
            {
                Toast toast = m_Toasts[i];
                toast.Remaining -= dt;
                if (toast.Remaining <= 0f) (expired ??= new List<Toast>()).Add(toast);
            }
            if (expired == null) return;
            foreach (Toast toast in expired)
                DismissToast(toast, relayout: false);
            LayoutToasts();
        }

        // ================================================================== frame

        void ToggleMinimised()
        {
            if (m_Root == null) return;
            m_Root.ToggleInClassList(DashboardIds.MinimisedClass);
            bool minimised = m_Root.ClassListContains(DashboardIds.MinimisedClass);
            if (m_MinimiseButton != null)
            {
                m_MinimiseButton.text = minimised ? "Dashboard" : "–";
                m_MinimiseButton.tooltip = minimised ? "Show dashboard (F1)" : "Minimise (F1)";
            }
        }
    }

    /// <summary>
    /// The dashboard's undo and redo stacks of whole-plot snapshots (LFR-29), apart from the UI so
    /// edit-mode tests can drive it. A new facilitator change clears redo; undo moves the current
    /// state to redo and back. Restoring is the caller's (it runs inside RunAsFacilitator).
    /// </summary>
    public sealed class DashboardUndoHistory
    {
        readonly int m_Cap;
        readonly List<PlotSnapshot> m_Undo = new();
        readonly List<PlotSnapshot> m_Redo = new();

        public DashboardUndoHistory(int cap) => m_Cap = Math.Max(1, cap);

        public int UndoCount => m_Undo.Count;
        public int RedoCount => m_Redo.Count;

        /// <summary>The state before a facilitator change. clearRedo false only for a slider drag that may still end where it began.</summary>
        public void Push(PlotSnapshot before, bool clearRedo = true)
        {
            if (before == null) return;
            m_Undo.Add(before);
            if (m_Undo.Count > m_Cap) m_Undo.RemoveAt(0);
            if (clearRedo) m_Redo.Clear();
        }

        public void DropLastUndo()
        {
            if (m_Undo.Count > 0) m_Undo.RemoveAt(m_Undo.Count - 1);
        }

        public void ClearRedo() => m_Redo.Clear();

        /// <summary>Restores the last undo snapshot through <paramref name="restore"/>; when it returns false nothing moves.</summary>
        public bool TryUndo(PlotSnapshot current, Func<PlotSnapshot, bool> restore) => Step(m_Undo, m_Redo, current, restore);

        /// <summary>Restores the last undone state; current goes back on the undo stack.</summary>
        public bool TryRedo(PlotSnapshot current, Func<PlotSnapshot, bool> restore) => Step(m_Redo, m_Undo, current, restore);

        bool Step(List<PlotSnapshot> from, List<PlotSnapshot> to, PlotSnapshot current, Func<PlotSnapshot, bool> restore)
        {
            if (from.Count == 0 || current == null || restore == null) return false;
            PlotSnapshot target = from[from.Count - 1];
            if (!restore(target)) return false;
            from.RemoveAt(from.Count - 1);
            to.Add(current);
            if (to.Count > m_Cap) to.RemoveAt(0);
            return true;
        }

        /// <summary>
        /// The first dataset file of a snapshot's dataset/subset layers that cannot be loaded here
        /// (DatasetRegistry.Get), or null. Replaces the old "Undo not possible after a dataset change":
        /// PlotSnapshot.WriteTo restores datasets itself now.
        /// </summary>
        public static string MissingDataset(PlotSnapshot snapshot)
        {
            if (snapshot?.Layers == null) return null;
            foreach (LayerSnapshot layer in snapshot.Layers)
            {
                if (layer == null || (LayerKind)layer.Kind == LayerKind.Computed || string.IsNullOrEmpty(layer.DatasetName)) continue;
                if (DatasetRegistry.Get(layer.DatasetName) == null) return layer.DatasetName;
            }
            return null;
        }
    }
}
