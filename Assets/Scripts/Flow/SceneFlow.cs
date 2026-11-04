using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;

namespace DataViz.Flow
{
    /// <summary>
    /// Single place that changes scenes, with a fade so VR users never see a hitch. Offline scene
    /// changes only; inside a multiplayer session the lobby agent loads scenes through NGO's scene
    /// manager instead.
    ///
    /// The fade reuses the XR rig's TunnelingVignetteController (as SummonedMenuController does):
    /// close to black in the old scene, load, then open from black in the new scene. Without a
    /// vignette controller it is a plain load.
    /// </summary>
    public static class SceneFlow
    {
        public static bool IsLoading { get; private set; }

        /// <summary>Raised right before a scene change starts (e.g. to save state).</summary>
        public static event Action<string> OnBeforeLoad;

        /// <summary>Fade to / from black duration, in seconds.</summary>
        public const float FadeSeconds = 0.3f;

        public static void Load(string sceneName)
        {
            if (IsLoading)
            {
                Debug.LogWarning($"[SceneFlow] Ignoring Load('{sceneName}'): a scene change is already running.");
                return;
            }

            IsLoading = true;
            OnBeforeLoad?.Invoke(sceneName);
            SceneFadeRunner.Begin(sceneName, () => IsLoading = false);
        }

        /// <summary>
        /// Hidden DontDestroyOnLoad helper that runs the fade coroutine across the scene change.
        /// It is its own ITunnelingVignetteProvider so it never fights the locomotion providers.
        /// </summary>
        sealed class SceneFadeRunner : MonoBehaviour, ITunnelingVignetteProvider
        {
            readonly VignetteParameters m_Parameters = new()
            {
                apertureSize = 0f,   // full black-out, not a partial comfort vignette
                featheringEffect = 0.2f,
                easeInTime = FadeSeconds,
                easeOutTime = FadeSeconds,
                vignetteColor = Color.black,
                vignetteColorBlend = Color.black,
            };

            public VignetteParameters vignetteParameters => m_Parameters;

            public static void Begin(string sceneName, Action onDone)
            {
                var go = new GameObject("[SceneFlow]") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                var runner = go.AddComponent<SceneFadeRunner>();
                runner.StartCoroutine(runner.Run(sceneName, onDone));
            }

            IEnumerator Run(string sceneName, Action onDone)
            {
                TunnelingVignetteController vignette = FindAnyObjectByType<TunnelingVignetteController>();
                if (vignette != null)
                {
                    m_Parameters.easeInTime = FadeSeconds;
                    vignette.BeginTunnelingVignette(this);
                    yield return new WaitForSecondsRealtime(FadeSeconds);
                }

                AsyncOperation load = SceneManager.LoadSceneAsync(sceneName);
                if (load == null) // not in Build Settings; LoadSceneAsync already logged why
                {
                    if (vignette != null)
                        vignette.EndTunnelingVignette(this);
                    Finish(onDone);
                    yield break;
                }

                while (!load.isDone)
                    yield return null;

                // The new scene's vignette starts open: snap it shut, then ease it open.
                vignette = FindAnyObjectByType<TunnelingVignetteController>();
                if (vignette != null)
                {
                    m_Parameters.easeInTime = 0f;
                    vignette.BeginTunnelingVignette(this);
                    yield return null;
                    vignette.EndTunnelingVignette(this);
                }

                Finish(onDone);
            }

            void Finish(Action onDone)
            {
                onDone?.Invoke();
                Destroy(gameObject);
            }
        }
    }
}
