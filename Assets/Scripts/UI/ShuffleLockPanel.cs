using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz
{
    /// <summary>
    /// The Preferences panel's Shuffle section: lock or unlock every field Shuffle can change,
    /// one at a time (a toggle per field, on = Locked), a whole island at once (each island
    /// heading's Lock all / Unlock all button) or everything at once (the global Unlock all and
    /// Lock all buttons). Locks only affect Shuffle; the state lives in
    /// MultiplayerScatterplotManager (per dataset, persisted), this component only shows and
    /// edits it.
    ///
    /// Every control is redrawn from the manager on OnPlotSettingsChanged, so an island or
    /// global button updates every row, and a dataset switch shows that dataset's locks.
    /// While Size is Fixed its toggle is greyed out and m_SizeFixedNote is shown.
    ///
    /// Lives next to ScatterplotUI (an always-active object) rather than on the summoned menu
    /// canvas, which is inactive until summoned, so it is subscribed from the start.
    /// </summary>
    public class ShuffleLockPanel : MonoBehaviour
    {
        [Serializable]
        public class FieldRow
        {
            public MappingTarget Target;
            [Tooltip("On = Locked (Shuffle keeps this field's label).")]
            public Toggle Toggle;
            [Tooltip("Shows \"<Field>: Locked\" / \"<Field>: Unlocked\".")]
            public TMP_Text Label;
            [Tooltip("Optional: faded while the field can't be unlocked (Size while Fixed).")]
            public CanvasGroup Group;
        }

        [Serializable]
        public class IslandRow
        {
            public ShuffleIsland Island;
            [Tooltip("Locks the whole island, or unlocks it when everything in it is already locked.")]
            public Button Button;
            public TMP_Text ButtonText;
        }

        [Header("References")]
        public MultiplayerScatterplotManager m_Manager;

        [Header("Rows")]
        public List<FieldRow> m_FieldRows = new();
        public List<IslandRow> m_IslandRows = new();

        [Header("Global")]
        public Button m_UnlockAllButton;
        public Button m_LockAllButton;

        [Header("Size")]
        [Tooltip("Shown while Size is Fixed: \"Set Size to Mapped to shuffle it\".")]
        public GameObject m_SizeFixedNote;

        public const string k_LockAllText = "Lock all";
        public const string k_UnlockAllText = "Unlock all";
        private const float k_DisabledAlpha = 0.4f;

        private bool m_IsUpdatingUI;
        private bool m_Subscribed;

        private void Start()
        {
            if (m_Manager == null)
                m_Manager = MultiplayerScatterplotManager.Instance;

            foreach (FieldRow row in m_FieldRows)
            {
                if (row.Toggle == null)
                    continue;

                MappingTarget target = row.Target;
                row.Toggle.onValueChanged.AddListener(isOn => OnFieldToggleChanged(target, isOn));
            }

            foreach (IslandRow row in m_IslandRows)
            {
                if (row.Button == null)
                    continue;

                ShuffleIsland island = row.Island;
                row.Button.onClick.AddListener(() => OnIslandButtonClicked(island));
            }

            if (m_UnlockAllButton != null)
                m_UnlockAllButton.onClick.AddListener(() => OnGlobalButtonClicked(false));

            if (m_LockAllButton != null)
                m_LockAllButton.onClick.AddListener(() => OnGlobalButtonClicked(true));

            if (m_Manager != null)
            {
                m_Manager.OnPlotSettingsChanged += Refresh;
                m_Manager.OnDatasetLoaded += Refresh;
                m_Subscribed = true;
            }

            Refresh();
        }

        private void OnDestroy()
        {
            if (m_Manager != null && m_Subscribed)
            {
                m_Manager.OnPlotSettingsChanged -= Refresh;
                m_Manager.OnDatasetLoaded -= Refresh;
            }
        }

        private void OnFieldToggleChanged(MappingTarget target, bool isOn)
        {
            if (m_IsUpdatingUI || m_Manager == null) return;

            m_Manager.RequestSetFieldLockedRpc(target, isOn);
            Refresh(); // a refused change (Size while Fixed) raises no event: put the toggle back
        }

        private void OnIslandButtonClicked(ShuffleIsland island)
        {
            if (m_Manager == null) return;

            m_Manager.RequestSetIslandLockedRpc(island, !m_Manager.IsIslandLocked(island));
            Refresh();
        }

        private void OnGlobalButtonClicked(bool locked)
        {
            if (m_Manager == null) return;

            m_Manager.RequestSetAllLockedRpc(locked);
            Refresh();
        }

        /// <summary>Redraws every row, island button and the Size note from the manager.</summary>
        public void Refresh()
        {
            if (m_Manager == null || m_IsUpdatingUI) return;

            m_IsUpdatingUI = true;

            foreach (FieldRow row in m_FieldRows)
            {
                bool locked = m_Manager.IsFieldLocked(row.Target);
                bool canUnlock = m_Manager.CanUnlockField(row.Target);

                if (row.Toggle != null)
                {
                    row.Toggle.isOn = locked;
                    row.Toggle.interactable = canUnlock;
                }

                if (row.Label != null)
                    row.Label.text = $"{ShuffleLocks.DisplayName(row.Target)}: {(locked ? "Locked" : "Unlocked")}";

                if (row.Group != null)
                    row.Group.alpha = canUnlock ? 1f : k_DisabledAlpha;
            }

            foreach (IslandRow row in m_IslandRows)
            {
                if (row.ButtonText != null)
                    row.ButtonText.text = m_Manager.IsIslandLocked(row.Island) ? k_UnlockAllText : k_LockAllText;
            }

            if (m_SizeFixedNote != null)
                m_SizeFixedNote.SetActive(!m_Manager.SizeMapped);

            m_IsUpdatingUI = false;
        }
    }
}
