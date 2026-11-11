using System;
using System.Collections.Generic;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;

namespace DataViz.Study
{
    /// <summary>WP4 checks: label text and label choice (LayerLabelRenderer), legend lines and when the legend hides (PlotLegend).</summary>
    public static class LayerOverlaySelfTests
    {
        // Moles: MoleOrder numeric with an empty cell (row 1), MoleOrderLabel categorical with an empty cell (row 3).
        static DatasetColumnar Moles() => SelfTestKit.Dataset("wp4_moles.cdataset",
            Col.Num("X", 0f, 1f, 2f, 3f, 4f),
            Col.Num("Y", 4f, 3f, 2f, 1f, 0f),
            Col.Num("SpawnTime", 0f, 1f, 2f, 3f, 4f),
            Col.Num("MoleOrder", 7f, null, 2.5f, 1f, 3f),
            Col.Cat("MoleOrderLabel", "M7", "M0", "M2", null, "M3"));

        static DatasetColumnar Cursor() => SelfTestKit.Dataset("wp4_cursor.cdataset",
            Col.Num("X", 0f, 1f, 2f),
            Col.Num("Y", 0f, 1f, 2f),
            Col.Num("Time", 0f, 1f, 2f),
            Col.Cat("Phase", "a", "b", "a"));

        // Cursor (fixed colour, Circle), Moles (Star, colour by MoleOrderLabel, Normalised), Trend (trendline of Cursor).
        static (LayerStack stack, PlotLayer cursor, PlotLayer moles, PlotLayer trend) ThreeLayers()
        {
            var stack = new LayerStack();
            PlotLayer cursor = stack.AddDataset("wp4_cursor.cdataset", Cursor());
            cursor.Name = "Cursor";
            cursor.ColorColumnIndex = -1;
            cursor.GlyphColumnIndex = -1;
            cursor.DefaultGlyph = 0;

            PlotLayer moles = stack.AddDataset("wp4_moles.cdataset", Moles());
            moles.Name = "Moles";
            moles.ColorColumnIndex = 4;
            moles.GlyphColumnIndex = -1;
            moles.DefaultGlyph = 5;
            moles.Normalised = true;

            PlotLayer trend = stack.AddComputed(cursor, ComputedLayerKind.Trendline);
            trend.Name = "Trend";
            return (stack, cursor, moles, trend);
        }

        static List<string> Texts(List<PlotLegend.LegendLine> lines) => lines.ConvertAll(l => l.Text);

        // ---- labels ------------------------------------------------------------------------------

        [SelfTest("Overlay: label text numeric, categorical, empty")]
        static void LabelText(SelfTestTally t)
        {
            DatasetColumnar ds = Moles();
            t.Eq("numeric whole", "7", LayerLabelRenderer.LabelText(ds, 0, 3));
            t.Eq("numeric fraction (UI formatting)", 2.5f.ToString(), LayerLabelRenderer.LabelText(ds, 2, 3));
            t.Eq("numeric empty cell: no label", null, LayerLabelRenderer.LabelText(ds, 1, 3));
            t.Eq("categorical", "M3", LayerLabelRenderer.LabelText(ds, 4, 4));
            t.Eq("categorical first category", "M7", LayerLabelRenderer.LabelText(ds, 0, 4));
            t.Eq("categorical empty cell: no label", null, LayerLabelRenderer.LabelText(ds, 3, 4));
            t.Eq("no label column", null, LayerLabelRenderer.LabelText(ds, 0, -1));
            t.Eq("column out of range", null, LayerLabelRenderer.LabelText(ds, 0, 9));
            t.Eq("row out of range", null, LayerLabelRenderer.LabelText(ds, 99, 3));
            t.Eq("no dataset", null, LayerLabelRenderer.LabelText(null, 0, 3));
        }

        [SelfTest("Overlay: labelled points chosen within the slice")]
        static void ChooseInSlice(SelfTestTally t)
        {
            DatasetColumnar ds = Moles();
            // Buffer: two points of another layer, then this layer's rows 0..4.
            var rows = new List<int> { 4, 4, 0, 1, 2, 3, 4 };
            var chosen = new List<int>();
            int total = LayerLabelRenderer.ChooseLabelled(rows, 2, 5, ds, 3, LayerLabelRenderer.MaxLabelsPerLayer, chosen);
            t.Eq("numeric: labelled count", 4, total);
            t.Eq("numeric: chosen", "2,4,5,6", string.Join(",", chosen));
            t.Eq("numeric: no warning", null, LayerLabelRenderer.LimitWarning("Moles", chosen.Count, total));

            total = LayerLabelRenderer.ChooseLabelled(rows, 2, 5, ds, 4, LayerLabelRenderer.MaxLabelsPerLayer, chosen);
            t.Eq("categorical: labelled count", 4, total);
            t.Eq("categorical: chosen", "2,3,4,6", string.Join(",", chosen));

            total = LayerLabelRenderer.ChooseLabelled(rows, 2, 5, ds, -1, LayerLabelRenderer.MaxLabelsPerLayer, chosen);
            t.Eq("no label column: none", 0, total);
            t.Eq("no label column: chosen", 0, chosen.Count);

            // A slice that runs past the buffer is cut, not read out of range.
            total = LayerLabelRenderer.ChooseLabelled(rows, 5, 10, ds, 3, LayerLabelRenderer.MaxLabelsPerLayer, chosen);
            t.Eq("cut slice", 2, total);
        }

        [SelfTest("Overlay: label limit and warning")]
        static void LabelLimit(SelfTestTally t)
        {
            var values = new float?[250];
            for (int i = 0; i < values.Length; i++)
                values[i] = i;
            DatasetColumnar ds = SelfTestKit.Dataset("wp4_many.cdataset", Col.Num("N", values));
            var rows = new List<int>();
            for (int i = 0; i < 250; i++)
                rows.Add(i);

            var chosen = new List<int>();
            int total = LayerLabelRenderer.ChooseLabelled(rows, 0, rows.Count, ds, 0, LayerLabelRenderer.MaxLabelsPerLayer, chosen);
            t.Eq("total", 250, total);
            t.Eq("at most 200", 200, chosen.Count);
            t.Eq("first point labelled", 0, chosen.Count > 0 ? chosen[0] : -1);
            bool increasing = true;
            for (int i = 1; i < chosen.Count; i++)
                increasing &= chosen[i] > chosen[i - 1];
            t.True("distinct and in order", increasing);
            t.True("spread to the end", chosen.Count > 0 && chosen[chosen.Count - 1] >= 245);
            t.Eq("warning", "Moles: labels on 200 of 250 points (at most 200 per layer)",
                LayerLabelRenderer.LimitWarning("Moles", chosen.Count, total));

            total = LayerLabelRenderer.ChooseLabelled(rows, 0, 200, ds, 0, LayerLabelRenderer.MaxLabelsPerLayer, chosen);
            t.Eq("exactly 200: all", 200, chosen.Count);
            t.Eq("exactly 200: no warning", null, LayerLabelRenderer.LimitWarning("Moles", chosen.Count, total));
        }

        // ---- legend ------------------------------------------------------------------------------

        [SelfTest("Overlay: legend lines for three layers")]
        static void LegendThreeLayers(SelfTestTally t)
        {
            (LayerStack stack, PlotLayer cursor, PlotLayer moles, PlotLayer trend) = ThreeLayers();
            Func<PlotLayer, string> warnings = l => l == moles ? "Moles: labels on 200 of 250 points (at most 200 per layer)" : null;

            List<string> lines = Texts(PlotLegend.BuildLines(stack, warnings));
            t.Eq("line count", 4, lines.Count);
            t.Eq("cursor", "Cursor: Circle, colour fixed", lines.Count > 0 ? lines[0] : "");
            t.Eq("moles", "Moles: Star, colour MoleOrderLabel, Normalised", lines.Count > 1 ? lines[1] : "");
            t.Eq("moles warning", "Moles: labels on 200 of 250 points (at most 200 per layer)", lines.Count > 2 ? lines[2] : "");
            t.Eq("trend", "Trend: line, colour fixed", lines.Count > 3 ? lines[3] : "");

            List<PlotLegend.LegendLine> raw = PlotLegend.BuildLines(stack, warnings);
            t.True("warning line flagged", raw.Count > 2 && raw[2].Warning && !raw[1].Warning);
            t.Eq("warning line belongs to its layer", moles.Id, raw.Count > 2 ? raw[2].LayerId : "");

            // A glyph column, a hidden layer, an unavailable layer.
            cursor.GlyphColumnIndex = 3;
            moles.Visible = false;
            trend.LocalUnavailable = true;
            lines = Texts(PlotLegend.BuildLines(stack, warnings));
            t.Eq("hidden layer has no entry", 2, lines.Count);
            t.Eq("glyph by column", "Cursor: glyph by Phase, colour fixed", lines.Count > 0 ? lines[0] : "");
            t.Eq("unavailable", "Trend: line, colour fixed, not available here", lines.Count > 1 ? lines[1] : "");
        }

        [SelfTest("Overlay: legend computed layer marks")]
        static void LegendComputed(SelfTestTally t)
        {
            (LayerStack stack, PlotLayer cursor, PlotLayer moles, PlotLayer trend) = ThreeLayers();

            PlotLayer moleMean = stack.AddComputed(moles, ComputedLayerKind.Mean);
            moleMean.Name = "Mole mean";
            t.Eq("computed on a normalised source is normalised", "line, colour fixed, Normalised", PlotLegend.BuildLine(moleMean, stack));

            stack.Remove(cursor.Id);
            t.Eq("source missing", "line, colour fixed, source missing", PlotLegend.BuildLine(trend, stack));

            t.Eq("status without source missing", "Trend: not enough points",
                PlotLegend.ComputedStatusWarning(trend, ComputedLayerBuilder.StatusSourceMissing + ", " + ComputedLayerBuilder.StatusNotEnoughPoints));
            t.Eq("status only source missing", null, PlotLegend.ComputedStatusWarning(trend, ComputedLayerBuilder.StatusSourceMissing));
            t.Eq("no status", null, PlotLegend.ComputedStatusWarning(trend, ""));

            // Multi-line warnings become one legend line each.
            List<string> lines = Texts(PlotLegend.BuildLines(stack, l => l == moles ? "a\nb" : null));
            t.Eq("multi-line warning", "Moles: Star, colour MoleOrderLabel, Normalised|a|b|Trend: line, colour fixed, source missing|Mole mean: line, colour fixed, Normalised",
                string.Join("|", lines));
        }

        [SelfTest("Overlay: legend hidden for one plain layer")]
        static void LegendHidden(SelfTestTally t)
        {
            t.Eq("no stack", 0, PlotLegend.BuildLines(null, null).Count);
            t.Eq("empty stack", 0, PlotLegend.BuildLines(new LayerStack(), null).Count);

            var stack = new LayerStack();
            PlotLayer only = stack.AddDataset("wp4_cursor.cdataset", Cursor());
            only.Name = "Cursor";
            t.Eq("one plain layer: hidden", 0, PlotLegend.BuildLines(stack, null).Count);

            only.Normalised = true;
            t.Eq("one normalised layer: shown", "Cursor: Circle, colour fixed, Normalised",
                string.Join("|", Texts(PlotLegend.BuildLines(stack, null))));

            only.Normalised = false;
            t.Eq("one layer with a warning: shown", "Cursor: Circle, colour fixed|Cursor: 2 rows skipped",
                string.Join("|", Texts(PlotLegend.BuildLines(stack, l => "Cursor: 2 rows skipped"))));

            only.Visible = false;
            t.Eq("one hidden layer with a warning: nothing", 0, PlotLegend.BuildLines(stack, l => "x").Count);

            only.Visible = true;
            PlotLayer second = stack.AddDataset("wp4_moles.cdataset", Moles());
            second.Visible = false;
            t.Eq("two layers, one hidden: one entry", 1, PlotLegend.BuildLines(stack, null).Count);
        }
    }
}
