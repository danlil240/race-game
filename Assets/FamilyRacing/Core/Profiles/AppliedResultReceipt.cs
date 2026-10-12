using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    // Durable proof that one immutable race result was applied to this
    // profile. Written together with the coin award inside the same
    // atomic profile save, so a crash can never leave coins without the
    // receipt (double-award) or the receipt without coins (lost award).
    //
    // `fingerprint` is the lowercase hex SHA-256 of the result's
    // versioned canonical encoding (RaceResult.Fingerprint). Replays of
    // the same resultId compare fingerprints: identical means
    // already-applied, different means a conflicting replay that must be
    // rejected even after a process restart.
    [Serializable]
    public sealed class AppliedResultReceipt
    {
        public string resultId = "";
        public string participantId = "";
        public int awardedCoins;
        public string fingerprint = "";

        public AppliedResultReceipt Copy()
        {
            return (AppliedResultReceipt)MemberwiseClone();
        }
    }
}
