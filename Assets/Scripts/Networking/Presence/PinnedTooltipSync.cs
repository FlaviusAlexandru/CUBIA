using TMPro;
using Unity.Netcode;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Shares the dataset row a player has pinned in their tooltip (<see cref="GPUPointInteractable.PinnedRowIndex"/>).
    /// The owner writes the row; every other player sees a marker in the owner's colour on that point
    /// with a "Name: Row #n" label. Hidden while the local dataset doesn't match the host's.
    /// </summary>
    public class PinnedTooltipSync : NetworkBehaviour
    {
        const float k_SearchInterval = 1f;

        [SerializeField] NetworkPlayerAvatar m_Avatar;
        [SerializeField] Transform m_Marker;
        [SerializeField] TextMeshPro m_Label;
        [Tooltip("Height of the label above the pinned point (metres).")]
        [SerializeField] float m_LabelHeight = 0.05f;

        readonly NetworkVariable<int> m_PinnedRow = new(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>Dataset row this player has pinned, or -1.</summary>
        public int PinnedRow => m_PinnedRow.Value;

        GPUPointInteractable m_Points;
        float m_NextSearchTime;
        // Cached lookup: plot-local position of (m_CachedRow) at points version m_CachedVersion.
        int m_CachedRow = -1;
        int m_CachedVersion = -1;
        bool m_CachedFound;
        Vector3 m_CachedLocal;
        Camera m_Camera;

        public override void OnNetworkSpawn()
        {
            m_Marker.gameObject.SetActive(false);
            m_Avatar.OnProfileChanged += RefreshLabel;
            m_PinnedRow.OnValueChanged += OnPinnedRowChanged;
            RefreshLabel();
        }

        public override void OnNetworkDespawn()
        {
            m_Avatar.OnProfileChanged -= RefreshLabel;
            m_PinnedRow.OnValueChanged -= OnPinnedRowChanged;
        }

        void LateUpdate()
        {
            if (!IsSpawned || !FindPoints()) return;

            if (IsOwner)
            {
                int row = m_Points.PinnedRowIndex;
                if (row != m_PinnedRow.Value) m_PinnedRow.Value = row;
                return;
            }

            bool show = m_PinnedRow.Value >= 0
                && SharedPlotAuthority.DatasetStatus == DatasetMatch.Match
                && TryGetPinnedLocal();
            m_Marker.gameObject.SetActive(show);
            if (!show) return;

            Vector3 world = m_Points.transform.TransformPoint(m_CachedLocal);
            m_Marker.position = world;

            if (m_Camera == null) m_Camera = Camera.main;
            Transform label = m_Label.transform;
            label.position = world + Vector3.up * m_LabelHeight;
            if (m_Camera != null)
            {
                Vector3 away = label.position - m_Camera.transform.position;
                if (away.sqrMagnitude > 1e-6f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        bool FindPoints()
        {
            if (m_Points != null) return true;
            if (Time.unscaledTime < m_NextSearchTime) return false;
            m_NextSearchTime = Time.unscaledTime + k_SearchInterval;
            m_Points = FindAnyObjectByType<GPUPointInteractable>();
            return m_Points != null;
        }

        /// <summary>Refreshes m_CachedLocal when the row or the plotted points changed.</summary>
        bool TryGetPinnedLocal()
        {
            int row = m_PinnedRow.Value;
            if (row != m_CachedRow || m_Points.PointDataVersion != m_CachedVersion)
            {
                m_CachedRow = row;
                m_CachedVersion = m_Points.PointDataVersion;
                m_CachedFound = m_Points.TryGetRowLocalPosition(row, out m_CachedLocal);
            }
            return m_CachedFound;
        }

        void OnPinnedRowChanged(int previous, int current) => RefreshLabel();

        void RefreshLabel()
        {
            m_Label.text = $"{m_Avatar.PlayerName}: Row #{m_PinnedRow.Value + 1}";
            m_Label.color = m_Avatar.PlayerColour;
        }
    }
}
