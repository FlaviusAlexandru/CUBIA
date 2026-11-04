using DataViz.Flow;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.MenuUI
{
    /// <summary>
    /// Wires a menu button to one of the flow entry points (lobby, import wizard). Static methods
    /// can't be persistent UnityEvent targets, so the listener is added here at runtime.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class MenuActionButton : MonoBehaviour
    {
        public enum MenuAction
        {
            EnterMultiplayerLobby,
            ChangeDataset,
            RunImportWizard
        }

        public MenuAction m_Action;

        private void Awake()
        {
            GetComponent<Button>().onClick.AddListener(Invoke);
        }

        private void Invoke()
        {
            switch (m_Action)
            {
                case MenuAction.EnterMultiplayerLobby: MultiplayerEntry.RequestEnterLobby(); break;
                case MenuAction.ChangeDataset: ImportWizardEntry.OpenChangeDataset(); break;
                case MenuAction.RunImportWizard: ImportWizardEntry.RunForCurrentDataset(); break;
            }
        }
    }
}
