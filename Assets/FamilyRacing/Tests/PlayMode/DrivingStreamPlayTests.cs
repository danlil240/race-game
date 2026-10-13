using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Consumer-side snapshot+delta reconstructor independent of the
    // producer's diff logic: snapshot/state records replace the baseline,
    // delta records must name the applied boundary sequence in `base` and
    // carry a recursive `changed` merge object plus dot-path `removed`
    // entries. A delta is never read as a full state; corrupt bases or
    // unresolvable paths fail the test. Shared by every PlayMode consumer.
    internal sealed class StreamReconstructor
    {
        private JObject baseline;
        private long baselineSequence = -1;

        internal long? BaseSequence
        {
            get { return baselineSequence < 0 ? (long?)null : baselineSequence; }
        }

        // Apply one sequenced record; returns the reconstructed boundary
        // state (a fresh copy) or null for non-boundary records.
        internal JObject Apply(JObject record)
        {
            var type = (string)record["type"];
            if (type == "snapshot" || type == "state")
            {
                var state = record["state"] as JObject;
                Assert.That(state, Is.Not.Null, "Boundary record without a full state");
                baseline = (JObject)state.DeepClone();
                baselineSequence = (long)record["sequence"];
                return (JObject)baseline.DeepClone();
            }
            if (type == "delta")
            {
                Assert.That(baseline, Is.Not.Null, "Delta arrived before any baseline");
                Assert.That(record["base"] != null
                    && record["base"].Type == JTokenType.Integer
                    && (long)record["base"] == baselineSequence, Is.True,
                    "Delta base does not name the applied boundary");
                var changed = record["changed"] as JObject;
                var removed = record["removed"] as JArray;
                Assert.That(changed, Is.Not.Null, "Malformed delta changed payload");
                Assert.That(removed, Is.Not.Null, "Malformed delta removed payload");
                var merged = (JObject)baseline.DeepClone();
                foreach (var pathToken in removed)
                    RemovePath(merged, (string)pathToken);
                MergeChanged(merged, changed);
                baseline = merged;
                baselineSequence = (long)record["sequence"];
                return (JObject)baseline.DeepClone();
            }
            return null;
        }

        private static void MergeChanged(JObject state, JObject changed)
        {
            foreach (var property in changed.Properties())
            {
                var current = state[property.Name] as JObject;
                var incoming = property.Value as JObject;
                if (current != null && incoming != null)
                {
                    MergeChanged(current, incoming);
                }
                else
                {
                    state[property.Name] = property.Value;
                }
            }
        }

        private static void RemovePath(JObject state, string path)
        {
            Assert.That(!string.IsNullOrEmpty(path), "Removed path must be non-empty");
            var segments = path.Split('.');
            JObject node = state;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                node = node[segments[i]] as JObject;
                Assert.That(node, Is.Not.Null,
                    "Removed path crosses a non-object: " + path);
            }
            Assert.That(node[segments[segments.Length - 1]] != null, Is.True,
                "Removed path does not resolve: " + path);
            node.Remove(segments[segments.Length - 1]);
        }
    }

    // Schema-2 driving observation over the real wire: ordered delta
    // boundaries reconstruct the complete published state at every tick —
    // including ticks where nothing changed — plus sequenced screenshot
    // rejections, honest dropped-event marking, and overload telemetry.
    public sealed class DrivingStreamPlayTests
    {
        private sealed class Client : IDisposable
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            internal readonly StreamReconstructor Reconstructor =
                new StreamReconstructor();

            public Client(int port)
            {
                client = new TcpClient();
                client.Connect("127.0.0.1", port);
                client.ReceiveTimeout = 5000;
                stream = client.GetStream();
            }

            public void Send(JObject frame)
            {
                var bytes = Encoding.UTF8.GetBytes(
                    frame.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            public JObject Read()
            {
                var buffer = new MemoryStream();
                int b;
                while ((b = stream.ReadByte()) >= 0 && b != '\n')
                    buffer.WriteByte((byte)b);
                if (buffer.Length == 0 && b < 0) return null;
                return JObject.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            }

            // Read until the next boundary record and reconstruct it.
            public JObject[] ReadBoundary()
            {
                for (var i = 0; i < 64; i++)
                {
                    var record = Read();
                    Assert.That(record, Is.Not.Null, "Stream closed before a boundary");
                    var state = Reconstructor.Apply(record);
                    if (state != null) return new[] { record, state };
                }
                Assert.Fail("No boundary record within 64 records");
                return null;
            }

            public void Dispose() { try { client.Close(); } catch { } }
        }

        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance, x = pose.x, y = 0f, z = pose.z,
                    normalY = 1f, distance = pose.y, surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
                float halfWidth, float halfLength, float height)
            {
                return new SweepHit { hit = false, fraction = 1f };
            }
        }

        private string directory;
        private string profilesDirectory;
        private ProfileStore store;
        private FoundationService service;
        private ValidationBridge bridge;
        private JObject connection;
        private readonly FlatWorld world = new FlatWorld();

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "fr-driving-stream-" + Guid.NewGuid().ToString("N"));
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-profiles-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(profilesDirectory, new UnityProfileSerializer());
            service = new FoundationService(store, "en", "run-test");
            service.SetDrivingContent(TestCatalog());
        }

        private void StartBridge()
        {
            bridge = new ValidationBridge(service, "run-test", directory, command =>
            {
                var result = service.Execute(command);
                bridge.EmitCommandBoundary(result);
                return result;
            }, null);
            connection = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "connection.json")));
        }

        private Client Connect()
        {
            return new Client((int)connection["port"]);
        }

        private void Hello(Client client)
        {
            var hello = new JObject();
            hello["type"] = "hello";
            hello["protocolVersion"] = 1;
            hello["runId"] = (string)connection["runId"];
            hello["token"] = (string)connection["token"];
            client.Send(hello);
        }

        private IEnumerator PumpSeconds(float seconds)
        {
            var until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until)
            {
                bridge.Pump();
                yield return null;
            }
        }

        private void Tick(int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                service.StepDriving(world);
                service.AdvanceTick();
                bridge.EmitTick();
            }
        }

        [UnityTest]
        public IEnumerator EveryBoundaryReconstructsPublishedState()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                Assert.That((string)client.Read()["type"], Is.EqualTo("hello"));
                yield return PumpSeconds(0.3f);

                var seenDelta = false;
                // Attach snapshot + a mix of tick and command boundaries: at
                // every single boundary the independent reconstruction must
                // equal the producer's own serialization byte-for-byte.
                var pair = client.ReadBoundary();
                Assert.That((string)pair[0]["type"], Is.EqualTo("snapshot"));
                AssertEquivalent(pair[1]);

                for (var i = 0; i < 3; i++)
                {
                    Tick();
                    pair = client.ReadBoundary();
                    if ((string)pair[0]["type"] == "delta") seenDelta = true;
                    AssertEquivalent(pair[1]);
                }

                client.Send(Command("cmd-create", "profile.create",
                    ("slot", "0"), ("language", "en")));
                yield return PumpSeconds(0.4f);
                pair = client.ReadBoundary();
                Assert.That((string)pair[0]["type"], Is.EqualTo("delta"));
                Assert.That((string)pair[0]["commandId"], Is.EqualTo("cmd-create"));
                AssertEquivalent(pair[1]);
                var result = client.Read();
                Assert.That((string)result["type"], Is.EqualTo("result"));

                Assert.That(seenDelta, Is.True,
                    "Tick boundaries must travel as ordered deltas");
            }
        }

        [UnityTest]
        public IEnumerator UnchangedTickStillEmitsDeltaBoundary()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.ReadBoundary(); // attach snapshot

                Tick();
                var first = client.ReadBoundary();
                Assert.That((string)first[0]["type"], Is.EqualTo("delta"));
                Assert.That((long)first[0]["base"],
                    Is.EqualTo((long)first[0]["sequence"] - 1));

                // A second emission with no state change is still a real
                // boundary: the delta carries the new sequence and the
                // prior boundary as its base, possibly empty changes.
                bridge.EmitTick();
                var second = client.ReadBoundary();
                Assert.That((string)second[0]["type"], Is.EqualTo("delta"));
                Assert.That((long)second[0]["base"],
                    Is.EqualTo((long)first[0]["sequence"]));
                AssertEquivalent(second[1]);
            }
        }

        [UnityTest]
        public IEnumerator ExplicitSnapshotResyncsDeltaBase()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.ReadBoundary();
                Tick(2);
                client.ReadBoundary();
                client.ReadBoundary();

                var snapshot = new JObject();
                snapshot["type"] = "snapshot";
                client.Send(snapshot);
                yield return PumpSeconds(0.4f);
                var pair = client.ReadBoundary();
                Assert.That((string)pair[0]["type"], Is.EqualTo("snapshot"));
                AssertEquivalent(pair[1]);

                Tick();
                var next = client.ReadBoundary();
                Assert.That((string)next[0]["type"], Is.EqualTo("delta"));
                Assert.That((long)next[0]["base"],
                    Is.EqualTo((long)pair[0]["sequence"]),
                    "The delta after a resnapshot must chain off the new baseline");
            }
        }

        [UnityTest]
        public IEnumerator DrivingBoundariesCarryReconstructableDrivingSlice()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.ReadBoundary();

                client.Send(Command("cmd-create", "profile.create",
                    ("slot", "0"), ("language", "en")));
                yield return PumpSeconds(0.4f);
                client.ReadBoundary();
                client.Read(); // result

                client.Send(Command("cmd-race", "race.start",
                    ("vehicleId", "car-01"), ("eventId", "intro-test")));
                yield return PumpSeconds(0.4f);
                var pair = client.ReadBoundary();
                AssertEquivalent(pair[1]);
                var driving = pair[1]["driving"];
                Assert.That(driving, Is.Not.Null);
                Assert.That((string)driving["vehicleId"], Is.EqualTo("car-01"));
                Assert.That((string)driving["raceId"], Is.Not.Null.And.Not.Empty);
                Assert.That((string)driving["phase"], Is.EqualTo("countdown"));
                Assert.That(pair[1]["drivingEvents"], Is.Not.Null);
                Assert.That(pair[1]["droppedDrivingEvents"], Is.Not.Null);
                client.Read(); // result

                // Simulated ticks advance the reconstructed simulation clock.
                // Only the latest emitted boundary may be compared against
                // the live snapshot — earlier queued boundaries honestly
                // describe older ticks.
                long firstSim = (long)driving["simulationTick"];
                Tick(3);
                JObject[] last = null;
                for (var i = 0; i < 3; i++)
                {
                    last = client.ReadBoundary();
                    Assert.That((long)last[1]["driving"]["simulationTick"],
                        Is.EqualTo(firstSim + i + 1));
                }
                AssertEquivalent(last[1]);
                var events = pair[1]["drivingEvents"] as JArray;
                Assert.That(events, Is.Not.Null.And.Count.GreaterThanOrEqualTo(1));
                foreach (var ev in events)
                    Assert.That((string)ev["id"], Is.Not.Null.And.Not.Empty,
                        "Driving events must carry stable ids");
            }
        }

        [UnityTest]
        public IEnumerator ScreenshotRejectionsAreSequencedAndTraced()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.ReadBoundary();

                var bad = new JObject();
                bad["type"] = "screenshot";
                bad["captureId"] = "../escape";
                client.Send(bad);
                yield return PumpSeconds(0.4f);
                var rejected = ReadOfType(client, "screenshot");
                Assert.That((string)rejected["status"], Is.EqualTo("rejected"));
                Assert.That((string)rejected["code"], Is.EqualTo("badScreenshot"));
                Assert.That(rejected["sequence"], Is.Not.Null,
                    "Screenshot rejection must be a sequenced record");

                // Four pending captures is the bound: the fifth request
                // must refuse with a sequenced queueFull outcome — emitted
                // as soon as its frame is pumped, before any pending
                // capture resolves on its own deadline.
                for (var i = 0; i < 5; i++)
                {
                    var shot = new JObject();
                    shot["type"] = "screenshot";
                    shot["captureId"] = "shot-" + i;
                    client.Send(shot);
                }
                yield return PumpSeconds(0.4f);
                var overflow = ReadOfType(client, "screenshot");
                Assert.That((string)overflow["status"], Is.EqualTo("rejected"));
                Assert.That((string)overflow["code"], Is.EqualTo("screenshotQueueFull"));
                Assert.That((string)overflow["captureId"], Is.EqualTo("shot-4"));

                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
                yield return PumpSeconds(0.6f);
            }
            bridge.Dispose();
            bridge = null;

            // Rejections are durable capture evidence, not socket-only errors.
            var trace = File.ReadAllLines(Path.Combine(directory, "state.ndjson"));
            var sawRejected = false;
            foreach (var line in trace)
            {
                var record = JObject.Parse(line);
                if ((string)record["type"] == "screenshot"
                    && (string)record["status"] == "rejected")
                    sawRejected = true;
            }
            Assert.That(sawRejected, Is.True,
                "Rejected screenshot requests must persist in the trace");
        }

        [UnityTest]
        public IEnumerator BoundedEventSlicePublishesDropMarker()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.ReadBoundary();

                client.Send(Command("cmd-create", "profile.create",
                    ("slot", "0"), ("language", "en")));
                yield return PumpSeconds(0.4f);
                client.ReadBoundary();
                client.Read();

                client.Send(Command("cmd-race", "race.start",
                    ("vehicleId", "car-01"), ("eventId", "intro-test")));
                yield return PumpSeconds(0.4f);
                client.ReadBoundary();
                client.Read();

                // Every restart stages race.ended + race.started events on
                // the same boundary; enough of them must overflow the
                // bounded slice and move the durable drop marker. All 80
                // commands fit the inbound bound, so one pump drains them.
                for (var i = 0; i < 80; i++)
                    client.Send(Command("cmd-r" + i, "race.restart"));
                yield return PumpSeconds(1.0f);
                for (var i = 0; i < 80; i++)
                {
                    client.ReadBoundary();
                    var result = client.Read();
                    Assert.That((string)result["type"], Is.EqualTo("result"));
                }
                Tick();
                var pair = client.ReadBoundary();
                AssertEquivalent(pair[1]);
                var dropped = (int)pair[1]["droppedDrivingEvents"];
                var slice = pair[1]["drivingEvents"] as JArray;
                Assert.That(dropped, Is.GreaterThan(0),
                    "Evicted driving events must move the durable drop marker");
                Assert.That(slice.Count, Is.LessThanOrEqualTo(128));
                foreach (var ev in slice)
                    Assert.That((string)ev["id"], Is.Not.Null.And.Not.Empty);
            }
        }

        // Task 11 physical observability, end-to-end over the real
        // stream: a PhysicalAssisted session stepped through a genuine
        // PhysicsVehicleSimulation publishes the full diagnostic slice
        // on every boundary — motor physics with the ordered wheel
        // array, the three-sample substep path, backend/fingerprint
        // identity, recovery bookkeeping and live world counters — and
        // the independent reconstruction still equals the producer's
        // own serialization field for field.
        [UnityTest]
        public IEnumerator PhysicalDiagnosticsReachTheStreamBoundary()
        {
            var physicsWorld = PhysicsCourseWorld.Create("StreamPhysics");
            try
            {
                PhysicsTestWorld.FlatGround(physicsWorld, 0f, 0f,
                    400f, 400f);
                service.SetDrivingContent(PhysicalCatalog());
                service.SetDrivingSimulationFactory(content =>
                    new PhysicsVehicleSimulation(physicsWorld));
                StartBridge();
                using (var client = Connect())
                {
                    Hello(client);
                    client.Read(); // hello
                    yield return PumpSeconds(0.3f);
                    client.ReadBoundary(); // attach snapshot

                    client.Send(Command("cmd-create", "profile.create",
                        ("slot", "0"), ("language", "en")));
                    yield return PumpSeconds(0.4f);
                    client.ReadBoundary();
                    client.Read(); // result

                    client.Send(Command("cmd-race", "race.start",
                        ("vehicleId", "car-physics-01"),
                        ("eventId", "phys-test")));
                    yield return PumpSeconds(0.4f);
                    var pair = client.ReadBoundary();
                    AssertEquivalent(pair[1]);
                    client.Read(); // result

                    var driving = pair[1]["driving"];
                    Assert.That((string)driving["simulationBackend"],
                        Is.EqualTo("physicalAssisted"),
                        "the session must name its real backend");
                    var fingerprint =
                        (string)driving["contentFingerprint"];
                    Assert.That(fingerprint,
                        Is.Not.Null.And.Length.EqualTo(64),
                        "the canonical tuning fingerprint must be a "
                        + "64-hex token");
                    Assert.That((string)driving["lastRecoveryReason"],
                        Is.EqualTo(""), "no recovery has run yet");

                    // Past the countdown plus a few running boundaries:
                    // physics, path and diagnostics must all be live.
                    Tick(6);
                    for (var i = 0; i < 6; i++)
                        pair = client.ReadBoundary();
                    AssertEquivalent(pair[1]);
                    driving = pair[1]["driving"];
                    var motor = (JObject)driving["motor"];
                    var physics = (JObject)motor["physics"];
                    Assert.That(physics, Is.Not.Null,
                        "physical backend must publish physics state");
                    var wheels = (JArray)physics["wheels"];
                    Assert.That(wheels,
                        Is.Not.Null.And.Count.EqualTo(4));
                    for (var w = 0; w < wheels.Count; w++)
                    {
                        Assert.That((int)wheels[w]["index"],
                            Is.EqualTo(w),
                            "wheel array must stay index-ordered");
                        Assert.That(wheels[w]["grounded"].Type,
                            Is.EqualTo(JTokenType.Boolean));
                        Assert.That(wheels[w]["contactValid"].Type,
                            Is.EqualTo(JTokenType.Boolean));
                        Assert.That((float)wheels[w]["normalLoad"],
                            Is.GreaterThanOrEqualTo(0f));
                    }

                    // Ordered substep evidence: exactly three samples at
                    // the fixed 0 / 1/60 / 1/30 offsets, the terminal
                    // sample matching the committed envelope pose.
                    var path = (JArray)driving["substepPath"];
                    Assert.That(path, Is.Not.Null.And.Count.EqualTo(3),
                        "every boundary must carry the ordered "
                        + "substep path");
                    var offsets = new[] { 0f, 1f / 60f, 1f / 30f };
                    for (var s = 0; s < 3; s++)
                        Assert.That((float)path[s]["offsetSeconds"],
                            Is.EqualTo(offsets[s]).Within(0.001f));
                    Assert.That((float)path[2]["pose"]["x"],
                        Is.EqualTo((float)motor["x"]).Within(0.05f));
                    Assert.That((float)path[2]["pose"]["z"],
                        Is.EqualTo((float)motor["z"]).Within(0.05f));

                    // Live world counters: one body, two substeps per
                    // tick, real non-negative cost, no refused states.
                    var sim = (JObject)driving["simulation"];
                    Assert.That(sim, Is.Not.Null,
                        "the physical world must publish diagnostics");
                    Assert.That((string)sim["backend"],
                        Is.EqualTo("physicalAssisted"));
                    Assert.That((long)sim["ticksSimulated"],
                        Is.GreaterThanOrEqualTo(6));
                    Assert.That((long)sim["substepsSimulated"],
                        Is.EqualTo((long)sim["ticksSimulated"] * 2));
                    Assert.That((int)sim["bodies"], Is.EqualTo(1));
                    Assert.That((double)sim["totalStepSeconds"],
                        Is.GreaterThanOrEqualTo(0));
                    Assert.That((double)sim["lastStepSeconds"],
                        Is.GreaterThanOrEqualTo(0));
                    Assert.That((long)sim["invalidStateFaults"],
                        Is.EqualTo(0));

                    // The current-course regression: the legacy envelope
                    // still agrees with the physics body's own velocity.
                    var vx = (float)physics["velocityX"];
                    var vz = (float)physics["velocityZ"];
                    var forward = vx * Math.Sin((float)motor["heading"])
                        + vz * Math.Cos((float)motor["heading"]);
                    Assert.That(Math.Abs(forward - (float)motor["speed"]),
                        Is.LessThan(0.5f),
                        "legacy envelope diverged from physics velocity");
                }
            }
            finally
            {
                physicsWorld.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator OverloadTelemetryReachesBoundaryState()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.ReadBoundary();

                var presentation = new PresentationState
                {
                    backlogSteps = 3,
                    stepOverloads = 7,
                    textDirection = "ltr"
                };
                service.SetPresentation(presentation);
                Tick();
                var pair = client.ReadBoundary();
                AssertEquivalent(pair[1]);
                Assert.That((int)pair[1]["presentation"]["backlogSteps"], Is.EqualTo(3));
                Assert.That((long)pair[1]["presentation"]["stepOverloads"], Is.EqualTo(7));
            }
        }

        // The independent reconstruction must equal the producer's own
        // serialization of the live snapshot — field for field. Both sides
        // pass through the same JSON round-trip so CLR float JValues are
        // compared in the wire representation the consumer actually sees.
        private void AssertEquivalent(JObject reconstructed)
        {
            var expected = JObject.Parse(ValidationJson.Line(
                JObject.FromObject(service.Snapshot())));
            var mismatch = FirstMismatch(expected, reconstructed, "");
            Assert.That(mismatch, Is.Null,
                "Reconstructed state diverges from the published snapshot: " + mismatch);
        }

        // Reports the first path where two tokens disagree, or null when
        // they are identical — so a divergent reconstruction names its
        // field instead of a bare boolean.
        private static string FirstMismatch(JToken expected, JToken actual, string path)
        {
            if (expected == null || actual == null)
                return expected == actual ? null : path;
            if (expected.Type != actual.Type)
                return path + " type " + expected.Type + " != " + actual.Type;
            var expectedObject = expected as JObject;
            if (expectedObject != null)
            {
                var actualObject = (JObject)actual;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in expectedObject.Properties()) names.Add(property.Name);
                foreach (var property in actualObject.Properties()) names.Add(property.Name);
                foreach (var name in names)
                {
                    var mismatch = FirstMismatch(expectedObject[name],
                        actualObject[name], path + "." + name);
                    if (mismatch != null) return mismatch;
                }
                return null;
            }
            var expectedArray = expected as JArray;
            if (expectedArray != null)
            {
                var actualArray = (JArray)actual;
                if (expectedArray.Count != actualArray.Count)
                    return path + " count " + expectedArray.Count + " != " + actualArray.Count;
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    var mismatch = FirstMismatch(expectedArray[i], actualArray[i],
                        path + "[" + i + "]");
                    if (mismatch != null) return mismatch;
                }
                return null;
            }
            return JToken.DeepEquals(expected, actual)
                ? null
                : path + " = " + expected + " != " + actual;
        }

        private JObject ReadOfType(Client client, string type)
        {
            for (var i = 0; i < 64; i++)
            {
                var record = client.Read();
                client.Reconstructor.Apply(record);
                if ((string)record["type"] == type) return record;
            }
            Assert.Fail("No " + type + " record within 64 records");
            return null;
        }

        private static DrivingContentCatalog TestCatalog()
        {
            var course = new CourseDefinition
            {
                id = "course-test",
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate { id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f },
                    new CheckpointGate { id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1997f }
                }
            };
            var intro = new EventDefinition
            {
                id = "intro-test",
                courseId = "course-test",
                laps = 1,
                countdownTicks = 2,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            return DrivingContentCatalog.Create("test-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course }, new[] { intro });
        }

        // A physical-backend catalog on flat geometry: one physical car
        // tuning plus the same minimal course shape the arcade fixture
        // uses — the course ground itself is supplied by the test's own
        // collision-only PhysicsCourseWorld.
        private static DrivingContentCatalog PhysicalCatalog()
        {
            var course = new CourseDefinition
            {
                id = "course-phys",
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate { id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f },
                    new CheckpointGate { id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1997f }
                }
            };
            var evt = new EventDefinition
            {
                id = "phys-test",
                courseId = "course-phys",
                laps = 1,
                countdownTicks = 2,
                vehicleIds = new List<string> { "car-physics-01" }
            };
            return DrivingContentCatalog.Create("phys-content-1",
                new[]
                {
                    PhysicsTestWorld.PhysicalTuning("car-physics-01",
                        VehiclePhysicsTuning.Car01())
                },
                new[] { course }, new[] { evt },
                VehicleSimulationBackend.PhysicalAssisted);
        }

        private static JObject Command(string id, string action,
            params (string name, string value)[] parameters)
        {
            var command = new JObject();
            command["type"] = "command";
            command["protocolVersion"] = 1;
            command["runId"] = "run-test";
            command["commandId"] = id;
            command["participantId"] = "local";
            command["action"] = action;
            var map = new JObject();
            foreach (var (name, value) in parameters) map[name] = value;
            command["parameters"] = map;
            return command;
        }

        [TearDown]
        public void TearDown()
        {
            bridge?.Dispose();
            bridge = null;
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception) { }
            try
            {
                if (Directory.Exists(profilesDirectory))
                    Directory.Delete(profilesDirectory, true);
            }
            catch (Exception) { }
        }
    }
}
