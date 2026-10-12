using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Physics-backed IMotorWorld: the ground probe is a raycast straight
    // down and the body sweep is a heading-oriented box cast. Both ignore
    // trigger volumes so checkpoint gates never block motion. The vehicle
    // visual has no collider, so casts only ever see the course.
    //
    // PhysX sweeps cannot see a collider the cast volume already touches
    // or contains — a wedged body would otherwise "free move" through
    // walls and the landmark ramp. A skin-ring overlap pre-check ahead of
    // every cast reports those starts as a zero-distance hit so the
    // motor's wedged path (step-climb / slide / block) engages instead.
    public sealed class UnityMotorWorld : IMotorWorld
    {
        private const float ProbeOriginLift = 0.1f;
        private const float ContactSkin = 0.05f;
        private const float SkinDrop = 0.05f;

        public GroundHit ProbeGround(MotorPose pose, float probeDistance)
        {
            var origin = new Vector3(pose.x, pose.y + ProbeOriginLift, pose.z);
            RaycastHit hit;
            if (Physics.Raycast(origin, Vector3.down, out hit,
                    probeDistance + ProbeOriginLift,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return new GroundHit
                {
                    hit = true,
                    x = hit.point.x,
                    y = hit.point.y,
                    z = hit.point.z,
                    normalX = hit.normal.x,
                    normalY = hit.normal.y,
                    normalZ = hit.normal.z,
                    distance = hit.distance - ProbeOriginLift,
                    surfaceId = hit.collider != null ? hit.collider.name : ""
                };
            }
            return new GroundHit { hit = false };
        }

        public SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
            float halfWidth, float halfLength, float height)
        {
            var delta = new Vector3(dx, dy, dz);
            var distance = delta.magnitude;
            if (distance <= 0f) return new SweepHit { hit = false, fraction = 1f };
            var center = new Vector3(pose.x, pose.y + height * 0.5f, pose.z);
            var rotation = Quaternion.Euler(0f, pose.heading * Mathf.Rad2Deg, 0f);

            // Pre-check: the cast is blind to colliders it starts touching
            // or inside, so a body already in the contact skin ring is
            // wedged by definition — but only against motion INTO the
            // blocker. Moving away or parallel must stay free or the car
            // would glue-stick to every wall it brushes (there is no
            // reverse gear to back out with). The ring is widened only
            // horizontally; the bottom face is dropped a skin so resting
            // on the ground can never read as penetration.
            var ring = new Vector3(halfWidth + ContactSkin,
                Mathf.Max(0.01f, height * 0.5f - SkinDrop), halfLength + ContactSkin);
            var castDir = delta / distance;
            var overlaps = Physics.OverlapBox(center, ring, rotation,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (var blocker in overlaps)
            {
                if (blocker == null) continue;
                var normal = SeparatingNormal(blocker, center, castDir);
                if (normal.x * castDir.x + normal.z * castDir.z >= 0f)
                    continue; // moving away or tangent — not a wedge
                return new SweepHit
                {
                    hit = true,
                    fraction = 0f,
                    normalX = normal.x,
                    normalY = normal.y,
                    normalZ = normal.z,
                    surfaceId = blocker.name
                };
            }

            RaycastHit hit;
            if (Physics.BoxCast(center, new Vector3(halfWidth, height * 0.5f, halfLength),
                    delta / distance, out hit, rotation, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return new SweepHit
                {
                    hit = true,
                    fraction = hit.distance / distance,
                    normalX = hit.normal.x,
                    normalY = hit.normal.y,
                    normalZ = hit.normal.z,
                    pointX = hit.point.x,
                    pointY = hit.point.y,
                    pointZ = hit.point.z,
                    surfaceId = hit.collider != null ? hit.collider.name : ""
                };
            }
            return new SweepHit { hit = false, fraction = 1f };
        }

        // An honest outward normal for a wedged start: the direction from
        // the collider's closest surface point toward the body centre.
        // A centre buried inside the collider has no closest face, so the
        // normal falls back to pushing back along the attempted move.
        private static Vector3 SeparatingNormal(Collider blocker,
            Vector3 center, Vector3 castDir)
        {
            var away = center - blocker.ClosestPoint(center);
            return away.sqrMagnitude > 1e-4f ? away.normalized : -castDir;
        }
    }
}
