using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DataViz.Networking;
using UnityEditor;
using UnityEngine;

namespace DataViz.Study
{
    /// <summary>
    /// Self-tests for the study logging helpers. The project has no asmdefs, so the Test Runner
    /// cannot reference Assembly-CSharp; this menu item runs plain checks instead.
    /// </summary>
    public static class StudySelfTests
    {
        [MenuItem("Tools/CUBIA/Study/Run Self-Tests")]
        public static void RunMenu() => Run();

        /// <summary>Runs every check; returns "PASS n / FAIL m" plus one line per failure, and logs it.</summary>
        public static string Run()
        {
            var t = new Tally();

            Guard(t, "CsvUtil", () => TestCsv(t));
            Guard(t, "BuildFileName", () => TestFileName(t));
            Guard(t, "StudyLogWriter", () => TestWriter(t));
            Guard(t, "PlotStateDiff", () => TestDiff(t));
            Guard(t, "PlotStateJson", () => TestJson(t));
            Guard(t, "PreparedViews", () => TestPreparedViews(t));
            Guard(t, "WamIa label", () => TestWamIaLabel(t));
            Guard(t, "WamIa scan", () => TestWamIaScan(t));
            Guard(t, "WamIa result", () => TestWamIaResult(t));

            // Every [SelfTest] method in this assembly (SelfTestKit), added to the same report.
            var kit = new SelfTestTally();
            Guard(t, "SelfTestKit", () => SelfTestKit.RunAll(kit));
            t.Pass += kit.Pass;
            t.Failures.AddRange(kit.Failures);

            var sb = new StringBuilder();
            sb.Append("PASS ").Append(t.Pass).Append(" / FAIL ").Append(t.Failures.Count);
            foreach (string f in t.Failures)
                sb.Append('\n').Append(f);
            string report = sb.ToString();

            if (t.Failures.Count == 0)
                Debug.Log("[StudySelfTests] " + report);
            else
                Debug.LogError("[StudySelfTests] " + report);
            return report;
        }

        // ---- CsvUtil ----------------------------------------------------------------------

        static void TestCsv(Tally t)
        {
            t.Eq("csv plain", "abc", CsvUtil.Escape("abc"));
            t.Eq("csv null", "", CsvUtil.Escape(null));
            t.Eq("csv comma", "\"a,b\"", CsvUtil.Escape("a,b"));
            t.Eq("csv quotes", "\"say \"\"hi\"\"\"", CsvUtil.Escape("say \"hi\""));
            t.Eq("csv newline", "\"a\nb\"", CsvUtil.Escape("a\nb"));
            t.Eq("csv cr", "\"a\rb\"", CsvUtil.Escape("a\rb"));
            t.Eq("csv join", "a,\"b,c\",,d", CsvUtil.JoinRow(new[] { "a", "b,c", "", "d" }));
        }

        // ---- BuildFileName ----------------------------------------------------------------

        static void TestFileName(Tally t)
        {
            var now = new DateTime(2026, 11, 6, 9, 5, 3);
            t.Eq("file P07", "P07_part1_20261106_090503.csv", StudyLogWriter.BuildFileName("P07", 1, now));
            t.Eq("file 07", "P07_part1_20261106_090503.csv", StudyLogWriter.BuildFileName("07", 1, now));
            t.Eq("file p07 lower", "P07_part2_20261106_090503.csv", StudyLogWriter.BuildFileName("p07", 2, now));
            t.Eq("file sanitised", "P0-7_x_part1_20261106_090503.csv", StudyLogWriter.BuildFileName(" 0/-7:_x ", 1, now));
        }

        // ---- StudyLogWriter ---------------------------------------------------------------

        static void TestWriter(Tally t)
        {
            string path = Path.Combine(Path.GetTempPath(), "cubia_selftest_" + Guid.NewGuid().ToString("N") + ".csv");
            CultureInfo savedCulture = CultureInfo.CurrentCulture;
            try
            {
                try
                {
                    CultureInfo.CurrentCulture = new CultureInfo("da-DK"); // "," decimals, "." time separator
                }
                catch (Exception e)
                {
                    t.Fail("writer culture", "could not set da-DK: " + e.Message);
                }

                var row1 = new StudyLogRow
                {
                    Timestamp = new DateTime(2026, 11, 6, 14, 5, 9, 123),
                    SessionTime = 12.5,
                    ParticipantId = "P07",
                    Part = 1,
                    Source = ChangeSource.Facilitator,
                    Action = StudyActions.SetX,
                    OldValue = "a",
                    NewValue = "b",
                };
                var row2 = new StudyLogRow
                {
                    Timestamp = new DateTime(2026, 11, 6, 14, 5, 10, 7),
                    SessionTime = 3.14159,
                    ParticipantId = "P07",
                    Part = 1,
                    Source = ChangeSource.Request,
                    Action = StudyActions.Note,
                    RequestText = "Show \"big\" values, please",
                    Tag = RequestTag.Partly,
                    PlotState = "{\"a\":1,\"b\":\"x\"}",
                };

                using (var w = new StudyLogWriter(path))
                {
                    w.WriteRow(row1);
                    w.WriteRow(row2);
                }

                string header = "Timestamp,SessionTime,ParticipantID,Part,Source,Action,OldValue,NewValue,RequestText,Tag,PlotState";
                string line1 = "2026-11-06T14:05:09.123,12.50,P07,1,facilitator,SetX,a,b,,,";
                string line2 = "2026-11-06T14:05:10.007,3.14,P07,1,request,Note,,,\"Show \"\"big\"\" values, please\",Partly,\"{\"\"a\"\":1,\"\"b\"\":\"\"x\"\"}\"";

                string[] lines = File.ReadAllLines(path);
                t.Eq("writer line count", "3", lines.Length.ToString(CultureInfo.InvariantCulture));
                if (lines.Length >= 3)
                {
                    t.Eq("writer header", header, lines[0]);
                    t.Eq("writer row1", line1, lines[1]);
                    t.Eq("writer row2", line2, lines[2]);
                }

                byte[] bytes = File.ReadAllBytes(path);
                t.True("writer no BOM", bytes.Length > 0 && bytes[0] != 0xEF);

                // Re-opening the same file appends without a second header.
                using (var w = new StudyLogWriter(path))
                    w.WriteRow(row1);
                lines = File.ReadAllLines(path);
                t.Eq("writer append count", "4", lines.Length.ToString(CultureInfo.InvariantCulture));
                if (lines.Length >= 4)
                    t.Eq("writer append row", line1, lines[3]);
            }
            finally
            {
                CultureInfo.CurrentCulture = savedCulture;
                try { File.Delete(path); } catch { /* temp file; ignore */ }
            }
        }

        // ---- PlotStateDiff ----------------------------------------------------------------

        static DatasetColumnar MakeDataset()
        {
            string[] names = { "Age", "Height", "Weight", "Country" };
            var ds = new DatasetColumnar("test", 0, names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                ds.Columns.Add(new DatasetColumn(names[i]));
                ds.ColumnMapping[names[i]] = i;
            }
            return ds;
        }

        static PlotSnapshot Base() => new PlotSnapshot
        {
            DatasetName = "test.cdataset",
            XColumn = 0,
            YColumn = 1,
            ZColumn = 2,
            ColourColumn = -1,
            TimeColumn = -1,
            PointSize = 0.02f,
            SizeMapped = false,
            SizeColumn = -1,
            GlyphColumn = -1,
            IsPlaying = false,
            PlaybackSpeed = 0.1f,
            TimeScrub = 0f,
        };

        static PlotSnapshot With(Action<PlotSnapshot> change)
        {
            PlotSnapshot s = Base();
            change(s);
            return s;
        }

        static void TestDiff(Tally t)
        {
            DatasetColumnar ds = MakeDataset();

            var none = PlotStateDiff.Describe(Base(), Base(), ds);
            t.Eq("diff identical count", "0", none.Count.ToString(CultureInfo.InvariantCulture));
            t.True("diff identical not relevant", !PlotStateDiff.HasRelevantChange(Base(), Base()));

            var x = PlotStateDiff.Describe(Base(), With(s => s.XColumn = 3), ds);
            t.Change("diff X", x, StudyActions.SetX, "Age", "Country");

            var xNull = PlotStateDiff.Describe(Base(), With(s => s.XColumn = 3), null);
            t.Change("diff X no dataset", xNull, StudyActions.SetX, "#0", "#3");

            var colour = PlotStateDiff.Describe(Base(), With(s => s.ColourColumn = 3), ds);
            t.Change("diff colour", colour, StudyActions.SetColor, "None", "Country");

            var outOfRange = PlotStateDiff.Describe(Base(), With(s => s.ColourColumn = 9), ds);
            t.Change("diff colour out of range", outOfRange, StudyActions.SetColor, "None", "#9");

            PlotSnapshot filterA = With(s => { s.FilterColumns = new[] { 3 }; s.FilterLabels = new[] { "" }; s.FilterRangeCounts = new[] { 0 }; });
            PlotSnapshot filterB = With(s => { s.FilterColumns = new[] { 3 }; s.FilterLabels = new[] { "Denmark" }; s.FilterRangeCounts = new[] { 0 }; });
            t.Change("diff filter value", PlotStateDiff.Describe(filterA, filterB, ds), StudyActions.SetFilterValue, "Any value", "Denmark");
            t.Change("diff filter column added", PlotStateDiff.Describe(Base(), filterA, ds), StudyActions.SetFilterColumn, "None", "Country");
            t.Change("diff filters cleared", PlotStateDiff.Describe(filterB, Base(), ds), StudyActions.ClearFilters, "Country = Denmark", "");

            PlotSnapshot filterTwo = With(s => { s.FilterColumns = new[] { 3, 0 }; s.FilterLabels = new[] { "Denmark", "" }; s.FilterRangeCounts = new[] { 0, 0 }; });
            t.Change("diff add filter", PlotStateDiff.Describe(filterB, filterTwo, ds), StudyActions.AddFilter, "", "Age = Any value");
            t.Change("diff remove filter", PlotStateDiff.Describe(filterTwo, filterB, ds), StudyActions.RemoveFilter, "Age = Any value", "");

            var load = PlotStateDiff.Describe(Base(), With(s => { s.DatasetName = "other.cdataset"; s.XColumn = 1; s.ColourColumn = 2; }), ds);
            t.Change("diff dataset only", load, StudyActions.LoadDataset, "test.cdataset", "other.cdataset");

            PlotSnapshot playing = With(s => s.IsPlaying = true);
            PlotSnapshot scrubbed = With(s => { s.IsPlaying = true; s.TimeScrub = 0.4f; s.ScrubServerTime = 12.0; s.PlaybackSpeed = 0.5f; s.DatasetHash = "abc"; });
            t.Eq("diff scrub only count", "0", PlotStateDiff.Describe(playing, scrubbed, ds).Count.ToString(CultureInfo.InvariantCulture));
            t.True("diff scrub only not relevant", !PlotStateDiff.HasRelevantChange(playing, scrubbed));

            t.Change("diff play", PlotStateDiff.Describe(Base(), playing, ds), StudyActions.Play, "", "");
            t.Change("diff pause", PlotStateDiff.Describe(playing, Base(), ds), StudyActions.Pause, "", "");

            t.Change("diff point size", PlotStateDiff.Describe(Base(), With(s => s.PointSize = 0.035f), ds), StudyActions.SetPointSize, "0.02", "0.035");
            t.Eq("diff point size tiny", "0", PlotStateDiff.Describe(Base(), With(s => s.PointSize = 0.02f + 1e-5f), ds).Count.ToString(CultureInfo.InvariantCulture));

            PlotSnapshot glyphA = With(s => { s.GlyphColumn = 3; s.GlyphLabels = new[] { "Denmark" }; s.GlyphIndices = new[] { 1 }; });
            PlotSnapshot glyphB = With(s => { s.GlyphColumn = 3; s.GlyphLabels = new[] { "Denmark", "Sweden" }; s.GlyphIndices = new[] { 2, 3 }; });
            var glyph = PlotStateDiff.Describe(glyphA, glyphB, ds);
            t.Eq("diff glyph count", "2", glyph.Count.ToString(CultureInfo.InvariantCulture));
            if (glyph.Count == 2)
            {
                t.Eq("diff glyph changed", "Denmark=2", glyph[0].newValue);
                t.Eq("diff glyph added", "Sweden=3", glyph[1].newValue);
            }
        }

        // ---- PlotStateJson ----------------------------------------------------------------

        static void TestJson(Tally t)
        {
            DatasetColumnar ds = MakeDataset();
            PlotSnapshot s = With(p =>
            {
                p.DatasetName = "te\"st";
                p.ColourColumn = 3;
                p.FilterColumns = new[] { 1 };
                p.FilterLabels = new[] { "" };
                p.FilterRangeCounts = new[] { 1 };
                p.FilterRangeMins = new[] { 1.5f };
                p.FilterRangeMaxs = new[] { 2.25f };
                p.TimeScrub = 0.456f;
            });

            CultureInfo savedCulture = CultureInfo.CurrentCulture;
            string json;
            try
            {
                try { CultureInfo.CurrentCulture = new CultureInfo("da-DK"); } catch { /* reported by the writer test */ }
                json = PlotStateJson.Write(s, ds);
            }
            finally
            {
                CultureInfo.CurrentCulture = savedCulture;
            }

            t.True("json one line", json.IndexOf('\n') < 0 && json.IndexOf('\r') < 0);
            t.True("json braces", json.StartsWith("{") && json.EndsWith("}"));
            foreach (string key in new[] { "ds", "x", "y", "z", "color", "size", "time", "glyph", "pointSize", "filters", "playing", "scrub" })
                t.True("json key " + key, json.Contains("\"" + key + "\":"));

            t.True("json ds escaped", json.Contains("\"ds\":\"te\\\"st\""));
            t.True("json x name", json.Contains("\"x\":\"Age\""));
            t.True("json color name", json.Contains("\"color\":\"Country\""));
            t.True("json size null", json.Contains("\"size\":null"));
            t.True("json time null", json.Contains("\"time\":null"));
            t.True("json filter", json.Contains("\"filters\":[{\"col\":\"Height\",\"value\":\"\",\"ranges\":[[1.5,2.25]]}]"));
            t.True("json playing", json.Contains("\"playing\":false"));
            t.True("json scrub", json.Contains("\"scrub\":0.46"));
            t.True("json point size", json.Contains("\"pointSize\":0.02"));
        }

        // ---- PreparedViews ----------------------------------------------------------------

        static readonly string[] k_MovementColumns =
        {
            "Time", "Part", "Phase", "MoleOrder", "ControllerX", "ControllerY", "ControllerZ", "ControllerSpeed",
            "LaserX", "LaserY", "LaserZ", "HeadX", "HeadY", "HeadZ", "HeadYaw", "HeadPitch",
        };

        static DatasetColumnar MakeDataset(string name, string[] columns)
        {
            var ds = new DatasetColumnar(name, 0, columns.Length);
            for (int i = 0; i < columns.Length; i++)
            {
                ds.Columns.Add(new DatasetColumn(columns[i]));
                ds.ColumnMapping[columns[i]] = i;
            }
            return ds;
        }

        static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

        static void TestPreparedViews(Tally t)
        {
            // FileFor: same participant normalisation as the session log file name.
            t.Eq("views file 07", "P07_part1_movement.cdataset", PreparedViews.FileFor("07", 1, PreparedViews.KindMovement));
            t.Eq("views file P07", "P07_part1_movement.cdataset", PreparedViews.FileFor("P07", 1, "movement"));
            t.Eq("views file p07 part2", "P07_part2_moles.cdataset", PreparedViews.FileFor("p07", 2, "moles"));
            t.Eq("views file trimmed", "P07_part1_combined.cdataset", PreparedViews.FileFor("  P07 ", 1, "combined"));
            t.Eq("views file sanitised", "P0-7_x_part1_moles.cdataset", PreparedViews.FileFor(" 0/-7:_x ", 1, "moles"));
            t.Eq("views participant", "P12", PreparedViews.NormaliseParticipant("12"));

            // The list itself.
            t.Eq("views count", "3", I(PreparedViews.All.Count));
            if (PreparedViews.All.Count == 3)
            {
                t.Eq("views 1 kind", PreparedViews.KindMovement, PreparedViews.All[0].Kind);
                t.Eq("views 2 kind", PreparedViews.KindCombined, PreparedViews.All[1].Kind);
                t.Eq("views 3 kind", PreparedViews.KindMoles, PreparedViews.All[2].Kind);
                t.Eq("views 1 title", "Controller path", PreparedViews.All[0].Title);
            }
            t.True("views find", PreparedViews.Find("controller-path") != null && PreparedViews.Find("nope") == null);

            // TryResolve success on the movement columns.
            DatasetColumnar movement = MakeDataset("P07_part1_movement.cdataset", k_MovementColumns);
            PreparedView path = PreparedViews.All[0];
            bool ok = PreparedViews.TryResolve(path, movement, out int x, out int y, out int z,
                out int color, out int size, out int time, out int glyph, out string missing);
            t.True("views resolve ok", ok);
            t.Eq("views resolve missing empty", "", missing);
            t.Eq("views resolve x", "4", I(x));
            t.Eq("views resolve y", "5", I(y));
            t.Eq("views resolve z", "6", I(z));
            t.Eq("views resolve color", "7", I(color));
            t.Eq("views resolve time", "0", I(time));
            t.Eq("views resolve size none", "-1", I(size));
            t.Eq("views resolve glyph none", "-1", I(glyph));

            // Failure lists every missing name, in field order.
            PreparedView accuracy = PreparedViews.All[2];
            bool bad = PreparedViews.TryResolve(accuracy, movement, out _, out _, out _, out _, out _, out _, out _, out string missingNames);
            t.True("views resolve fails", !bad);
            t.Eq("views resolve missing names", "MoleX, MoleY, SpawnTime, HitOffset, ReactionTimeOrLifetime", missingNames);

            // One column short: only that one is named, the others still resolve.
            var partial = new List<string>(k_MovementColumns);
            partial.Remove("ControllerSpeed");
            bool short1 = PreparedViews.TryResolve(path, MakeDataset("short", partial.ToArray()),
                out int px, out _, out _, out int pColor, out _, out _, out _, out string missingOne);
            t.True("views resolve one missing fails", !short1);
            t.Eq("views resolve one missing name", "ControllerSpeed", missingOne);
            t.Eq("views resolve one missing x", "4", I(px));
            t.Eq("views resolve one missing color", "-1", I(pColor));

            // Combined view: glyph on RowType, no time column.
            DatasetColumnar combined = MakeDataset("combined", new[]
                { "RowType", "X", "Y", "Z", "Time", "MoleOrder", "MoleOrderLabel", "Label", "Phase", "Part" });
            bool cOk = PreparedViews.TryResolve(PreparedViews.All[1], combined, out _, out _, out int cz,
                out int cColor, out _, out int cTime, out int cGlyph, out _);
            t.True("views combined ok", cOk);
            t.Eq("views combined z=Time", "4", I(cz));
            t.Eq("views combined color", "6", I(cColor));
            t.Eq("views combined time none", "-1", I(cTime));
            t.Eq("views combined glyph", "0", I(cGlyph));

            t.True("views resolve null dataset", !PreparedViews.TryResolve(path, null, out _, out _, out _, out _, out _, out _, out _, out _));
        }

        // ---- WAM+IA auto import -----------------------------------------------------------

        static void TestWamIaLabel(Tally t)
        {
            t.Eq("label 7", "P07", WamIaAutoImporter.ParticipantLabel("7"));
            t.Eq("label 12", "P12", WamIaAutoImporter.ParticipantLabel("12"));
            t.Eq("label 0", "P00", WamIaAutoImporter.ParticipantLabel("0"));
            t.Eq("label P07", "P07", WamIaAutoImporter.ParticipantLabel("P07"));
            t.Eq("label p7", "P07", WamIaAutoImporter.ParticipantLabel("p7"));
            t.Eq("label spaces", "P03", WamIaAutoImporter.ParticipantLabel(" 3 "));
            t.Eq("label 007", "P07", WamIaAutoImporter.ParticipantLabel("007"));
            t.Eq("label 123", "P123", WamIaAutoImporter.ParticipantLabel("123"));
            t.Eq("label text", "Pabc", WamIaAutoImporter.ParticipantLabel("abc!"));
            t.Eq("label NULL", "", WamIaAutoImporter.ParticipantLabel("NULL"));
            t.Eq("label empty", "", WamIaAutoImporter.ParticipantLabel("  "));
            t.Eq("label only junk", "", WamIaAutoImporter.ParticipantLabel("!?"));

            // Dropdown label → the converter's file names and the session log name, unchanged.
            t.Eq("label → views file", "P07_part1_movement.cdataset",
                PreparedViews.FileFor(WamIaAutoImporter.ParticipantLabel("7"), 1, PreparedViews.KindMovement));
            t.Eq("label → normalise P07", "P07", PreparedViews.NormaliseParticipant("P07"));
            t.Eq("label → normalise Pabc", "Pabc", PreparedViews.NormaliseParticipant(WamIaAutoImporter.ParticipantLabel("abc!")));
            t.Eq("label → log file", "P12_part2_20261106_090503.csv",
                StudyLogWriter.BuildFileName(WamIaAutoImporter.ParticipantLabel("12"), 2, new DateTime(2026, 11, 6, 9, 5, 3)));

            List<string> sorted = WamIaAutoImporter.SortParticipants(new[] { "P10", "Pabc", "P02", "P02", null, "", "P100" });
            t.Eq("sort", "P02,P10,P100,Pabc", string.Join(",", sorted));
            t.Eq("default highest", "P100", WamIaAutoImporter.DefaultParticipant(sorted));
            t.Eq("default text only", "Pzz", WamIaAutoImporter.DefaultParticipant(new List<string> { "Paa", "Pzz" }));
            t.True("default empty", WamIaAutoImporter.DefaultParticipant(new List<string>()) == null);

            var have = new[] { "P07_part1_movement.cdataset", "p07_PART1_moles.cdataset" };
            t.Eq("missing files", "P07_part1_combined.cdataset", string.Join(",", PreparedViews.MissingFiles("P07", 1, have)));
            t.Eq("missing files part 2", "3", I(PreparedViews.MissingFiles("P07", 2, have).Count));

            t.Eq("args plain", "a.py --auto", WamIaAutoImporter.BuildArguments(new[] { "a.py", "--auto" }));
            t.Eq("args spaces", "\"C:\\My Docs\\x.py\" --logs \"C:\\My Docs\\\\\"",
                WamIaAutoImporter.BuildArguments(new[] { @"C:\My Docs\x.py", "--logs", @"C:\My Docs\" }));
            t.Eq("args quote + empty", "\"a\\\"b\" \"\"", WamIaAutoImporter.BuildArguments(new[] { "a\"b", "" }));
        }

        static void TestWamIaScan(Tally t)
        {
            string folder = Path.Combine(Path.GetTempPath(), "cubia_wamia_selftest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                const string header = "Age;Email;ParticipantID;SessionID;SessionProgram;SessionState;Timestamp";
                File.WriteAllText(Path.Combine(folder, "log_a_Meta.csv"),
                    "\uFEFF" + header + "\n15;x@y.z;7;s1;TestPattern;Done;2026-11-06 10:11:31.0872\n");
                // Second row repeats participant 7 as "07": one label only.
                File.WriteAllText(Path.Combine(folder, "log_b_Meta.csv"),
                    header + "\r\n20;NULL;3;s2;TestPattern;Done;2026-11-06 11:00:00.0000\r\n20;NULL;07;s3;TestPattern;Done;2026-11-06 11:30:00.0000\r\n");
                File.WriteAllText(Path.Combine(folder, "log_c_Meta.csv"),
                    header + "\n30;NULL;0;s4;Warmup;Done;2026-11-06 12:00:00.0000\n" +
                    "31;NULL;9"); // a row still being written: fewer cells than the header, skipped
                File.WriteAllText(Path.Combine(folder, "log_d_Meta.csv"), header + "\n"); // header only
                File.WriteAllText(Path.Combine(folder, "log_e_Meta.csv"), "");            // empty
                File.WriteAllText(Path.Combine(folder, "log_a_Event.csv"), header + "\n1;x;99;s1;P;D;T\n"); // not a Meta file

                t.Eq("scan labels", "P00,P03,P07", string.Join(",", WamIaAutoImporter.ScanParticipants(folder)));

                // A file another program holds open for writing must not break the scan.
                using (new FileStream(Path.Combine(folder, "log_a_Meta.csv"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    List<string> whileOpen = WamIaAutoImporter.ScanParticipants(folder);
                    t.True("scan while file open", whileOpen.Contains("P03") && whileOpen.Contains("P00"));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception)
                {
                    // Temp folder: left for the OS to clean up.
                }
            }

            t.Eq("scan missing folder", "0", I(WamIaAutoImporter.ScanParticipants(Path.Combine(folder, "nope")).Count));
            t.Eq("scan null folder", "0", I(WamIaAutoImporter.ScanParticipants(null).Count));
        }

        static void TestWamIaResult(Tally t)
        {
            const string line = "WAMIA_RESULT {\"converted\": [{\"participant\": \"P07\", \"part\": 1, \"session\": \"s1\", " +
                                "\"files\": [\"P07_part1_movement.cdataset\", \"P07_part1_moles.cdataset\", \"P07_part1_combined.cdataset\"]}], " +
                                "\"replaced\": [{\"participant\": \"P07\", \"part\": 2, \"oldSession\": \"s2\", \"newSession\": \"s3\", " +
                                "\"archived\": [\"P07_part2_movement R 09-11-2026 14.35.cdataset\"]}], " +
                                "\"skipped\": [{\"session\": \"s0\", \"reason\": \"warmup\"}], " +
                                "\"warnings\": [\"Participant P09 follows P07: number P08 skipped\"], \"errors\": [], " +
                                "\"participants\": [\"P00\", \"P07\"]}";

            bool ok = WamIaAutoImporter.TryParseResultLine(line, out WamIaImportResult r);
            t.True("result parsed", ok && r != null);
            if (!ok || r == null) return;

            t.Eq("result converted count", "1", I(r.converted.Length));
            t.Eq("result converted participant", "P07", r.converted[0].participant);
            t.Eq("result converted part", "1", I(r.converted[0].part));
            t.Eq("result converted files", "3", I(r.converted[0].files.Length));
            t.Eq("result replaced", "P07/2/s2/s3", r.replaced[0].participant + "/" + I(r.replaced[0].part) + "/" +
                                                    r.replaced[0].oldSession + "/" + r.replaced[0].newSession);
            t.Eq("result skipped", "s0 warmup", r.skipped[0].session + " " + r.skipped[0].reason);
            t.Eq("result warnings", "1", I(r.warnings.Length));
            t.Eq("result errors empty", "0", I(r.errors.Length));
            t.Eq("result participants", "P00,P07", string.Join(",", r.participants));
            t.True("result has changes", r.HasChanges);
            t.Eq("result replaced message", "P07 part 2 replaced by a newer session; old files archived (R 09-11-2026 14.35)",
                WamIaAutoImporter.ReplacedMessage(r.replaced[0]));

            // Missing keys become empty arrays; anything else is rejected.
            bool sparse = WamIaAutoImporter.TryParseResultLine("WAMIA_RESULT {\"participants\": [\"P01\"]}", out WamIaImportResult s);
            t.True("result sparse", sparse && s.converted != null && s.converted.Length == 0 && s.errors != null &&
                                    s.participants.Length == 1 && !s.HasChanges);
            t.True("result wrong prefix", !WamIaAutoImporter.TryParseResultLine("RESULT {}", out _));
            t.True("result not json", !WamIaAutoImporter.TryParseResultLine("WAMIA_RESULT oops", out _));
            t.True("result broken json", !WamIaAutoImporter.TryParseResultLine("WAMIA_RESULT {\"converted\": [", out _));
            t.True("result null", !WamIaAutoImporter.TryParseResultLine(null, out _));
        }

        // ---- plumbing ---------------------------------------------------------------------

        static void Guard(Tally t, string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                t.Fail(name, "threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        sealed class Tally
        {
            public int Pass;
            public readonly List<string> Failures = new List<string>();

            public void Fail(string name, string detail) => Failures.Add("FAIL " + name + ": " + detail);

            public void True(string name, bool condition)
            {
                if (condition) Pass++;
                else Fail(name, "expected true");
            }

            public void Eq(string name, string expected, string actual)
            {
                if (expected == actual) Pass++;
                else Fail(name, "expected <" + expected + "> got <" + actual + ">");
            }

            /// <summary>Expects exactly one change with these values.</summary>
            public void Change(string name, List<(string action, string oldValue, string newValue)> changes,
                               string action, string oldValue, string newValue)
            {
                if (changes.Count != 1)
                {
                    var parts = new List<string>();
                    foreach (var c in changes)
                        parts.Add(c.action + "(" + c.oldValue + "->" + c.newValue + ")");
                    Fail(name, "expected 1 change, got " + changes.Count + " [" + string.Join(", ", parts) + "]");
                    return;
                }

                var only = changes[0];
                if (only.action == action && only.oldValue == oldValue && only.newValue == newValue)
                    Pass++;
                else
                    Fail(name, "expected " + action + "(" + oldValue + "->" + newValue + ") got " +
                               only.action + "(" + only.oldValue + "->" + only.newValue + ")");
            }
        }
    }
}
