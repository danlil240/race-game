using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Graceful-exit finalization over a real ValidationBridge/StateStream:
    // bridge.Dispose is exactly what FoundationApp.OnApplicationQuit calls,
    // so these fixture-level disposes lock in the terminal capture-status
    // contract. This is a synthetic dispose inside the editor — evidence for
    // the semantics, not a claim about a real WM_CLOSE/process exit.
    public sealed class FoundationQuitPlayTests
    {
        private sealed class Client : IDisposable
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;

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
                return JObject.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
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
                "fr-validation-quit-" + Guid.NewGuid().ToString("N"));
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-profiles-quit-" + Guid.NewGuid().ToString("N"));
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

        private CommandResult Execute(string action, Dictionary<string, string> parameters)
        {
            return service.Execute(new GameCommand(ValidationBridge.ProtocolVersion,
                "run-test", Guid.NewGuid().ToString("N"), "local", action, parameters));
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private JObject Status()
        {
            return JObject.Parse(File.ReadAllText(
                Path.Combine(directory, "capture-status.json")));
        }

        [UnityTest]
        public IEnumerator DisposeWithoutFinishStampsTerminalProcessExit()
        {
            StartBridge();
            yield return PumpSeconds(0.2f);

            bridge.Dispose();
            bridge = null;

            var status = Status();
            Assert.That((bool)status["captureValid"], Is.False,
                "A quit without a completed finish is never a healthy capture.");
            Assert.That((bool)status["captureComplete"], Is.False);
            Assert.That((string)status["captureError"], Is.EqualTo("processExit"));
            Assert.That((int)status["recordsEmitted"], Is.GreaterThanOrEqualTo(1),
                "The constructor snapshot must be counted.");
        }

        [UnityTest]
        public IEnumerator DisposeAfterCompletedFinishKeepsValidCompletedStatus()
        {
            StartBridge();
            JObject finished = null;
            using (var client = Connect())
            {
                Hello(client);
                client.Read(); // hello reply
                yield return PumpUntil(() => bridge.ControllerAttached);
                client.Read(); // attach snapshot

                var finish = new JObject();
                finish["type"] = "finish";
                client.Send(finish);
                yield return PumpUntil(() => bridge.Stream.FinishCompleted);

                // The finished record is the last frame delivered to the
                // requesting controller; a pre-finish state line may precede it.
                for (var i = 0; i < 8 && finished == null; i++)
                {
                    var record = client.Read();
                    if (record == null) break;
                    if ((string)record["type"] == "finished") finished = record;
                }
            }
            Assert.That(finished, Is.Not.Null,
                "The finished record must reach the requesting controller.");
            Assert.That((bool)finished["captureValid"], Is.True);

            bridge.Dispose();
            bridge = null;

            // A stamped valid completion is never rewritten by the quit path.
            var status = Status();
            Assert.That((bool)status["captureValid"], Is.True);
            Assert.That((bool)status["captureComplete"], Is.True);
            Assert.That((string)status["captureError"], Is.EqualTo(""));
        }

        [UnityTest]
        public IEnumerator DisposeWithAttachedControllerIsTerminalAndBounded()
        {
            StartBridge();
            Client client = null;
            TimeSpan disposeTime;
            try
            {
                client = Connect();
                Hello(client);
                client.Read(); // hello reply
                yield return PumpUntil(() => bridge.ControllerAttached);
                client.Read(); // attach snapshot

                // Mid-capture with a live controller: records are flowing when
                // the quit path disposes the bridge underneath it.
                for (var i = 0; i < 8; i++)
                    bridge.Stream.EmitState(service.Snapshot(), null);
                yield return PumpSeconds(0.2f);

                var watch = Stopwatch.StartNew();
                bridge.Dispose();
                bridge = null;
                disposeTime = watch.Elapsed;
            }
            finally
            {
                client?.Dispose();
                bridge?.Dispose();
                bridge = null;
            }

            Assert.That(disposeTime, Is.LessThan(TimeSpan.FromSeconds(8)),
                "Bridge dispose must stay inside the bounded writer join.");
            var status = Status();
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
            Assert.That((string)status["captureError"], Is.EqualTo("processExit"));
        }

        [UnityTest]
        public IEnumerator DisposeWithStalledSinkJoinIsBoundedAndTerminal()
        {
            StartBridge();
            Client client = null;
            TimeSpan disposeTime;
            try
            {
                client = Connect(receiveBufferSize: 1024);
                Hello(client);
                client.Read(); // hello reply
                yield return PumpUntil(() => bridge.ControllerAttached);
                client.Read(); // attach snapshot; the client reads nothing after this

                // An 8 MiB record cannot drain into a 64 KiB send buffer plus a
                // 1 KiB peer receive buffer: the writer blocks on the socket
                // send, so Dispose exercises the real bounded join, not an
                // idle-queue exit.
                var giantId = new string('a', 8 * 1024 * 1024);
                bridge.Stream.EmitLookup(giantId, null, service.Snapshot().tick);
                yield return PumpSeconds(0.2f);

                var watch = Stopwatch.StartNew();
                bridge.Dispose();
                bridge = null;
                disposeTime = watch.Elapsed;
            }
            finally
            {
                // Closing the client first unblocks a still-stalled writer on a
                // failing run so teardown can never hang.
                client?.Dispose();
                bridge?.Dispose();
                bridge = null;
            }

            Assert.That(disposeTime, Is.LessThan(TimeSpan.FromSeconds(8)),
                "A stalled sink must not hang the quit path beyond the bounded join.");
            var status = Status();
            Assert.That((bool)status["captureValid"], Is.False);
            Assert.That((bool)status["captureComplete"], Is.False);
            // If the blocked write failed before the terminal stamp the code is
            // streamError; if the record was still queued past the dead session
            // it is skipped and the code stays processExit. Either is a real
            // terminal failure — never a healthy completion.
            var code = (string)status["captureError"];
            Assert.That(code == "streamError" || code == "processExit", Is.True,
                "Terminal error must be a real failure code, got: " + code);
        }

        [UnityTest]
        public IEnumerator DisposeDoesNotPersistUnsavedSettingsDraft()
        {
            StartBridge();
            var created = Execute("profile.create", P("slot", "0", "language", "en"));
            Assert.That(created.status, Is.EqualTo("applied"));
            var profilePath = Path.Combine(profilesDirectory, "profile-0.json");
            var savedBytes = File.ReadAllBytes(profilePath);

            var updated = Execute("settings.update",
                P("language", "en", "camera", "overhead",
                  "reducedCameraShake", "true", "musicVolume", "0.2",
                  "effectsVolume", "0.3", "voiceVolume", "0.4"));
            Assert.That(updated.status, Is.EqualTo("applied"));
            Assert.That(service.Snapshot().pendingSettings, Is.True);
            Assert.That(service.Snapshot().draftProfile.camera, Is.EqualTo("overhead"));
            yield return PumpSeconds(0.1f);

            bridge.Dispose();
            bridge = null;

            // Designed draft/save separation: quit never auto-saves a draft.
            // The file must be byte-identical to the explicit create.
            Assert.That(File.ReadAllBytes(profilePath), Is.EqualTo(savedBytes),
                "An unsaved draft must not be persisted on exit.");
            var reloaded = new ProfileStore(profilesDirectory,
                new UnityProfileSerializer()).Load(0);
            Assert.That(reloaded.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            Assert.That(reloaded.Profile.camera, Is.EqualTo("chase"));
            Assert.That(reloaded.Profile.reducedCameraShake, Is.False);

            var status = Status();
            Assert.That((string)status["captureError"], Is.EqualTo("processExit"));
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
