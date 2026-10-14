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
    // Task 10: bounded client prediction for the PHYSICAL backend. The
    // client predicts only its own rigidbody vehicle inside a separate
    // collision-only PhysicsCourseWorld (PhysicsPredictionSimulation —
    // one body, never remote racers), restores the authoritative
    // body+controller state at the acknowledged boundary, and replays
    // the unacknowledged tail through the same force model + two
    // substeps while suppressing every replay side effect. PhysX replay
    // is approximate: convergence/corrections are judged, never bitwise
    // equality.
    public sealed class PhysicsPredictionPlayTests
    {
        private PhysicsCourseWorld hostWorld;
        private PhysicsCourseWorld predictWorld;
        private PhysicsVehicleSimulation hostSim;
        private PhysicsPredictionSimulation predictSim;
        private VehicleTuning tuning;
        private readonly List<PhysicsCourseWorld> extraWorlds =
            new List<PhysicsCourseWorld>();

        [TearDown]
        public void TearDown()
        {
            hostSim = null;
            predictSim = null;
            if (hostWorld != null) hostWorld.Dispose();
            if (predictWorld != null) predictWorld.Dispose();
            foreach (var world in extraWorlds)
                if (world != null) world.Dispose();
            hostWorld = null;
            predictWorld = null;
            extraWorlds.Clear();
            tuning = null;
        }

        private void NewSims()
        {
            hostWorld = PhysicsCourseWorld.Create("pred-host");
            PhysicsTestWorld.FlatGround(hostWorld, 0f, 60f, 200f, 300f);
            predictWorld = PhysicsCourseWorld.Create("pred-client");
            PhysicsTestWorld.FlatGround(predictWorld, 0f, 60f, 200f, 300f);
            hostSim = new PhysicsVehicleSimulation(hostWorld);
            predictSim = new PhysicsPredictionSimulation(predictWorld);
            tuning = PhysicsTestWorld.PhysicalTuning("car-01",
                VehiclePhysicsTuning.Car01());
            hostSim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = "p1",
                    tuning = tuning,
                    initialState = PhysicsTestWorld.PhysicalSpawnState(
                        tuning.physics,
                        new Vector3(0f,
                            PhysicsTestWorld.SpawnY(tuning.physics), 0f),
                        0f, 10f)
                }
            });
        }

        private static NetworkInputFrame Frame(long sequence, float steering)
        {
            return new NetworkInputFrame
            {
                sessionId = "sess-1",
                raceId = "race-1",
                participantId = "p1",
                ownershipEpoch = 1,
                inputSequence = sequence,
                clientTick = sequence,
                steering = steering
            };
        }

        private static NetworkSnapshot Snapshot(long sequence,
            MotorState localMotor, long ackSequence)
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = "sess-1",
                raceId = "race-1",
                raceOrdinal = 1,
                snapshotSequence = sequence,
                state = new NetworkRaceState
                {
                    sessionId = "sess-1",
                    raceId = "race-1",
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
            snapshot.acks.Add(new NetworkInputAck
            {
                participantId = "p1",
                inputSequence = ackSequence,
                clientTick = ackSequence,
                hostTick = snapshot.state.hostTick
            });
            return snapshot;
        }

        // One authoritative host tick for the local racer through the
        // mirror world — the same IVehicleSimulation seam the prediction
        // world replays through.
        private MotorState HostStep(DriverInput input)
        {
            var live = hostSim.Capture("p1");
            var results = hostSim.Step(new List<VehicleTickRequest>
            {
                new VehicleTickRequest
                {
                    participantId = "p1",
                    input = input,
                    rulesBefore = live,
                    simulateMotion = true
                }
            });
            foreach (var result in results)
                if (result.participantId == "p1")
                    return result.step.state;
            return live;
        }

        private static float Distance(MotorState a, MotorState b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float AttitudeDegrees(MotorState a, MotorState b)
        {
            if (a == null || b == null || a.physics == null
                || b.physics == null)
                return 0f;
            var pa = a.physics;
            var pb = b.physics;
            var dot = Mathf.Abs(pa.attitudeX * pb.attitudeX
                + pa.attitudeY * pb.attitudeY
                + pa.attitudeZ * pb.attitudeZ
                + pa.attitudeW * pb.attitudeW);
            return 2f * Mathf.Acos(Mathf.Min(1f, dot)) * Mathf.Rad2Deg;
        }

        // The authoritative restore must land the FULL physical state on
        // the prediction body — angular velocity and every controller
        // history — or the replay diverges the moment contact or
        // rotation matters. A yaw-only restore is the trap this pins.
        [Test]
        public void RestoreIncludesAngularAndControllerState()
        {
            NewSims();
            var prediction = new ClientPrediction("p1");
            prediction.SetSimulation(predictSim);
            // The host mirror drives a realistic state (moving, rolling
            // slightly under steering) so the restore carries honest
            // angular and controller content.
            for (var i = 0; i < 45; i++)
                HostStep(PhysicsTestWorld.Drive(0.3f));
            var authoritative = hostSim.Capture("p1");
            Assert.That(authoritative.physics, Is.Not.Null);
            // Seed reconcile establishes the race identity (the first
            // reconcile of a race always snaps), then one predicted
            // sample records the history entry the next ack measures —
            // an unmeasurable boundary snaps rather than running the
            // ordinary restore this test pins.
            prediction.Reconcile(Snapshot(1, authoritative, 0), tuning, null);
            prediction.Predict(Frame(1, 0.4f), tuning, null);
            // Mark the boundary with unmistakable angular/controller
            // content a yaw-only restore could never carry.
            authoritative.physics.angularVelocityY = 1.2f;
            authoritative.physics.angularVelocityX = 0.25f;
            authoritative.physics.filteredSteering = 0.21f;
            authoritative.physics.balancePrevError = -0.09f;
            authoritative.physics.filteredLeanTarget = 0.06f;
            authoritative.physics.filteredRollRate = 0.35f;
            authoritative.physics.driftAmount = 0.4f;
            var roll = Quaternion.Euler(14f, 25f, -9f);
            authoritative.physics.TrySetAttitude(roll.x, roll.y, roll.z,
                roll.w);
            var wheel = authoritative.physics.wheels[0];
            wheel.compression = 0.08f;
            wheel.previousCompression = 0.06f;
            wheel.contactValid = true;

            prediction.Reconcile(Snapshot(2, authoritative, 1), tuning, null);
            // The mirror must ride the same marked boundary — it owns a
            // copy, so mutation never touched its live body.
            hostSim.Restore("p1", authoritative);

            var restored = predictSim.Capture("p1");
            Assert.That(restored, Is.Not.Null,
                "the prediction world must hold the local body");
            Assert.That(restored.physics, Is.Not.Null);
            var p = restored.physics;
            Assert.That(p.angularVelocityY, Is.EqualTo(1.2f).Within(0.05f),
                "angular velocity must survive the authoritative restore");
            Assert.That(p.angularVelocityX, Is.EqualTo(0.25f).Within(0.05f));
            Assert.That(p.filteredSteering, Is.EqualTo(0.21f).Within(0.02f));
            Assert.That(p.balancePrevError, Is.EqualTo(-0.09f).Within(0.01f));
            Assert.That(p.filteredLeanTarget, Is.EqualTo(0.06f).Within(0.01f));
            Assert.That(p.filteredRollRate, Is.EqualTo(0.35f).Within(0.02f));
            Assert.That(p.driftAmount, Is.EqualTo(0.4f).Within(0.01f));
            Assert.That(AttitudeDegrees(restored, authoritative),
                Is.LessThan(1f),
                "the full attitude — roll and pitch, not just yaw — must "
                + "be restored");
            Assert.That(p.wheels[0].previousCompression,
                Is.EqualTo(0.06f).Within(1e-3f),
                "valid suspension damper history survives the restore");

            // The replayed tail converges with the host mirror: feed both
            // sides the same inputs from the same boundary and compare —
            // approximation tolerance, never bitwise equality.
            var input = PhysicsTestWorld.Drive(0.2f);
            MotorState predicted = null;
            MotorState hostMirror = null;
            for (var seq = 1; seq <= 6; seq++)
            {
                predicted = prediction.Predict(Frame(seq, 0.2f), tuning,
                    null);
                hostMirror = HostStep(input);
            }
            Assert.That(Distance(predicted, hostMirror), Is.LessThan(0.5f),
                "replayed prediction diverged from the mirrored host "
                + "world (" + Distance(predicted, hostMirror) + " m)");
            Assert.That(AttitudeDegrees(predicted, hostMirror),
                Is.LessThan(10f));
        }

        // Landing/contact transitions inside a replay produce the same
        // motor events the host would see — prediction consumes and
        // counts them and can never republish them as gameplay effects.
        [Test]
        public void ReplayNeverPublishesGameplayEvents()
        {
            NewSims();
            var prediction = new ClientPrediction("p1");
            prediction.SetSimulation(predictSim);
            // An airborne authoritative boundary 2.5 m up: gravity lands
            // the body inside the replayed window, so the suppressed
            // event counter is honest proof the callbacks ran.
            var authoritative = hostSim.Capture("p1");
            authoritative.y += 2.5f;
            authoritative.physics.velocityY = -2f;
            authoritative.grounded = false;
            authoritative.physics.grounded = false;
            authoritative.physics.airSeconds = 0.5f;
            authoritative.physics.airPeakY = authoritative.y;
            prediction.Reconcile(Snapshot(1, authoritative, 0), tuning,
                null);

            MotorState predicted = null;
            for (var seq = 1; seq <= 20; seq++)
                predicted = prediction.Predict(Frame(seq, 0f), tuning, null);
            Assert.That(prediction.SuppressedMotorEvents,
                Is.GreaterThanOrEqualTo(1),
                "the landing/contact callbacks inside replay ran and "
                + "were suppressed — prediction never publishes gameplay "
                + "events");
            Assert.That(predicted.recovering, Is.False,
                "a replayed landing can never mint a recovery boundary");
            Assert.That(predicted.y, Is.LessThan(authoritative.y),
                "the body kept falling — physics, not animation, moved it");
        }

        // The prediction world is structurally one-body: prediction can
        // never seat a second racer inside it — remote display vehicles
        // have no collision there by construction.
        [Test]
        public void PredictionWorldHoldsOnlyTheLocalBody()
        {
            predictWorld = PhysicsCourseWorld.Create("pred-only");
            predictSim = new PhysicsPredictionSimulation(predictWorld);
            var spawn = PhysicsTestWorld.PhysicalSpawnState(
                VehiclePhysicsTuning.Car01(), Vector3.up, 0f);
            var local = PhysicsTestWorld.PhysicalTuning("car-01",
                VehiclePhysicsTuning.Car01());
            var remote = PhysicsTestWorld.PhysicalTuning("car-02",
                VehiclePhysicsTuning.Car01());
            Assert.Throws<ArgumentException>(() =>
                predictSim.Initialize(new List<VehicleSpawn>
                {
                    new VehicleSpawn
                    {
                        participantId = "p1", tuning = local,
                        initialState = spawn
                    },
                    new VehicleSpawn
                    {
                        participantId = "p2", tuning = remote,
                        initialState = spawn
                    }
                }));
        }

        // --- full LAN measurement -------------------------------------------

        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

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
        private const float TimeoutSeconds = 12f;

        // The real LAN stack with physical worlds: the host runs every
        // racer through one PhysicsVehicleSimulation; the client predicts
        // its own body through a PhysicsPredictionSimulation over a
        // collision-only world. Both ride the same provider seams the
        // production app injects.
        private sealed class Rig : IDisposable
        {
            public readonly LanTransport Transport;
            public readonly LanDiscovery Discovery;
            public readonly LanSessionLink Link;
            public readonly LanSessionService Session;
            public readonly NetworkRaceBridge Bridge;
            public readonly List<PhysicsCourseWorld> Worlds =
                new List<PhysicsCourseWorld>();
            public double Now = 0.0;
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
                Bridge.SimulationProvider = content => NewHostSim();
                Bridge.PredictionSimulationProvider =
                    content => NewPredictionSim();
            }

            private IVehicleSimulation NewHostSim()
            {
                var world = PhysicsCourseWorld.Create("host-phys");
                PhysicsTestWorld.FlatGround(world, 0f, 500f, 300f, 1400f);
                Worlds.Add(world);
                return new PhysicsVehicleSimulation(world);
            }

            private IVehicleSimulation NewPredictionSim()
            {
                var world = PhysicsCourseWorld.Create("pred-phys");
                PhysicsTestWorld.FlatGround(world, 0f, 500f, 300f, 1400f);
                Worlds.Add(world);
                return new PhysicsPredictionSimulation(world);
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

            public int StepDue(DriverInput input)
            {
                var step = 1.0 / NetworkSettings.SimulationTicksPerSecond;
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
                foreach (var world in Worlds)
                    if (world != null) world.Dispose();
                Worlds.Clear();
            }
        }

        private static DrivingContentCatalog Catalog()
        {
            var course = new CourseDefinition
            {
                id = "phys-lan",
                spawnX = 0f,
                spawnZ = -12f,
                spawnHeading = 0f
            };
            course.checkpoints.Add(new CheckpointGate
            {
                id = "start", x = 0f, z = 0f, halfWidth = 8f,
                dirX = 0f, dirZ = 1f, recoverX = 0f, recoverZ = -3f
            });
            course.checkpoints.Add(new CheckpointGate
            {
                id = "gate-1", x = 0f, z = 900f, halfWidth = 8f,
                dirX = 0f, dirZ = 1f, recoverX = 0f, recoverZ = 800f
            });
            for (var i = 0; i < 4; i++)
                course.spawnSlots.Add(new MotorPose
                {
                    x = (i - 1.5f) * 4f,
                    z = -16f - (i % 2) * 4f
                });
            course.route.Add(new RouteWaypoint { x = 0f, z = -16f, radius = 4f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 0f, radius = 5f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 200f, radius = 6f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 500f, radius = 6f });
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -100f, minY = -5f, minZ = -100f,
                maxX = 100f, maxY = 30f, maxZ = 1200f
            });
            course.safePoses.Add(new MotorPose { x = 0f, z = 8f });
            course.safePoses.Add(new MotorPose { x = -6f, z = 8f });
            var evt = new EventDefinition
            {
                id = "phys-lan",
                courseId = "phys-lan",
                laps = 1,
                countdownTicks = 8,
                // Generous recovery timers keep the measurement window
                // free of respawns — ordinary corrections are measured,
                // not the snap path.
                recovery = new RecoverySettings
                {
                    offCourseSeconds = 60f,
                    minCourseY = -5f,
                    stuckSpeed = 0.75f,
                    stuckSeconds = 60f,
                    beachedSeconds = 60f,
                    overturnedSeconds = 60f,
                    outsideBoundsSeconds = 60f
                },
                vehicleIds = new List<string> { "car-01" },
                completionCoins = 10
            };
            var vehicles = new List<VehicleTuning>
            {
                PhysicsTestWorld.PhysicalTuning("car-01",
                    VehiclePhysicsTuning.Car01())
            };
            return DrivingContentCatalog.Create("physics-lan-1", vehicles,
                new[] { course }, new[] { evt },
                VehicleSimulationBackend.PhysicalAssisted);
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, "phys-prediction-play",
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
                var link = rig.Bridge.LinkState();
                diag += " | " + rig.Session.Role + "/" + s.phase
                    + " terr=" + rig.Transport.LastError
                    + " lastErr=" + rig.Bridge.LastError
                    + " gpSeen=" + link.gameplayPacketsSeen
                    + " snapPub=" + link.snapshotsPublished
                    + " staleSnap=" + link.staleSnapshots
                    + " inRej=" + link.inputRejections
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

        private static IEnumerator RaceToRunning(Rig host, Rig client,
            bool realtime)
        {
            var hosted = host.Session.Execute(Cmd("lan.host",
                P("port", "0")));
            Assert.That(hosted.status,
                Is.EqualTo(CommandResult.Applied), "lan.host failed");
            Assert.That(host.Transport.BoundPort, Is.GreaterThan(0));
            var joined = client.Session.Execute(Cmd("lan.join",
                P("address", "127.0.0.1",
                  "port", host.Transport.BoundPort.ToString(
                      System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(joined.status,
                Is.EqualTo(CommandResult.Accepted), "lan.join rejected");

            yield return PumpUntil(() =>
                client.Session.Snapshot().phase
                    == LanSessionService.PhaseLobby
                && Find(host.Session.Snapshot(), "p-guest") != null,
                host, client);

            Assert.That(host.Session.Execute(Cmd("lan.configure",
                P("eventId", "phys-lan", "classId", "car"))).status,
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
            Assert.That(host.Session.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var steer = new DriverInput(0.4f, false, false, false,
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
                            && racer.session.phase == "running"
                            && racer.session.motor != null
                            && racer.session.motor.physics != null;
                return false;
            }, host, client);
        }

        private static double Percentile(List<float> samples, double p)
        {
            if (samples == null || samples.Count == 0) return 0.0;
            var sorted = new List<float>(samples);
            sorted.Sort();
            var index = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Count) index = sorted.Count - 1;
            return sorted[index];
        }

        // Drive the client through the measurement loop: alternating
        // steering while samples are in flight is what turns latency
        // into genuine corrections. Returns after `ticks` host ticks.
        private static IEnumerator MeasureCorrections(Rig host, Rig client,
            int ticks, bool realtime, string tag)
        {
            var startTick = host.Bridge.LatestState != null
                ? host.Bridge.LatestState.hostTick : 0;
            var distances = new List<float>();
            var degrees = new List<float>();
            int pendingMax = 0, replayMax = 0;
            long correctionsSeen = 0, snapsSeen = 0, resyncsSeen = 0;
            string lastReason = "";
            bool? activeSeen = null;
            var deadline = Time.unscaledTime + TimeoutSeconds * 2;
            while (Time.unscaledTime < deadline
                && (host.Bridge.LatestState == null
                    || host.Bridge.LatestState.hostTick - startTick
                        < ticks))
            {
                var i = (int)(host.Bridge.LatestState != null
                    ? host.Bridge.LatestState.hostTick - startTick : 0);
                var steering = (i / 30) % 2 == 0 ? 0.85f : -0.85f;
                var clientInput = new DriverInput(steering, false,
                    false, false, false, false, false);
                host.PumpFrame();
                client.PumpFrame();
                if (realtime)
                {
                    host.StepDue(Neutral);
                    client.StepDue(clientInput);
                }
                else
                {
                    host.Bridge.StepTick(Neutral, World);
                    client.Bridge.StepTick(clientInput, World);
                }
                client.Bridge.SmoothedLocalMotor(client.Now);
                var facts = client.Bridge.Prediction;
                if (facts != null)
                {
                    pendingMax = Math.Max(pendingMax,
                        facts.pendingInputCount);
                    replayMax = Math.Max(replayMax,
                        facts.lastReplayCount);
                    if (facts.lastErrorMeasured)
                    {
                        distances.Add(facts.lastErrorDistance);
                        degrees.Add(facts.lastErrorDegrees);
                    }
                    correctionsSeen = facts.corrections;
                    snapsSeen = facts.snaps;
                    resyncsSeen = facts.resyncCount;
                    lastReason = facts.resyncReason;
                    activeSeen = facts.active;
                }
                yield return null;
            }
            var p95d = Percentile(distances, 95);
            var p95a = Percentile(degrees, 95);
            var maxd = distances.Count > 0
                ? distances[distances.Count - 1] : 0f;
            foreach (var d in distances) maxd = Math.Max(maxd, d);
            var maxa = 0f;
            foreach (var a in degrees) maxa = Math.Max(maxa, a);
            Debug.Log("[T10-MEASURE] " + tag
                + " samples=" + distances.Count
                + " p95dist=" + p95d.ToString("0.000") + "m"
                + " maxDist=" + maxd.ToString("0.000") + "m"
                + " p95deg=" + p95a.ToString("0.00")
                + " maxDeg=" + maxa.ToString("0.00")
                + " pendingMax=" + pendingMax
                + " replayMax=" + replayMax
                + " corrections=" + correctionsSeen
                + " snaps=" + snapsSeen
                + " resyncs=" + resyncsSeen
                + " lastResyncReason=" + lastReason
                + " active=" + (activeSeen.HasValue
                    ? activeSeen.Value.ToString() : "n/a"));
            Assert.That(pendingMax,
                Is.LessThanOrEqualTo(NetworkSettings.PredictionInputCapacity),
                tag + ": pending backlog exceeded the 120-input bound");
            Assert.That(replayMax,
                Is.LessThanOrEqualTo(
                    NetworkSettings.PredictionReplayTicksPerFrame),
                tag + ": a reconcile replayed past the 12-tick frame "
                + "budget");
            Assert.That(maxd,
                Is.LessThan(NetworkSettings.CorrectionSnapMeters),
                tag + ": correction exceeded the 3 m snap bound");
            Assert.That(maxa,
                Is.LessThan(NetworkSettings.CorrectionSnapDegrees),
                tag + ": orientation correction exceeded the 45-degree "
                + "snap bound");
        }

        // Ordinary LAN: no impairment — the honest baseline for the
        // provisional p95 targets (<=0.25 m / <=5 deg no-contact).
        [UnityTest]
        public IEnumerator OrdinaryLanPhysicalCorrections()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return RaceToRunning(host, client, realtime: false);
                yield return MeasureCorrections(host, client, 240,
                    realtime: false, tag: "ordinary");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // Impaired LAN: deterministic 100 ms RTT, ±30 ms jitter, 2% loss
        // on both directions through the real UTP simulator stage — the
        // provisional target is <=0.75 m / <=12 deg.
        [UnityTest]
        public IEnumerator ImpairedLanPhysicalCorrections()
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
                yield return MeasureCorrections(host, client, 240,
                    realtime: true, tag: "impaired");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }
    }
}
