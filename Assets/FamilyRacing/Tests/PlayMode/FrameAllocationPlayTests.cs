using System;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    public sealed class FrameAllocationPlayTests
    {
        private GameObject host;
        private FoundationApp app;
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo UpdateFrame = typeof(FoundationApp).GetMethod("Update", Private);
        private static readonly FieldInfo Accumulator = typeof(FoundationApp).GetField("accumulator", Private);

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("frame-allocation-test");
            host.SetActive(false); // Never open the user's profile store.
            app = host.AddComponent<FoundationApp>();
            var service = new FoundationService(new ProfileStore(
                Path.Combine(Path.GetTempPath(), "unused-frame-test-" + Guid.NewGuid().ToString("N")),
                new UnityProfileSerializer()), "en", "frame-allocation-test");
            typeof(FoundationApp).GetField("service", Private).SetValue(app, service);
        }

        [TearDown]
        public void TearDown() { Object.DestroyImmediate(host); }

        [Test]
        public void ReadingScreenForInputDoesNotAllocateStateSnapshots()
        {
            var screen = app.CurrentScreen;
            Assert.That(() => { screen = app.CurrentScreen; },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "Input routing must not copy the full state just to read the screen.");
            Assert.That(screen, Is.EqualTo("profiles"));
        }
        [Test]
        public void UnobservedCatchUpTicksDoNotAllocateFullSnapshots()
        {
            MeasureFrame(0); MeasureFrame(3); // Warm runtime/reflection paths.
            var noTicks = MeasureFrame(0);
            var catchUp = MeasureFrame(3);
            Assert.That(catchUp - noTicks, Is.LessThan(10),
                "Without a validation bridge, catch-up ticks must not construct unused observation snapshots.");
        }

        [Test]
        public void UnobservedInputRefreshDoesNotAllocateBoundarySnapshots()
        {
            MeasureCommand(false); MeasureCommand(true);
            var direct = MeasureCommand(false);
            var dispatched = MeasureCommand(true);
            Assert.That(dispatched - direct, Is.LessThan(5),
                "The recurring input refresh must not copy observation state without a validation consumer.");
        }

        private long MeasureCommand(bool dispatch)
        {
            var service = (FoundationService)typeof(FoundationApp)
                .GetField("service", Private).GetValue(app);
            var command = new FamilyRacing.Core.Automation.GameCommand(FoundationApp.ProtocolVersion, "frame-allocation-test",
                Guid.NewGuid().ToString("N"), "local", "input.set",
                new System.Collections.Generic.Dictionary<string, string> { { "steering", "0" } });
            var method = typeof(FoundationApp).GetMethod("ExecuteAndEmit", Private);
            var args = new object[] { command };
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false; recorder.FilterToCurrentThread(); recorder.enabled = true;
            try
            {
                if (dispatch) method.Invoke(app, args);
                else service.Execute(command);
            }
            finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
            return recorder.sampleBlockCount;
        }

        private long MeasureFrame(int ticks)
        {
            Accumulator.SetValue(app, ticks / FoundationApp.TickRate - Time.unscaledDeltaTime + 0.00001f);
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try { UpdateFrame.Invoke(app, null); }
            finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
            return recorder.sampleBlockCount;
        }
    }
}

