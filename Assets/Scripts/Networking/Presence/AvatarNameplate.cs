using TMPro;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// World-space name label floating above a remote player's head, tinted with their colour and
    /// billboarded to the local camera, plus a small speaker icon while that player is talking
    /// (<see cref="VoiceService.IsSpeaking"/>). Part of the avatar visuals, so never shown for the local player.
    /// </summary>
    public class AvatarNameplate : MonoBehaviour
    {
        [SerializeField] NetworkPlayerAvatar m_Avatar;
        [SerializeField] TextMeshPro m_Label;
        [SerializeField] SpriteRenderer m_SpeakerIcon;
        [Tooltip("Height of the label above the head centre (metres).")]
        [SerializeField] float m_HeightAboveHead = 0.22f;
        [Tooltip("Gap between the speaker icon and the name (metres).")]
        [SerializeField] float m_IconGap = 0.02f;

        static Sprite s_SpeakerSprite;
        Camera m_Camera;

        void Awake()
        {
            m_SpeakerIcon.sprite = GetSpeakerSprite();
            m_SpeakerIcon.enabled = false;
            m_Avatar.OnProfileChanged += Refresh;
        }

        void OnDestroy()
        {
            if (m_Avatar != null) m_Avatar.OnProfileChanged -= Refresh;
        }

        void LateUpdate()
        {
            bool visible = m_Avatar.HasPose;
            m_Label.enabled = visible;
            VoiceService voice = VoiceService.Instance;
            m_SpeakerIcon.enabled = visible && voice != null && voice.IsSpeaking(m_Avatar.OwnerClientId);
            if (!visible) return;

            transform.position = m_Avatar.HeadWorldPosition + Vector3.up * m_HeightAboveHead;

            if (m_Camera == null) m_Camera = Camera.main;
            if (m_Camera != null)
            {
                // Face away from the camera: TextMeshPro reads correctly when viewed along its +Z.
                Vector3 away = transform.position - m_Camera.transform.position;
                if (away.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        void Refresh()
        {
            Color colour = m_Avatar.PlayerColour;
            m_Label.text = m_Avatar.PlayerName;
            m_Label.color = colour;
            m_SpeakerIcon.color = colour;

            // Keep the icon just left of the (centred) name.
            m_Label.ForceMeshUpdate();
            float halfWidth = m_Label.textBounds.size.x * 0.5f;
            float iconHalf = m_SpeakerIcon.transform.localScale.x * 0.5f; // the sprite is 1 unit wide
            m_SpeakerIcon.transform.localPosition = new Vector3(-(halfWidth + m_IconGap + iconHalf), 0f, 0f);
        }

        /// <summary>A 32x32 white speaker-with-sound-waves sprite, generated once (tinted per player).</summary>
        static Sprite GetSpeakerSprite()
        {
            if (s_SpeakerSprite != null) return s_SpeakerSprite;

            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            var white = new Color32(255, 255, 255, 255);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool body = x >= 3 && x <= 9 && y >= 12 && y <= 19;
                // Cone widening from the body (x=9) to x=16.
                float spread = 4f + (x - 9) * 1.4f;
                bool cone = x > 9 && x <= 16 && Mathf.Abs(y - 15.5f) <= spread;
                // Two sound-wave arcs centred on the cone mouth, within +/-45 degrees.
                float dx = x - 16f, dy = y - 15.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                bool inWedge = dx > 0f && Mathf.Abs(dy) <= dx;
                bool wave = inWedge && (Mathf.Abs(r - 7f) <= 1.2f || Mathf.Abs(r - 12f) <= 1.2f);
                if (body || cone || wave) pixels[y * size + x] = white;
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            // pixelsPerUnit = size → the sprite is 1x1 unit; the icon transform scales it.
            s_SpeakerSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            return s_SpeakerSprite;
        }
    }
}
