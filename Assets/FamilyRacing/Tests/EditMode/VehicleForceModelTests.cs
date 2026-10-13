using System;
using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Physical oracles for the pure scalar force model the runtime backend
    // evaluates every substep. Suspension is a nonnegative support that can
    // push but never pull; the (longitudinal, lateral) tire pair shares one
    // mu*N friction budget; the brake force opposes actual contact travel
    // and is capped so it cannot reverse longitudinal velocity in a step.
    // Everything here is Unity-free so the same tests run in the dotnet
    // Core harness.
    public sealed class VehicleForceModelTests
    {
        [Test]
        public void SuspensionCannotPullGround()
        {
            Assert.That(VehicleForceModel.SuspensionForce(-0.1f, 0, 40000, 3500, 20000), Is.Zero);
            // Damper rebound at full extension is still only a push spring.
            Assert.That(VehicleForceModel.SuspensionForce(0f, -5f, 40000, 3500, 20000), Is.Zero);
            // Fast extension outweighing the spring clamp lands on zero,
            // never a negative support.
            Assert.That(VehicleForceModel.SuspensionForce(0.05f, -10f, 40000, 3500, 20000),
                Is.Zero);
        }

        [Test]
        public void SuspensionIsSpringPlusDamperBelowCap()
        {
            // N = k*x + c*xDot while inside the authored support cap.
            Assert.That(VehicleForceModel.SuspensionForce(0.1f, 0.5f, 40000, 3500, 20000),
                Is.EqualTo(5750.0).Within(0.01));
            // Compressing harder adds damper force.
            Assert.That(VehicleForceModel.SuspensionForce(0.1f, 1f, 40000, 3500, 20000),
                Is.GreaterThan(VehicleForceModel.SuspensionForce(0.1f, 0f, 40000, 3500, 20000)));
        }

        [Test]
        public void SuspensionSaturatesAtAuthoredMaxLoad()
        {
            Assert.That(VehicleForceModel.SuspensionForce(1f, 5f, 40000, 3500, 20000),
                Is.EqualTo(20000f));
            // Enormous compression still lands exactly on the cap.
            Assert.That(VehicleForceModel.SuspensionForce(1e6f, 0f, 40000, 3500, 20000),
                Is.EqualTo(20000f));
        }

        [Test]
        public void SuspensionContactLossAndReacquisition()
        {
            // Contact lost means no compression travel used: zero support.
            Assert.That(VehicleForceModel.SuspensionForce(0f, 0f, 40000, 3500, 20000),
                Is.Zero);
            // Reacquired contact pushes again on the very next evaluation.
            Assert.That(VehicleForceModel.SuspensionForce(0.1f, 0f, 40000, 3500, 20000),
                Is.EqualTo(4000.0).Within(0.01));
        }

        [Test]
        public void CombinedGripSharesOneBudget()
        {
            var force = VehicleForceModel.LimitTireForces(3000, 4000, 2500, 1);
            Assert.That(force.longitudinal, Is.EqualTo(1500).Within(0.01));
            Assert.That(force.lateral, Is.EqualTo(2000).Within(0.01));
            // Demand already inside the budget passes through unscaled —
            // grip is shared, not spent twice.
            var inside = VehicleForceModel.LimitTireForces(300f, 400f, 2500, 1);
            Assert.That(inside.longitudinal, Is.EqualTo(300f).Within(0.01));
            Assert.That(inside.lateral, Is.EqualTo(400f).Within(0.01));
        }

        [Test]
        public void CombinedGripNeverExceedsFrictionTimesLoad()
        {
            // Large slip demand lands exactly on the mu*N ellipse and keeps
            // its direction: braking cannot secretly buy extra cornering.
            var force = VehicleForceModel.LimitTireForces(1e6f, -1e6f, 3000f, 1.1f);
            var limit = 1.1 * 3000.0;
            var magnitude = Math.Sqrt(
                (double)force.longitudinal * force.longitudinal
                + (double)force.lateral * force.lateral);
            Assert.That(magnitude, Is.EqualTo(limit).Within(0.5));
            Assert.That(force.longitudinal, Is.GreaterThan(0f));
            Assert.That(force.lateral, Is.LessThan(0f));
            // The same scaling rule holds on a banked contact: the normal
            // load scalar is the whole basis story for these functions.
            var banked = VehicleForceModel.LimitTireForces(1e6f, -1e6f, 6000f, 1.1f);
            var bankedMagnitude = Math.Sqrt(
                (double)banked.longitudinal * banked.longitudinal
                + (double)banked.lateral * banked.lateral);
            Assert.That(bankedMagnitude, Is.EqualTo(2.0 * limit).Within(1.0));
        }

        [Test]
        public void NoNormalLoadMeansNoTraction()
        {
            var airborne = VehicleForceModel.LimitTireForces(5000f, 5000f, 0f, 1.1f);
            Assert.That(airborne.longitudinal, Is.Zero);
            Assert.That(airborne.lateral, Is.Zero);
            // A negative load is a degenerate contact, not negative grip.
            var negative = VehicleForceModel.LimitTireForces(5000f, 5000f, -100f, 1.1f);
            Assert.That(negative.longitudinal, Is.Zero);
            Assert.That(negative.lateral, Is.Zero);
            // Zero friction is a zero budget even under real load.
            var slick = VehicleForceModel.LimitTireForces(5000f, 5000f, 3000f, 0f);
            Assert.That(slick.longitudinal, Is.Zero);
            Assert.That(slick.lateral, Is.Zero);
        }

        [Test]
        public void BrakeCannotReverseVelocity()
        {
            Assert.That(VehicleForceModel.BrakeForce(10, 2000, 100, 1), Is.EqualTo(-1000));
            // Over the substep sweep the applied velocity change may erase
            // the contact speed but never carry it past zero.
            var speeds = new[] { 0.01f, 0.5f, 3f, 10f, 40f };
            var requests = new[] { 1f, 500f, 12000f, 1e6f };
            var masses = new[] { 100f, 220f, 1200f };
            var steps = new[] { 1f / 60f, 1f / 30f, 1f };
            foreach (var speed in speeds)
            foreach (var request in requests)
            foreach (var mass in masses)
            foreach (var dt in steps)
            {
                var brake = VehicleForceModel.BrakeForce(speed, request, mass, dt);
                var velocityChange = brake * dt / mass;
                Assert.That(Math.Abs(velocityChange), Is.LessThanOrEqualTo(speed + 1e-4f),
                    $"speed={speed} request={request} mass={mass} dt={dt}");
                Assert.That(brake, Is.LessThanOrEqualTo(0f),
                    "brake force must oppose forward travel");
            }
        }

        [Test]
        public void BrakeOpposesActualTravelDirection()
        {
            // Rolling backwards: the opposing force flips sign and keeps
            // the same magnitude as the symmetric forward case.
            var forward = VehicleForceModel.BrakeForce(5f, 500f, 100f, 1f / 60f);
            var reverse = VehicleForceModel.BrakeForce(-5f, 500f, 100f, 1f / 60f);
            Assert.That(forward, Is.LessThan(0f));
            Assert.That(reverse, Is.GreaterThan(0f));
            Assert.That(reverse, Is.EqualTo(-forward).Within(1e-4));
        }

        [Test]
        public void BrakeAtRestOrWithoutDemandProducesZero()
        {
            // Zero contact speed: no travel direction to oppose. Holding on
            // a slope is a separate contact demand upstream of the grip
            // budget, not this force.
            Assert.That(VehicleForceModel.BrakeForce(0f, 2000f, 100f, 1f / 60f), Is.Zero);
            Assert.That(VehicleForceModel.BrakeForce(10f, 0f, 100f, 1f / 60f), Is.Zero);
            // Degenerate steps or masses degrade to zero rather than NaN.
            Assert.That(VehicleForceModel.BrakeForce(10f, 2000f, 100f, 0f), Is.Zero);
            Assert.That(VehicleForceModel.BrakeForce(10f, 2000f, 0f, 1f / 60f), Is.Zero);
        }

        [Test]
        public void BrakeForceIsEnergyOpposing()
        {
            // The brake force always points opposite the direction of
            // contact travel and is bounded by the no-reversal cap.
            var brake = VehicleForceModel.BrakeForce(8f, 3000f, 1200f, 1f / 60f);
            Assert.That(brake, Is.LessThan(0f));
            Assert.That(Math.Abs(brake) * (1f / 60f) / 1200f,
                Is.LessThanOrEqualTo(8f + 1e-4f));
        }

        [Test]
        public void ForceOutputsStayFiniteAcrossParameterSweep()
        {
            var speeds = new[] { -1e6f, -10f, 0f, 10f, 1e6f, float.NaN, float.PositiveInfinity };
            var magnitudes = new[] { -1f, 0f, 1f, 1e4f, 1e30f, float.NaN, float.PositiveInfinity };
            foreach (var compression in magnitudes)
            foreach (var compressionSpeed in speeds)
            {
                AssertFinite(VehicleForceModel.SuspensionForce(
                    compression, compressionSpeed, 40000f, 3500f, 20000f));
            }
            // Degenerate tuning values never produce NaN/Infinity either.
            AssertFinite(VehicleForceModel.SuspensionForce(0.1f, 0f, 0f, 0f, 0f));
            AssertFinite(VehicleForceModel.SuspensionForce(0.1f, 0f, -1f, -1f, -1f));

            foreach (var demand in magnitudes)
            foreach (var load in magnitudes)
            {
                // Zero load and large slip are explicitly in the sweep.
                var tire = VehicleForceModel.LimitTireForces(demand, -demand, load, 1.1f);
                AssertFinite(tire.longitudinal);
                AssertFinite(tire.lateral);
            }
            foreach (var speed in speeds)
            foreach (var request in magnitudes)
            foreach (var dt in magnitudes)
            {
                AssertFinite(VehicleForceModel.BrakeForce(speed, request, 300f, dt));
                AssertFinite(VehicleForceModel.BrakeForce(speed, request, -1f, 1f / 60f));
            }
        }

        private static void AssertFinite(float value)
        {
            Assert.That(float.IsNaN(value), Is.False);
            Assert.That(float.IsInfinity(value), Is.False);
        }
    }
}
