using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // The Task-4 presentation camera. While a driving session has a motor
    // pose it consumes committed simulation state (never modifies physics)
    // plus the active profile draft's camera preference, and drives the
    // render camera in one of two real modes:
    //   chase    — smoothed behind-and-above the vehicle, pulled in front of
    //              solid geometry that would block the view (triggers such
    //              as checkpoint volumes are ignored);
    //   overhead — an elevated, gently tilted follower that likewise drops
    //              in front of any solid geometry occluding the downward
    //              view (canopies, bridges, tall blocks).
    // Both modes converge with exponential smoothing; "reduce camera shake"
    // slows the convergence instead of switching behaviour off. A
    // teleport-scale target jump (respawn) snaps rather than gliding the
    // camera through the world. The actual mode, pose and target are
    // recorded on Facts and published into driving.camera observation so
    // captures see what framing the player really had — the sim never
    // reads them back.
    public sealed class DrivingCamera : MonoBehaviour
    {
        [SerializeField] private float chaseBack = 8f;
        [SerializeField] private float chaseUp = 4.5f;
        [SerializeField] private float overheadUp = 30f;
        [SerializeField] private float overheadBack = 7f;
        [SerializeField] private float targetUp = 1.2f;
        [SerializeField] private float followRate = 9f;
        [SerializeField] private float reducedFollowRate = 4f;
        [SerializeField] private float snapDistance = 18f;
        [SerializeField] private float obstacleMargin = 0.4f;
        // Pull-in converges fast so a blocker never hides the vehicle;
        // release eases back slowly so a corner's on/off occlusion can
        // never pump the framing between two depths.
        [SerializeField] private float pullInRate = 14f;
        [SerializeField] private float releaseRate = 3.5f;

        private Camera cam;
        private bool engaged;
        private bool savedPose;
        private Vector3 savedPosition;
        private Quaternion savedRotation;
        private Vector3 smoothTarget;
        private bool hasTarget;
        private float pullDistance = -1f;
        private readonly DrivingCameraState facts = new DrivingCameraState();

        public DrivingCameraState Facts { get { return facts; } }
        public Camera RenderCamera { get { return cam; } }

        // Diagnostics for tests/debugging: which collider most recently
        // pulled the camera in (null when the view is clear).
        internal string LastObstacleName { get; private set; }

        // Capture the pre-game camera pose as early as possible — before any
        // session-driven framing has moved the camera — so race teardown can
        // put it back where the menu screens left it.
        private void Awake()
        {
            if (cam == null) cam = Camera.main;
            if (cam != null && !savedPose)
            {
                savedPosition = cam.transform.position;
                savedRotation = cam.transform.rotation;
                savedPose = true;
            }
        }

        // Per rendered frame (FoundationApp drives this after DrivingWorld
        // has applied visuals): wall-clock delta for smoothing, matching the
        // presentation-side unscaled clock.
        public void ApplyFrame(FoundationState state)
        {
            ApplyFrame(state, Time.unscaledDeltaTime);
        }

        internal void ApplyFrame(FoundationState state, float deltaSeconds)
        {
            var session = state != null ? state.driving : null;
            var motor = session != null ? session.motor : null;
            if (cam == null) cam = Camera.main;
            if (motor == null || cam == null)
            {
                if (engaged) Disengage();
                ClearFacts();
                return;
            }
            ApplyMotor(motor,
                state.draftProfile != null
                    && state.draftProfile.camera == "overhead",
                state.draftProfile != null
                    && state.draftProfile.reducedCameraShake,
                deltaSeconds);
        }

        // Task 6 seam: frame any committed motor pose — the LAN race
        // screen follows the locally owned racer's *predicted* (visually
        // blended) pose, not a raw snapshot pose, so corrections ease in
        // the same way solo driving does. Callers pass the same camera
        // preference facts the solo path reads from the draft profile.
        internal void ApplyMotor(MotorState motor, bool overhead,
            bool reducedShake, float deltaSeconds)
        {
            if (cam == null) cam = Camera.main;
            if (motor == null || cam == null)
            {
                if (engaged) Disengage();
                ClearFacts();
                return;
            }
            if (!engaged)
            {
                engaged = true;
                hasTarget = false; // engagement snaps into framing, no swoop
            }

            var mode = overhead ? "overhead" : "chase";
            var reduced = reducedShake;
            var rate = reduced ? reducedFollowRate : followRate;
            var dt = Mathf.Max(0f, deltaSeconds);
            var blend = 1f - Mathf.Exp(-rate * dt);

            var rawTarget = new Vector3(motor.x, motor.y + targetUp, motor.z);
            var firstFrame = !hasTarget;
            var teleported = hasTarget &&
                (rawTarget - smoothTarget).sqrMagnitude > snapDistance * snapDistance;
            if (firstFrame || teleported) smoothTarget = rawTarget;
            else smoothTarget = Vector3.Lerp(smoothTarget, rawTarget, blend);
            hasTarget = true;

            var desired = mode == "overhead"
                ? OverheadDesired(smoothTarget)
                : ChaseDesired(motor, smoothTarget);
            desired = ClearOfObstacles(smoothTarget, desired, dt,
                firstFrame || teleported);
            var lookDir = smoothTarget - desired;
            // An occluder flush against the vehicle can pull the camera
            // nearly onto the target; keep the previous heading then.
            var look = lookDir.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(lookDir, Vector3.up)
                : cam.transform.rotation;
            if (firstFrame || teleported)
            {
                cam.transform.SetPositionAndRotation(desired, look);
            }
            else
            {
                cam.transform.SetPositionAndRotation(
                    Vector3.Lerp(cam.transform.position, desired, blend),
                    Quaternion.Slerp(cam.transform.rotation, look, blend));
            }

            facts.mode = mode;
            facts.px = cam.transform.position.x;
            facts.py = cam.transform.position.y;
            facts.pz = cam.transform.position.z;
            facts.tx = smoothTarget.x;
            facts.ty = smoothTarget.y;
            facts.tz = smoothTarget.z;
        }

        private Vector3 ChaseDesired(MotorState motor, Vector3 target)
        {
            var forward = new Vector3(
                Mathf.Sin(motor.heading), 0f, Mathf.Cos(motor.heading));
            return target - forward * chaseBack + Vector3.up * chaseUp;
        }

        private Vector3 OverheadDesired(Vector3 target)
        {
            return target + new Vector3(0f, overheadUp, -overheadBack);
        }

        // Pull the camera along the target→ideal ray to the first solid
        // occluder so the vehicle stays visible. Trigger volumes
        // (checkpoint gates, the drift pad) are not view obstacles, and
        // neither is the vehicle's own silhouette — the ray can exit
        // through the Cabin/Rider parts the presentation builds. Shared by
        // both modes: chase yields side/behind blockers, overhead yields
        // canopies and tall blocks over the vehicle.
        //
        // The pull *distance* is smoothed, not the position: near corners
        // the occluder flickers on and off the ray every other frame, and
        // an instantaneous pulled/ideal flip used to pump the framing.
        // Pull-in converges fast (the blocker must not hide the vehicle);
        // release eases slowly so alternating occlusion reads as a steady
        // held depth, not a jump.
        private Vector3 ClearOfObstacles(Vector3 target, Vector3 ideal,
            float deltaSeconds, bool snap)
        {
            LastObstacleName = null;
            var delta = ideal - target;
            var distance = delta.magnitude;
            if (distance < 1e-6f) return ideal;
            var dir = delta / distance;
            var clear = distance;
            var hits = Physics.RaycastAll(target, dir, distance,
                ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits,
                (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                if (hit.collider.GetComponentInParent<VehiclePresentation>()
                        != null)
                    continue; // the vehicle itself never occludes itself
                clear = Mathf.Clamp(hit.distance - obstacleMargin, 0.25f, distance);
                LastObstacleName = hit.collider.name;
                break;
            }
            if (snap || pullDistance < 0f)
            {
                pullDistance = clear;
            }
            else
            {
                var rate = clear < pullDistance ? pullInRate : releaseRate;
                pullDistance = Mathf.Lerp(pullDistance, clear,
                    1f - Mathf.Exp(-rate * deltaSeconds));
            }
            return target + dir * pullDistance;
        }

        private void Disengage()
        {
            engaged = false;
            hasTarget = false;
            pullDistance = -1f;
            // The Awake-captured menu pose survives every disengage:
            // each race leaving restores it, not just the first.
            if (cam != null && savedPose)
                cam.transform.SetPositionAndRotation(savedPosition, savedRotation);
        }

        // mode:"none" carries no pose: consumers keying on coordinates
        // must never read a phantom framing from the last live mode.
        private void ClearFacts()
        {
            facts.mode = "none";
            facts.px = facts.py = facts.pz = 0f;
            facts.tx = facts.ty = facts.tz = 0f;
        }

        private void OnDisable()
        {
            if (engaged) Disengage();
            ClearFacts();
        }
    }
}
