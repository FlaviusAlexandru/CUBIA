using System;
using DataViz.Flow;
using UnityEngine;

namespace DataViz.Wizard
{
    /// <summary>
    /// First thing the Start scene runs. On the first entry of the app it skips the wizard when a
    /// dataset is forced on the command line (-cubiaDataset &lt;file&gt;) or marked Default on load:
    /// it sets LaunchContext and loads the data scene without showing anything. Otherwise (and on
    /// every re-entry from ImportWizardEntry) it opens the wizard panel at m_PanelAnchor.
    /// </summary>
    public sealed class StartSceneBoot : MonoBehaviour
    {
        public const string DatasetArgument = "-cubiaDataset";

        [Tooltip("Where the wizard panel appears (its forward axis points away from the user).")]
        [SerializeField] Transform m_PanelAnchor;

        // The auto-skip only applies to the app's first Start scene; coming back must show the wizard.
        static bool s_FirstEntryHandled;

        void Start()
        {
            bool reentry = WizardStartRequest.TryConsume(out WizardStep step, out string datasetFile);
            bool firstEntry = !s_FirstEntryHandled;
            s_FirstEntryHandled = true;

            if (!reentry && firstEntry && TryAutoLaunch())
                return;

            Transform anchor = m_PanelAnchor != null ? m_PanelAnchor : transform;
            WizardPanel panel = WizardPanel.Create("Import Wizard Panel", anchor.position, anchor.rotation);
            WizardController.Create(panel, inPlace: false)
                            .Begin(reentry ? step : WizardStep.Welcome, reentry ? datasetFile : LaunchContext.DatasetName);
        }

        static bool TryAutoLaunch()
        {
            string forced = CommandLineDataset();
            string forcedFile = DatasetCatalog.Resolve(forced);
            if (forced != null && forcedFile == null)
                Debug.LogWarning($"[StartSceneBoot] {DatasetArgument} '{forced}' not found in {ColumnarBinaryImporter.DataFolder}.");

            string file = forcedFile ?? DatasetCatalog.Resolve(LaunchPrefs.DefaultDataset);
            if (file == null)
                return false;

            // CameFromWizard: the saved bookmarks pick the first X/Y/Z, as after a wizard run.
            LaunchContext.DatasetName = file;
            LaunchContext.CameFromWizard = true;
            SceneFlow.Load(SceneNames.Data);
            return true;
        }

        static string CommandLineDataset()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], DatasetArgument, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
