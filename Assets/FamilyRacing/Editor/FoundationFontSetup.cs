using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace FamilyRacing.EditorTools
{
    // Packages bilingual text: TTF import settings, generated dynamic
    // FontAssets, text-settings font bindings, and ATG enablement. Repairs
    // existing assets in place; never replaces authored objects or GUIDs.
    public static class FoundationFontSetup
    {
        internal const string LatinFontPath = "Assets/FamilyRacing/Generated/FoundationLatinFont.asset";
        internal const string HebrewFontPath = "Assets/FamilyRacing/Generated/FoundationHebrewFont.asset";
        internal const string SymbolsFontPath = "Assets/FamilyRacing/Generated/FoundationSymbolsFont.asset";

        private const string FontsDir = "Assets/FamilyRacing/Fonts";

        public static void EnsureAssets(PanelSettings panel)
        {
            if (panel == null || panel.textSettings == null)
                throw new InvalidOperationException(
                    "FoundationFontSetup requires a panel with text settings.");
            var latin = EnsureGeneratedFont(LatinFontPath, FontsDir + "/NotoSans-Regular.ttf");
            var hebrew = EnsureGeneratedFont(HebrewFontPath, FontsDir + "/NotoSansHebrew-Regular.ttf");
            var symbols = EnsureGeneratedFont(SymbolsFontPath, FontsDir + "/NotoSansSymbols2-Regular.ttf");

            var text = panel.textSettings;
            if (text.defaultFontAsset == null)
            {
                text.defaultFontAsset = latin;
                EditorUtility.SetDirty(text);
            }
            EnsureFallbacks(latin, hebrew, symbols);
            if (text.defaultFontAsset != latin)
                EnsureFallbacks(text.defaultFontAsset, latin, hebrew, symbols);
            EnableAdvancedText();
            PopulateAtlas(latin, hebrew, symbols, text.defaultFontAsset);
        }

        // Seeds the serialized atlas with the shipped UI repertoire so player
        // cold starts render text against an already-uploaded texture instead
        // of pending glyph additions (PCQA-001/PCQA-005). Runs at setup so
        // regenerated assets re-populate; runtime FoundationFonts.Warmup
        // remains as the belt-and-suspenders net.
        private static void PopulateAtlas(params FontAsset[] fonts)
        {
            foreach (var font in fonts)
                if (font != null &&
                    font.atlasPopulationMode == AtlasPopulationMode.Dynamic)
                    font.TryAddCharacters(FoundationFonts.RequiredCharacters);
            FlushFontQueue();
            foreach (var font in fonts)
            {
                if (font == null)
                    continue;
                PersistSubAssets(font);
                var textures = font.atlasTextures;
                if (textures != null)
                    foreach (var texture in textures)
                        if (texture != null)
                            EditorUtility.SetDirty(texture);
            }
        }

        private static void FlushFontQueue()
        {
            var method = typeof(FontAsset).GetMethod(
                "UpdateFontAssetsInUpdateQueue",
                BindingFlags.Static | BindingFlags.NonPublic);
            method?.Invoke(null, null);
        }

        private static FontAsset EnsureGeneratedFont(string assetPath, string sourcePath)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TrueTypeFontImporter;
            if (importer == null)
                throw new InvalidOperationException(
                    "Packaged font source missing: " + sourcePath +
                    ". See " + FontsDir + "/README.md.");
            var importerChanged = false;
            if (!importer.includeFontData)
            {
                importer.includeFontData = true;
                importerChanged = true;
            }
            if (importer.fontTextureCase != FontTextureCase.Dynamic)
            {
                importer.fontTextureCase = FontTextureCase.Dynamic;
                importerChanged = true;
            }
            if (importerChanged)
                importer.SaveAndReimport();

            var source = AssetDatabase.LoadAssetAtPath<Font>(sourcePath);
            var font = AssetDatabase.LoadAssetAtPath<FontAsset>(assetPath);
            if (font == null)
            {
                font = FontAsset.CreateFontAsset(source, 42, 5, GlyphRenderMode.SDFAA,
                    1024, 1024, AtlasPopulationMode.Dynamic, true);
                font.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                AssetDatabase.CreateAsset(font, assetPath);
            }
            else
            {
                if (font.atlasPopulationMode != AtlasPopulationMode.Dynamic)
                {
                    font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                    EditorUtility.SetDirty(font);
                }
                if (font.sourceFontFile == null)
                    RepairSourceFontFile(font, source);
            }
            PersistSubAssets(font);
            return font;
        }

        private static void PersistSubAssets(FontAsset font)
        {
            if (font.material != null && !AssetDatabase.Contains(font.material))
                AssetDatabase.AddObjectToAsset(font.material, font);
            var textures = font.atlasTextures;
            if (textures != null)
                foreach (var texture in textures)
                    if (texture != null && !AssetDatabase.Contains(texture))
                        AssetDatabase.AddObjectToAsset(texture, font);
            EditorUtility.SetDirty(font);
        }

        private static void RepairSourceFontFile(FontAsset font, Font source)
        {
            var serialized = new SerializedObject(font);
            var property = serialized.FindProperty("m_SourceFontFile_Editor")
                           ?? serialized.FindProperty("m_SourceFontFile");
            if (property == null)
                throw new InvalidOperationException(
                    "FontAsset source font repair API changed in the pinned editor.");
            property.objectReferenceValue = source;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(font);
        }

        private static void EnsureFallbacks(FontAsset font, params FontAsset[] required)
        {
            var table = font.fallbackFontAssetTable;
            if (table == null)
            {
                table = new List<FontAsset>();
                font.fallbackFontAssetTable = table;
            }
            var changed = false;
            foreach (var entry in required)
                if (entry != null && !table.Contains(entry))
                {
                    table.Add(entry);
                    changed = true;
                }
            if (changed)
                EditorUtility.SetDirty(font);
        }

        private static void EnableAdvancedText()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.UIElements.UIToolkitProjectSettings"))
                .FirstOrDefault(t => t != null);
            var property = type?.GetProperty("enableAdvancedText",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite)
                throw new InvalidOperationException(
                    "UIToolkitProjectSettings.enableAdvancedText API changed in the pinned editor.");
            property.SetValue(null, true);
        }
    }
}
