using System;

namespace FamilyRacing.Core.Driving
{
    // Result of a downward ground probe from the body origin.
    public struct GroundHit
    {
        public bool hit;
        public float x;
        public float y;
        public float z;
        public float normalX;
        public float normalY;
        public float normalZ;
        public float distance;
        public string surfaceId;
        // False by default preserves older worlds. Rough ground is an
        // explicit surface classification supplied by the collision adapter.
        public bool offRoad;
    }

    // Result of a swept body-volume query. `fraction` is the portion of the
    // requested displacement that is clear before contact; `normal*` is the
    // contact normal pointing back into free space.
    public struct SweepHit
    {
        public bool hit;
        public float fraction;
        public float normalX;
        public float normalY;
        public float normalZ;
        public float pointX;
        public float pointY;
        public float pointZ;
        public string surfaceId;
    }

    // Unity-free collision query surface for the arcade motor. The Core
    // fake world implements it over analytic planes; the Unity adapter
    // (Runtime/Driving/UnityMotorWorld) implements it over Physics raycasts
    // and box casts against the graybox course colliders.
    public interface IMotorWorld
    {
        // Probe straight down from the pose origin for ground within
        // probeDistance. Returns hit=false when nothing is in reach.
        GroundHit ProbeGround(MotorPose pose, float probeDistance);

        // Sweep the heading-oriented body box (half extents halfWidth,
        // height/2, halfLength) from pose by (dx, dy, dz). Returns the first
        // contact or hit=false.
        SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
            float halfWidth, float halfLength, float height);
    }
}
