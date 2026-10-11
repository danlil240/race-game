using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using Newtonsoft.Json.Linq;

namespace FamilyRacing.Automation
{
    // Token-authenticated loopback command channel + state sink for the
    // editor/development validation flow. The listener accepts a single
    // controller; all game-state reads/writes happen on the game thread via
    // Pump(), while accept/reader/writer threads only move bytes. Inbound
    // frames carry their originating session so commands queued by a
    // disconnected controller are discarded, never applied.
    internal sealed class ValidationBridge : IDisposable
    {
        internal const int ProtocolVersion = 1;
        internal const int MaxPendingInbound = 128;
        private const string BuildVersion = "foundation-0.1";

        private sealed class InboundItem
        {
            public ControllerSession session;
            public JObject frame;
        }

        private readonly FoundationService service;
        private readonly Func<GameCommand, CommandResult> executeAndEmit;
        private readonly Action<string> markInputSource;
        private readonly string directory;
        private readonly string runId;
        private readonly string token;
        private readonly string deviceId;
        private readonly StateStream stream;
        private readonly ConcurrentQueue<InboundItem> inbound = new ConcurrentQueue<InboundItem>();
        private readonly object sessionLock = new object();
        private TcpListener listener;
        private Thread acceptThread;
        private ControllerSession session;
        private ControllerSession pending;
        private volatile int inboundCount;
        private volatile bool stopping;
        private volatile bool finishRequested;

        internal ValidationBridge(FoundationService service, string runId, string directory,
            Func<GameCommand, CommandResult> executeAndEmit, Action<string> markInputSource)
        {
            this.service = service;
            this.executeAndEmit = executeAndEmit;
            this.markInputSource = markInputSource;
            this.directory = directory;
            this.runId = runId;
            // Per-run identifier — never machine or player data.
            deviceId = Guid.NewGuid().ToString("N");
            token = Guid.NewGuid().ToString("N");

            if (Directory.Exists(directory) &&
                Directory.GetFileSystemEntries(directory).Length > 0)
                throw new InvalidOperationException(
                    "validation directory must be new or empty: " + directory);
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "profiles"));

            stream = new StateStream(directory, runId, deviceId,
                FoundationService.RequiredParticipant, BuildVersion);
            stream.Start();

            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            WriteConnectionFile(((IPEndPoint)listener.LocalEndpoint).Port);
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ValidationAccept" };
            acceptThread.Start();

            // Trace must open with a snapshot at sequence 1.
            stream.EmitSnapshot(service.Snapshot());
        }

        internal bool ControllerAttached => session != null;
        internal int PendingInbound => inboundCount;
        internal StateStream Stream => stream;

        private void WriteConnectionFile(int port)
        {
            var info = new JObject();
            info["protocolVersion"] = ProtocolVersion;
            info["host"] = "127.0.0.1";
            info["port"] = port;
            info["token"] = token;
            info["runId"] = runId;
            info["participantId"] = FoundationService.RequiredParticipant;
            File.WriteAllText(Path.Combine(directory, "connection.json"),
                ValidationJson.Line(info) + "\n");
        }

        // --- game-thread entry points -------------------------------------

        internal void Pump()
        {
            AttachPending();
            DetectDisconnect();
            InboundItem item;
            while (inbound.TryDequeue(out item))
            {
                Interlocked.Decrement(ref inboundCount);
                // Frames queued by a dead or replaced session are discarded:
                // a post-disconnect drain can never reapply held input or
                // mutations from the dead controller.
                if (item.session == null || item.session != session || !item.session.alive)
                    continue;
                HandleFrame(item.frame);
            }
            // Unified sticky failure: every stream failure (writer, queue,
            // trace, status, invalidation) reaches service state and health.
            var streamFailure = stream.Failure;
            if (streamFailure != null)
                service.SetCaptureFailure(streamFailure);
            // Async finish completion: the writer has flushed trace + status
            // and delivered the finished record — now close the session.
            if (finishRequested && session != null && stream.FinishCompleted)
            {
                stream.Detach();
                session.Dispose();
                session = null;
            }
        }

        internal void EmitTick()
        {
            if (!finishRequested) stream.EmitState(service.Snapshot(), null);
        }

        internal void EmitCommandBoundary(CommandResult result)
        {
            if (finishRequested || result == null) return;
            // Action boundary publishes the post-execution state before the
            // result so the result lands on a state boundary.
            stream.EmitState(service.Snapshot(), result.commandId);
            stream.EmitResult(result);
        }

        private void AttachPending()
        {
            ControllerSession candidate;
            lock (sessionLock)
            {
                if (pending != null && !pending.alive)
                {
                    // A dead pending session (bad hello, dropped socket) must
                    // never wedge the controller slot.
                    pending.Dispose();
                    pending = null;
                }
                candidate = pending;
            }
            if (candidate == null || !candidate.authenticated) return;
            lock (sessionLock)
            {
                session = candidate;
                pending = null;
            }
            stream.Attach(candidate);
            markInputSource?.Invoke("agent");
            // First live frame after the authenticated hello: a fresh snapshot
            // carrying the current global sequence — never a stale prefix.
            stream.EmitSnapshot(service.Snapshot());
        }

        private void DetectDisconnect()
        {
            if (session == null || session.alive) return;
            var dead = session;
            session = null;
            stream.Detach();
            dead.Dispose();
            // Queued frames from the dead session are dropped by the
            // session-association check in Pump's drain loop.
            service.ClearInput();
            markInputSource?.Invoke("none");
            // A disconnect invalidates the capture even mid-finish: the
            // finished reply can no longer reach its controller, so the run
            // must not complete as valid.
            service.SetCaptureFailure("controllerDisconnected");
            stream.Invalidate("controllerDisconnected");
        }

        private void HandleFrame(JObject frame)
        {
            var type = ValidationJson.Text(frame, "type");
            if (finishRequested)
            {
                // The run is closed: nothing after the finish boundary may
                // mutate state or emit capture records.
                stream.EmitError("captureClosed");
                return;
            }
            switch (type)
            {
                case "command": HandleCommand(frame); break;
                case "discover": HandleDiscover(); break;
                case "snapshot": stream.EmitSnapshot(service.Snapshot()); break;
                case "lookup": HandleLookup(frame); break;
                case "health": HandleHealth(); break;
                case "finish": HandleFinish(); break;
                case "hello": stream.EmitError("unexpectedHello"); break;
                default: stream.EmitError("unknownType"); break;
            }
        }

        private void HandleCommand(JObject frame)
        {
            var command = BuildCommand(frame);
            if (command == null)
            {
                stream.EmitError("badCommand");
                return;
            }
            executeAndEmit(command);
        }

        private GameCommand BuildCommand(JObject frame)
        {
            var version = ValidationJson.Int(frame, "protocolVersion");
            var id = ValidationJson.Text(frame, "commandId");
            var participant = ValidationJson.Text(frame, "participantId");
            var action = ValidationJson.Text(frame, "action");
            var commandRunId = ValidationJson.Text(frame, "runId");
            var parameters = frame["parameters"] as JObject;
            if (version == null || id == null || participant == null || action == null ||
                commandRunId == null || parameters == null)
                return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in parameters.Properties())
            {
                if (property.Value.Type != JTokenType.String) return null;
                map[property.Name] = (string)property.Value;
            }
            try
            {
                return new GameCommand(version.Value, commandRunId, id, participant, action, map);
            }
            catch (ArgumentException) { return null; }
        }

        private void HandleDiscover()
        {
            var state = service.Snapshot();
            var commands = new JArray();
            foreach (var descriptor in service.Discover())
                commands.Add(JObject.FromObject(descriptor));
            stream.EmitDiscovery(commands, JObject.FromObject(state.featureCoverage), state.tick);
        }

        private void HandleLookup(JObject frame)
        {
            var commandId = ValidationJson.Text(frame, "commandId");
            var result = commandId != null ? service.Lookup(commandId) : null;
            stream.EmitLookup(commandId, result, service.Snapshot().tick);
        }

        private void HandleHealth()
        {
            var state = service.Snapshot();
            stream.EmitHealth(state.captureValid, state.captureError,
                inboundCount, stream.PendingCommands, session != null, state.tick);
        }

        private void HandleFinish()
        {
            if (finishRequested) { stream.EmitError("captureClosed"); return; }
            finishRequested = true;
            service.ClearInput();
            markInputSource?.Invoke("none");
            var state = service.Snapshot();
            stream.EmitState(state, null);
            // Async finish: the writer flushes the finished trace line and
            // durable capture-status, then sends the finished record; Pump
            // closes the session once FinishCompleted. The game thread and
            // UI stay responsive even if the sink stalls.
            stream.RequestFinish(state.tick);
        }

        // --- background accept loop ----------------------------------------

        private bool TryEnqueueInbound(ControllerSession from, JObject frame)
        {
            if (finishRequested)
            {
                // Reply directly on the reader thread (send lock protects
                // ordering) without consuming inbound capacity.
                from.WriteError("captureClosed");
                return true;
            }
            if (Interlocked.Increment(ref inboundCount) > MaxPendingInbound)
            {
                Interlocked.Decrement(ref inboundCount);
                return false;
            }
            inbound.Enqueue(new InboundItem { session = from, frame = frame });
            return true;
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }
                client.NoDelay = true;
                // Bounded socket buffers: a stalled sink must surface as a
                // queue overflow, not silently absorb unbounded records.
                client.ReceiveBufferSize = 64 * 1024;
                client.SendBufferSize = 64 * 1024;
                string rejectCode;
                ControllerSession candidate = null;
                lock (sessionLock)
                {
                    if (pending != null && !pending.alive)
                    {
                        pending.Dispose();
                        pending = null;
                    }
                    if (finishRequested)
                    {
                        rejectCode = "captureClosed";
                    }
                    else if (session == null && pending == null)
                    {
                        candidate = new ControllerSession(client, runId, token,
                            ServerHello(), TryEnqueueInbound);
                        pending = candidate;
                        rejectCode = null;
                    }
                    else
                    {
                        rejectCode = "controllerBusy";
                    }
                }
                if (rejectCode != null)
                {
                    // The slow drain+close happens outside the lock so a
                    // rejected socket can never stall the game thread.
                    ControllerSession.RejectAndClose(client, rejectCode, runId);
                    candidate?.Dispose();
                }
            }
        }

        private string ServerHello()
        {
            return "{\"type\":\"hello\",\"protocolVersion\":1,\"runId\":\"" + runId +
                "\",\"buildVersion\":\"" + BuildVersion + "\",\"contentVersion\":\"" +
                BuildVersion + "\"}";
        }

        public void Dispose()
        {
            stopping = true;
            try { listener?.Stop(); } catch { }
            lock (sessionLock)
            {
                pending?.Dispose();
                pending = null;
            }
            session?.Dispose();
            session = null;
            stream.Dispose();
        }
    }
}
