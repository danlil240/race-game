using System;
using System.Collections;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Task 6 prediction/interpolation over the real two-rig transport
    // path — the same full stack as the Task-5 harness (real UDP sockets,
    // session protocol, replication bridge) with the local motor
    // predicted on the client and remote racers sampled from the
    // interpolation buffer. One leg also pins the controlled-impairment
    // seam: the UTP simulator stage puts a deterministic 100 ms RTT,
    // ±30 ms jitter, 2% loss link on both directions — no fake
    // transport, the impairment lives inside the real pipelines.
    public sealed class NetworkPredictionPlayTests
    {
        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

        // The same minimal world the replication harness simulates on:
        // flat ground, nothing to collide with — the client predicts
        // through the identical step the host runs.
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
        private static readonly DriverInput Neutral =
            new DriverInput(0f, false, false, false, false, false, false);
        private const float TimeoutSeconds = 10f;

        private sealed class Rig : IDisposable
        {
            public readonly LanTransport Transport;
            public readonly LanDiscovery Discovery;
            public readonly LanSessionLink Link;
            public readonly LanSessionService Session;
            public readonly NetworkRaceBridge Bridge;
            // Session wall time starts at zero and tracks REAL elapsed
            // time between pumps — the UTP simulator stage delays real
            // packets, so the session deadlines must see real seconds or
            // the virtual clock would outrun an impaired handshake and
            // time out joins that are actually still in flight.
            public double Now = 0.0;
            // Sim-time accumulator: real elapsed seconds are owed to the
            // fixed 30 Hz step — StepDue drains it so an impaired wait
            // advances the race by real tick count, not pump count.
            public double TickAccum;
            private double lastPumpReal = -1;

            public Rig(string participantId, ILanNetworkAccess access,
                LanImpairment impairment = null)
            {
                Transport = new LanTransport(access, impairment);
                Discovery = new LanDiscovery(access);
                Link = new LanSessionLink(Transport, Discovery);
                Session = new LanSessionService(Link, Link, "gameplay-test");
                Link.Bind(Session);
                Session.SetLocalContext(participantId, "fox", 0, true, true);
                Session.SetContent(Catalog());
                Bridge = new NetworkRaceBridge(Transport, Session);
                Bridge.SetContent(Catalog());
                Bridge.NowSeconds = () => Now;
            }

            public void PumpFrame()
            {
                var real = Time.realtimeSinceStartupAsDouble;
                var dt = lastPumpReal < 0 ? 0.033
                    : Math.Max(0.0, Math.Min(0.5, real - lastPumpReal));
                lastPumpReal = real;
                Now += dt;
                TickAccum += dt;
                Link.Pump(DateTime.UtcNow);
                Session.AdvanceWallTime(Now);
                Bridge.Pump();
            }

            // Step the rig's 30 Hz boundary for every real 33 ms owed —
            // the only honest cadence while packets sit in a delayed
            // queue. Returns the number of ticks stepped this pump.
            public int StepDue(DriverInput input)
            {
                var step = 1.0
                    / NetworkSettings.SimulationTicksPerSecond;
                var stepped = 0;
                while (TickAccum >= step)
                {
                    TickAccum -= step;
                    Bridge.StepTick(input, World);
                    stepped++;
                }
                return stepped;
            }

            public void Dispose()
            {
                Bridge.Dispose();
                Link.Dispose();
            }
        }

        private static DrivingContentCatalog Catalog()
        {
            var course = new CourseDefinition
            {
                id = "course-lan",
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f
                    },
                    new CheckpointGate
                    {
                        id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1500f
                    }
                },
                spawnSlots = new List<MotorPose>
                {
                    new MotorPose { x = -6f, z = 0f },
                    new MotorPose { x = -2f, z = 0f },
                    new MotorPose { x = 2f, z = 0f },
                    new MotorPose { x = 6f, z = 0f }
                }
            };
            var mixed = new EventDefinition
            {
                id = "lan-mixed",
                courseId = "course-lan",
                laps = 1,
                countdownTicks = 8,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            return DrivingContentCatalog.Create("lan-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course },
                new[] { mixed });
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, "lan-prediction-play",
                Guid.NewGuid().ToString("N"), "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private static IEnumerator PumpUntil(Func<bool> done,
            params Rig[] rigs)
        {
            var until = Time.unscaledTime + TimeoutSeconds;
            while (Time.unscaledTime < until && !done())
            {
                foreach (var rig in rigs) rig.PumpFrame();
                yield return null;
            }
            var diag = "";
            foreach (var rig in rigs)
            {
                if (rig == null) continue;
                var s = rig.Session.Snapshot();
                diag += " | " + rig.Session.Role + "/" + s.phase
                    + " terr=" + rig.Transport.LastError
                    + " lastErr=" + rig.Bridge.LastError
                    + " drops=" + rig.Bridge.DroppedPackets
                    + " race=" + (rig.Bridge.LatestState != null
                        ? rig.Bridge.LatestState.phase : "none");
            }
            Assert.That(done(), Is.True,
                "Timed out waiting for LAN condition." + diag);
        }

        private static ParticipantState Find(NetworkSessionState state,
            string participantId)
        {
            foreach (var p in state.participants)
                if (p.participantId == participantId) return p;
            return null;
        }

        // One simulated boundary: frame pump on both rigs, then one 30 Hz
        // StepTick each — the client ticks through the same world so its
        // prediction steps the identical motor the host simulates.
        private static void Tick(Rig host, Rig client,
            DriverInput hostInput, DriverInput clientInput)
        {
            host.PumpFrame();
            client.PumpFrame();
            host.Bridge.StepTick(hostInput, World);
            client.Bridge.StepTick(clientInput, World);
        }

        // realtime=false: each pumped frame steps one 30 Hz boundary —
        // fine on a clean link where waits are a handful of frames.
        // realtime=true: ticks are owed by the real-time accumulator, so
        // a delayed handshake does not burn thousands of sim ticks while
        // the test waits for packets.
        private static IEnumerator StartTwoPlayerRace(Rig host, Rig client,
            bool realtime = false)
        {
            var hosted = host.Session.Execute(Cmd("lan.host", P("port", "0")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied),
                "lan.host failed: " + hosted.code);
            Assert.That(host.Transport.BoundPort, Is.GreaterThan(0));

            var joined = client.Session.Execute(Cmd("lan.join",
                P("address", "127.0.0.1",
                  "port", host.Transport.BoundPort.ToString(
                      System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(joined.status, Is.EqualTo(CommandResult.Accepted),
                "lan.join rejected: " + joined.code);

            yield return PumpUntil(() =>
                client.Session.Snapshot().phase == LanSessionService.PhaseLobby
                && Find(host.Session.Snapshot(), "p-guest") != null,
                host, client);

            Assert.That(host.Session.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Session.Execute(Cmd("lan.ready",
                P("ready", "true"))).status, Is.EqualTo(CommandResult.Applied));
            Assert.That(client.Session.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.Not.EqualTo(CommandResult.Rejected));
            yield return PumpUntil(() =>
                Find(host.Session.Snapshot(), "p-guest")?.ready == true,
                host, client);
            Assert.That(host.Session.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));

            yield return PumpUntil(() =>
            {
                if (realtime)
                {
                    host.StepDue(Neutral);
                    client.StepDue(Neutral);
                }
                else
                {
                    host.Bridge.StepTick(Neutral, World);
                    client.Bridge.StepTick(Neutral, World);
                }
                return host.Bridge.LatestState != null
                    && client.Bridge.LatestState != null;
            }, host, client);
        }

        private static IEnumerator RaceToRunning(Rig host, Rig client,
            bool realtime = false)
        {
            yield return StartTwoPlayerRace(host, client, realtime);
            var steer = new DriverInput(0.5f, false, false, false,
                false, false, false);
            yield return PumpUntil(() =>
            {
                if (realtime)
                {
                    host.StepDue(Neutral);
                    client.StepDue(steer);
                }
                else
                {
                    host.Bridge.StepTick(Neutral, World);
                    client.Bridge.StepTick(steer, World);
                }
                var state = client.Bridge.LatestState;
                if (state == null) return false;
                foreach (var racer in state.racers)
                    if (racer.participantId == "p-guest")
                        return racer.session != null
                            && racer.session.phase == "running";
                return false;
            }, host, client);
        }

        private static NetworkRacerState RacerFor(NetworkRaceState state,
            string participantId)
        {
            if (state == null) return null;
            foreach (var racer in state.racers)
                if (racer.participantId == participantId) return racer;
            return null;
        }

        // Prediction must move the local motor before the host can have
        // seen the sample: after the race is running, a steering hold
        // changes the predicted pose while the delivered ack still lags
        // behind the input stream.
        [UnityTest]
        public IEnumerator LocalPredictionRespondsBeforeRoundTrip()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return RaceToRunning(host, client);
                var steer = new DriverInput(1f, false, false, false,
                    false, false, false);
                var ackBefore = client.Bridge.AckFor("p-guest");
                var ackSeqBefore = ackBefore != null
                    ? ackBefore.inputSequence : 0;

                // A few local ticks of fresh input: the predicted pose
                // moves immediately while the newest ack stays behind.
                MotorState predicted = null;
                var unackedSeen = -1;
                for (var i = 0; i < 6; i++)
                {
                    Tick(host, client, Neutral, steer);
                    predicted = client.Bridge.PredictedMotor;
                    var tickFacts = client.Bridge.Prediction;
                    if (predicted != null && tickFacts != null)
                        unackedSeen = Math.Max(unackedSeen,
                            tickFacts.pendingInputCount);
                    if (i % 2 == 0) yield return null;
                }
                Assert.That(predicted, Is.Not.Null,
                    "prediction never produced a motor state");
                // The visual seam renders the LIVE predicted pose plus
                // the decaying correction offset — it must exist, stay
                // within a correction's reach of the simulated pose,
                // and advance between snapshots: two consecutive local
                // ticks with no packet pump in between cannot deliver a
                // reconcile, so any motion here is live prediction.
                var smoothed = client.Bridge.SmoothedLocalMotor(
                    client.Now);
                Assert.That(smoothed, Is.Not.Null,
                    "no smoothed local pose for the render seam");
                var sdx = smoothed.x - predicted.x;
                var sdz = smoothed.z - predicted.z;
                Assert.That(Mathf.Sqrt(sdx * sdx + sdz * sdz),
                    Is.LessThanOrEqualTo(
                        NetworkSettings.CorrectionSnapMeters));
                client.Bridge.StepTick(steer, World);
                client.Bridge.StepTick(steer, World);
                var smoothedAfter = client.Bridge.SmoothedLocalMotor(
                    client.Now + 0.066);
                Assert.That(smoothedAfter, Is.Not.Null);
                var moveX = smoothedAfter.x - smoothed.x;
                var moveZ = smoothedAfter.z - smoothed.z;
                Assert.That(Mathf.Sqrt(moveX * moveX + moveZ * moveZ),
                    Is.GreaterThan(0.0001f),
                    "smoothed pose froze between snapshot accepts — it "
                    + "must follow the live prediction every tick");
                var facts = client.Bridge.Prediction;
                Assert.That(facts, Is.Not.Null);
                Assert.That(facts.predictedMotor, Is.Not.Null);
                Assert.That(unackedSeen, Is.GreaterThan(0),
                    "the predicted pose led the acknowledged input — "
                    + "local feedback precedes the round trip");

                // The delivered authoritative racer still exists and the
                // predicted pose stays honest: once enough ticks pass for
                // acks to land, divergence stays bounded (deterministic
                // flat-world sim — corrections stay near zero).
                for (var i = 0; i < 20; i++)
                {
                    Tick(host, client, Neutral, steer);
                    if (i % 4 == 0) yield return null;
                }
                var latest = client.Bridge.LatestState;
                var localRacer = RacerFor(latest, "p-guest");
                Assert.That(localRacer, Is.Not.Null);
                var settled = client.Bridge.PredictedMotor;
                var dx = settled.x - localRacer.session.motor.x;
                var dz = settled.z - localRacer.session.motor.z;
                var drift = Mathf.Sqrt(dx * dx + dz * dz);
                Assert.That(drift, Is.LessThan(3f),
                    "predicted pose must stay near the delivered "
                    + "authoritative pose (drift " + drift + " m)");
                Assert.That(facts.pendingInputCount,
                    Is.LessThanOrEqualTo(
                        NetworkSettings.PredictionInputCapacity));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // The published `prediction` slice is contract-gated to a live
        // LAN race: a client creates its predictor on the first session
        // tick — while still connecting or sitting in the lobby — but
        // the slice must stay null until the first authoritative
        // snapshot confirms a race, matching `networkRace`. A live
        // scenario caught the slice leaking an all-null-facts object
        // during `connecting`, violating the stream contract that
        // prediction exists only on a client inside a LAN race.
        [UnityTest]
        public IEnumerator PredictionSliceStaysNullUntilFirstSnapshot()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                var hosted = host.Session.Execute(
                    Cmd("lan.host", P("port", "0")));
                Assert.That(hosted.status,
                    Is.EqualTo(CommandResult.Applied));
                var joined = client.Session.Execute(Cmd("lan.join",
                    P("address", "127.0.0.1",
                      "port", host.Transport.BoundPort.ToString(
                          System.Globalization.CultureInfo
                              .InvariantCulture))));
                Assert.That(joined.status,
                    Is.EqualTo(CommandResult.Accepted));

                // Through connecting and the settled lobby the client
                // ticks every boundary — its predictor object exists —
                // but no snapshot has confirmed a race, so the slice
                // stays unpublished.
                yield return PumpUntil(() =>
                    client.Session.Snapshot().phase
                        == LanSessionService.PhaseLobby,
                    host, client);
                for (var i = 0; i < 6; i++)
                {
                    client.Bridge.StepTick(Neutral, World);
                    Assert.That(client.Bridge.Prediction, Is.Null,
                        "prediction slice leaked before any LAN race "
                        + "existed (lobby/connecting tick " + i + ")");
                    Assert.That(client.Bridge.LatestState, Is.Null);
                }

                // Once the race is confirmed the slice publishes again —
                // continue from the lobby this test already established.
                Assert.That(host.Session.Execute(Cmd("lan.configure",
                    P("eventId", "lan-mixed", "classId", "car"))).status,
                    Is.EqualTo(CommandResult.Applied));
                Assert.That(host.Session.Execute(Cmd("lan.ready",
                    P("ready", "true"))).status,
                    Is.EqualTo(CommandResult.Applied));
                Assert.That(client.Session.Execute(Cmd("lan.ready",
                    P("ready", "true"))).status,
                    Is.Not.EqualTo(CommandResult.Rejected));
                yield return PumpUntil(() =>
                    Find(host.Session.Snapshot(), "p-guest")?.ready == true,
                    host, client);
                Assert.That(host.Session.Execute(Cmd("lan.start", null))
                    .status, Is.EqualTo(CommandResult.Applied));
                yield return PumpUntil(() =>
                {
                    host.Bridge.StepTick(Neutral, World);
                    client.Bridge.StepTick(Neutral, World);
                    return host.Bridge.LatestState != null
                        && client.Bridge.LatestState != null;
                }, host, client);
                Assert.That(client.Bridge.Prediction, Is.Not.Null,
                    "prediction slice missing inside a live LAN race");
                Assert.That(client.Bridge.LatestState, Is.Not.Null);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // When snapshots stop arriving the remote pose extrapolates at
        // most 100 ms, then holds and reports stale — measured against
        // the deterministic rig clock.
        [UnityTest]
        public IEnumerator RemotePoseHoldsAndFlagsStaleWhenSnapshotsStop()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return RaceToRunning(host, client);
                var last = client.Bridge.SampleRemotePose("p-host",
                    client.Now);
                Assert.That(last, Is.Not.Null,
                    "no buffered remote pose for the host racer");
                Assert.That(client.Bridge.RenderHostTime(client.Now),
                    Is.GreaterThan(0));

                // Stop the host entirely: no more snapshots can arrive.
                // Advance the client render clock past the 100 ms
                // extrapolation bound — the pose must hold and turn
                // stale. The clock tracks real elapsed time, so pump
                // until the lane reports stale or a generous real-time
                // deadline passes.
                MotorState held = null;
                RemoteInterpolationEntry entry = null;
                var deadline = Time.unscaledTime + 4f;
                while (entry == null && Time.unscaledTime < deadline)
                {
                    client.PumpFrame();
                    held = client.Bridge.SampleRemotePose("p-host",
                        client.Now);
                    foreach (var e in client.Bridge.RemotePresentation
                        .racers)
                        if (e.participantId == "p-host" && e.stale)
                            entry = e;
                    yield return null;
                }
                Assert.That(held, Is.Not.Null);
                Assert.That(held.x, Is.EqualTo(last.x).Within(0.001f));
                Assert.That(held.z, Is.EqualTo(last.z).Within(0.001f));
                Assert.That(entry, Is.Not.Null,
                    "remote presentation must carry the host racer lane");
                Assert.That(entry.stale, Is.True,
                    "a held pose past the 100 ms bound reports stale");
                Assert.That(entry.bufferCount,
                    Is.LessThanOrEqualTo(
                        NetworkSettings.InterpolationBufferCapacity));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // The controlled-impairment exercise over the real wire: UTP's
        // simulator stage gives each direction a deterministic 50 ms
        // delay with ±30 ms jitter and 2% loss — a 100 ms RTT link —
        // while input/snapshot traffic rides the real sequenced and
        // reliable pipelines. Measurements are logged for the report;
        // assertions bound the behavior, they do not grade "feel".
        [UnityTest]
        public IEnumerator ImpairedLinkExercisesPredictionAndInterpolation()
        {
            var impairHost = new LanImpairment
            {
                DelayMilliseconds = 50,
                JitterMilliseconds = 30,
                DropPercentage = 2,
                RandomSeed = 11,
                Mode = ApplyMode.SentPacketsOnly
            };
            var impairClient = new LanImpairment
            {
                DelayMilliseconds = 50,
                JitterMilliseconds = 30,
                DropPercentage = 2,
                RandomSeed = 22,
                Mode = ApplyMode.SentPacketsOnly
            };
            var host = new Rig("p-host", new StubNetworkAccess(),
                impairHost);
            var client = new Rig("p-guest", new StubNetworkAccess(),
                impairClient);
            try
            {
                yield return RaceToRunning(host, client, realtime: true);

                // ~8 s of sim with the client alternating steering:
                // direction flips while samples are in flight are what a
                // delayed link turns into genuine corrections. Host and
                // client step at the real 30 Hz cadence — the impairment
                // is real-time, so the tick budget must be too.
                var startTick = host.Bridge.LatestState != null
                    ? host.Bridge.LatestState.hostTick : 0;
                int pendingMax = 0, pendingSum = 0, pendingSamples = 0;
                int staleSamples = 0, remoteSamples = 0;
                int bufferMax = 0, bufferMin = int.MaxValue;
                float errorMax = 0f, errorSum = 0f;
                int errorSamples = 0;
                long ackLagMax = 0;
                var lastSeenPhase = "";
                // ~8 s of sim is ~8 s of real time at the honest 30 Hz
                // cadence; the deadline gives the impaired link slack.
                var deadline = Time.unscaledTime + 20f;
                while (Time.unscaledTime < deadline
                    && (host.Bridge.LatestState == null
                        || host.Bridge.LatestState.hostTick
                            - startTick < 240))
                {
                    var i = (int)(host.Bridge.LatestState != null
                        ? host.Bridge.LatestState.hostTick - startTick : 0);
                    var steering = (i / 30) % 2 == 0 ? 0.9f : -0.9f;
                    var clientInput = new DriverInput(steering, false,
                        false, false, false, false, false);
                    host.PumpFrame();
                    client.PumpFrame();
                    host.StepDue(Neutral);
                    client.StepDue(clientInput);
                    // The render seam rides every iteration too — the
                    // smoothed pose exists whenever prediction is live.
                    client.Bridge.SmoothedLocalMotor(client.Now);
                    var facts = client.Bridge.Prediction;
                    if (facts != null)
                    {
                        pendingMax = Math.Max(pendingMax,
                            facts.pendingInputCount);
                        pendingSum += facts.pendingInputCount;
                        pendingSamples++;
                        if (facts.lastErrorMeasured)
                        {
                            errorMax = Math.Max(errorMax,
                                facts.lastErrorDistance);
                            errorSum += facts.lastErrorDistance;
                            errorSamples++;
                        }
                        ackLagMax = Math.Max(ackLagMax,
                            facts.pendingInputCount);
                    }
                    var remote = client.Bridge.SampleRemotePose("p-host",
                        client.Now);
                    if (remote != null)
                    {
                        remoteSamples++;
                        var lane = client.Bridge.RemotePresentation;
                        foreach (var e in lane.racers)
                        {
                            if (e.participantId != "p-host") continue;
                            bufferMax = Math.Max(bufferMax, e.bufferCount);
                            bufferMin = Math.Min(bufferMin, e.bufferCount);
                            if (e.stale) staleSamples++;
                        }
                    }
                    yield return null;
                    var live = client.Bridge.LatestState;
                    if (live != null && live.phase != lastSeenPhase)
                    {
                        lastSeenPhase = live.phase;
                        var roster = "";
                        foreach (var r in live.racers)
                            roster += " [" + r.participantId + " fin="
                                + r.finished + " dnf=" + r.dnf
                                + " sess=" + (r.session != null
                                    ? r.session.phase : "-")
                                + " z=" + (r.session != null
                                    ? r.session.motor.z.ToString("0.0")
                                    : "-") + "]";
                        Debug.Log("[T6-DBG] tick " + i + " phase="
                            + live.phase + " hostTick=" + live.hostTick
                            + roster);
                    }
                }

                var factsNow = client.Bridge.Prediction;
                var corrections = factsNow != null ? factsNow.corrections : 0;
                var snaps = factsNow != null ? factsNow.snaps : 0;
                var suppressed = factsNow != null
                    ? factsNow.suppressedMotorEvents : 0;
                Debug.Log("[T6-MEASURE] impaired run: pendingMax="
                    + pendingMax + " pendingMean="
                    + (pendingSamples > 0
                        ? (pendingSum / (double)pendingSamples)
                            .ToString("0.00") : "n/a")
                    + " ackLagMax=" + ackLagMax
                    + " corrections=" + corrections + " snaps=" + snaps
                    + " errorMax=" + errorMax.ToString("0.000")
                    + " errorMean=" + (errorSamples > 0
                        ? (errorSum / errorSamples).ToString("0.000")
                        : "n/a")
                    + " staleSamples=" + staleSamples + "/" + remoteSamples
                    + " buffer=[" + bufferMin + ".." + bufferMax + "]"
                    + " suppressedMotorEvents=" + suppressed);

                Assert.That(client.Bridge.LatestState, Is.Not.Null,
                    "the impaired link dropped the race entirely");
                Assert.That(client.Bridge.LatestState.phase,
                    Is.EqualTo(NetworkRace.PhaseRunning),
                    "race must still be running under impairment");
                Assert.That(pendingMax,
                    Is.LessThanOrEqualTo(
                        NetworkSettings.PredictionInputCapacity),
                    "pending buffer exceeded the replay bound");
                Assert.That(bufferMax,
                    Is.LessThanOrEqualTo(
                        NetworkSettings.InterpolationBufferCapacity));
                var predicted = client.Bridge.PredictedMotor;
                Assert.That(predicted, Is.Not.Null);
                // The honest divergence bound lives at the ack horizon:
                // every reconcile restores authoritative and replays the
                // unacked tail, so the measured correction is the only
                // drift that can exist — it must stay under the snap
                // threshold. (Predicted-versus-stale-snapshot distance is
                // not drift; it is prediction doing its job.)
                Assert.That(errorMax,
                    Is.LessThan(NetworkSettings.CorrectionSnapMeters),
                    "measured ack-horizon correction exceeded the snap "
                    + "bound — held-input divergence ran away");
                Assert.That(remoteSamples, Is.GreaterThan(0),
                    "no remote pose was ever sampled");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }
    }
}
