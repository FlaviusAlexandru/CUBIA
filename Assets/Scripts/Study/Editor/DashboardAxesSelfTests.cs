using System;
using System.Collections.Generic;
using DataViz.Networking;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>
    /// The dashboard's Axes block (ASR-09, 13, 14): each change writes one plot-level row whose text
    /// matches what PlotStateDiff.DescribeAxes writes for the same change (so facilitator and
    /// participant rows read the same), refusals write nothing, and the undo snapshot restores the
    /// settings. The controls need a panel to raise events, so the section is driven through the
    /// Set… methods they call.
    /// </summary>
    public static class DashboardAxesSelfTests
    {
        static DatasetColumnar Slab() => SelfTestKit.Dataset("dash_slab.cdataset",
            Col.Num("X", 0f, 6f, 3f, 1f), Col.Num("Y", 0f, 2f, 1f, 2f), Col.Num("Z", 20f, 60f, 40f, 30f));

        sealed class Recorder
        {
            public readonly List<(string action, string oldValue, string newValue)> Rows = new();
            public readonly List<(string action, string oldValue, string newValue)> Expected = new();
            public MultiplayerScatterplotManager Manager;

            public void Change(string action, string oldValue, string newValue, Action apply)
            {
                AxisSettings before = Manager.Axes.Clone();
                Rows.Add((action, oldValue, newValue));
                apply();
                var described = new List<(string, string, string)>();
                PlotStateDiff.DescribeAxes(described, before, Manager.Axes);
                Expected.AddRange(described);
            }
        }

        static string Text(List<(string action, string oldValue, string newValue)> rows)
        {
            var parts = new List<string>();
            foreach (var r in rows) parts.Add($"{r.action}({r.oldValue}->{r.newValue})");
            return string.Join(" | ", parts);
        }

        [SelfTest("Dashboard axes: rows match PlotStateDiff")]
        static void RowsMatchDiff(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("dash_slab.cdataset");
                m.RestoreAxes(AxisSettings.CreateDefault());
                var rec = new Recorder { Manager = m };
                var section = new DashboardAxesSection(null, m, rec.Change);

                section.SetAspect(AxisAspect.FitEach);
                section.SetAspect(AxisAspect.Proportional);
                section.SetPadding(0f);
                section.SetMode(0, AxisRangeMode.Limits);       // starts from the data range 0..6
                section.SetLimits(0, 5f, 1f);                     // swapped to 1..5
                section.SetMode(1, AxisRangeMode.IncludeZero);
                section.SetMode(2, AxisRangeMode.Zoom);
                section.UseDataRange(2);                          // 20..60 is already the range: no row
                section.SetReverse(1, true);
                section.SetGroup(2, false);

                t.Eq("rows", Text(rec.Expected), Text(rec.Rows));
                t.Eq("row count", 9, rec.Rows.Count);
                t.Eq("limits from data", "X: Limits 0..6", rec.Rows[3].newValue);
                t.Eq("limits swapped", "X: Limits 1..5", rec.Rows[4].newValue);
                foreach (var r in rec.Rows)
                    t.True("plot-level " + r.action, StudyActions.IsPlotLevel(r.action));
                t.True("x limits stored", m.Axes.Mode(0) == AxisRangeMode.Limits && m.Axes.LimitMin(0) == 1f && m.Axes.LimitMax(0) == 5f);
                t.True("y reversed", m.Axes.Reversed(1));
                t.True("z out of group", !m.Axes.InGroup(2));

                int before = rec.Rows.Count;
                section.SetLimits(0, 3f, 3f);
                section.SetLimits(0, float.NaN, 2f);
                section.SetLimits(1, 0f, 1f);                     // Y is Include zero: limits do nothing
                section.SetAspect(AxisAspect.Proportional);       // unchanged
                t.Eq("refusals write no row", before, rec.Rows.Count);
                t.True("refusal named", section.StatusText.Contains("two different numbers") || section.StatusText.Length == 0);

                section.ResetAxes();
                t.Eq("reset row", "ResetAxes(->)", Text(new List<(string, string, string)> { rec.Rows[rec.Rows.Count - 1] }));
                t.True("reset to defaults", m.Axes.SameAs(AxisSettings.CreateDefault()));
                section.ResetAxes();
                t.Eq("second reset no row", before + 1, rec.Rows.Count);
            }, ("dash_slab.cdataset", Slab()));
        }

        [SelfTest("Dashboard axes: undo snapshot restores the settings")]
        static void UndoRestores(SelfTestTally t)
        {
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("dash_slab.cdataset");
                m.RestoreAxes(AxisSettings.CreateDefault());
                PlotSnapshot undo = PlotSnapshot.Capture(m);
                var section = new DashboardAxesSection(null, m, (a, o, n, apply) => apply());
                section.SetAspect(AxisAspect.FitEach);
                section.SetMode(0, AxisRangeMode.Zoom);
                section.SetReverse(2, true);
                section.SetPadding(0.1f);
                t.True("changed", !m.Axes.SameAs(AxisSettings.CreateDefault()));

                PlotSnapshot redo = PlotSnapshot.Capture(m);
                m.ApplyNetworkSnapshot(undo, undo.TimeScrub);
                t.True("undo restores defaults", m.Axes.SameAs(AxisSettings.CreateDefault()));
                AxisBounds b = m.SharedBounds;
                t.True("undo bounds not reversed", b.Z.Min < b.Z.Max);
                m.ApplyNetworkSnapshot(redo, redo.TimeScrub);
                t.True("redo restores", m.Axes.AspectMode == AxisAspect.FitEach && m.Axes.Mode(0) == AxisRangeMode.Zoom && m.Axes.Reversed(2));
                t.True("redo bounds reversed", m.SharedBounds.Z.Min > m.SharedBounds.Z.Max);
            }, ("dash_slab.cdataset", Slab()));
        }

        [SelfTest("Dashboard axes: flattened warning text")]
        static void Warning(SelfTestTally t)
        {
            t.Eq("none", "", DashboardAxesSection.WarningText(0));
            t.True("one axis", DashboardAxesSection.WarningText(0b100).StartsWith("Z is drawn at under 5 %"));
            t.True("two axes", DashboardAxesSection.WarningText(0b101).StartsWith("X Z are drawn"));

            // The rebound column next to slab coordinates in metres: Z far longer, so X and Y flatten.
            DatasetColumnar wide = SelfTestKit.Dataset("dash_wide.cdataset",
                Col.Num("X", 0f, 0.6f), Col.Num("Y", 0f, 0.2f), Col.Num("Rebound", 20f, 60f));
            SelfTestKit.WithManager(m =>
            {
                m.LoadLocalDataset("dash_wide.cdataset");
                m.RestoreAxes(AxisSettings.CreateDefault());
                t.Eq("x and y flattened", 0b011, m.FlattenedAxes);
                var section = new DashboardAxesSection(null, m, (a, o, n, apply) => apply());
                section.SetGroup(2, false); // the fix: Rebound on its own
                t.Eq("fixed", 0, m.FlattenedAxes);
            }, ("dash_wide.cdataset", wide));
        }
    }
}
