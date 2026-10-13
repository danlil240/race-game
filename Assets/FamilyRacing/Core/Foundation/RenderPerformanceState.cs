using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Foundation
{
    // M4 Task 2: truthful rendered-frame measurement. The capture engine
    // (RenderPerformanceCapture) is pure C# so EditMode/offline Core tests
    // exercise the exact percentile/segmentation/overflow semantics; the
    // Runtime recorder only feeds it real frame intervals and clocks.
    // Metrics that cannot be measured stay absent (null), never zero.
    [Serializable]
    public sealed class RenderPerformanceSegment
    {
        public int windowIndex;      // five-minute window, from capture start
        public bool warmup;          // first WarmupSeconds of the capture
        public string phase;         // countdown|racing|paused|loading|results|menu
        public string cameraMode;    // actual applied camera ("none" until engaged)
        public string vehicleClass;  // vehicle identity observed; "" when none
        public long samples;
        public double seconds;
        // Nearest-rank aggregates; null when the segment holds no samples —
        // an unavailable metric is never emitted as a zero.
        public double? medianFrameMs;
        public double? p95FrameMs;
        public double? maxFrameMs;
        public int stalls;           // samples strictly over the stall threshold

        public RenderPerformanceSegment Copy()
        {
            return (RenderPerformanceSegment)MemberwiseClone();
        }
    }

    // The compact schema-5 slice published inside PresentationState: bounded
    // scalars only — segment detail and raw samples stay out of the stream
    // so a record never carries a frame-sized buffer.
    [Serializable]
    public sealed class RenderPerformanceSummary
    {
        public string profile = "none";      // tablet|pc|none|missing
        public string buildIdentity = "";
        public string contentVersion = "";
        public string platform = "";
        public int targetFrameRate;
        public float renderScale;
        // False when no rendered player exists (e.g. -batchmode): frame
        // intervals are still recorded but are never presented as rendered-
        // player evidence.
        public bool renderingAvailable;
        public bool captureValid = true;
        public string captureInvalidReason = "";
        public long frames;                  // rendered intervals observed
        public long racingSamples;           // steady-state racing samples
        public double observedSeconds;
        public double warmupSeconds;
        public double pausedSeconds;
        public double loadingSeconds;
        public double? medianFrameMs;
        public double? p95FrameMs;
        public double? maxFrameMs;
        public int stallCount;               // steady-state racing stalls
        // GPU timing is a different quantity from CPU frame intervals; this
        // profile exposes no GPU timer, so it reports unavailable with a
        // reason rather than aliasing frame intervals onto it.
        public string gpuTimingStatus = "unavailable";
        public string gpuTimingReason =
            "no GPU timer exposed by the render profile";
        // Process memory is sampled EXTERNALLY once per second (Android
        // total PSS via adb dumpsys meminfo; Windows working set via the
        // harness) — Unity heap allocations are never substituted. The
        // metric/source/units are named so reports can never confuse the
        // two differently-sourced quantities; devices compare only against
        // their own baseline.
        public string processMemoryStatus = "external";
        public string processMemoryMetric = "";
        public string processMemorySource = "";
        public string processMemoryUnits = "";
        // Report-write health: none|queued|written|failed plus the failure
        // code, so a lost report is visible rather than silent.
        public string reportStatus = "none";
        public string reportError = "";

        public RenderPerformanceSummary Copy()
        {
            return (RenderPerformanceSummary)MemberwiseClone();
        }
    }

    // Full capture snapshot for the on-disk performance report: the summary
    // scalars plus bounded per-window/phase/camera segment aggregates.
    [Serializable]
    public sealed class RenderPerformanceState
    {
        public long frameCount;
        public long sampleCount;        // retained samples (bounded)
        public long racingSamples;      // steady-state racing samples
        public double observedSeconds;
        public double? medianFrameMs;
        public double? p95FrameMs;
        public double? maxFrameMs;
        public int stallCount;
        public double warmupSeconds;
        public long warmupSamples;
        public double? warmupMedianFrameMs;
        public double? warmupP95FrameMs;
        public double pausedSeconds;
        public double loadingSeconds;
        public long droppedFrames;      // non-finite/non-positive intervals
        public string profile = "none";
        public string buildIdentity = "";
        public string contentVersion = "";
        public string platform = "";
        public string cameraMode = "";  // most recent segment tags
        public string vehicleClass = "";
        public bool renderingAvailable;
        public bool captureValid = true;
        public string captureInvalidReason = "";
        // collected | insufficientData | invalid — the honest evidence
        // label; an empty or invalid capture can never masquerade as a
        // passing measurement.
        public string evidenceStatus = "insufficientData";
        public string gpuTimingStatus = "unavailable";
        public string gpuTimingReason =
            "no GPU timer exposed by the render profile";
        public string processMemoryStatus = "external";
        public string processMemoryMetric = "";
        public string processMemorySource = "";
        public string processMemoryUnits = "";
        public string reportStatus = "none";
        public string reportError = "";
        public List<RenderPerformanceSegment> segments =
            new List<RenderPerformanceSegment>();

        public RenderPerformanceState Copy()
        {
            var copy = (RenderPerformanceState)MemberwiseClone();
            copy.segments = new List<RenderPerformanceSegment>(segments.Count);
            foreach (var segment in segments)
                copy.segments.Add(segment != null ? segment.Copy() : null);
            return copy;
        }
    }

    // Bounded, truthful frame-interval capture. Every recorded interval is
    // classified by phase/camera/vehicle/five-minute window and by whether
    // it fell inside the warm-up prefix; segments keep their raw samples so
    // percentiles stay exact nearest-rank values. Two hard bounds — total
    // samples and distinct segments — invalidate the capture on overflow
    // instead of silently dropping evidence.
    public sealed class RenderPerformanceCapture
    {
        public const int MaxSamples = 120000;
        public const int MaxSegments = 1024;
        public const double WarmupSeconds = 60.0;
        public const double WindowSeconds = 300.0;
        public const double StallThresholdMs = 100.0;
        // Summary percentiles are recomputed at most this often; a sort of
        // the retained racing samples must not hitch every rendered frame.
        public const double SummaryRefreshSeconds = 1.0;

        public const string PhaseCountdown = "countdown";
        public const string PhaseRacing = "racing";
        public const string PhasePaused = "paused";
        public const string PhaseLoading = "loading";
        public const string PhaseResults = "results";
        public const string PhaseMenu = "menu";

        public const string EvidenceCollected = "collected";
        public const string EvidenceInsufficient = "insufficientData";
        public const string EvidenceInvalid = "invalid";

        public const string VerdictPass = "pass";
        public const string VerdictFail = "fail";

        private static readonly HashSet<string> Phases = new HashSet<string>(
            StringComparer.Ordinal)
        {
            PhaseCountdown, PhaseRacing, PhasePaused,
            PhaseLoading, PhaseResults, PhaseMenu
        };

        private struct SegmentKey : IEquatable<SegmentKey>
        {
            public int window;
            public bool warmup;
            public string phase;
            public string camera;
            public string vehicle;

            public bool Equals(SegmentKey other)
            {
                return window == other.window && warmup == other.warmup
                    && phase == other.phase && camera == other.camera
                    && vehicle == other.vehicle;
            }

            public override bool Equals(object obj)
            {
                return obj is SegmentKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = window * 397 ^ (warmup ? 1 : 0);
                    hash = hash * 31 + (phase != null ? phase.GetHashCode() : 0);
                    hash = hash * 31 + (camera != null ? camera.GetHashCode() : 0);
                    return hash * 31 + (vehicle != null ? vehicle.GetHashCode() : 0);
                }
            }
        }

        private readonly Func<double> clockSeconds;
        private readonly Dictionary<SegmentKey, List<double>> segments =
            new Dictionary<SegmentKey, List<double>>();
        private readonly string profile;
        private readonly string buildIdentity;
        private readonly string contentVersion;
        private readonly string platform;
        private readonly bool renderingAvailable;
        private readonly string memoryMetric;
        private readonly string memorySource;
        private readonly string memoryUnits;
        private double startSeconds = -1;
        private double lastElapsed;
        private long frames;
        private long samples;
        private long dropped;
        private long warmupSamples;
        private double pausedMs;
        private double loadingMs;
        private string lastCamera = "";
        private string lastVehicle = "";
        private bool valid = true;
        private string invalidReason = "";
        private string reportStatus = "none";
        private string reportError = "";
        private RenderPerformanceSummary cachedSummary;
        private double lastSummarySeconds = -1;

        // clockSeconds supplies monotonic seconds (tests inject a fake).
        // Identity fields are fixed at construction: a capture describes one
        // profile/build/platform combination, never a blend.
        public RenderPerformanceCapture(Func<double> clockSeconds,
            string profile, string buildIdentity, string contentVersion,
            string platform, bool renderingAvailable,
            string processMemoryMetric = "", string processMemorySource = "",
            string processMemoryUnits = "")
        {
            this.clockSeconds = clockSeconds ?? (() => 0.0);
            this.profile = profile ?? "none";
            this.buildIdentity = buildIdentity ?? "";
            this.contentVersion = contentVersion ?? "";
            this.platform = platform ?? "";
            this.renderingAvailable = renderingAvailable;
            memoryMetric = processMemoryMetric ?? "";
            memorySource = processMemorySource ?? "";
            memoryUnits = processMemoryUnits ?? "";
        }

        public bool CaptureValid => valid;
        public long FrameCount => frames;
        public long SampleCount => samples;

        private double Now()
        {
            var t = clockSeconds();
            if (double.IsNaN(t) || double.IsInfinity(t) || t < 0) return 0.0;
            return t;
        }

        // One rendered-frame interval. Past the bounds the capture is
        // invalidated and further samples are refused — a truncated capture
        // can never pass as complete evidence.
        public void RecordFrame(double frameMs, string phase,
            string cameraMode, string vehicleClass)
        {
            if (startSeconds < 0) startSeconds = Now();
            lastElapsed = Now() - startSeconds;
            frames++;
            if (!valid)
            {
                dropped++;
                return;
            }
            if (double.IsNaN(frameMs) || double.IsInfinity(frameMs)
                || frameMs <= 0)
            {
                dropped++;
                return;
            }
            var key = new SegmentKey
            {
                warmup = lastElapsed < WarmupSeconds,
                window = (int)(lastElapsed / WindowSeconds),
                phase = Phases.Contains(phase) ? phase : PhaseMenu,
                camera = cameraMode ?? "",
                vehicle = vehicleClass ?? ""
            };
            List<double> bucket;
            if (!segments.TryGetValue(key, out bucket))
            {
                if (segments.Count >= MaxSegments)
                {
                    Invalidate("segmentLimitExceeded");
                    dropped++;
                    return;
                }
                bucket = new List<double>();
                segments.Add(key, bucket);
            }
            if (samples >= MaxSamples)
            {
                Invalidate("sampleLimitExceeded");
                dropped++;
                return;
            }
            bucket.Add(frameMs);
            samples++;
            if (key.warmup) warmupSamples++;
            if (key.phase == PhasePaused) pausedMs += frameMs;
            if (key.phase == PhaseLoading) loadingMs += frameMs;
            lastCamera = key.camera;
            lastVehicle = key.vehicle;
        }

        public void MarkReportQueued()
        {
            reportStatus = "queued";
            reportError = "";
        }

        public void MarkReportWritten()
        {
            reportStatus = "written";
            reportError = "";
        }

        public void MarkReportFailed(string code)
        {
            reportStatus = "failed";
            reportError = string.IsNullOrEmpty(code) ? "writeError" : code;
        }

        // Nearest-rank percentile over a copy of the samples: the k-th
        // ordered value where k = ceil(p * n / 100). For n=100, p95 names
        // sorted[94] — never an interpolated or averaged value.
        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
            if (rank < 1) rank = 1;
            if (rank > sorted.Count) rank = sorted.Count;
            return sorted[rank - 1];
        }

        private static double? PercentileOrNull(List<double> samples,
            double p)
        {
            if (samples == null || samples.Count == 0) return null;
            var sorted = new List<double>(samples);
            sorted.Sort();
            return Percentile(sorted, p);
        }

        private List<double> Collect(bool warmup, string phase)
        {
            var all = new List<double>();
            foreach (var pair in segments)
                if (pair.Key.warmup == warmup
                    && (phase == null || pair.Key.phase == phase))
                    all.AddRange(pair.Value);
            return all;
        }

        private void Invalidate(string code)
        {
            valid = false;
            invalidReason = code;
        }

        private string EvidenceStatus()
        {
            if (!valid) return EvidenceInvalid;
            if (!renderingAvailable) return EvidenceInsufficient;
            return Collect(false, PhaseRacing).Count > 0
                ? EvidenceCollected : EvidenceInsufficient;
        }

        // The honest verdict path: invalid captures and captures without
        // steady-state racing evidence can never report "pass".
        public string Evaluate(double p95BudgetMs)
        {
            if (!valid) return EvidenceInvalid;
            if (!renderingAvailable) return EvidenceInsufficient;
            var racing = Collect(false, PhaseRacing);
            if (racing.Count == 0) return EvidenceInsufficient;
            racing.Sort();
            return Percentile(racing, 95.0) <= p95BudgetMs
                ? VerdictPass : VerdictFail;
        }

        private void FillIdentity(RenderPerformanceSummary target)
        {
            target.profile = profile;
            target.buildIdentity = buildIdentity;
            target.contentVersion = contentVersion;
            target.platform = platform;
            target.renderingAvailable = renderingAvailable;
            target.captureValid = valid;
            target.captureInvalidReason = invalidReason;
            target.frames = frames;
            target.observedSeconds = lastElapsed;
            target.warmupSeconds = Math.Min(lastElapsed, WarmupSeconds);
            target.pausedSeconds = pausedMs / 1000.0;
            target.loadingSeconds = loadingMs / 1000.0;
            target.processMemoryMetric = memoryMetric;
            target.processMemorySource = memorySource;
            target.processMemoryUnits = memoryUnits;
            target.reportStatus = reportStatus;
            target.reportError = reportError;
        }

        // Compact per-record summary. Percentile math runs at most once per
        // SummaryRefreshSeconds of capture clock; between refreshes the last
        // computed aggregates are republished with the live counters.
        public RenderPerformanceSummary Summary()
        {
            var now = Now();
            if (cachedSummary != null
                && now - lastSummarySeconds < SummaryRefreshSeconds
                && cachedSummary.captureValid == valid
                && cachedSummary.reportStatus == reportStatus)
            {
                var cached = cachedSummary.Copy();
                cached.frames = frames;
                cached.observedSeconds = lastElapsed;
                cached.pausedSeconds = pausedMs / 1000.0;
                cached.loadingSeconds = loadingMs / 1000.0;
                cached.reportStatus = reportStatus;
                cached.reportError = reportError;
                cached.captureValid = valid;
                cached.captureInvalidReason = invalidReason;
                return cached;
            }
            var racing = Collect(false, PhaseRacing);
            racing.Sort();
            var summary = new RenderPerformanceSummary();
            FillIdentity(summary);
            summary.racingSamples = racing.Count;
            summary.medianFrameMs = PercentileOrNull(racing, 50.0);
            summary.p95FrameMs = PercentileOrNull(racing, 95.0);
            summary.maxFrameMs = racing.Count > 0
                ? (double?)racing[racing.Count - 1] : null;
            var stalls = 0;
            foreach (var ms in racing)
                if (ms > StallThresholdMs) stalls++;
            summary.stallCount = stalls;
            cachedSummary = summary;
            lastSummarySeconds = now;
            return summary.Copy();
        }

        // Full report snapshot: aggregates plus every segment, deep-copied.
        public RenderPerformanceState Snapshot()
        {
            var state = new RenderPerformanceState();
            state.frameCount = frames;
            state.sampleCount = samples;
            state.observedSeconds = lastElapsed;
            state.warmupSeconds = Math.Min(lastElapsed, WarmupSeconds);
            state.warmupSamples = warmupSamples;
            state.pausedSeconds = pausedMs / 1000.0;
            state.loadingSeconds = loadingMs / 1000.0;
            state.droppedFrames = dropped;
            state.profile = profile;
            state.buildIdentity = buildIdentity;
            state.contentVersion = contentVersion;
            state.platform = platform;
            state.cameraMode = lastCamera;
            state.vehicleClass = lastVehicle;
            state.renderingAvailable = renderingAvailable;
            state.captureValid = valid;
            state.captureInvalidReason = invalidReason;
            state.processMemoryMetric = memoryMetric;
            state.processMemorySource = memorySource;
            state.processMemoryUnits = memoryUnits;
            state.reportStatus = reportStatus;
            state.reportError = reportError;

            var racing = Collect(false, PhaseRacing);
            racing.Sort();
            state.racingSamples = racing.Count;
            state.medianFrameMs = racing.Count > 0
                ? (double?)Percentile(racing, 50.0) : null;
            state.p95FrameMs = racing.Count > 0
                ? (double?)Percentile(racing, 95.0) : null;
            state.maxFrameMs = racing.Count > 0
                ? (double?)racing[racing.Count - 1] : null;
            var stalls = 0;
            foreach (var ms in racing)
                if (ms > StallThresholdMs) stalls++;
            state.stallCount = stalls;

            var warmupRacing = Collect(true, PhaseRacing);
            warmupRacing.Sort();
            state.warmupMedianFrameMs = warmupRacing.Count > 0
                ? (double?)Percentile(warmupRacing, 50.0) : null;
            state.warmupP95FrameMs = warmupRacing.Count > 0
                ? (double?)Percentile(warmupRacing, 95.0) : null;

            state.evidenceStatus = EvidenceStatus();

            var keys = new List<SegmentKey>(segments.Keys);
            keys.Sort((a, b) =>
            {
                var order = a.window.CompareTo(b.window);
                if (order != 0) return order;
                order = a.warmup.CompareTo(b.warmup);
                if (order != 0) return order;
                order = string.CompareOrdinal(a.phase, b.phase);
                if (order != 0) return order;
                order = string.CompareOrdinal(a.camera, b.camera);
                if (order != 0) return order;
                return string.CompareOrdinal(a.vehicle, b.vehicle);
            });
            foreach (var key in keys)
            {
                var bucket = segments[key];
                bucket.Sort();
                var segment = new RenderPerformanceSegment
                {
                    windowIndex = key.window,
                    warmup = key.warmup,
                    phase = key.phase,
                    cameraMode = key.camera,
                    vehicleClass = key.vehicle,
                    samples = bucket.Count,
                    medianFrameMs = Percentile(bucket, 50.0),
                    p95FrameMs = Percentile(bucket, 95.0),
                    maxFrameMs = bucket[bucket.Count - 1]
                };
                var segmentSeconds = 0.0;
                var segmentStalls = 0;
                foreach (var ms in bucket)
                {
                    segmentSeconds += ms / 1000.0;
                    if (ms > StallThresholdMs) segmentStalls++;
                }
                segment.seconds = segmentSeconds;
                segment.stalls = segmentStalls;
                state.segments.Add(segment);
            }
            return state;
        }
    }
}
