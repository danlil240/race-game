using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Developer car laboratory: a playable harness for the physical
    // backend outside the race rule layer. It owns a PhysicsCourseWorld
    // built from a collision-only clone of the lab course prefab, steps
    // the shared world from the render loop through a plain accumulator
    // (the only clock — physics never hooks Update/FixedUpdate itself)
    // and reads committed poses onto a collider-free visual. Selected by
    // opening the generated PhysicsLab scene; ordinary gameplay and the
    // arcade backend are untouched — this backend stays opt-in.
    public sealed class PhysicsLabWorld : MonoBehaviour
    {
        [SerializeField] private GameObject coursePrefab;
        [SerializeField] private VehiclePhysicsTuningAsset physicsTuning;
        [SerializeField] private Vector3 spawnPosition = new Vector3(0f, 0.8f, -140f);
        [SerializeField] private float spawnHeadingDegrees;

        private PhysicsCourseWorld world;
        private PhysicsVehicleSimulation sim;
        private VehiclePhysicsTuning tuning;
        private GameObject visual;
        private Camera followCamera;
        private float accumulator;
        private const float Tick = 1f / 30f;
        private const float MaxAccumulated = 4f * Tick; // bounded backlog

        private void Start()
        {
            if (physicsTuning == null)
            {
                Debug.LogError("PhysicsLabWorld: no physics tuning asset.");
                enabled = false;
                return;
            }
            string error;
            tuning = physicsTuning.ToTuning(out error);
            if (tuning == null)
            {
                Debug.LogError("PhysicsLabWorld: " + error);
                enabled = false;
                return;
            }
            world = coursePrefab != null
                ? PhysicsCourseWorld.Create("PhysicsLab", coursePrefab)
                : PhysicsCourseWorld.Create("PhysicsLab");
            sim = new PhysicsVehicleSimulation(world);
            var vehicleTuning = VehicleTuning.Car01();
            vehicleTuning.simulationBackend =
                VehicleSimulationBackend.PhysicalAssisted;
            vehicleTuning.physics = tuning;
            var spawn = new MotorState
            {
                x = spawnPosition.x,
                y = spawnPosition.y,
                z = spawnPosition.z,
                heading = spawnHeadingDegrees * Mathf.Deg2Rad,
                grounded = true
            };
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = "lab",
                    tuning = vehicleTuning,
                    initialState = spawn
                }
            });

            // Collider-free visual: reads the committed pose only — it can
            // never write back into the chassis.
            visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "PhysicsLabVisual";
            DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.localScale = new Vector3(tuning.chassisWidth,
                tuning.chassisHeight, tuning.chassisLength);

            followCamera = Camera.main;
        }

        private void Update()
        {
            if (sim == null) return;
            accumulator = Mathf.Min(accumulator + Time.deltaTime,
                MaxAccumulated);
            var brake = (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)
                ? 1f : 0f);
            var steering = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
                steering -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
                steering += 1f;
            var input = new DriverInput(steering,
                Input.GetKey(KeyCode.Space), false, false,
                false, false, false, null, brake);
            // R is a once-per-press recovery: the lab has no rule layer,
            // so the press edge lands the same full controller reset the
            // session applies (cleared histories, bumped epoch).
            if (Input.GetKeyDown(KeyCode.R))
                RequestRecovery();
            while (accumulator >= Tick)
            {
                sim.Step(new List<VehicleTickRequest>
                {
                    new VehicleTickRequest
                    {
                        participantId = "lab",
                        input = input,
                        simulateMotion = true
                    }
                });
                accumulator -= Tick;
            }
            var state = sim.Capture("lab");
            if (state == null || visual == null) return;
            var p = state.physics;
            visual.transform.SetPositionAndRotation(
                new Vector3(state.x, state.y, state.z),
                new Quaternion(p.attitudeX, p.attitudeY, p.attitudeZ,
                    p.attitudeW));
            if (followCamera != null)
            {
                var forward = visual.transform.forward;
                var flat = new Vector3(forward.x, 0f, forward.z).normalized;
                followCamera.transform.position = visual.transform.position
                    - flat * 9f + Vector3.up * 4f;
                followCamera.transform.LookAt(visual.transform.position
                    + Vector3.up * 1.2f);
            }
        }

        // The lab's manual recovery: respawn at the authored spawn with a
        // fully reset controller — the same shape the session's physical
        // reset builds (zero velocities, invalidated wheel/damper and
        // controller histories, upright attitude, bumped epoch).
        private void RequestRecovery()
        {
            if (sim == null || tuning == null) return;
            var current = sim.Capture("lab");
            var epoch = current != null && current.physics != null
                ? current.physics.recoveryEpoch + 1L : 1L;
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[tuning.wheels.Length],
                grounded = true,
                airPeakY = spawnPosition.y,
                lastContact = "ground",
                recoveryEpoch = epoch
            };
            for (var i = 0; i < physics.wheels.Length; i++)
                physics.wheels[i] = new WheelPhysicsState { index = i };
            var halfYaw = spawnHeadingDegrees * Mathf.Deg2Rad * 0.5f;
            physics.TrySetAttitude(0f, Mathf.Sin(halfYaw), 0f,
                Mathf.Cos(halfYaw));
            sim.Restore("lab", new MotorState
            {
                x = spawnPosition.x,
                y = spawnPosition.y,
                z = spawnPosition.z,
                heading = spawnHeadingDegrees * Mathf.Deg2Rad,
                grounded = true,
                lastContact = "ground",
                physics = physics
            });
        }

        private void OnDestroy()
        {
            if (sim != null) sim.Dispose();
            sim = null;
        }
    }
}
