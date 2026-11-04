using Unity.VectorGraphics;
using Unity.VectorGraphics.Editor;
using UnityEditor;
using UnityEngine;

namespace DataViz.MenuUI.EditorTools
{
    /// <summary>
    /// SVG files under Assets/UI/Icons/ import as textured sprites, usable in a uGUI Image, the
    /// first time they are imported (later manual changes in the inspector are kept). Icons are
    /// single-colour white line art, so Image.color tints them.
    /// </summary>
    public class MenuIconImportSettings : AssetPostprocessor
    {
        public const string IconFolder = "Assets/UI/Icons/";

        private void OnPreprocessAsset()
        {
            if (!assetPath.StartsWith(IconFolder) || !assetPath.EndsWith(".svg"))
                return;
            if (assetImporter is not SVGImporter importer || !importer.importSettingsMissing)
                return;

            importer.SvgType = SVGType.TexturedSprite;
            importer.ViewportOptions = ViewportOptions.PreserveViewport; // keep the 64x64 viewBox so icons share a size
            importer.KeepTextureAspectRatio = true;
            importer.TextureSize = 256;
            importer.SampleCount = 4;                // antialiased edges
            importer.FilterMode = FilterMode.Bilinear;
            importer.WrapMode = TextureWrapMode.Clamp;
        }
    }
}
