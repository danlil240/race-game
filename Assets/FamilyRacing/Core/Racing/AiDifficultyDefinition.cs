using System;

namespace FamilyRacing.Core.Racing
{
    // One visible AI difficulty preset (easy/medium/hard). Presets change
    // AI *decisions* only — braking safety margin, boost/gadget timing
    // and shortcut willingness — never a hidden speed multiplier; the AI
    // drives the same normalized tuning as humans.
    public sealed class AiDifficultyDefinition
    {
        public string id = "";
        // Scale on the cornering safety margin (higher = brakes earlier).
        public float brakeMarginScale = 1f;
        // Boost-meter charge the AI waits for before spending a boost.
        public float boostUseMinCharge = 0.9f;
        // 0..1 willingness to spend a held gadget when a use is legal.
        public float gadgetUseWillingness = 0.5f;
        // 0..1 willingness to attempt authored optional shortcuts.
        public float shortcutWillingness = 0.5f;

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (!Positive(brakeMarginScale)) { error = "brakeMarginScale"; return false; }
            if (!Fraction(boostUseMinCharge)) { error = "boostUseMinCharge"; return false; }
            if (!Fraction(gadgetUseWillingness)) { error = "gadgetUseWillingness"; return false; }
            if (!Fraction(shortcutWillingness)) { error = "shortcutWillingness"; return false; }
            return true;
        }

        public AiDifficultyDefinition Copy()
        {
            return (AiDifficultyDefinition)MemberwiseClone();
        }

        private static bool Positive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static bool Fraction(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value)
                && value >= 0f && value <= 1f;
        }
    }
}
