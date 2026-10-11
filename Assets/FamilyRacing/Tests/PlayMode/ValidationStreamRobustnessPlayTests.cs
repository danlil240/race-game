using System;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FamilyRacing.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Stalled-sink and reconnect regressions over real loopback sockets: a
    // peer that stops reading must not wedge finish or leave durable status
    // healthy below queue capacity, and a reconnected controller must get a
    // fresh snapshot plus outcome lookup without repeating mutations or
    // restoring capture validity.
    public sealed class ValidationStreamRobustnessPlayTests
    {
        private sealed class Client : IDisposable
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            internal readonly StreamReconstructor Reconstructor =
                new StreamReconstructor();
            private JObject appliedBoundary;

            public Client(int port, int receiveBufferSize = 0)
            {
                client = new TcpClient();
                if (receiveBufferSize > 0)
                    client.ReceiveBufferSize = receiveBufferSize;
                client.Connect("127.0.0.1", port);
                client.ReceiveTimeout = 5000;
                stream = client.GetStream();
            }

            public void Send(JObject frame)
            {
                var bytes = Encoding.UTF8.GetBytes(frame.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            public JObject Read()
            {
                var buffer = new MemoryStream();
                int b;
                while ((b = stream.ReadByte()) >= 0 && b != '\n') buffer.WriteByte((byte)b);
                if (buffer.Length == 0 && b < 0) return null;
                var record = JObject.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
                appliedBoundary = Reconstructor.Apply(record);
                return record;
            }

            // Read until the next boundary record; returns {record,
            // reconstructed state}.
            public JObject[] ReadBoundary()
            {
                for (var i = 0; i < 64; i++)
                {
                    var record = Read();
                    if (appliedBoundary != null) return new[] { record, appliedBoundary };
                }
                Assert.Fail("No boundary record within 64 records");
                return null;
            }

            public void Dispose() { try { client.Close(); } catch { } }
        }

        private string directory;
        private string profilesDirectory;
        private ProfileStore store;
        private FoundationService service;
        private ValidationBridge bridge;
        private JObject connection;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "fr-validation-" + Guid.NewGuid().ToString("N"));
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-profiles-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(profilesDirectory, new UnityProfileSerializer());
            service = new FoundationService(store, "en", "run-test");
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

        private Client Connect(int receiveBufferSize = 0)
        {
            return new Client((int)connection["port"], receiveBufferSize);
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

        private IEnumerator PumpUntil(System.Func<bool> done, float timeout = 5f)
        {
            var until = Time.unscaledTime + timeout;
            while (Time.unscaledTime < until && !done())
            {
                bridge.Pump();
                yield return null;
            }
            Assert.That(done(), Is.True, "Timed out waiting for condition.");
        }

        // A sink that never reads: its small receive buffer plus the server's
        // bounded send buffer make the writer stall on a large record. The
        // client object stays connected; it just never reads again.
        private Client ConnectStalledSink()
        {
            var client = Connect(receiveBufferSize: 1024);
            Hello(client);
            client.Read(); // hello reply
            return client;
        }

        // Enqueues an 8 MiB lookup record (ASCII command identity) plus
        // ordinary state records — far below the 256 outgoing bound, so queue
        // overflow can never rescue a stalled socket write. The giant record
        // is emitted directly through the internal stream: this exercises the
        // sink path only, never a wire command or input limit.
        private void EnqueueStalledLoad()
        {
            var giantId = new string('a', 8 * 1024 * 1024);
            bridge.Stream.EmitLookup(giantId, null, service.Snapshot().tick);
            for (var i = 0; i < 32; i++)
                bridge.Stream.EmitState(service.Snapshot(), null);
        }

        [UnityTest]
        public IEnumerator StalledSinkFinishCompletesWithoutQueueOverflow()
        {
            StartBridge();
            Client client = null;
            try
            {
                client = ConnectStalledSink();
                yield return PumpSeconds(0.3f);
                client.Read(); // attach snapshot; the client reads nothing after this

                EnqueueStalledLoad();

                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);

                yield return PumpUntil(() => bridge.Stream.FinishRequested, 5f);
                var tickBefore = service.Snapshot().tick;

                // The writer may be stalled on the socket, but the game thread
                // driving Pump must keep ticking while finish is pending.
                var deadline = Time.unscaledTime + 8f;
                while (Time.unscaledTime < deadline && !bridge.Stream.FinishCompleted)
                {
                    service.AdvanceTick();
                    bridge.Pump();
                    yield return null;
                }
                Assert.That(service.Snapshot().tick, Is.GreaterThan(tickBefore),
                    "Main-thread ticks must progress while the sink is stalled.");
                Assert.That(bridge.Stream.FinishCompleted, Is.True,
                    "Finish must complete within 8s despite a stalled sink.");
                Assert.That(bridge.Stream.FinishSucceeded, Is.False,
                    "A finish that could not deliver its record is not a success.");
                Assert.That(bridge.Stream.Failure, Is.EqualTo("streamError"),
                    "The stall is a socket failure, never a queue overflow.");
                yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
                Assert.That(service.Snapshot().captureError,
                    Is.Not.EqualTo("outgoingQueueFull"));
            }
            finally
            {
                // Close the client before the bridge: on a failing (unfixed)
                // run this unblocks the still-stalled writer so teardown
                // cannot hang or leak a blocked writer thread.
                client?.Dispose();
                bridge?.Dispose();
                bridge = null;
            }

            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
        }

        [UnityTest]
        public IEnumerator StalledSinkInvalidatesDurableStatusWithoutFinish()
        {
            StartBridge();
            Client client = null;
            try
            {
                client = ConnectStalledSink();
                yield return PumpSeconds(0.3f);
                client.Read(); // attach snapshot; the client reads nothing after this

                EnqueueStalledLoad();

                // No finish, no overflow: the socket failure alone must flip
                // the sticky failure and the durable status while the bridge
                // is still alive.
                yield return PumpUntil(() => bridge.Stream.Failure != null, 8f);
                Assert.That(bridge.Stream.Failure, Is.EqualTo("streamError"));
                yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
                Assert.That(service.Snapshot().captureError, Is.EqualTo("streamError"));

                var statusPath = Path.Combine(directory, "capture-status.json");
                var status = JObject.Parse(File.ReadAllText(statusPath));
                Assert.That((bool)status["captureValid"], Is.False);
                Assert.That((bool)status["captureComplete"], Is.False);
                Assert.That((string)status["captureError"], Is.EqualTo("streamError"));

                // Once the queue drains, the trace is a stable prefix:
                // pre-failure records already queued may drain to it, but
                // emissions after invalidation are never appended.
                yield return PumpUntil(() => bridge.Stream.PendingCommands == 0, 5f);
                yield return PumpSeconds(0.3f); // let the last taken item settle
                var tracePath = Path.Combine(directory, "state.ndjson");
                var prefixLength = new FileInfo(tracePath).Length;
                var prefixLines = ReadSharedLines(tracePath).Length;
                bridge.Stream.EmitState(service.Snapshot(), null);
                yield return PumpSeconds(0.5f);
                Assert.That(new FileInfo(tracePath).Length, Is.EqualTo(prefixLength),
                    "Trace must stay a prefix after invalidation.");
                Assert.That(ReadSharedLines(tracePath).Length,
                    Is.EqualTo(prefixLines));
            }
            finally
            {
                client?.Dispose();
                bridge?.Dispose();
                bridge = null;
            }
        }

        [UnityTest]
        public IEnumerator ReconnectReturnsSnapshotAndOriginalOutcomeWithoutRepeatingSave()
        {
            StartBridge();
            var savePath = Path.Combine(profilesDirectory, "profile-0.json");
            string profileId;
            string saveHash;
            long appliedTick;
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read(); // attach snapshot

                client.Send(Command("cmd-create", "profile.create",
                    ("slot", "0"), ("language", "en")));
                yield return PumpSeconds(0.4f);
                var pair = client.ReadBoundary();
                Assert.That((string)pair[0]["type"], Is.EqualTo("delta"));
                profileId = (string)pair[1]["savedProfile"]["id"];
                Assert.That(profileId, Is.Not.Null.And.Not.Empty);
                var created = client.Read();
                Assert.That((string)created["result"]["status"], Is.EqualTo("applied"));
                appliedTick = (long)created["result"]["tick"];
                using (var sha = SHA256.Create())
                    saveHash = BitConverter.ToString(
                        sha.ComputeHash(File.ReadAllBytes(savePath)));

                client.Send(Command("cmd-nav", "ui.navigate", ("screen", "sandbox")));
                yield return PumpSeconds(0.4f);
                client.Read();
                client.Read();

                client.Send(Command("cmd-hold", "input.set",
                    ("steering", "0.5"), ("drift", "true"), ("boost", "false"),
                    ("gadget", "false"), ("recover", "false"), ("changeCamera", "false"),
                    ("pause", "false"), ("durationTicks", "150")));
                yield return PumpSeconds(0.4f);
                client.Read();
                var held = client.Read();
                Assert.That((string)held["status"], Is.EqualTo("applied"));
                Assert.That(service.Snapshot().input.steering, Is.EqualTo(0.5f));
            } // abrupt disconnect with input held

            yield return PumpUntil(() =>
                !bridge.ControllerAttached && !service.Snapshot().captureValid, 5f);
            Assert.That(service.Snapshot().captureError,
                Is.EqualTo("controllerDisconnected"));

            using (var client = Connect())
            {
                Hello(client);
                Assert.That((string)client.Read()["type"], Is.EqualTo("hello"));
                yield return PumpUntil(() => bridge.ControllerAttached, 5f);
                var snapshot = client.Read();
                Assert.That((string)snapshot["type"], Is.EqualTo("snapshot"));
                var state = snapshot["state"];
                Assert.That((string)state["savedProfile"]["id"], Is.EqualTo(profileId),
                    "Reconnect must restore observation of the same profile.");
                Assert.That((float)state["input"]["steering"], Is.EqualTo(0f),
                    "Held input from the dead controller must not carry over.");
                Assert.That((bool)state["input"]["drift"], Is.False);
                Assert.That((bool)state["captureValid"], Is.False,
                    "Reconnect must never restore capture validity.");
                Assert.That((string)state["captureError"],
                    Is.EqualTo("controllerDisconnected"));

                var lookup = new JObject();
                lookup["type"] = "lookup";
                lookup["commandId"] = "cmd-create";
                client.Send(lookup);
                yield return PumpSeconds(0.4f);
                var found = client.Read();
                Assert.That((string)found["type"], Is.EqualTo("lookup"));
                Assert.That((bool)found["found"], Is.True);
                Assert.That((string)found["result"]["status"], Is.EqualTo("applied"));
                Assert.That((long)found["result"]["tick"], Is.EqualTo(appliedTick),
                    "Lookup must return the original applied outcome tick.");

                // Re-delivery of the identical command returns the recorded
                // outcome; the save must not be rewritten.
                client.Send(Command("cmd-create", "profile.create",
                    ("slot", "0"), ("language", "en")));
                yield return PumpSeconds(0.4f);
                client.Read(); // state boundary for the duplicate result
                var duplicate = client.Read();
                Assert.That((string)duplicate["type"], Is.EqualTo("result"));
                Assert.That((bool)duplicate["duplicate"], Is.True);
                Assert.That((string)duplicate["result"]["status"], Is.EqualTo("applied"));
                Assert.That((long)duplicate["result"]["tick"], Is.EqualTo(appliedTick));
                using (var sha = SHA256.Create())
                    Assert.That(BitConverter.ToString(
                        sha.ComputeHash(File.ReadAllBytes(savePath))),
                        Is.EqualTo(saveHash),
                        "A duplicate must never repeat the persisted mutation.");
                Assert.That((string)service.Snapshot().savedProfile.id,
                    Is.EqualTo(profileId));

                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
                yield return PumpUntil(() => bridge.Stream.FinishCompleted, 5f);
                yield return PumpSeconds(0.3f);
                var last = client.Read();
                var lastType = (string)last["type"];
                if (lastType == "state" || lastType == "delta") last = client.Read();
                Assert.That((string)last["type"], Is.EqualTo("finished"));
                Assert.That((bool)last["captureValid"], Is.False,
                    "Finish must not certify a capture that lost history.");
            }

            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
        }

        // Reads a trace the stream writer still holds open; FileShare.ReadWrite
        // coexists with the writer's FileAccess.Write handle.
        private static string[] ReadSharedLines(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file))
            {
                var lines = new System.Collections.Generic.List<string>();
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines.ToArray();
            }
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
