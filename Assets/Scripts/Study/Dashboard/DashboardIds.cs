namespace DataViz.Study
{
    /// <summary>
    /// Element names in FacilitatorDashboard.uxml (WP1) that DashboardController (WP2) queries with
    /// root.Q&lt;T&gt;(name). Types in brackets are the UI Toolkit element types to use.
    /// </summary>
    public static class DashboardIds
    {
        // Frame
        public const string Root = "dash-root";                     // VisualElement (docked right, ~34% width)
        public const string Body = "dash-body";                     // ScrollView (everything below the title bar)
        public const string MinimiseButton = "dash-minimise";       // Button ("–" / "Dashboard" when minimised)
        public const string MinimisedClass = "dash--minimised";     // USS class toggled on Root
        public const string ToastArea = "dash-toasts";              // VisualElement (picking-mode Ignore; toasts added by DashboardController.ShowToast)

        // Session
        public const string ParticipantField = "session-participant"; // TextField
        public const string PartField = "session-part";               // DropdownField ("1", "2")
        public const string SessionButton = "session-toggle";         // Button ("Start logging" / "Stop logging")
        public const string SessionTime = "session-time";             // Label ("00:00")
        public const string LogFolderField = "session-folder";        // TextField
        public const string LogFolderApply = "session-folder-apply";  // Button ("Set")
        public const string SessionStatus = "session-status";         // Label (file name or error)

        // Dataset
        public const string DatasetDropdown = "dataset-list";      // DropdownField
        public const string DatasetRescan = "dataset-rescan";      // Button
        public const string DatasetLoad = "dataset-load";          // Button

        // Mappings (DropdownField; first choice is "None" where the manager allows -1)
        public const string XDropdown = "map-x";
        public const string YDropdown = "map-y";
        public const string ZDropdown = "map-z";
        public const string ColorDropdown = "map-color";           // "None" + columns
        public const string SizeDropdown = "map-size";             // "None" + columns (None = SizeMapped false)
        public const string TimeDropdown = "map-time";             // "None" + columns
        public const string GlyphColumnDropdown = "map-glyph";     // "None" + columns
        public const string PointSizeSlider = "map-pointsize";     // Slider 0..MaxPointSize

        // Glyph assignment
        public const string GlyphLabelDropdown = "glyph-label";    // DropdownField (labels of glyph column)
        public const string GlyphShapeDropdown = "glyph-shape";    // DropdownField (ScatterplotUI.k_GlyphShapeNames)
        public const string GlyphRanges = "glyph-ranges";          // VisualElement (DashboardRangeEditor builds the range sliders in it)

        // Filters (first filter entry; matches the VR menu's single filter row)
        public const string FilterColumnDropdown = "filter-column"; // "None" + columns
        public const string FilterValueDropdown = "filter-value";   // "Any value" + values of that column
        public const string FilterClear = "filter-clear";           // Button
        public const string FilterRanges = "filter-ranges";         // VisualElement (DashboardRangeEditor builds the range sliders in it)

        // Time
        public const string TimeScrubSlider = "time-scrub";        // Slider 0..1
        public const string PlayPauseButton = "time-play";         // Button ("Play" / "Pause")
        public const string PlaybackRateDropdown = "time-rate";    // DropdownField ("0.25×" … "2×", from PlaybackRateOptions)

        // View
        public const string TooltipsToggle = "view-tooltips";      // Toggle
        public const string ResetPositionButton = "view-reset";    // Button
        public const string UndoButton = "view-undo";              // Button ("Undo (Ctrl+Z)")
        public const string RedoButton = "view-redo";              // Button ("Redo (Ctrl+Y)"), built in code right after UndoButton

        // Layers (LFR-26): section built in code by DashboardLayersSection, inserted after the Dataset section
        public const string LayersSection = "layers-section";      // VisualElement (dash-section)

        // Request log
        public const string RequestText = "req-text";              // TextField (multiline off)
        public const string TagPossible = "req-tag-possible";      // RadioButton (in RadioButtonGroup "req-tags")
        public const string TagImpossible = "req-tag-impossible";
        public const string TagPartly = "req-tag-partly";
        public const string TagGroup = "req-tags";                 // RadioButtonGroup
        public const string LogButton = "req-log";                 // Button ("Log (Enter)")
        public const string MarkerButton = "req-marker";           // Button ("Mark moment (F9)")
        public const string RecentList = "req-recent";             // VisualElement holding the last 10 rows as Labels

        // Status line
        public const string StatusLine = "status-line";            // Label ("1,234 rows shown · last change: participant, SetColor")
    }
}
