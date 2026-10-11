using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using FamilyRacing.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    // Loopback transport + capture tests against a real ValidationBridge,
    // service and TCP client over 127.0.0.1.
    public sealed class ValidationBridgePlayTests
    {
        private sealed class Client : IDisposable
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;

            public Client(int port)
            {
                client = new TcpClient();
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

            public void SendRaw(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            public JObject Read()
            {
                var buffer = new MemoryStream();
                int b;
                while ((b = stream.ReadByte()) >= 0 && b != '\n') buffer.WriteByte((byte)b);
                if (buffer.Length == 0 && b < 0) return null;
                return JObject.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            }

            public void Dispose() { try { client.Close(); } catch { } }
        }

        private string directory;
        private ProfileStore store;
        private FoundationService service;
        private ValidationBridge bridge;
        private JObject connection;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "fr-validation-" + Guid.NewGuid().ToString("N"));
            var profiles = Path.Combine(Path.GetTempPath(),
                "fr-profiles-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(profiles, new UnityProfileSerializer());
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

        [UnityTest]
        public IEnumerator HelloAttachCommandAndFinishFlow()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                var reply = client.Read();
                Assert.That((string)reply["type"], Is.EqualTo("hello"));
                Assert.That((string)reply["runId"], Is.EqualTo("run-test"));

                yield return PumpSeconds(0.3f);
                var snapshot = client.Read();
                Assert.That((string)snapshot["type"], Is.EqualTo("snapshot"));
                Assert.That(snapshot["state"], Is.Not.Null);
                Assert.That((string)snapshot["state"]["screen"], Is.EqualTo("profiles"));
                Assert.That(snapshot["sequence"].Value<long>(), Is.GreaterThanOrEqualTo(1));
                Assert.That((string)snapshot["deviceId"], Is.Not.Null.And.Not.Empty);
                Assert.That((string)snapshot["participantId"], Is.Not.Null.And.Not.Empty);
                Assert.That(snapshot["monotonicMs"], Is.Not.Null);
                Assert.That(snapshot["tick"], Is.Not.Null);

                var command = new JObject();
                command["type"] = "command";
                command["protocolVersion"] = 1;
                command["runId"] = "run-test";
                command["commandId"] = "cmd-create-1";
                command["participantId"] = "local";
                command["action"] = "profile.create";
                var parameters = new JObject();
                parameters["slot"] = "0";
                parameters["language"] = "en";
                command["parameters"] = parameters;
                client.Send(command);
                yield return PumpSeconds(0.5f);

                var boundary = client.Read();
                Assert.That((string)boundary["type"], Is.EqualTo("state"));
                Assert.That((string)boundary["commandId"], Is.EqualTo("cmd-create-1"));
                Assert.That((string)boundary["state"]["screen"], Is.EqualTo("settings"));

                var result = client.Read();
                Assert.That((string)result["type"], Is.EqualTo("result"));
                Assert.That((string)result["commandId"], Is.EqualTo("cmd-create-1"));
                Assert.That((string)result["status"], Is.EqualTo("applied"));
                Assert.That((bool)result["duplicate"], Is.False);
                Assert.That((string)result["result"]["status"], Is.EqualTo("applied"));

                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
                yield return PumpSeconds(0.5f);
                var finalState = client.Read();
                Assert.That((string)finalState["type"], Is.EqualTo("state"));
                var finished = client.Read();
                Assert.That((string)finished["type"], Is.EqualTo("finished"));
                Assert.That((bool)finished["captureValid"], Is.True);
            }
            bridge.Dispose();

            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.True);
            Assert.That((bool)status["captureComplete"], Is.True);

            var trace = File.ReadAllLines(Path.Combine(directory, "state.ndjson"));
            Assert.That(trace.Length, Is.GreaterThanOrEqualTo(2));
            var first = JObject.Parse(trace[0]);
            Assert.That((string)first["type"], Is.EqualTo("snapshot"));
            Assert.That((int)first["sequence"], Is.EqualTo(1));
            bridge = null;
        }

        [UnityTest]
        public IEnumerator UnauthorizedHelloRejected()
        {
            StartBridge();
            using (var client = Connect())
            {
                var hello = new JObject();
                hello["type"] = "hello";
                hello["protocolVersion"] = 1;
                hello["runId"] = "run-test";
                hello["token"] = "wrong-token";
                client.Send(hello);
                var reply = client.Read();
                Assert.That((string)reply["type"], Is.EqualTo("error"));
                Assert.That((string)reply["code"], Is.EqualTo("unauthorized"));
            }
            yield return PumpSeconds(0.2f);
        }

        [UnityTest]
        public IEnumerator MalformedAndOversizeFramesRejected()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                Assert.That((string)client.Read()["type"], Is.EqualTo("hello"));
                yield return PumpSeconds(0.3f);
                client.Read(); // attach snapshot

                client.SendRaw(new string('x', ValidationJson.MaxFrameChars + 10));
                var oversized = client.Read();
                Assert.That((string)oversized["type"], Is.EqualTo("error"));
                Assert.That((string)oversized["code"], Is.EqualTo("frameTooLarge"));

                client.SendRaw("{\"type\":\"x\",\"type\":\"y\"}");
                var dup = client.Read();
                Assert.That((string)dup["type"], Is.EqualTo("error"));

                client.SendRaw("{deep}");
                var bad = client.Read();
                Assert.That((string)bad["type"], Is.EqualTo("error"));
            }
            yield return PumpSeconds(0.2f);
        }

        [UnityTest]
        public IEnumerator SecondControllerRejectedWhileFirstAttached()
        {
            StartBridge();
            using (var first = Connect())
            {
                Hello(first);
                Assert.That((string)first.Read()["type"], Is.EqualTo("hello"));
                yield return PumpSeconds(0.3f);
                first.Read();

                using (var second = Connect())
                {
                    Hello(second);
                    var reply = second.Read();
                    Assert.That((string)reply["type"], Is.EqualTo("error"));
                    Assert.That((string)reply["code"], Is.EqualTo("controllerBusy"));
                }
            }
            yield return PumpSeconds(0.2f);
        }

        [UnityTest]
        public IEnumerator DisconnectInvalidatesCaptureAndClearsAgentInput()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read();

                // Create a profile (lands on settings), then navigate to the
                // sandbox and hold input so disconnect must clear it.
                var create = Command("cmd-create", "profile.create",
                    ("slot", "1"), ("language", "en"));
                client.Send(create);
                yield return PumpSeconds(0.4f);
                client.Read(); // state boundary
                var created = client.Read(); // result
                Assert.That((string)created["result"]["status"], Is.EqualTo("applied"));

                var nav = Command("cmd-nav", "ui.navigate", ("screen", "sandbox"));
                client.Send(nav);
                yield return PumpSeconds(0.4f);
                client.Read(); // state boundary
                client.Read(); // result

                var input = Command("cmd-input", "input.set",
                    ("steering", "0.5"), ("drift", "true"), ("boost", "false"),
                    ("gadget", "false"), ("recover", "false"), ("changeCamera", "false"),
                    ("pause", "false"), ("durationTicks", "150"));
                client.Send(input);
                yield return PumpSeconds(0.4f);
                client.Read();
                var result = client.Read();
                Assert.That((string)result["status"], Is.EqualTo("applied"));
            } // abrupt disconnect

            yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
            var state = service.Snapshot();
            Assert.That(state.captureValid, Is.False);
            Assert.That(state.captureError, Is.EqualTo("controllerDisconnected"));
            Assert.That(state.input.steering, Is.EqualTo(0f));
            Assert.That(state.input.drift, Is.False);

            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((string)status["captureError"], Is.EqualTo("controllerDisconnected"));
        }

        [UnityTest]
        public IEnumerator WrongHelloThenValidHelloAccepted()
        {
            StartBridge();
            using (var bad = Connect())
            {
                var hello = new JObject();
                hello["type"] = "hello";
                hello["protocolVersion"] = 1;
                hello["runId"] = "run-test";
                hello["token"] = "wrong-token";
                bad.Send(hello);
                var reply = bad.Read();
                Assert.That((string)reply["type"], Is.EqualTo("error"));
                Assert.That((string)reply["code"], Is.EqualTo("unauthorized"));
            }
            // A dead pending session must never wedge the controller slot.
            yield return PumpSeconds(0.3f);
            using (var good = Connect())
            {
                Hello(good);
                var reply = good.Read();
                Assert.That((string)reply["type"], Is.EqualTo("hello"));
                yield return PumpSeconds(0.3f);
                var snapshot = good.Read();
                Assert.That((string)snapshot["type"], Is.EqualTo("snapshot"));
            }
        }

        [UnityTest]
        public IEnumerator TrailingJsonAndOverflowIntNeverMutateState()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read(); // attach snapshot

                var trailing = Command("cmd-trail", "profile.create",
                    ("slot", "0"), ("language", "en"));
                client.SendRaw(trailing.ToString(Newtonsoft.Json.Formatting.None) + " {\"junk\":1}");
                var err = client.Read();
                Assert.That((string)err["type"], Is.EqualTo("error"));

                var overflow = new JObject();
                overflow["type"] = "command";
                overflow["protocolVersion"] = 999999999999999999L;
                overflow["runId"] = "run-test";
                overflow["commandId"] = "cmd-huge";
                overflow["participantId"] = "local";
                overflow["action"] = "profile.create";
                overflow["parameters"] = new JObject();
                client.Send(overflow);
                // The frame must be pumped through the inbound queue before
                // the badCommand error is emitted.
                yield return PumpSeconds(0.4f);
                var err2 = client.Read();
                Assert.That((string)err2["type"], Is.EqualTo("error"));
                Assert.That((string)err2["code"], Is.EqualTo("badCommand"));
            }
            yield return PumpSeconds(0.4f);
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("profiles"));
            Assert.That(state.activeSlot, Is.EqualTo(-1));
        }

        [UnityTest]
        public IEnumerator QueuedCommandsFromDeadSessionAreDropped()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read();

                client.Send(Command("cmd-create", "profile.create",
                    ("slot", "1"), ("language", "en")));
                yield return PumpSeconds(0.4f);
                client.Read(); client.Read();

                client.Send(Command("cmd-nav", "ui.navigate", ("screen", "sandbox")));
                yield return PumpSeconds(0.4f);
                client.Read(); client.Read();

                client.Send(Command("cmd-hold", "input.set",
                    ("steering", "0.5"), ("drift", "true"), ("boost", "false"),
                    ("gadget", "false"), ("recover", "false"), ("changeCamera", "false"),
                    ("pause", "false"), ("durationTicks", "150")));
                yield return PumpSeconds(0.4f);
                client.Read();
                var applied = client.Read();
                Assert.That((string)applied["status"], Is.EqualTo("applied"));

                // A command queued just before disconnect must never apply:
                // the drain drops frames whose session is already dead.
                client.Send(Command("cmd-late", "input.set",
                    ("steering", "-1"), ("drift", "true"), ("boost", "true"),
                    ("gadget", "true"), ("recover", "false"), ("changeCamera", "false"),
                    ("pause", "false"), ("durationTicks", "150")));
            } // close immediately — dead-session frames are discarded
            yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
            var state = service.Snapshot();
            Assert.That(state.input.steering, Is.EqualTo(0f));
            Assert.That(state.input.drift, Is.False);
            Assert.That(state.input.boost, Is.False);
        }

        [UnityTest]
        public IEnumerator NewControllerAfterFinishRejectedCaptureClosed()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read();
                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
                yield return PumpSeconds(0.6f);
                var last = client.Read();
                if ((string)last["type"] == "state") last = client.Read();
                Assert.That((string)last["type"], Is.EqualTo("finished"));
            }
            yield return PumpSeconds(0.3f);
            using (var late = Connect())
            {
                var reply = late.Read();
                Assert.That((string)reply["type"], Is.EqualTo("error"));
                Assert.That((string)reply["code"], Is.EqualTo("captureClosed"));
            }
        }

        [UnityTest]
        public IEnumerator TraceBoundPreservesPrefixAndReachesService()
        {
            StartBridge();
            // Direct stream with an injectable tiny bound + tiny queue.
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var stream = new StateStream(dir, "run-bounded", "dev-b", "local", "test",
                maxTraceBytes: 1200, maxOutgoing: 128);
            try
            {
                stream.Start();
                var snapshot = service.Snapshot();
                stream.EmitSnapshot(snapshot);
                // Non-ASCII command ids make the UTF-8 byte bound observable:
                // bytes, not characters, must decide the limit.
                for (var i = 0; i < 20; i++)
                    stream.EmitLookup("cmd-שלום-עולם-" + i, null, snapshot.tick);
                yield return WaitFor(() => stream.Failure != null, 5f);
                Assert.That(stream.Failure, Is.EqualTo("traceLimit"));

                var info = new FileInfo(Path.Combine(dir, "state.ndjson"));
                Assert.That(info.Exists, Is.True, "Prefix trace must exist.");
                Assert.That(info.Length, Is.LessThanOrEqualTo(1200),
                    "Trace must stop at the byte bound, never truncate.");
                var lines = File.ReadAllLines(info.FullName);
                Assert.That(lines.Length, Is.GreaterThanOrEqualTo(1));
                var first = JObject.Parse(lines[0]);
                Assert.That((string)first["type"], Is.EqualTo("snapshot"));
                Assert.That((int)first["sequence"], Is.EqualTo(1));
            }
            finally { stream.Dispose(); }

            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(dir, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
            try { Directory.Delete(dir, true); } catch { }
        }

        [UnityTest]
        public IEnumerator OutgoingQueueOverflowReachesServiceAndStatus()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read(); // attach snapshot
                // The client never reads again: its socket buffer fills, the
                // writer stalls on send, and the bounded outgoing queue
                // overflows — a real sink failure the service must see.
                for (var i = 0; i < 800; i++)
                    bridge.Stream.EmitState(service.Snapshot(), null);
                yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
                var state = service.Snapshot();
                Assert.That(state.captureValid, Is.False);
                Assert.That(state.captureError, Is.EqualTo("outgoingQueueFull"));
            }
            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
        }

        [UnityTest]
        public IEnumerator StatusWriteFailurePreventsFalseComplete()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-stream-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var stream = new StateStream(dir, "run-st", "dev-st", "local", "test");
            try
            {
                stream.Start();
                stream.EmitSnapshot(service.Snapshot());
                yield return WaitFor(
                    () => File.Exists(Path.Combine(dir, "capture-status.json")), 5f);
                // Swap the status file for a directory: every WriteStatus now
                // fails, and the finish must not claim a valid capture.
                File.Delete(Path.Combine(dir, "capture-status.json"));
                Directory.CreateDirectory(Path.Combine(dir, "capture-status.json"));
                stream.RequestFinish(0);
                yield return WaitFor(() => stream.FinishCompleted, 5f);
                Assert.That(stream.FinishSucceeded, Is.False);
                Assert.That(stream.Failure, Is.Not.Null);
            }
            finally { stream.Dispose(); }
            Assert.That(Directory.Exists(
                Path.Combine(dir, "capture-status.json")), Is.True,
                "No parseable complete status may be left behind.");
            try { Directory.Delete(dir, true); } catch { }
        }

        [UnityTest]
        public IEnumerator StatusWriteFailureRejectsFinishedReply()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read();
                // Swap the durable status file for a directory so every
                // WriteStatus fails: the finished socket payload must not
                // stay captureValid=true after that failure.
                var statusPath = Path.Combine(directory, "capture-status.json");
                File.Delete(statusPath);
                Directory.CreateDirectory(statusPath);
                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
                yield return PumpSeconds(0.6f);
                var last = client.Read();
                if ((string)last["type"] == "state") last = client.Read();
                Assert.That((string)last["type"], Is.EqualTo("finished"));
                Assert.That((bool)last["captureValid"], Is.False,
                    "Finished reply must reflect the failed status write.");
            }
            yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
            Assert.That(Directory.Exists(
                Path.Combine(directory, "capture-status.json")), Is.True,
                "No parseable complete status may be left behind.");
        }

        [UnityTest]
        public IEnumerator DisconnectDuringFinishInvalidatesCapture()
        {
            StartBridge();
            using (var client = Connect())
            {
                Hello(client);
                client.Read();
                yield return PumpSeconds(0.3f);
                client.Read();
                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
            } // disconnect before the finish boundary is processed
            yield return PumpUntil(() => !service.Snapshot().captureValid, 5f);
            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
        }

        [UnityTest]
        public IEnumerator AbnormalDisposeWritesInvalidIncompleteStatus()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-stream-exit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var stream = new StateStream(dir, "run-exit", "dev-x", "local", "test");
            stream.Start();
            stream.EmitSnapshot(service.Snapshot());
            yield return WaitFor(() => File.Exists(Path.Combine(dir, "state.ndjson")), 5f);
            stream.Dispose();

            var status = JObject.Parse(File.ReadAllText(
                Path.Combine(dir, "capture-status.json")));
            Assert.That((bool)status["captureValid"], Is.False,
                "Abnormal exit must not leave the started-valid status behind.");
            Assert.That((bool)status["captureComplete"], Is.False);
            Assert.That((string)status["captureError"], Is.EqualTo("processExit"));
            try { Directory.Delete(dir, true); } catch { }
        }

        // Test-only dependency swap: FoundationApp always constructs its own
        // service on persistentDataPath; fixtures replace it with a temp
        // store so no test ever reads or writes real profile data.
        private static FoundationService IsolateService(FoundationApp app)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-app-" + Guid.NewGuid().ToString("N"));
            var isolated = new FoundationService(
                new ProfileStore(dir, new UnityProfileSerializer()), "en", app.RunId);
            typeof(FoundationApp).GetField("service",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance).SetValue(app, isolated);
            return isolated;
        }

        [UnityTest]
        public IEnumerator OpposingTouchInputsComposeNeutral()
        {
            var go = new GameObject("input-fixture");
            var input = go.AddComponent<FoundationInput>();
            var app = go.AddComponent<FoundationApp>();
            yield return null;
            IsolateService(app);
            app.Dispatch("profile.create", new Dictionary<string, string>
                { ["slot"] = "2", ["language"] = "en" });
            app.Dispatch("ui.navigate", new Dictionary<string, string>
                { ["screen"] = "sandbox" });
            app.Input.TouchLeft(true);
            yield return WaitFor(
                () => app.Service.Snapshot().input.steering < 0f, 5f);
            app.Input.TouchRight(true);
            yield return WaitFor(
                () => app.Service.Snapshot().input.steering == 0f, 5f);
            Assert.That(app.Service.Snapshot().input.steering, Is.EqualTo(0f),
                "Simultaneous left+right must cancel, not prefer one side.");

            // Disabling the component clears held input through the shared
            // command path — no service hold survives the component.
            app.Input.TouchRight(false);
            app.Input.TouchLeft(true);
            yield return WaitFor(
                () => app.Service.Snapshot().input.steering < 0f, 5f);
            input.enabled = false;
            yield return WaitFor(
                () => app.Service.Snapshot().input.steering == 0f, 5f);
            Assert.That(app.Service.Snapshot().input.steering, Is.EqualTo(0f));
            Assert.That(app.Input.PhysicalActive, Is.False);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator LocaleBoundaryCarriesRtlPresentation()
        {
            var go = new GameObject("app-fixture");
            var app = go.AddComponent<FoundationApp>();
            yield return null;
            IsolateService(app);
            var result = app.Dispatch("ui.language", new Dictionary<string, string>
                { ["language"] = "he" });
            Assert.That(result.status, Is.EqualTo("applied"));
            // The presentation refresh runs before the boundary emit, so the
            // state read immediately after dispatch already shows RTL.
            Assert.That(app.Service.Snapshot().presentation.textDirection,
                Is.EqualTo("rtl"));
            Assert.That(app.Service.Snapshot().presentation.fontWarning,
                Is.Not.Null.And.Not.Empty, "Missing Hebrew font must warn.");
            UnityEngine.Object.Destroy(go);
        }

        // Loads the real generated scene end-to-end: the UIDocument must
        // attach to a live panel, lay out, and the shell must answer real
        // button callbacks — the blank-preview regression.
        [UnityTest]
        public IEnumerator FoundationSceneRendersLiveShell()
        {
            yield return SceneManager.LoadSceneAsync(
                "Assets/FamilyRacing/Generated/Foundation.unity");
            var app = UnityEngine.Object.FindFirstObjectByType<FoundationApp>();
            Assert.That(app, Is.Not.Null, "Scene must contain FoundationApp.");
            IsolateService(app);
            var doc = app.GetComponent<UIDocument>();
            Assert.That(doc, Is.Not.Null);
            Assert.That(doc.panelSettings, Is.Not.Null,
                "Scene UIDocument must carry the generated PanelSettings.");
            Debug.Log("scene-test: waiting for panel attach");
            yield return WaitFor(() => doc.rootVisualElement != null
                && doc.rootVisualElement.panel != null, 5f);
            var root = doc.rootVisualElement;
            Debug.Log("scene-test: waiting for layout");
            yield return WaitFor(() => root.resolvedStyle.width > 0f
                && root.resolvedStyle.height > 0f, 5f);
            for (var i = 0; i < 4; i++)
            {
                var pick = root.Q<Button>("profile." + i + ".select");
                Assert.That(pick, Is.Not.Null, "profile." + i + ".select missing.");
                Assert.That(pick.resolvedStyle.display,
                    Is.Not.EqualTo(DisplayStyle.None));
            }
            // Real button callback on an empty slot: profile.create +
            // ui.navigate must land on the settings screen's named controls.
            // Clickable consumes pointer events, not ClickEvent.
            PressButton(root.Q<Button>("profile.0.select"));
            Debug.Log("scene-test: click -> settings");
            yield return WaitFor(() =>
                app.Service.Snapshot().screen == "settings", 5f);
            yield return WaitFor(() =>
                doc.rootVisualElement.Q<Button>("settings.save") != null, 5f);
            Debug.Log("scene-test: navigate -> sandbox");
            app.Dispatch("ui.navigate", new Dictionary<string, string>
                { ["screen"] = "sandbox" });
            foreach (var name in new[] { "input.left", "input.right",
                         "input.drift", "input.boost" })
                yield return WaitFor(() =>
                    doc.rootVisualElement.Q<VisualElement>(name) != null, 5f);
            // Disable/re-enable detaches the root: the shell must rebuild
            // against the live document root, not the Awake-cached one.
            Debug.Log("scene-test: re-enable rebind");
            doc.enabled = false;
            yield return null;
            doc.enabled = true;
            yield return WaitFor(() => doc.rootVisualElement != null
                && doc.rootVisualElement.panel != null
                && doc.rootVisualElement.Q<Button>("input.left") != null, 5f);
        }

        // Dispatches the same pointer down/up pair a real touch produces —
        // the path Clickable.ProcessDown/UpEvent actually consumes.
        private static void PressButton(Button button)
        {
            var center = button.worldBound.center;
            var touch = new Touch();
            touch.position = center;
            touch.fingerId = 5;
            touch.phase = TouchPhase.Began;
            using (var down = PointerDownEvent.GetPooled(touch, EventModifiers.None))
            {
                down.target = button;
                button.SendEvent(down);
            }
            touch.phase = TouchPhase.Ended;
            using (var up = PointerUpEvent.GetPooled(touch, EventModifiers.None))
            {
                up.target = button;
                button.SendEvent(up);
            }
        }

        private IEnumerator WaitFor(System.Func<bool> done, float timeout = 5f)
        {
            var until = Time.unscaledTime + timeout;
            while (Time.unscaledTime < until && !done())
                yield return null;
            Assert.That(done(), Is.True, "Timed out waiting for condition.");
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
        }
    }
}
