using System;
using System.Collections.Generic;
using System.IO;
using DataViz.Networking;
using UnityEngine;

namespace DataViz.Study
{
    /// <summary>
    /// Writes the WAM+IA session CSV. Dashboard changes are attributed to the facilitator by running
    /// them inside <see cref="RunAsFacilitator"/>; any other OnPlotSettingsChanged is a participant
    /// change, named by diffing PlotSnapshots (scrub-only changes during playback are ignored).
    /// Every row names its layer in the Layer column (LFR-28); column names are taken from each
    /// layer's own dataset (see DatasetOf).
    /// IO errors never reach the caller: they come back as false + message, or stop the session.
    /// </summary>
    public sealed class StudySessionLogger : MonoBehaviour, IStudySessionLogger
    {
        public const string LogFolderPrefsKey = "CUBIA.Study.LogFolder";

        MultiplayerScatterplotManager m_Manager;
        PlotSnapshot m_Last;
        StudyLogWriter m_Writer;
        double m_StartTime;
        int m_FacilitatorDepth; // a counter, so a nested RunAsFacilitator does not end the outer scope early
        bool m_WriteErrorReported;

        public bool IsLogging => m_Writer != null;
        public string ParticipantId { get; private set; } = "";
        public int Part { get; private set; }
        public double SessionTime => IsLogging ? Math.Max(0.0, Time.realtimeSinceStartupAsDouble - m_StartTime) : 0.0;
        public string CurrentFilePath => m_Writer?.FilePath;

        /// <summary>Last error that stopped a session (e.g. disk full); null when none. For the UI's status line.</summary>
        public string LastError { get; private set; }

        public event Action<StudyLogRow> RowWritten;
        public event Action SessionStateChanged;

        static string DefaultLogFolder => Path.Combine(Application.persistentDataPath, "StudyLogs");

        /// <summary>When set, used instead of the persisted folder (not saved). For the editor self-tests, which must not change the facilitator's setting.</summary>
        public string LogFolderOverride { get; set; }

        public string LogFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(LogFolderOverride))
                    return LogFolderOverride;
                string stored = PlayerPrefs.GetString(LogFolderPrefsKey, "");
                return string.IsNullOrWhiteSpace(stored) ? DefaultLogFolder : stored;
            }
        }

        bool InFacilitatorChange => m_FacilitatorDepth > 0;

        // ---- setup ------------------------------------------------------------------------

        public void Initialise(MultiplayerScatterplotManager manager)
        {
            Unsubscribe();
            m_Manager = manager;
            if (m_Manager == null)
            {
                Debug.LogWarning("[StudySessionLogger] Initialise called without a manager; participant changes will not be logged.");
                m_Last = null;
                return;
            }

            m_Manager.OnPlotSettingsChanged += HandlePlotSettingsChanged;
            m_Manager.OnDatasetLoaded += HandleDatasetLoaded;
            m_Last = Capture();
        }

        void Unsubscribe()
        {
            if (m_Manager == null)
                return;
            m_Manager.OnPlotSettingsChanged -= HandlePlotSettingsChanged;
            m_Manager.OnDatasetLoaded -= HandleDatasetLoaded;
        }

        void OnDestroy()
        {
            StopSession();
            Unsubscribe();
            m_Manager = null;
        }

        void OnApplicationQuit() => StopSession();

        // ---- folder -----------------------------------------------------------------------

        public bool SetLogFolder(string folder, out string error)
        {
            error = null;
            if (IsLogging)
            {
                error = "Stop logging first";
                return false;
            }

            folder = folder?.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                error = "Folder is empty";
                return false;
            }

            if (!TryPrepareFolder(folder, true, out error))
                return false;

            PlayerPrefs.SetString(LogFolderPrefsKey, folder);
            PlayerPrefs.Save();
            return true;
        }

        static bool TryPrepareFolder(string folder, bool probe, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(folder);
                if (probe)
                {
                    // CreateDirectory succeeds on read-only folders; only a real write proves it is usable.
                    string probePath = Path.Combine(folder, ".cubia_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probePath, "probe");
                    File.Delete(probePath);
                }
                return true;
            }
            catch (Exception e)
            {
                error = "Folder not usable: " + e.Message;
                return false;
            }
        }

        // ---- session ----------------------------------------------------------------------

        public bool StartSession(string participantId, int part, out string error)
        {
            error = null;
            string id = participantId?.Trim() ?? "";
            if (id.Length == 0)
            {
                error = "Enter a participant ID";
                return false;
            }
            if (part != 1 && part != 2)
            {
                error = "Part must be 1 or 2";
                return false;
            }

            if (IsLogging)
                StopSession();

            string folder = LogFolder;
            if (!TryPrepareFolder(folder, false, out error))
                return false;

            string path = Path.Combine(folder, StudyLogWriter.BuildFileName(id, part, DateTime.Now));
            try
            {
                m_Writer = new StudyLogWriter(path);
            }
            catch (Exception e)
            {
                m_Writer = null;
                error = "Could not open log file: " + e.Message;
                return false;
            }

            ParticipantId = id;
            Part = part;
            m_StartTime = Time.realtimeSinceStartupAsDouble;
            m_WriteErrorReported = false;
            LastError = null;
            m_Last = Capture(); // changes before the session are not part of it

            WriteRow(ChangeSource.Facilitator, StudyActions.SessionStart, "", "", Path.GetFileName(path), "", RequestTag.None);
            if (!IsLogging)
            {
                // The very first write failed; WriteRow already stopped and notified.
                error = LastError ?? "Could not write to log file";
                return false;
            }

            RaiseSessionStateChanged();
            return true;
        }

        public void StopSession()
        {
            if (!IsLogging)
                return;

            WriteRow(ChangeSource.Facilitator, StudyActions.SessionStop, "", "", "", "", RequestTag.None);
            if (!IsLogging)
                return; // the write failed: WriteRow already closed the file and notified

            CloseWriter();
            RaiseSessionStateChanged();
        }

        void CloseWriter()
        {
            StudyLogWriter writer = m_Writer;
            m_Writer = null;
            try
            {
                writer?.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StudySessionLogger] Closing the log file failed: " + e.Message);
            }
        }

        // ---- facilitator ------------------------------------------------------------------

        /// <summary>Layer column: the edit layer's name, captured before apply runs, or "" for a plot-level action.</summary>
        public void RunAsFacilitator(string action, string oldValue, string newValue, Action apply, bool writeRow = true) =>
            RunAsFacilitator(action, DefaultLayer(action), oldValue, newValue, apply, writeRow);

        /// <summary>layer: the Layer column ("" = plot-level; null = the active layer's name after apply ran, e.g. for AddLayer).</summary>
        public void RunAsFacilitator(string action, string layer, string oldValue, string newValue, Action apply, bool writeRow = true)
        {
            m_FacilitatorDepth++;
            try
            {
                apply?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                m_FacilitatorDepth--;
            }

            // Re-baseline so this change is not later attributed to the participant.
            m_Last = Capture();

            if (writeRow && IsLogging)
                WriteRow(ChangeSource.Facilitator, action, layer ?? ActiveLayerName(), oldValue, newValue, "", RequestTag.None);
        }

        /// <summary>Layer column as in the RunAsFacilitator overload without a layer.</summary>
        public void LogFacilitatorAction(string action, string oldValue, string newValue) =>
            LogFacilitatorAction(action, DefaultLayer(action), oldValue, newValue);

        /// <summary>layer: the Layer column ("" = plot-level; null = the active layer's name).</summary>
        public void LogFacilitatorAction(string action, string layer, string oldValue, string newValue)
        {
            m_Last = Capture();
            if (IsLogging)
                WriteRow(ChangeSource.Facilitator, action, layer ?? ActiveLayerName(), oldValue, newValue, "", RequestTag.None);
        }

        // The edit layer is what a legacy request acts on (a remove then changes the active layer,
        // so this is read before apply runs).
        string DefaultLayer(string action)
        {
            if (StudyActions.IsPlotLevel(action) || m_Manager == null || !m_Manager.HasEditLayer)
                return "";
            return m_Manager.EditLayer.Name ?? "";
        }

        string ActiveLayerName() => m_Manager != null ? m_Manager.ActiveLayer?.Name ?? "" : "";

        public void LogRequest(string text, RequestTag tag)
        {
            if (string.IsNullOrWhiteSpace(text) || !IsLogging)
                return;
            WriteRow(ChangeSource.Request, StudyActions.Note, "", "", "", text, tag);
        }

        public void LogMarker(string note = "")
        {
            if (!IsLogging)
                return;
            WriteRow(ChangeSource.Marker, StudyActions.Marker, "", "", "", note ?? "", RequestTag.None);
        }

        // ---- participant detection --------------------------------------------------------

        // Raised on every manager change, and every frame during playback (scrub only).
        void HandlePlotSettingsChanged()
        {
            if (InFacilitatorChange || m_Manager == null)
                return;

            PlotSnapshot current = Capture();
            if (!PlotStateDiff.HasRelevantChange(m_Last, current))
            {
                m_Last = current;
                return;
            }

            if (IsLogging)
            {
                List<LayerChange> changes = PlotStateDiff.DescribeStack(m_Last, current, DatasetOf);
                string state = PlotStateJson.Write(current, DatasetOf);
                if (changes.Count == 0)
                {
                    WriteRow(ChangeSource.Participant, StudyActions.Unknown, "", "", "", "", RequestTag.None, state);
                }
                else
                {
                    foreach (LayerChange change in changes)
                    {
                        if (!IsLogging)
                            break; // a write failed mid-way
                        WriteRow(ChangeSource.Participant, change.Action, change.Layer, change.OldValue, change.NewValue, "", RequestTag.None, state);
                    }
                }
            }

            m_Last = current;
        }

        // The manager raises OnPlotSettingsChanged right after this, which names the load.
        void HandleDatasetLoaded()
        {
            if (InFacilitatorChange)
                m_Last = Capture();
        }

        // ---- rows -------------------------------------------------------------------------

        PlotSnapshot Capture() => m_Manager != null ? PlotSnapshot.Capture(m_Manager) : null;

        /// <summary>
        /// The loaded dataset for a layer's file name, for column names: the instance a layer of the
        /// plot uses, else DatasetRegistry (cached; null when the file is not there). A removed
        /// layer's dataset is never needed (its row names only the file).
        /// </summary>
        DatasetColumnar DatasetOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            if (m_Manager != null)
            {
                foreach (PlotLayer layer in m_Manager.Layers)
                {
                    if (layer.Dataset != null && layer.DatasetName == fileName)
                        return layer.Dataset;
                }
            }
            return DatasetRegistry.Get(fileName);
        }

        void WriteRow(ChangeSource source, string action, string layer, string oldValue, string newValue,
                      string requestText, RequestTag tag, string plotState = null)
        {
            if (m_Writer == null)
                return;

            var row = new StudyLogRow
            {
                Timestamp = DateTime.Now,
                SessionTime = SessionTime,
                ParticipantId = ParticipantId,
                Part = Part,
                Source = source,
                Action = action ?? "",
                Layer = layer ?? "",
                OldValue = oldValue ?? "",
                NewValue = newValue ?? "",
                RequestText = requestText ?? "",
                Tag = tag,
                PlotState = plotState ?? CurrentPlotState(),
            };

            try
            {
                m_Writer.WriteRow(row);
            }
            catch (Exception e)
            {
                // Disk full, file locked, folder removed...: stop instead of failing every frame.
                LastError = "Logging stopped: " + e.Message;
                if (!m_WriteErrorReported)
                {
                    m_WriteErrorReported = true;
                    Debug.LogError("[StudySessionLogger] Writing " + m_Writer.FilePath + " failed, session stopped: " + e);
                }
                CloseWriter();
                RaiseSessionStateChanged();
                return;
            }

            try
            {
                RowWritten?.Invoke(row);
            }
            catch (Exception e)
            {
                Debug.LogException(e); // a broken listener must not stop logging
            }
        }

        string CurrentPlotState()
        {
            if (m_Manager == null)
                return "";
            try
            {
                return PlotStateJson.Write(PlotSnapshot.Capture(m_Manager), DatasetOf);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return "";
            }
        }

        void RaiseSessionStateChanged()
        {
            try
            {
                SessionStateChanged?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
