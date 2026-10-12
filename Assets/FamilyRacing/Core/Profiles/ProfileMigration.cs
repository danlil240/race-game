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
    public static class ProfileMigration
    {
        // Returns true when the profile was upgraded in place.
        public static bool Migrate(ProfileSave profile)
        {
            if (profile == null
                || profile.schemaVersion < 1
                || profile.schemaVersion >= ProfileValidation.SchemaVersion)
                return false;
            profile.schemaVersion = ProfileValidation.SchemaVersion;
            if (profile.appliedResults == null)
                profile.appliedResults = new List<AppliedResultReceipt>();
            return true;
        }
    }
}
