using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    // The single lossless profile copy. Every snapshot, settings draft,
    // reward candidate and slot cache goes through DeepCopy so a caller
    // mutating a copy can never corrupt — or silently share — the live
    // profile's nested collections. Null fields stay null: validation
    // distinguishes a missing collection from an empty one, and a copy
    // must not launder a corrupt shape into a valid-looking one.
    public static class ProfileCopy
    {
        public static ProfileSave DeepCopy(ProfileSave source)
        {
            if (source == null) return null;
            var copy = new ProfileSave
            {
                schemaVersion = source.schemaVersion,
                id = source.id,
                slot = source.slot,
                avatar = source.avatar,
                colorIndex = source.colorIndex,
                language = source.language,
                camera = source.camera,
                reducedCameraShake = source.reducedCameraShake,
                musicVolume = source.musicVolume,
                effectsVolume = source.effectsVolume,
                voiceVolume = source.voiceVolume
            };
            CopyProgression(source, copy);
            return copy;
        }

        // Progression is owned by the live profile, not the settings
        // draft: coins, unlocks, the receipt ledger and every schema-3
        // collection. Copying this slice keeps a published draft in step
        // after a reward or purchase lands while a settings edit is
        // pending — the draft can never carry stale progression into a
        // save.
        public static void CopyProgression(ProfileSave source, ProfileSave target)
        {
            if (source == null || target == null) return;
            target.coins = source.coins;
            target.unlockedVehicles = CopyStrings(source.unlockedVehicles);
            target.appliedResults = CopyRows(source.appliedResults,
                row => row.Copy());
            target.eventProgress = CopyRows(source.eventProgress,
                row => row.Copy());
            target.worldProgress = CopyRows(source.worldProgress,
                row => row.Copy());
            target.finaleProgress = CopyRows(source.finaleProgress,
                row => row.Copy());
            target.vehicleUpgrades = CopyRows(source.vehicleUpgrades,
                row => row.Copy());
            target.ownedCosmetics = CopyStrings(source.ownedCosmetics);
            target.equippedCosmetics = CopyStrings(source.equippedCosmetics);
            target.activeBounties = CopyRows(source.activeBounties,
                row => row.Copy());
            target.bountyRotationCursor = source.bountyRotationCursor;
            target.timeTrialRecords = CopyRows(source.timeTrialRecords,
                row => row.Copy());
            target.purchaseReceipts = CopyRows(source.purchaseReceipts,
                row => row.Copy());
            target.pendingRaceContexts = CopyRows(source.pendingRaceContexts,
                row => row.Copy());
            target.localRaceOrdinal = source.localRaceOrdinal;
        }

        // The user-facing settings slice — the only fields a settings
        // draft is allowed to carry. Applying just these onto a fresh
        // deep copy of the live profile is what keeps a pending settings
        // save from rolling progression back to a stale draft.
        public static void CopySettings(ProfileSave source, ProfileSave target)
        {
            if (source == null || target == null) return;
            target.language = source.language;
            target.camera = source.camera;
            target.reducedCameraShake = source.reducedCameraShake;
            target.musicVolume = source.musicVolume;
            target.effectsVolume = source.effectsVolume;
            target.voiceVolume = source.voiceVolume;
        }

        private static List<string> CopyStrings(List<string> source)
        {
            return source == null ? null : new List<string>(source);
        }

        private static List<T> CopyRows<T>(List<T> source, Func<T, T> copyRow)
            where T : class
        {
            if (source == null) return null;
            var copy = new List<T>(source.Count);
            foreach (var row in source)
                copy.Add(row != null ? copyRow(row) : null);
            return copy;
        }
    }
}
