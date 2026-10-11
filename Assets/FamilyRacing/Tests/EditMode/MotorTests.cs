using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Core motor contract: kinematic auto-acceleration, steering authority,
    // bounded wall penetration through swept queries, invalid-tuning
    // rejection without state mutation, and copy-safe snapshots. The fake
    // world is a Unity-free stand-in for Runtime/Driving/UnityMotorWorld.
    public sealed class MotorTests
    {
        private const float Dt = 1f / 30f;

        // A flat infinite ground plane plus oriented half-space walls.
        // Each wall allows the region { p : p.x*nx + p.z*nz <= d } where
        // (nx, nz) is the unit inward normal.
        private sealed class FlatWorld : IMotorWorld
        {
            internal float GroundY = 0f;
            // Optional ramp: north of RampToeZ the surface rises RampSlope
            // per metre up to RampCrestZ, then drops back to the plane —
            // the shipped wedge's buried toe and falling crest edge.
            internal float RampToeZ = float.NaN;
            internal float RampCrestZ;
            internal float RampSlope = 0.25f;

            internal struct Wall
            {
                internal float nx, nz, d;
            }

            internal readonly List<Wall> Walls = new List<Wall>();

            private float SurfaceAt(float z)
            {
                if (float.IsNaN(RampToeZ)) return GroundY;
                if (z <= RampToeZ || z >= RampCrestZ) return GroundY;
                return GroundY + (z - RampToeZ) * RampSlope;
            }

            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                var surface = SurfaceAt(pose.z);
                var hit = pose.y - surface <= probeDistance;
                return new GroundHit
                {
                    hit = hit,
                    x = pose.x,
                    y = surface,
                    z = pose.z,
                    normalX = 0f,
                    normalY = 1f,
                    normalZ = 0f,
                    distance = pose.y - surface,
                    surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
                float halfWidth, float halfLength, float height)
            {
                var fx = (float)Math.Sin(pose.heading);
                var fz = (float)Math.Cos(pose.heading);
                var rx = fz;
                var rz = -fx;
                var best = new SweepHit { hit = false, fraction = 1f };
                foreach (var wall in Walls)
                {
                    var approach =
                        Math.Abs(wall.nx * rx + wall.nz * rz) * halfWidth +
                        Math.Abs(wall.nx * fx + wall.nz * fz) * halfLength;
                    var limit = wall.d - approach;
                    var s0 = pose.x * wall.nx + pose.z * wall.nz;
                    var s1 = s0 + dx * wall.nx + dz * wall.nz;
                    if (s1 <= limit) continue;
                    var fraction = s1 == s0 ? 0f : (limit - s0) / (s1 - s0);
                    if (fraction < 0f) fraction = 0f;
                    if (best.hit && fraction >= best.fraction) continue;
                    best = new SweepHit
                    {
                        hit = true,
                        fraction = fraction,
                        normalX = wall.nx,
                        normalY = 0f,
                        normalZ = wall.nz,
                        pointX = pose.x + dx * fraction,
                        pointY = pose.y,
                        pointZ = pose.z + dz * fraction,
                        surfaceId = "wall"
                    };
                }
                if (!float.IsNaN(RampToeZ))
                {
                    // The wedge blocks where its rising surface reaches the
                    // body's underside: material sits under the box only
                    // while the nose overlaps the slope and the tail has
                    // not cleared the crest — the same zero-fraction
                    // signal a tilted-box collider gives Unity's cast.
                    var extentZ = Math.Abs(fz) * halfLength
                        + Math.Abs(fx) * halfWidth;
                    var contactZ = RampToeZ
                        + Math.Max(0f, (pose.y - GroundY) / RampSlope);
                    var n0 = pose.z + extentZ;
                    var n1 = n0 + dz;
                    if (contactZ < RampCrestZ && n1 > contactZ
                        && pose.z - extentZ < RampCrestZ)
                    {
                        var f = n1 == n0 ? 0f : (contactZ - n0) / (n1 - n0);
                        if (f < 0f) f = 0f;
                        if (!best.hit || f < best.fraction)
                        {
                            best = new SweepHit
                            {
                                hit = true,
                                fraction = f,
                                normalX = 0f,
                                normalY = 0f,
                                normalZ = -1f,
                                pointX = pose.x + dx * f,
                                pointY = pose.y,
                                pointZ = pose.z + dz * f,
                                surfaceId = "ramp"
                            };
                        }
                    }
                }
                return best;
            }
        }

        private static DriverInput Neutral()
        {
            return new DriverInput(0f, false, false, false, false, false, false);
        }

        private static DriverInput Steering(float steering)
        {
            return new DriverInput(steering, false, false, false, false, false, false);
        }

        private static MotorState Spawn()
        {
            return new MotorState { grounded = true };
        }

        private static MotorState StepN(MotorState state, DriverInput input,
            VehicleTuning tuning, IMotorWorld world, int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                var result = ArcadeMotor.Step(state, input, tuning, world, Dt);
                Assert.That(result.rejected, Is.False, "step unexpectedly rejected");
                state = result.state;
            }
            return state;
        }

        [Test]
        public void AutoAccelerationMovesCarWithNeutralSteering()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Neutral(), tuning, world, 60);
            Assert.That(state.speed, Is.GreaterThan(0f), "auto-acceleration must build speed");
            Assert.That(state.speed, Is.LessThanOrEqualTo(tuning.maxSpeed + 0.001f));
            Assert.That(state.z, Is.GreaterThan(5f),
                "a neutral car must drive forward along its heading");
            Assert.That(Math.Abs(state.x), Is.LessThan(0.001f),
                "neutral steering must not drift sideways");
            Assert.That(Math.Abs(state.heading), Is.LessThan(0.001f));
            Assert.That(state.grounded, Is.True);
            Assert.That(state.tick, Is.EqualTo(60));
        }

        [Test]
        public void SpeedSettlesAtMaxSpeed()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Neutral(), tuning, world, 600);
            Assert.That(state.speed, Is.EqualTo(tuning.maxSpeed).Within(0.01f));
        }

        [Test]
        public void SteeringChangesHeading()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Neutral(), tuning, world, 60);
            var before = state.heading;
            state = StepN(state, Steering(1f), tuning, world, 30);
            Assert.That(state.heading, Is.GreaterThan(before + 0.3f),
                "held right steering must rotate the heading");
            Assert.That(state.x, Is.GreaterThan(1f),
                "a right-hand turn must move the car right of its start line");
            state = StepN(state, Steering(-1f), tuning, world, 30);
            var after = state.heading;
            state = StepN(state, Steering(-1f), tuning, world, 30);
            Assert.That(state.heading, Is.LessThan(after),
                "held left steering must rotate the heading the other way");
        }

        [Test]
        public void StationaryCarDoesNotTurnInPlace()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Steering(1f), tuning, world, 3);
            Assert.That(Math.Abs(state.heading), Is.LessThan(0.05f),
                "steering authority scales with speed; a barely-moving car barely turns");
        }

        [Test]
        public void WallSweepBoundsPenetration()
        {
            var world = new FlatWorld();
            // Wall at z = 20 blocking +z motion (inward normal -z? allowed z <= 20).
            world.Walls.Add(new FlatWorld.Wall { nx = 0f, nz = 1f, d = 20f });
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Neutral(), tuning, world, 600);
            var clearance = 20f - state.z;
            Assert.That(clearance, Is.GreaterThanOrEqualTo(tuning.halfLength - 0.01f),
                "the swept body must stop with its nose at or before the wall");
            var probe = world.SweepBody(
                new MotorPose { x = state.x, y = state.y, z = state.z, heading = state.heading },
                0f, 0f, tuning.maxSpeed * Dt,
                tuning.halfWidth, tuning.halfLength, tuning.height);
            Assert.That(probe.hit, Is.True, "one more step of motion must still be blocked");
            Assert.That(probe.fraction, Is.LessThan(1f),
                "the blocked sweep reports a real contact fraction, not a pass-through");
            // Sliding along the wall must not accumulate penetration either.
            var sliding = StepN(state, Steering(0.5f), tuning, world, 120);
            Assert.That(20f - sliding.z, Is.GreaterThanOrEqualTo(
                tuning.halfLength * 0.5f - 0.01f),
                "sliding must keep the body out of the wall");
        }

        [Test]
        public void WallHitEmitsEvent()
        {
            var world = new FlatWorld();
            world.Walls.Add(new FlatWorld.Wall { nx = 0f, nz = 1f, d = 10f });
            var tuning = VehicleTuning.Car01();
            var state = Spawn();
            var sawHit = false;
            for (var i = 0; i < 300 && !sawHit; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                foreach (var ev in result.events)
                    if (ev.kind == "wallHit") sawHit = true;
            }
            Assert.That(sawHit, Is.True, "a wall contact must emit a wallHit event");
        }

        [Test]
        public void InvalidTuningRejectedWithoutStateChange()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            tuning.maxSpeed = float.NaN;
            var before = StepN(Spawn(), Neutral(), VehicleTuning.Car01(), world, 10);
            var result = ArcadeMotor.Step(before, Neutral(), tuning, world, Dt);
            Assert.That(result.rejected, Is.True);
            Assert.That(result.error, Is.EqualTo("invalidTuning"));
            Assert.That(result.state.x, Is.EqualTo(before.x));
            Assert.That(result.state.z, Is.EqualTo(before.z));
            Assert.That(result.state.speed, Is.EqualTo(before.speed));
            Assert.That(result.state.heading, Is.EqualTo(before.heading));
            Assert.That(result.state.tick, Is.EqualTo(before.tick),
                "a rejected step must not advance the motor tick");
        }

        [Test]
        public void InvalidDeltaRejectedWithoutStateChange()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var before = Spawn();
            var result = ArcadeMotor.Step(before, Neutral(), tuning, world, float.NaN);
            Assert.That(result.rejected, Is.True);
            Assert.That(result.error, Is.EqualTo("invalidDelta"));
            Assert.That(result.state.tick, Is.EqualTo(before.tick));
        }

        [Test]
        public void MissingWorldRejectedWithoutStateChange()
        {
            var tuning = VehicleTuning.Car01();
            var before = Spawn();
            var result = ArcadeMotor.Step(before, Neutral(), tuning, null, Dt);
            Assert.That(result.rejected, Is.True);
            Assert.That(result.state.tick, Is.EqualTo(before.tick));
        }

        [Test]
        public void AirborneCarFallsAndLands()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var state = new MotorState { y = 8f, grounded = false };
            var landed = false;
            for (var i = 0; i < 300 && !landed; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                foreach (var ev in result.events)
                    if (ev.kind == "landed") landed = true;
            }
            Assert.That(landed, Is.True, "a falling car must land and emit landed");
            Assert.That(state.grounded, Is.True);
            Assert.That(state.y, Is.EqualTo(world.GroundY).Within(0.001f));
            Assert.That(state.verticalSpeed, Is.EqualTo(0f));
        }

        [Test]
        public void RampToeClimbsAndLeapsOffTheCrest()
        {
            // A buried-toe slope ending in a falling lip — the same
            // geometry the shipped landmark ramp presents. The wedged
            // body must step-climb the slope, then leave the crest
            // airborne and land: no wall blocks it, no teleport moves it.
            var world = new FlatWorld
            {
                RampToeZ = 10f,
                RampCrestZ = 24f,
                RampSlope = 0.25f
            };
            var tuning = VehicleTuning.Car01();
            var crestHeight = (world.RampCrestZ - world.RampToeZ)
                * world.RampSlope;
            var state = Spawn();
            var airborne = false;
            var landed = false;
            var clean = false;
            var maxY = 0f;
            for (var i = 0; i < 900 && !landed; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                Assert.That(result.rejected, Is.False, "step unexpectedly rejected");
                state = result.state;
                maxY = Math.Max(maxY, state.y);
                foreach (var ev in result.events)
                {
                    if (ev.kind == "becameAirborne") airborne = true;
                    if (ev.kind == "landed") landed = true;
                    if (ev.kind == "cleanLanding") clean = true;
                }
            }
            Assert.That(maxY, Is.GreaterThan(crestHeight * 0.9f),
                "the step-climb must carry the body near the crest (maxY="
                + maxY + " crest=" + crestHeight + ")");
            Assert.That(airborne, Is.True,
                "leaving the crest must produce a real airborne episode");
            Assert.That(landed, Is.True, "the flight must land");
            Assert.That(clean, Is.True,
                "an untouched ramp flight earns the clean-landing credit");
            Assert.That(state.grounded, Is.True);
            Assert.That(state.z, Is.GreaterThan(world.RampCrestZ),
                "the car must land beyond the crest, z=" + state.z);
        }

        [Test]
        public void WedgedAgainstFlatWallDoesNotStepClimb()
        {
            // The bounded step only fires where a surface the ground
            // probe can follow lies above the pose — a flat floor next to
            // a tall wall gives the lifted cast no clearance either.
            var world = new FlatWorld();
            world.Walls.Add(new FlatWorld.Wall { nx = 0f, nz = 1f, d = 20f });
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Neutral(), tuning, world, 600);
            Assert.That(20f - state.z, Is.GreaterThanOrEqualTo(
                    tuning.halfLength - 0.01f),
                "a wall still blocks: step-climb must not vault it");
            Assert.That(state.y, Is.LessThan(0.5f),
                "the pose must never be lifted over a real wall");
        }

        [Test]
        public void SnapshotCopyDoesNotAliasLiveState()
        {
            var world = new FlatWorld();
            var tuning = VehicleTuning.Car01();
            var state = StepN(Spawn(), Neutral(), tuning, world, 10);
            var copy = state.Copy();
            copy.x = 999f;
            copy.speed = -5f;
            copy.drifting = true;
            copy.boostCharge = 0f;
            copy.lastContact = "tampered";
            copy.tick = -1;
            Assert.That(state.x, Is.Not.EqualTo(999f));
            Assert.That(state.speed, Is.GreaterThan(0f));
            Assert.That(state.drifting, Is.False);
            Assert.That(state.lastContact, Is.EqualTo("ground"));
            Assert.That(state.tick, Is.EqualTo(10));
        }

        [Test]
        public void TuningPresetsAreValidAndDistinct()
        {
            var car = VehicleTuning.Car01();
            var moto = VehicleTuning.Motorcycle01();
            string error;
            Assert.That(car.Validate(out error), Is.True, error);
            Assert.That(moto.Validate(out error), Is.True, error);
            Assert.That(car.vehicleId, Is.EqualTo("car-01"));
            Assert.That(moto.vehicleId, Is.EqualTo("motorcycle-01"));
            Assert.That(moto.steeringRate, Is.GreaterThan(car.steeringRate),
                "the motorcycle must steer quicker than the car");
        }
    }
}
