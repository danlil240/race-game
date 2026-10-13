using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class VehiclePhysicsTuningTests
    {
        [Test]
        public void SpecPresetsValidate()
        {
            var car = VehiclePhysicsTuning.Car01();
            Assert.That(car.Validate(out var carError), Is.True, carError);
            var bike = VehiclePhysicsTuning.Motorcycle01();
            Assert.That(bike.Validate(out var bikeError), Is.True, bikeError);
        }

        [Test]
        public void CopyDoesNotAliasWheels()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            var copy = tuning.Copy();
            copy.wheels[0].mountX = 99f;
            Assert.That(tuning.wheels[0].mountX, Is.Not.EqualTo(99f));
        }

        [Test]
        public void NegativeAndNonFiniteMassRejected()
        {
            var invalidMass = VehiclePhysicsTuning.Car01();
            invalidMass.massKg = -10f;
            Assert.That(invalidMass.Validate(out _), Is.False);
            invalidMass = VehiclePhysicsTuning.Car01();
            invalidMass.massKg = 0f;
            Assert.That(invalidMass.Validate(out _), Is.False);
            invalidMass = VehiclePhysicsTuning.Car01();
            invalidMass.massKg = float.NaN;
            Assert.That(invalidMass.Validate(out _), Is.False);
            invalidMass = VehiclePhysicsTuning.Car01();
            invalidMass.massKg = float.PositiveInfinity;
            Assert.That(invalidMass.Validate(out _), Is.False);
        }

        [Test]
        public void ZeroInertiaMultiplierRejected()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            tuning.inertiaMultiplierY = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.inertiaMultiplierZ = float.NaN;
            Assert.That(tuning.Validate(out _), Is.False);
        }

        [Test]
        public void InvalidWheelCountRejected()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            tuning.wheels = null;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.wheels = new VehicleWheelSpec[0];
            Assert.That(tuning.Validate(out _), Is.False);
            // Supported layouts are two inline contacts or four wheels.
            tuning = VehiclePhysicsTuning.Car01();
            tuning.wheels = new VehicleWheelSpec[3];
            for (var i = 0; i < tuning.wheels.Length; i++)
                tuning.wheels[i] = new VehicleWheelSpec();
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.wheels = new VehicleWheelSpec[1];
            tuning.wheels[0] = new VehicleWheelSpec();
            Assert.That(tuning.Validate(out _), Is.False);
        }

        [Test]
        public void InvalidWheelLayoutRejected()
        {
            // A four-wheel chassis with zero track width cannot balance.
            var tuning = VehiclePhysicsTuning.Car01();
            foreach (var wheel in tuning.wheels) wheel.mountX = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            // A four-wheel chassis with zero wheelbase is equally invalid.
            tuning = VehiclePhysicsTuning.Car01();
            foreach (var wheel in tuning.wheels) wheel.mountZ = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            // A two-wheel chassis must keep its contacts inline.
            var bike = VehiclePhysicsTuning.Motorcycle01();
            bike.wheels[0].mountX = 0.4f;
            Assert.That(bike.Validate(out _), Is.False);
            // A null wheel entry can never reach the force model.
            tuning = VehiclePhysicsTuning.Car01();
            tuning.wheels[1] = null;
            Assert.That(tuning.Validate(out _), Is.False);
        }

        [Test]
        public void ForceBoundsRejected()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            tuning.peakDriveForce = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.peakBrakeForce = -100f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.frictionCoefficient = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.maxNormalLoadPerWheel = float.PositiveInfinity;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.springRatePerWheel = -1f;
            Assert.That(tuning.Validate(out _), Is.False);
        }

        [Test]
        public void InvalidGeometryAndSteeringRejected()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            tuning.chassisWidth = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.comOffsetY = float.NaN;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.wheels[2].radius = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.steerMaxRadians = 0f;
            Assert.That(tuning.Validate(out _), Is.False);
            tuning = VehiclePhysicsTuning.Car01();
            tuning.boostSpeed = tuning.cruiseSpeed - 1f;
            Assert.That(tuning.Validate(out _), Is.False);
        }

        [Test]
        public void VehicleTuningSelectsArcadeUntilDeliberatelyConfigured()
        {
            var tuning = VehicleTuning.Car01();
            Assert.That(tuning.simulationBackend,
                Is.EqualTo(VehicleSimulationBackend.Arcade));
            Assert.That(tuning.physics, Is.Null);
            Assert.That(tuning.Validate(out var error), Is.True, error);
        }

        [Test]
        public void PhysicalBackendRequiresValidatedPhysicsTuning()
        {
            var tuning = VehicleTuning.Car01();
            tuning.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            Assert.That(tuning.Validate(out _), Is.False,
                "physical backend without physics tuning must not validate");
            tuning.physics = VehiclePhysicsTuning.Car01();
            Assert.That(tuning.Validate(out var error), Is.True, error);
        }

        [Test]
        public void AttachedInvalidPhysicsFailsValidation()
        {
            // Even under the arcade backend an attached physics tuning is
            // deliberately configured content and must validate.
            var tuning = VehicleTuning.Car01();
            tuning.physics = VehiclePhysicsTuning.Car01();
            tuning.physics.massKg = -1f;
            Assert.That(tuning.Validate(out _), Is.False);
        }
    }
}
