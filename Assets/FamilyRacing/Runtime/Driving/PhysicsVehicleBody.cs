using FamilyRacing.Core.Driving;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing
{
    // One rigidbody chassis inside a PhysicsCourseWorld: a single compound
    // primitive box collider sized by the tuning, mass/COM/inertia taken
    // from the validated VehiclePhysicsTuning (collider-computed tensor
    // multiplied by the authored per-axis factors — rotation is never
    // frozen and no upright constraint exists). The wheel rays, tire
    // forces and assists live in WheelContactSolver; this class owns the
    // body lifecycle, freeze/restore bookkeeping and read-back into the
    // merged MotorState the Core rule layer consumes.
    public sealed class PhysicsVehicleBody
    {
        // The chassis slides on a near-frictionless material: tire forces
        // are the authored lateral/longitudinal authority, so body contact
        // friction is kept minimal rather than double-counted.
        private static PhysicsMaterial chassisMaterial;

        public readonly string participantId;
        public readonly VehiclePhysicsTuning tuning;
        // The whole vehicle envelope: physical tuning above is the force
        // authority; this carries the rule-economy fields (landing
        // credit thresholds, boost charge) the sim consults for events.
        public readonly VehicleTuning envelope;
        public readonly Rigidbody body;
        public readonly BoxCollider chassis;
        public readonly PhysicsContactProbe probe;

        // Merged live state: physical fields are read back from the body
        // every substep; rule-owned fields (boost economy, drift flags,
        // tick, recovering) are copied in from rulesBefore at Step time.
        public readonly MotorState live = new MotorState();
        public bool frozen { get; private set; }
        public bool chassisGrounded;
        public string chassisContact = "none";
        public float peakContactSpeed;
        public bool newContactThisTick;
        // Airborne->grounded transition observed this tick (emitted as the
        // ordered "landed" event by the sim once per Step).
        public bool landedThisTick;
        public float landedAirSeconds;
        // Facts captured at the landing transition for clean-landing
        // qualification: the airborne episode's peak-to-landing drop and
        // whether it clipped a barrier in flight.
        public float landedPeakDrop;
        public bool landedWallContact;
        // Grounded->airborne transition observed this tick (emitted as the
        // ordered "becameAirborne" event — arcade parity).
        public bool becameAirborneThisTick;
        // An episode shorter than this is suspension flicker, not a jump.
        public const float LandedMinAirSeconds = 0.10f;

        // Injected test forces (oracle isolation): applied every substep
        // until cleared. World-frame force, optionally at a body-local
        // offset so torque is exercised honestly.
        public bool debugForceActive;
        public Vector3 debugForce;
        public bool debugForceAtOffset;
        public Vector3 debugForceLocalOffset;

        // Per-substep wheel scratch, preallocated at spawn: zero managed
        // allocation per wheel/substep after warmup.
        public bool[] wheelHit;
        public Vector3[] wheelPoint;
        public Vector3[] wheelNormal;
        public Vector3[] wheelForward;
        public Vector3[] wheelRight;
        public float[] wheelCompression;
        public float[] wheelNormalLoad;
        public float[] wheelLongSpeed;
        public float[] wheelLatSpeed;

        public VehiclePhysicsState physics { get { return live.physics; } }

        public PhysicsVehicleBody(PhysicsCourseWorld world, VehicleSpawn spawn)
        {
            participantId = spawn.participantId;
            tuning = spawn.tuning.physics;
            envelope = spawn.tuning;
            var go = new GameObject("PhysicsBody-" + participantId);
            go.layer = PhysicsCourseWorld.VehicleLayer;
            chassis = go.AddComponent<BoxCollider>();
            chassis.size = new Vector3(tuning.chassisWidth,
                tuning.chassisHeight, tuning.chassisLength);
            if (chassisMaterial == null)
            {
                chassisMaterial = new PhysicsMaterial("PhysicsChassis")
                {
                    // Moderate body friction: high enough that a curb or
                    // barrier can catch and trip the chassis (real
                    // rollovers), low enough that wall brushes slide.
                    dynamicFriction = 0.5f,
                    staticFriction = 0.5f,
                    bounciness = 0f,
                    frictionCombine = PhysicsMaterialCombine.Average,
                    bounceCombine = PhysicsMaterialCombine.Minimum
                };
            }
            chassis.material = chassisMaterial;
            body = go.AddComponent<Rigidbody>();
            body.mass = tuning.massKg;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.constraints = RigidbodyConstraints.None; // rotation never frozen
            // COM before tensor: the computed inertia is relative to the
            // center of mass. Apply authored multipliers on top — never a
            // hand-picked tensor and never an axis freeze.
            body.centerOfMass = new Vector3(tuning.comOffsetX,
                tuning.comOffsetY, tuning.comOffsetZ);
            var tensor = body.inertiaTensor;
            body.inertiaTensor = new Vector3(
                tensor.x * tuning.inertiaMultiplierX,
                tensor.y * tuning.inertiaMultiplierY,
                tensor.z * tuning.inertiaMultiplierZ);
            body.inertiaTensorRotation = Quaternion.identity;
            probe = go.AddComponent<PhysicsContactProbe>();
            SceneManager.MoveGameObjectToScene(go, world.Scene);

            var count = tuning.wheels.Length;
            wheelHit = new bool[count];
            wheelPoint = new Vector3[count];
            wheelNormal = new Vector3[count];
            wheelForward = new Vector3[count];
            wheelRight = new Vector3[count];
            wheelCompression = new float[count];
            wheelNormalLoad = new float[count];
            wheelLongSpeed = new float[count];
            wheelLatSpeed = new float[count];
            live.physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[count]
            };
            for (var i = 0; i < count; i++)
                live.physics.wheels[i] = new WheelPhysicsState { index = i };
            ApplyRestore(spawn.initialState);
        }

        // Frozen bodies are kinematic with their collider disabled: they
        // neither move nor interfere with active racers (a parked
        // countdown/finished/disabled car is a ghost, not an obstacle).
        public void SetFrozen(bool value)
        {
            if (frozen == value) return;
            frozen = value;
            if (value)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                chassis.enabled = false;
            }
            else
            {
                chassis.enabled = true;
                body.isKinematic = false;
                body.WakeUp();
            }
        }

        // Authoritative reset: pose, velocities, wheel/controller
        // histories, episode bookkeeping and epoch. No gameplay events.
        // A state without physics is derived from the legacy fields
        // (spawn/ recovery poses), a state with physics restores the full
        // replay surface including valid compression histories.
        public void ApplyRestore(MotorState state)
        {
            var wasFrozen = frozen;
            if (wasFrozen) SetFrozen(false);
            live.x = state.x;
            live.y = state.y;
            live.z = state.z;
            live.heading = state.heading;
            live.grounded = state.grounded;
            live.recovering = state.recovering;
            live.lastContact = state.lastContact ?? "none";
            var source = state.physics;
            if (source != null)
            {
                physics.TrySetAttitude(source.attitudeX, source.attitudeY,
                    source.attitudeZ, source.attitudeW);
                physics.velocityX = source.velocityX;
                physics.velocityY = source.velocityY;
                physics.velocityZ = source.velocityZ;
                physics.angularVelocityX = source.angularVelocityX;
                physics.angularVelocityY = source.angularVelocityY;
                physics.angularVelocityZ = source.angularVelocityZ;
                physics.filteredSteering = source.filteredSteering;
                physics.filteredRollRate = source.filteredRollRate;
                physics.balancePrevError = source.balancePrevError;
                physics.driftAmount = source.driftAmount;
                physics.grounded = source.grounded;
                physics.airSeconds = source.airSeconds;
                physics.airPeakY = source.airPeakY;
                physics.airWallContact = source.airWallContact;
                physics.overturnedSeconds = source.overturnedSeconds;
                physics.lastContact = source.lastContact ?? "none";
                physics.recoveryEpoch = source.recoveryEpoch;
                // Prediction restore: preserve a valid wheel history —
                // matching length and contactValid flags are respected;
                // a mismatched/absent array reinitializes cleanly.
                if (source.wheels != null
                    && source.wheels.Length == physics.wheels.Length)
                {
                    for (var i = 0; i < physics.wheels.Length; i++)
                    {
                        var from = source.wheels[i];
                        var to = physics.wheels[i];
                        if (from == null) { to.contactValid = false; continue; }
                        to.grounded = from.grounded;
                        to.compression = from.compression;
                        to.previousCompression = from.previousCompression;
                        to.contactValid = from.contactValid;
                        to.steering = from.steering;
                        to.spin = from.spin;
                    }
                }
                else
                {
                    InvalidateWheelHistory();
                }
            }
            else
            {
                // Legacy-derived spawn/recovery: heading -> yaw attitude,
                // signed forward/lateral/vertical speed -> world velocity,
                // empty wheel and controller histories.
                var yaw = Quaternion.Euler(0f, state.heading * Mathf.Rad2Deg, 0f);
                physics.TrySetAttitude(yaw.x, yaw.y, yaw.z, yaw.w);
                var forward = yaw * Vector3.forward;
                var right = yaw * Vector3.right;
                physics.velocityX = forward.x * state.speed
                    + right.x * state.lateralSpeed;
                physics.velocityY = state.verticalSpeed;
                physics.velocityZ = forward.z * state.speed
                    + right.z * state.lateralSpeed;
                physics.angularVelocityX = 0f;
                physics.angularVelocityY = 0f;
                physics.angularVelocityZ = 0f;
                physics.grounded = state.grounded;
                physics.lastContact = state.lastContact ?? "none";
                InvalidateWheelHistory();
            }
            SyncBodyFromState();
            if (wasFrozen) SetFrozen(true);
        }

        private void InvalidateWheelHistory()
        {
            foreach (var wheel in physics.wheels)
            {
                wheel.grounded = false;
                wheel.compression = 0f;
                wheel.previousCompression = 0f;
                wheel.contactValid = false;
                wheel.steering = 0f;
                wheel.normalLoad = 0f;
                wheel.slipLongitudinal = 0f;
                wheel.slipLateral = 0f;
            }
            physics.filteredSteering = 0f;
            physics.filteredRollRate = 0f;
            physics.balancePrevError = 0f;
            physics.driftAmount = 0f;
            physics.airSeconds = 0f;
            physics.airPeakY = live.y;
            physics.airWallContact = false;
            physics.overturnedSeconds = 0f;
        }

        private void SyncBodyFromState()
        {
            var p = physics;
            body.position = new Vector3(live.x, live.y, live.z);
            body.rotation = new Quaternion(p.attitudeX, p.attitudeY,
                p.attitudeZ, p.attitudeW);
            if (!body.isKinematic)
            {
                body.linearVelocity = new Vector3(p.velocityX,
                    p.velocityY, p.velocityZ);
                body.angularVelocity = new Vector3(p.angularVelocityX,
                    p.angularVelocityY, p.angularVelocityZ);
                // Sleep bookkeeping: a teleported/awakened body must never
                // stay asleep through a restore.
                body.WakeUp();
            }
            RefreshDerivedFields();
        }

        // Reads the simulated body back into the merged live state and
        // updates episode bookkeeping. Called after every substep for
        // active bodies; `dt` is the substep that just ran.
        public void ReadBack(float dt)
        {
            var p = physics;
            var rotation = body.rotation;
            p.TrySetAttitude(rotation.x, rotation.y, rotation.z, rotation.w);
            var velocity = body.linearVelocity;
            p.velocityX = velocity.x;
            p.velocityY = velocity.y;
            p.velocityZ = velocity.z;
            var angular = body.angularVelocity;
            p.angularVelocityX = angular.x;
            p.angularVelocityY = angular.y;
            p.angularVelocityZ = angular.z;

            var anyWheel = false;
            foreach (var wheel in p.wheels) anyWheel |= wheel.grounded;
            var wasGrounded = p.grounded;
            var priorAir = p.airSeconds;
            var grounded = anyWheel || chassisGrounded;
            p.grounded = grounded;
            if (!grounded && wasGrounded)
                becameAirborneThisTick = true;
            if (grounded && !wasGrounded && priorAir >= LandedMinAirSeconds)
            {
                landedThisTick = true;
                landedAirSeconds = priorAir;
                // Capture the episode facts before the airborne
                // bookkeeping clears them below.
                landedPeakDrop = p.airPeakY - body.position.y;
                landedWallContact = p.airWallContact;
            }
            if (grounded)
            {
                p.airSeconds = 0f;
                p.airPeakY = body.position.y;
                p.airWallContact = false;
            }
            else
            {
                p.airSeconds += dt;
                if (body.position.y > p.airPeakY) p.airPeakY = body.position.y;
            }
            // Overturned bookkeeping: continuous time spent clearly not
            // upright while supported by a contact — RecoveryRules (Task
            // 5) owns the 2-second policy; this is the raw episode clock.
            var upDot = Vector3.Dot(body.transform.up, Vector3.up);
            if (upDot < 0.3f && grounded) p.overturnedSeconds += dt;
            else if (upDot > 0.5f) p.overturnedSeconds = 0f;
            // Roll-rate history for the bounded roll-damping assist.
            var rollRate = Vector3.Dot(angular, body.transform.forward);
            p.filteredRollRate += (rollRate - p.filteredRollRate) * 0.25f;

            RefreshDerivedFields();
        }

        // Legacy envelope fields are derived from the physical state —
        // never an independent authority.
        private void RefreshDerivedFields()
        {
            var p = physics;
            live.x = body.position.x;
            live.y = body.position.y;
            live.z = body.position.z;
            var forward = body.transform.forward;
            var flatForward = new Vector3(forward.x, 0f, forward.z);
            if (flatForward.sqrMagnitude > 1e-6f)
            {
                flatForward.Normalize();
                live.heading = Mathf.Atan2(flatForward.x, flatForward.z);
                var v = body.linearVelocity;
                live.speed = v.x * flatForward.x + v.z * flatForward.z;
                live.lateralSpeed = v.x * flatForward.z - v.z * flatForward.x;
                live.verticalSpeed = v.y;
            }
            live.grounded = p.grounded;
            live.airSeconds = p.airSeconds;
            live.airPeakY = p.airPeakY;
            live.airWallContact = p.airWallContact;
            live.lastContact = p.lastContact;
        }

        // Rule-owned fields copied in from the request's rulesBefore.
        // They never overwrite the physical pose.
        public void MergeRules(MotorState rulesBefore)
        {
            if (rulesBefore == null) return;
            live.drifting = rulesBefore.drifting;
            live.driftSign = rulesBefore.driftSign;
            live.boostCharge = rulesBefore.boostCharge;
            live.boostActive = rulesBefore.boostActive;
            live.boostSecondsRemaining = rulesBefore.boostSecondsRemaining;
            live.boostLatched = rulesBefore.boostLatched;
            live.recovering = rulesBefore.recovering;
            live.tick = rulesBefore.tick;
        }

        // Classifies the contacts collected by the probe during the last
        // Simulate. Returns the strongest *new* chassis contact speed (0
        // when only stays were seen) — the sim emits at most one ordered
        // contact event per tick.
        public float ProcessContacts()
        {
            chassisGrounded = false;
            var newest = "none";
            var newestSpeed = 0f;
            var records = probe.records;
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var kind = record.other != null
                    && record.other.attachedRigidbody != null
                        ? "vehicle"
                        : record.normal.y > 0.5f ? "ground" : "wall";
                if (kind == "ground") chassisGrounded = true;
                else if (record.isEnter)
                {
                    if (record.relativeSpeed > newestSpeed)
                    {
                        newestSpeed = record.relativeSpeed;
                        newest = kind;
                    }
                    if (!physics.grounded) physics.airWallContact = true;
                }
            }
            if (newest != "none")
            {
                newContactThisTick = true;
                peakContactSpeed = newestSpeed;
                chassisContact = newest;
                physics.lastContact = newest;
            }
            else if (chassisGrounded)
            {
                physics.lastContact = "ground";
            }
            return newestSpeed;
        }

        public MotorPose PoseNow()
        {
            return new MotorPose
            {
                x = live.x,
                y = live.y,
                z = live.z,
                heading = live.heading
            };
        }
    }
}
