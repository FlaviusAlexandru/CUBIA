using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DataViz.Networking;
using UnityEditor;
using UnityEngine;

namespace DataViz.Study
{
    /// <summary>WP7 (LFR-28, LNFR-06): the Layer column, DescribeStack, the layered PlotState JSON and the logger.</summary>
    public static class LayerLogSelfTests
    {
        // ---- fixtures --------------------------------------------------------------------------

        static DatasetColumnar Movement() => SelfTestKit.Dataset("movement.cdataset",
            SelfTestKit.Col.Num("Time", 0, 1, 2), SelfTestKit.Col.Num("CX", 1, 2, 3),
            SelfTestKit.Col.Num("CY", 1, 2, 3), SelfTestKit.Col.Num("CZ", 1, 2, 3));

        static DatasetColumnar Moles() => SelfTestKit.Dataset("moles.cdataset",
            SelfTestKit.Col.Num("SpawnTime", 0, 1), SelfTestKit.Col.Num("MoleX", 1, 2),
            SelfTestKit.Col.Num("MoleY", 1, 2), SelfTestKit.Col.Num("MoleZ", 1, 2), SelfTestKit.Col.Num("HitTime", 1, 2));

        static Func<string, DatasetColumnar> Resolver()
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            return name => name == "movement.cdataset" ? mv : name == "moles.cdataset" ? ml : null;
        }

        static LayerSnapshot Layer(string id, string name, string dataset, int x = 1, int y = 2, int z = 3) =>
            new LayerSnapshot { Id = id, Name = name, DatasetName = dataset, XColumn = x, YColumn = y, ZColumn = z, PointSize = 0.02f };

        static LayerSnapshot Computed(string id, string name, string source) =>
            new LayerSnapshot { Id = id, Name = name, Kind = (byte)LayerKind.Computed, SourceLayerId = source, ComputedKind = (byte)ComputedLayerKind.Path };

        static LayerSnapshot Copy(LayerSnapshot s) => LayerSnapshot.From(s.ToLayer());

        static PlotSnapshot Snap(string active, params LayerSnapshot[] layers) =>
            new PlotSnapshot { Layers = layers, ActiveLayerId = active, NextLayerNumber = 9, PlaybackSpeed = 0.25f };

        // Two layers: L1 movement (active) and L2 moles.
        static PlotSnapshot Two(string active = "L1") =>
            Snap(active, Layer("L1", "movement", "movement.cdataset"), Layer("L2", "moles", "moles.cdataset"));

        static PlotSnapshot WithLayer(PlotSnapshot s, string id, Action<LayerSnapshot> change)
        {
            var copy = new PlotSnapshot
            {
                Layers = Array.ConvertAll(s.Layers, Copy), ActiveLayerId = s.ActiveLayerId, NextLayerNumber = s.NextLayerNumber,
                PlaybackSpeed = s.PlaybackSpeed, PlaybackRate = s.PlaybackRate, IsPlaying = s.IsPlaying, TimeScrub = s.TimeScrub,
            };
            change(copy.FindLayer(id));
            return copy;
        }

        static string Diff(PlotSnapshot a, PlotSnapshot b) => string.Join(",", PlotStateDiff.DescribeStack(a, b, Resolver()));

        // ---- CSV column ------------------------------------------------------------------------

        [SelfTest("Log: Layer column after Action")]
        static void LayerColumn(SelfTestTally t)
        {
            t.Eq("header index", Array.IndexOf(StudyLogRow.Header, "Action") + 1, Array.IndexOf(StudyLogRow.Header, "Layer"));
            t.Eq("header count", 12, StudyLogRow.Header.Length);
            var row = new StudyLogRow
            {
                Timestamp = new DateTime(2026, 11, 10, 9, 0, 0, 5), SessionTime = 1, ParticipantId = "P01", Part = 2,
                Source = ChangeSource.Participant, Action = StudyActions.SetLayerOpacity, Layer = "moles, part 2", OldValue = "1", NewValue = "0.5",
            };
            t.Eq("row", "2026-11-10T09:00:00.005,1.00,P01,2,participant,SetLayerOpacity,\"moles, part 2\",1,0.5,,,", StudyLogWriter.FormatRow(row));
            t.True("plot-level", StudyActions.IsPlotLevel(StudyActions.Play) && StudyActions.IsPlotLevel(StudyActions.LoadDataset) &&
                                  StudyActions.IsPlotLevel(StudyActions.Redo) && StudyActions.IsPlotLevel(StudyActions.SessionStop) &&
                                  StudyActions.IsPlotLevel(StudyActions.Shuffle)); // Shuffle can change several layers
            t.True("layer-scoped", !StudyActions.IsPlotLevel(StudyActions.SetX) && !StudyActions.IsPlotLevel(StudyActions.AddLayer) &&
                                    !StudyActions.IsPlotLevel(StudyActions.SetLayerShuffleUnlocked) && !StudyActions.IsPlotLevel(null));
        }

        // ---- DescribeStack -----------------------------------------------------------------------

        [SelfTest("Log: DescribeStack structure")]
        static void DescribeStructure(SelfTestTally t)
        {
            PlotSnapshot two = Two();
            t.Eq("identical", "", Diff(two, Two()));
            t.True("identical not relevant", !PlotStateDiff.HasRelevantChange(two, Two()));

            PlotSnapshot playing = WithLayer(two, "L1", _ => { });
            playing.IsPlaying = true;
            PlotSnapshot scrubbed = WithLayer(playing, "L2", l => l.DatasetHash = "abc");
            scrubbed.TimeScrub = 0.5f; scrubbed.ScrubServerTime = 3.0; scrubbed.PlaybackSpeed = 1f; scrubbed.ScrubPerSecond = 2f;
            t.Eq("scrub only", "", Diff(playing, scrubbed));
            t.True("scrub only not relevant", !PlotStateDiff.HasRelevantChange(playing, scrubbed));
            t.Eq("play", "Play[](->)", Diff(two, playing));

            PlotSnapshot one = Snap("L1", Layer("L1", "movement", "movement.cdataset"));
            t.Eq("add", "AddLayer[moles](->Dataset: moles.cdataset)", Diff(one, Two("L2")));
            t.Eq("remove", "RemoveLayer[moles](Dataset: moles.cdataset->)", Diff(Two("L2"), one));
            t.Eq("remove last", "RemoveLayer[movement](Dataset: movement.cdataset->)", Diff(one, Snap("")));
            t.Eq("add to empty", "AddLayer[movement](->Dataset: movement.cdataset)", Diff(Snap(""), one));

            PlotSnapshot swapped = Snap("L2", Layer("L2", "moles", "moles.cdataset"), Layer("L1", "movement", "movement.cdataset"));
            t.Eq("reorder", "ReorderLayer[moles](2->1)", Diff(Two("L2"), swapped));
            t.Eq("reorder active first", "ReorderLayer[movement](1->2)", Diff(Two("L1"), WithLayer(swapped, "L1", _ => { }).Also(s => s.ActiveLayerId = "L1")));

            t.Eq("select", "SelectLayer[moles](movement->moles)", Diff(Two("L1"), Two("L2")));
            t.Eq("rename", "RenameLayer[targets](moles->targets)", Diff(two, WithLayer(two, "L2", l => l.Name = "targets")));

            PlotSnapshot dup = Snap("L3", Layer("L1", "movement", "movement.cdataset"), Layer("L2", "moles", "moles.cdataset"),
                                    Layer("L3", "moles copy", "moles.cdataset"));
            t.Eq("duplicate", "DuplicateLayer[moles copy](moles->moles copy)", Diff(Two("L2"), dup));

            PlotSnapshot newPlot = Snap("L1", Layer("L1", "moles", "moles.cdataset", 0, 1, 2));
            t.Eq("new plot", "LoadDataset[](movement.cdataset->moles.cdataset)", Diff(Two("L2"), newPlot));
            t.Eq("layer dataset swap", "LoadDataset[moles](moles.cdataset->other.cdataset)",
                Diff(two, WithLayer(two, "L2", l => { l.DatasetName = "other.cdataset"; l.XColumn = 0; })));

            // Removing a computed layer's source freezes it (copies the source's mapping): only the remove is named.
            PlotSnapshot withPath = Snap("L3", Layer("L1", "movement", "movement.cdataset"), Layer("L2", "moles", "moles.cdataset"),
                                         Computed("L3", "Path of moles", "L2"));
            LayerSnapshot frozen = Computed("L3", "Path of moles", "L2");
            frozen.DatasetName = "moles.cdataset"; frozen.XColumn = 1; frozen.YColumn = 2; frozen.ZColumn = 3;
            t.Eq("freeze", "RemoveLayer[moles](Dataset: moles.cdataset->)", Diff(withPath, Snap("L3", Layer("L1", "movement", "movement.cdataset"), frozen)));
            t.Eq("add computed", "AddLayer[Path of moles](->Computed: Path of moles)", Diff(Two("L2"), withPath));
        }

        [SelfTest("Log: DescribeStack layer fields")]
        static void DescribeFields(SelfTestTally t)
        {
            PlotSnapshot two = Two(); // L1 active, so every L2 change is on a non-active layer
            string F(Action<LayerSnapshot> change) => Diff(two, WithLayer(two, "L2", change));

            t.Eq("non-active X uses its own dataset", "SetX[moles](MoleX->HitTime)", F(l => l.XColumn = 4));
            t.Eq("active X", "SetX[movement](CX->Time)", Diff(two, WithLayer(two, "L1", l => l.XColumn = 0)));
            t.Eq("non-active filter", "SetFilterColumn[moles](None->SpawnTime)",
                F(l => { l.FilterColumns = new[] { 0 }; l.FilterLabels = new[] { "" }; l.FilterRangeCounts = new[] { 0 }; }));
            t.Eq("visible", "SetLayerVisible[moles](True->False)", F(l => l.Visible = false));
            t.Eq("normalised", "SetLayerNormalised[moles](False->True)", F(l => l.Normalised = true));
            t.Eq("opacity", "SetLayerOpacity[moles](1->0.5)", F(l => l.Opacity = 0.5f));
            t.Eq("colour", "SetLayerColour[moles](#00FFFF->#FF800080)",
                F(l => l.FixedColour = LayerSnapshot.PackColour(new Color32(255, 128, 0, 128))));
            t.Eq("glyph", "SetLayerGlyph[moles](0->2)", F(l => l.DefaultGlyph = 2));
            t.Eq("time behaviour", "SetLayerTimeBehaviour[moles](Follow->Span)", F(l => l.TimeBehaviour = (byte)LayerTimeBehaviour.Span));
            t.Eq("end time", "SetLayerEndTime[moles](None->HitTime)", F(l => l.EndTimeColumn = 4));
            t.Eq("label", "SetLayerLabel[moles](None->MoleX)", F(l => l.LabelColumn = 1));
            t.Eq("hide in source", "SetSubsetHiddenInSource[moles](False->True)", F(l => { l.Kind = (byte)LayerKind.Subset; l.HiddenInSource = true; }));
            t.Eq("line width", "SetLayerLineWidth[moles](0.006->0.01)", F(l => l.LineWidth = 0.01f));

            PlotSnapshot path = Snap("L1", Layer("L1", "movement", "movement.cdataset"), Layer("L2", "moles", "moles.cdataset"),
                                     Computed("L3", "Path of moles", "L2"));
            string C(Action<LayerSnapshot> change) => Diff(path, WithLayer(path, "L3", change));
            t.Eq("computed axis", "SetComputedParam[Path of moles](Axis=Auto->Axis=X)", C(l => l.DriverAxis = 0));
            t.Eq("computed order uses source dataset", "SetComputedParam[Path of moles](Order=Auto->Order=HitTime)", C(l => l.OrderColumn = 4));
            t.Eq("computed flag", "SetComputedParam[Path of moles](Smoothed=False->Smoothed=True)", C(l => l.Smoothed = true));
            t.Eq("computed value", "SetComputedParam[Path of moles](Value=0->Value=1.5)", C(l => l.RefValue = 1.5f));
            t.Eq("computed span", "SetComputedParam[Path of moles](Span=0.1->Span=0.25)", C(l => l.SmoothSpan = 0.25f));
            t.Eq("computed visible", "SetLayerVisible[Path of moles](True->False)", C(l => l.Visible = false));

            // Every changed field of a layer is named, in a fixed order, on the layer's current name.
            t.Eq("several", "RenameLayer[targets](moles->targets),SetY[targets](MoleY->SpawnTime),SetLayerOpacity[targets](1->0.3)",
                F(l => { l.Name = "targets"; l.YColumn = 0; l.Opacity = 0.3f; }));
            t.True("field relevant", PlotStateDiff.HasRelevantChange(two, WithLayer(two, "L2", l => l.LabelColumn = 1)));
        }

        // ---- JSON --------------------------------------------------------------------------------

        [SelfTest("Log: layered PlotState JSON")]
        static void LayeredJson(SelfTestTally t)
        {
            PlotSnapshot s = Snap("L2", Layer("L1", "movement", "movement.cdataset"), Layer("L2", "moles", "moles.cdataset"),
                                  Computed("L3", "Path \"of\" moles", "L2"));
            s.Layers[1].Opacity = 0.5f;
            s.Layers[1].LabelColumn = 4;
            s.Layers[2].OrderColumn = 4;
            s.TimeScrub = 0.25f;

            CultureInfo saved = CultureInfo.CurrentCulture;
            string json;
            try
            {
                try { CultureInfo.CurrentCulture = new CultureInfo("da-DK"); } catch { /* reported by the writer test */ }
                json = PlotStateJson.Write(s, Resolver());
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }

            t.True("json one line", json.IndexOf('\n') < 0 && json.IndexOf('\r') < 0);
            t.True("json top-level is the active layer", json.StartsWith("{\"ds\":\"moles.cdataset\",\"x\":\"MoleX\","));
            t.True("json active", json.Contains(",\"active\":\"L2\",\"layers\":[{\"id\":\"L1\""));
            t.Eq("json one object per layer", 3, Count(json, "{\"id\":"));
            t.True("json L1 own names", json.Contains("\"id\":\"L1\",\"name\":\"movement\",\"kind\":\"Dataset\",\"visible\":true,\"ds\":\"movement.cdataset\",\"x\":\"CX\""));
            t.True("json invariant opacity", json.Contains("\"opacity\":0.5,"));
            t.True("json invariant numbers", json.Contains("\"pointSize\":0.02,") && json.Contains("\"lineWidth\":0.006}") && json.Contains("\"scrub\":0.25,"));
            t.True("json label", json.Contains("\"label\":\"HitTime\""));
            t.True("json computed", json.Contains("\"name\":\"Path \\\"of\\\" moles\",\"kind\":\"Computed\"") &&
                                    json.Contains("\"src\":\"L2\"") && json.Contains("\"computed\":\"Path\",\"axis\":\"Auto\",\"order\":\"HitTime\""));
            t.True("json fixed colour", json.Contains("\"fixed\":\"#00FFFF\""));
            t.True("json ends", json.EndsWith("}]}"));
            t.Eq("json empty plot", "{\"ds\":\"\",\"x\":\"#0\",\"y\":\"#0\",\"z\":\"#0\",\"color\":null,\"size\":null,\"time\":null,\"glyph\":null,\"pointSize\":0,\"filters\":[],\"playing\":false,\"scrub\":0.00,\"active\":\"\",\"layers\":[]}",
                PlotStateJson.Write(new PlotSnapshot(), Resolver()));
            string legacy = PlotStateJson.Write(s, Moles());
            t.True("json starts with the legacy keys", json.StartsWith(legacy.Substring(0, legacy.Length - 1) + ",\"active\":"));
        }

        static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        // ---- logger with a manager ---------------------------------------------------------------

        [SelfTest("Log: session logger names layers")]
        static void LoggerRows(SelfTestTally t)
        {
            DatasetColumnar mv = Movement(), ml = Moles();
            string folder = Path.Combine(Path.GetTempPath(), "cubia_layerlog_" + Guid.NewGuid().ToString("N"));
            GameObject go = null;
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("movement.cdataset");
                string baseName = m.Layers[0].Name;

                go = EditorUtility.CreateGameObjectWithHideFlags("SelfTest Logger", HideFlags.HideAndDontSave);
                var logger = go.AddComponent<StudySessionLogger>();
                logger.LogFolderOverride = folder;
                logger.Initialise(m);
                bool started = logger.StartSession("P99", 1, out string error);
                t.True("logger started " + error, started);
                if (!started)
                    return;

                var rows = new List<StudyLogRow>();
                logger.RowWritten += rows.Add;
                string Last() => rows.Count == 0 ? "none" : $"{StudyLogWriter.SourceName(rows[rows.Count - 1].Source)}:{rows[rows.Count - 1].Action}[{rows[rows.Count - 1].Layer}]({rows[rows.Count - 1].OldValue}->{rows[rows.Count - 1].NewValue})";

                // Participant (VR) changes, found by the diff.
                m.RequestAddDatasetLayerRpc("moles.cdataset");
                string molesId = m.ActiveLayerId, molesName = m.ActiveLayer.Name;
                t.Eq("participant add", "participant:AddLayer[" + molesName + "](->Dataset: moles.cdataset)", Last());
                m.RequestSetLayerLabelColumnRpc(molesId, 4);
                t.Eq("participant label (own dataset)", "participant:SetLayerLabel[" + molesName + "](None->HitTime)", Last());
                int oldColour = m.Layers[0].ColorColumnIndex;
                using (m.EditLayerScope("L1"))
                    m.RequestColorColumnRpc(oldColour == 1 ? 2 : 1);
                t.Eq("participant non-active colour", "participant:SetColor[" + baseName + "](" +
                     (oldColour < 0 ? "None" : mv.GetColumn(oldColour).Name) + "->" + (oldColour == 1 ? "CY" : "CX") + ")", Last());
                t.True("participant state is layered", rows.Count > 0 && rows[rows.Count - 1].PlotState.Contains("\"layers\":[{\"id\":\"L1\""));
                int participantRows = rows.Count;

                // Facilitator (dashboard) changes: one row each, never a participant row.
                logger.RunAsFacilitator(StudyActions.SetLayerOpacity, molesName, "1", "0.5", () => m.RequestSetLayerOpacityRpc(molesId, 0.5f));
                t.Eq("facilitator layer overload", "facilitator:SetLayerOpacity[" + molesName + "](1->0.5)", Last());
                logger.RunAsFacilitator(StudyActions.SetPointSize, "0.1", "0.05", () => m.RequestPointSizeRpc(0.05f));
                t.Eq("facilitator legacy overload uses the edit layer", "facilitator:SetPointSize[" + molesName + "](0.1->0.05)", Last());
                logger.RunAsFacilitator(StudyActions.RemoveLayer, "Dataset: moles.cdataset", "", () => m.RequestRemoveLayerRpc(molesId));
                t.Eq("facilitator remove keeps the removed name", "facilitator:RemoveLayer[" + molesName + "](Dataset: moles.cdataset->)", Last());
                logger.RunAsFacilitator(StudyActions.AddLayer, null, "", "Dataset: moles.cdataset", () => m.RequestAddDatasetLayerRpc("moles.cdataset"));
                t.Eq("facilitator null layer = active after apply", "facilitator:AddLayer[" + m.ActiveLayer.Name + "](->Dataset: moles.cdataset)", Last());
                logger.RunAsFacilitator(StudyActions.Play, "Paused", "Playing", () => m.RequestPlaybackToggleRpc(true));
                t.Eq("facilitator plot-level", "facilitator:Play[](Paused->Playing)", Last());
                logger.LogFacilitatorAction(StudyActions.SetLayerVisible, "L?", "True", "False");
                t.Eq("facilitator log overload", "facilitator:SetLayerVisible[L?](True->False)", Last());
                t.Eq("no participant rows from facilitator changes", participantRows + 6, rows.Count);

                logger.StopSession();
                string[] lines = File.ReadAllLines(Directory.GetFiles(folder, "*.csv")[0]);
                t.True("file header", lines.Length > 0 && lines[0].StartsWith("Timestamp,SessionTime,ParticipantID,Part,Source,Action,Layer,OldValue"));
                t.Eq("file rows", rows.Count + 2, lines.Length); // + header and SessionStart (SessionStop is in rows)
            }, ("movement.cdataset", mv), ("moles.cdataset", ml));

            if (go != null)
                UnityEngine.Object.DestroyImmediate(go);
            try { Directory.Delete(folder, true); } catch { /* temp folder */ }
        }

        static PlotSnapshot Also(this PlotSnapshot s, Action<PlotSnapshot> change)
        {
            change(s);
            return s;
        }
    }
}
