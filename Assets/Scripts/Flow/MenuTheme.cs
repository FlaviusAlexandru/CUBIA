using UnityEngine;

namespace DataViz.Flow
{
    /// <summary>
    /// STUB — owner: menu agent. One look for every CUBIA panel (summoned menu, wizard, lobby):
    /// translucent light panels, an accent, font sizes, tile size. The asset lives at
    /// Resources/CUBIAMenuTheme.asset so any scene can load it with MenuTheme.Load().
    /// Wizard and lobby panels should read their colours from here.
    /// </summary>
    [CreateAssetMenu(menuName = "CUBIA/Menu Theme", fileName = "CUBIAMenuTheme")]
    public class MenuTheme : ScriptableObject
    {
        public Color PanelColor = new Color(0.93f, 0.95f, 0.98f, 0.82f);
        public Color PanelOutline = new Color(0.55f, 0.75f, 0.95f, 0.9f);
        public Color Accent = new Color(0.20f, 0.55f, 0.95f, 1f);
        public Color Text = new Color(0.10f, 0.13f, 0.18f, 1f);
        public Color TextMuted = new Color(0.35f, 0.40f, 0.48f, 1f);
        public Color IconTint = new Color(0.15f, 0.40f, 0.75f, 1f);
        public float TitleFontSize = 36f;
        public float BodyFontSize = 22f;

        static MenuTheme s_Cached;

        public static MenuTheme Load()
        {
            if (s_Cached == null)
                s_Cached = Resources.Load<MenuTheme>("CUBIAMenuTheme");
            if (s_Cached == null)
                s_Cached = CreateInstance<MenuTheme>(); // defaults until the asset exists
            return s_Cached;
        }
    }
}
