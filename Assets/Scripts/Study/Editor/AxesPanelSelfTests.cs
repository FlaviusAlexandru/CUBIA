using DataViz.MenuUI;

namespace DataViz.Study
{
    /// <summary>AxesPanelModel: the Map tab's axis controls (spec "axis scales and real proportions", ASR-08, ASR-14).</summary>
    static class AxesPanelSelfTests
    {
        static AxisBounds Slab()
        {
            var b = new AxisBounds();
            b.Set(0, AxisScale.Numeric(0f, 6f));
            b.Set(1, AxisScale.Numeric(0f, 2f));
            b.Set(2, AxisScale.Numeric(20f, 60f));
            return b;
        }

        [SelfTest("AxesPanelModel view")]
        static void View(SelfTestTally t)
        {
            AxisSettings s = AxisSettings.CreateDefault();
            AxesPanelModel.View v = AxesPanelModel.Build(s, Slab(), 0);
            t.Eq("aspect text", "Proportional", v.AspectText);
            t.True("padding 5 % highlighted", v.PaddingDefault && !v.PaddingZero);
            t.Eq("row name", "Y", v.Rows[1].Name);
            t.True("numeric row", v.Rows[2].Numeric);
            t.Eq("mode auto", (int)AxisRangeMode.Auto, v.Rows[0].Mode);
            t.True("in group", v.Rows[0].InGroup);
            t.Eq("limits from data", "20 to 60", v.Rows[2].LimitsText);
            t.Eq("no warning", "", v.Warning);

            s.Aspect = (byte)AxisAspect.FitEach;
            s.Padding = 0f;
            s.Modes[0] = (byte)AxisRangeMode.Limits;
            s.LimitMins[0] = -1f;
            s.LimitMaxs[0] = 7.5f;
            s.ReverseMask = 2;
            s.GroupMask = 3;
            v = AxesPanelModel.Build(s, Slab(), 0);
            t.Eq("fit text", "Fit each axis", v.AspectText);
            t.True("padding 0 highlighted", v.PaddingZero && !v.PaddingDefault);
            t.Eq("limits stored", "-1 to 7.5", v.Rows[0].LimitsText);
            t.Eq("mode limits", (int)AxisRangeMode.Limits, v.Rows[0].Mode);
            t.True("Y reversed", v.Rows[1].Reversed && !v.Rows[0].Reversed);
            t.True("Z out of group", !v.Rows[2].InGroup);

            var cat = Slab();
            cat.Set(1, AxisScale.Category(new[] { "a", "b" }));
            cat.Set(2, new AxisScale());
            v = AxesPanelModel.Build(AxisSettings.CreateDefault(), cat, 0);
            t.True("categorical not numeric", !v.Rows[1].Numeric);
            t.True("unmapped not numeric", !v.Rows[2].Numeric);
            t.Eq("not numeric text", AxesPanelModel.NotNumericText, v.Rows[1].LimitsText);
        }

        [SelfTest("AxesPanelModel flattened warning")]
        static void Flattened(SelfTestTally t)
        {
            AxesPanelModel.View v = AxesPanelModel.Build(AxisSettings.CreateDefault(), Slab(), 4);
            t.Eq("one warning", "Z is drawn at under 5 % of the cube", v.Warning);
            t.Eq("one fix", "Fit Z on its own", v.FixText);
            v = AxesPanelModel.Build(AxisSettings.CreateDefault(), Slab(), 6);
            t.Eq("two warning", "Y and Z are drawn at under 5 % of the cube", v.Warning);
            t.Eq("two fix", "Fit Y and Z on their own", v.FixText);
            t.Eq("three names", "X, Y and Z", AxesPanelModel.MaskNames(7));
        }

        [SelfTest("AxesPanelModel limits parsing")]
        static void Parsing(SelfTestTally t)
        {
            t.True("space", AxesPanelModel.TryParseLimits("0 6", out float a, out float b, out _) && a == 0f && b == 6f);
            t.True("swapped", AxesPanelModel.TryParseLimits("6 -1.5", out a, out b, out _) && a == -1.5f && b == 6f);
            t.True("to", AxesPanelModel.TryParseLimits("2 to 3", out a, out b, out _) && a == 2f && b == 3f);
            t.True("extra spaces", AxesPanelModel.TryParseLimits("  0   6 ", out a, out b, out _) && b == 6f);
            t.True("one number refused", !AxesPanelModel.TryParseLimits("6", out _, out _, out string e1) && e1 != null);
            t.True("equal refused", !AxesPanelModel.TryParseLimits("3 3", out _, out _, out string e2) && e2 != null);
            t.True("garbage refused", !AxesPanelModel.TryParseLimits("a b", out _, out _, out _));
            t.Eq("keyboard text from data", "20 60", AxesPanelModel.KeyboardText(AxisSettings.CreateDefault(), Slab(), 2));
        }

        [SelfTest("AxesPanelModel signature")]
        static void Signature(SelfTestTally t)
        {
            AxisSettings s = AxisSettings.CreateDefault();
            int a = AxesPanelModel.Signature(s, Slab(), 0);
            t.True("same input same signature", a == AxesPanelModel.Signature(AxisSettings.CreateDefault(), Slab(), 0));
            s.ReverseMask = 1;
            t.True("settings change it", a != AxesPanelModel.Signature(s, Slab(), 0));
            t.True("flattened changes it", a != AxesPanelModel.Signature(AxisSettings.CreateDefault(), Slab(), 4));
        }
    }
}
