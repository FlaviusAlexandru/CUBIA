using System.Linq;
using Unity.VectorGraphics.Editor;
using UnityEditor;
using UnityEngine;

namespace DataViz.Networking.EditorTools
{
    /// <summary>
    /// Loads the lobby/session line icons (Assets/UI/Icons/lobby_*.svg) as sprites for the builders,
    /// making sure each SVG is imported as a textured sprite usable by a uGUI Image.
    /// </summary>
    public static class UiIconsLoader
    {
        const string k_Folder = "Assets/UI/Icons/";

        public static UiIcons Load() => new()
        {
            Back = Get("lobby_back"),
            Host = Get("lobby_host"),
            Join = Get("lobby_join"),
            Leave = Get("lobby_leave"),
            Mic = Get("lobby_mic"),
            MicOff = Get("lobby_mic_off"),
            Align = Get("lobby_align"),
            Presenter = Get("lobby_presenter"),
            Users = Get("lobby_users"),
            Wizard = Get("lobby_wizard"),
            Check = Get("lobby_check"),
            Warning = Get("lobby_warning"),
        };

        static Sprite Get(string name)
        {
            string path = k_Folder + name + ".svg";
            if (AssetImporter.GetAtPath(path) == null)
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            if (AssetImporter.GetAtPath(path) is SVGImporter importer && importer.SvgType != SVGType.TexturedSprite)
            {
                importer.SvgType = SVGType.TexturedSprite;
                importer.ViewportOptions = Unity.VectorGraphics.ViewportOptions.PreserveViewport; // icons share one 48x48 box
                importer.KeepTextureAspectRatio = true;
                importer.TextureSize = 256;
                importer.SampleCount = 4;
                importer.FilterMode = FilterMode.Bilinear;
                importer.WrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            Sprite sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
            if (sprite == null)
                Debug.LogWarning($"[UiIconsLoader] No sprite for {path}; that widget shows text only.");
            return sprite;
        }
    }
}
