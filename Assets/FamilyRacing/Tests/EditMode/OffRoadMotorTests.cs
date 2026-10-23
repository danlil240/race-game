using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class OffRoadMotorTests
    {
        private sealed class SurfaceWorld : IMotorWorld
        {
            public bool rough;
            public GroundHit ProbeGround(MotorPose p, float distance) => new GroundHit
                { hit = true, y = 0, normalY = 1, offRoad = rough };
            public SweepHit SweepBody(MotorPose p, float x, float y, float z, float w, float l, float h) =>
                new SweepHit { fraction = 1 };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RoughGroundSlowsFullSpeedEvenWithBoost(bool boost)
        {
            var tuning = VehicleTuning.Car01();
            var state = new MotorState { grounded = true, speed = tuning.maxSpeed, boostCharge = 1 };
            var world = new SurfaceWorld { rough = true };
            for (var i = 0; i < 60; i++)
                state = ArcadeMotor.Step(state, new DriverInput(0, false, boost, false, false, false, false), tuning, world, 1f / 30).state;
            Assert.That(state.speed, Is.LessThanOrEqualTo(tuning.maxSpeed * .4f));
            Assert.That(state.speed, Is.GreaterThan(1), "The player must still be able to steer back onto the road");
        }

        [Test]
        public void RejoiningRoadRestoresNormalAcceleration()
        {
            var tuning = VehicleTuning.Car01();
            var state = new MotorState { grounded = true };
            var world = new SurfaceWorld { rough = true };
            for (var i = 0; i < 180; i++) state = ArcadeMotor.Step(state, new DriverInput(), tuning, world, 1f / 30).state;
            float roughSpeed = state.speed;
            world.rough = false;
            for (var i = 0; i < 120; i++) state = ArcadeMotor.Step(state, new DriverInput(), tuning, world, 1f / 30).state;
            Assert.That(state.speed, Is.GreaterThan(roughSpeed * 1.8f));
            Assert.That(state.speed, Is.EqualTo(tuning.maxSpeed).Within(.01f));
        }
    }
}
