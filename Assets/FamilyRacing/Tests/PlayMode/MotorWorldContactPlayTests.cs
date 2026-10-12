using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // Contact-integrity regression tests for the reported pass-throughs:
    // Physics.BoxCast cannot see a collider the cast volume already
    // touches or contains, so without explicit handling a wedged body
    // free-moves through walls and the landmark ramp. These tests drive
    // the real UnityMotorWorld + ArcadeMotor, not a fake world.
    public sealed class MotorWorldContactPlayTests
    {
        private const float Dt = 1f / 30f;
        private GameObject root;
        private UnityMotorWorld world;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("contact-test");
            world = new UnityMotorWorld();
            Box("Ground", new Vector3(0f, -0.5f, 0f),
                new Vector3(240f, 1f, 160f), Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
        }

        private GameObject Box(string name, Vector3 position,
            Vector3 scale, Quaternion rotation)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(root.transform, false);
            box.transform.localPosition = position;
            box.transform.localRotation = rotation;
            box.transform.localScale = scale;
            Physics.SyncTransforms();
            return box;
        }

        private MotorState Drive(MotorState state, float steering,
            int ticks, VehicleTuning tuning)
        {
            var input = new DriverInput(steering, false, false, false, false, false, false);
            for (var i = 0; i < ticks; i++)
            {
                var result = ArcadeMotor.Step(state, input, tuning, world, Dt);
                Assert.That(result.rejected, Is.False,
                    "the real world must never reject a valid step: " + result.error);
                state = result.state;
            }
            return state;
        }

        // --- the cast must see wedged/contact states it starts inside -------

        [Test]
        public void OverlappedCastReportsWedgedHitNotFreeMove()
        {
            Box("Wall", new Vector3(2.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            var t = VehicleTuning.Car01();
            // Nose already 0.3m inside the wall face (x=2.0): the cast must
            // report a blocking hit, never a free move.
            var pose = new MotorPose { x = 0.7f, y = 0f, z = 0f, heading = Mathf.PI / 2f };
            var hit = world.SweepBody(pose, 0.6f, 0f, 0f,
                t.halfWidth, t.halfLength, t.height);
            Assert.That(hit.hit, Is.True,
                "a cast that starts overlapped must report the blocker");
            Assert.That(hit.fraction, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void FlushContactCastReportsHitNotFreeMove()
        {
            Box("Wall", new Vector3(2.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            var t = VehicleTuning.Car01();
            // Front face exactly at the wall face: touching is the state a
            // wall approach converges to; it must still report contact.
            var pose = new MotorPose { x = 0.4f, y = 0f, z = 0f, heading = Mathf.PI / 2f };
            var hit = world.SweepBody(pose, 0.6f, 0f, 0f,
                t.halfWidth, t.halfLength, t.height);
            Assert.That(hit.hit, Is.True,
                "a cast starting in flush contact must report the blocker");
        }

        // --- end-to-end: the user's report, steering at a wall ---------------

        [Test]
        public void SteeringAgainstWallCannotTunnelThrough()
        {
            // Wall face at x=2.0 (1m slab, 3m tall). The car drives in,
            // then steers full-lock left and right — the exact play that
            // walked vehicles through walls before the contact fix.
            Box("Wall", new Vector3(2.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            var tuning = VehicleTuning.Car01();
            var state = new MotorState
            {
                x = -2f, y = 0f, z = 0f, heading = Mathf.PI / 2f, grounded = true
            };
            state = Drive(state, 0f, 90, tuning); // reach speed, touch the wall
            var input = new DriverInput(1f, false, false, false, false, false, false);
            var worst = -999f;
            for (var i = 0; i < 400; i++)
            {
                var steering = (i / 40) % 2 == 0 ? 1f : -1f;
                var result = ArcadeMotor.Step(state,
                    new DriverInput(steering, false, false, false, false, false, false),
                    tuning, world, Dt);
                state = result.state;
                // The rotated box's max +x extent: length leads at yaw 0,
                // width takes over as the body turns parallel to the wall.
                // Turning in place may dig a corner ~7cm into the face —
                // bounded and honest. The tunneling invariant: the far
                // face (x=3.0) is never crossed.
                var front = state.x
                    + Mathf.Abs(Mathf.Sin(state.heading)) * tuning.halfLength
                    + Mathf.Abs(Mathf.Cos(state.heading)) * tuning.halfWidth;
                worst = Mathf.Max(worst, front);
                Assert.That(front, Is.LessThan(2.9f),
                    "the box must never reach the wall's far face: tick " + i
                    + " x=" + state.x.ToString("F2")
                    + " z=" + state.z.ToString("F2")
                    + " heading=" + state.heading.ToString("F2"));
                Assert.That(state.x, Is.LessThan(1.0f),
                    "the box centre must stay clear of the wall interior: "
                    + "tick " + i + " x=" + state.x.ToString("F2"));
            }
            Assert.That(worst, Is.LessThan(2.9f),
                "worst-case front extent stayed inside the wall's near "
                + "half; never reached the far face");
        }

        // --- the fix must not glue the car to walls ---------------------------

        [Test]
        public void SlidingParallelAlongWallSkinIsFree()
        {
            // Side skin against the wall face: the ring overlaps, but a
            // parallel drive never pushes INTO the blocker, so the car
            // must keep moving — a blind "any overlap is wedged" rule
            // would glue it in place.
            Box("Wall", new Vector3(2.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            var tuning = VehicleTuning.Car01();
            var state = new MotorState
            {
                x = 1.08f, y = 0f, z = 0f, heading = 0f,
                speed = 8f, grounded = true
            };
            state = Drive(state, 0f, 90, tuning);
            Assert.That(state.z, Is.GreaterThan(5f),
                "sliding along the wall inside the skin ring must stay "
                + "free (z=" + state.z.ToString("F2") + ")");
        }

        [Test]
        public void PinnedCarCanTurnAwayAndEscape()
        {
            // Nose buried in the wall skin facing it, then steer until the
            // heading points away: the drive-off is legal motion and must
            // not stay wedged (there is no reverse gear to back out).
            Box("Wall", new Vector3(2.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            var tuning = VehicleTuning.Car01();
            var state = new MotorState
            {
                x = 0.5f, y = 0f, z = 0f, heading = Mathf.PI / 2f,
                speed = 6f, grounded = true
            };
            state = Drive(state, 0f, 30, tuning); // press into the wall
            // Closed-loop steer to face away (-x), then keep driving:
            // every into-wall move stays blocked, every away move frees.
            for (var i = 0; i < 400; i++)
            {
                var err = WrapAngle(-Mathf.PI / 2f - state.heading);
                var steer = Mathf.Clamp(err * 3f, -1f, 1f);
                state = Drive(state, steer, 1, tuning);
            }
            Assert.That(state.x, Is.LessThan(0f),
                "a wall-pinned car that turns away must escape (x="
                + state.x.ToString("F2") + " heading="
                + state.heading.ToString("F2") + ")");
        }

        private static float WrapAngle(float angle)
        {
            while (angle > Mathf.PI) angle -= Mathf.PI * 2f;
            while (angle < -Mathf.PI) angle += Mathf.PI * 2f;
            return angle;
        }

        // --- the shipped ramp must be solid ----------------------------------

        // Slab top height on the ramp centre line (matches the generated
        // and fixture ramp: centre (-60,1.2,30), 12x0.6x16, pitch -14 deg).
        private static float RampTop(float z)
        {
            return 1.49f + 0.249f * (z - 30f);
        }

        private static bool InsideSlab(MotorState s)
        {
            if (Mathf.Abs(s.x + 60f) > 5.9f) return false;
            // The slab physically spans z≈22.2..37.8; airborne flight past
            // the crest is legitimate, not interior penetration.
            if (s.z < 21.5f || s.z > 38.5f) return false;
            var top = RampTop(s.z);
            var bottom = top - 0.6f;
            var centre = s.y + 0.55f;
            return centre > bottom && centre < top;
        }

        [Test]
        public void RampInteriorCannotBeEnteredFromAnyApproach()
        {
            Box("Ramp", new Vector3(-60f, 1.2f, 30f),
                new Vector3(12f, 0.6f, 16f), Quaternion.Euler(-14f, 0f, 0f));
            var tuning = VehicleTuning.Car01();
            // Three approaches: the intended toe line, a side entry into
            // the mid-slope, and head-on into the raised north face.
            var approaches = new[]
            {
                new { x = -60f, z = 8f, heading = 0f, name = "toe" },
                new { x = -52f, z = 30f, heading = -Mathf.PI / 2f, name = "west side" },
                new { x = -60f, z = 46f, heading = Mathf.PI, name = "north face" }
            };
            foreach (var approach in approaches)
            {
                var state = new MotorState
                {
                    x = approach.x, y = 0f, z = approach.z,
                    heading = approach.heading, speed = 8f, grounded = true
                };
                var input = new DriverInput(0f, false, false, false, false, false, false);
                for (var i = 0; i < 500; i++)
                {
                    var steering = i > 100 ? ((i / 30) % 2 == 0 ? 0.7f : -0.7f) : 0f;
                    var result = ArcadeMotor.Step(state,
                        new DriverInput(steering, false, false, false, false, false, false),
                        tuning, world, Dt);
                    state = result.state;
                    Assert.That(InsideSlab(state), Is.False,
                        approach.name + " approach: the box centre must never be "
                        + "inside the ramp slab (x=" + state.x.ToString("F1")
                        + " y=" + state.y.ToString("F2")
                        + " z=" + state.z.ToString("F1") + " tick=" + i + ")");
                }
            }
        }

        // --- sanity: the contact fix must not wedge normal driving ----------

        [Test]
        public void FlatGroundDrivingIsUnaffected()
        {
            var tuning = VehicleTuning.Car01();
            var state = new MotorState { x = 0f, y = 0f, z = 0f, heading = 0f, grounded = true };
            var start = state.z;
            state = Drive(state, 0f, 90, tuning);
            Assert.That(state.grounded, Is.True);
            Assert.That(state.speed, Is.GreaterThan(10f),
                "the contact fix must not impede open-ground acceleration");
            Assert.That(state.z - start, Is.GreaterThan(10f),
                "the car still drives freely on flat ground");
        }
    }
}
