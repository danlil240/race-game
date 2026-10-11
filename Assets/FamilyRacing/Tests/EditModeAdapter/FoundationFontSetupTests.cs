using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FamilyRacing.Core.Localization;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
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
            var authoredFallback = ScriptableObject.CreateInstance<FontAsset>();
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
