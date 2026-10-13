using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FamilyRacing.Automation;
using FamilyRacing.Core.Foundation;
using Newtonsoft.Json.Linq;

namespace FamilyRacing
{
    // Runtime adapter over Core's RenderPerformanceCapture: feeds it real
    // rendered-frame intervals and owns bounded, off-thread report writes.
    // The writer thread serializes a snapshot taken on the game thread —
    // file IO can never stall the race thread. Write failures are sticky:
    // reportStatus/reportError stay visible in the schema-5 presentation
    // summary and a "performance" record carries the outcome to the
    // validation stream when one exists.
    public sealed class RenderPerformanceRecorder : IDisposable
    {
        // A report is compact by contract: aggregates + bounded segments.
        // A serialized report past this ceiling fails instead of growing
        // without limit.
        internal const long MaxReportBytes = 4L * 1024 * 1024;
        private const int MaxQueuedReports = 4;

        // Delivered to the state stream via DrainReportEvents on the game
        // thread: each queued report resolves to exactly one outcome.
        public sealed class ReportEvent
        {
            public string path;
            public string file;
            public string status;   // "written" | "failed"
            public string error;    // "" when written
        }

        private sealed class ReportJob
        {
            public string path;
            public RenderPerformanceState state;
        }

        private readonly RenderPerformanceCapture capture;
        private readonly BlockingCollection<ReportJob> jobs =
            new BlockingCollection<ReportJob>(MaxQueuedReports);
        private readonly ConcurrentQueue<ReportEvent> events =
            new ConcurrentQueue<ReportEvent>();
        private readonly object writeLock = new object();
        private Thread writer;
        private bool started;
        private bool disposed;
        private int pendingWrites;
        private int completedWrites;

        public RenderPerformanceRecorder(string profile,
            string buildIdentity, string contentVersion, string platform,
            bool renderingAvailable,
            string processMemoryMetric = "", string processMemorySource = "",
            string processMemoryUnits = "",
            Func<double> clockSeconds = null)
        {
            capture = new RenderPerformanceCapture(clockSeconds,
                profile, buildIdentity, contentVersion, platform,
                renderingAvailable, processMemoryMetric,
                processMemorySource, processMemoryUnits);
        }

        // The brief's measurement contract.
        public void RecordFrame(double frameMs, string phase,
            string cameraMode, string vehicleClass)
        {
            capture.RecordFrame(frameMs, phase, cameraMode, vehicleClass);
        }

        public RenderPerformanceState Snapshot()
        {
            return capture.Snapshot();
        }

        public RenderPerformanceSummary Summary()
        {
            return capture.Summary();
        }

        public string Evaluate(double p95BudgetMs)
        {
            return capture.Evaluate(p95BudgetMs);
        }

        public bool CaptureValid => capture.CaptureValid;
        public long FrameCount => capture.FrameCount;
        public bool ReportWritePending => pendingWrites > completedWrites;

        // Queue a compact report write. Returns false when the queue is
        // full or the recorder is disposed — the capture keeps running and
        // the failure is marked on the report health fields.
        public bool RequestReport(string path)
        {
            if (disposed || string.IsNullOrEmpty(path))
            {
                capture.MarkReportFailed("reportRefused");
                return false;
            }
            // Snapshot on the calling (game) thread: bounded aggregate work;
            // the writer thread owns UTF-8/serialization and file IO.
            var job = new ReportJob { path = path, state = Snapshot() };
            job.state.reportStatus = "queued";
            if (!jobs.TryAdd(job))
            {
                capture.MarkReportFailed("reportQueueFull");
                return false;
            }
            capture.MarkReportQueued();
            Interlocked.Increment(ref pendingWrites);
            EnsureWriter();
            return true;
        }

        // Queued report outcomes for the validation stream; called on the
        // game thread between boundaries.
        public List<ReportEvent> DrainReportEvents()
        {
            if (events.IsEmpty) return null;
            var drained = new List<ReportEvent>();
            ReportEvent item;
            while (events.TryDequeue(out item)) drained.Add(item);
            return drained;
        }

        // Wait for queued writes to land; used at quit so the final report
        // is durable before the process exits.
        public bool FlushReportWrites(int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (pendingWrites > completedWrites
                && DateTime.UtcNow < deadline)
                Thread.Sleep(5);
            return pendingWrites == completedWrites;
        }

        private void EnsureWriter()
        {
            lock (writeLock)
            {
                if (started) return;
                started = true;
                writer = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "RenderPerformanceReportWriter"
                };
                writer.Start();
            }
        }

        private void WriterLoop()
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                var outcome = new ReportEvent { path = job.path };
                try
                {
                    outcome.file = Path.GetFileName(job.path);
                    var payload = new JObject();
                    payload["reportVersion"] = 1;
                    payload["state"] = JObject.FromObject(job.state);
                    var line = ValidationJson.Line(payload);
                    if (line.Length > MaxReportBytes)
                        throw new InvalidDataException("reportTooLarge");
                    var directory = Path.GetDirectoryName(job.path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    File.WriteAllText(job.path, line + "\n");
                    outcome.status = "written";
                    outcome.error = "";
                    capture.MarkReportWritten();
                    job.state.reportStatus = "written";
                }
                catch (Exception error)
                {
                    var code = error is InvalidDataException
                        ? "reportTooLarge" : "writeError";
                    outcome.status = "failed";
                    outcome.error = code;
                    capture.MarkReportFailed(code);
                    job.state.reportStatus = "failed";
                    job.state.reportError = code;
                }
                events.Enqueue(outcome);
                Interlocked.Increment(ref completedWrites);
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { jobs.CompleteAdding(); } catch { }
            try { writer?.Join(TimeSpan.FromSeconds(2)); } catch { }
            try { jobs.Dispose(); } catch { }
        }
    }
}
