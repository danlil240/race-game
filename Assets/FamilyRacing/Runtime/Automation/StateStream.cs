using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using Newtonsoft.Json.Linq;

namespace FamilyRacing.Automation
{
    // Sequenced NDJSON record stream: every emitted record goes to the run's
    // state.ndjson trace and (while the controller session captured at emit
    // time is still attached) to that session's socket. Emit*/RequestFinish
    // are game-thread calls; Invalidate runs on the game thread and on the
    // writer thread when a socket write fails — a single writer thread
    // performs file and socket IO. The trace opens once with CreateNew, is
    // bounded by exact UTF-8 byte counts, and never reopens after failure —
    // a captured prefix can never be truncated.
    internal sealed class StateStream : IDisposable
    {
        internal const int DefaultMaxOutgoing = 256;
        internal const long DefaultMaxTraceBytes = 256L * 1024 * 1024;

        private sealed class WorkItem
        {
            // Serialized on the writer thread: emit-time code only builds
            // the immutable record, so UTF-8 work stays off the game thread.
            public string line;
            public bool traceWrite;
            public ControllerSession target; // session captured at emit time
            public bool finishCapture;
            public bool finishValid;
            public JObject record; // immutable payload; finished records are re-patched at write time
        }

        private readonly string directory;
        private readonly string runId;
        private readonly string deviceId;
        private readonly string participantId;
        private readonly string buildVersion;
        private readonly Func<string> contentVersion;
        private readonly long maxTraceBytes;
        private readonly BlockingCollection<WorkItem> queue;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object statusLock = new object();
        private Thread writer;
        private FileStream traceFile;
        private StreamWriter trace;
        private long traceBytes;
        private bool traceClosed;
        private ControllerSession attachedSession;
        private int sequence;
        // Producer-side baseline for schema-2 delta boundaries: the last
        // emitted boundary payload (deep copy) plus its stream sequence.
        // Only boundary records advance it, so a delta's `base` always
        // names the boundary an independent consumer last applied.
        private JObject baseline;
        private long baselineSequence;
        private bool captureActive = true;
        private string captureInvalidCode = "";
        private bool finishRequested;
        private volatile bool finishCompleted;
        private volatile bool finishSucceeded;
        private bool statusCompleted;
        private volatile string failure;
        private volatile bool disposed;

        internal StateStream(string directory, string runId, string deviceId,
            string participantId, string buildVersion,
            Func<string> contentVersion = null,
            long maxTraceBytes = DefaultMaxTraceBytes,
            int maxOutgoing = DefaultMaxOutgoing)
        {
            this.directory = directory;
            this.runId = runId;
            this.deviceId = deviceId;
            this.participantId = participantId;
            this.buildVersion = buildVersion;
            this.contentVersion = contentVersion;
            this.maxTraceBytes = maxTraceBytes;
            queue = new BlockingCollection<WorkItem>(maxOutgoing);
        }

        internal string TracePath => Path.Combine(directory, "state.ndjson");
        internal string StatusPath => Path.Combine(directory, "capture-status.json");
        internal int Sequence => sequence;
        internal bool CaptureActive => captureActive && !finishRequested;
        internal bool HasSession => attachedSession != null;
        internal bool FinishRequested => finishRequested;
        internal bool FinishCompleted => finishCompleted;
        internal bool FinishSucceeded => finishSucceeded;
        internal int PendingCommands => queue.Count;

        // Unified sticky failure: writer errors, queue overflow, trace limit,
        // status failures and explicit invalidations all surface here so the
        // service and health records can never report a healthy capture
        // after any stream failure.
        internal string Failure => failure;
        internal string WriterFailure => failure;

        internal void Start()
        {
            writer = new Thread(WriterLoop) { IsBackground = true, Name = "StateStreamWriter" };
            writer.Start();
            WriteStatus(valid: true, complete: false, error: "");
        }

        internal void Attach(ControllerSession session) { attachedSession = session; }
        internal void Detach() { attachedSession = null; }

        private JObject Record(string type, long tick, string commandId)
        {
            var record = new JObject();
            record["type"] = type;
            record["protocolVersion"] = 1;
            record["runId"] = runId;
            record["deviceId"] = deviceId;
            record["participantId"] = participantId;
            record["buildVersion"] = buildVersion;
            // Content is versioned separately from the build: the catalog
            // that produced driving definitions travels with every record.
            record["contentVersion"] = contentVersion != null ? contentVersion() : buildVersion;
            record["sequence"] = ++sequence;
            record["monotonicMs"] = clock.ElapsedMilliseconds;
            record["tick"] = tick;
            if (commandId != null) record["commandId"] = commandId;
            return record;
        }

        internal void EmitSnapshot(FoundationState state)
        {
            var payload = JObject.FromObject(state);
            var record = Record("snapshot", state.tick, null);
            record["stateSchemaVersion"] = state.stateSchemaVersion;
            record["state"] = payload;
            EmitBoundary(record, payload);
        }

        internal void EmitState(FoundationState state, string commandId)
        {
            var payload = JObject.FromObject(state);
            var record = Record("state", state.tick, commandId);
            record["stateSchemaVersion"] = state.stateSchemaVersion;
            record["state"] = payload;
            EmitBoundary(record, payload);
        }

        // Ordered boundary emission: a delta naming the previous boundary's
        // sequence plus its changed/removed fields, or a full `state` record
        // when no baseline exists. A delta with empty changed/removed is a
        // legitimate boundary — a tick boundary exists even when no fields
        // change.
        internal void EmitDeltaBoundary(FoundationState state, string commandId)
        {
            var payload = JObject.FromObject(state);
            if (baseline == null)
            {
                var full = Record("state", state.tick, commandId);
                full["stateSchemaVersion"] = state.stateSchemaVersion;
                full["state"] = payload;
                EmitBoundary(full, payload);
                return;
            }
            var changed = DiffChanged(baseline, payload);
            var removed = new JArray();
            CollectRemoved(baseline, payload, "", removed);
            var record = Record("delta", state.tick, commandId);
            record["stateSchemaVersion"] = state.stateSchemaVersion;
            record["base"] = baselineSequence;
            record["changed"] = changed;
            record["removed"] = removed;
            EmitBoundary(record, payload);
        }

        // Recursive object merge producer: object-vs-object diffs nest,
        // everything else (scalars, arrays, null) replaces wholesale.
        private static JObject DiffChanged(JObject oldObj, JObject newObj)
        {
            var changed = new JObject();
            foreach (var property in newObj.Properties())
            {
                var oldValue = oldObj[property.Name];
                var newValue = property.Value;
                if (oldValue == null)
                {
                    changed[property.Name] = newValue;
                    continue;
                }
                var oldChild = oldValue as JObject;
                var newChild = newValue as JObject;
                if (oldChild != null && newChild != null)
                {
                    var child = DiffChanged(oldChild, newChild);
                    if (child.HasValues) changed[property.Name] = child;
                    continue;
                }
                if (!JToken.DeepEquals(oldValue, newValue))
                    changed[property.Name] = newValue;
            }
            return changed;
        }

        // Dot-path removals mirror the consumer's removed-path semantics:
        // only keys absent from the new state are removed; present-but-null
        // is a change, never a removal.
        private static void CollectRemoved(JObject oldObj, JObject newObj,
            string prefix, JArray removed)
        {
            foreach (var property in oldObj.Properties())
            {
                var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                var newValue = newObj[property.Name];
                if (newValue == null)
                {
                    removed.Add(path);
                    continue;
                }
                var oldChild = property.Value as JObject;
                var newChild = newValue as JObject;
                if (oldChild != null && newChild != null)
                    CollectRemoved(oldChild, newChild, path, removed);
            }
        }

        // Queues a boundary record and advances the producer baseline. The
        // baseline is a deep copy so the writer can serialize the queued
        // record while the game thread diffs the next tick without sharing
        // mutable objects across threads.
        private void EmitBoundary(JObject record, JObject payload)
        {
            baselineSequence = (long)record["sequence"];
            baseline = (JObject)payload.DeepClone();
            Emit(record);
        }

        internal void EmitResult(CommandResult result)
        {
            var record = Record("result", result.tick, result.commandId);
            record["status"] = result.status;
            record["code"] = result.code;
            record["action"] = result.action;
            record["duplicate"] = result.duplicate;
            record["result"] = JObject.FromObject(result);
            Emit(record);
        }

        internal void EmitDiscovery(JArray commands, JObject coverage, long tick,
            int stateSchemaVersion)
        {
            var record = Record("discovery", tick, null);
            // Consumers must learn the observation schema at discovery
            // time — never silently reinterpret an unknown one.
            record["stateSchemaVersion"] = stateSchemaVersion;
            record["commands"] = commands;
            record["featureCoverage"] = coverage;
            Emit(record);
        }

        internal void EmitLookup(string commandId, CommandResult result, long tick)
        {
            var record = Record("lookup", result != null ? result.tick : tick, commandId);
            record["found"] = result != null;
            record["result"] = result != null ? JObject.FromObject(result) : null;
            Emit(record);
        }

        internal void EmitHealth(bool captureValid, string captureError, int pendingCommands,
            int outgoingRecords, bool controllerConnected, long tick)
        {
            var record = Record("health", tick, null);
            record["captureValid"] = captureValid;
            record["captureError"] = captureError ?? "";
            record["pendingCommands"] = pendingCommands;
            record["outgoingRecords"] = outgoingRecords;
            record["controllerConnected"] = controllerConnected;
            Emit(record);
        }

        // Screenshot completion is a sequenced capture record correlated by
        // captureId: "captured" carries the run-relative file, "failed" is
        // an honest outcome (e.g. headless capture), "rejected" carries the
        // refusal code for invalid/overflowing requests — never an
        // unsequenced error that leaves no durable trace evidence.
        internal void EmitScreenshot(string captureId, string file, string status,
            long tick, string code = null)
        {
            var record = Record("screenshot", tick, null);
            record["captureId"] = captureId;
            record["file"] = file ?? "";
            record["status"] = status;
            if (code != null) record["code"] = code;
            Emit(record);
        }

        // Unsequenced protocol error frame; socket only, never traced.
        internal void EmitError(string code)
        {
            var record = new JObject();
            record["type"] = "error";
            record["protocolVersion"] = 1;
            record["runId"] = runId;
            record["code"] = code ?? "streamError";
            Enqueue(new WorkItem { record = record, target = attachedSession });
        }

        private void Emit(JObject record)
        {
            // No capture emissions past the finish boundary.
            if (disposed || finishRequested) return;
            Enqueue(new WorkItem
            {
                record = record,
                traceWrite = captureActive,
                target = attachedSession
            });
        }

        private void Enqueue(WorkItem item)
        {
            if (!queue.TryAdd(item))
                Invalidate("outgoingQueueFull");
        }

        // Sticky failure: trace stops at the captured prefix, capture-status
        // reports the failure and Failure keeps the service informed.
        internal void Invalidate(string code)
        {
            lock (statusLock)
            {
                failure = failure ?? (code ?? "captureError");
                if (!captureActive || finishRequested) return;
                captureActive = false;
                captureInvalidCode = code ?? "captureError";
                WriteStatus(valid: false, complete: false, error: captureInvalidCode);
            }
        }

        // Async finish: the finished record is the last queued capture item;
        // the writer flushes trace + durable capture-status, then delivers
        // the record to the session captured at request time. The game
        // thread never blocks — callers poll FinishCompleted from Pump.
        internal bool RequestFinish(long tick)
        {
            if (finishRequested) return true;
            finishRequested = true;
            var record = Record("finished", tick, null);
            var item = new WorkItem
            {
                record = record,
                traceWrite = captureActive,
                target = attachedSession,
                finishCapture = true,
                finishValid = captureActive && failure == null
            };
            if (!queue.TryAdd(item))
            {
                failure = failure ?? "outgoingQueueFull";
                WriteStatus(valid: false, complete: false, error: "outgoingQueueFull");
                finishCompleted = true;
                return false;
            }
            return true;
        }

        internal void Stop()
        {
            disposed = true;
            try { queue.CompleteAdding(); } catch { }
        }

        public void Dispose()
        {
            Stop();
            try { writer?.Join(TimeSpan.FromSeconds(2)); } catch { }
            lock (statusLock)
            {
                // Abnormal exit is never reported as a healthy capture: if no
                // valid complete status was stamped the run is incomplete.
                if (!statusCompleted)
                {
                    var code = captureInvalidCode != "" ? captureInvalidCode
                        : (failure ?? (finishCompleted ? "streamError" : "processExit"));
                    WriteStatus(valid: false, complete: false, error: code);
                }
            }
            try { trace?.Dispose(); } catch { }
            try { traceFile?.Dispose(); } catch { }
        }

        private void WriterLoop()
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (item.finishCapture)
                    {
                        // ProcessFinish serializes its payload and owns its
                        // own trace write — the line does not exist earlier.
                        ProcessFinish(item);
                    }
                    else
                    {
                        // Serialization lives here so emit-time work on the
                        // game thread stays bounded to building the record.
                        if (item.line == null)
                            item.line = ValidationJson.Line(item.record);
                        if (item.traceWrite && !traceClosed)
                            WriteTraceLine(item);
                        if (!SendToTarget(item))
                            Invalidate("streamError");
                    }
                }
                catch (Exception)
                {
                    Invalidate("streamError");
                    if (item.finishCapture) finishCompleted = true;
                }
            }
        }

        private void ProcessFinish(WorkItem item)
        {
            // Trace the finished record with validity known at write time,
            // then re-evaluate: a failure during that write must not let the
            // status or the socket payload claim a healthy capture.
            if (item.traceWrite && !traceClosed)
            {
                PatchFinished(item);
                try { WriteTraceLine(item); trace?.Flush(); }
                catch (Exception) { FailSafe("streamError"); }
            }
            PatchFinished(item);
            var valid = (bool)item.record["captureValid"];
            var statusOk = WriteStatus(valid, valid,
                valid ? "" : FailureCode());
            if (!statusOk)
            {
                // Status write failed after the payload was serialized —
                // re-stamp so the finished reply cannot stay captureValid.
                FailSafe("streamError");
                PatchFinished(item);
            }
            statusCompleted = valid && statusOk;
            if (!SendFinished(item) || !statusOk)
            {
                // The finished reply could not reach its exact live target,
                // or the durable status itself failed — never false-complete.
                FailSafe("streamError");
                WriteStatus(false, false, "streamError");
                statusCompleted = false;
            }
            finishSucceeded = statusCompleted;
            finishCompleted = true;
        }

        private void PatchFinished(WorkItem item)
        {
            var valid = item.finishValid && failure == null;
            item.record["captureValid"] = valid;
            item.record["captureError"] = valid ? "" : FailureCode();
            item.line = ValidationJson.Line(item.record);
        }

        private string FailureCode()
        {
            return captureInvalidCode != "" ? captureInvalidCode
                : (failure ?? "streamError");
        }

        private void FailSafe(string code)
        {
            lock (statusLock) { failure = failure ?? code; }
        }

        // The finished record is delivered only to the exact live session it
        // was captured for; a dead or replaced target is a real failure, not
        // a skippable record.
        private bool SendFinished(WorkItem item)
        {
            var session = item.target;
            if (session == null ||
                !ReferenceEquals(session, attachedSession) || !session.alive)
                return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(item.line + "\n");
                lock (session.SendLock)
                {
                    session.Stream.Write(bytes, 0, bytes.Length);
                    session.Stream.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                // Invalidate before closing so a racing disconnect check can
                // never relabel a sink write failure; then close the exact
                // failed session so queued records skip it instead of each
                // paying the send timeout.
                Invalidate("streamError");
                session.Dispose();
                return false;
            }
        }

        // Sends to the session captured when the item was emitted. Records
        // queued under a session that has since detached or been replaced are
        // skipped — never retargeted to a newer socket. A failed write to the
        // still-current live session is a real sink failure.
        private bool SendToTarget(WorkItem item)
        {
            var session = item.target;
            if (session == null) return true;
            if (!ReferenceEquals(session, attachedSession) || !session.alive) return true;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(item.line + "\n");
                lock (session.SendLock)
                {
                    session.Stream.Write(bytes, 0, bytes.Length);
                    session.Stream.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                Invalidate("streamError");
                session.Dispose();
                return false;
            }
        }

        private void WriteTraceLine(WorkItem item)
        {
            if (trace == null)
            {
                // CreateNew can never truncate an existing trace file.
                traceFile = new FileStream(TracePath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read);
                trace = new StreamWriter(traceFile, new UTF8Encoding(false));
                traceBytes = 0;
            }
            var needed = Encoding.UTF8.GetByteCount(item.line) + 1;
            if (traceBytes + needed > maxTraceBytes)
            {
                // Bound reached: the captured prefix stays on disk intact;
                // trace writes stop permanently for this run.
                try { trace.Flush(); trace.Dispose(); } catch { }
                trace = null;
                traceFile = null;
                traceClosed = true;
                failure = failure ?? "traceLimit";
                return;
            }
            trace.Write(item.line);
            trace.Write('\n');
            trace.Flush();
            traceBytes += needed;
        }

        private bool WriteStatus(bool valid, bool complete, string error)
        {
            lock (statusLock)
            {
                var status = new JObject();
                status["captureValid"] = valid;
                status["captureComplete"] = complete;
                status["captureError"] = error ?? "";
                status["recordsEmitted"] = sequence;
                try
                {
                    File.WriteAllText(StatusPath, ValidationJson.Line(status) + "\n");
                    return true;
                }
                catch (Exception)
                {
                    failure = failure ?? "streamError";
                    return false;
                }
            }
        }
    }
}
