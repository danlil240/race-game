using System;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Networking
{
    // Visual correction smoothing (Task 6) — deliberately separate from
    // the simulation correction ClientPrediction applies immediately.
    // When a reconcile corrects the predicted pose, the rendered pose
    // keeps its visual offset and decays it to zero linearly over
    // CorrectionBlendSeconds, so ordinary errors ease in over 100 ms.
    // Snap-class corrections (ownership change, recovery, or an error
    // past CorrectionSnapMeters — the prediction flags them) clear the
    // offset instantly: no blend across a teleport.
    public sealed class CorrectionSmoother
    {
        private float offsetX;
        private float offsetY;
        private float offsetZ;
        private float offsetHeading;
        private double remainingSeconds;

        // The currently rendered pose minus the corrected pose becomes
        // the offset the visual keeps showing; a snap clears it instead.
        public void Correct(MotorState rendered, MotorState corrected,
            bool snap)
        {
            if (snap || rendered == null || corrected == null)
            {
                offsetX = offsetY = offsetZ = offsetHeading = 0f;
                remainingSeconds = 0;
                return;
            }
            offsetX += rendered.x - corrected.x;
            offsetY += rendered.y - corrected.y;
            offsetZ += rendered.z - corrected.z;
            offsetHeading = WrapAngle(offsetHeading
                + WrapAngle(rendered.heading - corrected.heading));
            remainingSeconds = NetworkSettings.CorrectionBlendSeconds;
        }

        // The pose to render this frame: the corrected sim pose plus the
        // decaying visual offset. The offset shrinks linearly in time
        // and reaches exactly zero when CorrectionBlendSeconds elapse.
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
                remainingSeconds -= dt;
                if (remainingSeconds <= 0)
                {
                    offsetX = offsetY = offsetZ = offsetHeading = 0f;
                    remainingSeconds = 0;
                }
            }
            var copy = predicted.Copy();
            copy.x += offsetX;
            copy.y += offsetY;
            copy.z += offsetZ;
            copy.heading = WrapAngle(copy.heading + offsetHeading);
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

        public bool HasOffset { get { return OffsetMeters > 0.0001f; } }

        public void Reset()
        {
            offsetX = offsetY = offsetZ = offsetHeading = 0f;
            remainingSeconds = 0;
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
