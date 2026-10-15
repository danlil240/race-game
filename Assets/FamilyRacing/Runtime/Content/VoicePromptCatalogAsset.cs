using System;
using UnityEngine;

namespace FamilyRacing
{
    // The authored bilingual voice-prompt table (M6 Task 8 contract,
    // created by Task 4 so production validation can require it): each
    // row binds one cue id to its licensed English and Hebrew clips.
    // Resolve returns null for an unknown cue, an unknown language, or
    // an unbundled clip — the presenter (Task 8) reports the miss and
    // never substitutes a different cue. Generic catalog conversion
    // deliberately does not require clips so fixtures and mid-production
    // trees stay loadable; FullContentValidation owns the production
    // requirement that every required cue ships both locales.
    [CreateAssetMenu(menuName = "FamilyRacing/Voice Prompt Catalog",
        fileName = "voice-prompt-catalog")]
    public sealed class VoicePromptCatalogAsset : ScriptableObject
    {
        [Serializable]
        public sealed class CueRow
        {
            public string cueId = "";
            public AudioClip english;
            public AudioClip hebrew;
        }

        public CueRow[] cues = new CueRow[0];

        // `language` is "en" or "he"; anything else resolves null — the
        // catalog is closed, never locale-guessed.
        public AudioClip Resolve(string cueId, string language)
        {
            if (cues == null || string.IsNullOrEmpty(cueId)) return null;
            if (language != "en" && language != "he") return null;
            foreach (var row in cues)
            {
                if (row == null || row.cueId != cueId) continue;
                return language == "en" ? row.english : row.hebrew;
            }
            return null;
        }

        public bool Validate(out string error)
        {
            error = null;
            if (cues == null) return true;
            var seen = new System.Collections.Generic.HashSet<string>(
                StringComparer.Ordinal);
            foreach (var row in cues)
            {
                if (row == null || string.IsNullOrEmpty(row.cueId))
                { error = "cueId"; return false; }
                if (!seen.Add(row.cueId))
                { error = "duplicate"; return false; }
            }
            return true;
        }
    }
}
