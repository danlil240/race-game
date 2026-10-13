using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 5: the host-side validated input path every racer — including
    // the host's own local racer — must pass through before a sample may
    // steer an authoritative NetworkRace. The host derives ownership from
    // the authenticated connection (the `connectionParticipantId`
    // argument), never from the payload's participantId.
    //
    // Validation order is part of the contract; the tests pin it:
    //   invalidFrame -> noRace -> staleSession -> staleRace ->
    //   unknownConnection -> foreignOwnership -> unknownParticipant ->
    //   staleEpoch -> nonFiniteInput -> outOfRangeInput -> tooManyActions
    //   -> badActionId -> duplicateSequence -> futureSequence ->
    //   bufferOverflow
    public sealed class HostInputBufferTests
    {
        private const string SessionId = "sess-1";
        private const string RaceId = "sess-1-r1";

        private HostInputBuffer buffer;

        [SetUp]
        public void SetUp()
        {
            buffer = new HostInputBuffer();
            buffer.BeginRace(SessionId, RaceId);
            buffer.SeatParticipant("p-host", 1);
            buffer.SeatParticipant("p-guest", 1);
        }

        private static NetworkInputFrame Frame(string participantId, long sequence,
            float steering = 0f, long epoch = 1, string raceId = RaceId,
            string sessionId = SessionId, long clientTick = 0,
            bool drift = false, bool boost = false, bool recover = false,
            string[] actionIds = null, float brake = 0f,
            bool hasThrottleOverride = false, float throttle = 0f)
        {
            var frame = new NetworkInputFrame
            {
                sessionId = sessionId,
                raceId = raceId,
                participantId = participantId,
                ownershipEpoch = epoch,
                inputSequence = sequence,
                clientTick = clientTick,
                steering = steering,
                drift = drift,
                boost = boost,
                recover = recover,
                brake = brake,
                hasThrottleOverride = hasThrottleOverride,
                throttle = throttle
            };
            if (actionIds != null) frame.actionIds.AddRange(actionIds);
            return frame;
        }

        private static bool Enqueue(HostInputBuffer target, NetworkInputFrame frame,
            string connectionParticipantId, out string error)
        {
            return target.TryEnqueue(frame, connectionParticipantId, out error);
        }

        // --- identity, session and epoch fencing ----------------------------

        [Test]
        public void ForeignOwnershipRejected()
        {
            // The payload claims p-host but the authenticated connection
            // owns p-guest: rejected before it can touch p-host's lane.
            var accepted = Enqueue(buffer, Frame("p-host", 1), "p-guest", out var error);
            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo("foreignOwnership"));
            Assert.That(buffer.QueueDepth("p-host"), Is.EqualTo(0),
                "a foreign frame must never enter another racer's queue");

            // The connection's own participant is accepted.
            accepted = Enqueue(buffer, Frame("p-guest", 1), "p-guest", out error);
            Assert.That(accepted, Is.True, error);
        }

        [Test]
        public void UnknownConnectionAndParticipantRejected()
        {
            string error;
            Assert.That(Enqueue(buffer, Frame("p-guest", 1), null, out error), Is.False);
            Assert.That(error, Is.EqualTo("unknownConnection"));
            Assert.That(Enqueue(buffer, Frame("p-guest", 1), "", out error), Is.False);
            Assert.That(error, Is.EqualTo("unknownConnection"));
            // Authenticated connection whose participant was never seated.
            Assert.That(Enqueue(buffer, Frame("p-stray", 1), "p-stray", out error), Is.False);
            Assert.That(error, Is.EqualTo("unknownParticipant"));
        }

        [Test]
        public void WrongRaceOrSessionRejected()
        {
            string error;
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 1, sessionId: "other-session"), "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("staleSession"));
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 1, raceId: "sess-1-r9"), "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("staleRace"));

            // Without an open race nothing is accepted at all — a frame for
            // a session this process has never seen cannot smuggle input in.
            var fresh = new HostInputBuffer();
            fresh.SeatParticipant("p-guest", 1);
            Assert.That(fresh.TryEnqueue(Frame("p-guest", 1), "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("noRace"));
            Assert.That(Enqueue(buffer, null, "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("invalidFrame"));
        }

        [Test]
        public void StaleRaceCannotAffectNewRace()
        {
            // Enqueue one sample in race 1, then rematch into race 2:
            // traffic still carrying the old race id is fenced out before
            // it can reach the newly spawned session.
            Assert.That(Enqueue(buffer, Frame("p-guest", 1, steering: 0.5f),
                "p-guest", out _), Is.True);
            buffer.BeginRace(SessionId, "sess-1-r2");
            string error;
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 2, raceId: RaceId), "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("staleRace"));
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(0),
                "stale-race traffic must not survive into the new race's queue");
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 3, raceId: "sess-1-r2"), "p-guest", out error), Is.True, error);
        }

        [Test]
        public void StaleEpochRejected()
        {
            string error;
            // Old epoch from before a reclaim: rejected, lane untouched.
            Assert.That(Enqueue(buffer, Frame("p-guest", 1, epoch: 0),
                "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("staleEpoch"));

            Assert.That(Enqueue(buffer, Frame("p-guest", 1, epoch: 1, steering: 0.5f),
                "p-guest", out error), Is.True, error);

            // Reconnect bumps the epoch; every queued old-epoch sample is
            // purged so it can never drive the reclaimed racer.
            buffer.SetOwnershipEpoch("p-guest", 2);
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(0),
                "an ownership transfer must purge in-flight old-epoch samples");
            Assert.That(Enqueue(buffer, Frame("p-guest", 2, epoch: 1),
                "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("staleEpoch"));
            Assert.That(Enqueue(buffer, Frame("p-guest", 2, epoch: 2),
                "p-guest", out error), Is.True, error);
        }

        // --- payload sanity --------------------------------------------------

        [Test]
        public void NonFiniteInputRejected()
        {
            string error;
            foreach (var bad in new[] { float.NaN, float.PositiveInfinity,
                         float.NegativeInfinity })
            {
                Assert.That(Enqueue(buffer, Frame("p-guest", 1, steering: bad),
                    "p-guest", out error), Is.False, "steering " + bad);
                Assert.That(error, Is.EqualTo("nonFiniteInput"));
            }
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(0));
            // A finite out-of-range value is accepted; DriverInput clamps it.
            Assert.That(Enqueue(buffer, Frame("p-guest", 1, steering: 9f),
                "p-guest", out error), Is.True, error);
            var consumed = buffer.Consume("p-guest", 1);
            Assert.That(consumed.Steering, Is.EqualTo(1f));
        }

        // Task 7: brake and throttle ride the same bounded wire contract —
        // non-finite values fail at the same gate as steering, and
        // out-of-range unit demands are rejected outright rather than
        // clamped, because a hostile "200% brake" is malformed input.
        [Test]
        public void NonFiniteBrakeAndThrottleRejected()
        {
            string error;
            foreach (var bad in new[] { float.NaN, float.PositiveInfinity,
                         float.NegativeInfinity })
            {
                Assert.That(Enqueue(buffer, Frame("p-guest", 1, brake: bad),
                    "p-guest", out error), Is.False, "brake " + bad);
                Assert.That(error, Is.EqualTo("nonFiniteInput"));
                Assert.That(Enqueue(buffer, Frame("p-guest", 1, throttle: bad,
                        hasThrottleOverride: true), "p-guest", out error),
                    Is.False, "throttle " + bad);
                Assert.That(error, Is.EqualTo("nonFiniteInput"));
            }
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(0));
        }

        [Test]
        public void OutOfRangeBrakeAndThrottleRejected()
        {
            string error;
            foreach (var bad in new[] { -0.01f, 1.01f, 4f })
            {
                Assert.That(Enqueue(buffer, Frame("p-guest", 1, brake: bad),
                    "p-guest", out error), Is.False, "brake " + bad);
                Assert.That(error, Is.EqualTo("outOfRangeInput"));
            }
            Assert.That(Enqueue(buffer, Frame("p-guest", 1,
                    hasThrottleOverride: true, throttle: 1.5f),
                "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("outOfRangeInput"));
            // An absent override carries no throttle meaning, so its raw
            // field is never range-gated — only finiteness applies.
            Assert.That(Enqueue(buffer, Frame("p-guest", 1,
                    hasThrottleOverride: false, throttle: 0f),
                "p-guest", out error), Is.True, error);
        }

        [Test]
        public void BrakeAndThrottleSurviveValidationAndHold()
        {
            Assert.That(Enqueue(buffer, Frame("p-guest", 1, brake: 0.7f,
                    hasThrottleOverride: true, throttle: 0.25f),
                "p-guest", out var error), Is.True, error);
            var held = buffer.Consume("p-guest", 1);
            Assert.That(held.Brake, Is.EqualTo(0.7f).Within(0.0001f));
            Assert.That(held.HasThrottleOverride, Is.True);
            Assert.That(held.Throttle, Is.EqualTo(0.25f).Within(0.0001f));

            // The held sample persists across silent host ticks inside the
            // staleness bound — the race keeps braking/coasting honestly.
            held = buffer.Consume("p-guest", 2);
            Assert.That(held.Brake, Is.EqualTo(0.7f).Within(0.0001f));
            Assert.That(held.HasThrottleOverride, Is.True);

            // A neutral frame without an override restores automatic
            // throttle and releases the brake — never an ambiguous zero.
            Assert.That(Enqueue(buffer, Frame("p-guest", 2),
                "p-guest", out error), Is.True, error);
            held = buffer.Consume("p-guest", 3);
            Assert.That(held.Brake, Is.EqualTo(0f));
            Assert.That(held.HasThrottleOverride, Is.False);
            Assert.That(held.Throttle, Is.EqualTo(1f));
        }

        [Test]
        public void OversizedActionListRejected()
        {
            var many = new string[NetworkSettings.MaxFrameActionIds + 1];
            for (var i = 0; i < many.Length; i++) many[i] = "a" + i;
            string error;
            Assert.That(Enqueue(buffer, Frame("p-guest", 1, actionIds: many),
                "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("tooManyActions"));

            var overlong = new string('x', NetworkSettings.ActionIdLengthMax + 1);
            Assert.That(Enqueue(buffer, Frame("p-guest", 1,
                    actionIds: new[] { overlong }), "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("badActionId"));
            Assert.That(Enqueue(buffer, Frame("p-guest", 1,
                    actionIds: new[] { "" }), "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("badActionId"));
        }

        // --- sequencing -------------------------------------------------------

        [Test]
        public void DuplicateAndOldSequencesIgnored()
        {
            string error;
            Assert.That(Enqueue(buffer, Frame("p-guest", 5), "p-guest", out error),
                Is.True, error);
            // The redundant resend (a packet carries up to 4 recent samples)
            // is ignored, never queued twice.
            Assert.That(Enqueue(buffer, Frame("p-guest", 5), "p-guest", out error),
                Is.False);
            Assert.That(error, Is.EqualTo("duplicateSequence"));
            // Older than the newest seen is old, not a gap-filler.
            Assert.That(Enqueue(buffer, Frame("p-guest", 3), "p-guest", out error),
                Is.False);
            Assert.That(error, Is.EqualTo("duplicateSequence"));
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(1));
        }

        [Test]
        public void FutureSequenceWindowEnforced()
        {
            string error;
            Assert.That(Enqueue(buffer, Frame("p-guest", 10), "p-guest", out error),
                Is.True, error);
            var beyond = 10 + NetworkSettings.InputSequenceWindow + 1;
            Assert.That(Enqueue(buffer, Frame("p-guest", beyond), "p-guest", out error),
                Is.False);
            Assert.That(error, Is.EqualTo("futureSequence"));
            var atEdge = 10 + NetworkSettings.InputSequenceWindow;
            Assert.That(Enqueue(buffer, Frame("p-guest", atEdge), "p-guest", out error),
                Is.True, error);
        }

        [Test]
        public void QueueIsBoundedAtCapacity()
        {
            string error;
            for (var i = 1; i <= NetworkSettings.InputQueueCapacity; i++)
                Assert.That(Enqueue(buffer, Frame("p-guest", i), "p-guest", out error),
                    Is.True, "sample " + i + ": " + error);
            // Sample 121 overflows: explicitly rejected, and the backlog is
            // cleared so a following resync does not replay stale bursts.
            Assert.That(Enqueue(buffer,
                Frame("p-guest", NetworkSettings.InputQueueCapacity + 1),
                "p-guest", out error), Is.False);
            Assert.That(error, Is.EqualTo("bufferOverflow"));
            Assert.That(buffer.OverflowCount, Is.EqualTo(1));
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(0),
                "overflow resynchronizes by clearing the pending backlog");
        }

        // --- consumption ------------------------------------------------------

        [Test]
        public void BurstsGrantNoExtraSteps()
        {
            // A packet carrying 4 samples still yields one step per host tick.
            for (var i = 1; i <= 4; i++)
                Assert.That(Enqueue(buffer, Frame("p-guest", i, steering: 0.1f * i),
                    "p-guest", out _), Is.True);
            for (var tick = 1; tick <= 4; tick++)
            {
                var input = buffer.Consume("p-guest", tick);
                Assert.That(input.Steering, Is.EqualTo(0.1f * tick).Within(0.0001f),
                    "tick " + tick + " must apply sample " + tick + " in order");
            }
            Assert.That(buffer.QueueDepth("p-guest"), Is.EqualTo(0));
        }

        [Test]
        public void HeldInputSurvivesShortGapsThenNeutralizes()
        {
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 1, steering: 1f, drift: true), "p-guest", out _), Is.True);
            var held = buffer.Consume("p-guest", 1);
            Assert.That(held.Steering, Is.EqualTo(1f));
            Assert.That(held.Drift, Is.True);

            // Held input survives the gap below the staleness bound; after
            // InputStaleTicks host ticks without a usable sample the held
            // commands/steering are cleared.
            for (var tick = 2; tick <= NetworkSettings.InputStaleTicks + 1; tick++)
            {
                held = buffer.Consume("p-guest", tick);
                var stillHeld = tick <= NetworkSettings.InputStaleTicks;
                Assert.That(held.Steering, Is.EqualTo(stillHeld ? 1f : 0f),
                    "silent tick " + tick);
                Assert.That(held.Drift, Is.EqualTo(stillHeld),
                    "held commands clear after " + NetworkSettings.InputStaleTicks
                    + " silent ticks, tick " + tick);
            }

            // A fresh sample reasserts control immediately.
            Assert.That(Enqueue(buffer, Frame("p-guest", 2, steering: -1f),
                "p-guest", out _), Is.True);
            held = buffer.Consume("p-guest", NetworkSettings.InputStaleTicks + 2);
            Assert.That(held.Steering, Is.EqualTo(-1f));
            Assert.That(held.Drift, Is.False);
        }

        [Test]
        public void UnknownParticipantConsumesNeutral()
        {
            var input = buffer.Consume("nobody", 1);
            Assert.That(input.Steering, Is.EqualTo(0f));
            Assert.That(input.Drift, Is.False);
            Assert.That(input.Boost, Is.False);
        }

        // --- one-shot actions -------------------------------------------------

        [Test]
        public void OneShotActionsDeduplicateAcrossResends()
        {
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 1, actionIds: new[] { "act-1", "act-2" }),
                "p-guest", out _), Is.True);
            // The next packet resends the same one-shot identities attached
            // to a newer held sample — applied once, independent of input.
            Assert.That(Enqueue(buffer, Frame("p-guest", 2, steering: 0.5f,
                actionIds: new[] { "act-1", "act-2" }), "p-guest", out _), Is.True);

            var drained = buffer.DrainActions("p-guest");
            Assert.That(drained, Is.EquivalentTo(new[] { "act-1", "act-2" }));
            Assert.That(buffer.DrainActions("p-guest"), Is.Empty,
                "actions drain exactly once");
        }

        [Test]
        public void OneShotDedupSurvivesReseat()
        {
            // "Old or duplicated input after reconnect must never repeat a
            // recovery pulse": the dedup set is retained across the
            // replay/reconnect boundary even though queued samples purge.
            Assert.That(Enqueue(buffer,
                Frame("p-guest", 1, actionIds: new[] { "act-1" }),
                "p-guest", out _), Is.True);
            buffer.DrainActions("p-guest");

            buffer.SeatParticipant("p-guest", 2); // reclaim: epoch bump
            Assert.That(Enqueue(buffer, Frame("p-guest", 2, epoch: 2,
                actionIds: new[] { "act-1" }), "p-guest", out _), Is.True,
                "the new-epoch frame itself is valid");
            Assert.That(buffer.DrainActions("p-guest"), Is.Empty,
                "the replayed one-shot id must not re-fire after reseat");
            Assert.That(Enqueue(buffer, Frame("p-guest", 3, epoch: 2,
                actionIds: new[] { "act-2" }), "p-guest", out _), Is.True);
            Assert.That(buffer.DrainActions("p-guest"),
                Is.EquivalentTo(new[] { "act-2" }));
        }

        // --- acknowledgment ----------------------------------------------------

        [Test]
        public void AcknowledgesOnlyConsumedInput()
        {
            // Merely received samples are never acknowledged.
            for (var i = 1; i <= 3; i++)
                Assert.That(Enqueue(buffer, Frame("p-guest", i, clientTick: 100 + i),
                    "p-guest", out _), Is.True);
            var ack = buffer.AckFor("p-guest");
            Assert.That(ack, Is.Not.Null);
            Assert.That(ack.inputSequence, Is.EqualTo(0),
                "received-but-unconsumed samples must not be acknowledged");

            var consumed = buffer.Consume("p-guest", 42);
            Assert.That(consumed, Is.Not.Null);
            ack = buffer.AckFor("p-guest");
            Assert.That(ack.inputSequence, Is.EqualTo(1),
                "the ack names the last processed input, not the newest received");
            Assert.That(ack.clientTick, Is.EqualTo(101));
            Assert.That(ack.hostTick, Is.EqualTo(42),
                "the ack identifies the authoritative host tick it fed");
            Assert.That(ack.participantId, Is.EqualTo("p-guest"));

            buffer.Consume("p-guest", 43);
            buffer.Consume("p-guest", 44);
            ack = buffer.AckFor("p-guest");
            Assert.That(ack.inputSequence, Is.EqualTo(3));
            Assert.That(ack.hostTick, Is.EqualTo(44));
        }

        [Test]
        public void HostLocalRacerUsesSameValidatedPath()
        {
            // The host's own racer is not a special case: it is validated
            // and queued exactly like a remote participant's stream.
            string error;
            Assert.That(Enqueue(buffer, Frame("p-host", 1, steering: -0.5f),
                "p-host", out error), Is.True, error);
            Assert.That(Enqueue(buffer, Frame("p-host", 1),
                "p-host", out error), Is.False);
            Assert.That(error, Is.EqualTo("duplicateSequence"));
            var input = buffer.Consume("p-host", 1);
            Assert.That(input.Steering, Is.EqualTo(-0.5f));
        }
    }
}
