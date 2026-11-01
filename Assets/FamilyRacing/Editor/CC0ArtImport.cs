using UnityEditor;

namespace FamilyRacing.EditorTools
{
    // Import rules for the CC0 Poly Haven sets: GL normal maps, linear masks,
    // anisotropic filtering for ground seen at grazing angles, no FBX/OBJ materials.
    public sealed class CC0ArtImport : AssetPostprocessor
    {
        public const string Root = "Assets/FamilyRacing/Art/CC0";
        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root)) return;
            var importer = (TextureImporter)assetImporter;
            if (assetPath.Contains("_nor_gl_")) importer.textureType = TextureImporterType.NormalMap;
            if (assetPath.Contains("_mask_")) importer.sRGBTexture = false;
            if (assetPath.Contains("_diffa_")) { importer.alphaIsTransparency = true; importer.mipMapsPreserveCoverage = true; importer.alphaTestReferenceValue = .5f; }
            importer.anisoLevel = 8; importer.mipmapEnabled = true; importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
        }
        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Root)) return;
            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false; importer.importCameras = false; importer.importLights = false;
            importer.isReadable = false; importer.globalScale = 1f;
        }
    }
}
