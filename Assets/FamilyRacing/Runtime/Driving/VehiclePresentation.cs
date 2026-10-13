using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Graybox vehicle visual: a root parked at the motor's committed
    // pose, a lean pivot that carries the cosmetic body roll in the
    // arcade presentation, and child primitives assembled into a
    // per-class silhouette. The lean pivot only rotates the visual —
    // collision is always the motor's swept box in Core, so the arcade
    // motorcycle can lean hard while its stable collision body stays
    // upright. No colliders: the world never sees these meshes.
    //
    // A physical motor (motor.physics != null) renders differently: the
    // root takes the full committed body attitude — roll and pitch
    // included, quaternion-slerped between 30 Hz commits along the
    // shortest arc — and each visible wheel follows its committed
    // suspension travel, road-wheel steer and travel-derived spin. The
    // cosmetic lean pivot stays neutral there: the physical chassis
    // roll already IS the motorcycle's lean, so applying the cosmetic
    // lean again would double it. A recovery epoch bump snaps the pose
    // instead of interpolating through a teleport the sim never made.
    public sealed class VehiclePresentation : MonoBehaviour
    {
        private const float LeanSmoothing = 10f;

        private Transform leanPivot;
        private readonly List<GameObject> parts = new List<GameObject>();
        private Material bodyMaterial;
        private Material darkMaterial;
        private float leanMaxDegrees;
        private float leanRoll;
        private string shownKey = "";

        // Physical wheel rig: one authored mount/steer spec per visible
        // wheel transform, in the tuning's wheel order.
        private struct PhysWheelVisual
        {
            public Vector3 mount;
            public float radius;
            public bool steerable;
        }
        private PhysWheelVisual[] physWheels;
        private Transform[] wheelVisuals = new Transform[0];
        private float physRestLength;
        private float physExtraDroop;
        // The rendered (interpolated) wheel channel committed per sample.
        private struct WheelSample
        {
            public float compression;
            public float steer;
            public float spin;
            public bool grounded;
        }

        // (Re)builds the silhouette for the given definition. Safe to
        // call every frame — it only rebuilds when the key changes.
        public void SetVehicle(VehicleDefinitionAsset asset)
        {
            if (asset == null) return;
            EnsureBuilt();
            var key = asset.silhouette + "|" + asset.vehicleId;
            if (bodyMaterial != null) bodyMaterial.color = asset.bodyColor;
            leanMaxDegrees = asset.leanDegrees;
            if (key == shownKey) return;
            shownKey = key;
            Rebuild(asset);
        }

        // Committed motor pose → root transform; steering hold → cosmetic
        // roll on the lean pivot. The root itself only yaws: roll can
        // never leak into the collision body's orientation.
        //
        // The sim commits at a fixed 30 Hz tick while this runs per
        // rendered frame — snapping straight to the committed pose makes
        // the vehicle visibly step/judder against the smoothly-smoothed
        // camera. Instead the pose interpolates between the last two
        // commits by the render-time alpha (fraction of the current tick
        // elapsed), at the cost of at most one tick of visual lag.
        public void ApplyPose(MotorState motor, float steering)
        {
            ApplyPose(motor, steering, 1f);
        }

        public void ApplyPose(MotorState motor, float steering, float alpha)
        {
            if (leanPivot == null || motor == null) return;
            if (motor.physics != null)
            {
                ApplyPhysicalPose(motor, alpha);
                return;
            }
            var pos = new Vector3(motor.x, motor.y, motor.z);
            if (motor.tick != interpTick)
            {
                interpPrevPos = interpCurPos;
                interpPrevYaw = interpCurYaw;
                interpCurPos = pos;
                interpCurYaw = motor.heading;
                interpTick = motor.tick;
                // A respawn or any teleport-scale jump must snap, never
                // sweep the body across the course for a frame.
                if ((interpCurPos - interpPrevPos).sqrMagnitude
                        > TeleportSnapDistance * TeleportSnapDistance)
                {
                    interpPrevPos = interpCurPos;
                    interpPrevYaw = interpCurYaw;
                }
            }
            var a = Mathf.Clamp01(alpha);
            var prevDeg = interpPrevYaw * Mathf.Rad2Deg;
            var yaw = prevDeg
                + Mathf.DeltaAngle(prevDeg, interpCurYaw * Mathf.Rad2Deg) * a;
            transform.SetPositionAndRotation(
                Vector3.LerpUnclamped(interpPrevPos, interpCurPos, a),
                Quaternion.Euler(0f, yaw, 0f));
            var target = -Mathf.Clamp(steering, -1f, 1f) * leanMaxDegrees;
            leanRoll = Mathf.Lerp(leanRoll, target,
                1f - Mathf.Exp(-LeanSmoothing * Time.deltaTime));
            leanPivot.localRotation = Quaternion.Euler(0f, 0f, leanRoll);
        }

        // Committed poses the visual interpolates between, plus the last
        // tick applied. TeleportSnapDistance exceeds the largest legal
        // per-tick move (boost top speed ~28 m/s ≈ 0.94 m a tick), so
        // anything bigger is a respawn, not motion.
        private const float TeleportSnapDistance = 2f;
        private Vector3 interpPrevPos, interpCurPos;
        private float interpPrevYaw, interpCurYaw;
        private long interpTick = -1;

        // The physical channel: same committed-boundary interpolation as
        // the legacy path, but the rendered orientation is the full
        // committed attitude quaternion — slerped along the shortest arc
        // so a +/-180-degree wrap never swings back through zero.
        private Vector3 physPrevPos, physCurPos;
        private Quaternion physPrevAtt = Quaternion.identity;
        private Quaternion physCurAtt = Quaternion.identity;
        private WheelSample[] physPrevWheels, physCurWheels;
        private long physTick = -1;
        private long physEpoch = -1;
        private bool physHasPose;

        // Physical pose: the committed body attitude reaches the root
        // unchanged (the physics is the authority — the render layer may
        // never re-upright it), position/attitude/wheel state interpolate
        // between the last two committed boundaries, and a recovery
        // epoch bump or teleport-scale jump snaps to the new commit.
        private void ApplyPhysicalPose(MotorState motor, float alpha)
        {
            var phys = motor.physics;
            var pos = new Vector3(motor.x, motor.y, motor.z);
            var att = new Quaternion(phys.attitudeX, phys.attitudeY,
                phys.attitudeZ, phys.attitudeW);
            if (motor.tick != physTick)
            {
                physPrevPos = physCurPos;
                physPrevAtt = physCurAtt;
                physPrevWheels = physCurWheels;
                physCurPos = pos;
                physCurAtt = att;
                physCurWheels = SnapshotWheels(phys.wheels);
                physTick = motor.tick;
                if (!physHasPose || phys.recoveryEpoch != physEpoch
                    || (physCurPos - physPrevPos).sqrMagnitude
                        > TeleportSnapDistance * TeleportSnapDistance)
                {
                    // First sample, respawn teleport, or a recovery
                    // epoch change: land exactly on the new commit —
                    // never sweep through poses the sim never occupied.
                    physPrevPos = physCurPos;
                    physPrevAtt = physCurAtt;
                    physPrevWheels = physCurWheels;
                }
                physEpoch = phys.recoveryEpoch;
                physHasPose = true;
            }
            var a = Mathf.Clamp01(alpha);
            // Quaternion sign is not canonicalized by the transport —
            // negate the target into the prev hemisphere so the slerp
            // always takes the short arc.
            var target = physCurAtt;
            if (Quaternion.Dot(physPrevAtt, target) < 0f)
                target = new Quaternion(-target.x, -target.y, -target.z,
                    -target.w);
            transform.SetPositionAndRotation(
                Vector3.LerpUnclamped(physPrevPos, physCurPos, a),
                Quaternion.Slerp(physPrevAtt, target, a));
            // Physical chassis roll is the lean. The cosmetic pivot
            // stays identity so steering can never stack a second roll
            // on top of the one the physics already committed.
            leanRoll = 0f;
            leanPivot.localRotation = Quaternion.identity;
            ApplyPhysicalWheels(physPrevWheels, physCurWheels, a);
        }

        private static WheelSample[] SnapshotWheels(WheelPhysicsState[] wheels)
        {
            if (wheels == null) return null;
            var snapshot = new WheelSample[wheels.Length];
            for (var i = 0; i < wheels.Length; i++)
            {
                var wheel = wheels[i];
                if (wheel == null) continue;
                snapshot[i].grounded = wheel.grounded;
                snapshot[i].compression = wheel.compression;
                snapshot[i].steer = wheel.steering;
                snapshot[i].spin = wheel.spin;
            }
            return snapshot;
        }

        // Visible wheels read the committed suspension state: strut
        // length (rest - compression, or rest + droop when airborne),
        // the road-wheel steer on steerable wheels only, and spin that
        // the solver accumulates from real contact travel — never an
        // engine-RPM fiction.
        private void ApplyPhysicalWheels(WheelSample[] prev,
            WheelSample[] cur, float a)
        {
            if (physWheels == null || wheelVisuals == null) return;
            var count = Mathf.Min(physWheels.Length, wheelVisuals.Length);
            for (var i = 0; i < count; i++)
            {
                var part = wheelVisuals[i];
                if (part == null) continue;
                var c = cur != null && i < cur.Length ? cur[i] : new WheelSample();
                var p = prev != null && i < prev.Length ? prev[i] : c;
                var spec = physWheels[i];
                var compression = Mathf.Lerp(p.compression, c.compression, a);
                var spring = c.grounded
                    ? Mathf.Max(0f, physRestLength - compression)
                    : physRestLength + physExtraDroop;
                var steer = spec.steerable
                    ? Mathf.Lerp(p.steer, c.steer, a) : 0f;
                var spin = Mathf.Lerp(p.spin, c.spin, a);
                part.localPosition = new Vector3(spec.mount.x,
                    spec.mount.y - spring, spec.mount.z);
                // Steer yaw wraps the axle tilt and roll spin: road-wheel
                // direction first, then the cylinder's axle laid onto the
                // lateral axis, then the travel-derived roll about it.
                part.localRotation = Quaternion.Euler(0f,
                        steer * Mathf.Rad2Deg, 0f)
                    * Quaternion.Euler(0f, 0f, 90f)
                    * Quaternion.Euler(0f, spin * Mathf.Rad2Deg, 0f);
            }
        }

        public float LeanRoll { get { return leanRoll; } }
        public string ShownKey { get { return shownKey; } }

        private void EnsureBuilt()
        {
            if (leanPivot != null) return;
            var pivot = new GameObject("LeanPivot");
            pivot.transform.SetParent(transform, false);
            leanPivot = pivot.transform;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            bodyMaterial = new Material(shader);
            darkMaterial = new Material(shader) { color = new Color(0.16f, 0.17f, 0.19f) };
        }

        private void Rebuild(VehicleDefinitionAsset asset)
        {
            // DestroyImmediate: a deferred Destroy would leave the old
            // silhouette's parts alive in the scene for a full frame —
            // visible to same-frame queries and physics alike.
            foreach (var part in parts)
                if (part != null) DestroyImmediate(part);
            parts.Clear();
            physWheels = null;
            wheelVisuals = new Transform[0];
            if (asset.physicsTuning != null
                && asset.simulationBackend
                    == VehicleSimulationBackend.PhysicalAssisted)
            {
                BuildPhysical(asset);
            }
            else if (asset.silhouette == "motorcycle")
                BuildMotorcycle(asset);
            else if (asset.silhouette == "car")
                BuildCar(asset);
            else
            {
                // Unknown silhouette falls back to a plain scaled body so
                // new classes can never render as an invisible vehicle.
                Part(PrimitiveType.Cube, "Body", bodyMaterial,
                    new Vector3(0f, asset.bodyScale.y * 0.5f, 0f),
                    asset.bodyScale);
            }
        }

        // Car: a wide low body, a cabin set toward the rear, and four
        // dark wheels — reads as a boxy rally car at a glance.
        private void BuildCar(VehicleDefinitionAsset asset)
        {
            var s = asset.bodyScale;
            Part(PrimitiveType.Cube, "Body", bodyMaterial,
                new Vector3(0f, 0.18f + s.y * 0.5f, 0f), s);
            Part(PrimitiveType.Cube, "Cabin", bodyMaterial,
                new Vector3(0f, 0.18f + s.y + 0.24f, -s.z * 0.1f),
                new Vector3(s.x * 0.72f, 0.48f, s.z * 0.42f));
            var wheel = new Vector3(0.62f, 0.14f, 0.62f);
            var wx = s.x * 0.5f - 0.1f;
            var wz = s.z * 0.5f - 0.55f;
            Part(PrimitiveType.Cylinder, "WheelFL", darkMaterial,
                new Vector3(-wx, 0.31f, wz), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelFR", darkMaterial,
                new Vector3(wx, 0.31f, wz), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelRL", darkMaterial,
                new Vector3(-wx, 0.31f, -wz), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelRR", darkMaterial,
                new Vector3(wx, 0.31f, -wz), wheel, Quaternion.Euler(0f, 0f, 90f));
        }

        // Motorcycle: two tall wheels, a narrow frame, handlebars and a
        // rider — a visibly different silhouette from the car even in
        // flat graybox shading.
        private void BuildMotorcycle(VehicleDefinitionAsset asset)
        {
            var s = asset.bodyScale;
            var wheel = new Vector3(0.78f, 0.08f, 0.78f);
            Part(PrimitiveType.Cylinder, "WheelF", darkMaterial,
                new Vector3(0f, 0.39f, s.z * 0.38f), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelR", darkMaterial,
                new Vector3(0f, 0.39f, -s.z * 0.36f), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cube, "Frame", bodyMaterial,
                new Vector3(0f, 0.78f, 0f), new Vector3(0.3f, 0.45f, s.z * 0.66f));
            Part(PrimitiveType.Cube, "Handlebar", darkMaterial,
                new Vector3(0f, 1.28f, s.z * 0.33f), new Vector3(0.72f, 0.07f, 0.12f));
            Part(PrimitiveType.Cube, "Rider", bodyMaterial,
                new Vector3(0f, 1.32f, -s.z * 0.08f), new Vector3(0.4f, 0.62f, 0.36f));
            Part(PrimitiveType.Sphere, "Head", darkMaterial,
                new Vector3(0f, 1.82f, -s.z * 0.04f), new Vector3(0.3f, 0.3f, 0.3f));
        }

        // Physical silhouette: the authored chassis box is the body and
        // every wheel part sits at its authored suspension mount, so the
        // render transform IS the rigid-body frame — the physics body
        // origin anchors at (0,0,0) of this hierarchy, not at ground
        // level the way the arcade silhouette assumes.
        private void BuildPhysical(VehicleDefinitionAsset asset)
        {
            var tuning = asset.physicsTuning;
            var chassis = tuning.chassisSize;
            physRestLength = tuning.suspensionRestLength;
            physExtraDroop = tuning.suspensionExtraDroop;
            var specs = tuning.wheels != null ? tuning.wheels.Length : 0;
            physWheels = new PhysWheelVisual[specs];
            wheelVisuals = new Transform[specs];

            Part(PrimitiveType.Cube, "Body", bodyMaterial,
                Vector3.zero, chassis);
            Part(PrimitiveType.Cube, "Cabin", bodyMaterial,
                new Vector3(0f, chassis.y * 0.5f + 0.2f, -chassis.z * 0.1f),
                new Vector3(chassis.x * 0.72f, 0.4f, chassis.z * 0.42f));
            if (specs == 2)
            {
                // Inline motorcycle: frame over the chassis, bars and a
                // rider — the lean lives on the root now, not the pivot.
                Part(PrimitiveType.Cube, "Handlebar", darkMaterial,
                    new Vector3(0f, chassis.y * 0.5f + 0.28f,
                        chassis.z * 0.33f),
                    new Vector3(0.72f, 0.07f, 0.12f));
                Part(PrimitiveType.Cube, "Rider", bodyMaterial,
                    new Vector3(0f, chassis.y * 0.5f + 0.32f,
                        -chassis.z * 0.08f),
                    new Vector3(0.4f, 0.62f, 0.36f));
            }
            var names = specs == 4
                ? new[] { "WheelFL", "WheelFR", "WheelRL", "WheelRR" }
                : specs == 2
                    ? new[] { "WheelF", "WheelR" }
                    : null;
            for (var i = 0; i < specs; i++)
            {
                var spec = tuning.wheels[i];
                if (spec == null) continue;
                physWheels[i] = new PhysWheelVisual
                {
                    mount = spec.mount,
                    radius = spec.radius,
                    steerable = spec.steerable
                };
                var wheel = Part(PrimitiveType.Cylinder,
                    names != null ? names[i] : "Wheel" + i, darkMaterial,
                    spec.mount, new Vector3(spec.radius * 2f,
                        specs == 2 ? 0.04f : 0.14f, spec.radius * 2f),
                    Quaternion.Euler(0f, 0f, 90f));
                wheelVisuals[i] = wheel.transform;
            }
        }

        private GameObject Part(PrimitiveType primitive, string name,
            Material material, Vector3 localPosition, Vector3 localScale)
        {
            return Part(primitive, name, material, localPosition, localScale,
                Quaternion.identity);
        }

        private GameObject Part(PrimitiveType primitive, string name,
            Material material, Vector3 localPosition, Vector3 localScale,
            Quaternion localRotation)
        {
            var part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(leanPivot, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = localRotation;
            part.transform.localScale = localScale;
            // Visual only: collision lives entirely in the motor's swept
            // box, so no part may carry a collider. DestroyImmediate —
            // a deferred Destroy would leave the collider live in the
            // physics scene for a full frame, where camera occlusion
            // linecasts and same-frame motor sweeps can still hit it.
            var collider = part.GetComponent<Collider>();
            if (collider != null) DestroyImmediate(collider);
            part.GetComponent<Renderer>().sharedMaterial = material;
            parts.Add(part);
            return part;
        }

        private void OnDestroy()
        {
            if (bodyMaterial != null) Destroy(bodyMaterial);
            if (darkMaterial != null) Destroy(darkMaterial);
        }
    }
}
