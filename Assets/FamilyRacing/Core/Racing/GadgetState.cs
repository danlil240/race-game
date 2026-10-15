using System;
using System.Collections.Generic;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // Authored pickup placement on a course: a vertical trigger column a
    // swept racer segment must intersect to collect. `y` is the road
    // height so elevated routes and flyovers never touch a ground-level
    // site (DifferentHeightsNeverInteract).
    [Serializable]
    public sealed class GadgetPickupSite
    {
        public string id = "";
        public float x;
        public float y;
        public float z;
        public float radiusMeters = 2f;
        public float verticalRangeMeters = 2.5f;

        public GadgetPickupSite Copy()
        {
            return (GadgetPickupSite)MemberwiseClone();
        }

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (!Finite(x) || !Finite(y) || !Finite(z))
            { error = "position"; return false; }
            if (!Finite(radiusMeters) || radiusMeters <= 0f)
            { error = "radiusMeters"; return false; }
            if (!Finite(verticalRangeMeters) || verticalRangeMeters <= 0f)
            { error = "verticalRangeMeters"; return false; }
            return true;
        }

        private static bool Finite(float v)
        {
            return !(float.IsNaN(v) || float.IsInfinity(v));
        }
    }

    // Live pickup: mirrors its authored site plus the host's activation
    // bookkeeping. `generation` rotates on every respawn so a stale
    // grant/ack can never re-collect (StaleGenerationCannotMutate).
    [Serializable]
    public sealed class GadgetPickupState
    {
        public string siteId = "";
        public float x;
        public float y;
        public float z;
        public float radiusMeters = 2f;
        public float verticalRangeMeters = 2.5f;
        public long generation;
        public bool active = true;
        public int respawnTicksRemaining;

        public GadgetPickupState Copy()
        {
            return (GadgetPickupState)MemberwiseClone();
        }
    }

    // A dropped foam patch: authored geometry/lifetime at drop time,
    // owned so the dropper never trips it. Once a patch hits (or is
    // shield-blocked by) a racer it is permanently inert for that racer
    // — the per-racer immunity lives on the racer row's immunePatchIds.
    [Serializable]
    public sealed class GadgetPatchState
    {
        public string patchId = "";
        public string ownerParticipantId = "";
        public float x;
        public float y;
        public float z;
        public float radiusMeters;
        public float verticalRangeMeters = 2.5f;
        public int ticksRemaining;
        public float slowMultiplier = 1f;
        public int slowTicks;
        public int immunityTicks;

        public GadgetPatchState Copy()
        {
            var copy = (GadgetPatchState)MemberwiseClone();
            return copy;
        }
    }

    // One racer's authoritative gadget row: the single held slot, the
    // active turbo envelope (authored modifiers captured at activation so
    // a catalog swap can never change an in-flight effect), remaining
    // shield charges, foam slow/immunity clocks, the press-edge latch,
    // and the bounded list of patches that already resolved against
    // this racer.
    [Serializable]
    public sealed class GadgetRacerState
    {
        public string participantId = "";
        public string heldItemId = "";
        // The granted item's authored kind, stamped at grant so wire
        // consumers (HUD/presentation) can name the held item without
        // re-resolving the catalog. Empty whenever the slot is empty.
        public string heldKind = "";
        public long heldGeneration;
        public int heldTicks;
        public int turboTicksRemaining;
        public float turboAccelerationMultiplier = 1f;
        public float turboSpeedCapMultiplier = 1f;
        public int shieldCharges;
        public int foamSlowTicksRemaining;
        // The authored patch's multiplier captured at hit time — a patch
        // expiring early can never change an in-flight slow.
        public float foamSlowMultiplier = 1f;
        public int foamImmunityTicksRemaining;
        // One-shot edge latch: a use fires only on the pressed edge, so
        // holding the input consumes at most one item until release.
        public bool gadgetHeld;
        public List<string> immunePatchIds = new List<string>();

        public GadgetRacerState Copy()
        {
            var copy = (GadgetRacerState)MemberwiseClone();
            copy.immunePatchIds = new List<string>(immunePatchIds);
            return copy;
        }
    }

    // The host-authoritative gadget world: pickup sites with their
    // respawn clocks, live foam patches, per-racer gadget rows and the
    // seeded draw bag. Serialized verbatim into race snapshots; the wire
    // codec validates it on decode (ValidateWire) before any client
    // applies it. All lists stay bounded by the NetworkSettings gadget
    // capacities — the world can never grow past the snapshot budget.
    [Serializable]
    public sealed class GadgetWorldState
    {
        public long tick;
        // Seeded shuffle bookkeeping (xorshift32 state as a signed-safe
        // long): the draw order is fixed at creation by the race seed and
        // is independent of standings or collection order.
        public long rngState;
        public List<int> itemBag = new List<int>();
        public int bagCursor;
        public long nextPickupGeneration;
        public long nextPatchSerial;
        public List<GadgetPickupState> pickups =
            new List<GadgetPickupState>();
        public List<GadgetPatchState> patches =
            new List<GadgetPatchState>();
        public List<GadgetRacerState> racers =
            new List<GadgetRacerState>();

        public GadgetRacerState Racer(string participantId)
        {
            if (racers != null)
                foreach (var racer in racers)
                    if (racer != null && racer.participantId == participantId)
                        return racer;
            return null;
        }

        public GadgetWorldState Copy()
        {
            var copy = new GadgetWorldState
            {
                tick = tick,
                rngState = rngState,
                bagCursor = bagCursor,
                nextPickupGeneration = nextPickupGeneration,
                nextPatchSerial = nextPatchSerial,
                itemBag = new List<int>(itemBag),
                pickups = new List<GadgetPickupState>(),
                patches = new List<GadgetPatchState>(),
                racers = new List<GadgetRacerState>()
            };
            foreach (var pickup in pickups)
                copy.pickups.Add(pickup != null ? pickup.Copy() : null);
            foreach (var patch in patches)
                copy.patches.Add(patch != null ? patch.Copy() : null);
            foreach (var racer in racers)
                copy.racers.Add(racer != null ? racer.Copy() : null);
            return copy;
        }

        // Wire-side structural validation: the decode path rejects a
        // gadget payload that is unbounded, non-finite or otherwise
        // unmergeable before any client state observes it.
        public bool ValidateWire(out string error)
        {
            error = null;
            if (pickups == null || patches == null || racers == null
                || itemBag == null)
            { error = "gadgetLists"; return false; }
            if (pickups.Count > NetworkSettings.GadgetPickupCapacity
                || patches.Count > NetworkSettings.GadgetPatchCapacity
                || racers.Count > NetworkSettings.ParticipantCapacity
                || itemBag.Count > NetworkSettings.GadgetPickupCapacity)
            { error = "gadgetCapacity"; return false; }
            var seenPickups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pickup in pickups)
            {
                if (pickup == null || string.IsNullOrEmpty(pickup.siteId)
                    || !Finite(pickup.x) || !Finite(pickup.y)
                    || !Finite(pickup.z) || !Finite(pickup.radiusMeters)
                    || pickup.radiusMeters <= 0f
                    || !Finite(pickup.verticalRangeMeters))
                { error = "gadgetPickup"; return false; }
                if (!seenPickups.Add(pickup.siteId))
                { error = "gadgetPickup.duplicate"; return false; }
            }
            var seenPatches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var patch in patches)
            {
                if (patch == null || string.IsNullOrEmpty(patch.patchId)
                    || !Finite(patch.x) || !Finite(patch.y)
                    || !Finite(patch.z) || !Finite(patch.radiusMeters)
                    || patch.radiusMeters <= 0f
                    || !Finite(patch.verticalRangeMeters)
                    || !Finite(patch.slowMultiplier))
                { error = "gadgetPatch"; return false; }
                if (!seenPatches.Add(patch.patchId))
                { error = "gadgetPatch.duplicate"; return false; }
            }
            var seenRacers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var racer in racers)
            {
                if (racer == null || string.IsNullOrEmpty(racer.participantId)
                    || racer.immunePatchIds == null
                    || racer.immunePatchIds.Count
                        > NetworkSettings.GadgetImmunePatchCapacity
                    || !Finite(racer.turboAccelerationMultiplier)
                    || !Finite(racer.turboSpeedCapMultiplier)
                    || !Finite(racer.foamSlowMultiplier))
                { error = "gadgetRacer"; return false; }
                // The kind tag must agree with the slot: empty slot ->
                // empty kind; held item -> a known kind only.
                if (string.IsNullOrEmpty(racer.heldItemId)
                        ? !string.IsNullOrEmpty(racer.heldKind)
                        : !GadgetKind.IsValid(racer.heldKind))
                { error = "gadgetRacer.heldKind"; return false; }
                if (!seenRacers.Add(racer.participantId))
                { error = "gadgetRacer.duplicate"; return false; }
            }
            return true;
        }

        private static bool Finite(float v)
        {
            return !(float.IsNaN(v) || float.IsInfinity(v));
        }
    }
}
