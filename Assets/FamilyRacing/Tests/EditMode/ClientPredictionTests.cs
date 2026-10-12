using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 6 client prediction + remote interpolation contract. The
    // prediction side predicts the local motor through the same
    // ArcadeMotor.Step the host runs, restores-and-replays only
    // unacknowledged inputs on each snapshot, bounds the pending buffer
    // at the shared replay capacity, and never re-emits authoritative
    // events or one-shots — recovery, checkpoints, landings and contacts
    // stay host-decisive. The remote side buffers authoritative poses
    // and samples them 100 ms behind the newest host tick, extrapolating
    // at most 100 ms before holding the last pose as stale.
    public sealed class ClientPredictionTests
    {
        private const float Dt = DrivingSession.StepSeconds;

        // Flat infinite ground plane, no obstacles — the same minimal
        // world the replication harness runs the authoritative sim on.
        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance,
                    x = pose.x,
                    y = 0f,
                    z = pose.z,
                    normalY = 1f,
                    distance = pose.y,
                    surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy,
                float dz, float halfWidth, float halfLength, float height)
            {
                return new SweepHit { hit = false, fraction = 1f };
            }
        }

        private static readonly FlatWorld World = new FlatWorld();
        private static readonly VehicleTuning Tuning = VehicleTuning.Car01();

        private static MotorState Spawn(long tick)
        {
            return new MotorState
            {
                x = 0f, y = 0f, z = 0f, heading = 0f,
                grounded = true, tick = tick
            };
        }

        private static NetworkInputFrame Frame(long sequence, float steering,
            bool drift = false, bool boost = false, bool recover = false,
            params string[] actionIds)
        {
            var frame = new NetworkInputFrame
            {
                sessionId = "sess-1",
                raceId = "race-1",
                participantId = "p1",
                ownershipEpoch = 1,
                inputSequence = sequence,
                clientTick = sequence,
                steering = steering,
                drift = drift,
                boost = boost,
                recover = recover
            };
            if (actionIds != null)
                frame.actionIds.AddRange(actionIds);
            return frame;
        }

        // A "host" mirror: the same ArcadeMotor step on the same world,
        // standing in for the authoritative sim an ack describes.
        private static MotorState HostStep(MotorState state, DriverInput input)
        {
            return ArcadeMotor.Step(state, input, Tuning, World, Dt).state;
        }

        private static NetworkSnapshot Snapshot(long snapshotSequence,
            string raceId, MotorState localMotor, long ackSequence,
            params NetworkRacerState[] extraRacers)
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = "sess-1",
                raceId = raceId,
                raceOrdinal = 1,
                snapshotSequence = snapshotSequence,
                state = new NetworkRaceState
                {
                    sessionId = "sess-1",
                    raceId = raceId,
                    hostTick = localMotor != null ? localMotor.tick : 0
                }
            };
            snapshot.state.racers.Add(new NetworkRacerState
            {
                participantId = "p1",
                slot = 0,
                vehicleId = "car-01",
                session = new DrivingSessionState
                {
                    phase = "running",
                    motor = localMotor
                }
            });
            if (extraRacers != null)
                snapshot.state.racers.AddRange(extraRacers);
            snapshot.acks.Add(new NetworkInputAck
            {
                participantId = "p1",
                inputSequence = ackSequence,
                clientTick = ackSequence,
                hostTick = snapshot.state.hostTick
            });
            return snapshot;
        }

        private static NetworkRacerState RemoteRacer(string participantId,
            MotorState motor)
        {
            return new NetworkRacerState
            {
                participantId = participantId,
                slot = 1,
                vehicleId = "car-01",
                session = new DrivingSessionState
                {
                    phase = "running",
                    motor = motor
                }
            };
        }

        private static float Distance(MotorState a, MotorState b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            var dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [Test]
        public void LocalInputChangesPoseBeforeRoundTrip()
        {
            var prediction = new ClientPrediction("p1");
            var seeded = prediction.Reconcile(
                Snapshot(1, "race-1", Spawn(10), 0), Tuning, World);
            Assert.That(seeded, Is.Not.Null, "first snapshot seeds prediction");

            // Local samples advance the predicted motor immediately —
            // acks for them cannot exist until a host round trip later.
            // Steering authority is speed-scaled, so a handful of ticks
            // is what a turning wheel looks like.
            MotorState predicted = null;
            for (var seq = 1; seq <= 10; seq++)
                predicted = prediction.Predict(Frame(seq, 1f), Tuning, World);
            Assert.That(predicted, Is.Not.Null);
            Assert.That(predicted.tick, Is.GreaterThan(seeded.tick),
                "the predicted motor must advance on local input alone");
            Assert.That(predicted.heading, Is.Not.EqualTo(seeded.heading)
                .Within(0.0001f),
                "steering must change the predicted heading now, not after the ack");
            Assert.That(predicted.x, Is.Not.EqualTo(seeded.x)
                .Within(0.0001f));
            Assert.That(prediction.PendingCount, Is.EqualTo(10));
            Assert.That(prediction.LastAckedSequence, Is.EqualTo(0),
                "no round trip has happened yet");
        }

        [Test]
        public void CorrectionReplaysOnlyUnacknowledgedInputs()
        {
            var prediction = new ClientPrediction("p1");
            prediction.Reconcile(Snapshot(1, "race-1", Spawn(0), 0),
                Tuning, World);
            for (var seq = 1; seq <= 5; seq++)
                prediction.Predict(Frame(seq, 1f), Tuning, World);

            // The host consumed samples 1..3 — but its mirror held the
            // third sample for two extra ticks (a delayed fourth sample),
            // so the authoritative pose diverges from the client's
            // predicted-at-sequence-3 and a real correction must apply.
            var authoritative = Spawn(0);
            var input = Frame(0, 1f).input;
            authoritative = HostStep(authoritative, input);
            authoritative = HostStep(authoritative, input);
            authoritative = HostStep(authoritative, input);
            authoritative = HostStep(authoritative, input); // held
            authoritative = HostStep(authoritative, input); // held

            var reconciled = prediction.Reconcile(
                Snapshot(2, "race-1", authoritative, 3), Tuning, World);

            Assert.That(prediction.LastReplayCount, Is.EqualTo(2),
                "only samples 4 and 5 are unacknowledged — replaying 1..5 "
                + "would double-count host-consumed input");
            Assert.That(prediction.PendingCount, Is.EqualTo(2));
            Assert.That(prediction.LastErrorDistance, Is.GreaterThan(0.001f),
                "the held-input divergence is a real measured correction");
            Assert.That(prediction.Corrections, Is.GreaterThanOrEqualTo(1));

            // The corrected pose is the authoritative pose replayed through
            // exactly the two unacknowledged samples — nothing else.
            var expected = HostStep(HostStep(authoritative, input), input);
            Assert.That(Distance(reconciled, expected),
                Is.LessThan(0.0001f),
                "restore-and-replay must rebuild the predicted pose from the "
                + "authoritative motor plus unacknowledged inputs only");
        }

        [Test]
        public void StaleSnapshotIgnored()
        {
            var prediction = new ClientPrediction("p1");
            prediction.Reconcile(Snapshot(5, "race-1", Spawn(20), 0),
                Tuning, World);
            prediction.Predict(Frame(1, 1f), Tuning, World);
            var before = prediction.Predicted;
            var pendingBefore = prediction.PendingCount;

            // Reordered and duplicated snapshots must never roll the
            // prediction back or replay twice.
            prediction.Reconcile(Snapshot(4, "race-1", Spawn(-99), 3),
                Tuning, World);
            prediction.Reconcile(Snapshot(5, "race-1", Spawn(-88), 3),
                Tuning, World);
            Assert.That(prediction.StaleSnapshots, Is.EqualTo(2));
            Assert.That(prediction.PendingCount, Is.EqualTo(pendingBefore));
            var after = prediction.Predicted;
            Assert.That(after.tick, Is.EqualTo(before.tick));
            Assert.That(after.x, Is.EqualTo(before.x).Within(0.0001f));
            Assert.That(after.z, Is.EqualTo(before.z).Within(0.0001f));
        }

        [Test]
        public void ReplayDoesNotEmitAuthoritativeEvents()
        {
            var prediction = new ClientPrediction("p1");
            prediction.Reconcile(Snapshot(1, "race-1", Spawn(0), 0),
                Tuning, World);
            prediction.Predict(Frame(1, 0f), Tuning, World);
            // Boost one-shots a boostStart motor event inside a replayed
            // step; recover one-shots must never fire inside prediction.
            prediction.Predict(Frame(2, 0f, boost: true), Tuning, World);
            prediction.Predict(Frame(3, 0f, boost: true, recover: true,
                actionIds: new[] { "rc-9" }), Tuning, World);

            // The host consumed only sample 1: samples 2..3 replay from
            // the authoritative pose and the boost-start step re-runs.
            var authoritative = HostStep(Spawn(0), Frame(0, 0f).input);
            prediction.Reconcile(
                Snapshot(2, "race-1", authoritative, 1), Tuning, World);

            Assert.That(prediction.SuppressedMotorEvents,
                Is.GreaterThanOrEqualTo(1),
                "replayed steps produce motor events internally — they are "
                + "consumed and suppressed, never re-emitted as "
                + "authoritative checkpoint/boost/landing/reward events");
            // The recover press rode pending into the replay: prediction
            // holds its pose (no local recovery teleport) and reports the
            // suppressed one-shot so a cancelled/replayed recover action
            // can never fire twice — the host applies it exactly once.
            Assert.That(prediction.SuppressedOneShots,
                Is.GreaterThanOrEqualTo(2),
                "the recover press plus its action id are acknowledged "
                + "host-side only; prediction never applies them");
            var predicted = prediction.Predicted;
            Assert.That(predicted.recovering, Is.False,
                "prediction must never manufacture a recovery boundary");
            Assert.That(predicted.boostActive, Is.True,
                "the predicted *state* still reflects the held boost — "
                + "only the event is suppressed");
        }

        [Test]
        public void ReplayOverflowRequestsFullState()
        {
            var prediction = new ClientPrediction("p1");
            prediction.Reconcile(Snapshot(1, "race-1", Spawn(0), 0),
                Tuning, World);
            // Flood past the 120-sample replay bound with no acks: the
            // buffer may never exceed it and overflow must resynchronize
            // explicitly rather than replaying a stale burst.
            for (var seq = 1; seq <= 130; seq++)
            {
                prediction.Predict(Frame(seq, 0.5f), Tuning, World);
                Assert.That(prediction.PendingCount,
                    Is.LessThanOrEqualTo(
                        NetworkSettings.PredictionInputCapacity),
                    "pending buffer exceeded the 120-sample replay bound");
            }
            Assert.That(prediction.ResyncRequested, Is.True,
                "overflow must request a full resync, not silently grow");
            Assert.That(prediction.PendingCount,
                Is.LessThan(NetworkSettings.PredictionInputCapacity),
                "overflow clears the pending backlog");

            // The next authoritative snapshot re-anchors cleanly: stale
            // pre-overflow inputs never replay through the fresh state.
            var reconciled = prediction.Reconcile(
                Snapshot(2, "race-1", Spawn(40), 0), Tuning, World);
            Assert.That(reconciled, Is.Not.Null);
            Assert.That(prediction.LastReplayCount,
                Is.EqualTo(prediction.PendingCount));
        }

        [Test]
        public void RecoveryOrEpochChangeClearsHistory()
        {
            var prediction = new ClientPrediction("p1");
            prediction.Reconcile(Snapshot(1, "race-1", Spawn(0), 0),
                Tuning, World);
            for (var seq = 1; seq <= 4; seq++)
                prediction.Predict(Frame(seq, 0.5f), Tuning, World);

            // An authoritative recovery teleports the racer: history from
            // before the respawn is meaningless — the correction snaps and
            // the pre-recovery buffer clears instead of blending a ghost.
            var recovered = Spawn(12);
            recovered.x = 3f; recovered.z = 17f;
            recovered.recovering = true;
            prediction.Reconcile(
                Snapshot(2, "race-1", recovered, 2), Tuning, World);
            Assert.That(prediction.LastCorrectionSnap, Is.True,
                "recovery must snap, never blend across the teleport");
            Assert.That(prediction.Snaps, Is.GreaterThanOrEqualTo(1));
            Assert.That(prediction.PendingCount, Is.EqualTo(2),
                "unacknowledged samples 3..4 still replay from the new pose");

            // A new race/ownership epoch boundary clears the same way:
            // frames from the previous identity never steer the new race.
            prediction.Reset();
            Assert.That(prediction.PendingCount, Is.EqualTo(0));
            Assert.That(prediction.HasPrediction, Is.False);
            Assert.That(prediction.HistoryCount, Is.EqualTo(0));
            prediction.Reconcile(Snapshot(1, "race-2", Spawn(0), 0),
                Tuning, World);
            prediction.Predict(Frame(1, 0.5f), Tuning, World);
            prediction.Reconcile(Snapshot(2, "race-2", Spawn(3), 1),
                Tuning, World);
            Assert.That(prediction.PendingCount, Is.EqualTo(0),
                "post-reset samples reconcile without stale history");
        }

        // --- RemoteInterpolation ------------------------------------------

        [Test]
        public void InterpolationSamplesBehindNewestAndExtrapolatesBoundedly()
        {
            var interpolation = new RemoteInterpolation();
            // 15 Hz snapshots: host ticks 0, 2, 4.
            interpolation.Push(Snapshot(1, "race-1", Spawn(0), 0,
                RemoteRacer("p2", Pose(0, 0f, 0f))));
            interpolation.Push(Snapshot(2, "race-1", Spawn(2), 0,
                RemoteRacer("p2", Pose(2, 0f, 4f))));
            var racer4 = RemoteRacer("p2", Pose(4, 0f, 8f));
            racer4.session.motor.speed = 9f;
            interpolation.Push(Snapshot(3, "race-1", Spawn(4), 0, racer4));

            // 100 ms behind the newest host tick (4/30 ≈ 0.133 s) lands
            // inside the [2,4] bracket and interpolates the real poses.
            var between = interpolation.Sample("p2", 4.0 / 30.0 - 0.033);
            Assert.That(between, Is.Not.Null);
            Assert.That(between.z, Is.GreaterThan(4f).And.LessThan(8f),
                "a render time inside the buffer must interpolate");
            Assert.That(interpolation.IsStale("p2"), Is.False);

            // Up to 100 ms past the newest tick extrapolates by velocity.
            var extrapolated = interpolation.Sample("p2",
                4.0 / 30.0 + 0.08);
            Assert.That(extrapolated.z, Is.GreaterThan(8f),
                "inside the 100 ms bound the pose extrapolates forward");
            Assert.That(interpolation.IsStale("p2"), Is.False);
            Assert.That(interpolation.ExtrapolationSeconds("p2"),
                Is.GreaterThan(0.0));

            // Past the bound the last pose holds and reports stale.
            var held = interpolation.Sample("p2", 4.0 / 30.0 + 0.5);
            Assert.That(held.z, Is.EqualTo(8f).Within(0.001f),
                "beyond the bound the last pose holds — no runaway");
            Assert.That(interpolation.IsStale("p2"), Is.True,
                "stale state must be exposed, not hidden");
            Assert.That(interpolation.SourceTick("p2"), Is.EqualTo(4));
        }

        [Test]
        public void InterpolationDropsReorderedPushAndBoundsBuffer()
        {
            var interpolation = new RemoteInterpolation();
            interpolation.Push(Snapshot(3, "race-1", Spawn(4), 0,
                RemoteRacer("p2", Pose(4, 0f, 8f))));
            interpolation.Push(Snapshot(2, "race-1", Spawn(2), 0,
                RemoteRacer("p2", Pose(2, 0f, -500f))));
            Assert.That(interpolation.BufferDepth("p2"), Is.EqualTo(1),
                "a reordered snapshot never rewinds the pose buffer");

            for (var i = 0; i < NetworkSettings.InterpolationBufferCapacity + 8; i++)
                interpolation.Push(Snapshot(4 + i, "race-1",
                    Spawn(4 + i * 2), 0,
                    RemoteRacer("p2", Pose(4 + i * 2, 0f, i))));
            Assert.That(interpolation.BufferDepth("p2"),
                Is.LessThanOrEqualTo(
                    NetworkSettings.InterpolationBufferCapacity),
                "the pose buffer is bounded");
        }

        // --- CorrectionSmoother (visual blend, separate from sim) --------

        [Test]
        public void CorrectionBlendDecaysToZeroWithinBoundAndSnapsImmediately()
        {
            var smoother = new CorrectionSmoother();
            var rendered = new MotorState { x = 0f, z = 0f, heading = 0f };
            var corrected = new MotorState { x = 1f, z = 0f, heading = 0f };

            // An ordinary error blends: the visual keeps its offset from
            // the corrected sim pose and decays it to zero inside the
            // shared blend bound.
            smoother.Correct(rendered, corrected, snap: false);
            var blended = smoother.Apply(corrected, 0.01);
            Assert.That(blended.x, Is.LessThan(corrected.x),
                "the visual trails the corrected sim while blending");
            var elapsed = 0.01;
            var pose = blended;
            while (elapsed < NetworkSettings.CorrectionBlendSeconds + 0.2)
            {
                pose = smoother.Apply(corrected, 0.02);
                elapsed += 0.02;
            }
            Assert.That(pose.x, Is.EqualTo(corrected.x).Within(0.001f),
                "the blend converges to the corrected sim pose");

            // A snap-class correction (ownership, recovery, >3 m) applies
            // instantly — no blend window at all.
            var far = new MotorState { x = 50f, z = 0f, heading = 0f };
            smoother.Correct(rendered, far, snap: true);
            var snapped = smoother.Apply(far, 0.001);
            Assert.That(snapped.x, Is.EqualTo(far.x).Within(0.0001f),
                "snap corrections never blend");
        }

        private static MotorState Pose(long tick, float x, float z)
        {
            return new MotorState
            {
                x = x, y = 0f, z = z, heading = 0f,
                grounded = true, tick = tick
            };
        }
    }
}
