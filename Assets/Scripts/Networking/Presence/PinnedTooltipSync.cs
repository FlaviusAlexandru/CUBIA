using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Shares the point a player has pinned in their tooltip: its layer id and dataset row
    /// (<see cref="GPUPointInteractable.PinnedLayerId"/>, <see cref="GPUPointInteractable.PinnedRowIndex"/>);
    /// a row alone is ambiguous once the plot has several layers. The owner writes both; every other
    /// player sees a marker in the owner's colour on that point with a "Name: Row #n" label
    /// ("Name: Layer row #n" when the plot has several layers). Hidden while the local datasets
    /// don't match the host's, or the point is not plotted (hidden layer, outside the time window).
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
        // Layer ids are short ("L12"); "" = none (or a build without layers).
        readonly NetworkVariable<FixedString32Bytes> m_PinnedLayer = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>Dataset row this player has pinned, or -1.</summary>
        public int PinnedRow => m_PinnedRow.Value;

        /// <summary>Id of the layer the pinned row belongs to, or "".</summary>
        public string PinnedLayerId => m_PinnedLayer.Value.ToString();

        GPUPointInteractable m_Points;
        float m_NextSearchTime;
        // Cached lookup: plot-local position of (m_CachedRow) at points version m_CachedVersion.
        int m_CachedRow = -1;
        string m_CachedLayer = "";
        int m_CachedVersion = -1;
        bool m_CachedFound;
        Vector3 m_CachedLocal;
        Camera m_Camera;

        public override void OnNetworkSpawn()
        {
            m_Marker.gameObject.SetActive(false);
            m_Avatar.OnProfileChanged += RefreshLabel;
            m_PinnedRow.OnValueChanged += OnPinnedRowChanged;
            m_PinnedLayer.OnValueChanged += OnPinnedLayerChanged;
            RefreshLabel();
        }

        public override void OnNetworkDespawn()
        {
            m_Avatar.OnProfileChanged -= RefreshLabel;
            m_PinnedRow.OnValueChanged -= OnPinnedRowChanged;
            m_PinnedLayer.OnValueChanged -= OnPinnedLayerChanged;
        }

        void LateUpdate()
        {
            if (!IsSpawned || !FindPoints()) return;

            if (IsOwner)
            {
                int row = m_Points.PinnedRowIndex;
                string layer = m_Points.PinnedLayerId;
                if (layer.Length > FixedString32Bytes.UTF8MaxLengthInBytes) layer = ""; // ids are "L<n>"; never expected
                if (layer != m_PinnedLayer.Value.ToString()) m_PinnedLayer.Value = new FixedString32Bytes(layer);
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
            string layer = PinnedLayerId;
            if (row != m_CachedRow || layer != m_CachedLayer || m_Points.PointDataVersion != m_CachedVersion)
            {
                m_CachedRow = row;
                m_CachedLayer = layer;
                m_CachedVersion = m_Points.PointDataVersion;
                m_CachedFound = m_Points.TryGetRowLocalPosition(layer, row, out m_CachedLocal);
                RefreshLabel(); // the layer's name may have changed with the plot
            }
            return m_CachedFound;
        }

        void OnPinnedRowChanged(int previous, int current) => RefreshLabel();

        void OnPinnedLayerChanged(FixedString32Bytes previous, FixedString32Bytes current) => RefreshLabel();

        void RefreshLabel()
        {
            m_Label.text = $"{m_Avatar.PlayerName}: {LayerPrefix()}Row #{m_PinnedRow.Value + 1}";
            m_Label.color = m_Avatar.PlayerColour;
        }

        // "<layer name> " when the local plot has several layers and knows the pinned layer.
        string LayerPrefix()
        {
            MultiplayerScatterplotManager manager = m_Points != null && m_Points.m_Visualizer != null
                ? m_Points.m_Visualizer.m_Manager
                : null;
            if (manager == null || manager.Layers.Count <= 1)
                return "";
            PlotLayer layer = manager.Stack.Find(PinnedLayerId);
            return layer != null ? layer.Name + " " : "";
        }
    }
}
