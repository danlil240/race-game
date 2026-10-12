using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FamilyRacing.Core.Profiles
{
    internal enum ProfileCheck
    {
        Missing,
        Invalid,
        NewerSchema,
        Valid
    }

    // Shared invariant rules for factory defaults, load validation, and save
    // validation. A profile is only usable when every check passes.
    internal static class ProfileValidation
    {
        internal const int SchemaVersion = 2;
        internal const int SlotCount = 4;

        internal static readonly string[] Avatars = { "fox", "rabbit", "panda", "raccoon" };
        internal static readonly string[] Languages = { "en", "he" };
        internal static readonly string[] Cameras = { "chase", "overhead" };

        private static readonly Regex VehicleId =
            new Regex("^[A-Za-z0-9]+(-[A-Za-z0-9]+)*\\z", RegexOptions.CultureInvariant);

        internal static void RequireValidSlot(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
                throw new ArgumentOutOfRangeException("slot", slot, "Profile slot must be between 0 and 3.");
        }

        internal static bool IsSupportedLanguage(string language)
        {
            return language == "en" || language == "he";
        }

        // expectedSlot of -1 skips the slot match but not the slot range check.
        internal static ProfileCheck Check(ProfileSave profile, int expectedSlot)
        {
            if (profile == null) return ProfileCheck.Invalid;
            if (profile.schemaVersion > SchemaVersion) return ProfileCheck.NewerSchema;
            if (profile.schemaVersion < 1) return ProfileCheck.Invalid;
            if (profile.slot < 0 || profile.slot >= SlotCount) return ProfileCheck.Invalid;
            if (expectedSlot >= 0 && profile.slot != expectedSlot) return ProfileCheck.Invalid;
            if (string.IsNullOrEmpty(profile.id)
                || !Guid.TryParseExact(profile.id, "D", out var id)
                || id == Guid.Empty) return ProfileCheck.Invalid;
            if (Array.IndexOf(Avatars, profile.avatar) < 0) return ProfileCheck.Invalid;
            if (profile.colorIndex < 0 || profile.colorIndex >= SlotCount) return ProfileCheck.Invalid;
            if (!IsSupportedLanguage(profile.language)) return ProfileCheck.Invalid;
            if (Array.IndexOf(Cameras, profile.camera) < 0) return ProfileCheck.Invalid;
            if (profile.coins < 0) return ProfileCheck.Invalid;
            if (!IsValidVolume(profile.musicVolume)
                || !IsValidVolume(profile.effectsVolume)
                || !IsValidVolume(profile.voiceVolume)) return ProfileCheck.Invalid;
            if (profile.unlockedVehicles == null) return ProfileCheck.Invalid;
            foreach (var vehicle in profile.unlockedVehicles)
                if (string.IsNullOrEmpty(vehicle) || !VehicleId.IsMatch(vehicle)) return ProfileCheck.Invalid;
            // The schema-2 receipt ledger is validated at its own schema:
            // schema-1 files migrate on read and gain an empty ledger
            // there, so a missing one only fails once it claims schema 2.
            if (profile.schemaVersion >= 2)
            {
                if (profile.appliedResults == null) return ProfileCheck.Invalid;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var receipt in profile.appliedResults)
                {
                    if (receipt == null) return ProfileCheck.Invalid;
                    if (string.IsNullOrEmpty(receipt.resultId))
                        return ProfileCheck.Invalid;
                    // Receipts are bound to this profile's identity — a
                    // foreign participant's receipt in this file is
                    // corrupt, never silently adopted or evicted.
                    if (receipt.participantId != profile.id)
                        return ProfileCheck.Invalid;
                    if (receipt.awardedCoins < 0) return ProfileCheck.Invalid;
                    if (!IsFingerprint(receipt.fingerprint))
                        return ProfileCheck.Invalid;
                    if (!seen.Add(receipt.resultId)) return ProfileCheck.Invalid;
                }
            }
            return ProfileCheck.Valid;
        }

        internal static void RequireValidForSave(ProfileSave profile)
        {
            // Saves always write the current schema: an unmigrated
            // in-memory profile must pass through a load first.
            if (Check(profile, profile == null ? -1 : profile.slot) != ProfileCheck.Valid
                || profile.schemaVersion != SchemaVersion)
                throw new ArgumentException("The profile data is not valid and cannot be saved.", "profile");
        }

        // SHA-256 fingerprints serialize as exactly 64 lowercase hex
        // characters; anything else is corrupt data, not a receipt.
        private static bool IsFingerprint(string fingerprint)
        {
            if (fingerprint == null || fingerprint.Length != 64) return false;
            foreach (var c in fingerprint)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        private static bool IsValidVolume(float volume)
        {
            return !float.IsNaN(volume) && !float.IsInfinity(volume) && volume >= 0f && volume <= 1f;
        }
    }
}
