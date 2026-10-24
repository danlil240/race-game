using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    // Forward-only schema upgrades applied to a profile as it is read.
    // Migration is deliberately in-memory only: the file bytes are never
    // touched here — an explicit successful save is what writes the newer
    // schema, so an interrupted or abandoned session can never strand a
    // half-upgraded save on disk.
    //
    // Schema 1 -> 2 adds the applied-result receipt ledger
    // (ProfileSave.appliedResults); a schema-1 file has no receipts, so
    // the ledger simply starts empty.
    //
    // Schema 2 -> 3 adds the first-world progression collections. Schema-2
    // bytes carry none of them, so each starts empty — except the three
    // initial challenge assignments, which seed exactly like a new
    // profile so an old save gets the same starting challenges.
    public static class ProfileMigration
    {
        // Returns true when the profile was upgraded in place.
        public static bool Migrate(ProfileSave profile)
        {
            if (profile == null
                || profile.schemaVersion < 1
                || profile.schemaVersion >= ProfileValidation.SchemaVersion)
                return false;
            // Ordered steps, each raising the in-memory version once —
            // a schema-1 profile passes through schema 2 on its way to 3.
            if (profile.schemaVersion < 2) MigrateOneToTwo(profile);
            if (profile.schemaVersion < 3) MigrateTwoToThree(profile);
            return true;
        }

        private static void MigrateOneToTwo(ProfileSave profile)
        {
            if (profile.appliedResults == null)
                profile.appliedResults = new List<AppliedResultReceipt>();
            profile.schemaVersion = 2;
        }

        private static void MigrateTwoToThree(ProfileSave profile)
        {
            if (profile.eventProgress == null)
                profile.eventProgress = new List<EventProgress>();
            if (profile.worldProgress == null)
                profile.worldProgress = new List<WorldProgress>();
            if (profile.finaleProgress == null)
                profile.finaleProgress = new List<FinaleProgress>();
            if (profile.vehicleUpgrades == null)
                profile.vehicleUpgrades = new List<VehicleUpgrade>();
            if (profile.ownedCosmetics == null)
                profile.ownedCosmetics = new List<string>();
            if (profile.equippedCosmetics == null)
                profile.equippedCosmetics = new List<string>();
            // A migrated profile that never carried assignments gets the
            // same three initial challenges a fresh profile does; rows
            // already present (defensive: schema-2 files cannot have
            // them) are preserved untouched.
            if (profile.activeBounties == null || profile.activeBounties.Count == 0)
                profile.activeBounties = ProfileProgressSeed.CreateInitialBounties();
            if (profile.timeTrialRecords == null)
                profile.timeTrialRecords = new List<TimeTrialRecord>();
            if (profile.purchaseReceipts == null)
                profile.purchaseReceipts = new List<PurchaseReceipt>();
            if (profile.pendingRaceContexts == null)
                profile.pendingRaceContexts = new List<RaceRewardContext>();
            profile.schemaVersion = 3;
        }
    }
}
