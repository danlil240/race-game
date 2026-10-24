using System;
using System.Collections.Generic;
using System.Linq;
using FamilyRacing.Core.Localization;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Live bilingual readiness read from the actual bound text configuration:
    // an Advanced text generator and bound font on the shell root, a panel
    // default font covering the catalog+control repertoire, and a fallback
    // chain of dynamic fonts with bundled sources. Recomputed per refresh.
    internal static class FoundationFonts
    {
        internal const string Warning =
            "Bilingual font or Advanced Text Generator configuration unavailable.";

        internal static readonly string RequiredCharacters = BuildRequiredCharacters();

        // Test-observable signal that the bind-time warm-up path ran; lets
        // PlayMode coverage assert the fix executed rather than only its
        // (frame-timing-dependent) queue side-effects.
        internal static int WarmupCalls;

        private static readonly System.Reflection.MethodInfo FlushMethod =
            typeof(FontAsset).GetMethod("UpdateFontAssetsInUpdateQueue",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);

        // PCQA-005: dynamic glyph additions are queued, not applied — the
        // atlas texture upload only lands when the font pipeline flushes
        // its update queue, which can trail the first rendered frames by
        // seconds on a cold player launch. Elements whose text generates
        // against the not-yet-uploaded atlas bake UVs for glyph rects that
        // never arrive and stay permanently blank — no amount of dirtying
        // or rebuilding regenerates them once cached. Adding the full
        // repertoire and flushing the queue synchronously at bind keeps
        // the first screen's UV generation aligned with the real atlas.
        // Never throws: a corrupted authored font or engine-side failure
        // degrades to the pre-fix behavior (the pipeline flushes itself on
        // its own schedule) rather than aborting Awake through the
        // ApplyTextConfiguration → Bind path.
        internal static void Warmup(PanelSettings panelSettings)
        {
            try
            {
                var settings = panelSettings != null ? panelSettings.textSettings : null;
                var font = settings != null ? settings.defaultFontAsset : null;
                if (font == null)
                    return;
                WarmupCalls++;
                var visited = new HashSet<FontAsset>();
                WarmupChain(font, visited);
                FlushMethod?.Invoke(null, null);
            }
            catch (Exception)
            {
                // Best-effort only: the character adds still seed the atlas,
                // and a missing internal flush API or broken font just means
                // the pipeline flushes on the first pass.
            }
        }

        private static void WarmupChain(FontAsset font, HashSet<FontAsset> visited)
        {
            if (font == null || !visited.Add(font)) return;
            if (font.atlasPopulationMode == AtlasPopulationMode.Dynamic)
                font.TryAddCharacters(RequiredCharacters);
            var fallbacks = font.fallbackFontAssetTable;
            if (fallbacks != null)
                foreach (var fallback in fallbacks)
                    WarmupChain(fallback, visited);
        }

        internal static bool IsReady(UIDocument document)
        {
            if (document == null)
                return false;
            var root = document.rootVisualElement;
            if (root == null || root.panel == null)
                return false;
            var panel = document.panelSettings;
            if (panel == null || panel.textSettings == null)
                return false;
            var defaultFont = panel.textSettings.defaultFontAsset;
            if (defaultFont == null)
                return false;

            // StyleKeyword.Undefined means the style holds a literal value;
            // Null means unset, so the resolved style answers instead. Any
            // other keyword cannot stand in for an explicit configuration.
            var generator = root.style.unityTextGenerator;
            bool advanced;
            if (generator.keyword == StyleKeyword.Undefined)
                advanced = generator.value == TextGeneratorType.Advanced;
            else if (generator.keyword == StyleKeyword.Null)
                advanced = root.resolvedStyle.unityTextGenerator == TextGeneratorType.Advanced;
            else
                advanced = false;
            if (!advanced)
                return false;

            var definition = root.style.unityFontDefinition;
            FontDefinition bound;
            if (definition.keyword == StyleKeyword.Undefined)
                bound = definition.value;
            else if (definition.keyword == StyleKeyword.Null)
                bound = root.resolvedStyle.unityFontDefinition;
            else
                return false;
            if (bound.fontAsset == null || bound.fontAsset != defaultFont)
                return false;

            var visited = new HashSet<FontAsset>();
            if (!FontsUsable(defaultFont, visited))
                return false;

            // Coverage follows the usability gate so a broken authored entry
            // in the chain reports unready rather than throwing.
            try
            {
                // Bind warms the atlas once. A readiness read must not ask
                // the Latin font to add missing Hebrew glyphs every frame
                // before searching the already-warmed fallback chain.
                return defaultFont.HasCharacters(RequiredCharacters, out _, true, false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool FontsUsable(FontAsset font, HashSet<FontAsset> visited)
        {
            if (font == null)
                return false;
            if (!visited.Add(font))
                return true;
            if (font.atlasPopulationMode != AtlasPopulationMode.Dynamic)
                return false;
            if (font.sourceFontFile == null)
                return false;
            var fallbacks = font.fallbackFontAssetTable;
            if (fallbacks != null)
                foreach (var fallback in fallbacks)
                    if (!FontsUsable(fallback, visited))
                        return false;
            return true;
        }

        private static string BuildRequiredCharacters()
        {
            var chars = new HashSet<char>();
            void Add(string value)
            {
                if (value == null)
                    return;
                foreach (var c in value)
                    chars.Add(c);
            }
            foreach (var key in ShellText.Keys)
            {
                Add(ShellText.Get(key, "en"));
                Add(ShellText.Get(key, "he"));
            }
            Add("English");
            Add("עברית");
            Add("0123456789");
            Add("!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~");
            Add("◀▶");
            return new string(chars.OrderBy(c => c).ToArray());
        }
    }
}
