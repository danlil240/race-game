using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Racing
{
    // One boost pad painted on the road: driving over it fires a short
    // turbo kick. Pads are pure course geometry, so every peer derives the
    // identical list from the shared course and nothing about them rides
    // the wire; only the turbo they grant lives on the racer's gadget row.
    public sealed class GadgetBoostPad
    {
        public string id = "";
        public float x;
        public float y;
        public float z;
        public float heading;
        public float radiusMeters = BoostPads.RadiusMeters;
        public float verticalRangeMeters = 2.5f;
    }

    // Boost pad layout and tuning. The layout is derived from the ordered
    // checkpoint gates (already validated, on the road, with a travel
    // direction): a pad sits a short run past every intermediate gate,
    // alternating left and right of the centre line so racers have to
    // aim for it. Start and finish lines never get a pad.
    public static class BoostPads
    {
        public const float RadiusMeters = 3.2f;
        public const float DistancePastGateMeters = 14f;
        public const float LaneOffsetMeters = 3.2f;
        // 1.6 s kick: hard acceleration and a raised cap, shorter than a
        // turbo pickup so the held item stays the bigger reward.
        public const int TurboTicks = 48;
        public const float AccelerationMultiplier = 3.2f;
        public const float SpeedCapMultiplier = 1.4f;
        public const int CooldownTicks = 20;

        public static List<GadgetBoostPad> Derive(CourseDefinition course)
        {
            var pads = new List<GadgetBoostPad>();
            if (course == null || course.checkpoints == null) return pads;
            var gates = course.checkpoints;
            var last = course.isOpenRoute ? gates.Count - 1 : gates.Count;
            for (var i = 1; i < last; i++)
            {
                var gate = gates[i];
                if (gate == null) continue;
                var len = (float)Math.Sqrt(gate.dirX * gate.dirX + gate.dirZ * gate.dirZ);
                if (len < 1e-4f || float.IsNaN(len)) continue;
                var fx = gate.dirX / len;
                var fz = gate.dirZ / len;
                // Right of travel is (fz, -fx), matching the motor frame.
                var side = i % 2 == 0 ? 1f : -1f;
                var lane = Math.Min(LaneOffsetMeters, Math.Max(0f, gate.halfWidth - RadiusMeters - 1f));
                pads.Add(new GadgetBoostPad
                {
                    id = "pad-" + gate.id,
                    x = gate.x + fx * DistancePastGateMeters + fz * lane * side,
                    y = gate.y,
                    z = gate.z + fz * DistancePastGateMeters - fx * lane * side,
                    heading = (float)Math.Atan2(fx, fz)
                });
            }
            return pads;
        }
    }
}
