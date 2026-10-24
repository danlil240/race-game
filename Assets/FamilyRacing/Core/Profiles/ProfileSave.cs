using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    [Serializable]
    public sealed class ProfileSave
    {
        public int schemaVersion = 3;
        public string id;
        public int slot;
        public string avatar;
        public int colorIndex;
        public string language;
        public string camera;
        public bool reducedCameraShake;
        public float musicVolume;
        public float effectsVolume;
        public float voiceVolume;
        public int coins;
        public List<string> unlockedVehicles;
        // Schema 2: receipts for immutable race results already applied to
        // this profile. Coins and receipts are always written together.
        public List<AppliedResultReceipt> appliedResults;
        // Schema 3: first-world progression. Campaign facts (per-event
        // completion/objective stars, world and finale completion),
        // purchased vehicle upgrades, owned/equipped cosmetics, the three
        // rotating challenge assignments plus their deterministic
        // rotation cursor, time-trial records, durable purchase receipts,
        // and race-bound reward contexts awaiting a finalized result.
        public List<EventProgress> eventProgress;
        public List<WorldProgress> worldProgress;
        public List<FinaleProgress> finaleProgress;
        public List<VehicleUpgrade> vehicleUpgrades;
        public List<string> ownedCosmetics;
        public List<string> equippedCosmetics;
        public List<BountyAssignment> activeBounties;
        public int bountyRotationCursor;
        public List<TimeTrialRecord> timeTrialRecords;
        public List<PurchaseReceipt> purchaseReceipts;
        public List<RaceRewardContext> pendingRaceContexts;
        // Durable floor for locally minted solo-race identities: every
        // bound reward context advances it inside the same atomic save,
        // so a crashed run's pending row can never be silently adopted
        // by a fresh process re-minting the same identity. Older files
        // deserialize it as 0 — additive within schema 3.
        public int localRaceOrdinal;
    }
}
