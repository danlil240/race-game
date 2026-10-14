using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    // Schema-3 progression records. Every row type is a flat serializable
    // record with a Copy() that returns an independent instance, so
    // ProfileCopy.DeepCopy can guarantee no snapshot, draft or candidate
    // ever shares a mutable row with the live profile.

    // Per regular campaign event: the completion fact plus the three-bit
    // objective mask (completion/introductory/mastery stars), unioned
    // across replays by Task 6's evaluator.
    [Serializable]
    public sealed class EventProgress
    {
        public string eventId = "";
        public bool completed;
        public int objectiveMask;

        public EventProgress Copy()
        {
            return (EventProgress)MemberwiseClone();
        }
    }

    // Per campaign world: `completed` is marked exactly once when the
    // world's finale gate resolves; regional star counts are derived
    // from event masks, never stored here twice.
    [Serializable]
    public sealed class WorldProgress
    {
        public string worldId = "";
        public bool completed;

        public WorldProgress Copy()
        {
            return (WorldProgress)MemberwiseClone();
        }
    }

    // Per rival finale: `completed` advances the world; `victorious` is
    // the one-time rival-defeat fact whose separate cosmetic and bounty
    // rewards must never pay twice.
    [Serializable]
    public sealed class FinaleProgress
    {
        public string eventId = "";
        public bool completed;
        public bool victorious;

        public FinaleProgress Copy()
        {
            return (FinaleProgress)MemberwiseClone();
        }
    }

    // Purchased per-vehicle upgrade levels (Task 8 owns the purchase
    // transaction): one row per vehicle, one level counter per category.
    [Serializable]
    public sealed class VehicleUpgrade
    {
        public string vehicleId = "";
        public int acceleration;
        public int handling;
        public int boost;

        public VehicleUpgrade Copy()
        {
            return (VehicleUpgrade)MemberwiseClone();
        }
    }

    // One active repeatable-challenge slot. `assignmentId` is a unique
    // per-profile identity minted at assignment time, so a rotated-in
    // replacement is provably not its predecessor; `progress` accumulates
    // finalized human-attributed metric amounts in the definition's own
    // unit (count/seconds/ticks — thresholds are longs in the catalog).
    [Serializable]
    public sealed class BountyAssignment
    {
        public string assignmentId = "";
        public string bountyId = "";
        public long progress;

        public BountyAssignment Copy()
        {
            return (BountyAssignment)MemberwiseClone();
        }
    }

    // Best time-trial result per course/class pair: only a lower
    // positive elapsed-tick count ever replaces it (Task 7 owns the
    // update rule).
    [Serializable]
    public sealed class TimeTrialRecord
    {
        public string courseId = "";
        public string classId = "";
        public long bestTicks;

        public TimeTrialRecord Copy()
        {
            return (TimeTrialRecord)MemberwiseClone();
        }
    }

    // The challenge assignments every new or migrated profile starts
    // with: the three initial definitions from the gameplay catalog
    // (Task 2), each under a fresh assignment identity at zero progress.
    // Assignment ids are minted per profile, so a serialized row can
    // never collide across profiles or with a later rotated replacement.
    public static class ProfileProgressSeed
    {
        public static readonly string[] InitialBountyIds =
            { "drift-10s", "boost-3", "landing-2" };

        public static List<BountyAssignment> CreateInitialBounties()
        {
            var assignments = new List<BountyAssignment>(InitialBountyIds.Length);
            foreach (var bountyId in InitialBountyIds)
            {
                assignments.Add(new BountyAssignment
                {
                    assignmentId = Guid.NewGuid().ToString("D"),
                    bountyId = bountyId,
                    progress = 0
                });
            }
            return assignments;
        }
    }
}
