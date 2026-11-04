using TMPro;
using UnityEngine;

namespace DataViz.Wizard
{
    /// <summary>
    /// Font and line icons the import wizard builds its panel with. The wizard's UI is built in
    /// code (the same panel runs in the Start scene and in place in the lobby / a session), so the
    /// assets are found through Resources. Created and filled by Tools > CUBIA > Scenes > Build
    /// Start Scene; every field may be empty (TMP's default font, no icons).
    /// </summary>
    public class WizardAssets : ScriptableObject
    {
        public const string ResourceName = "WizardAssets";

        public TMP_FontAsset Font;
        public Sprite Back;
        public Sprite Next;
        public Sprite Dataset;
        public Sprite LeaveOut;
        public Sprite Check;

        static WizardAssets s_Cached;

        public static WizardAssets Load()
        {
            if (s_Cached == null)
                s_Cached = Resources.Load<WizardAssets>(ResourceName);
            if (s_Cached == null)
                s_Cached = CreateInstance<WizardAssets>();
            return s_Cached;
        }
    }
}
