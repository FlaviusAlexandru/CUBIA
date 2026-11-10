using System;

namespace DataViz.Study
{
    // ---------------------------------------------------------------------------------------------
    // Shared contract for the WAM+IA facilitator dashboard (spec: "Spec Sheet - CUBIA Facilitator
    // Dashboard"). Written first so the UI, controller and logger can be built in parallel.
    // Everything here is plain C#: no MonoBehaviours, no Unity UI types.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Who caused a log row.</summary>
    public enum ChangeSource
    {
        Facilitator, // a change made from the dashboard
        Participant, // a change made from CUBIA's VR menu (detected by diffing plot snapshots)
        Request,     // a typed request note from the facilitator
        Marker,      // F9 / "Mark moment"
    }

    /// <summary>Tag on a request row: could CUBIA do what the participant asked?</summary>
    public enum RequestTag
    {
        None,
        Possible,
        Impossible,
        Partly,
    }

    /// <summary>One row of the session CSV. Column order = <see cref="Header"/>.</summary>
    public sealed class StudyLogRow
    {
        public static readonly string[] Header =
        {
            "Timestamp", "SessionTime", "ParticipantID", "Part", "Source", "Action",
            "OldValue", "NewValue", "RequestText", "Tag", "PlotState",
        };

        public DateTime Timestamp;   // local time, written as yyyy-MM-ddTHH:mm:ss.fff
        public double SessionTime;   // seconds since StartSession, written with 2 decimals
        public string ParticipantId = "";
        public int Part;
        public ChangeSource Source;
        public string Action = "";   // see StudyActions
        public string OldValue = "";
        public string NewValue = "";
        public string RequestText = "";
        public RequestTag Tag = RequestTag.None; // written as "" when None
        public string PlotState = ""; // compact JSON, see PlotStateJson

        // The layer a layer-scoped row is about (its Name); "" for plot-level rows. Not yet in
        // Header/FormatRow: WP7 adds the column after Action (decision E2).
        public string Layer = "";
    }

    /// <summary>Action names used in the Action column. Use these constants, never literals.</summary>
    public static class StudyActions
    {
        public const string SetX = "SetX";
        public const string SetY = "SetY";
        public const string SetZ = "SetZ";
        public const string SetColor = "SetColor";
        public const string SetSize = "SetSize";           // size column
        public const string SetSizeMapped = "SetSizeMapped";
        public const string SetTime = "SetTime";
        public const string SetGlyphColumn = "SetGlyphColumn";
        public const string AssignGlyph = "AssignGlyph";   // NewValue = "label=shape"
        public const string SetGlyphRanges = "SetGlyphRanges";
        public const string SetPointSize = "SetPointSize";
        public const string SetFilterColumn = "SetFilterColumn";
        public const string SetFilterValue = "SetFilterValue";
        public const string SetFilterRanges = "SetFilterRanges";
        public const string AddFilter = "AddFilter";
        public const string RemoveFilter = "RemoveFilter";
        public const string ClearFilters = "ClearFilters";
        public const string Scrub = "Scrub";
        public const string Play = "Play";
        public const string Pause = "Pause";
        public const string SetPlaybackRate = "SetPlaybackRate"; // OldValue/NewValue = rate, e.g. "1" -> "0.5"
        public const string Tooltips = "Tooltips";
        public const string ResetPosition = "ResetPosition";
        public const string Shuffle = "Shuffle";
        public const string LoadDataset = "LoadDataset";
        public const string ApplyView = "ApplyView";       // prepared view; NewValue = view title
        public const string Undo = "Undo";
        public const string Note = "Note";
        public const string Marker = "Marker";
        public const string SessionStart = "SessionStart";
        public const string SessionStop = "SessionStop";
        public const string Unknown = "Change";            // participant change that could not be named

        // Layers (LFR-28). Each value equals its name. Layer-scoped rows name the layer in StudyLogRow.Layer.
        public const string AddLayer = "AddLayer";         // NewValue = "<Kind>: <dataset, or computed kind of source>"
        public const string RemoveLayer = "RemoveLayer";
        public const string DuplicateLayer = "DuplicateLayer";
        public const string RenameLayer = "RenameLayer";
        public const string ReorderLayer = "ReorderLayer"; // 1-based positions
        public const string SelectLayer = "SelectLayer";   // the active layer changed; values are layer names
        public const string SetLayerVisible = "SetLayerVisible";
        public const string SetLayerNormalised = "SetLayerNormalised";
        public const string SetLayerOpacity = "SetLayerOpacity";
        public const string SetLayerColour = "SetLayerColour";
        public const string SetLayerGlyph = "SetLayerGlyph";
        public const string SetLayerTimeBehaviour = "SetLayerTimeBehaviour";
        public const string SetLayerEndTime = "SetLayerEndTime";
        public const string SetLayerLabel = "SetLayerLabel";
        public const string SetSubsetHiddenInSource = "SetSubsetHiddenInSource";
        public const string SetComputedParam = "SetComputedParam";
        public const string Redo = "Redo";
    }

    /// <summary>
    /// The session logger (implemented by StudySessionLogger, WP3). The dashboard controller (WP2)
    /// only talks to the logger through this interface.
    ///
    /// Attribution: every dashboard change goes through <see cref="RunAsFacilitator"/>. Any
    /// MultiplayerScatterplotManager.OnPlotSettingsChanged that fires outside that call is treated
    /// as a participant change; the logger diffs the new PlotSnapshot against its last one to name
    /// it (TimeScrub changes during playback are ignored).
    /// </summary>
    public interface IStudySessionLogger
    {
        bool IsLogging { get; }
        string ParticipantId { get; }
        int Part { get; }
        /// <summary>Seconds since StartSession (0 when not logging).</summary>
        double SessionTime { get; }

        /// <summary>Folder the CSV files go to. Persisted in PlayerPrefs (key "CUBIA.Study.LogFolder").</summary>
        string LogFolder { get; }
        /// <summary>Full path of the CSV being written, or null when not logging.</summary>
        string CurrentFilePath { get; }

        /// <summary>Raised after every row is written (and flushed), on the main thread.</summary>
        event Action<StudyLogRow> RowWritten;
        /// <summary>Raised when a session starts or stops.</summary>
        event Action SessionStateChanged;

        /// <summary>Validates (creates if missing) and persists the folder. False + error if unusable.</summary>
        bool SetLogFolder(string folder, out string error);

        /// <summary>Opens P{id}_part{part}_{yyyyMMdd_HHmmss}.csv in LogFolder and writes the header + a SessionStart row.</summary>
        bool StartSession(string participantId, int part, out string error);
        /// <summary>Writes a SessionStop row and closes the file. No-op when not logging.</summary>
        void StopSession();

        /// <summary>
        /// Runs <paramref name="apply"/> (a manager call) attributed to the facilitator, then, if
        /// <paramref name="writeRow"/> and logging, writes a Facilitator row with the state after it.
        /// Pass writeRow=false for every intermediate step of a slider drag and true on release.
        /// Works (applies, without writing) when not logging.
        /// </summary>
        void RunAsFacilitator(string action, string oldValue, string newValue, Action apply, bool writeRow = true);

        /// <summary>
        /// RunAsFacilitator for a layer-scoped change: <paramref name="layer"/> is the layer's Name
        /// for the Layer column ("" for a plot-level change). The overload without it uses the edit
        /// layer's name, captured before apply runs.
        /// </summary>
        void RunAsFacilitator(string action, string layer, string oldValue, string newValue, Action apply, bool writeRow = true);

        /// <summary>Writes a facilitator row without running anything (e.g. after an undo).</summary>
        void LogFacilitatorAction(string action, string oldValue, string newValue);

        /// <summary>LogFacilitatorAction with the layer's Name for the Layer column ("" = plot-level).</summary>
        void LogFacilitatorAction(string action, string layer, string oldValue, string newValue);
        void LogRequest(string text, RequestTag tag);
        void LogMarker(string note = "");
    }
}
