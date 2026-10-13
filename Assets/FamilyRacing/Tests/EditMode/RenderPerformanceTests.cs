using System;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using Newtonsoft.Json.Linq;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // M4 Task 2 measurement semantics: exact nearest-rank percentiles,
    // phase segmentation, bounded retention and the additive schema-5
    // presentation slice. These tests pin the Core capture engine the
    // Runtime recorder feeds; Unity-facing profile application is covered
    // by RenderProfilePlayTests.
    public sealed class RenderPerformanceTests
    {
        private double now;

        private RenderPerformanceCapture Capture(bool rendering = true)
        {
            return new RenderPerformanceCapture(() => now,
                profile: "pc", buildIdentity: "test-build",
                contentVersion: "visual-1", platform: "WindowsPlayer",
                renderingAvailable: rendering,
                processMemoryMetric: "process.workingSet",
                processMemorySource: "harness", processMemoryUnits: "bytes");
        }

        // Warm-up is the first 60 s OF THE CAPTURE (elapsed since the first
        // recorded frame), so a test that wants steady-state samples primes
        // one menu frame at t=0 then advances the clock past the prefix.
        private void PastWarmup(RenderPerformanceCapture capture)
        {
            capture.RecordFrame(16.0, "menu", "none", "");
            now += RenderPerformanceCapture.WarmupSeconds + 1.0;
        }

        private void Record(RenderPerformanceCapture capture, double frameMs,
            string phase = "racing", double advance = 0.02)
        {
            capture.RecordFrame(frameMs, phase, "chase", "car-01");
            now += advance;
        }

        [Test]
        public void KnownSamplesProduceExpectedPercentiles()
        {
            var capture = Capture();
            PastWarmup(capture);
            for (var i = 1; i <= 100; i++)
                Record(capture, i);
            var state = capture.Snapshot();
            Assert.That(state.captureValid, Is.True);
            Assert.That(state.racingSamples, Is.EqualTo(100));
            // Nearest rank: p95 of 1..100 is sorted[94] = 95 — never an
            // interpolated value; median is sorted[49] = 50, max is 100.
            Assert.That(state.p95FrameMs, Is.EqualTo(95.0));
            Assert.That(state.medianFrameMs, Is.EqualTo(50.0));
            Assert.That(state.maxFrameMs, Is.EqualTo(100.0));
            Assert.That(state.stallCount, Is.EqualTo(0),
                "stalls are strictly over 100 ms");
            Assert.That(state.evidenceStatus, Is.EqualTo("collected"));
        }

        [Test]
        public void PausedFramesDoNotCountAsRacing()
        {
            var capture = Capture();
            PastWarmup(capture);
            for (var i = 0; i < 10; i++) Record(capture, 20.0);
            // A pause is expensive per frame but must not pollute the racing
            // percentile the qualification gate reads.
            for (var i = 0; i < 5; i++)
                Record(capture, 500.0, phase: "paused", advance: 0.5);
            var state = capture.Snapshot();
            Assert.That(state.racingSamples, Is.EqualTo(10));
            Assert.That(state.p95FrameMs, Is.EqualTo(20.0));
            Assert.That(state.maxFrameMs, Is.EqualTo(20.0));
            Assert.That(state.stallCount, Is.EqualTo(0),
                "paused-phase intervals are not racing stalls");
            Assert.That(state.pausedSeconds, Is.EqualTo(2.5).Within(0.001));
            var paused = state.segments.Find(s => s.phase == "paused");
            Assert.That(paused, Is.Not.Null);
            Assert.That(paused.samples, Is.EqualTo(5));
            Assert.That(paused.stalls, Is.EqualTo(5),
                "segment-level stall counting still sees the paused cost");
        }

        [Test]
        public void MissingSamplesCannotPass()
        {
            var capture = Capture();
            PastWarmup(capture);
            // An empty capture cannot be a passing measurement, and an
            // invalid or headless one cannot either.
            Assert.That(capture.Evaluate(40.0),
                Is.Not.EqualTo("pass"));
            Assert.That(capture.Evaluate(40.0),
                Is.EqualTo("insufficientData"));
            var state = capture.Snapshot();
            Assert.That(state.p95FrameMs, Is.Null,
                "no racing samples means no p95 — never a zero");
            Assert.That(state.evidenceStatus, Is.EqualTo("insufficientData"));
            // Pause-only evidence still cannot pass.
            Record(capture, 16.0, phase: "paused");
            Assert.That(capture.Evaluate(40.0),
                Is.EqualTo("insufficientData"));
            // And a headless run can never pass even with racing samples.
            var headless = Capture(rendering: false);
            for (var i = 0; i < 10; i++)
                headless.RecordFrame(16.0, "racing", "chase", "car-01");
            Assert.That(headless.Evaluate(40.0),
                Is.EqualTo("insufficientData"));
            Assert.That(headless.Snapshot().renderingAvailable, Is.False);
        }

        [Test]
        public void SampleRetentionIsBounded()
        {
            var capture = Capture();
            PastWarmup(capture);   // priming frame occupies one slot
            for (var i = 0; i < RenderPerformanceCapture.MaxSamples - 1; i++)
                capture.RecordFrame(16.0, "racing", "chase", "car-01");
            Assert.That(capture.Snapshot().captureValid, Is.True);
            // One frame past the bound invalidates the whole capture rather
            // than silently truncating evidence.
            capture.RecordFrame(16.0, "racing", "chase", "car-01");
            var state = capture.Snapshot();
            Assert.That(state.captureValid, Is.False);
            Assert.That(state.captureInvalidReason,
                Is.EqualTo("sampleLimitExceeded"));
            Assert.That(state.sampleCount,
                Is.EqualTo(RenderPerformanceCapture.MaxSamples));
            Assert.That(capture.Evaluate(40.0), Is.EqualTo("invalid"));
        }

        [Test]
        public void SchemaFiveRoundTripIncludesPresentation()
        {
            Assert.That(NetworkSettings.StateSchemaVersion, Is.EqualTo(5),
                "schema 5 is the presentation-observation epoch");
            var state = new FoundationState
            {
                presentation = new PresentationState
                {
                    renderProfileId = "tablet",
                    renderScale = 0.75f,
                    targetFrameRate = 30,
                    renderingAvailable = true,
                    performance = new RenderPerformanceSummary
                    {
                        profile = "tablet",
                        buildIdentity = "test-build",
                        racingSamples = 42,
                        p95FrameMs = 33.5,
                        reportStatus = "written"
                    }
                }
            };
#if UNITY_5_3_OR_NEWER
            var json = JObject.FromObject(state).ToString(
                Newtonsoft.Json.Formatting.None);
            var roundTrip = JObject.Parse(json)
                .ToObject<FoundationState>();
#else
            var options = new JsonSerializerOptions { IncludeFields = true };
            var json = JsonSerializer.Serialize(state, options);
            var roundTrip = JsonSerializer.Deserialize<FoundationState>(
                json, options);
#endif
            Assert.That(roundTrip.stateSchemaVersion, Is.EqualTo(5));
            Assert.That(roundTrip.presentation.renderProfileId,
                Is.EqualTo("tablet"));
            Assert.That(roundTrip.presentation.renderScale,
                Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(roundTrip.presentation.targetFrameRate,
                Is.EqualTo(30));
            Assert.That(roundTrip.presentation.renderingAvailable, Is.True);
            Assert.That(roundTrip.presentation.performance, Is.Not.Null);
            Assert.That(roundTrip.presentation.performance.racingSamples,
                Is.EqualTo(42));
            Assert.That(roundTrip.presentation.performance.p95FrameMs,
                Is.EqualTo(33.5).Within(0.0001));
            // Deep-copy handling: mutating a published copy never rewrites
            // the live presentation slice.
            var copy = state.presentation.Copy();
            copy.performance.racingSamples = -1;
            copy.renderProfileId = "pc";
            Assert.That(state.presentation.performance.racingSamples,
                Is.EqualTo(42));
            Assert.That(state.presentation.renderProfileId,
                Is.EqualTo("tablet"));
        }
    }
}
