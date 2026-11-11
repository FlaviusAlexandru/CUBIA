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

                string header = "Timestamp,SessionTime,ParticipantID,Part,Source,Action,Layer,OldValue,NewValue,RequestText,Tag,PlotState";
                string line1 = "2026-11-06T14:05:09.123,12.50,P07,1,facilitator,SetX,,a,b,,,";
                string line2 = "2026-11-06T14:05:10.007,3.14,P07,1,request,Note,,,,\"Show \"\"big\"\" values, please\",Partly,\"{\"\"a\"\":1,\"\"b\"\":\"\"x\"\"}\"";

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
