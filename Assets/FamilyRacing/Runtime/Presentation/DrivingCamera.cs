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

        private Camera cam;
        private bool engaged;
        private bool savedPose;
        private Vector3 savedPosition;
        private Quaternion savedRotation;
        private Vector3 smoothTarget;
        private bool hasTarget;
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
                facts.mode = "none";
                return;
            }
            if (!engaged)
            {
                engaged = true;
                hasTarget = false; // engagement snaps into framing, no swoop
            }

            var mode = state.draftProfile != null
                && state.draftProfile.camera == "overhead" ? "overhead" : "chase";
            var reduced = state.draftProfile != null
                && state.draftProfile.reducedCameraShake;
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
            return ClearOfObstacles(target,
                target - forward * chaseBack + Vector3.up * chaseUp);
        }

        private Vector3 OverheadDesired(Vector3 target)
        {
            return ClearOfObstacles(target,
                target + new Vector3(0f, overheadUp, -overheadBack));
        }

        // Pull the desired camera spot in front of the first solid occluder
        // on the target→camera ray so the vehicle stays visible. Trigger
        // volumes (checkpoint gates, the drift pad) are not view obstacles,
        // and neither is the vehicle's own silhouette — the ray can exit
        // through the Cabin/Rider parts the presentation builds. Shared by
        // both modes: chase yields side/behind blockers, overhead yields
        // canopies and tall blocks over the vehicle.
        private Vector3 ClearOfObstacles(Vector3 target, Vector3 desired)
        {
            LastObstacleName = null;
            var delta = desired - target;
            var distance = delta.magnitude;
            if (distance < 1e-6f) return desired;
            var hits = Physics.RaycastAll(target, delta / distance, distance,
                ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits,
                (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                if (hit.collider.GetComponentInParent<VehiclePresentation>()
                        != null)
                    continue; // the vehicle itself never occludes itself
                var towardTarget = target - hit.point;
                if (towardTarget.sqrMagnitude <= 1e-6f) continue;
                LastObstacleName = hit.collider.name;
                return hit.point + towardTarget.normalized * obstacleMargin;
            }
            return desired;
        }

        private void Disengage()
        {
            engaged = false;
            hasTarget = false;
            if (cam != null && savedPose)
                cam.transform.SetPositionAndRotation(savedPosition, savedRotation);
            savedPose = false;
        }

        private void OnDisable()
        {
            if (engaged) Disengage();
            facts.mode = "none";
        }
    }
}
