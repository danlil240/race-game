using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    // Durable proof that one immutable race result was applied to this
    // profile. Written together with every awarded fact inside the same
    // atomic profile save, so a crash can never leave coins without the
    // receipt (double-award) or the receipt without coins (lost award).
    //
    // `fingerprint` is the lowercase hex SHA-256 of the result's
    // versioned canonical encoding (RaceResult.Fingerprint). Replays of
    // the same resultId compare fingerprints: identical means
    // already-applied, different means a conflicting replay that must be
    // rejected even after a process restart.
    //
    // M5 Task 7 adds the rules identity + the persisted award breakdown.
    // `rulesFingerprint` binds the receipt to the rules edition the
    // reward was evaluated under (the bound context's fingerprint for
    // progression races, the result's own for time trials); `mode`
    // records which mode vocabulary produced the verdict. The
    // per-component coin fields, `completedBountyIds` and the record
    // fields let a redelivery report the honest "already applied"
    // breakdown without re-evaluating anything. Rows written before
    // Task 7 deserialize with "" lists null and zeros — the legacy
    // dedup contract (resultId + fingerprint) still governs them.
    [Serializable]
    public sealed class AppliedResultReceipt
    {
        public string resultId = "";
        public string participantId = "";
        public int awardedCoins;
        public string fingerprint = "";
        // Task 7 additive fields — absent on pre-Task-7 rows.
        public string rulesFingerprint = "";
        public string mode = "";
        public int completionCoins;
        public int placementCoins;
        public int challengeCoins;
        public int bountyCoins;
        public int rivalCoins;
        public List<string> completedBountyIds = new List<string>();
        // Time-trial record facts: whether the run set a new personal
        // best and the standing best after the transaction.
        public bool recordImproved;
        public long recordBestTicks;
        // Commit-size facts kept for honest replay reporting.
        public int updatedVehicleCount;
        public int updatedTimeTrialRecordCount;

        public AppliedResultReceipt Copy()
        {
            var copy = (AppliedResultReceipt)MemberwiseClone();
            copy.completedBountyIds = completedBountyIds != null
                ? new List<string>(completedBountyIds) : null;
            return copy;
        }
    }
}
