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
    }
}
