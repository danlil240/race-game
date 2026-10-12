using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // Bounded racer-to-racer contact source built once per race step from
    // the single pre-step racer snapshot. Every participant's contact
    // queries this tick read that one fixed view, so a racer stepped
    // earlier can never leak its post-step pose into a later racer's
    // contact — iteration order cannot change outcomes.
    //
    // Contact semantics: two bodies nudge when their ground-plane
    // separation is inside NetworkSettings.ContactRadiusMeters and their
    // vertical separation is inside ContactVerticalRangeMeters (a racer
    // mid-jump never contacts the ground layer). The impulse pushes the
    // querying body directly away from the neighbour, proportional to
    // the overlap and individually capped at
    // DriftBoostRules.NudgeMaxDeltaSpeed; the motor additionally clamps
    // the summed applied delta per step. Exactly-overlapping poses —
    // stacked spawn or recovery bodies — split deterministically along
    // ±x by stable participant-id order, never NaN.
    public sealed class RacerContacts
    {
        // One racer's pre-step body: identity plus the motor snapshot it
        // had before any session stepped this tick.
        public sealed class Entry
        {
            public string participantId = "";
            public MotorState motor;
        }

        private readonly List<Entry> entries;

        public RacerContacts(IReadOnlyList<Entry> racers)
        {
            entries = new List<Entry>();
            if (racers != null) entries.AddRange(racers);
        }

        // The per-participant contact view handed to that racer's
        // session step. Unknown or empty ids still receive a working
        // source — they simply exclude nothing by identity.
        public IRacerContactSource ForParticipant(string participantId)
        {
            return new ParticipantContact(entries, participantId ?? "");
        }

        private sealed class ParticipantContact : IRacerContactSource
        {
            private readonly List<Entry> entries;
            private readonly string selfId;

            internal ParticipantContact(List<Entry> entries, string selfId)
            {
                this.entries = entries;
                this.selfId = selfId;
            }

            public List<RacerNudge> NudgesFor(MotorState self)
            {
                var nudges = new List<RacerNudge>();
                if (self == null) return nudges;
                var radius = NetworkSettings.ContactRadiusMeters;
                var radiusSquared = radius * radius;
                foreach (var entry in entries)
                {
                    if (entry == null || entry.motor == null) continue;
                    // The self body can never nudge itself — identity is
                    // the binding, not the pose.
                    if (entry.participantId == selfId) continue;
                    var other = entry.motor;
                    if (Math.Abs(self.y - other.y)
                        > NetworkSettings.ContactVerticalRangeMeters)
                        continue;
                    var dx = self.x - other.x;
                    var dz = self.z - other.z;
                    var d2 = dx * dx + dz * dz;
                    if (d2 >= radiusSquared) continue;

                    float nx, nz, dist;
                    if (d2 < 0.000001f)
                    {
                        // Exact overlap: direction is undefined, so split
                        // along ±x by stable id order. The pair separates
                        // deterministically and independently of
                        // iteration order — for any ordered pair of ids
                        // the pushes point apart.
                        nx = string.CompareOrdinal(selfId, entry.participantId)
                            <= 0 ? 1f : -1f;
                        nz = 0f;
                        dist = 0f;
                    }
                    else
                    {
                        dist = (float)Math.Sqrt(d2);
                        nx = dx / dist;
                        nz = dz / dist;
                    }
                    var impulse = Math.Min(
                        (radius - dist) * NetworkSettings.ContactNudgeImpulsePerMeter,
                        DriftBoostRules.NudgeMaxDeltaSpeed);
                    if (impulse <= 0f) continue;
                    nudges.Add(new RacerNudge
                    {
                        impulseX = nx * impulse,
                        impulseZ = nz * impulse,
                        racerId = entry.participantId
                    });
                }
                return nudges;
            }
        }
    }
}
