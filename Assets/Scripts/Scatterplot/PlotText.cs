namespace DataViz
{
    /// <summary>
    /// Size of the text drawn in the plot (PF-02, pilot 2: "all plot text 1.5x larger"): axis
    /// titles, tick labels, the legend and point labels multiply their own base font size by
    /// Scale. One constant, so the serialised base sizes in the scene stay as they are. The text
    /// stays white; readability comes from the dark studio (DarkStudio).
    /// </summary>
    public static class PlotText
    {
        public const float Scale = 1.5f;

        /// <summary>The font size to use for a base size.</summary>
        public static float Size(float baseSize) => baseSize * Scale;
    }
}
