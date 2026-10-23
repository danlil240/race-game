using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class LongRouteTests
    {
        private static CourseDefinition Road(bool open = true)
        {
            var course = new CourseDefinition { id = "long-road", isOpenRoute = open, spawnZ = -12 };
            for (int i = 0; i < 3; i++)
            {
                course.checkpoints.Add(new CheckpointGate { id = "gate-" + i, z = i * 100,
                    dirZ = 1, halfWidth = 8, recoverZ = i * 100 - 4 });
                course.route.Add(new RouteWaypoint { z = i * 100, radius = 5 });
            }
            return course;
        }
        private static EventDefinition Event(int laps = 1) => new EventDefinition {
            id = "long-event", courseId = "long-road", laps = laps,
            vehicleIds = new List<string> { "car-01" } };
        private static CheckpointResult Cross(CourseDefinition road, CheckpointState state, float z, bool reverse = false)
            => CheckpointProgress.Advance(road, state,
                new MotorPose { z = z + (reverse ? 1 : -1) },
                new MotorPose { z = z + (reverse ? -1 : 1) });

        [Test] public void JourneyFinishesAtLastGateWithoutReturningToStart()
        {
            var road = Road(); var state = CheckpointProgress.Create(road, 1);
            Cross(road, state, 100);
            Assert.That(state.finished, Is.False);
            var result = Cross(road, state, 200);
            Assert.That(state.finished, Is.True);
            Assert.That(state.lap, Is.EqualTo(1));
            Assert.That(result.events.FindAll(e => e.kind == "finish").Count, Is.EqualTo(1));
            Assert.That(Cross(road, state, 200).events, Is.Empty);
        }
        [Test] public void JourneyRejectsSkippedAndReverseFinalGate()
        {
            var road = Road(); var state = CheckpointProgress.Create(road, 1);
            Cross(road, state, 200); Assert.That(state.finished, Is.False);
            Cross(road, state, 100); Cross(road, state, 200, true);
            Assert.That(state.finished, Is.False);
            Cross(road, state, 200); Assert.That(state.finished, Is.True);
        }
        [Test] public void CircuitStillNeedsTwoCompleteReturnsToStart()
        {
            var road = Road(false); var state = CheckpointProgress.Create(road, 2);
            for (int lap = 0; lap < 2; lap++)
            {
                Cross(road, state, 100); Cross(road, state, 200);
                Assert.That(state.finished, Is.False);
                Cross(road, state, 0);
                Assert.That(state.finished, Is.EqualTo(lap == 1));
            }
        }
        [Test] public void RouteTopologyCopiesAndChangesCompatibilityFingerprint()
        {
            var road = Road(); Assert.That(road.Copy().isOpenRoute, Is.True);
            var a = DrivingContentCatalog.Create("long", new[] { VehicleTuning.Car01() }, new[] { road }, new[] { Event() });
            var closed = road.Copy(); closed.isOpenRoute = false;
            var b = DrivingContentCatalog.Create("long", new[] { VehicleTuning.Car01() }, new[] { closed }, new[] { Event() });
            Assert.That(a.CompatibilityFingerprint, Is.Not.EqualTo(b.CompatibilityFingerprint));
        }
        [Test] public void JourneyCannotAuthorMultipleLaps()
        {
            Assert.Throws<ArgumentException>(() => DrivingContentCatalog.Create("long",
                new[] { VehicleTuning.Car01() }, new[] { Road() }, new[] { Event(2) }));
            Assert.Throws<ArgumentException>(() => new DrivingSession("r", VehicleTuning.Car01(), Road(), Event(2), "long", "start"));
        }
        [TestCase(197f)] [TestCase(199f)]
        public void AiDoesNotWrapBackToStartNearJourneyEndpoint(float z)
        {
            var state = new DrivingSessionState { vehicleId = "car-01", phase = "running", nextCheckpoint = 2,
                motor = new MotorState { z = z, heading = 0, grounded = true, speed = 20 } };
            var input = new AiDriver().Sample(state, Road());
            Assert.That(input.Steering, Is.EqualTo(0).Within(.01), "Endpoint pursuit must stay forward");
            Assert.That(input.Brake, Is.EqualTo(0).Within(.01), "No fictitious corner back to the start");
        }
        [Test] public void FinishedJourneyAiCoastsWithoutSteeringOrBoost()
        {
            var state = new DrivingSessionState { phase = "finished", motor = new MotorState { z = 201 } };
            var input = new AiDriver().Sample(state, Road());
            Assert.That(input.Steering, Is.Zero); Assert.That(input.Boost, Is.False);
        }
        [Test] public void SnapshotCopiesJourneyMetadata()
        {
            var snapshot = new DrivingSession("r", VehicleTuning.Car01(), Road(), Event(), "long", "start").Snapshot();
            Assert.That(snapshot.isOpenRoute, Is.True);
            Assert.That(snapshot.routeProgress, Is.Zero);
            Assert.That(snapshot.Copy().isOpenRoute, Is.True);
        }
        [Test] public void LongEventGraceIsValidatedCopiedAndFingerprintBound()
        {
            var evt = Event(); Assert.That(evt.finishGraceSeconds, Is.EqualTo(60));
            evt.finishGraceSeconds = 240; Assert.That(evt.Copy().finishGraceSeconds, Is.EqualTo(240));
            var catalog = DrivingContentCatalog.Create("tour",new[]{VehicleTuning.Car01()},new[]{Road()},new[]{evt});
            var before = catalog.CompatibilityFingerprint;
            evt.finishGraceSeconds = 60;
            Assert.That(catalog.CompatibilityFingerprint, Is.Not.EqualTo(before));
            evt.finishGraceSeconds = 0; Assert.That(evt.Validate(out _), Is.False);
        }
    }
}
