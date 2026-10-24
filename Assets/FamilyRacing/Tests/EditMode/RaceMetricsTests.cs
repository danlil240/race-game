using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M5 Task 4: the metrics accumulator counts only at completed
    // simulation boundaries. Attribution is stamped on the boundary and
    // on each event at the action tick, so AI takeover cannot claim
    // earlier human facts and a state re-publication can never double
    // count. Shared counters record for every racer; human-skill
    // counters require a human-driven live boundary.
    public sealed class RaceMetricsTests
    {
        private static DrivingSessionState MotorState(bool grounded = true,
            float speed = 10f, bool drifting = false, bool recovering = false)
        {
            var state = new DrivingSessionState();
            state.motor = new MotorState();
            state.motor.grounded = grounded;
            state.motor.speed = speed;
            state.motor.drifting = drifting;
            state.motor.recovering = recovering;
            return state;
        }

        private static DrivingEvent Ev(string kind, string detail = "",
            string attribution = "human")
        {
            return new DrivingEvent
            {
                kind = kind,
                detail = detail,
                attribution = attribution
            };
        }

        [Test]
        public void SharedCountersApplyToEveryRacer()
        {
            var metrics = new RaceMetricsAccumulator();
            var state = MotorState();
            var events = new List<DrivingEvent>
            {
                Ev("motor.boostStart"),
                Ev("motor.wallHit"),
                Ev("shortcut.accepted", "cut-1"),
                Ev("shortcut.accepted", "cut-1"),
                Ev("shortcut.accepted", "cut-2"),
                Ev("gadget.used", "rocket"),
                Ev("gadget.used", "shield"),
                Ev("race.recovered")
            };
            metrics.RecordBoundary(1, "human-p", "human", true, state, events);
            metrics.RecordBoundary(1, "ai-p", "ai", true, state, events);
            var rows = metrics.Snapshot();
            Assert.That(rows.Count, Is.EqualTo(2));
            foreach (var row in rows)
            {
                Assert.That(row.boostActivations, Is.EqualTo(1));
                Assert.That(row.impacts, Is.EqualTo(1));
                Assert.That(row.recoveries, Is.EqualTo(1));
                Assert.That(row.acceptedShortcuts, Is.EqualTo(3));
                Assert.That(row.distinctShortcuts, Is.EqualTo(2),
                    "repeating the same shortcut must not inflate the distinct count");
                Assert.That(row.gadgetUses, Is.EqualTo(2));
                Assert.That(row.distinctGadgetTypes, Is.EqualTo(2));
            }
            Assert.That(rows[0].participantId, Is.EqualTo("human-p"));
            Assert.That(rows[1].participantId, Is.EqualTo("ai-p"));
        }

        [Test]
        public void HumanSkillCountersRequireHumanAttribution()
        {
            var metrics = new RaceMetricsAccumulator();
            var drifting = MotorState(drifting: true);
            // A drifting AI boundary counts nothing human-skilled.
            for (long t = 1; t <= 5; t++)
                metrics.RecordBoundary(t, "ai-p", "ai", true, drifting, null);
            // A drifting human boundary while not running (countdown or
            // paused) counts nothing either.
            metrics.RecordBoundary(6, "human-p", "human", false, drifting, null);
            // A drifting human on a live boundary counts drift ticks.
            for (long t = 7; t <= 9; t++)
                metrics.RecordBoundary(t, "human-p", "human", true, drifting, null);
            var rows = metrics.Snapshot();
            Assert.That(rows[0].driftTicks, Is.EqualTo(0));
            Assert.That(rows[0].longestCleanStreakTicks, Is.EqualTo(0));
            Assert.That(rows[1].driftTicks, Is.EqualTo(3));
            Assert.That(rows[1].longestCleanStreakTicks, Is.EqualTo(3));
        }

        [Test]
        public void BoundaryTickDeduplicatesCounters()
        {
            var metrics = new RaceMetricsAccumulator();
            var state = MotorState(drifting: true);
            var events = new List<DrivingEvent> { Ev("motor.boostStart") };
            // The same boundary tick published again — a snapshot
            // republish or a replay — must never move a counter.
            metrics.RecordBoundary(10, "p", "human", true, state, events);
            metrics.RecordBoundary(10, "p", "human", true, state, events);
            metrics.RecordBoundary(10, "p", "human", true, state, events);
            metrics.RecordBoundary(11, "p", "human", true, state, events);
            var row = metrics.Snapshot()[0];
            Assert.That(row.boostActivations, Is.EqualTo(2));
            Assert.That(row.driftTicks, Is.EqualTo(2));
            Assert.That(row.longestCleanStreakTicks, Is.EqualTo(2));
        }

        [Test]
        public void TakeoverNeverClaimsEarlierHumanFacts()
        {
            var metrics = new RaceMetricsAccumulator();
            var state = MotorState(drifting: true);
            // The human drives boundaries 1..3 — the boost event carries
            // its own human attribution stamped at the action tick.
            for (long t = 1; t <= 3; t++)
            {
                var events = t == 2
                    ? new List<DrivingEvent>
                        { Ev("motor.boostStart", "", "human") }
                    : null;
                metrics.RecordBoundary(t, "p", "human", true, state, events);
            }
            // AI takeover at boundary 4: the boundary attribution flips
            // but the earlier human facts stay credited, and a later
            // AI-attributed boost event counts nothing human-skilled.
            metrics.RecordBoundary(4, "p", "ai", true, state,
                new List<DrivingEvent>
                    { Ev("motor.boostStart", "", "ai") });
            var row = metrics.Snapshot()[0];
            Assert.That(row.attribution, Is.EqualTo("ai"));
            Assert.That(row.boostActivations, Is.EqualTo(1),
                "AI takeover must not erase or inflate the human boost");
            Assert.That(row.driftTicks, Is.EqualTo(3),
                "AI boundaries never add human drift ticks");
            // The AI boundary is not a clean-streak continuation.
            Assert.That(row.longestCleanStreakTicks, Is.EqualTo(3));
        }

        [Test]
        public void CleanStreakBreaksOnImpactRecoveryAndPause()
        {
            var metrics = new RaceMetricsAccumulator();
            var state = MotorState();
            for (long t = 1; t <= 4; t++)
                metrics.RecordBoundary(t, "p", "human", true, state, null);
            // An impact on the boundary breaks the streak.
            metrics.RecordBoundary(5, "p", "human", true, state,
                new List<DrivingEvent> { Ev("motor.wallHit") });
            metrics.RecordBoundary(6, "p", "human", true, state, null);
            metrics.RecordBoundary(7, "p", "human", true, state, null);
            // A paused boundary (running=false) freezes the streak, not
            // grow it.
            metrics.RecordBoundary(8, "p", "human", false, state, null);
            // Recovery breaks it again.
            metrics.RecordBoundary(9, "p", "human", true,
                MotorState(recovering: true), null);
            metrics.RecordBoundary(10, "p", "human", true, state, null);
            var row = metrics.Snapshot()[0];
            Assert.That(row.longestCleanStreakTicks, Is.EqualTo(4));
            Assert.That(row.impacts, Is.EqualTo(1));
        }

        [Test]
        public void StandardsFillAuthoritativePlacementFacts()
        {
            var metrics = new RaceMetricsAccumulator();
            metrics.RecordBoundary(1, "p", "human", true, MotorState(), null);
            metrics.UpdateStandards("p", 1, "finished", 210, 250);
            var row = metrics.Snapshot()[0];
            Assert.That(row.position, Is.EqualTo(1));
            Assert.That(row.outcome, Is.EqualTo("finished"));
            Assert.That(row.finishTicks, Is.EqualTo(210));
            Assert.That(row.elapsedTicks, Is.EqualTo(250));
            // A DNF row carries no finish clock.
            metrics.UpdateStandards("p", 3, "dnf", 0, 400);
            row = metrics.Snapshot()[0];
            Assert.That(row.outcome, Is.EqualTo("dnf"));
            Assert.That(row.finishTicks, Is.EqualTo(0));
        }
    }
}
