using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataViz.Networking
{
    /// <summary>
    /// The Session island's player list: one row per connected player with a colour dot, the name
    /// and a "host" tag. Rows are clones of m_RowTemplate (an inactive child with children named
    /// "Dot" (Image), "Name" (TMP) and "HostTag"), stacked downwards m_RowSpacing apart.
    /// The dot grows a little while that player is speaking (fed by SetSpeaking).
    /// </summary>
    public class PlayerListView : MonoBehaviour
    {
        public RectTransform m_RowTemplate;
        public float m_RowSpacing = 34f;
        [Tooltip("Dot scale while the player is speaking.")]
        public float m_SpeakingDotScale = 1.35f;

        private class Row
        {
            public RectTransform Root;
            public Image Dot;
            public TMP_Text Name;
            public GameObject HostTag;
            public ulong ClientId;
        }

        private readonly List<Row> m_Rows = new();
        private readonly HashSet<ulong> m_Speaking = new();

        public int RowCount { get; private set; }

        public void SetPlayers(IReadOnlyList<PlayerInfo> players)
        {
            int count = players?.Count ?? 0;
            if (m_RowTemplate == null)
                return;

            m_RowTemplate.gameObject.SetActive(false);

            while (m_Rows.Count < count)
                m_Rows.Add(CreateRow(m_Rows.Count));

            for (int i = 0; i < m_Rows.Count; i++)
            {
                Row row = m_Rows[i];
                bool show = i < count;
                row.Root.gameObject.SetActive(show);
                if (!show)
                    continue;

                PlayerInfo p = players[i];
                row.ClientId = p.ClientId;
                if (row.Dot != null)
                {
                    Color c = p.Colour;
                    c.a = 1f;
                    row.Dot.color = c;
                }
                if (row.Name != null)
                    row.Name.text = string.IsNullOrWhiteSpace(p.Name) ? "Player" : p.Name;
                if (row.HostTag != null)
                    row.HostTag.SetActive(p.IsHost);
                ApplySpeaking(row);
            }

            RowCount = count;
        }

        public void SetSpeaking(ulong clientId, bool speaking)
        {
            if (speaking) m_Speaking.Add(clientId);
            else m_Speaking.Remove(clientId);

            foreach (Row row in m_Rows)
                if (row.ClientId == clientId)
                    ApplySpeaking(row);
        }

        public void ClearSpeaking()
        {
            m_Speaking.Clear();
            foreach (Row row in m_Rows)
                ApplySpeaking(row);
        }

        private void ApplySpeaking(Row row)
        {
            if (row.Dot != null)
                row.Dot.rectTransform.localScale = Vector3.one * (m_Speaking.Contains(row.ClientId) ? m_SpeakingDotScale : 1f);
        }

        private Row CreateRow(int index)
        {
            RectTransform rt = Instantiate(m_RowTemplate, m_RowTemplate.parent);
            rt.name = "PlayerRow" + index;
            rt.anchoredPosition = m_RowTemplate.anchoredPosition - new Vector2(0f, index * m_RowSpacing);

            Transform dot = rt.Find("Dot");
            Transform nameT = rt.Find("Name");
            Transform host = rt.Find("HostTag");

            return new Row
            {
                Root = rt,
                Dot = dot != null ? dot.GetComponent<Image>() : null,
                Name = nameT != null ? nameT.GetComponent<TMP_Text>() : null,
                HostTag = host != null ? host.gameObject : null,
            };
        }
    }
}
