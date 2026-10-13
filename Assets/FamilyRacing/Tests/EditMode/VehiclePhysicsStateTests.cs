using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class VehiclePhysicsStateTests
    {
        private static VehiclePhysicsState ValidState(int wheelCount = 4)
        {
            var state = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[wheelCount]
            };
            for (var i = 0; i < wheelCount; i++)
                state.wheels[i] = new WheelPhysicsState { index = i };
            return state;
        }

        [Test]
        public void PhysicsCopyDoesNotAliasWheels()
        {
            const float originalCompression = 0.12f;
            var motor = new MotorState();
            motor.physics = ValidState();
            motor.physics.wheels[0].compression = originalCompression;
            var copy = motor.Copy();
            motor.physics.wheels[0].compression = 0.9f;
            Assert.That(copy.physics.wheels[0].compression,
                Is.EqualTo(originalCompression));
        }

        [Test]
        public void PhysicsCopyCarriesControllerAndEpisodeState()
        {
            var motor = new MotorState();
            motor.physics = ValidState(2);
            motor.physics.recoveryEpoch = 7;
            motor.physics.filteredSteering = 0.31f;
            motor.physics.balancePrevError = -0.02f;
            motor.physics.airSeconds = 1.5f;
            motor.physics.wheels[1].contactValid = true;
            motor.physics.wheels[1].previousCompression = 0.05f;
            var copy = motor.Copy();
            Assert.That(copy.physics.recoveryEpoch, Is.EqualTo(7));
            Assert.That(copy.physics.filteredSteering, Is.EqualTo(0.31f));
            Assert.That(copy.physics.balancePrevError, Is.EqualTo(-0.02f));
            Assert.That(copy.physics.airSeconds, Is.EqualTo(1.5f));
            Assert.That(copy.physics.wheels[1].contactValid, Is.True);
            Assert.That(copy.physics.wheels[1].previousCompression,
                Is.EqualTo(0.05f));
        }

        [Test]
        public void ArcadeMotorStateCarriesNoPhysics()
        {
            Assert.That(new MotorState().physics, Is.Null);
            Assert.That(new MotorState().Copy().physics, Is.Null);
        }

        [Test]
        public void ValidStatePassesValidation()
        {
            var state = ValidState();
            Assert.That(state.Validate(out var error), Is.True, error);
        }

        [Test]
        public void ValidateRejectsNonFiniteQuaternion()
        {
            var nonFiniteQuaternion = ValidState();
            nonFiniteQuaternion.attitudeX = float.NaN;
            Assert.That(nonFiniteQuaternion.Validate(out _), Is.False);
            nonFiniteQuaternion = ValidState();
            nonFiniteQuaternion.attitudeW = float.PositiveInfinity;
            Assert.That(nonFiniteQuaternion.Validate(out _), Is.False);
        }

        [Test]
        public void ValidateRejectsZeroAndUnnormalizedQuaternion()
        {
            var zero = ValidState();
            zero.attitudeW = 0f;
            Assert.That(zero.Validate(out _), Is.False);
            var scaled = ValidState();
            scaled.attitudeW = 2f;
            Assert.That(scaled.Validate(out _), Is.False);
        }

        [Test]
        public void TrySetAttitudeNormalizesUsableQuaternion()
        {
            var state = ValidState();
            Assert.That(state.TrySetAttitude(0f, 0f, 0f, 2f), Is.True);
            Assert.That(state.attitudeW, Is.EqualTo(1f));
            Assert.That(state.TrySetAttitude(0f, 0.6f, 0f, 0.8f), Is.True);
            Assert.That(state.attitudeY, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(state.attitudeW, Is.EqualTo(0.8f).Within(0.0001f));
        }

        [Test]
        public void TrySetAttitudeRejectsBeforeMutation()
        {
            var state = ValidState();
            state.TrySetAttitude(0f, 0.6f, 0f, 0.8f);
            Assert.That(state.TrySetAttitude(0f, 0f, 0f, 0f), Is.False,
                "a zero quaternion must be rejected");
            Assert.That(state.TrySetAttitude(float.NaN, 0f, 0f, 1f), Is.False,
                "a non-finite quaternion must be rejected");
            // Rejections happened before any mutation: the previous
            // normalized attitude survives.
            Assert.That(state.attitudeY, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(state.attitudeW, Is.EqualTo(0.8f).Within(0.0001f));
        }

        [Test]
        public void ValidateRejectsNullWheelArrayAndEntries()
        {
            var state = ValidState();
            state.wheels = null;
            Assert.That(state.Validate(out _), Is.False);
            state = ValidState();
            state.wheels[2] = null;
            Assert.That(state.Validate(out _), Is.False);
        }

        [Test]
        public void ValidateRejectsBadWheelIndexAndNonFiniteVelocity()
        {
            var state = ValidState();
            state.wheels[1].index = 5;
            Assert.That(state.Validate(out _), Is.False);
            state = ValidState();
            state.velocityY = float.NegativeInfinity;
            Assert.That(state.Validate(out _), Is.False);
            state = ValidState();
            state.angularVelocityZ = float.NaN;
            Assert.That(state.Validate(out _), Is.False);
        }

        [Test]
        public void ValidateRejectsBadEpisodeBookkeeping()
        {
            var state = ValidState();
            state.airSeconds = -0.5f;
            Assert.That(state.Validate(out _), Is.False);
            state = ValidState();
            state.recoveryEpoch = -1;
            Assert.That(state.Validate(out _), Is.False);
            state = ValidState();
            state.wheels[0].compression = -0.1f;
            Assert.That(state.Validate(out _), Is.False);
        }
    }
}
