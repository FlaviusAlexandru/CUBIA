using UnityEngine;

namespace DataViz.Study
{
    /// <summary>
    /// Point colours stay readable on the dark studio (PF-02): no step of the numeric colour scale
    /// may be dark. The old blue-to-red scale went through dark purple (luminance about 0.1).
    /// </summary>
    public static class PointColourSelfTests
    {
        const float k_MinLuminance = 0.35f;

        static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        [SelfTest("Numeric point colours are light on a dark background")]
        static void NumericColoursAreLight(SelfTestTally t)
        {
            for (int i = 0; i <= 10; i++)
            {
                float norm = i / 10f;
                Color c = LayerPointBuilder.NumericColour(norm);
                t.True($"luminance at {norm:0.0} >= {k_MinLuminance}", Luminance(c) >= k_MinLuminance);
            }
            Color low = LayerPointBuilder.NumericColour(0f), high = LayerPointBuilder.NumericColour(1f);
            t.True("low end is bluish", low.b > low.r);
            t.True("high end is reddish", high.r > high.b);
            t.True("clamped below 0", LayerPointBuilder.NumericColour(-1f) == low);
            t.True("clamped above 1", LayerPointBuilder.NumericColour(2f) == high);
        }

        [SelfTest("Category colours are bright, saturated and distinct")]
        static void CategoryColoursAreDistinct(SelfTestTally t)
        {
            var palette = LayerPointBuilder.CategoricalPalette;
            t.Eq("twelve colours", 12, palette.Count);
            for (int i = 0; i < palette.Count; i++)
            {
                Color c = palette[i];
                t.True($"category {i} luminance >= {k_MinLuminance}", Luminance(c) >= k_MinLuminance);
                // All but the white entry are clearly coloured, not greyish.
                float chroma = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) - Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                if (i != 8) t.True($"category {i} saturated", chroma >= 0.4f);
                for (int j = 0; j < i; j++)
                {
                    Color d = palette[j];
                    float distance = Mathf.Sqrt((c.r - d.r) * (c.r - d.r) + (c.g - d.g) * (c.g - d.g) + (c.b - d.b) * (c.b - d.b));
                    t.True($"categories {j} and {i} differ", distance >= 0.3f);
                }
            }
        }
    }
}
