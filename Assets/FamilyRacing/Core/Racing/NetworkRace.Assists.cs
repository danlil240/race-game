using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Racing
{
    // Fun-pass race assists layered on the shared coordinator. Everything
    // here reads the same pre-step snapshot every racer sees and only
    // scales the per-tick MotorModifiers seam, so it stays deterministic
    // and backend-neutral:
    //
    //   * Slipstream: tuck in behind a rival for a second and the wake
    //     pulls you along (more acceleration and a slightly higher cap),
    //     with a short slingshot after you pull out to pass.
    //   * Rival catch-up (local races only): AI that falls far behind the
    //     leading human gets a little extra pace, and AI far ahead eases
    //     off, so there is always someone to fight with.
    //   * Elimination mode: every interval the last racer still running
    //     is knocked out; the last one standing wins.
    public sealed partial class NetworkRace
    {
        // Slipstream geometry and payout.
        public const float SlipstreamMinSpeed = 15f;        // m/s
        public const float SlipstreamMinGapMeters = 3f;
        public const float SlipstreamMaxGapMeters = 28f;
        public const float SlipstreamHalfWidthMeters = 2.8f;
        public const int SlipstreamChargeTicks = 30;         // 1 s in the wake
        public const int SlipstreamSlingTicks = 24;          // 0.8 s carry-over
        public const float SlipstreamAcceleration = 1.5f;
        public const float SlipstreamSpeedCap = 1.08f;

        // Rival catch-up bands (metres of race distance to the best human).
        public const float CatchUpStartMeters = 30f;
        public const float CatchUpFullMeters = 280f;
        public const float CatchUpMaxAcceleration = 1.45f;
        public const float CatchUpMaxSpeedCap = 1.10f;
        public const float EaseOffStartMeters = 50f;
        public const float EaseOffFullMeters = 350f;
        public const float EaseOffMinSpeedCap = 0.92f;

        // Elimination: one knockout every 30 s of race clock by default.
        public const int DefaultEliminationIntervalTicks = 30 * 30;

        private int eliminationIntervalTicks;
        private long nextEliminationElapsedTicks;
        private int eliminationCount;
        private float[] gateDistance;   // race metres from gate 0 to gate i
        private float lapLength;

        // Slipstream is on by default; tests that compare racers'
        // raw motors side by side switch it off.
        public bool SlipstreamEnabled { get; set; } = true;

        // 0 turns Elimination off (the default). Set before the race runs.
        public int EliminationIntervalTicks
        {
            get { return eliminationIntervalTicks; }
            set
            {
                eliminationIntervalTicks = Math.Max(0, value);
                nextEliminationElapsedTicks = eliminationIntervalTicks;
            }
        }

        // --- standings helpers ----------------------------------------------

        private int[] PlacesFrom(DrivingSessionState[] snaps)
        {
            var places = new int[racers.Length];
            var order = StandingOrder(snaps);
            for (var i = 0; i < order.Count; i++) places[order[i]] = i + 1;
            return places;
        }

        // Racers still competing for place-weighted draws. Eliminated
        // racers leave the field, so the last two standing are leader
        // and last.
        private int ActiveFieldSize()
        {
            var n = 0;
            foreach (var racer in racers)
                if (racer != null && !racer.eliminated) n++;
            return n;
        }

        // Metres of race travelled: completed laps, gates on this lap and
        // the clamped progress toward the next gate.
        internal float RaceDistance(DrivingSessionState s)
        {
            if (s == null) return 0f;
            EnsureGateDistances();
            var gates = gateDistance != null ? gateDistance.Length : 0;
            if (gates == 0) return 0f;
            var passed = Math.Max(0, Math.Min(gates - 1, s.acceptedGatesThisLap));
            if (s.finished) return (s.lap + 1) * lapLength + gateDistance[gates - 1];
            return s.lap * lapLength + gateDistance[passed] + s.segmentProgressMeters;
        }

        private void EnsureGateDistances()
        {
            if (gateDistance != null) return;
            var gates = course.checkpoints != null ? course.checkpoints.Count : 0;
            gateDistance = new float[gates];
            var total = 0f;
            for (var i = 1; i < gates; i++)
            {
                var a = course.checkpoints[i - 1];
                var b = course.checkpoints[i];
                float dx = b.x - a.x, dz = b.z - a.z;
                total += (float)Math.Sqrt(dx * dx + dz * dz);
                gateDistance[i] = total;
            }
            if (gates > 1 && !course.isOpenRoute)
            {
                var a = course.checkpoints[gates - 1];
                var b = course.checkpoints[0];
                float dx = b.x - a.x, dz = b.z - a.z;
                total += (float)Math.Sqrt(dx * dx + dz * dz);
            }
            lapLength = total;
        }

        // The furthest race distance of any human still racing, or NaN
        // when no human is on course (all-AI races get no catch-up).
        private float BestHumanDistance(DrivingSessionState[] snaps)
        {
            var best = float.NaN;
            for (var i = 0; i < racers.Length; i++)
            {
                var racer = racers[i];
                if (racer == null || racer.attribution != "human"
                    || racer.finished || racer.dnf || snaps[i] == null)
                    continue;
                var d = RaceDistance(snaps[i]);
                if (float.IsNaN(best) || d > best) best = d;
            }
            return best;
        }

        // --- slipstream -------------------------------------------------------

        private void UpdateSlipstreams(DrivingSessionState[] snaps,
            List<DrivingEvent> emitted)
        {
            if (phase != PhaseRunning) return;
            for (var i = 0; i < racers.Length; i++)
            {
                var racer = racers[i];
                if (racer == null) continue;
                var was = racer.slipstream;
                var drafting = SlipstreamEnabled && !racer.finished
                    && !racer.dnf && InWake(i, snaps);
                if (drafting)
                    racer.draftTicks = Math.Min(racer.draftTicks + 1,
                        SlipstreamChargeTicks * 3);
                else
                    racer.draftTicks = Math.Max(0, racer.draftTicks - 3);
                racer.slipstream = drafting
                    && racer.draftTicks >= SlipstreamChargeTicks;
                if (racer.slingTicks > 0) racer.slingTicks--;
                if (was && !racer.slipstream && !racer.finished && !racer.dnf)
                    racer.slingTicks = SlipstreamSlingTicks;
                if (racer.slipstream && !was)
                {
                    var ev = CoordinatorEvent("race.slipstream", "");
                    ev.participantId = racer.participantId;
                    ev.attribution = racer.attribution;
                    emitted.Add(ev);
                }
            }
        }

        private bool InWake(int slot, DrivingSessionState[] snaps)
        {
            var self = snaps[slot] != null ? snaps[slot].motor : null;
            if (self == null || !self.grounded || self.recovering
                || self.speed < SlipstreamMinSpeed) return false;
            var fx = (float)Math.Sin(self.heading);
            var fz = (float)Math.Cos(self.heading);
            for (var i = 0; i < racers.Length; i++)
            {
                if (i == slot || racers[i] == null || racers[i].finished
                    || racers[i].dnf || snaps[i] == null) continue;
                var other = snaps[i].motor;
                if (other == null || Math.Abs(other.y - self.y) > 2f) continue;
                var dx = other.x - self.x;
                var dz = other.z - self.z;
                var ahead = dx * fx + dz * fz;
                var side = Math.Abs(dx * fz - dz * fx);
                if (ahead >= SlipstreamMinGapMeters
                    && ahead <= SlipstreamMaxGapMeters
                    && side <= SlipstreamHalfWidthMeters)
                    return true;
            }
            return false;
        }

        // --- modifiers --------------------------------------------------------

        private MotorModifiers ApplyAssists(Racer racer, MotorModifiers mods,
            DrivingSessionState[] snaps, float bestHuman)
        {
            if (mods == null) mods = MotorModifiers.Neutral();
            if (racer.slipstream || racer.slingTicks > 0)
            {
                mods.accelerationMultiplier *= SlipstreamAcceleration;
                mods.speedCapMultiplier *= SlipstreamSpeedCap;
            }
            if (local != null && racer.attribution == "ai"
                && !float.IsNaN(bestHuman) && snaps[racer.slot] != null)
            {
                var gap = bestHuman - RaceDistance(snaps[racer.slot]);
                CatchUp(gap, out var accel, out var cap);
                mods.accelerationMultiplier *= accel;
                mods.speedCapMultiplier *= cap;
            }
            return mods;
        }

        // Positive gap: the AI is behind the leading human by that many
        // metres. Pure so tests can pin the bands.
        public static void CatchUp(float gap, out float acceleration,
            out float speedCap)
        {
            acceleration = 1f;
            speedCap = 1f;
            if (float.IsNaN(gap)) return;
            if (gap > CatchUpStartMeters)
            {
                var f = Math.Min(1f, (gap - CatchUpStartMeters)
                    / (CatchUpFullMeters - CatchUpStartMeters));
                acceleration = 1f + (CatchUpMaxAcceleration - 1f) * f;
                speedCap = 1f + (CatchUpMaxSpeedCap - 1f) * f;
            }
            else if (gap < -EaseOffStartMeters)
            {
                var f = Math.Min(1f, (-gap - EaseOffStartMeters)
                    / (EaseOffFullMeters - EaseOffStartMeters));
                speedCap = 1f - (1f - EaseOffMinSpeedCap) * f;
            }
        }

        // --- elimination ------------------------------------------------------

        private void StepElimination(List<DrivingEvent> emitted)
        {
            if (eliminationIntervalTicks <= 0 || result != null
                || phase != PhaseRunning
                || elapsedTicks < nextEliminationElapsedTicks) return;
            nextEliminationElapsedTicks += eliminationIntervalTicks;
            // Once someone crosses the line the normal finish rules take
            // over; knockouts only run while everybody is still racing.
            foreach (var racer in racers)
                if (racer.finished) return;
            var snaps = SnapshotAll();
            var order = StandingOrder(snaps);
            var active = new List<Racer>();
            foreach (var slot in order)
                if (!racers[slot].finished && !racers[slot].dnf)
                    active.Add(racers[slot]);
            if (active.Count < 2) return;

            var out_ = active[active.Count - 1];
            out_.dnf = true;
            out_.eliminated = true;
            out_.eliminationOrder = ++eliminationCount;
            out_.slipstream = false;
            out_.slingTicks = 0;
            var ev = CoordinatorEvent("race.eliminated", out_.participantId);
            ev.participantId = out_.participantId;
            ev.attribution = out_.attribution;
            emitted.Add(ev);
            active.RemoveAt(active.Count - 1);

            // The race ends when one racer is left, or when no human is
            // left to watch it: the survivors are placed in running order.
            var humansLeft = false;
            foreach (var racer in active)
                if (racer.attribution == "human") humansLeft = true;
            var anyHuman = false;
            foreach (var racer in racers)
                if (racer.attribution == "human") anyHuman = true;
            if (active.Count == 1 || (anyHuman && !humansLeft))
            {
                for (var i = 0; i < active.Count; i++)
                {
                    active[i].finished = true;
                    active[i].finishTicks = elapsedTicks + i;
                }
                var winner = CoordinatorEvent("race.lastStanding",
                    active[0].participantId);
                winner.participantId = active[0].participantId;
                winner.attribution = active[0].attribution;
                emitted.Add(winner);
            }
        }
    }
}
