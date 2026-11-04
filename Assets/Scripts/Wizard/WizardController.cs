using DataViz.Flow;
using UnityEngine;

namespace DataViz.Wizard
{
    /// <summary>
    /// Runs the import wizard on a WizardPanel: Welcome, Choose a dataset, Mark the important
    /// columns, Leave out the rest, Summary. Every step but Welcome has Back; the two column steps
    /// can be skipped (Skip keeps the saved prefs).
    ///
    /// In the Start scene the summary's "Start visualising" saves the prefs and loads the data
    /// scene. In place (lobby / multiplayer session, see ImportWizardEntry) only the column steps
    /// run, on the current dataset; Done saves the prefs, refreshes the scatterplot manager and
    /// closes the panel without leaving the scene.
    /// </summary>
    public sealed class WizardController : MonoBehaviour
    {
        public WizardPanel Panel { get; private set; }
        public bool InPlace { get; private set; }

        /// <summary>The dataset picked on the dataset step (file name), or null.</summary>
        public string DatasetFile { get; private set; }

        /// <summary>Working copy of the picked dataset's bookmarks / blacklist.</summary>
        public WizardSelection Selection { get; private set; }

        WelcomeStep m_Welcome;
        DatasetStep m_Datasets;
        MarkColumnsStep m_MarkColumns;
        LeaveOutStep m_LeaveOut;
        SummaryStep m_Summary;

        public static WizardController Create(WizardPanel panel, bool inPlace)
        {
            var wizard = panel.gameObject.AddComponent<WizardController>();
            wizard.Panel = panel;
            wizard.InPlace = inPlace;
            wizard.m_Welcome = new WelcomeStep(wizard);
            wizard.m_Datasets = new DatasetStep(wizard);
            wizard.m_MarkColumns = new MarkColumnsStep(wizard);
            wizard.m_LeaveOut = new LeaveOutStep(wizard);
            wizard.m_Summary = new SummaryStep(wizard);
            return wizard;
        }

        /// <summary>Opens at a step. datasetFile pre-selects a dataset (required for column steps).</summary>
        public void Begin(WizardStep step, string datasetFile)
        {
            bool hasDataset = !string.IsNullOrEmpty(datasetFile) && SelectDataset(datasetFile);

            if (InPlace)
            {
                if (!hasDataset)
                {
                    Debug.LogWarning($"[WizardController] Can't read the columns of '{datasetFile}'.");
                    Close();
                    return;
                }
                step = WizardStep.MarkColumns; // in place only the column steps run
            }
            else if (step > WizardStep.ChooseDataset && !hasDataset)
            {
                step = WizardStep.ChooseDataset; // the file is gone: let the user pick another
            }

            GoTo(step);
        }

        public void GoTo(WizardStep step)
        {
            switch (step)
            {
                case WizardStep.Welcome: m_Welcome.Show(); break;
                case WizardStep.ChooseDataset: m_Datasets.Show(); break;
                case WizardStep.MarkColumns: m_MarkColumns.Show(); break;
                case WizardStep.LeaveOut: m_LeaveOut.Show(); break;
                case WizardStep.Summary: m_Summary.Show(); break;
            }
        }

        /// <summary>Back from a step; at the first in-place step this cancels.</summary>
        public void Back(WizardStep from)
        {
            if (InPlace && from == WizardStep.MarkColumns)
                Close();
            else
                GoTo(from - 1);
        }

        /// <summary>Reads the dataset's columns and saved prefs. False when the file can't be read.</summary>
        public bool SelectDataset(string file)
        {
            if (file == DatasetFile && Selection != null)
                return true; // keep unsaved choices when stepping back and forth

            DatasetSummary summary = DatasetCatalog.Full(file);
            if (summary == null)
                return false;

            DatasetFile = file;
            Selection = new WizardSelection(summary);
            return true;
        }

        public string StepLabel(WizardStep step)
        {
            if (step == WizardStep.Welcome)
                return "";
            return InPlace
                ? $"Step {step - WizardStep.MarkColumns + 1} of 3"
                : $"Step {step - WizardStep.ChooseDataset + 1} of 4";
        }

        /// <summary>Summary's main button: save, then start the plot (or apply in place).</summary>
        public void Finish()
        {
            Selection.Save();

            if (InPlace)
            {
                MultiplayerScatterplotManager manager = MultiplayerScatterplotManager.Instance;
                if (manager != null)
                    manager.NotifyColumnPrefsChanged();
                Close();
                return;
            }

            LaunchContext.DatasetName = DatasetFile;
            LaunchContext.CameFromWizard = true;
            SceneFlow.Load(SceneNames.Data);
        }

        public void Close() => Destroy(Panel.gameObject);
    }
}
