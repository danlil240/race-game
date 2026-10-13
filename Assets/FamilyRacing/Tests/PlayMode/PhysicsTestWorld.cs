using FamilyRacing.Core.Driving;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing.Tests
{
    // Collision-only fixture builder for the physical backend's local
    // scene. Every helper creates primitive boxes with no renderers and
    // moves them into the sim's private scene, so tests exercise real
    // PhysX contact without depending on generated content. Geometry
    // numbers here are the reproducible fixtures the prototype report
    // quotes.
    public static class PhysicsTestWorld
    {
        public const float Dt = 1f / 30f;
        public const float SubstepDt = 1f / 60f;

        // A collision-only box inside the world's local scene. Renderers
        // are stripped: the render meshes stay a separate concern.
        public static GameObject Box(PhysicsCourseWorld world, string name,
            Vector3 position, Vector3 size)
        {
            return Box(world, name, position, size, Quaternion.identity);
        }

        public static GameObject Box(PhysicsCourseWorld world, string name,
            Vector3 position, Vector3 size, Quaternion rotation)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            var renderer = box.GetComponent<MeshRenderer>();
            if (renderer != null) Object.DestroyImmediate(renderer);
            box.transform.position = position;
            box.transform.rotation = rotation;
            box.transform.localScale = size;
            SceneManager.MoveGameObjectToScene(box, world.Scene);
            return box;
        }

        // Flat drivable lane: top surface at y=0.
        public static GameObject FlatGround(PhysicsCourseWorld world,
            float centerX, float centerZ, float sizeX, float sizeZ)
        {
            return Box(world, "Ground", new Vector3(centerX, -0.5f, centerZ),
                new Vector3(sizeX, 1f, sizeZ));
        }

        // VehicleTuning envelope selecting the physical backend with the
        // given (already valid) physics tuning.
        public static VehicleTuning PhysicalTuning(string vehicleId,
            VehiclePhysicsTuning physics)
        {
            var tuning = VehicleTuning.Car01();
            tuning.vehicleId = vehicleId;
            tuning.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            tuning.physics = physics;
            return tuning;
        }

        // The labeled high-COM untripped-roll preset from the brief:
        // stock geometry with the center of mass lifted above the body
        // origin and extra grip so a flat full-lock turn rolls without
        // tripping on geometry.
        public static VehiclePhysicsTuning HighComCar()
        {
            var tuning = VehiclePhysicsTuning.Car01();
            tuning.comOffsetY = 0.55f;
            tuning.frictionCoefficient = 1.8f;
            tuning.steerReferenceSpeed = 60f; // keep low-speed authority at test speed
            tuning.cruiseSpeed = 30f;
            tuning.boostSpeed = 30f;
            tuning.rollDamping = 0f;
            return tuning;
        }

        // Spawn height above ground top (y=0) for a settled start: the
        // suspension mounts hang `rest - expectedCompression + radius`
        // below the body origin at rest; spawn slightly high so the struts
        // settle down onto the road instead of starting pre-loaded.
        public static float SpawnY(VehiclePhysicsTuning tuning)
        {
            var perWheel = tuning.massKg * VehiclePhysicsTuning.StandardGravity
                / (tuning.wheels.Length * tuning.springRatePerWheel);
            var lowestMountY = float.MaxValue;
            foreach (var wheel in tuning.wheels)
                if (wheel.mountY < lowestMountY) lowestMountY = wheel.mountY;
            return tuning.wheels[0].radius + tuning.suspensionRestLength
                - perWheel - lowestMountY + 0.05f;
        }

        // A MotorState for a physical spawn: legacy envelope fields plus a
        // physics state with authored wheel slots, upright attitude at the
        // given heading and an optional forward speed.
        public static MotorState PhysicalSpawnState(VehiclePhysicsTuning tuning,
            Vector3 position, float headingRadians, float forwardSpeed = 0f)
        {
            var state = new MotorState
            {
                x = position.x,
                y = position.y,
                z = position.z,
                heading = headingRadians,
                speed = forwardSpeed,
                grounded = true
            };
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[tuning.wheels.Length]
            };
            for (var i = 0; i < physics.wheels.Length; i++)
                physics.wheels[i] = new WheelPhysicsState { index = i };
            var half = headingRadians * 0.5f;
            physics.TrySetAttitude(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
            physics.velocityZ = Mathf.Cos(headingRadians) * forwardSpeed;
            physics.velocityX = Mathf.Sin(headingRadians) * forwardSpeed;
            physics.grounded = true;
            state.physics = physics;
            return state;
        }

        public static DriverInput Drive(float steering = 0f, float brake = 0f,
            float? throttleOverride = null, bool drift = false)
        {
            return new DriverInput(steering, drift, false, false, false,
                false, false, throttleOverride, brake);
        }

        public static VehicleTickRequest Request(string participantId,
            DriverInput input, bool simulateMotion = true,
            MotorState rulesBefore = null)
        {
            return new VehicleTickRequest
            {
                participantId = participantId,
                input = input,
                rulesBefore = rulesBefore,
                simulateMotion = simulateMotion
            };
        }

        // Signed forward speed along the body heading (the merge target
        // for the legacy `speed` field).
        public static float ForwardSpeed(MotorState state)
        {
            var physics = state.physics;
            // Quaternion-rotated +Z: fwd = (2(xz+wy), 2(yz-wx), 1-2(x2+y2)).
            var fwdX = 2f * (physics.attitudeX * physics.attitudeZ
                + physics.attitudeW * physics.attitudeY);
            var fwdZ = 1f - 2f * (physics.attitudeX * physics.attitudeX
                + physics.attitudeY * physics.attitudeY);
            var len = Mathf.Sqrt(fwdX * fwdX + fwdZ * fwdZ);
            if (len < 1e-6f) return 0f;
            return (physics.velocityX * fwdX + physics.velocityZ * fwdZ) / len;
        }

        // dot(bodyUp, worldUp) from the attitude quaternion: <0 means the
        // chassis is past 90 degrees of roll/pitch.
        public static float BodyUpDot(MotorState state)
        {
            var p = state.physics;
            // Rotate world +Y by the attitude: up.y = 1 - 2(x^2 + z^2).
            return 1f - 2f * (p.attitudeX * p.attitudeX + p.attitudeZ * p.attitudeZ);
        }
    }
}
