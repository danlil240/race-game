using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Racing
{
    // Authored gadget vocabulary. GadgetRules owns authoritative behavior.
    public static class GadgetKind
    {
        public const string Turbo = "turbo";
        public const string BubbleShield = "bubble-shield";
        public const string FoamTrap = "foam-trap";
        public const string HomingMissile = "homing-missile";
        public const string Mine = "mine";
        public const string EmpPulse = "emp-pulse";

        public static bool IsValid(string kind)
        {
            return kind == Turbo || kind == BubbleShield || kind == FoamTrap
                || kind == HomingMissile || kind == Mine || kind == EmpPulse;
        }
    }

    // One gadget's authored parameters. Durations are unpaused
    // 30 Hz simulation ticks; multipliers ride the simulation seam.
    // Kind-relevant fields are validated per kind; the rest are inert.
    public sealed class GadgetDefinition
    {
        public string id = "";
        public string kind = "";
        // Turbo: active duration and modifiers (repeat activations
        // refresh duration without multiplying modifiers).
        public int durationTicks;
        public float accelerationMultiplier = 1f;
        public float speedCapMultiplier = 1f;
        // Bubble shield: charges until it blocks one rival attack or
        // the race ends.
        public int charges;
        // Offensive gadgets share hit effect parameters. Radius/lifetime
        // describe the patch, missile collider, or EMP area by kind.
        public float patchRadiusMeters;
        public int patchLifetimeTicks;
        public float slowMultiplier = 1f;
        public int slowDurationTicks;
        public int immunityTicks;
        public float projectileSpeedMetersPerSecond = 45f;
        public float targetingRangeMeters = 70f;
        public float turnRadiansPerSecond = 3f;
        public int armingTicks = 18;

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (!GadgetKind.IsValid(kind)) { error = "kind"; return false; }
            if (kind == GadgetKind.Turbo)
            {
                if (durationTicks <= 0) { error = "durationTicks"; return false; }
                if (!Finite(accelerationMultiplier) || accelerationMultiplier < 1f)
                { error = "accelerationMultiplier"; return false; }
                if (!Finite(speedCapMultiplier) || speedCapMultiplier < 1f)
                { error = "speedCapMultiplier"; return false; }
            }
            else if (kind == GadgetKind.BubbleShield)
            {
                if (charges <= 0) { error = "charges"; return false; }
            }
            else if (kind == GadgetKind.FoamTrap || kind == GadgetKind.Mine
                || kind == GadgetKind.HomingMissile || kind == GadgetKind.EmpPulse)
            {
                if (!Finite(patchRadiusMeters) || patchRadiusMeters <= 0f)
                { error = "patchRadiusMeters"; return false; }
                if (patchLifetimeTicks <= 0) { error = "patchLifetimeTicks"; return false; }
                if (!Finite(slowMultiplier) || slowMultiplier <= 0f || slowMultiplier > 1f)
                { error = "slowMultiplier"; return false; }
                if (slowDurationTicks <= 0) { error = "slowDurationTicks"; return false; }
                if (immunityTicks < 0) { error = "immunityTicks"; return false; }
                if (kind == GadgetKind.Mine && armingTicks < 0)
                { error = "armingTicks"; return false; }
                if (kind == GadgetKind.HomingMissile)
                {
                    if (!Finite(projectileSpeedMetersPerSecond) || projectileSpeedMetersPerSecond <= 0f)
                    { error = "projectileSpeedMetersPerSecond"; return false; }
                    if (!Finite(targetingRangeMeters) || targetingRangeMeters <= 0f)
                    { error = "targetingRangeMeters"; return false; }
                    if (!Finite(turnRadiansPerSecond) || turnRadiansPerSecond <= 0f)
                    { error = "turnRadiansPerSecond"; return false; }
                }
            }
            return true;
        }

        public GadgetDefinition Copy()
        {
            return (GadgetDefinition)MemberwiseClone();
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    // The authored gadget catalog: item definitions plus the pickup and
    // inventory rules. Pickup generation is host-owned; distribution is
    // seeded and independent of standings.
    public sealed class GadgetDefinitionCatalog
    {
        // Ticks before a consumed pickup respawns.
        public int pickupRespawnTicks = 300;
        // Held items per racer; a full slot leaves pickups unconsumed.
        public int heldSlotCapacity = 1;
        public List<GadgetDefinition> gadgets = new List<GadgetDefinition>();

        public bool Validate(out string error)
        {
            error = null;
            if (pickupRespawnTicks <= 0) { error = "pickupRespawnTicks"; return false; }
            if (heldSlotCapacity <= 0) { error = "heldSlotCapacity"; return false; }
            if (gadgets == null) { error = "gadgets"; return false; }
            if (gadgets != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var gadget in gadgets)
                {
                    if (gadget == null || !gadget.Validate(out error))
                    { error = "gadgets." + error; return false; }
                    if (!seen.Add(gadget.id))
                    { error = "gadgets.duplicate"; return false; }
                }
            }
            return true;
        }

        public GadgetDefinition Gadget(string id)
        {
            if (gadgets != null)
                foreach (var gadget in gadgets)
                    if (gadget != null && gadget.id == id) return gadget;
            return null;
        }

        public GadgetDefinitionCatalog Copy()
        {
            var copy = new GadgetDefinitionCatalog
            {
                pickupRespawnTicks = pickupRespawnTicks,
                heldSlotCapacity = heldSlotCapacity,
                gadgets = new List<GadgetDefinition>()
            };
            if (gadgets != null)
                foreach (var gadget in gadgets)
                    copy.gadgets.Add(gadget == null ? null : gadget.Copy());
            return copy;
        }
    }
}
