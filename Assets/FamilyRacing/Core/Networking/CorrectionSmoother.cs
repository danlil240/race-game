using System;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Networking
{
    // Visual correction smoothing (Tasks 6 + 10) — deliberately separate
    // from the simulation correction ClientPrediction applies
    // immediately. When a reconcile corrects the predicted pose, the
    // rendered pose keeps its visual offset and decays it to zero over
    // CorrectionBlendSeconds, so ordinary errors ease in over 100 ms.
    // Snap-class corrections (ownership change, recovery, or an error
    // past CorrectionSnapMeters/Degrees — the prediction flags them)
    // clear the offset instantly: no blend across a teleport.
    //
    // The offset has two channels: a linear position offset plus a
    // rotation offset. Physical motors carry the rotation as a
    // quaternion on the physics attitude — roll and pitch blend like
    // position does, never just yaw — while legacy motors keep the
    // wrapped heading offset. Only the RENDERED pose is smoothed; the
    // predicted body is corrected immediately by ClientPrediction.
    public sealed class CorrectionSmoother
    {
        private float offsetX;
        private float offsetY;
        private float offsetZ;
        private float offsetHeading;
        // Rendered attitude times the inverse of the corrected attitude:
        // the world-frame rotation that still separates what the player
        // sees from the corrected pose.
        private float offsetQX;
        private float offsetQY;
        private float offsetQZ;
        private float offsetQW = 1f;
        private double remainingSeconds;

        // The currently rendered pose minus the corrected pose becomes
        // the offset the visual keeps showing; a snap clears it instead.
        public void Correct(MotorState rendered, MotorState corrected,
            bool snap)
        {
            if (snap || rendered == null || corrected == null)
            {
                offsetX = offsetY = offsetZ = offsetHeading = 0f;
                offsetQX = offsetQY = offsetQZ = 0f;
                offsetQW = 1f;
                remainingSeconds = 0;
                return;
            }
            offsetX += rendered.x - corrected.x;
            offsetY += rendered.y - corrected.y;
            offsetZ += rendered.z - corrected.z;
            offsetHeading = WrapAngle(offsetHeading
                + WrapAngle(rendered.heading - corrected.heading));
            var rp = rendered.physics;
            var cp = corrected.physics;
            if (rp != null && cp != null)
            {
                // offset = rendered ⊗ corrected⁻¹ (unit quaternions —
                // the inverse is the conjugate).
                var rx = rp.attitudeX;
                var ry = rp.attitudeY;
                var rz = rp.attitudeZ;
                var rw = rp.attitudeW;
                var cx = cp.attitudeX;
                var cy = cp.attitudeY;
                var cz = cp.attitudeZ;
                var cw = cp.attitudeW;
                var ox = -rw * cx + rx * cw - ry * cz + rz * cy;
                var oy = -rw * cy + rx * cz + ry * cw - rz * cx;
                var oz = -rw * cz - rx * cy + ry * cx + rz * cw;
                var ow = rw * cw + rx * cx + ry * cy + rz * cz;
                var norm = (float)Math.Sqrt(ox * ox + oy * oy + oz * oz
                    + ow * ow);
                if (norm > 1e-6f)
                {
                    var inv = 1f / norm;
                    offsetQX = ox * inv;
                    offsetQY = oy * inv;
                    offsetQZ = oz * inv;
                    offsetQW = ow * inv;
                    // Always blend the shortest arc: a negative w means
                    // the same rotation reachable the shorter way.
                    if (offsetQW < 0f)
                    {
                        offsetQX = -offsetQX;
                        offsetQY = -offsetQY;
                        offsetQZ = -offsetQZ;
                        offsetQW = -offsetQW;
                    }
                }
            }
            remainingSeconds = NetworkSettings.CorrectionBlendSeconds;
        }

        // The pose to render this frame: the corrected sim pose plus the
        // decaying visual offset. The offset shrinks toward identity in
        // time and reaches exactly zero when CorrectionBlendSeconds
        // elapse. For physical motors the rotation offset composes onto
        // the physics attitude (and the legacy heading follows the same
        // rotation); for legacy motors the wrapped yaw offset applies.
        public MotorState Apply(MotorState predicted, double deltaSeconds)
        {
            if (predicted == null) return null;
            var dt = Math.Max(0.0, deltaSeconds);
            if (remainingSeconds > 0)
            {
                var scale = dt >= remainingSeconds
                    ? 0.0 : 1.0 - dt / remainingSeconds;
                offsetX *= (float)scale;
                offsetY *= (float)scale;
                offsetZ *= (float)scale;
                offsetHeading *= (float)scale;
                offsetQX *= (float)scale;
                offsetQY *= (float)scale;
                offsetQZ *= (float)scale;
                // slerp(identity -> offset, scale): the angle decays
                // toward identity, renormalized below.
                offsetQW = 1f + (offsetQW - 1f) * (float)scale;
                remainingSeconds -= dt;
                if (remainingSeconds <= 0)
                {
                    offsetX = offsetY = offsetZ = offsetHeading = 0f;
                    offsetQX = offsetQY = offsetQZ = 0f;
                    offsetQW = 1f;
                    remainingSeconds = 0;
                }
            }
            var copy = predicted.Copy();
            copy.x += offsetX;
            copy.y += offsetY;
            copy.z += offsetZ;
            copy.heading = WrapAngle(copy.heading + offsetHeading);
            var physics = copy.physics;
            if (physics != null
                && (offsetQX != 0f || offsetQY != 0f || offsetQZ != 0f
                    || Math.Abs(offsetQW - 1f) > 1e-6f))
            {
                // Normalize the decayed offset before composing — the
                // linear scale above does not preserve unit length.
                var norm = (float)Math.Sqrt(offsetQX * offsetQX
                    + offsetQY * offsetQY + offsetQZ * offsetQZ
                    + offsetQW * offsetQW);
                if (norm > 1e-6f)
                {
                    var inv = 1f / norm;
                    var ox = offsetQX * inv;
                    var oy = offsetQY * inv;
                    var oz = offsetQZ * inv;
                    var ow = offsetQW * inv;
                    var px = physics.attitudeX;
                    var py = physics.attitudeY;
                    var pz = physics.attitudeZ;
                    var pw = physics.attitudeW;
                    // rendered = offset ⊗ corrected.
                    physics.TrySetAttitude(
                        ow * px + ox * pw + oy * pz - oz * py,
                        ow * py - ox * pz + oy * pw + oz * px,
                        ow * pz + ox * py - oy * px + oz * pw,
                        ow * pw - ox * px - oy * py - oz * pz);
                    // The envelope heading follows the rotated attitude
                    // so yaw consumers (camera, HUD) agree with what the
                    // presentation draws.
                    copy.heading = HeadingOf(physics);
                }
            }
            return copy;
        }

        // Remaining visual offset in metres — zero once the blend ends.
        public float OffsetMeters
        {
            get
            {
                return (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY
                    + offsetZ * offsetZ);
            }
        }

        // Remaining visual offset in degrees — zero once the blend ends.
        public float OffsetDegrees
        {
            get
            {
                var dot = Math.Abs(offsetQW);
                if (dot > 1f) dot = 1f;
                var quat = 2f * (float)Math.Acos(dot)
                    * (float)(180.0 / Math.PI);
                var yaw = Math.Abs(offsetHeading) * (float)(180.0 / Math.PI);
                return quat > yaw ? quat : yaw;
            }
        }

        public bool HasOffset
        {
            get { return OffsetMeters > 0.0001f || OffsetDegrees > 0.01f; }
        }

        public void Reset()
        {
            offsetX = offsetY = offsetZ = offsetHeading = 0f;
            offsetQX = offsetQY = offsetQZ = 0f;
            offsetQW = 1f;
            remainingSeconds = 0;
        }

        // Yaw of the attitude's flat forward vector — the same
        // derivation PhysicsVehicleBody uses for the envelope heading.
        private static float HeadingOf(VehiclePhysicsState physics)
        {
            var x = physics.attitudeX;
            var y = physics.attitudeY;
            var z = physics.attitudeZ;
            var w = physics.attitudeW;
            // Rotate +Z by the quaternion: fwd = (2(xz+wy), ..., 1-2(x2+y2)).
            var fx = 2f * (x * z + w * y);
            var fz = 1f - 2f * (x * x + y * y);
            if (fx * fx + fz * fz < 1e-6f) return 0f;
            return (float)Math.Atan2(fx, fz);
        }

        private static float WrapAngle(float radians)
        {
            if (float.IsNaN(radians) || float.IsInfinity(radians)) return 0f;
            var twoPi = (float)(Math.PI * 2.0);
            while (radians > Math.PI) radians -= twoPi;
            while (radians <= -Math.PI) radians += twoPi;
            return radians;
        }
    }
}
