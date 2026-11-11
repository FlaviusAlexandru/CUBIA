using System.Collections.Generic;
using DataViz.MenuUI;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;
using Model = DataViz.MenuUI.LayersPanelModel;

namespace DataViz.Study
{
    /// <summary>WP5 checks for the VR Layers tab's view model (LayersPanelModel): row texts and tags, enabled states, the property block per layer kind, paging, pickers and the badge.</summary>
    public static class LayersPanelSelfTests
    {
        static DatasetColumnar Data(string name = "wp5_a.cdataset") => SelfTestKit.Dataset(name,
            Col.Num("X", 1f, 2f, 3f, 4f),
            Col.Num("Y", 10f, 20f, null, 40f),
            Col.Num("Time", 0f, 1f, 2f, 3f),
            Col.Cat("Kind", "b", "a", "b", "c"));

        static Model.View Build(LayerStack stack, Dictionary<string, string> warnings = null, HashSet<string> unlocked = null, int page = 0) =>
            Model.Build(stack,
                l => warnings != null && warnings.TryGetValue(l.Id, out string w) ? w : null,
                id => unlocked != null && unlocked.Contains(id),
                page);

        [SelfTest("LayersPanel: empty plot")]
        static void EmptyPlot(SelfTestTally t)
        {
            var stack = new LayerStack();
            Model.View v = Build(stack);
            t.Eq("empty: no rows", 0, v.Rows.Count);
            t.Eq("empty: one page", 1, v.PageCount);
            t.True("empty: hint shown", v.EmptyText.Length > 0);
            t.Eq("empty: no props", false, v.Props.HasLayer);
            t.Eq("empty: no subset", false, v.CanAddSubset);
            t.Eq("empty: no computed", false, v.CanAddComputed);
            t.True("empty: no badge", Model.BadgeText(stack) == null);
            t.True("null stack: no badge", Model.BadgeText(null) == null);
        }

        [SelfTest("LayersPanel: one dataset layer")]
        static void OneLayer(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Data();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            Model.View v = Build(stack);

            t.Eq("one: rows", 1, v.Rows.Count);
            Model.Row r = v.Rows[0];
            t.Eq("one: name", "1  wp5_a", r.Name);
            t.Eq("one: tags", "Dataset · wp5_a.cdataset", r.Tags);
            t.Eq("one: active", true, r.Active);
            t.Eq("one: visible text", "Shown", r.VisibleText);
            t.Eq("one: cannot move up", false, r.CanMoveUp);
            t.Eq("one: cannot move down", false, r.CanMoveDown);
            t.Eq("one: no warning", "", r.Warning);
            t.True("one: no badge (AT-01)", Model.BadgeText(stack) == null);

            Model.Props p = v.Props;
            t.Eq("one: point", true, p.IsPoint);
            t.Eq("one: id", a.Id, p.LayerId);
            t.Eq("one: title", "Editing: wp5_a", p.Title);
            t.Eq("one: normalise off", "Normalise: off", p.NormaliseText);
            t.Eq("one: no hide in source", false, p.ShowHideInSource);
            t.Eq("one: opacity 100%", 0, p.OpacityStep);
            t.Eq("one: opacity text", "Opacity 100%", p.OpacityText);
            t.Eq("one: default cyan is no palette colour", -1, p.ColourIndex);
            t.Eq("one: glyph", "Glyph: Circle", p.GlyphText);
            t.Eq("one: first layer always shuffles", "Shuffle: always (first layer)", p.ShuffleText);
            t.Eq("one: shuffle not switchable", false, p.ShuffleEnabled);
            t.Eq("one: follow", (int)LayerTimeBehaviour.Follow, p.TimeMode);
            t.True("one: time hint without time column", p.TimeHint.Length > 0);
            t.Eq("one: end column off outside Span", false, p.EndColumnEnabled);
            t.Eq("one: end text", "End: none", p.EndColumnText);
            t.Eq("one: label text", "Labels: none", p.LabelColumnText);
            t.Eq("one: can add subset", true, v.CanAddSubset);
            t.Eq("one: subset text", "Add subset of wp5_a", v.AddSubsetText);

            a.TimeColumnIndex = 2;
            a.TimeBehaviour = LayerTimeBehaviour.Span;
            a.EndTimeColumnIndex = 0;
            a.LabelColumnIndex = 3;
            a.DefaultGlyph = 5;
            p = Build(stack).Props;
            t.Eq("span: no hint", "", p.TimeHint);
            t.Eq("span: end enabled", true, p.EndColumnEnabled);
            t.Eq("span: end text", "End: X", p.EndColumnText);
            t.Eq("span: label text", "Labels: Kind", p.LabelColumnText);
            t.Eq("span: glyph", "Glyph: Star", p.GlyphText);
        }

        [SelfTest("LayersPanel: two layers, tags and badge")]
        static void TwoLayers(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Data();
            DatasetColumnar ds2 = Data("wp5_b.cdataset");
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            PlotLayer b = stack.AddDataset(ds2.Name, ds2);
            b.Normalised = true;
            b.Opacity = 0.5f;
            a.Visible = false;

            Model.View v = Build(stack, unlocked: new HashSet<string> { b.Id });
            t.Eq("two: rows", 2, v.Rows.Count);
            t.Eq("two: hidden tag", "Dataset · wp5_a.cdataset · hidden", v.Rows[0].Tags);
            t.Eq("two: hidden greyed", true, v.Rows[0].Greyed);
            t.Eq("two: hidden text", "Hidden", v.Rows[0].VisibleText);
            t.Eq("two: normalised tag", "Dataset · wp5_b.cdataset · " + Model.TagNormalised, v.Rows[1].Tags);
            t.Eq("two: second active", true, v.Rows[1].Active);
            t.Eq("two: first not active", false, v.Rows[0].Active);
            t.Eq("two: first down", true, v.Rows[0].CanMoveDown);
            t.Eq("two: second up", true, v.Rows[1].CanMoveUp);
            t.Eq("two: second not down", false, v.Rows[1].CanMoveDown);
            t.Eq("two: badge", "Editing: wp5_b", Model.BadgeText(stack));
            t.Eq("two: normalise on", "Normalise: on", v.Props.NormaliseText);
            t.Eq("two: opacity step 50%", 2, v.Props.OpacityStep);
            t.Eq("two: shuffle switchable", true, v.Props.ShuffleEnabled);
            t.Eq("two: shuffle unlocked", "Shuffle: unlocked", v.Props.ShuffleText);
            t.Eq("two: locked when not in set", "Shuffle: locked", Build(stack).Props.ShuffleText);

            b.Opacity = 0.6f;
            t.Eq("two: odd opacity has no step", -1, Build(stack).Props.OpacityStep);

            b.FixedColour = PlayerProfile.Palette[3];
            t.Eq("two: palette colour", 3, Build(stack).Props.ColourIndex);
            b.ColorColumnIndex = 1;
            Model.Props p = Build(stack).Props;
            t.Eq("two: colour by column", true, p.ColourByColumn);
            t.Eq("two: colour column text", "By Y", p.ColourColumnText);
            t.Eq("two: no dot while by column", -1, p.ColourIndex);
        }

        [SelfTest("LayersPanel: subset and computed layers")]
        static void SubsetAndComputed(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Data();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            PlotLayer s = stack.AddSubset(a, 3, "a");
            Model.View v = Build(stack);
            t.Eq("subset: tags", "Subset of wp5_a: a", v.Rows[1].Tags);
            t.Eq("subset: hide in source shown", true, v.Props.ShowHideInSource);
            t.Eq("subset: hide off", "Hide in source: off", v.Props.HideInSourceText);
            s.HiddenInSource = true;
            t.Eq("subset: hide on", "Hide in source: on", Build(stack).Props.HideInSourceText);

            stack.SetActive(a.Id);
            PlotLayer tr = stack.AddComputed(a, ComputedLayerKind.Trendline);
            v = Build(stack);
            Model.Props p = v.Props;
            t.Eq("trend: tags", "Trendline of wp5_a", v.Rows[2].Tags);
            t.Eq("trend: computed", true, p.IsComputed);
            t.Eq("trend: not point", false, p.IsPoint);
            t.Eq("trend: driver axis", true, p.ShowDriverAxis);
            t.Eq("trend: auto axis", 0, p.DriverAxisChoice);
            t.Eq("trend: flag", "Smoothed: off", p.FlagText);
            t.Eq("trend: no span while linear", false, p.ShowSpan);
            t.Eq("trend: no order column", false, p.ShowOrderColumn);
            t.Eq("trend: no value", false, p.ShowValue);
            t.Eq("trend: shuffle never", "Shuffle: never", p.ShuffleText);
            t.Eq("trend: shuffle off", false, p.ShuffleEnabled);
            t.Eq("trend: line width", "Line 6", p.LineWidthText);
            t.Eq("trend: no subset from computed", false, v.CanAddSubset);
            t.Eq("trend: no computed from computed", false, v.CanAddComputed);
            t.Eq("trend: badge names source", "Editing: Trendline of wp5_a (mapping comes from its source)", Model.BadgeText(stack));
            tr.Smoothed = true;
            tr.DriverAxis = 1;
            p = Build(stack).Props;
            t.Eq("trend: smoothed", "Smoothed: on", p.FlagText);
            t.Eq("trend: span when smoothed", true, p.ShowSpan);
            t.Eq("trend: span text", "Span 10%", p.SpanText);
            t.Eq("trend: axis Y", 2, p.DriverAxisChoice);

            stack.SetActive(a.Id);
            PlotLayer refLayer = stack.AddComputed(a, ComputedLayerKind.Reference);
            refLayer.RefValue = 2.5f;
            p = Model.Build(stack, null, null, 0, 0.5f).Props;
            t.Eq("ref: shape line", "Shape: line", p.FlagText);
            t.Eq("ref: value", true, p.ShowValue);
            t.Eq("ref: value text", "At 2.5", p.ValueText);
            refLayer.Smoothed = true;
            t.Eq("ref: plane", "Shape: plane", Build(stack).Props.FlagText);

            stack.SetActive(a.Id);
            PlotLayer path = stack.AddComputed(a, ComputedLayerKind.Path);
            p = Build(stack).Props;
            t.Eq("path: order column", true, p.ShowOrderColumn);
            t.Eq("path: order default", "Order: time column", p.OrderColumnText);
            t.Eq("path: no axis", false, p.ShowDriverAxis);
            t.Eq("path: no flag", false, p.ShowFlag);
            path.OrderColumnIndex = 2;
            t.Eq("path: order column name (source's)", "Order: Time", Build(stack).Props.OrderColumnText);

            stack.SetActive(a.Id);
            stack.AddComputed(a, ComputedLayerKind.SmoothedPath);
            p = Build(stack).Props;
            t.Eq("smoothed path: span", true, p.ShowSpan);
            t.Eq("smoothed path: order", true, p.ShowOrderColumn);

            stack.SetActive(a.Id);
            stack.AddComputed(a, ComputedLayerKind.Mean);
            p = Build(stack).Props;
            t.True("mean: no parameters", !p.ShowSpan && !p.ShowOrderColumn && !p.ShowFlag && !p.ShowValue && !p.ShowDriverAxis);
        }

        [SelfTest("LayersPanel: source missing, unavailable, warnings")]
        static void StatusTags(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Data();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            PlotLayer b = stack.AddDataset(ds.Name, ds);
            PlotLayer tr = stack.AddComputed(a, ComputedLayerKind.Trendline);
            stack.Remove(a.Id);

            var warnings = new Dictionary<string, string>
            {
                { tr.Id, "source missing, not enough points" },
                { b.Id, "wp5_a: 1 row skipped (empty Y)\nwp5_a: down-sampled" },
            };
            Model.View v = Build(stack, warnings);
            t.Eq("missing: rows", 2, v.Rows.Count);
            t.True("missing: tag", v.Rows[1].Tags.Contains(Model.TagSourceMissing));
            t.Eq("missing: frozen dataset in kind text", "Trendline of wp5_a.cdataset · " + Model.TagSourceMissing, v.Rows[1].Tags);
            t.Eq("missing: warning without the tag text", "not enough points", v.Rows[1].Warning);
            t.Eq("warning: first line and count", "wp5_a: 1 row skipped (empty Y) (+1 more)", v.Rows[0].Warning);
            tr.OrderColumnIndex = 3;
            t.Eq("missing: order column uses frozen dataset", "Order: Kind", Build(stack).Props.OrderColumnText);

            stack.SetActive(b.Id);
            t.Eq("warning: props keep every line", "wp5_a: 1 row skipped (empty Y)\nwp5_a: down-sampled", Build(stack, warnings).Props.Warning);

            b.LocalUnavailable = true;
            v = Build(stack);
            t.True("unavailable: tag", v.Rows[0].Tags.EndsWith(Model.TagUnavailable));
            t.Eq("unavailable: greyed", true, v.Rows[0].Greyed);

            t.Eq("clean: only tag", "", Model.CleanWarning("source missing"));
            t.Eq("clean: null", "", Model.CleanWarning(null));
            t.Eq("one line: empty", "", Model.OneLine(""));
        }

        [SelfTest("LayersPanel: paging")]
        static void Paging(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Data();
            for (int i = 0; i < 7; i++)
                stack.AddDataset(ds.Name, ds);
            Model.View v = Build(stack);
            t.Eq("paging: pages", 2, v.PageCount);
            t.Eq("paging: first page rows", Model.RowsPerPage, v.Rows.Count);
            t.Eq("paging: page text", "Page 1 of 2", v.PageText);
            v = Build(stack, page: 1);
            t.Eq("paging: second page rows", 2, v.Rows.Count);
            t.Eq("paging: second page first index", 5, v.Rows[0].Index);
            t.Eq("paging: second page name", "6  wp5_a 6", v.Rows[0].Name);
            t.Eq("paging: clamp high", 1, Build(stack, page: 9).Page);
            t.Eq("paging: clamp low", 0, Build(stack, page: -3).Page);
            t.Eq("paging: page of 6", 1, Model.PageOf(6));
            t.Eq("paging: page of 4", 0, Model.PageOf(4));
            t.Eq("paging: page of -1", 0, Model.PageOf(-1));
        }

        [SelfTest("LayersPanel: picker options")]
        static void Pickers(SelfTestTally t)
        {
            DatasetColumnar ds = Data();
            List<Model.Option> cols = Model.ColumnOptions(ds, "None");
            t.Eq("cols: count", 5, cols.Count);
            t.Eq("cols: none first", -1, cols[0].Value);
            t.Eq("cols: second", "X", cols[1].Label);
            t.Eq("cols: value", 3, cols[4].Value);
            t.Eq("cols: without none", 4, Model.ColumnOptions(ds, null).Count);
            t.Eq("cols: null dataset", 1, Model.ColumnOptions(null, "None").Count);

            List<Model.Option> cat = Model.FilterValueOptions(ds, 3);
            t.Eq("values: count", 4, cat.Count);
            t.Eq("values: any first", Model.AnyValueOption, cat[0].Label);
            t.Eq("values: any is empty label", "", cat[0].Text);
            t.Eq("values: sorted", "a,b,c", cat[1].Text + "," + cat[2].Text + "," + cat[3].Text);
            List<Model.Option> num = Model.FilterValueOptions(ds, 1);
            t.Eq("values: numeric skips empty cell", 4, num.Count);
            t.Eq("values: numeric sorted", "10", num[1].Text);
            t.Eq("values: capped", 3, Model.FilterValueOptions(ds, 0, 2).Count);
            t.Eq("values: bad column", 1, Model.FilterValueOptions(ds, 9).Count);

            var stack = new LayerStack();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            List<Model.Option> sub = Model.SubsetColumnOptions(a);
            t.Eq("subset cols: count", 5, sub.Count);
            t.Eq("subset cols: first copies filters", -1, sub[0].Value);
            t.Eq("subset cols: all rows without filters", "All rows of wp5_a", sub[0].Label);

            List<Model.Option> kinds = Model.ComputedKindOptions();
            t.Eq("kinds: five", 5, kinds.Count);
            t.Eq("kinds: trendline first", (int)ComputedLayerKind.Trendline, kinds[0].Value);
            t.Eq("kinds: names", "Smoothed path", kinds[2].Label);

            List<Model.Option> files = Model.DatasetOptions(new[] { "moles.cdataset", "movement.cdataset" });
            t.Eq("files: label", "moles", files[0].Label);
            t.Eq("files: text", "movement.cdataset", files[1].Text);

            t.Near("ref step: numeric", 1f, Model.ReferenceStep(AxisScale.Numeric(0f, 20f)));
            t.Near("ref step: unmapped", 1f, Model.ReferenceStep(default));
            t.Near("ref step: flat", 1f, Model.ReferenceStep(AxisScale.Numeric(3f, 3f)));
        }

        [SelfTest("LayersPanel: signature")]
        static void Signature(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Data();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            string warning = "";
            System.Func<PlotLayer, string> w = _ => warning;
            int s0 = Model.Signature(stack, w);
            t.Eq("sig: stable", s0, Model.Signature(stack, w));
            a.ColorColumnIndex = 1;
            int s1 = Model.Signature(stack, w);
            t.True("sig: colour column", s1 != s0);
            warning = "x";
            t.True("sig: warning", Model.Signature(stack, w) != s1);
            t.Eq("sig: null stack", 0, Model.Signature(null, w));
        }

        [SelfTest("LayersPanel: manager requests the panel uses")]
        static void ManagerRequests(SelfTestTally t)
        {
            DatasetColumnar ds = Data();
            SelfTestKit.WithManager(m =>
            {
                m.RequestLoadDatasetRpc(ds.Name);
                m.RequestAddDatasetLayerRpc(ds.Name);
                t.Eq("mgr: two layers", 2, m.Layers.Count);
                string second = m.ActiveLayerId;
                t.Eq("mgr: badge", "Editing: " + m.ActiveLayer.Name, Model.BadgeText(m.Stack));

                m.RequestSetLayerColourRpc(second, PlayerProfile.Palette[5]);
                t.Eq("mgr: palette colour round trip", 5, Model.Build(m.Stack, null, m.IsLayerShuffleUnlocked, 0).Props.ColourIndex);
                m.RequestSetLayerShuffleUnlockedRpc(second, true);
                t.Eq("mgr: shuffle unlock shown", "Shuffle: unlocked", Model.Build(m.Stack, null, m.IsLayerShuffleUnlocked, 0).Props.ShuffleText);
                m.RequestSetLayerOpacityRpc(second, LayersPanelModel.OpacitySteps[3]);
                t.Eq("mgr: opacity step", 3, Model.Build(m.Stack, null, null, 0).Props.OpacityStep);
                m.RequestAddSubsetLayerRpc(second, 3, "a");
                t.Eq("mgr: subset added", 3, m.Layers.Count);
                t.Eq("mgr: subset row", "Subset of " + m.Layers[1].Name + ": a", Model.Build(m.Stack, null, null, 0).Rows[2].Tags);
                m.RequestRemoveLayerRpc(m.ActiveLayerId);
                m.RequestRemoveLayerRpc(m.ActiveLayerId);
                t.Eq("mgr: one left", 1, m.Layers.Count);
                t.True("mgr: no badge with one layer", Model.BadgeText(m.Stack) == null);
            }, (ds.Name, ds));
        }
    }
}
