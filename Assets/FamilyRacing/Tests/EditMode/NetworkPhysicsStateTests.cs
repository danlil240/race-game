using System;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 9: the compact wire DTO carrying a physical racer's whole
    // replay state (rule envelope + body + controller histories) inside a
    // NetworkSnapshot. The contracts under test: complete round-trips for
    // the states a live racer actually reaches (upside-down, airborne,
    // mid-drift), normalized quaternions in both directions, a bounded
    // wheel array, and a validation gate that rejects malformed wire
    // state before any motor is attached — so a hostile or corrupted
    // snapshot can never partially mutate client state.
    public sealed class NetworkPhysicsStateTests
    {
        // A physical MotorState with distinctive values in every carried
        // field: four wheels, non-trivial body state, mid-episode
        // bookkeeping and a rule envelope that is not the default.
        private static MotorState PhysicalMotor(float attitudeX,
            float attitudeY, float attitudeZ, float attitudeW)
        {
            var state = new MotorState
            {
                x = -12.5f,
                y = 0.75f,
                z = 103.25f,
                heading = -0.9f,
                speed = 7.5f,
                lateralSpeed = -1.25f,
                verticalSpeed = 0.5f,
                grounded = true,
                airSeconds = 0f,
                drifting = true,
                driftSign = -1,
                boostCharge = 0.62f,
                boostActive = true,
                boostSecondsRemaining = 1.1f,
                boostLatched = true,
                airWallContact = false,
                airPeakY = 0.75f,
                recovering = false,
                lastContact = "vehicle",
                tick = 4321
            };
            var physics = new VehiclePhysicsState
            {
                velocityX = -1.5f,
                velocityY = 0.25f,
                velocityZ = 7.75f,
                angularVelocityX = 0.1f,
                angularVelocityY = -0.4f,
                angularVelocityZ = 0.8f,
                filteredSteering = 0.31f,
                filteredRollRate = -0.22f,
                balancePrevError = 0.045f,
                filteredLeanTarget = -0.17f,
                driftAmount = 0.66f,
                grounded = true,
                airSeconds = 0f,
                airPeakY = 0.75f,
                airWallContact = false,
                overturnedSeconds = 0f,
                contactLossSeconds = 0f,
                lastContact = "vehicle",
                recoveryEpoch = 3,
                wheels = new WheelPhysicsState[4]
            };
            physics.TrySetAttitude(attitudeX, attitudeY, attitudeZ, attitudeW);
            for (var i = 0; i < physics.wheels.Length; i++)
            {
                physics.wheels[i] = new WheelPhysicsState
                {
                    index = i,
                    grounded = true,
                    compression = 0.05f + i * 0.01f,
                    previousCompression = 0.04f + i * 0.01f,
                    contactValid = true,
                    steering = i < 2 ? 0.31f : 0f,
                    spin = 40f + i,
                    // Diagnostic-only fields deliberately non-zero: the
                    // compact DTO must not carry them.
                    contactX = i,
                    contactY = -0.5f,
                    contactZ = i * 2f,
                    normalX = 0.1f,
                    normalY = 0.99f,
                    normalZ = -0.05f,
                    normalLoad = 3000f + i,
                    slipLongitudinal = 0.5f,
                    slipLateral = -0.25f
                };
            }
            state.physics = physics;
            return state;
        }

        private static void AssertMotorEquals(MotorState expected,
            MotorState actual, float tolerance)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance));
            Assert.That(actual.heading, Is.EqualTo(expected.heading).Within(tolerance));
            Assert.That(actual.speed, Is.EqualTo(expected.speed).Within(tolerance));
            Assert.That(actual.lateralSpeed, Is.EqualTo(expected.lateralSpeed).Within(tolerance));
            Assert.That(actual.verticalSpeed, Is.EqualTo(expected.verticalSpeed).Within(tolerance));
            Assert.That(actual.grounded, Is.EqualTo(expected.grounded));
            Assert.That(actual.airSeconds, Is.EqualTo(expected.airSeconds).Within(tolerance));
            Assert.That(actual.drifting, Is.EqualTo(expected.drifting));
            Assert.That(actual.driftSign, Is.EqualTo(expected.driftSign));
            Assert.That(actual.boostCharge, Is.EqualTo(expected.boostCharge).Within(tolerance));
            Assert.That(actual.boostActive, Is.EqualTo(expected.boostActive));
            Assert.That(actual.boostSecondsRemaining,
                Is.EqualTo(expected.boostSecondsRemaining).Within(tolerance));
            Assert.That(actual.boostLatched, Is.EqualTo(expected.boostLatched));
            Assert.That(actual.airWallContact, Is.EqualTo(expected.airWallContact));
            Assert.That(actual.airPeakY, Is.EqualTo(expected.airPeakY).Within(tolerance));
            Assert.That(actual.recovering, Is.EqualTo(expected.recovering));
            Assert.That(actual.lastContact, Is.EqualTo(expected.lastContact));
            Assert.That(actual.tick, Is.EqualTo(expected.tick));
        }

        private static void AssertPhysicsEquals(VehiclePhysicsState expected,
            VehiclePhysicsState actual, float tolerance)
        {
            Assert.That(actual.attitudeX, Is.EqualTo(expected.attitudeX).Within(tolerance));
            Assert.That(actual.attitudeY, Is.EqualTo(expected.attitudeY).Within(tolerance));
            Assert.That(actual.attitudeZ, Is.EqualTo(expected.attitudeZ).Within(tolerance));
            Assert.That(actual.attitudeW, Is.EqualTo(expected.attitudeW).Within(tolerance));
            Assert.That(actual.velocityX, Is.EqualTo(expected.velocityX).Within(tolerance));
            Assert.That(actual.velocityY, Is.EqualTo(expected.velocityY).Within(tolerance));
            Assert.That(actual.velocityZ, Is.EqualTo(expected.velocityZ).Within(tolerance));
            Assert.That(actual.angularVelocityX, Is.EqualTo(expected.angularVelocityX).Within(tolerance));
            Assert.That(actual.angularVelocityY, Is.EqualTo(expected.angularVelocityY).Within(tolerance));
            Assert.That(actual.angularVelocityZ, Is.EqualTo(expected.angularVelocityZ).Within(tolerance));
            Assert.That(actual.filteredSteering, Is.EqualTo(expected.filteredSteering).Within(tolerance));
            Assert.That(actual.filteredRollRate, Is.EqualTo(expected.filteredRollRate).Within(tolerance));
            Assert.That(actual.balancePrevError, Is.EqualTo(expected.balancePrevError).Within(tolerance));
            Assert.That(actual.filteredLeanTarget, Is.EqualTo(expected.filteredLeanTarget).Within(tolerance));
            Assert.That(actual.driftAmount, Is.EqualTo(expected.driftAmount).Within(tolerance));
            Assert.That(actual.grounded, Is.EqualTo(expected.grounded));
            Assert.That(actual.airSeconds, Is.EqualTo(expected.airSeconds).Within(tolerance));
            Assert.That(actual.airPeakY, Is.EqualTo(expected.airPeakY).Within(tolerance));
            Assert.That(actual.airWallContact, Is.EqualTo(expected.airWallContact));
            Assert.That(actual.overturnedSeconds, Is.EqualTo(expected.overturnedSeconds).Within(tolerance));
            Assert.That(actual.contactLossSeconds, Is.EqualTo(expected.contactLossSeconds).Within(tolerance));
            Assert.That(actual.lastContact, Is.EqualTo(expected.lastContact));
            Assert.That(actual.recoveryEpoch, Is.EqualTo(expected.recoveryEpoch));
            Assert.That(actual.wheels.Length, Is.EqualTo(expected.wheels.Length));
            for (var i = 0; i < expected.wheels.Length; i++)
            {
                var from = expected.wheels[i];
                var to = actual.wheels[i];
                Assert.That(to.index, Is.EqualTo(i));
                Assert.That(to.grounded, Is.EqualTo(from.grounded), "wheel " + i);
                Assert.That(to.compression, Is.EqualTo(from.compression).Within(tolerance), "wheel " + i);
                Assert.That(to.previousCompression,
                    Is.EqualTo(from.previousCompression).Within(tolerance), "wheel " + i);
                Assert.That(to.contactValid, Is.EqualTo(from.contactValid), "wheel " + i);
                Assert.That(to.steering, Is.EqualTo(from.steering).Within(tolerance), "wheel " + i);
                Assert.That(to.spin, Is.EqualTo(from.spin).Within(tolerance), "wheel " + i);
            }
        }

        private static MotorState RoundTrip(MotorState source)
        {
            var dto = NetworkPhysicsState.FromMotor(source);
            Assert.That(dto, Is.Not.Null, "a physical motor must produce a DTO");
            Assert.That(dto.present, Is.True);
            string error;
            Assert.That(dto.Validate(out error), Is.True,
                "a real motor state must always encode a valid DTO: " + error);
            return dto.ToMotor();
        }

        // --- round trips ---------------------------------------------------

        // 180 degrees about X: a fully upside-down chassis must keep its
        // attitude, its inverted-support episode and its damper history
        // through the compact wire form.
        [Test]
        public void UpsideDownStateRoundTrips()
        {
            var source = PhysicalMotor(1f, 0f, 0f, 0f);
            source.physics.overturnedSeconds = 1.25f;
            source.grounded = true;
            source.physics.grounded = true;

            var restored = RoundTrip(source);

            AssertMotorEquals(source, restored, 1e-6f);
            AssertPhysicsEquals(source.physics, restored.physics, 1e-6f);
            var upDot = 1f - 2f * (restored.physics.attitudeX * restored.physics.attitudeX
                + restored.physics.attitudeZ * restored.physics.attitudeZ);
            Assert.That(upDot, Is.LessThan(-0.99f),
                "the decoded chassis must still be upside down");
            Assert.That(restored.physics.overturnedSeconds,
                Is.EqualTo(1.25f).Within(1e-6f));
            string error;
            Assert.That(restored.physics.Validate(out error), Is.True, error);
        }

        // A mid-flight episode: contact loss, elapsed airtime, episode
        // peak and the barrier-clip flag all survive — a restore can
        // never manufacture or swallow a landing the original saw.
        [Test]
        public void AirborneStateRoundTrips()
        {
            var source = PhysicalMotor(0f, 0.3f, 0f, 0.953939f);
            source.grounded = false;
            source.airSeconds = 0.42f;
            source.airPeakY = 8.5f;
            source.airWallContact = true;
            source.physics.grounded = false;
            source.physics.airSeconds = 0.42f;
            source.physics.airPeakY = 8.5f;
            source.physics.airWallContact = true;
            source.physics.contactLossSeconds = 0.07f;
            foreach (var wheel in source.physics.wheels)
            {
                wheel.grounded = false;
                wheel.compression = 0f;
                wheel.contactValid = false;
            }

            var restored = RoundTrip(source);

            AssertMotorEquals(source, restored, 1e-6f);
            AssertPhysicsEquals(source.physics, restored.physics, 1e-6f);
            Assert.That(restored.grounded, Is.False);
            Assert.That(restored.physics.airWallContact, Is.True);
        }

        // Every replay-required controller history — steering rate
        // limiter, roll-rate estimate, bike balance error and lean
        // target, progressive drift — survives the round trip.
        [Test]
        public void ControllerHistoriesRoundTrip()
        {
            var source = PhysicalMotor(0f, 0.2f, 0.1f, 0.974679f);

            var restored = RoundTrip(source);

            Assert.That(restored.physics.filteredSteering,
                Is.EqualTo(0.31f).Within(1e-6f));
            Assert.That(restored.physics.filteredRollRate,
                Is.EqualTo(-0.22f).Within(1e-6f));
            Assert.That(restored.physics.balancePrevError,
                Is.EqualTo(0.045f).Within(1e-6f));
            Assert.That(restored.physics.filteredLeanTarget,
                Is.EqualTo(-0.17f).Within(1e-6f));
            Assert.That(restored.physics.driftAmount,
                Is.EqualTo(0.66f).Within(1e-6f));
            Assert.That(restored.physics.recoveryEpoch, Is.EqualTo(3));
        }

        // The rule envelope the economy and presentation layers consume —
        // boost charge/activity, drift flags, recovering flag, tick and
        // the vehicle-contact fact — round-trips alongside the body.
        [Test]
        public void RuleEnvelopeRoundTrips()
        {
            var source = PhysicalMotor(0f, 0.4f, 0f, 0.916515f);

            var restored = RoundTrip(source);

            Assert.That(restored.boostCharge, Is.EqualTo(0.62f).Within(1e-6f));
            Assert.That(restored.boostActive, Is.True);
            Assert.That(restored.boostLatched, Is.True);
            Assert.That(restored.drifting, Is.True);
            Assert.That(restored.driftSign, Is.EqualTo(-1));
            Assert.That(restored.lastContact, Is.EqualTo("vehicle"));
            Assert.That(restored.tick, Is.EqualTo(4321));
        }

        // --- quaternion handling ------------------------------------------

        // Serialization normalizes: a non-unit attitude entering FromMotor
        // comes back unit-length on the wire and in the decoded motor.
        [Test]
        public void FromMotorNormalizesAttitude()
        {
            var source = PhysicalMotor(0f, 0f, 0f, 1f);
            // Force a denormalized quaternion into the state directly.
            source.physics.attitudeX = 0f;
            source.physics.attitudeY = 6f;
            source.physics.attitudeZ = 0f;
            source.physics.attitudeW = 8f;

            var dto = NetworkPhysicsState.FromMotor(source);
            var restored = dto.ToMotor();

            var norm = restored.physics.attitudeX * restored.physics.attitudeX
                + restored.physics.attitudeY * restored.physics.attitudeY
                + restored.physics.attitudeZ * restored.physics.attitudeZ
                + restored.physics.attitudeW * restored.physics.attitudeW;
            Assert.That(norm, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(restored.physics.attitudeY, Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(restored.physics.attitudeW, Is.EqualTo(0.8f).Within(1e-6f));
            // The wire copy is normalized too — a peer never sees the
            // pre-normalization components.
            var wireNorm = dto.attitudeX * dto.attitudeX
                + dto.attitudeY * dto.attitudeY
                + dto.attitudeZ * dto.attitudeZ
                + dto.attitudeW * dto.attitudeW;
            Assert.That(wireNorm, Is.EqualTo(1f).Within(1e-6f));
        }

        // Deserialization normalizes through the same single mutation
        // path: a scaled-but-finite wire quaternion decodes to unit
        // length instead of poisoning the body.
        [Test]
        public void ToMotorNormalizesWireAttitude()
        {
            var source = PhysicalMotor(0f, 0f, 0f, 1f);
            var dto = NetworkPhysicsState.FromMotor(source);
            dto.attitudeX = 0f;
            dto.attitudeY = 0f;
            dto.attitudeZ = 0f;
            dto.attitudeW = 3f; // finite, non-unit, wrong scale

            var restored = dto.ToMotor();
            var norm = restored.physics.attitudeX * restored.physics.attitudeX
                + restored.physics.attitudeY * restored.physics.attitudeY
                + restored.physics.attitudeZ * restored.physics.attitudeZ
                + restored.physics.attitudeW * restored.physics.attitudeW;
            Assert.That(norm, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(restored.physics.attitudeW, Is.EqualTo(1f).Within(1e-6f));
        }

        // --- shape / bounds -------------------------------------------------

        [Test]
        public void FromMotorReturnsNullWithoutPhysics()
        {
            Assert.That(NetworkPhysicsState.FromMotor(null), Is.Null);
            Assert.That(NetworkPhysicsState.FromMotor(new MotorState()),
                Is.Null, "arcade motors carry no wire physics DTO");
        }

        // The compact form deliberately drops diagnostic-only wheel data:
        // contact points, normals, loads and slips decode as zero — the
        // client never presents them as authoritative.
        [Test]
        public void DiagnosticsAreNotCarriedOnTheWire()
        {
            var source = PhysicalMotor(0f, 0f, 0f, 1f);
            var restored = RoundTrip(source);
            foreach (var wheel in restored.physics.wheels)
            {
                Assert.That(wheel.normalLoad, Is.EqualTo(0f));
                Assert.That(wheel.slipLongitudinal, Is.EqualTo(0f));
                Assert.That(wheel.slipLateral, Is.EqualTo(0f));
                Assert.That(wheel.contactX, Is.EqualTo(0f));
                Assert.That(wheel.contactY, Is.EqualTo(0f));
                Assert.That(wheel.contactZ, Is.EqualTo(0f));
                Assert.That(wheel.normalX, Is.EqualTo(0f));
                Assert.That(wheel.normalZ, Is.EqualTo(0f));
            }
        }

        // --- validation: malformed wire state rejects before mutation ------

        private static NetworkPhysicsState ValidDto()
        {
            return NetworkPhysicsState.FromMotor(
                PhysicalMotor(0f, 0.2f, 0.1f, 0.97f));
        }

        [Test]
        public void ValidDtoPassesValidation()
        {
            string error;
            Assert.That(ValidDto().Validate(out error), Is.True, error);
        }

        [Test]
        public void NonFiniteFieldsReject()
        {
            // The offline runner invokes parameterless tests only, so the
            // field matrix unrolls here — every carried float must be
            // finite before any state is applied.
            foreach (var field in new[]
                { "x", "velocityZ", "angularVelocityY",
                  "boostCharge", "airPeakY", "filteredSteering" })
            {
                var dto = ValidDto();
                switch (field)
                {
                    case "x": dto.x = float.NaN; break;
                    case "velocityZ": dto.velocityZ = float.PositiveInfinity; break;
                    case "angularVelocityY":
                        dto.angularVelocityY = float.NaN; break;
                    case "boostCharge": dto.boostCharge = float.NaN; break;
                    case "airPeakY": dto.airPeakY = float.NegativeInfinity; break;
                    case "filteredSteering": dto.filteredSteering = float.NaN; break;
                }
                string error;
                Assert.That(dto.Validate(out error), Is.False,
                    field + "=non-finite must reject");
            }
        }

        [Test]
        public void ZeroLengthAttitudeRejects()
        {
            var dto = ValidDto();
            dto.attitudeX = 0f; dto.attitudeY = 0f;
            dto.attitudeZ = 0f; dto.attitudeW = 0f;
            string error;
            Assert.That(dto.Validate(out error), Is.False);
        }

        [Test]
        public void WheelBoundAndShapeReject()
        {
            var dto = ValidDto();
            // Five wheels exceeds every authored layout (2 or 4).
            dto.wheels = new NetworkPhysicsWheel[5];
            for (var i = 0; i < dto.wheels.Length; i++)
                dto.wheels[i] = new NetworkPhysicsWheel();
            string error;
            Assert.That(dto.Validate(out error), Is.False,
                "an over-capacity wheel array must reject");

            dto = ValidDto();
            dto.wheels = null;
            Assert.That(dto.Validate(out error), Is.False,
                "a null wheel array must reject");

            dto = ValidDto();
            dto.wheels[1] = null;
            Assert.That(dto.Validate(out error), Is.False,
                "a null wheel entry must reject");

            dto = ValidDto();
            dto.wheels[0].compression = -0.01f;
            Assert.That(dto.Validate(out error), Is.False,
                "negative compression must reject");

            dto = ValidDto();
            dto.wheels[2].steering = float.NaN;
            Assert.That(dto.Validate(out error), Is.False,
                "non-finite wheel steering must reject");
        }

        [Test]
        public void SemanticBoundsReject()
        {
            // Unrolled like NonFiniteFieldsReject — the offline runner
            // cannot supply [Values] parameters.
            foreach (var kind in new[]
                { "driftHigh", "negativeAir", "negativeOverturned",
                  "negativeContactLoss", "negativeEpoch", "nullContact" })
            {
                var dto = ValidDto();
                switch (kind)
                {
                    case "driftHigh": dto.driftAmount = 1.01f; break;
                    case "negativeAir": dto.airSeconds = -0.5f; break;
                    case "negativeOverturned": dto.overturnedSeconds = -1f; break;
                    case "negativeContactLoss": dto.contactLossSeconds = -1f; break;
                    case "negativeEpoch": dto.recoveryEpoch = -1; break;
                    case "nullContact": dto.lastContact = null; break;
                }
                string error;
                Assert.That(dto.Validate(out error), Is.False,
                    kind + " must reject");
            }
        }

        // A rejected DTO yields no usable motor: callers must check
        // Validate() and drop — ToMotor on a sane DTO always produces a
        // VehiclePhysicsState that passes its own authority gate.
        [Test]
        public void ToMotorProducesStateTheAuthorityAccepts()
        {
            var restored = RoundTrip(PhysicalMotor(0.5f, 0.5f, 0.5f, 0.5f));
            string error;
            Assert.That(restored.physics.Validate(out error), Is.True,
                "decoded state must satisfy the physics authority: " + error);
        }
    }
}
