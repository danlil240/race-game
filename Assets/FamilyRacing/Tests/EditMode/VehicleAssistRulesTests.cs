using System;
using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Bounded-assist oracles. SteeringTarget maps a [-1,1] demand onto
    // road-wheel radians whose authority tapers from steerMaxRadians at
    // rest down to steerHighSpeedRadians at steerReferenceSpeed. The
    // motorcycle lean target follows the steady-turn demand tan(lean) =
    // v*yawRate/g and stays inside the authored 45-degree bound; torque
    // application itself remains in Runtime.
    public sealed class VehicleAssistRulesTests
    {
        [Test]
        public void SteeringAndLeanAreBounded()
        {
            var car = VehiclePhysicsTuning.Car01();
            var speeds = new[] { 0f, 5f, 10f, 20f, 50f, 200f };
            var inputs = new[] { -1f, -0.4f, 0f, 0.4f, 1f };
            foreach (var speed in speeds)
            foreach (var input in inputs)
            {
                var target = VehicleAssistRules.SteeringTarget(input, speed, car);
                Assert.That(Math.Abs(target),
                    Is.LessThanOrEqualTo(car.steerMaxRadians + 1e-6f),
                    $"speed={speed} input={input}");
                Assert.That(Math.Abs(target),
                    Is.GreaterThanOrEqualTo(car.steerHighSpeedRadians * Math.Abs(input) - 1e-6f),
                    "authority never drops below the authored high-speed floor");
            }
            var bike = VehiclePhysicsTuning.Motorcycle01();
            var yawRates = new[] { -3f, -1f, 0f, 1f, 3f };
            foreach (var speed in speeds)
            foreach (var yawRate in yawRates)
            {
                var lean = VehicleAssistRules.BikeLeanTarget(
                    speed, yawRate, bike.assistMaxLeanRadians);
                Assert.That(Math.Abs(lean),
                    Is.LessThanOrEqualTo(bike.assistMaxLeanRadians + 1e-6f),
                    $"speed={speed} yawRate={yawRate}");
            }
        }

        [Test]
        public void LowSpeedSteeringUsesFullAuthority()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            Assert.That(VehicleAssistRules.SteeringTarget(1f, 0f, tuning),
                Is.EqualTo(tuning.steerMaxRadians).Within(1e-6));
            Assert.That(VehicleAssistRules.SteeringTarget(-1f, 0f, tuning),
                Is.EqualTo(-tuning.steerMaxRadians).Within(1e-6));
            // Partial input scales linearly at rest.
            Assert.That(VehicleAssistRules.SteeringTarget(0.5f, 0f, tuning),
                Is.EqualTo(0.5f * tuning.steerMaxRadians).Within(1e-6));
        }

        [Test]
        public void SteeringAuthorityTapersToHighSpeedFloor()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            // At and beyond the reference speed the floor applies.
            Assert.That(
                VehicleAssistRules.SteeringTarget(1f, tuning.steerReferenceSpeed, tuning),
                Is.EqualTo(tuning.steerHighSpeedRadians).Within(1e-6));
            Assert.That(
                VehicleAssistRules.SteeringTarget(1f, tuning.steerReferenceSpeed * 3f, tuning),
                Is.EqualTo(tuning.steerHighSpeedRadians).Within(1e-6));
            // Authority decreases monotonically between rest and reference.
            var previous = float.MaxValue;
            for (var speed = 0f; speed <= tuning.steerReferenceSpeed; speed += 2f)
            {
                var authority = VehicleAssistRules.SteeringTarget(1f, speed, tuning);
                Assert.That(authority, Is.LessThanOrEqualTo(previous + 1e-6f));
                previous = authority;
            }
            // Reversing uses speed magnitude, not direction.
            Assert.That(
                VehicleAssistRules.SteeringTarget(1f, -tuning.steerReferenceSpeed, tuning),
                Is.EqualTo(tuning.steerHighSpeedRadians).Within(1e-6));
        }

        [Test]
        public void SteeringInputIsClampedAndSanitized()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            Assert.That(VehicleAssistRules.SteeringTarget(5f, 0f, tuning),
                Is.EqualTo(tuning.steerMaxRadians).Within(1e-6));
            Assert.That(VehicleAssistRules.SteeringTarget(-5f, 0f, tuning),
                Is.EqualTo(-tuning.steerMaxRadians).Within(1e-6));
            Assert.That(VehicleAssistRules.SteeringTarget(float.NaN, 0f, tuning), Is.Zero);
            Assert.That(VehicleAssistRules.SteeringTarget(0f, 0f, tuning), Is.Zero);
            // Missing tuning cannot author a steering angle.
            Assert.That(VehicleAssistRules.SteeringTarget(1f, 0f, null), Is.Zero);
        }

        [Test]
        public void BikeLeanFollowsSteadyTurnDemand()
        {
            // tan(lean) = v*yawRate/g under the backend's standard gravity.
            var expected = (float)Math.Atan(10.0 * 0.5 / VehiclePhysicsTuning.StandardGravity);
            Assert.That(VehicleAssistRules.BikeLeanTarget(10f, 0.5f, 0.7854f),
                Is.EqualTo(expected).Within(1e-5));
            // A mirrored turn mirrors the lean.
            Assert.That(VehicleAssistRules.BikeLeanTarget(10f, -0.5f, 0.7854f),
                Is.EqualTo(-expected).Within(1e-5));
            // A stationary bike targets upright — low-speed balance help is
            // the PD controller's job downstream of this target.
            Assert.That(VehicleAssistRules.BikeLeanTarget(0f, 0.5f, 0.7854f), Is.Zero);
        }

        [Test]
        public void BikeLeanSaturatesAtAuthoredBound()
        {
            // Violent demand still cannot lean past the authored 45 degrees.
            Assert.That(VehicleAssistRules.BikeLeanTarget(40f, 3f, 0.7854f),
                Is.EqualTo(0.7854f).Within(1e-6));
            Assert.That(VehicleAssistRules.BikeLeanTarget(40f, -3f, 0.7854f),
                Is.EqualTo(-0.7854f).Within(1e-6));
        }

        [Test]
        public void AssistOutputsStayFiniteAcrossParameterSweep()
        {
            var car = VehiclePhysicsTuning.Car01();
            var bike = VehiclePhysicsTuning.Motorcycle01();
            var values = new[] { -1e6f, -20f, -1f, 0f, 1f, 20f, 1e6f,
                float.NaN, float.PositiveInfinity };
            foreach (var input in values)
            foreach (var speed in values)
            {
                AssertFinite(VehicleAssistRules.SteeringTarget(input, speed, car));
                AssertFinite(VehicleAssistRules.SteeringTarget(input, speed, bike));
                AssertFinite(VehicleAssistRules.BikeLeanTarget(
                    speed, input, bike.assistMaxLeanRadians));
                AssertFinite(VehicleAssistRules.BikeLeanTarget(speed, input, 0f));
            }
        }

        private static void AssertFinite(float value)
        {
            Assert.That(float.IsNaN(value), Is.False);
            Assert.That(float.IsInfinity(value), Is.False);
        }
    }
}
