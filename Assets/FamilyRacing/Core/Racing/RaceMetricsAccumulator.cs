using System;
using System.Collections.Generic;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // Counts authoritative per-racer metrics at completed simulation
    // boundaries only. The coordinator (or the solo session for a lone
    // trial) feeds one boundary per stepped tick with the committed
    // session state plus the events that boundary emitted — the events
    // already carry the attribution stamped at their action tick, so a
    // later AI takeover can never retro-claim human facts, and a state
    // re-publication (Snapshot) can never double count because nothing
    // calls RecordBoundary for it.
    //
    // Boundary dedup is the last line of defense: the same boundaryTick
    // recorded twice for a participant is ignored, so paused or
    // republished boundaries cannot move any counter.
    public sealed class RaceMetricsAccumulator
    {
        // Forward progress a clean-driving streak tick requires: the
        // human must actually be driving — grounded, moving, not
        // respawning — not merely holding throttle.
        private const float CleanMinSpeed = 0.5f;

        private sealed class Accum
        {
            internal RaceMetrics metrics = new RaceMetrics();
            internal int cleanStreak;
            internal long lastBoundary = -1;
            internal readonly HashSet<string> shortcutIds =
                new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> gadgetIds =
                new HashSet<string>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Accum> racers =
            new Dictionary<string, Accum>(StringComparer.Ordinal);
        private readonly List<string> order = new List<string>();

        // One committed boundary for one racer. `running` marks the
        // boundary as a live race-clock tick (shared clock already past
        // countdown, not paused, not finalized) — the coordinator knows
        // this because paused/finalized boundaries never reach here.
        // Events are this boundary's own emitted list with attribution
        // already stamped.
        public void RecordBoundary(long boundaryTick, string participantId,
            string attribution, bool running, DrivingSessionState state,
            IReadOnlyList<DrivingEvent> events)
        {
            if (string.IsNullOrEmpty(participantId)) return;
            var accum = For(participantId);
            if (boundaryTick == accum.lastBoundary) return;
            accum.lastBoundary = boundaryTick;

            var metrics = accum.metrics;
            metrics.participantId = participantId;
            metrics.attribution = attribution ?? "";
            var motor = state != null ? state.motor : null;
            var human = attribution == "human";
            var driving = human && running && motor != null && !motor.recovering;
            if (driving && motor.drifting) metrics.driftTicks++;

            var broke = !driving;
            if (events != null)
            {
                foreach (var ev in events)
                {
                    if (ev == null) continue;
                    switch (ev.kind)
                    {
                        case "motor.boostStart":
                            if (ev.attribution == "human" || ev.attribution == "")
                                metrics.boostActivations++;
                            break;
                        case "motor.cleanLanding":
                            // A validated clean landing: the motor only
                            // emits it when the recovery-flagged boundary
                            // did not produce it and the landing probe
                            // confirmed the surface.
                            if (ev.attribution == "human" || ev.attribution == "")
                                metrics.cleanLandings++;
                            break;
                        case "motor.wallHit":
                        case "motor.racerNudge":
                            metrics.impacts++;
                            broke = true;
                            break;
                        case "race.recovered":
                            metrics.recoveries++;
                            broke = true;
                            break;
                        case "shortcut.accepted":
                            metrics.acceptedShortcuts++;
                            if (!string.IsNullOrEmpty(ev.detail)
                                && accum.shortcutIds.Add(ev.detail))
                                metrics.distinctShortcuts++;
                            broke = true;
                            break;
                        case "gadget.used":
                            metrics.gadgetUses++;
                            if (!string.IsNullOrEmpty(ev.detail)
                                && accum.gadgetIds.Add(ev.detail))
                                metrics.distinctGadgetTypes++;
                            break;
                    }
                }
            }

            // Longest clean-driving streak: consecutive live boundary
            // ticks the human drove grounded and moving without an
            // impact, recovery or shortcut traversal. Countdown, pause
            // and recovery movement never enter the streak.
            var clean = driving && !broke && motor.grounded
                && motor.speed > CleanMinSpeed;
            if (clean)
            {
                accum.cleanStreak++;
                if (accum.cleanStreak > metrics.longestCleanStreakTicks)
                    metrics.longestCleanStreakTicks = accum.cleanStreak;
            }
            else
            {
                accum.cleanStreak = 0;
            }
        }

        // Authoritative placement/clock facts the coordinator owns —
        // refreshed every boundary so a mid-race metrics snapshot is
        // truthful without manufacturing counters.
        public void UpdateStandards(string participantId, int position,
            string outcome, long finishTicks, long elapsedTicks)
        {
            if (string.IsNullOrEmpty(participantId)) return;
            var accum = For(participantId);
            var metrics = accum.metrics;
            metrics.position = position;
            metrics.outcome = outcome ?? "";
            metrics.finishTicks = outcome == "finished" ? finishTicks : 0;
            metrics.elapsedTicks = elapsedTicks;
        }

        // Independent copies in first-seen roster order — observers
        // mutate freely without touching the live counters.
        public List<RaceMetrics> Snapshot()
        {
            var snapshot = new List<RaceMetrics>(order.Count);
            foreach (var id in order)
                snapshot.Add(racers[id].metrics.Copy());
            return snapshot;
        }

        private Accum For(string participantId)
        {
            Accum accum;
            if (!racers.TryGetValue(participantId, out accum))
            {
                accum = new Accum();
                racers[participantId] = accum;
                order.Add(participantId);
            }
            return accum;
        }
    }
}
