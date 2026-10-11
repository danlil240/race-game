using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Physics-backed IMotorWorld: the ground probe is a raycast straight
    // down and the body sweep is a heading-oriented box cast. Both ignore
    // trigger volumes so checkpoint gates never block motion. The vehicle
    // visual has no collider, so casts only ever see the course.
    public sealed class UnityMotorWorld : IMotorWorld
    {
        private const float ProbeOriginLift = 0.1f;

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
    }
}
