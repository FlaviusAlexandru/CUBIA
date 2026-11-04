using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Android (Quest): asks for the microphone once at start-up, so Vivox voice chat and the
    /// lobby's mic test work. Desktop needs no permission; this compiles to nothing there.
    /// </summary>
    static class AndroidMicPermission
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Request()
        {
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
        }
#endif
    }
}
