using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // One traveled pose segment a racer actually drove during a boundary
    // (the simulation's ordered substep samples): pickups and foam
    // patches only resolve against these swept segments — never against
    // the end-to-end chord — so a recovery teleport or a compressed
    // interval can never manufacture a collection.
    public struct GadgetSegment
    {
        public float ax;
        public float ay;
        public float az;
        public float bx;
        public float by;
        public float bz;
    }

    // One racer's gadget-relevant boundary facts, supplied by the host
    // coordinator (or the solo session) after the shared batch commits.
    // `segments` is the chronological swept path for the interval;
    // `recovering` marks a boundary whose travel is ineligible (a manual
    // or rule-requested respawn — it may still tick clocks and latch
    // edges); `finished` marks a finalized racer whose row stays inert.
    // `gadgetHeld` is the raw held bit — the rules own the edge latch,
    // so a held input consumes at most one item until release.
    public sealed class GadgetRacerInput
    {
        public string participantId = "";
        public int slot;
        public bool gadgetHeld;
        public bool recovering;
        public bool finished;
        public float x;
        public float y;
        public float z;
        public float heading;
        public List<GadgetSegment> segments = new List<GadgetSegment>();
    }

    // One authoritative gadget boundary: the host tick plus every racer
    // that completed a boundary this tick. Consumers feed racers in
    // stable slot order; the rules additionally order by slot defensively
    // so collection ties and draw order can never depend on caller
    // iteration.
    public sealed class GadgetStepInput
    {
        public long tick;
        public List<GadgetRacerInput> racers = new List<GadgetRacerInput>();
    }

    // The boundary result: the next world state (an independent deep
    // copy — `previous` is never mutated) plus the ordered transient
    // events this boundary produced. Event ids are deterministic per
    // (tick, ordinal) so a replayed boundary cannot mint divergent
    // identities; the coordinator stamps participant attribution before
    // publication.
    public sealed class GadgetStepResult
    {
        public GadgetWorldState state;
        public List<DrivingEvent> events = new List<DrivingEvent>();
    }

    // Pure authoritative gadget rules (M5 Task 5). Everything the host
    // owns lives here: pickup activation/respawn, the seeded draw bag,
    // edge-triggered item use, turbo/shield/foam application, and the
    // per-racer modifier the simulation seam consumes next boundary.
    //
    // Ordering contract (deterministic): within one Step —
    //   1. pickup respawn clocks + patch lifetimes tick in list order;
    //   2. every participating racer's clocks + held ticks tick in slot
    //      order;
    //   3. use edges fire in slot order (held slot clears before any
    //      collection pass);
    //   4. collections resolve in slot order over authored site order —
    //      a same-tick tie always goes to the lower slot, and a full
    //      slot leaves the pickup untouched;
    //   5. missiles move and resolve swept contact, then mines resolve
    //      against the earliest crossing rival (slot order breaks ties);
    //   6. foam patches resolve in slot order over patch order — owner
    //      and already-resolved patches skip, shield absorbs one hit.
    // Event ids mint as "gadget-<raceId>-<tick>-<ordinal>" —
    // deterministic per boundary AND scoped to the race identity, so a
    // rematch's minted ids can never collide with the concluded race's
    // wire-side dedup window; the namespace stays disjoint from the
    // raceId-scoped "<raceId>-e<n>" ids the sessions and coordinator
    // emit.
    public static partial class GadgetRules
    {
        public const string EventPickup = "gadget.pickup";
        public const string EventUsed = "gadget.used";
        public const string EventFoamDropped = "gadget.foamDropped";
        public const string EventFoamHit = "gadget.foamHit";
        public const string EventShieldBlocked = "gadget.shieldBlocked";
        public const string EventRespawn = "gadget.respawn";

        // Foam patches get this authored trigger height everywhere — the
        // same vertical band pickups use, so a designed jump flies over
        // both.
        private const float PatchVerticalRangeMeters = 2.5f;

        // Stable 32-bit seed for the draw bag from a race identity —
        // FNV-1a so the same raceId always deals the same item sequence,
        // on every device, independent of standings or timing.
        public static uint StableSeed(string text)
        {
            const uint fnvOffset = 2166136261u;
            const uint fnvPrime = 16777619u;
            var hash = fnvOffset;
            if (text != null)
                foreach (var c in text)
                {
                    hash ^= c;
                    hash *= fnvPrime;
                }
            return hash == 0u ? 0x9E3779B9u : hash;
        }

        // Builds the authoritative world for a race identity: pickups at
        // the authored sites (all active), one inert row per participant
        // in roster order, and the seeded bag shuffled once so the first
        // cycle already deals every kind exactly once. `raceId` scopes
        // every event id the world mints — a rematch under a fresh race
        // identity can never collide with the concluded race's dedup
        // window.
        public static GadgetWorldState Create(GadgetDefinitionCatalog catalog,
            IReadOnlyList<GadgetPickupSite> sites, uint seed,
            IReadOnlyList<string> participantIds, string raceId = "")
        {
            var world = new GadgetWorldState();
            world.raceId = raceId ?? "";
            var count = catalog != null && catalog.gadgets != null
                ? catalog.gadgets.Count : 0;
            for (var i = 0; i < count; i++)
                world.itemBag.Add(i);
            var rng = seed;
            Shuffle(world.itemBag, ref rng);
            world.rngState = rng;
            if (sites != null)
                foreach (var site in sites)
                {
                    if (site == null || !site.Validate(out _)) continue;
                    world.pickups.Add(new GadgetPickupState
                    {
                        siteId = site.id,
                        x = site.x,
                        y = site.y,
                        z = site.z,
                        radiusMeters = site.radiusMeters,
                        verticalRangeMeters = site.verticalRangeMeters,
                        generation = ++world.nextPickupGeneration,
                        active = true
                    });
                }
            if (participantIds != null)
                foreach (var id in participantIds)
                    if (!string.IsNullOrEmpty(id)
                        && world.Racer(id) == null)
                        world.racers.Add(new GadgetRacerState
                        {
                            participantId = id
                        });
            return world;
        }

        // The per-racer modifier the simulation seam consumes next
        // boundary: turbo supplies both authored multipliers, foam
        // scales BOTH drive channels by the authored slow factor (the
        // spec's "x0.70 drive acceleration/speed cap"), and a shield is
        // inert on motion. Composition is multiplicative — a turbo'd
        // racer on foam keeps turbo's authored envelope scaled by the
        // slow; "no stacking" refers to repeated foam hits, which the
        // immunity window already prevents. Absent world/row reads as
        // neutral.
        public static MotorModifiers ModifierFor(GadgetWorldState state,
            string participantId)
        {
            var mods = MotorModifiers.Neutral();
            var row = state != null ? state.Racer(participantId) : null;
            if (row == null) return mods;
            if (row.turboTicksRemaining > 0)
            {
                mods.accelerationMultiplier = row.turboAccelerationMultiplier;
                mods.speedCapMultiplier = row.turboSpeedCapMultiplier;
            }
            if (row.foamSlowTicksRemaining > 0)
            {
                mods.accelerationMultiplier *= row.foamSlowMultiplier;
                mods.speedCapMultiplier *= row.foamSlowMultiplier;
            }
            return mods;
        }

        // Advances the authoritative world exactly one boundary. The
        // returned state is an independent deep copy — callers keep
        // `previous` untouched for prediction replay or rollback.
        public static GadgetStepResult Step(GadgetStepInput input,
            GadgetWorldState previous, GadgetDefinitionCatalog catalog)
        {
            var result = new GadgetStepResult();
            if (previous == null) return result;
            var state = previous.Copy();
            result.state = state;
            if (input == null) return result;
            state.tick = input.tick > state.tick ? input.tick
                : state.tick + 1;
            var seq = 0;
            // Stable participant order: callers feed slot order; a
            // defensive sort keeps that even if they don't.
            var racers = input.racers != null
                ? new List<GadgetRacerInput>(input.racers) : null;
            if (racers == null || racers.Count == 0)
            {
                TickWorldClocks(state, result.events, ref seq);
                return result;
            }
            racers.Sort(CompareBySlotThenId);
            foreach (var entry in racers)
                if (entry != null
                    && !string.IsNullOrEmpty(entry.participantId)
                    && state.Racer(entry.participantId) == null)
                    state.racers.Add(new GadgetRacerState
                    {
                        participantId = entry.participantId
                    });

            TickWorldClocks(state, result.events, ref seq);

            // Per-racer clocks first: held-item timers and effect
            // durations advance on every unpaused boundary the racer
            // completed — a finished racer's row is inert.
            foreach (var entry in racers)
            {
                var row = state.Racer(entry.participantId);
                if (row == null) continue;
                if (entry.finished) { row.gadgetHeld = entry.gadgetHeld; continue; }
                TickRacerClocks(row);
            }

            // Edge-triggered use: one item per press edge, in slot order.
            foreach (var entry in racers)
            {
                var row = state.Racer(entry.participantId);
                if (row == null) continue;
                var pressed = entry.gadgetHeld && !row.gadgetHeld;
                row.gadgetHeld = entry.gadgetHeld;
                if (!pressed || entry.recovering || entry.finished) continue;
                if (!string.IsNullOrEmpty(row.heldItemId))
                    UseItem(state, row, entry, racers, catalog, result.events,
                        ref seq);
            }

            // Collections in slot order: same-tick ties resolve to the
            // lower slot deterministically.
            foreach (var entry in racers)
            {
                var row = state.Racer(entry.participantId);
                if (row == null || entry.finished || entry.recovering)
                    continue;
                CollectPickups(state, row, entry, catalog,
                    result.events, ref seq);
            }

            MoveProjectiles(state, racers, result.events, ref seq);
            ResolveMines(state, racers, result.events, ref seq);
            // Patch hits last, in slot order over patch order.
            foreach (var entry in racers)
            {
                var row = state.Racer(entry.participantId);
                if (row == null || entry.finished || entry.recovering)
                    continue;
                ResolvePatchHits(state, row, entry, result.events,
                    ref seq);
            }

            return result;
        }

        // Explicit grant seam for the host's ack path: a stale or
        // mismatched generation, an inactive site, or a full slot all
        // refuse without mutating — the wire can never re-collect a
        // pickup it already consumed.
        public static bool TryGrantFromPickup(GadgetWorldState state,
            string siteId, long generation, string participantId,
            GadgetDefinitionCatalog catalog, out DrivingEvent granted)
        {
            granted = null;
            if (state == null || catalog == null
                || string.IsNullOrEmpty(siteId)
                || string.IsNullOrEmpty(participantId))
                return false;
            GadgetPickupState pickup = null;
            foreach (var candidate in state.pickups)
                if (candidate != null && candidate.siteId == siteId)
                    pickup = candidate;
            var row = state.Racer(participantId);
            if (pickup == null || !pickup.active
                || pickup.generation != generation
                || row == null || !string.IsNullOrEmpty(row.heldItemId))
                return false;
            GrantItem(state, row, catalog);
            pickup.active = false;
            pickup.respawnTicksRemaining = catalog.pickupRespawnTicks;
            granted = new DrivingEvent
            {
                kind = EventPickup,
                detail = row.heldItemId,
                participantId = participantId,
                tick = state.tick
            };
            return true;
        }

        // --- internals -------------------------------------------------------

        private static int CompareBySlotThenId(GadgetRacerInput a,
            GadgetRacerInput b)
        {
            if (a == null) return b == null ? 0 : 1;
            if (b == null) return -1;
            var bySlot = a.slot.CompareTo(b.slot);
            return bySlot != 0 ? bySlot
                : string.CompareOrdinal(a.participantId, b.participantId);
        }

        // World clocks: respawn timers count down and mint fresh
        // generations; patch lifetimes expire oldest-first.
        private static void TickWorldClocks(GadgetWorldState state,
            List<DrivingEvent> events, ref int seq)
        {
            foreach (var pickup in state.pickups)
            {
                if (pickup == null || pickup.active) continue;
                if (--pickup.respawnTicksRemaining <= 0)
                {
                    pickup.active = true;
                    pickup.generation = ++state.nextPickupGeneration;
                    events.Add(Emit(state, EventRespawn, "",
                        pickup.siteId, ref seq));
                }
            }
            for (var i = state.patches.Count - 1; i >= 0; i--)
            {
                var patch = state.patches[i];
                if (patch == null) { state.patches.RemoveAt(i); continue; }
                if (patch.armingTicksRemaining > 0) patch.armingTicksRemaining--;
                if (--patch.ticksRemaining <= 0)
                    state.patches.RemoveAt(i);
            }
            for (var i = state.projectiles.Count - 1; i >= 0; i--)
                if (--state.projectiles[i].ticksRemaining <= 0) state.projectiles.RemoveAt(i);
            for (var i = state.pulses.Count - 1; i >= 0; i--)
                if (--state.pulses[i].ticksRemaining <= 0) state.pulses.RemoveAt(i);
        }

        private static void TickRacerClocks(GadgetRacerState row)
        {
            if (!string.IsNullOrEmpty(row.heldItemId)) row.heldTicks++;
            if (row.turboTicksRemaining > 0) row.turboTicksRemaining--;
            if (row.foamSlowTicksRemaining > 0
                && --row.foamSlowTicksRemaining == 0)
                row.foamSlowMultiplier = 1f;
            if (row.foamImmunityTicksRemaining > 0)
                row.foamImmunityTicksRemaining--;
        }

        // The press edge consumes the held slot and applies the item by
        // kind: turbo refreshes (never stacks), the shield takes its
        // authored charges, foam drops a bounded patch under the racer.
        private static void UseItem(GadgetWorldState state,
            GadgetRacerState row, GadgetRacerInput entry,
            List<GadgetRacerInput> racers,
            GadgetDefinitionCatalog catalog, List<DrivingEvent> events,
            ref int seq)
        {
            var itemId = row.heldItemId;
            var def = catalog != null ? catalog.Gadget(itemId) : null;
            row.heldItemId = "";
            row.heldKind = "";
            row.heldTicks = 0;
            events.Add(Emit(state, EventUsed, entry.participantId,
                itemId, ref seq));
            if (def == null) return; // unknown authored id: slot clears
            switch (def.kind)
            {
                case GadgetKind.Turbo:
                    row.turboTicksRemaining = def.durationTicks;
                    row.turboAccelerationMultiplier =
                        def.accelerationMultiplier;
                    row.turboSpeedCapMultiplier = def.speedCapMultiplier;
                    break;
                case GadgetKind.BubbleShield:
                    row.shieldCharges = def.charges;
                    break;
                case GadgetKind.FoamTrap:
                case GadgetKind.Mine:
                    DropPatch(state, entry, def, events, ref seq);
                    break;
                case GadgetKind.HomingMissile:
                    LaunchMissile(state, entry, racers, def, events, ref seq);
                    break;
                case GadgetKind.EmpPulse:
                    FireEmp(state, entry, racers, def, events, ref seq);
                    break;
            }
        }

        private static void DropPatch(GadgetWorldState state,
            GadgetRacerInput entry, GadgetDefinition def,
            List<DrivingEvent> events, ref int seq)
        {
            // Bounded world effects: at the cap the oldest patch retires
            // so a spamming racer can never grow the snapshot unboundedly.
            while (state.patches.Count >= NetworkSettings.GadgetPatchCapacity)
                state.patches.RemoveAt(0);
            var patch = new GadgetPatchState
            {
                kind = def.kind,
                armingTicksRemaining = def.kind == GadgetKind.Mine ? def.armingTicks : 0,
                patchId = "patch-" + (++state.nextPatchSerial)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ownerParticipantId = entry.participantId ?? "",
                x = entry.x - (def.kind == GadgetKind.Mine ? (float)Math.Sin(entry.heading) * 3f : 0f),
                y = entry.y,
                z = entry.z - (def.kind == GadgetKind.Mine ? (float)Math.Cos(entry.heading) * 3f : 0f),
                radiusMeters = def.patchRadiusMeters,
                verticalRangeMeters = PatchVerticalRangeMeters,
                ticksRemaining = def.patchLifetimeTicks,
                slowMultiplier = def.slowMultiplier,
                slowTicks = def.slowDurationTicks,
                immunityTicks = def.immunityTicks
            };
            state.patches.Add(patch);
            events.Add(Emit(state, def.kind == GadgetKind.Mine ? EventMineDropped : EventFoamDropped, entry.participantId,
                patch.patchId, ref seq));
        }

        // Swept-segment collection: a racer that drove through an active
        // site claims one seeded item when its slot is free; a full slot
        // leaves the pickup active for everyone else.
        private static void CollectPickups(GadgetWorldState state,
            GadgetRacerState row, GadgetRacerInput entry,
            GadgetDefinitionCatalog catalog, List<DrivingEvent> events,
            ref int seq)
        {
            if (!string.IsNullOrEmpty(row.heldItemId)
                && catalog != null && catalog.heldSlotCapacity <= 1)
                return;
            foreach (var pickup in state.pickups)
            {
                if (pickup == null || !pickup.active) continue;
                if (!Swept(entry, pickup.x, pickup.y, pickup.z,
                        pickup.radiusMeters, pickup.verticalRangeMeters))
                    continue;
                if (!string.IsNullOrEmpty(row.heldItemId)) continue;
                GrantItem(state, row, catalog);
                pickup.active = false;
                pickup.respawnTicksRemaining =
                    catalog != null ? catalog.pickupRespawnTicks : 0;
                events.Add(Emit(state, EventPickup, entry.participantId,
                    row.heldItemId, ref seq));
            }
        }

        private static void GrantItem(GadgetWorldState state,
            GadgetRacerState row, GadgetDefinitionCatalog catalog)
        {
            var count = catalog != null && catalog.gadgets != null
                ? catalog.gadgets.Count : 0;
            if (count == 0) return;
            if (state.itemBag == null || state.itemBag.Count != count
                || state.bagCursor >= count)
            {
                // Reshuffle the bag deterministically — every cycle
                // deals each authored gadget exactly once.
                state.itemBag.Clear();
                for (var i = 0; i < count; i++) state.itemBag.Add(i);
                var rng = (uint)(state.rngState & 0xFFFFFFFFu);
                Shuffle(state.itemBag, ref rng);
                state.rngState = rng;
                state.bagCursor = 0;
            }
            var def = catalog.gadgets[state.itemBag[state.bagCursor++]];
            row.heldItemId = def != null ? def.id : "";
            row.heldKind = def != null ? def.kind : "";
            row.heldGeneration = ++state.nextPickupGeneration;
            row.heldTicks = 0;
        }

        // Foam resolution: swept segments against patch columns, owner
        // and already-resolved patches skip. A shielded hit consumes one
        // charge and marks the patch permanently resolved for that
        // racer; an unshielded hit applies the authored slow + immunity
        // window, also resolving the patch for that racer forever.
        private static void ResolvePatchHits(GadgetWorldState state,
            GadgetRacerState row, GadgetRacerInput entry,
            List<DrivingEvent> events, ref int seq)
        {
            if (state.patches == null || state.patches.Count == 0) return;
            foreach (var patch in state.patches)
            {
                if (patch == null) continue;
                if (patch.kind == GadgetKind.Mine || row.foamImmunityTicksRemaining > 0) continue;
                if (patch.ownerParticipantId == entry.participantId)
                    continue;
                if (row.immunePatchIds != null
                    && row.immunePatchIds.Contains(patch.patchId))
                    continue;
                if (!Swept(entry, patch.x, patch.y, patch.z,
                        patch.radiusMeters, patch.verticalRangeMeters))
                    continue;
                MarkPatchResolved(row, patch.patchId);
                if (row.shieldCharges > 0)
                {
                    row.shieldCharges--;
                    events.Add(Emit(state, EventShieldBlocked,
                        entry.participantId, patch.patchId, ref seq));
                }
                else
                {
                    row.foamSlowTicksRemaining = patch.slowTicks;
                    row.foamSlowMultiplier = patch.slowMultiplier;
                    row.foamImmunityTicksRemaining =
                        patch.slowTicks + patch.immunityTicks;
                    events.Add(Emit(state, EventFoamHit,
                        entry.participantId, patch.patchId, ref seq));
                }
                return; // at most one hit per boundary
            }
        }

        private static void MarkPatchResolved(GadgetRacerState row,
            string patchId)
        {
            if (row.immunePatchIds == null)
                row.immunePatchIds = new List<string>();
            if (row.immunePatchIds.Contains(patchId)) return;
            // Bounded per-racer resolution list: the oldest ids retire —
            // expired patches can never re-arrive so dropping them is
            // safe.
            while (row.immunePatchIds.Count
                >= NetworkSettings.GadgetImmunePatchCapacity)
                row.immunePatchIds.RemoveAt(0);
            row.immunePatchIds.Add(patchId);
        }

        // Swept-segment overlap: the closest point on any segment to the
        // trigger centre must land inside the XZ radius AND the vertical
        // band — a different-height road never interacts (flyovers pass
        // clean over ground-level sites and patches).
        private static bool Swept(GadgetRacerInput entry, float cx,
            float cy, float cz, float radius, float verticalRange)
        {
            if (entry == null || entry.segments == null) return false;
            var r2 = radius * radius;
            foreach (var seg in entry.segments)
            {
                var dx = seg.bx - seg.ax;
                var dy = seg.by - seg.ay;
                var dz = seg.bz - seg.az;
                var len2 = dx * dx + dy * dy + dz * dz;
                var t = len2 > 1e-8f
                    ? ((cx - seg.ax) * dx + (cy - seg.ay) * dy
                        + (cz - seg.az) * dz) / len2
                    : 0f;
                t = t < 0f ? 0f : t > 1f ? 1f : t;
                var px = seg.ax + dx * t;
                var py = seg.ay + dy * t;
                var pz = seg.az + dz * t;
                var ex = px - cx;
                var ez = pz - cz;
                if (ex * ex + ez * ez <= r2
                    && Math.Abs(py - cy) <= verticalRange)
                    return true;
            }
            return false;
        }

        // Deterministic Fisher-Yates over the item bag.
        private static void Shuffle(List<int> bag, ref uint rng)
        {
            for (var i = bag.Count - 1; i > 0; i--)
            {
                var j = (int)(Next(ref rng) % (uint)(i + 1));
                var tmp = bag[i];
                bag[i] = bag[j];
                bag[j] = tmp;
            }
        }

        private static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            if (state == 0) state = 0x9E3779B9u;
            return state;
        }

        private static DrivingEvent Emit(GadgetWorldState state,
            string kind, string participantId, string detail, ref int seq)
        {
            return new DrivingEvent
            {
                id = "gadget-" + (state.raceId ?? "")
                    + "-" + state.tick.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + (++seq).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                tick = state.tick,
                kind = kind,
                commandId = "",
                detail = detail ?? "",
                participantId = participantId ?? "",
                attribution = ""
            };
        }
    }
}
