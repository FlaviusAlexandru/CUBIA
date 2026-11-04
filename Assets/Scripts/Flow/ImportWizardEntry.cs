using DataViz.Networking;
using DataViz.Wizard;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz.Flow
{
    /// <summary>
    /// Ways back into the import wizard, called from the Settings panel (menu agent).
    /// Offline they return to the Start scene through SceneFlow. In the lobby or a multiplayer
    /// session leaving the scene would break the session, so RunForCurrentDataset shows the column
    /// steps on a panel in place instead (prefs are local to this computer), and OpenChangeDataset
    /// is refused: the host changes the shared dataset from the data scene.
    /// </summary>
    public static class ImportWizardEntry
    {
        const float k_InPlaceDistance = 1.3f;

        static WizardController s_InPlace;

        /// <summary>Settings > Change dataset…: back to the wizard's dataset step.</summary>
        public static void OpenChangeDataset()
        {
            if (MustStayInScene())
            {
                Debug.LogWarning("[ImportWizardEntry] Change dataset is not available in the lobby or a multiplayer session.");
                return;
            }

            WizardStartRequest.Set(WizardStep.ChooseDataset, CurrentDatasetFile());
            SceneFlow.Load(SceneNames.Start);
        }

        /// <summary>Settings > Run import wizard: re-run the column steps for the current dataset
        /// (also allowed in the lobby and during a session; prefs are local).</summary>
        public static void RunForCurrentDataset()
        {
            string dataset = CurrentDatasetFile();
            if (string.IsNullOrEmpty(dataset))
            {
                Debug.LogWarning("[ImportWizardEntry] No dataset is loaded, so there are no columns to sort.");
                return;
            }

            if (!MustStayInScene())
            {
                WizardStartRequest.Set(WizardStep.MarkColumns, dataset);
                SceneFlow.Load(SceneNames.Start);
                return;
            }

            if (s_InPlace != null)
                return; // already open

            Camera head = Camera.main;
            Vector3 position = Vector3.up * 1.4f;
            Quaternion rotation = Quaternion.identity;
            if (head != null)
            {
                // Like SummonedMenuController: level in front of the head, facing it.
                Vector3 forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
                position = head.transform.position + forward * k_InPlaceDistance;
                rotation = Quaternion.LookRotation(forward);
            }

            WizardPanel panel = WizardPanel.Create("Import Wizard Panel (in place)", position, rotation);
            s_InPlace = WizardController.Create(panel, inPlace: true);
            s_InPlace.Begin(WizardStep.MarkColumns, dataset);
        }

        static bool MustStayInScene() =>
            SceneManager.GetActiveScene().name == SceneNames.Lobby
            || (SessionService.Instance != null && SessionService.Instance.IsInSession)
            || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening);

        /// <summary>The data scene's loaded dataset file, else the one the Start scene chose.</summary>
        static string CurrentDatasetFile()
        {
            MultiplayerScatterplotManager manager = MultiplayerScatterplotManager.Instance;
            if (manager != null && manager.LoadedDataset != null)
                return manager.CurrentDatasetName;
            return LaunchContext.DatasetName;
        }
    }
}
