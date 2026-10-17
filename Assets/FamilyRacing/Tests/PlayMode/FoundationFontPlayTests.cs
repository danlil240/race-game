using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.Text;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    public sealed class FoundationFontPlayTests
    {
        private FoundationApp app;
        private UIDocument document;
        private string profileDirectory;

        [UnitySetUp]
        public IEnumerator LoadRealFoundationScene()
        {
            yield return SceneManager.LoadSceneAsync("Assets/FamilyRacing/Generated/Foundation.unity");
            app = UnityEngine.Object.FindFirstObjectByType<FoundationApp>();
            document = app.GetComponent<UIDocument>();
            profileDirectory = Path.Combine(Application.temporaryCachePath, "font-tests-" + Guid.NewGuid().ToString("N"));
            var service = new FoundationService(new ProfileStore(profileDirectory, new UnityProfileSerializer()), "en", app.RunId);
            typeof(FoundationApp).GetField("service", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, service);
            for (var i = 0; i < 10; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator SceneBindsAdvancedBilingualFontAndKeepsPhysicalSteeringInRtl()
        {
            Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.True,
                "Readiness must include the Hebrew button while the shell is English.");
            Assert.That(app.Service.Snapshot().presentation.fontWarning, Is.Empty);
            var root = document.rootVisualElement;
            Assert.That(root.resolvedStyle.unityTextGenerator, Is.EqualTo(TextGeneratorType.Advanced));
            Assert.That(root.resolvedStyle.unityFontDefinition.fontAsset,
                Is.SameAs(document.panelSettings.textSettings.defaultFontAsset));
            app.Dispatch("ui.language", new Dictionary<string, string> { ["language"] = "he" });
            Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.True);
            Assert.That(app.Service.Snapshot().presentation.textDirection, Is.EqualTo("rtl"));
            yield return null;
            Assert.That(root.languageDirection, Is.EqualTo(LanguageDirection.RTL));
            app.Dispatch("profile.create", new Dictionary<string, string> { ["slot"] = "0", ["language"] = "he" });
            app.Dispatch("ui.navigate", new Dictionary<string, string> { ["screen"] = "sandbox" });
            yield return null;
            Assert.That(root.Q<Button>("input.left").text, Is.EqualTo("◀"));
            Assert.That(root.Q<Button>("input.right").text, Is.EqualTo("▶"));
            app.Input.TouchLeft(true);
            yield return null;
            Assert.That(app.Service.Snapshot().input.steering, Is.LessThan(0f));
            app.Input.TouchLeft(false);
            app.Input.TouchRight(true);
            yield return null;
            Assert.That(app.Service.Snapshot().input.steering, Is.GreaterThan(0f));
            app.Input.TouchRight(false);
        }

        // PCQA-005 regression: the very first rendered screen — no
        // navigation, no profile creation — must carry the Advanced text
        // generator and the bound default font on the actual attached
        // rootVisualElement, and every label the first RebuildTree built
        // must resolve a non-null font asset. A stale detached root or an
        // unapplied configuration would pass state flags yet paint blank.
        [UnityTest]
        public IEnumerator FirstRenderedScreenResolvesBoundFontWithoutNavigation()
        {
            var defaultFont = document.panelSettings.textSettings.defaultFontAsset;
            var root = document.rootVisualElement;
            Assert.That(root, Is.Not.Null);
            Assert.That(root.panel, Is.Not.Null,
                "The first screen's root must be attached to a live panel.");
            Assert.That(app.Service.Snapshot().screen, Is.EqualTo("profiles"));
            Assert.That(root.resolvedStyle.unityTextGenerator,
                Is.EqualTo(TextGeneratorType.Advanced));
            Assert.That(root.resolvedStyle.unityFontDefinition.fontAsset,
                Is.SameAs(defaultFont));

            var labels = root.Query<Label>().ToList();
            Assert.That(labels.Count, Is.GreaterThan(0),
                "The first RebuildTree must have produced visible labels.");
            foreach (var label in labels)
            {
                var definition = label.resolvedStyle.unityFontDefinition;
                Assert.That(definition.fontAsset != null || definition.font != null,
                    "Label '" + label.name + "' ('" + label.text +
                    "') resolved no font on the first rendered screen.");
            }
            var buttons = root.Query<Button>().ToList();
            Assert.That(buttons.Count, Is.GreaterThan(0),
                "The first RebuildTree must have produced visible buttons.");
            foreach (var button in buttons)
            {
                var definition = button.resolvedStyle.unityFontDefinition;
                Assert.That(definition.fontAsset != null || definition.font != null,
                    "Button '" + button.name + "' ('" + button.text +
                    "') resolved no font on the first rendered screen.");
            }
            Assert.That(FontsReady(document), Is.True,
                "Readiness must hold on the very first rendered screen.");
            yield break;
        }

        // PCQA-005 regression for the render-path half of the defect: a
        // dynamic glyph add is only queued — the atlas texture upload lands
        // when the font pipeline's update queue is flushed. The shell warms
        // every dynamic font in the chain at bind, so by the first rendered
        // screen the repertoire must already sit in the glyph table against
        // a live atlas texture with nothing left pending; otherwise the
        // first elements bake UVs for a not-yet-uploaded atlas and paint
        // blank until a rebuild (the player-only cold-launch symptom).
        [UnityTest]
        public IEnumerator FirstScreenGlyphsAreInTheAtlasWithNoPendingUpdate()
        {
            yield return null;
            var font = document.panelSettings.textSettings.defaultFontAsset;
            Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Dynamic));
            Assert.That(font.atlasTextures, Is.Not.Null.And.Not.Empty,
                "The warm-up must leave a real atlas texture, not a queued upload.");
            Assert.That(font.atlasTextures[0], Is.Not.Null);
            var labels = document.rootVisualElement.Query<Label>().ToList();
            var text = string.Concat(labels
                .Where(l => !string.IsNullOrEmpty(l.text))
                .Select(l => l.text));
            Assert.That(text, Is.Not.Empty,
                "The first screen must actually contain rendered strings.");
            Assert.That(font.HasCharacters(text, out _,
                    searchFallbacks: true, tryAddCharacter: false), Is.True,
                "Every first-screen glyph must already live in the font " +
                "chain — a render-time add lands after UVs are baked.");
            foreach (var fallback in font.fallbackFontAssetTable)
            {
                if (fallback == null) continue;
                Assert.That(fallback.atlasTextures, Is.Not.Null.And.Not.Empty,
                    "Fallback '" + fallback.name + "' must also carry a live atlas.");
            }
            var pending = typeof(FontAsset).GetField("m_AtlasUpdateQueue",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (pending != null)
            {
                var queue = (System.Collections.ICollection)pending.GetValue(null);
                Assert.That(queue.Count, Is.EqualTo(0),
                    "No atlas update may still be pending on the first " +
                    "rendered screen — pending uploads are exactly what " +
                    "leaves cold-launch text permanently blank.");
            }
        }

        [UnityTest]
        public IEnumerator ReadinessTracksMissingAndRestoredSettingsWithoutLocaleChange()
        {
            var panel = document.panelSettings;
            var original = panel.textSettings;
            try
            {
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.True);
                panel.textSettings = null;
                yield return null;
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.False);
                Assert.That(app.Service.Snapshot().presentation.fontWarning, Is.Not.Empty,
                    "Missing text configuration also affects the Hebrew button in English mode.");
                panel.textSettings = original;
                yield return null;
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.True);
                Assert.That(app.Service.Snapshot().presentation.fontWarning, Is.Empty);
                document.rootVisualElement.style.unityTextGenerator = TextGeneratorType.Standard;
                app.Dispatch("ui.language", new Dictionary<string, string> { ["language"] = "en" });
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.False,
                    "Assets alone do not mean Hebrew is configured when ATG is disabled.");
            }
            finally
            {
                panel.textSettings = original;
                document.rootVisualElement.style.unityTextGenerator = TextGeneratorType.Advanced;
            }
        }

        [UnityTest]
        public IEnumerator ReadinessDropsWhenHebrewFallbackRemovedAndRecoversOnRestore()
        {
            var latin = document.panelSettings.textSettings.defaultFontAsset;
            var original = new List<FontAsset>(latin.fallbackFontAssetTable);
            var hebrew = original.Find(f => f != null && f.name.Contains("Hebrew"));
            var sourceless = ScriptableObject.CreateInstance<FontAsset>();
            try
            {
                Assert.That(hebrew, Is.Not.Null, "Packaged Hebrew fallback must exist first.");
                latin.fallbackFontAssetTable = original.FindAll(f => f != hebrew);
                yield return null;
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.False,
                    "Removing the Hebrew fallback must drop readiness without a locale change.");
                latin.fallbackFontAssetTable = new List<FontAsset>(original);
                yield return null;
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.True);
                latin.fallbackFontAssetTable = new List<FontAsset>(original) { sourceless };
                yield return null;
                Assert.That(app.Service.Snapshot().presentation.hebrewFontAvailable, Is.False,
                    "A reachable source-less font must not report readiness.");
            }
            finally
            {
                latin.fallbackFontAssetTable = original;
                UnityEngine.Object.DestroyImmediate(sourceless);
                UnityEditor.EditorUtility.SetDirty(latin);
                UnityEditor.AssetDatabase.SaveAssets();
            }
        }

        [UnityTest]
        public IEnumerator ReadinessReflectsRootAttachmentAndInlineStyleChanges()
        {
            yield return null;
            var font = document.panelSettings.textSettings.defaultFontAsset;
            try
            {
                var root = document.rootVisualElement;
                Assert.That(FontsReady(document), Is.True,
                    "Attached configured root must be ready.");

                root.RemoveFromHierarchy();
                Assert.That(FontsReady(document), Is.False,
                    "A detached root must fail readiness before the shell rebinds.");

                document.enabled = false;
                document.enabled = true;
                yield return null;
                root = document.rootVisualElement;
                Assert.That(root.panel, Is.Not.Null);
                Assert.That(FontsReady(document), Is.True,
                    "A re-created attached root must recover readiness.");

                root.style.unityTextGenerator = TextGeneratorType.Standard;
                Assert.That(FontsReady(document), Is.False,
                    "Explicit inline Standard must fail immediately.");
                root.style.unityTextGenerator = TextGeneratorType.Advanced;
                Assert.That(FontsReady(document), Is.True,
                    "Inline Advanced must restore readiness without a frame.");

                root.style.unityFontDefinition = new FontDefinition();
                Assert.That(FontsReady(document), Is.False,
                    "A null bound font must fail readiness.");
                root.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
                Assert.That(FontsReady(document), Is.True);
            }
            finally
            {
                var live = document.rootVisualElement;
                if (live != null)
                {
                    live.style.unityTextGenerator = TextGeneratorType.Advanced;
                    if (font != null)
                        live.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
                }
            }
        }

        private static bool FontsReady(UIDocument doc)
        {
            var method = typeof(FoundationApp).Assembly
                .GetType("FamilyRacing.FoundationFonts")
                .GetMethod("IsReady", BindingFlags.Static | BindingFlags.NonPublic);
            return (bool)method.Invoke(null, new object[] { doc });
        }

        [TearDown]
        public void Cleanup()
        {
            if (app != null) UnityEngine.Object.DestroyImmediate(app.gameObject);
            if (profileDirectory != null && Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, true);
        }
    }
}
