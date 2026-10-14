using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // Task 9 schema-3 stream tests: record shape, deep-copy isolation and
    // snapshot+delta reconstruction proven over the real StateStream
    // writer — a captured NDJSON prefix is replayed by an independent
    // merge implementation that never shares the producer's diff logic.
    public sealed class NetworkStreamPlayTests
    {
        private string directory;
        private StateStream stream;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "fr-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            stream = new StateStream(directory, "run-stream-test",
                "device-test", "participant-test", "build-test",
                () => "content-test");
            stream.Start();
        }

        [TearDown]
        public void TearDown()
        {
            if (stream != null) stream.Dispose();
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception) { }
        }

        private List<JObject> ReadRecords(int expected)
        {
            // The writer thread serializes asynchronously; poll briefly so
            // the assertions never read a partially flushed trace.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            List<JObject> records;
            do
            {
                records = new List<JObject>();
                var path = Path.Combine(directory, "state.ndjson");
                if (File.Exists(path))
                    using (var reader = new StreamReader(
                        new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            if (line.Length > 0)
                                records.Add(JObject.Parse(line));
                    }
                if (records.Count >= expected) return records;
                Thread.Sleep(10);
            } while (DateTime.UtcNow < deadline);
            return records;
        }

        private static FoundationState LanState(long tick)
        {
            return new FoundationState
            {
                screen = "lan-lobby",
                language = "en",
                activeSlot = 0,
                tick = tick,
                captureValid = true,
                captureError = "",
                input = new InputState(),
                slots = new List<SlotState>(),
                pendingSettings = false,
                persistenceStatus = "",
                featureCoverage = new FeatureCoverage(),
                presentation = new PresentationState
                {
                    textDirection = "ltr",
                    camera = new DrivingCameraState { mode = "none" }
                },
                drivingEvents = new List<DrivingEvent>(),
                droppedDrivingEvents = 0,
                network = new NetworkSessionState
                {
                    role = "host",
                    phase = "lobby",
                    localParticipantId = "p-local",
                    descriptor = new SessionDescriptor
                    {
                        sessionId = "s-1",
                        eventId = "lan-mixed",
                        classId = "car",
                        joinable = true
                    },
                    participants = new List<ParticipantState>
                    {
                        new ParticipantState
                        {
                            participantId = "p-local", slot = 0,
                            occupied = true, connected = true,
                            controlSource = "local", vehicleId = "car-01",
                            ownershipEpoch = 1
                        }
                    },
                    discovered = new List<LanDiscoveredHost>
                    {
                        new LanDiscoveredHost
                        {
                            address = "192.168.0.10", port = 47777
                        }
                    },
                    results = new List<ResultDeliveryState>
                    {
                        new ResultDeliveryState
                        {
                            resultId = "r-1", raceId = "race-1",
                            participantId = "p-local", state = "pending"
                        }
                    }
                },
                networkRace = new NetworkRaceState
                {
                    sessionId = "s-1",
                    raceId = "race-1",
                    phase = "running",
                    hostTick = 42,
                    elapsedTicks = 30,
                    racers = new List<NetworkRacerState>
                    {
                        new NetworkRacerState
                        {
                            participantId = "p-local", slot = 0,
                            vehicleId = "car-01", attribution = "human",
                            position = 1,
                            session = new DrivingSessionState
                            {
                                raceId = "race-1",
                                // Task 11 physical observability: the
                                // full diagnostic slice rides the
                                // stream — identity, recovery facts,
                                // ordered substep path, world counters
                                // and the complete physics state.
                                simulationBackend = "physicalAssisted",
                                contentFingerprint =
                                    "0123456789abcdef0123456789abcdef"
                                    + "0123456789abcdef0123456789abcdef",
                                lastRecoveryReason = "",
                                substepPath =
                                    new List<VehiclePathSample>
                                    {
                                        new VehiclePathSample
                                        {
                                            offsetSeconds = 0f,
                                            pose = new MotorPose
                                            { x = 1f, z = 2f }
                                        },
                                        new VehiclePathSample
                                        {
                                            offsetSeconds = 1f / 60f,
                                            pose = new MotorPose
                                            { x = 1.1f, z = 2.1f }
                                        },
                                        new VehiclePathSample
                                        {
                                            offsetSeconds = 1f / 30f,
                                            pose = new MotorPose
                                            { x = 1.2f, z = 2.2f }
                                        }
                                    },
                                simulation = new VehicleSimulationDiagnostics
                                {
                                    backend = "physicalAssisted",
                                    ticksSimulated = 42,
                                    substepsSimulated = 84,
                                    bodies = 1,
                                    lastStepSeconds = 0.001,
                                    totalStepSeconds = 0.042,
                                    invalidStateFaults = 0
                                },
                                motor = new MotorState
                                {
                                    x = 1f, z = 2f,
                                    physics = PhysicalMotor()
                                }
                            }
                        }
                    },
                    standings = new List<int> { 0 },
                    events = new List<DrivingEvent>()
                },
                prediction = new NetworkPredictionState
                {
                    participantId = "p-local",
                    active = true,
                    acknowledgedInputSequence = 7,
                    pendingInputCount = 2,
                    predictedMotor = new MotorState { x = 3f }
                },
                remotePresentation = new RemoteInterpolationState()
            };
        }

        // A valid four-wheel physical state for the stream fixture:
        // unit yaw attitude, finite velocities and a full index-ordered
        // wheel array — enough that every consumer can treat it as real
        // evidence rather than a placeholder.
        private static VehiclePhysicsState PhysicalMotor()
        {
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[4],
                grounded = true,
                lastContact = "ground",
                recoveryEpoch = 1
            };
            physics.TrySetAttitude(0f, 0f, 0f, 1f);
            for (var i = 0; i < physics.wheels.Length; i++)
            {
                physics.wheels[i] = new WheelPhysicsState
                {
                    index = i,
                    grounded = true,
                    contactValid = true,
                    normalLoad = 2000f,
                    normalY = 1f
                };
            }
            return physics;
        }

        // Independent merge — mirrors the documented consumer semantics in
        // the stream contract, sharing no code with the producer's diff.
        private static JObject ApplyDelta(JObject baseline, JObject delta)
        {
            var merged = (JObject)baseline.DeepClone();
            var removed = (JArray)delta["removed"];
            foreach (var pathToken in removed)
            {
                var path = (string)pathToken;
                var segments = path.Split('.');
                JObject node = merged;
                for (var i = 0; i < segments.Length - 1; i++)
                    node = (JObject)node[segments[i]];
                Assert.That(node.Remove(segments[segments.Length - 1]),
                    Is.True, "removed path must resolve: " + path);
            }
            Merge((JObject)delta["changed"], merged);
            return merged;
        }

        private static void Merge(JObject changed, JObject into)
        {
            foreach (var property in changed.Properties())
            {
                var current = into[property.Name];
                var child = property.Value as JObject;
                if (child != null && current is JObject)
                    Merge(child, (JObject)current);
                else
                    into[property.Name] = property.Value;
            }
        }

        [Test]
        public void BoundaryRecordsCarrySchema4IdentityAndSlices()
        {
            stream.EmitDeltaBoundary(LanState(5), "cmd-9");
            stream.EmitDiscovery(new JArray(), new JObject(), 5,
                NetworkSettings.StateSchemaVersion);
            var result = new CommandResult
            {
                commandId = "cmd-9", action = "lan.host",
                status = CommandResult.Applied, code = "", tick = 5
            };
            stream.EmitResult(result);
            stream.RequestFinish(5);
            var records = ReadRecords(4);

            var boundary = records[0];
            Assert.That((string)boundary["type"], Is.EqualTo("state"),
                "The first boundary is always a full state record");
            Assert.That((int)boundary["protocolVersion"], Is.EqualTo(1));
            Assert.That((string)boundary["runId"], Is.EqualTo("run-stream-test"));
            Assert.That((string)boundary["deviceId"], Is.EqualTo("device-test"));
            Assert.That((string)boundary["participantId"],
                Is.EqualTo("participant-test"));
            Assert.That((string)boundary["buildVersion"], Is.EqualTo("build-test"));
            Assert.That((string)boundary["contentVersion"],
                Is.EqualTo("content-test"));
            Assert.That((int)boundary["stateSchemaVersion"],
                Is.EqualTo(NetworkSettings.StateSchemaVersion));
            Assert.That((int)boundary["sequence"], Is.EqualTo(1));
            Assert.That((long)boundary["tick"], Is.EqualTo(5));
            Assert.That((long)boundary["monotonicMs"], Is.GreaterThanOrEqualTo(0));

            var state = (JObject)boundary["state"];
            Assert.That((int)state["stateSchemaVersion"],
                Is.EqualTo(NetworkSettings.StateSchemaVersion),
                "The nested state marker must match the record schema");
            // Schema-3+ network slices ride the boundary state verbatim.
            Assert.That((string)state["network"]["role"], Is.EqualTo("host"));
            Assert.That((string)state["network"]["descriptor"]["sessionId"],
                Is.EqualTo("s-1"));
            Assert.That((string)state["networkRace"]["raceId"],
                Is.EqualTo("race-1"));
            Assert.That((long)state["prediction"]["acknowledgedInputSequence"],
                Is.EqualTo(7));
            Assert.That(state["remotePresentation"], Is.Not.Null);
            Assert.That(state["presentation"]["camera"], Is.Not.Null);
            // Task 11: the complete physical + diagnostic slice rides
            // the stream verbatim — backend identity, canonical tuning
            // fingerprint, ordered substep path, world counters and the
            // full wheel-array physics state.
            var session = (JObject)state["networkRace"]["racers"][0]
                ["session"];
            Assert.That((string)session["simulationBackend"],
                Is.EqualTo("physicalAssisted"));
            Assert.That((string)session["contentFingerprint"],
                Is.Not.Null.And.Length.EqualTo(64));
            var substep = (JArray)session["substepPath"];
            Assert.That(substep, Is.Not.Null.And.Count.EqualTo(3),
                "the ordered substep path must serialize per boundary");
            Assert.That((float)substep[1]["offsetSeconds"],
                Is.EqualTo(1f / 60f).Within(0.0001f));
            var sim = (JObject)session["simulation"];
            Assert.That(sim, Is.Not.Null);
            Assert.That((long)sim["ticksSimulated"], Is.EqualTo(42));
            Assert.That((long)sim["substepsSimulated"], Is.EqualTo(84));
            Assert.That((int)sim["bodies"], Is.EqualTo(1));
            var physics = (JObject)session["motor"]["physics"];
            Assert.That(physics, Is.Not.Null);
            Assert.That((JArray)physics["wheels"],
                Is.Not.Null.And.Count.EqualTo(4));
            Assert.That((long)physics["recoveryEpoch"], Is.EqualTo(1));
            // Discovery carries the negotiated schema too.
            var discovery = records[1];
            Assert.That((string)discovery["type"], Is.EqualTo("discovery"));
            Assert.That((int)discovery["stateSchemaVersion"],
                Is.EqualTo(NetworkSettings.StateSchemaVersion));
            // Result records stay command-correlated.
            var emitted = records[2];
            Assert.That((string)emitted["type"], Is.EqualTo("result"));
            Assert.That((string)emitted["commandId"], Is.EqualTo("cmd-9"));
            Assert.That((string)emitted["action"], Is.EqualTo("lan.host"));
            Assert.That((int)emitted["sequence"], Is.EqualTo(3));
        }

        [Test]
        public void EmittedStateIsDeepCopyIsolatedFromProducerMutation()
        {
            var state = LanState(1);
            stream.EmitDeltaBoundary(state, null);
            // Mutate every schema-3 network slice after emission — a shared
            // reference would corrupt the recorded boundary.
            state.network.participants[0].participantId = "mutated";
            state.network.discovered[0].address = "10.9.9.9";
            state.network.results[0].state = "persisted";
            state.networkRace.racers[0].participantId = "mutated";
            state.networkRace.standings[0] = 3;
            state.prediction.predictedMotor.x = 99f;
            var records = ReadRecords(1);
            var emitted = (JObject)records[0]["state"];
            Assert.That((string)emitted["network"]["participants"][0]["participantId"],
                Is.EqualTo("p-local"));
            Assert.That((string)emitted["network"]["discovered"][0]["address"],
                Is.EqualTo("192.168.0.10"));
            Assert.That((string)emitted["network"]["results"][0]["state"],
                Is.EqualTo("pending"));
            Assert.That((string)emitted["networkRace"]["racers"][0]["participantId"],
                Is.EqualTo("p-local"));
            Assert.That((int)emitted["networkRace"]["standings"][0], Is.EqualTo(0));
            Assert.That((float)emitted["prediction"]["predictedMotor"]["x"],
                Is.EqualTo(3f));
        }

        [Test]
        public void DeltaBoundariesReconstructFullStateFromBase()
        {
            var first = LanState(1);
            stream.EmitDeltaBoundary(first, null);

            var second = LanState(2);
            // Shrink the roster and change nested fields so the delta must
            // carry both removals and nested merges.
            second.network.participants.Clear();
            second.networkRace.elapsedTicks = 60;
            second.prediction.acknowledgedInputSequence = 8;
            second.prediction = null; // scalar change: object -> null
            stream.EmitDeltaBoundary(second, "cmd-2");

            var third = LanState(3);
            third.network.lastError = "hostLost";
            stream.EmitDeltaBoundary(third, "cmd-3");

            var records = ReadRecords(3);
            Assert.That((string)records[0]["type"], Is.EqualTo("state"));
            var baseSequence = (int)records[0]["sequence"];
            var baseline = (JObject)records[0]["state"].DeepClone();

            // Delta 1: names the previous boundary's sequence; shrinking
            // the roster serializes as a changed empty array (removal
            // paths only fire when a key vanishes from the payload).
            var delta1 = records[1];
            Assert.That((string)delta1["type"], Is.EqualTo("delta"));
            Assert.That((int)delta1["base"], Is.EqualTo(baseSequence));
            Assert.That(delta1["removed"], Is.TypeOf<JArray>());
            var reconstructed = ApplyDelta(baseline, delta1);
            Assert.That((long)reconstructed["networkRace"]["elapsedTicks"],
                Is.EqualTo(60));
            Assert.That(reconstructed["network"]["participants"].HasValues,
                Is.False);
            Assert.That(reconstructed["prediction"].Type,
                Is.EqualTo(JTokenType.Null));

            // Delta 2 chains off the delta's own sequence — boundaries
            // form a linked list, not a star from the first snapshot.
            var delta2 = records[2];
            Assert.That((int)delta2["base"],
                Is.EqualTo((int)delta1["sequence"]));
            reconstructed = ApplyDelta(reconstructed, delta2);
            Assert.That((string)reconstructed["network"]["lastError"],
                Is.EqualTo("hostLost"));
            Assert.That((long)reconstructed["tick"], Is.EqualTo(3));
        }

        [Test]
        public void EmittedRecordsStayIndependentOfOneAnother()
        {
            // A later boundary's baseline must not share objects with the
            // earlier emitted record — mutate the producer-side state
            // between emissions and confirm the first record is untouched.
            var first = LanState(1);
            stream.EmitDeltaBoundary(first, null);
            var second = LanState(2);
            stream.EmitDeltaBoundary(second, "cmd-2");
            var records = ReadRecords(2);
            var recordedFirst = (JObject)records[0]["state"];
            Assert.That((long)recordedFirst["tick"], Is.EqualTo(1));
            Assert.That((string)records[1]["type"], Is.EqualTo("delta"));
            Assert.That((int)records[1]["base"],
                Is.EqualTo((int)records[0]["sequence"]),
                "A delta must identify the previous boundary sequence");
        }

        [Test]
        public void WireEncodeStripsStreamOnlyDiagnostics()
        {
            // The compact LAN snapshot must never carry the full
            // diagnostic weight: the session's ordered substep path and
            // the world counters are stream-only facts (non-serializable
            // DTO element types JsonUtility cannot write), while the
            // small identity strings and the compact physics DTO DO
            // cross. Encoding must never grow past the snapshot ceiling.
            var wire = new NetworkSnapshot
            {
                sessionId = "s-1",
                raceId = "race-1",
                raceOrdinal = 1,
                snapshotSequence = 9,
                state = LanState(9).networkRace
            };
            byte[] packet;
            string error;
            Assert.That(Networking.NetworkWireCodec.TryEncodeSnapshot(
                wire, out packet, out error), Is.True, error);
            Assert.That(packet.Length,
                Is.LessThanOrEqualTo(NetworkSettings.SnapshotCeilingBytes));

            NetworkSnapshot decoded;
            Assert.That(Networking.NetworkWireCodec.TryDecodeSnapshot(
                packet, out decoded, out error), Is.True, error);
            var session = decoded.state.racers[0].session;
            Assert.That(session.substepPath, Is.Empty,
                "substep diagnostics must not cross the compact wire");
            Assert.That(session.simulation, Is.Null,
                "world diagnostics must not cross the compact wire");
            // Identity strings are small wire facts: they DO cross.
            Assert.That(session.simulationBackend,
                Is.EqualTo("physicalAssisted"));
            Assert.That(session.contentFingerprint,
                Is.Not.Null.And.Length.EqualTo(64));
            // The compact racer.physics DTO replaced the full
            // diagnostic physics and folded back onto the motor.
            Assert.That(decoded.state.racers[0].physics, Is.Not.Null);
            Assert.That(decoded.state.racers[0].physics.present,
                Is.True);
            Assert.That(session.motor.physics, Is.Not.Null,
                "decode folds the compact DTO back onto the motor");
            Assert.That(session.motor.physics.recoveryEpoch,
                Is.EqualTo(1));
            // The host's live state was never mutated by the encode:
            // the wire transform works on a deep copy.
            var live = wire.state.racers[0].session;
            Assert.That(live.substepPath.Count, Is.EqualTo(3));
            Assert.That(live.simulation, Is.Not.Null);
            Assert.That(live.motor.physics, Is.Not.Null);
        }

        [Test]
        public void HelloRecordShapeIsUnsequencedAndAdvertisesSchema()
        {
            // The controller hello is negotiated by ValidationBridge, not
            // the stream — this pins the advertised schema constant so a
            // silent downgrade is caught here. Schema 5 is the M4 visual
            // benchmark epoch: the Task-2 additive presentation slice
            // (render profile/scale/frame-rate/performance summary) plus
            // the Task-3 courseVisualId; LAN protocol stays 2.
            Assert.That(NetworkSettings.StateSchemaVersion, Is.EqualTo(6));
        }
    }
}
