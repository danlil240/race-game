using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamilyRacing.Core.Localization;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    public sealed class FoundationFontSetupTests
    {
        private const string PanelPath = "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";

        [Test]
        public void SetupPackagesDynamicSourcesCoveringBothCatalogsAndLanguageButtons()
        {
            FoundationProjectSetup.Create();
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            Assert.That(panel.textSettings, Is.Not.Null);
            var latin = panel.textSettings.defaultFontAsset;
            Assert.That(latin, Is.Not.Null, "The panel needs a packaged default font.");
            AssertPackagedDynamicFont(latin);
            Assert.That(latin.fallbackFontAssetTable, Is.Not.Empty,
                "Hebrew must be reachable even while the shell is English.");
            foreach (var font in latin.fallbackFontAssetTable) AssertPackagedDynamicFont(font);
            foreach (var language in new[] { "en", "he" })
            foreach (var key in ShellText.Keys)
                AssertCovered(latin, ShellText.Get(key, language), language + ":" + key);
            AssertCovered(latin, "English עברית 0123456789:.-◀▶", "language buttons and controls");
        }

        [Test]
        public void SetupRepairsMissingTextBindingWithoutReplacingExistingPanel()
        {
            FoundationProjectSetup.Create();
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            var previousText = panel.textSettings;
            var previousResolution = panel.referenceResolution;
            var guid = AssetDatabase.AssetPathToGUID(PanelPath);
            try
            {
                panel.textSettings = null;
                panel.referenceResolution = new Vector2Int(1440, 900);
                FoundationProjectSetup.Create();
                Assert.That(panel.textSettings, Is.Not.Null, "Rerun must repair a missing text-settings reference.");
                Assert.That(AssetDatabase.AssetPathToGUID(PanelPath), Is.EqualTo(guid));
                Assert.That(panel.referenceResolution, Is.EqualTo(new Vector2Int(1440, 900)),
                    "Repair must retain authored panel sizing.");
            }
            finally
            {
                panel.textSettings = previousText;
                panel.referenceResolution = previousResolution;
                EditorUtility.SetDirty(panel);
                AssetDatabase.SaveAssets();
            }
        }

        [Test]
        public void SetupRepairsFontReferencesAndPreservesAuthoredFallbacks()
        {
            FoundationProjectSetup.Create();
            var text = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath).textSettings;
            var latin = text.defaultFontAsset;
            Assert.That(latin, Is.Not.Null, "The default font must exist before testing repair.");
            var originalFallbacks = new List<FontAsset>(latin.fallbackFontAssetTable);
            var authoredFallback = FontAsset.CreateFontAsset(
                AssetDatabase.LoadAssetAtPath<Font>("Assets/FamilyRacing/Fonts/NotoSans-Regular.ttf"),
                42, 5, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(latin));
            try
            {
                text.defaultFontAsset = null;
                latin.fallbackFontAssetTable = new List<FontAsset> { authoredFallback };
                FoundationProjectSetup.Create();
                FoundationProjectSetup.Create();
                Assert.That(text.defaultFontAsset, Is.SameAs(latin));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(latin)), Is.EqualTo(guid));
                Assert.That(latin.fallbackFontAssetTable, Does.Contain(authoredFallback));
                foreach (var fallback in originalFallbacks)
                    Assert.That(latin.fallbackFontAssetTable.FindAll(f => f == fallback).Count, Is.EqualTo(1));
                AssertCovered(latin, "עברית", "repaired fallback chain");
            }
            finally
            {
                text.defaultFontAsset = latin;
                latin.fallbackFontAssetTable = originalFallbacks;
                UnityEngine.Object.DestroyImmediate(authoredFallback);
                EditorUtility.SetDirty(latin);
                EditorUtility.SetDirty(text);
                AssetDatabase.SaveAssets();
            }
        }

        [Test]
        public void SetupEnablesAdvancedTextInPinnedEditorProjectSettings()
        {
            FoundationProjectSetup.Create();
            var settingsType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.UIElements.UIToolkitProjectSettings"))
                .FirstOrDefault(type => type != null);
            Assert.That(settingsType, Is.Not.Null, "Pinned editor UI Toolkit project settings type changed.");
            var enabled = settingsType.GetProperty("enableAdvancedText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(enabled, Is.Not.Null, "Pinned editor ATG setting API changed.");
            Assert.That(enabled.GetValue(null), Is.True, "ATG must be enabled in project settings for builds.");
        }

        [Test]
        public void SetupRepairsFontSourceImporterSettings()
        {
            var paths = new[]
            {
                "Assets/FamilyRacing/Fonts/NotoSans-Regular.ttf",
                "Assets/FamilyRacing/Fonts/NotoSansHebrew-Regular.ttf",
                "Assets/FamilyRacing/Fonts/NotoSansSymbols2-Regular.ttf",
            };
            var originals = new List<(TrueTypeFontImporter importer, bool data, FontTextureCase mode)>();
            try
            {
                foreach (var path in paths)
                {
                    var importer = AssetImporter.GetAtPath(path) as TrueTypeFontImporter;
                    Assert.That(importer, Is.Not.Null, path);
                    originals.Add((importer, importer.includeFontData, importer.fontTextureCase));
                    importer.includeFontData = false;
                    importer.fontTextureCase = FontTextureCase.ASCII;
                    importer.SaveAndReimport();
                }
                FoundationProjectSetup.Create();
                foreach (var path in paths)
                {
                    var importer = AssetImporter.GetAtPath(path) as TrueTypeFontImporter;
                    Assert.That(importer, Is.Not.Null, path);
                    Assert.That(importer.includeFontData, Is.True, path);
                    Assert.That(importer.fontTextureCase, Is.EqualTo(FontTextureCase.Dynamic), path);
                }
            }
            finally
            {
                foreach (var (importer, data, mode) in originals)
                {
                    importer.includeFontData = data;
                    importer.fontTextureCase = mode;
                    importer.SaveAndReimport();
                }
            }
        }

        [Test]
        public void StreamingLicenseMatchesPackagedOflByteForByte()
        {
            var packaged = File.ReadAllBytes("Assets/FamilyRacing/Fonts/OFL.txt");
            var bundled = File.ReadAllBytes("Assets/StreamingAssets/ThirdPartyLicenses/Noto-OFL.txt");
            Assert.That(bundled, Is.EqualTo(packaged),
                "Bundled OFL must be byte-identical to the vendored source license.");
            var text = File.ReadAllText("Assets/StreamingAssets/ThirdPartyLicenses/Noto-OFL.txt");
            Assert.That(text, Does.StartWith("Copyright"));
            Assert.That(text, Does.Contain("SIL Open Font License"));
        }

        [Test]
        public void GeneratedFontAssetsOwnSerializedMaterialAndAtlas()
        {
            FoundationProjectSetup.Create();
            foreach (var path in new[]
            {
                "Assets/FamilyRacing/Generated/FoundationLatinFont.asset",
                "Assets/FamilyRacing/Generated/FoundationHebrewFont.asset",
                "Assets/FamilyRacing/Generated/FoundationSymbolsFont.asset",
            })
            {
                var font = AssetDatabase.LoadAssetAtPath<FontAsset>(path);
                Assert.That(font, Is.Not.Null, path);
                Assert.That(font.material, Is.Not.Null, path);
                Assert.That(AssetDatabase.GetAssetPath(font.material), Is.EqualTo(path));
                Assert.That(font.atlasTextures, Is.Not.Empty, path);
                foreach (var texture in font.atlasTextures)
                    Assert.That(AssetDatabase.GetAssetPath(texture), Is.EqualTo(path));
            }
        }

        [Test]
        public void SetupPreservesAuthoredTextSettingsAndDefaultFont()
        {
            FoundationProjectSetup.Create();
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            var previousText = panel.textSettings;
            var folder = "Assets/FontSetupAuthored-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            try
            {
                var authoredText = ScriptableObject.CreateInstance<PanelTextSettings>();
                AssetDatabase.CreateAsset(authoredText, folder + "/AuthoredTextSettings.asset");
                var authoredFont = FontAsset.CreateFontAsset(
                    AssetDatabase.LoadAssetAtPath<Font>("Assets/FamilyRacing/Fonts/NotoSansHebrew-Regular.ttf"),
                    42, 5, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                authoredFont.name = "AuthoredFont";
                AssetDatabase.CreateAsset(authoredFont, folder + "/AuthoredFont.asset");
                if (authoredFont.material != null)
                    AssetDatabase.AddObjectToAsset(authoredFont.material, authoredFont);
                foreach (var texture in authoredFont.atlasTextures)
                    if (texture != null)
                        AssetDatabase.AddObjectToAsset(texture, authoredFont);
                authoredText.defaultFontAsset = authoredFont;
                panel.textSettings = authoredText;
                EditorUtility.SetDirty(panel);
                EditorUtility.SetDirty(authoredText);
                AssetDatabase.SaveAssets();
                var guid = AssetDatabase.AssetPathToGUID(folder + "/AuthoredTextSettings.asset");
                FoundationProjectSetup.Create();
                FoundationProjectSetup.Create();
                Assert.That(panel.textSettings, Is.SameAs(authoredText));
                Assert.That(AssetDatabase.AssetPathToGUID(
                    AssetDatabase.GetAssetPath(authoredText)), Is.EqualTo(guid));
                Assert.That(authoredText.defaultFontAsset, Is.SameAs(authoredFont));
                foreach (var requiredPath in new[]
                {
                    "Assets/FamilyRacing/Generated/FoundationLatinFont.asset",
                    "Assets/FamilyRacing/Generated/FoundationHebrewFont.asset",
                    "Assets/FamilyRacing/Generated/FoundationSymbolsFont.asset",
                })
                {
                    var required = AssetDatabase.LoadAssetAtPath<FontAsset>(requiredPath);
                    Assert.That(authoredFont.fallbackFontAssetTable
                        .FindAll(f => f == required).Count, Is.EqualTo(1), requiredPath);
                }
            }
            finally
            {
                panel.textSettings = previousText;
                EditorUtility.SetDirty(panel);
                AssetDatabase.SaveAssets();
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static void AssertPackagedDynamicFont(FontAsset font)
        {
            Assert.That(font, Is.Not.Null);
            Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Dynamic));
            Assert.That(font.sourceFontFile, Is.Not.Null, "Dynamic fonts need their source bundled offline.");
            Assert.That(AssetDatabase.GetAssetPath(font.sourceFontFile), Does.StartWith("Assets/FamilyRacing/Fonts/"));
        }

        private static void AssertCovered(FontAsset font, string text, string context)
        {
            Assert.That(font.HasCharacters(text, out uint[] missing, true, true), Is.True,
                context + " missing codepoints: " + (missing == null ? "" : string.Join(",", missing)));
        }
    }
}
