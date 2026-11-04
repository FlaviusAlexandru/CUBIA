using UnityEngine;

namespace DataViz.Lobby
{
    /// <summary>
    /// Lobby mic test: while running, records the default microphone into a short looping clip and
    /// shows its loudness by stretching m_Fill (anchored to the left of its bar). Nothing is sent
    /// anywhere; voice chat (Vivox) keeps its own capture and mute state. Stops when disabled.
    /// </summary>
    public class MicLevelMeter : MonoBehaviour
    {
        const int k_SampleRate = 16000;
        const int k_Window = 512;          // samples per reading (32 ms)
        const float k_FloorDb = -60f;      // silence
        const float k_Decay = 6f;          // how fast the bar falls (1/s); it rises instantly

        [SerializeField] RectTransform m_Fill;

        AudioClip m_Clip;
        string m_Device;
        readonly float[] m_Samples = new float[k_Window];
        float m_Level;

        public bool IsRunning => m_Clip != null;

        /// <summary>False when the system has no microphone.</summary>
        public static bool HasMicrophone => Microphone.devices.Length > 0;

        public void Init(RectTransform fill) => m_Fill = fill;

        public void SetRunning(bool on)
        {
            if (on == IsRunning) return;
            if (on)
            {
                if (!HasMicrophone) return;
                m_Device = Microphone.devices[0];
                m_Clip = Microphone.Start(m_Device, true, 1, k_SampleRate);
            }
            else
            {
                Microphone.End(m_Device);
                Destroy(m_Clip);
                m_Clip = null;
                m_Level = 0f;
                ShowLevel();
            }
        }

        void OnDisable() => SetRunning(false);

        void Update()
        {
            if (!IsRunning) return;

            float target = 0f;
            int end = Microphone.GetPosition(m_Device);
            if (end >= k_Window)
            {
                m_Clip.GetData(m_Samples, end - k_Window);
                float sum = 0f;
                foreach (float s in m_Samples) sum += s * s;
                float db = 20f * Mathf.Log10(Mathf.Sqrt(sum / k_Window) + 1e-7f);
                target = Mathf.InverseLerp(k_FloorDb, 0f, db);
            }
            m_Level = target > m_Level ? target : Mathf.MoveTowards(m_Level, target, k_Decay * Time.unscaledDeltaTime);
            ShowLevel();
        }

        void ShowLevel()
        {
            if (m_Fill != null)
                m_Fill.anchorMax = new Vector2(m_Level, 1f);
        }
    }
}
