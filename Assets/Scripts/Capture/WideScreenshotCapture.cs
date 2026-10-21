using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Takes a very high-resolution, wide-angle screenshot from the VR user's head position.
/// Renders with a hidden off-screen camera, so the image size and field of view are
/// independent of the monitor and the headset. Press the capture key in Play mode, or use
/// the component's context menu ("Capture Now") in the Editor.
/// Screenshots are never overwritten: name clashes get a _2, _3, ... suffix.
/// </summary>
[DisallowMultipleComponent]
public class WideScreenshotCapture : MonoBehaviour
{
    [Header("Trigger")]
#if ENABLE_INPUT_SYSTEM
    [Tooltip("Keyboard key that triggers a capture in Play mode.")]
    public Key captureKey = Key.Space;
#endif

    [Header("Source")]
    [Tooltip("Camera whose pose is used (the XR head camera). Defaults to Camera.main.")]
    public Camera headCamera;

    [Header("Image")]
    [Min(16)] public int width = 7680;
    [Min(16)] public int height = 4320;
    [Tooltip("Horizontal field of view in degrees. Headsets show roughly 100-110 deg; larger values show more of the scene.")]
    [Range(20f, 170f)] public float horizontalFieldOfView = 100f;
    [Tooltip("MSAA samples used for the capture render (1, 2, 4 or 8).")]
    public int msaaSamples = 8;

    [Header("Framing")]
    [Tooltip("Remove head roll and pitch so the horizon is level (recommended for figures).")]
    public bool levelHorizon = true;
    [Tooltip("Keep head pitch when levelHorizon is on (only roll is removed).")]
    public bool keepPitch = true;
    [Tooltip("Move the capture camera backwards along the view direction, in metres, to fit more in frame.")]
    public float pullBack = 0f;
    [Tooltip("Extra vertical offset in metres.")]
    public float heightOffset = 0f;

    [Header("Clean-up")]
    [Tooltip("Objects hidden only while the capture renders (e.g. controller rays, cursor sphere, hand models).")]
    public List<GameObject> hideDuringCapture = new List<GameObject>();

    [Header("Output")]
    [Tooltip("Folder relative to the project root (outside Assets) or an absolute path.")]
    public string outputFolder = "Screenshots";
    public string filePrefix = "cubia";

    Camera _captureCamera;

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb[captureKey].wasPressedThisFrame)
            Capture();
#endif
    }

    [ContextMenu("Capture Now")]
    public void Capture()
    {
        var source = headCamera != null ? headCamera : Camera.main;
        if (source == null) { Debug.LogError("[WideScreenshot] No head camera found."); return; }

        int max = SystemInfo.maxTextureSize;
        int w = Mathf.Min(width, max), h = Mathf.Min(height, max);
        if (w != width || h != height)
            Debug.LogWarning($"[WideScreenshot] Clamped to {w}x{h} (GPU max texture size {max}).");

        var cam = GetCaptureCamera(source);
        PoseCamera(cam, source.transform);
        cam.aspect = (float)w / h;
        cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(horizontalFieldOfView, cam.aspect);

        var desc = new RenderTextureDescriptor(w, h, GraphicsFormat.R8G8B8A8_SRGB, 24)
        {
            msaaSamples = Mathf.ClosestPowerOfTwo(Mathf.Clamp(msaaSamples, 1, 8)),
            useMipMap = false,
        };
        var rt = RenderTexture.GetTemporary(desc);
        var resolved = RenderTexture.GetTemporary(w, h, 0, GraphicsFormat.R8G8B8A8_SRGB);

        var hidden = new List<GameObject>();
        foreach (var go in hideDuringCapture)
            if (go != null && go.activeSelf) { go.SetActive(false); hidden.Add(go); }

        Texture2D tex = null;
        try
        {
            RenderInto(cam, rt);
            Graphics.Blit(rt, resolved); // resolves MSAA

            var prev = RenderTexture.active;
            RenderTexture.active = resolved;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
        }
        finally
        {
            foreach (var go in hidden) if (go != null) go.SetActive(true);
            RenderTexture.ReleaseTemporary(rt);
            RenderTexture.ReleaseTemporary(resolved);
        }

        byte[] png = tex.EncodeToPNG();
        DestroyNow(tex);

        string folder = Path.IsPathRooted(outputFolder)
            ? outputFolder
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputFolder));
        Directory.CreateDirectory(folder);
        string path = UniquePath(folder, $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{w}x{h}");

        Task.Run(() => File.WriteAllBytes(path, png)).ContinueWith(t =>
        {
            lock (_pendingPaths) _pendingPaths.Remove(path);
            if (t.IsFaulted) Debug.LogError($"[WideScreenshot] Saving failed: {t.Exception?.GetBaseException().Message}");
            else Debug.Log($"[WideScreenshot] Saved {w}x{h}, {horizontalFieldOfView:0} deg FOV -> {path}");
        });
    }

    Camera GetCaptureCamera(Camera source)
    {
        if (_captureCamera == null)
        {
            var go = new GameObject("WideScreenshot Camera") { hideFlags = HideFlags.HideAndDontSave };
            _captureCamera = go.AddComponent<Camera>();
        }

        var cam = _captureCamera;
        cam.CopyFrom(source);
        cam.enabled = false;               // never renders on its own
        cam.targetTexture = null;

        var src = source.GetUniversalAdditionalCameraData();
        var dst = cam.GetUniversalAdditionalCameraData();
        if (src != null && dst != null)
        {
            dst.renderPostProcessing = src.renderPostProcessing;
            dst.antialiasing = src.antialiasing == AntialiasingMode.None ? AntialiasingMode.SubpixelMorphologicalAntiAliasing : src.antialiasing;
            dst.antialiasingQuality = AntialiasingQuality.High;
            dst.renderShadows = src.renderShadows;
            dst.stopNaN = src.stopNaN;
            dst.dithering = src.dithering;
            dst.allowXRRendering = false;  // render to the texture, not the headset
        }
        return cam;
    }

    void PoseCamera(Camera cam, Transform head)
    {
        Quaternion rot = head.rotation;
        if (levelHorizon)
        {
            Vector3 e = rot.eulerAngles;
            rot = Quaternion.Euler(keepPitch ? e.x : 0f, e.y, 0f);
        }
        Vector3 pos = head.position - rot * Vector3.forward * pullBack + Vector3.up * heightOffset;
        cam.transform.SetPositionAndRotation(pos, rot);
    }

    static void RenderInto(Camera cam, RenderTexture rt)
    {
        var request = new RenderPipeline.StandardRequest();
        if (RenderPipeline.SupportsRenderRequest(cam, request))
        {
            request.destination = rt;
            RenderPipeline.SubmitRenderRequest(cam, request);
        }
        else
        {
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
        }
    }

    // Never overwrite: append _2, _3, ... if a file with this name (or a pending save) already exists.
    static readonly HashSet<string> _pendingPaths = new HashSet<string>();
    static string UniquePath(string folder, string baseName)
    {
        lock (_pendingPaths)
        {
            string path = Path.Combine(folder, baseName + ".png");
            for (int i = 2; File.Exists(path) || _pendingPaths.Contains(path); i++)
                path = Path.Combine(folder, $"{baseName}_{i}.png");
            _pendingPaths.Add(path);
            return path;
        }
    }

    static void DestroyNow(UnityEngine.Object o)
    {
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    void OnDestroy()
    {
        if (_captureCamera != null) DestroyNow(_captureCamera.gameObject);
    }

    void OnDrawGizmosSelected()
    {
        var source = headCamera != null ? headCamera : Camera.main;
        if (source == null) return;
        Quaternion rot = source.transform.rotation;
        if (levelHorizon) { var e = rot.eulerAngles; rot = Quaternion.Euler(keepPitch ? e.x : 0f, e.y, 0f); }
        Vector3 pos = source.transform.position - rot * Vector3.forward * pullBack + Vector3.up * heightOffset;
        float aspect = (float)width / height;
        Gizmos.color = Color.cyan;
        Gizmos.matrix = Matrix4x4.TRS(pos, rot, Vector3.one);
        Gizmos.DrawFrustum(Vector3.zero, Camera.HorizontalToVerticalFieldOfView(horizontalFieldOfView, aspect), 3f, 0.05f, aspect);
    }
}
