using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.MenuUI
{
    /// <summary>
    /// A single Shuffle lock toggle (on = Locked) shown next to its field in a menu panel, e.g. the
    /// X lock beside the X dropdown. Like ShuffleLockPanel (the Settings overview) it only shows and
    /// edits the manager's lock state, so both stay in sync: every change goes through
    /// RequestSetFieldLockedRpc and both redraw on OnPlotSettingsChanged.
    ///
    /// Subscribes only while enabled; panels are hidden most of the time and OnEnable redraws.
    /// </summary>
    public class FieldLockToggle : MonoBehaviour
    {
        public MappingTarget m_Target;
        [Tooltip("On = Locked (Shuffle keeps this field's label).")]
        public Toggle m_Toggle;
        [Tooltip("Shows \"<Field>: Locked\" / \"<Field>: Unlocked\".")]
        public TMP_Text m_Label;
        [Tooltip("Faded while the field can't be unlocked (Size while Fixed).")]
        public CanvasGroup m_Group;

        private const float k_DisabledAlpha = 0.4f;

        private MultiplayerScatterplotManager m_Manager;
        private bool m_IsUpdatingUI;

        private void OnEnable()
        {
            m_Manager = MultiplayerScatterplotManager.Instance;
            if (m_Toggle != null)
                m_Toggle.onValueChanged.AddListener(OnToggleChanged);
            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged += Refresh;
                m_Manager.OnDatasetLoaded += Refresh;
            }
            Refresh();
        }

        private void OnDisable()
        {
            if (m_Toggle != null)
                m_Toggle.onValueChanged.RemoveListener(OnToggleChanged);
            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged -= Refresh;
                m_Manager.OnDatasetLoaded -= Refresh;
            }
        }

        private void OnToggleChanged(bool isOn)
        {
            if (m_IsUpdatingUI || m_Manager == null) return;

            m_Manager.RequestSetFieldLockedRpc(m_Target, isOn);
            Refresh(); // a refused change (Size while Fixed) raises no event: put the toggle back
        }

        private void Refresh()
        {
            if (m_Manager == null || m_IsUpdatingUI) return;

            m_IsUpdatingUI = true;

            bool locked = m_Manager.IsFieldLocked(m_Target);
            bool canUnlock = m_Manager.CanUnlockField(m_Target);

            if (m_Toggle != null)
            {
                m_Toggle.isOn = locked;
                m_Toggle.interactable = canUnlock;
            }
            if (m_Label != null)
                m_Label.text = $"{ShuffleLocks.DisplayName(m_Target)}: {(locked ? "Locked" : "Unlocked")}";
            if (m_Group != null)
                m_Group.alpha = canUnlock ? 1f : k_DisabledAlpha;

            m_IsUpdatingUI = false;
        }
    }
}
