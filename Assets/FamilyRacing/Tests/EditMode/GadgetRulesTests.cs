using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M5 Task 5: the authoritative gadget rules — pure, deterministic,
    // host-owned. Pickups grant through swept segment overlap only,
    // collection ties break by roster slot, distribution is seeded and
    // standings-independent, effects tick in unpaused boundaries, and a
    // snapshot/wire copy can never replay or double-apply an effect.
    public sealed class GadgetRulesTests
    {
        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance,
                    x = pose.x,
                    y = 0f,
                    z = pose.z,
                    normalY = 1f,
                    distance = pose.y,
                    surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy,
                float dz, float halfWidth, float halfLength, float height)
            {
                return new SweepHit { hit = false, fraction = 1f };
            }
        }

        // --- fixtures -------------------------------------------------------

        private static GadgetDefinitionCatalog Catalog()
        {
            var catalog = new GadgetDefinitionCatalog
            {
                pickupRespawnTicks = 300,
                heldSlotCapacity = 1
            };
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "turbo-01", kind = GadgetKind.Turbo,
                durationTicks = 60, accelerationMultiplier = 1.25f,
                speedCapMultiplier = 1.10f
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "shield-01", kind = GadgetKind.BubbleShield,
                charges = 1
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "foam-01", kind = GadgetKind.FoamTrap,
                patchRadiusMeters = 3f, patchLifetimeTicks = 180,
                slowMultiplier = 0.70f, slowDurationTicks = 45,
                immunityTicks = 60
            });
            string error;
            Assert.IsTrue(catalog.Validate(out error), error);
            return catalog;
        }

        private static GadgetPickupSite Site(string id, float x, float z,
            float y = 0f)
        {
            return new GadgetPickupSite
            {
                id = id, x = x, y = y, z = z,
                radiusMeters = 2f, verticalRangeMeters = 2.5f
            };
        }

        private static GadgetSegment Seg(float ax, float az, float bx, float bz,
            float y = 0f)
        {
            return new GadgetSegment
            {
                ax = ax, ay = y, az = az,
                bx = bx, by = y, bz = bz
            };
        }

        private static GadgetRacerInput Racer(string id, int slot,
            float x, float z, bool gadget = false, bool recovering = false,
            bool finished = false, params GadgetSegment[] segments)
        {
            var input = new GadgetRacerInput
            {
                participantId = id,
                slot = slot,
                gadgetHeld = gadget,
                recovering = recovering,
                finished = finished,
                x = x, y = 0f, z = z
            };
            if (segments != null)
                input.segments.AddRange(segments);
            return input;
        }

        private static GadgetWorldState World(GadgetDefinitionCatalog catalog,
            GadgetPickupSite[] sites, params string[] participants)
        {
            var ids = participants != null && participants.Length > 0
                ? participants
                : new[] { "p0", "p1", "p2", "p3" };
            return GadgetRules.Create(catalog,
                sites ?? new GadgetPickupSite[0], 0xC0FFEEu,
                new List<string>(ids));
        }

        private static GadgetStepResult Step(GadgetWorldState world,
            GadgetDefinitionCatalog catalog,
            params GadgetRacerInput[] racers)
        {
            var input = new GadgetStepInput { tick = world.tick + 1 };
            if (racers != null) input.racers.AddRange(racers);
            return GadgetRules.Step(input, world, catalog);
        }

        private static GadgetRacerState Row(GadgetWorldState world, string id)
        {
            return world != null ? world.Racer(id) : null;
        }

        // Grants a specific item directly into the slot (the rules-level
        // grant path still validates catalog membership).
        private static void Grant(GadgetWorldState world, string participantId,
            string itemId, GadgetDefinitionCatalog catalog = null)
        {
            var row = Row(world, participantId);
            row.heldItemId = itemId;
            var def = catalog != null ? catalog.Gadget(itemId) : null;
            row.heldKind = def != null ? def.kind : "";
            row.heldGeneration = ++world.nextPickupGeneration;
            row.heldTicks = 0;
        }

        // --- pinned authored numbers ----------------------------------------

        [Test]
        public void AuthoredDefaultsArePinned()
        {
            var catalog = Catalog();
            var turbo = catalog.Gadget("turbo-01");
            Assert.AreEqual(60, turbo.durationTicks);
            Assert.AreEqual(1.25f, turbo.accelerationMultiplier, 1e-6f);
            Assert.AreEqual(1.10f, turbo.speedCapMultiplier, 1e-6f);
            var shield = catalog.Gadget("shield-01");
            Assert.AreEqual(1, shield.charges);
            var foam = catalog.Gadget("foam-01");
            Assert.AreEqual(3f, foam.patchRadiusMeters, 1e-6f);
            Assert.AreEqual(180, foam.patchLifetimeTicks);
            Assert.AreEqual(0.70f, foam.slowMultiplier, 1e-6f);
            Assert.AreEqual(45, foam.slowDurationTicks);
            Assert.AreEqual(60, foam.immunityTicks);
            Assert.AreEqual(300, catalog.pickupRespawnTicks);
            Assert.AreEqual(1, catalog.heldSlotCapacity);
        }

        // --- collection ------------------------------------------------------

        [Test]
        public void SweepOntoPickupGrantsOneItem()
        {
            var catalog = Catalog();
            var world = World(catalog, new[] { Site("s1", 0f, 10f) });
            var step = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) }));
            var row = Row(step.state, "p0");
            Assert.IsFalse(string.IsNullOrEmpty(row.heldItemId),
                "driving through the site grants one item");
            Assert.IsFalse(step.state.pickups[0].active);
            Assert.AreEqual(catalog.pickupRespawnTicks,
                step.state.pickups[0].respawnTicksRemaining);
            var picked = false;
            foreach (var ev in step.events)
                if (ev.kind == "gadget.pickup" && ev.participantId == "p0")
                    picked = true;
            Assert.IsTrue(picked, "gadget.pickup event for the collector");
        }

        [Test]
        public void FullSlotRefusalLeavesPickupActive()
        {
            var catalog = Catalog();
            var world = World(catalog,
                new[] { Site("s1", 0f, 10f), Site("s2", 0f, 20f) });
            var step = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[] { Seg(0f, 8f, 0f, 12f) }));
            var held = Row(step.state, "p0").heldItemId;
            Assert.IsFalse(string.IsNullOrEmpty(held));
            // Same racer crosses a second site while full: untouched.
            var step2 = Step(step.state, catalog,
                Racer("p0", 0, 0f, 20f, segments: new[]
                    { Seg(0f, 18f, 0f, 22f) }));
            Assert.IsTrue(step2.state.pickups[1].active,
                "a full slot cannot consume another pickup");
            Assert.AreEqual(held, Row(step2.state, "p0").heldItemId,
                "the held item is not replaced");
            // A second racer can still take it.
            var step3 = Step(step2.state, catalog,
                Racer("p0", 0, 0f, 20f),
                Racer("p1", 1, 0f, 20f, segments: new[]
                    { Seg(0f, 18f, 0f, 22f) }));
            Assert.IsFalse(step3.state.pickups[1].active);
            Assert.IsFalse(string.IsNullOrEmpty(Row(step3.state, "p1").heldItemId));
        }

        [Test]
        public void SameTickCollectionTieBreaksBySlot()
        {
            var catalog = Catalog();
            var world = World(catalog,
                new[] { Site("s1", 0f, 10f) });
            // Two racers sweep the same site on the same tick: the lower
            // slot collects; the higher slot leaves it consumed.
            var step = Step(world, catalog,
                Racer("p2", 2, 0f, 10f, segments: new[]
                    { Seg(-2f, 10f, 2f, 10f) }),
                Racer("p1", 1, 0f, 10f, segments: new[]
                    { Seg(-2f, 10f, 2f, 10f) }));
            Assert.IsFalse(string.IsNullOrEmpty(Row(step.state, "p1").heldItemId),
                "slot 1 beats slot 2 on the same-tick tie");
            Assert.IsTrue(string.IsNullOrEmpty(Row(step.state, "p2").heldItemId));
        }

        [Test]
        public void SeededDistributionIndependentOfStandings()
        {
            var catalog = Catalog();
            // Collect many pickups and count per gadget kind: a full bag
            // cycle grants each authored gadget exactly once per cycle.
            var world = World(catalog,
                new[] { Site("s1", 0f, 10f) });
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var grants = new List<string>();
            for (var round = 0; round < catalog.gadgets.Count * 3; round++)
            {
                var step = Step(world, catalog,
                    Racer("p0", 0, 0f, 10f, segments: new[]
                        { Seg(0f, 8f, 0f, 12f) }));
                var row = Row(step.state, "p0");
                Assert.IsFalse(string.IsNullOrEmpty(row.heldItemId));
                grants.Add(row.heldItemId);
                counts[row.heldItemId] =
                    (counts.ContainsKey(row.heldItemId)
                        ? counts[row.heldItemId] : 0) + 1;
                row.heldItemId = "";
                // Respawn the site immediately for the next collection —
                // direct state edit is legal here: this is the host's
                // authoritative copy in a test.
                world = step.state;
                world.pickups[0].active = true;
            }
            foreach (var gadget in catalog.gadgets)
            {
                int count;
                Assert.IsTrue(counts.TryGetValue(gadget.id, out count),
                    "every authored gadget must be reachable: " + gadget.id);
                Assert.AreEqual(3, count,
                    "one bag cycle grants each item exactly once per cycle");
            }
            // Standings/order independence: a different participant
            // ordering on the same seed must produce the same grant
            // sequence. Collect once from fresh worlds where the
            // collecting racer sits at different slots.
            var worldB = World(catalog, new[] { Site("s1", 0f, 10f) });
            var stepB = Step(worldB, catalog,
                Racer("p1", 1, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) }));
            Assert.AreEqual(grants[0], Row(stepB.state, "p1").heldItemId,
                "the seed decides the draw, not who is winning");
        }

        // --- item effects ----------------------------------------------------

        [Test]
        public void TurboUseAppliesAuthoredModifiersThenExpires()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "turbo-01");
            // Press edge: activation consumes the item and starts the
            // authored duration.
            var step = Step(world, catalog, Racer("p0", 0, 0f, 0f,
                gadget: true));
            var row = Row(step.state, "p0");
            Assert.AreEqual("", row.heldItemId);
            Assert.AreEqual(60, row.turboTicksRemaining);
            var used = false;
            foreach (var ev in step.events)
                if (ev.kind == "gadget.used" && ev.participantId == "p0"
                    && ev.detail == "turbo-01")
                    used = true;
            Assert.IsTrue(used, "gadget.used carries the item id");
            var mods = GadgetRules.ModifierFor(step.state, "p0");
            Assert.AreEqual(1.25f, mods.accelerationMultiplier, 1e-6f);
            Assert.AreEqual(1.10f, mods.speedCapMultiplier, 1e-6f);
            // Release then count the remaining ticks out — 60 live
            // boundaries drain the 60-tick envelope to zero.
            world = step.state;
            for (var i = 0; i < 60; i++)
                world = Step(world, catalog,
                    Racer("p0", 0, 0f, 0f)).state;
            row = Row(world, "p0");
            Assert.AreEqual(0, row.turboTicksRemaining,
                "turbo expires after its authored duration");
            mods = GadgetRules.ModifierFor(world, "p0");
            Assert.AreEqual(1f, mods.accelerationMultiplier, 1e-6f);
            Assert.AreEqual(1f, mods.speedCapMultiplier, 1e-6f);
        }

        [Test]
        public void TurboRefreshRenewsDurationWithoutStacking()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "turbo-01");
            var world1 = Step(world, catalog,
                Racer("p0", 0, 0f, 0f, gadget: true)).state;
            // Burn 30 ticks, then refresh with a second turbo.
            for (var i = 0; i < 30; i++)
                world1 = Step(world1, catalog,
                    Racer("p0", 0, 0f, 0f)).state;
            Grant(world1, "p0", "turbo-01");
            var world2 = Step(world1, catalog,
                Racer("p0", 0, 0f, 0f, gadget: true)).state;
            var row = Row(world2, "p0");
            Assert.AreEqual(60, row.turboTicksRemaining,
                "repeat activation refreshes the authored duration");
            var mods = GadgetRules.ModifierFor(world2, "p0");
            Assert.AreEqual(1.25f, mods.accelerationMultiplier, 1e-6f,
                "modifiers refresh, never multiply");
            Assert.AreEqual(1.10f, mods.speedCapMultiplier, 1e-6f);
        }

        [Test]
        public void FoamPatchSlowsOpponentThenGrantsImmunity()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "foam-01");
            // Owner drops the patch at its committed pose.
            var world1 = Step(world, catalog,
                Racer("p0", 0, 50f, 50f, gadget: true)).state;
            Assert.AreEqual(1, world1.patches.Count);
            var patch = world1.patches[0];
            Assert.AreEqual(180, patch.ticksRemaining);
            // Owner is unaffected by its own patch.
            var world2 = Step(world1, catalog,
                Racer("p0", 0, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) }),
                Racer("p1", 1, 30f, 30f)).state;
            Assert.AreEqual(0, Row(world2, "p0").foamSlowTicksRemaining,
                "the owner never slows on its own patch");
            // Rival sweeps the patch: authored slow + immunity window.
            var world3 = Step(world2, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) })).state;
            var victim = Row(world3, "p1");
            Assert.AreEqual(45, victim.foamSlowTicksRemaining);
            Assert.AreEqual(45 + 60, victim.foamImmunityTicksRemaining);
            var mods = GadgetRules.ModifierFor(world3, "p1");
            // Spec: "opponents slow to x0.70 drive acceleration/speed
            // cap" — BOTH channels carry the authored factor.
            Assert.AreEqual(0.70f, mods.speedCapMultiplier, 1e-6f,
                "foam slows through the cap multiplier");
            Assert.AreEqual(0.70f, mods.accelerationMultiplier, 1e-6f,
                "foam slows the acceleration channel too");
            var crossing = Step(world2, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) }));
            var hitSeen = false;
            foreach (var ev in crossing.events)
                if (ev.kind == "gadget.foamHit"
                    && ev.participantId == "p1")
                    hitSeen = true;
            Assert.IsTrue(hitSeen, "gadget.foamHit reports the victim");
            // The same patch can never hit that racer again — even after
            // the timed immunity lapses.
            var world4 = world3;
            for (var i = 0; i < 120; i++)
                world4 = Step(world4, catalog,
                    Racer("p1", 1, 30f, 30f)).state;
            Assert.AreEqual(0, Row(world4, "p1").foamImmunityTicksRemaining,
                "the timed immunity lapses after slow+immunity ticks");
            var world5 = Step(world4, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) })).state;
            Assert.AreEqual(0,
                Row(world5, "p1").foamSlowTicksRemaining,
                "a patch that already hit stays inert for that racer");
        }

        [Test]
        public void FoamSlowComposesWithActiveTurbo()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            // Turbo'd victim drives over a rival's patch: the authored
            // 0.70 scales BOTH channels of the turbo envelope — the
            // spec's "no stacking" bars repeat foam hits (immunity),
            // not foam+turbo co-application. Pinned: accel 1.25x0.70,
            // cap 1.10x0.70.
            Grant(world, "p1", "turbo-01");
            var turbo = Step(world, catalog,
                Racer("p1", 1, 30f, 30f, gadget: true)).state;
            Assert.Greater(Row(turbo, "p1").turboTicksRemaining, 0);
            Grant(turbo, "p0", "foam-01");
            var dropped = Step(turbo, catalog,
                Racer("p0", 0, 50f, 50f, gadget: true)).state;
            var hit = Step(dropped, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) })).state;
            var row = Row(hit, "p1");
            Assert.Greater(row.turboTicksRemaining, 0,
                "the turbo envelope is still live under the slow");
            Assert.AreEqual(45, row.foamSlowTicksRemaining);
            var mods = GadgetRules.ModifierFor(hit, "p1");
            Assert.AreEqual(1.25f * 0.70f, mods.accelerationMultiplier,
                1e-6f, "foam scales the turbo acceleration envelope");
            Assert.AreEqual(1.10f * 0.70f, mods.speedCapMultiplier,
                1e-6f, "foam scales the turbo cap envelope");
        }

        [Test]
        public void GadgetEventIdsCarryTheRaceScope()
        {
            var catalog = Catalog();
            var worldA = World(catalog, new[] { Site("s1", 0f, 10f) });
            worldA.raceId = "sess-r1";
            var worldB = worldA.Copy();
            worldB.raceId = "sess-r2";
            // Identical boundary on two race-scoped worlds: same events,
            // disjoint identities — a rematch's minted ids can never
            // collide with the concluded race's dedup window.
            var collect = new[]
            {
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) })
            };
            var stepA = Step(worldA, catalog, collect);
            var stepB = Step(worldB, catalog, collect);
            Assert.AreEqual(stepA.events.Count, stepB.events.Count);
            Assert.Greater(stepA.events.Count, 0,
                "the shared collection boundary emits at least one event");
            var seenA = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ev in stepA.events) seenA.Add(ev.id);
            foreach (var ev in stepB.events)
            {
                Assert.IsFalse(seenA.Contains(ev.id),
                    "race-2 event identity must differ from race-1: "
                    + ev.id);
                StringAssert.Contains("sess-r2", ev.id,
                    "the race identity prefixes the event id");
            }
            // Determinism preserved: the SAME world boundary replayed
            // mints the SAME ids (snapshot replay cannot fabricate new
            // identities).
            var replay = Step(worldA, catalog, collect);
            for (var i = 0; i < replay.events.Count; i++)
                Assert.AreEqual(stepA.events[i].id, replay.events[i].id,
                    "replayed boundaries mint identical ids");
        }

        [Test]
        public void FoamImmunityBlocksOtherPatchesAndExpires()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "foam-01");
            world = Step(world, catalog,
                Racer("p0", 0, 10f, 10f, gadget: true)).state;
            Grant(world, "p0", "foam-01");
            // The press edge latched on the first drop — release before
            // the second press or the held input cannot fire again.
            world = Step(world, catalog, Racer("p0", 0, 40f, 40f)).state;
            world = Step(world, catalog,
                Racer("p0", 0, 40f, 40f, gadget: true)).state;
            Assert.AreEqual(2, world.patches.Count);
            // Rival hits patch one.
            world = Step(world, catalog,
                Racer("p1", 1, 10f, 10f, segments: new[]
                    { Seg(8f, 10f, 12f, 10f) })).state;
            Assert.AreEqual(45, Row(world, "p1").foamSlowTicksRemaining);
            // While slowed, patch two cannot chain-hit.
            world = Step(world, catalog,
                Racer("p1", 1, 40f, 40f, segments: new[]
                    { Seg(38f, 40f, 42f, 40f) })).state;
            Assert.AreEqual(44, Row(world, "p1").foamSlowTicksRemaining,
                "immunity suppresses a chain hit mid-slow");
            // Run out the immunity window entirely, then patch two hits.
            for (var i = 0; i < 110; i++)
                world = Step(world, catalog,
                    Racer("p1", 1, 30f, 30f)).state;
            world = Step(world, catalog,
                Racer("p1", 1, 40f, 40f, segments: new[]
                    { Seg(38f, 40f, 42f, 40f) })).state;
            Assert.AreEqual(45, Row(world, "p1").foamSlowTicksRemaining,
                "after immunity a different patch can hit again");
        }

        [Test]
        public void ShieldBlocksOneRivalFoamHit()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "foam-01");
            world = Step(world, catalog,
                Racer("p0", 0, 50f, 50f, gadget: true)).state;
            Grant(world, "p1", "shield-01");
            world = Step(world, catalog,
                Racer("p1", 1, 30f, 30f, gadget: true)).state;
            Assert.AreEqual(1, Row(world, "p1").shieldCharges);
            // Shielded rival sweeps the patch: one charge absorbs it,
            // no slow is applied, and that patch can never hit again.
            world = Step(world, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) })).state;
            var victim = Row(world, "p1");
            Assert.AreEqual(0, victim.shieldCharges,
                "the block consumes the single authored charge");
            Assert.AreEqual(0, victim.foamSlowTicksRemaining);
            // Verify the crossing step emits the block event for the
            // shielded racer.
            var world2 = World(catalog, null);
            Grant(world2, "p0", "foam-01");
            world2 = Step(world2, catalog,
                Racer("p0", 0, 50f, 50f, gadget: true)).state;
            Grant(world2, "p1", "shield-01");
            world2 = Step(world2, catalog,
                Racer("p1", 1, 30f, 30f, gadget: true)).state;
            var crossing = Step(world2, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) }));
            var sawBlock = false;
            foreach (var ev in crossing.events)
                if (ev.kind == "gadget.shieldBlocked"
                    && ev.participantId == "p1")
                    sawBlock = true;
            Assert.IsTrue(sawBlock);
            // The blocked patch stays inert for this racer permanently.
            var later = crossing.state;
            for (var i = 0; i < 90; i++)
                later = Step(later, catalog, Racer("p1", 1, 30f, 30f)).state;
            later = Step(later, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f) })).state;
            Assert.AreEqual(0, Row(later, "p1").foamSlowTicksRemaining);
        }

        // --- world lifecycle -------------------------------------------------

        [Test]
        public void PickupRespawnsAfterAuthoredTicks()
        {
            var catalog = Catalog();
            var world = World(catalog, new[] { Site("s1", 0f, 10f) });
            var collected = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) })).state;
            var generation = collected.pickups[0].generation;
            var current = collected;
            for (var i = 1; i < 300; i++)
            {
                current = Step(current, catalog,
                    Racer("p0", 0, 30f, 30f)).state;
                Assert.IsFalse(current.pickups[0].active,
                    "pickup stays consumed for the authored window");
            }
            current = Step(current, catalog,
                Racer("p0", 0, 30f, 30f)).state;
            Assert.IsTrue(current.pickups[0].active,
                "respawn lands at exactly 300 unpaused ticks");
            Assert.AreNotEqual(generation, current.pickups[0].generation,
                "a respawn mints a new generation");
        }

        [Test]
        public void RecoveryCannotCollectOrRespawnPickup()
        {
            var catalog = Catalog();
            var world = World(catalog, new[] { Site("s1", 0f, 10f) });
            // A recovery-flagged boundary crossing the site collects
            // nothing and leaves every world clock untouched.
            var step = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, recovering: true,
                    segments: new[] { Seg(0f, 8f, 0f, 12f) }));
            Assert.IsTrue(string.IsNullOrEmpty(
                Row(step.state, "p0").heldItemId),
                "a respawn boundary cannot collect");
            Assert.IsTrue(step.state.pickups[0].active);
            var pickups = 0;
            foreach (var ev in step.events)
                if (ev.kind == "gadget.pickup") pickups++;
            Assert.AreEqual(0, pickups);
        }

        [Test]
        public void DuplicateInputDoesNotUseTwoItems()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "turbo-01");
            // The press edge consumes exactly one item; holding the same
            // press across later ticks can never consume a second.
            var world1 = Step(world, catalog,
                Racer("p0", 0, 0f, 0f, gadget: true)).state;
            Assert.AreEqual(60, Row(world1, "p0").turboTicksRemaining);
            Grant(world1, "p0", "turbo-01");
            var world2 = Step(world1, catalog,
                Racer("p0", 0, 0f, 0f, gadget: true)).state;
            Assert.AreEqual("turbo-01", Row(world2, "p0").heldItemId,
                "a held press cannot fire twice — release is required");
            // Release, then press: the latched item now fires.
            var world3 = Step(world2, catalog,
                Racer("p0", 0, 0f, 0f)).state;
            var press = Step(world3, catalog,
                Racer("p0", 0, 0f, 0f, gadget: true));
            Assert.AreEqual("", Row(press.state, "p0").heldItemId);
            var fired = false;
            foreach (var ev in press.events)
                if (ev.kind == "gadget.used"
                    && ev.participantId == "p0")
                    fired = true;
            Assert.IsTrue(fired,
                "release-then-press uses the next held item");
        }

        [Test]
        public void SnapshotReplayDoesNotReplayEffect()
        {
            var catalog = Catalog();
            var world = World(catalog, new[] { Site("s1", 0f, 10f) });
            var step1 = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) }));
            // The wire decode delivers a deep copy: replaying the same
            // input against the copied world must produce an identical
            // state — never a double collect, never a re-fired event.
            var copy = step1.state.Copy();
            var stepA = Step(step1.state, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) }));
            var stepB = Step(copy, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) }));
            Assert.AreEqual(stepA.state.tick, stepB.state.tick);
            Assert.AreEqual(stepA.state.pickups[0].active,
                stepB.state.pickups[0].active);
            Assert.AreEqual(stepA.state.pickups[0].respawnTicksRemaining,
                stepB.state.pickups[0].respawnTicksRemaining);
            Assert.AreEqual(stepA.state.rngState, stepB.state.rngState);
            Assert.AreEqual(Row(stepA.state, "p0").heldItemId,
                Row(stepB.state, "p0").heldItemId,
                "the deep copy replays to the identical world");
            // The consumed pickup stays consumed on both branches — a
            // replayed snapshot cannot re-collect it.
            Assert.IsFalse(stepA.state.pickups[0].active);
            Assert.IsFalse(stepB.state.pickups[0].active);
        }

        [Test]
        public void DifferentHeightsNeverInteract()
        {
            var catalog = Catalog();
            var world = World(catalog,
                new[] { Site("s1", 0f, 10f, y: 0f) });
            // An elevated segment at the same XZ cannot reach the
            // ground pickup.
            var step = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f, y: 8f) }));
            Assert.IsTrue(step.state.pickups[0].active,
                "a flyover never collects the ground-level pickup");
            // The same holds for a patch at a different height.
            var world2 = World(catalog, null);
            Grant(world2, "p0", "foam-01");
            world2 = Step(world2, catalog,
                Racer("p0", 0, 50f, 50f, gadget: true)).state;
            var over = Step(world2, catalog,
                Racer("p1", 1, 50f, 50f, segments: new[]
                    { Seg(48f, 50f, 52f, 50f, y: 9f) }));
            Assert.AreEqual(0,
                Row(over.state, "p1").foamSlowTicksRemaining,
                "a patch does not hit a racer flying above it");
        }

        [Test]
        public void WorldEffectsStayBounded()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            // Spam foam drops faster than they expire: the patch list
            // never exceeds the shared cap.
            for (var i = 0; i < NetworkSettings.GadgetPatchCapacity + 8; i++)
            {
                Grant(world, "p0", "foam-01");
                var released = Step(world, catalog,
                    Racer("p0", 0, 0f, 0f)).state;
                world = Step(released, catalog,
                    Racer("p0", 0, 0f, 0f, gadget: true)).state;
                Assert.IsTrue(world.patches.Count
                        <= NetworkSettings.GadgetPatchCapacity,
                    "bounded world effects: oldest patches retire");
            }
            // Immune-patch bookkeeping is likewise bounded.
            var row = Row(world, "p0");
            Assert.IsTrue(row.immunePatchIds.Count
                <= NetworkSettings.GadgetImmunePatchCapacity);
        }

        [Test]
        public void StaleGenerationCannotMutate()
        {
            var catalog = Catalog();
            var world = World(catalog, new[] { Site("s1", 0f, 10f) });
            var collected = Step(world, catalog,
                Racer("p0", 0, 0f, 10f, segments: new[]
                    { Seg(0f, 8f, 0f, 12f) })).state;
            var stale = collected.pickups[0].generation;
            // Fast-forward the respawn: the generation rotates.
            var current = collected;
            for (var i = 0; i < 300; i++)
                current = Step(current, catalog,
                    Racer("p0", 0, 30f, 30f)).state;
            DrivingEvent ev;
            Assert.IsFalse(GadgetRules.TryGrantFromPickup(current,
                "s1", stale, "p1", catalog, out ev),
                "a stale generation can never re-grant the pickup");
            Assert.AreEqual(stale == current.pickups[0].generation,
                false, "the live pickup carries a fresh generation");
            // The current generation still grants normally — to a
            // racer with a free slot (p0 still holds its item).
            Assert.IsTrue(GadgetRules.TryGrantFromPickup(current,
                "s1", current.pickups[0].generation, "p1", catalog, out ev));
        }

        // --- deterministic race-level integration ------------------------------

        private static CheckpointGate Gate(string id, float x, float z,
            float dirX, float dirZ)
        {
            return new CheckpointGate
            {
                id = id,
                x = x, y = 0f, z = z,
                dirX = dirX, dirZ = dirZ,
                halfWidth = 12f,
                recoverX = x - dirX * 3f, recoverY = 0f,
                recoverZ = z - dirZ * 3f,
                recoverHeading = (float)Math.Atan2(dirX, dirZ)
            };
        }

        private static CourseDefinition GadgetCourse()
        {
            var course = new CourseDefinition
            {
                id = "gadget-course",
                spawnX = 0f, spawnY = 0f, spawnZ = -60f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    Gate("start", 0f, -60f, 0f, 1f),
                    Gate("gate-1", 0f, 25f, 0f, 1f),
                    Gate("gate-2", 55f, 55f, 1f, 0f),
                    Gate("gate-3", 95f, 0f, 0f, -1f),
                    Gate("gate-4", 50f, -55f, -1f, 0f)
                },
                route = new List<RouteWaypoint>
                {
                    new RouteWaypoint { x = 0f, z = 33f, radius = 7f },
                    new RouteWaypoint { x = 63f, z = 55f, radius = 7f },
                    new RouteWaypoint { x = 95f, z = -8f, radius = 7f },
                    new RouteWaypoint { x = 44f, z = -55f, radius = 7f },
                    new RouteWaypoint { x = 0f, z = -70f, radius = 7f },
                    new RouteWaypoint { x = 0f, z = -50f, radius = 7f }
                }
            };
            course.spawnSlots.Add(new MotorPose
                { x = -5f, z = -57f });
            course.spawnSlots.Add(new MotorPose
                { x = 5f, z = -57f });
            course.spawnSlots.Add(new MotorPose
                { x = -5f, z = -62f });
            course.spawnSlots.Add(new MotorPose
                { x = 5f, z = -62f });
            // One pickup on the local racer's lane early in the run
            // (slot 0 spawns at x=-5 and drives straight +z; the AI
            // followers' lane aims stay at x >= -3, so this site is
            // deterministically the human's), plus one on the shared
            // racing line deeper into the lap.
            course.pickupSites.Add(Site("p1", -6.5f, -30f));
            course.pickupSites.Add(Site("p2", 60f, 55f));
            // A second site on the same human lane: the takeover test
            // collects it after spending the first item.
            course.pickupSites.Add(Site("p3", -6.5f, -5f));
            return course;
        }

        private static DrivingContentCatalog GadgetContent(
            CourseDefinition course)
        {
            var evt = new EventDefinition
            {
                id = "gadget-race",
                courseId = course.id,
                laps = 2,
                countdownTicks = 3,
                vehicleIds = new List<string> { "car-01" },
                completionCoins = 20
            };
            return DrivingContentCatalog.Create("gadget-1",
                new[] { VehicleTuning.Car01() },
                new[] { course },
                new[] { evt });
        }

        private static NetworkRace GadgetRace(DrivingContentCatalog content,
            GadgetDefinitionCatalog gadgets)
        {
            var specs = new List<NetworkRacerSpec>
            {
                new NetworkRacerSpec
                {
                    slot = 0, participantId = "local",
                    vehicleId = "car-01", controlSource = "local"
                },
                new NetworkRacerSpec
                {
                    slot = 1, participantId = "ai-1",
                    vehicleId = "car-01", controlSource = "ai"
                },
                new NetworkRacerSpec
                {
                    slot = 2, participantId = "ai-2",
                    vehicleId = "car-01", controlSource = "ai"
                },
                new NetworkRacerSpec
                {
                    slot = 3, participantId = "ai-3",
                    vehicleId = "car-01", controlSource = "ai"
                }
            };
            NetworkRace race;
            string error;
            Assert.IsTrue(NetworkRace.TryCreate("sess", "race-1", content,
                "gadget-race", "car", specs, out race, out error,
                gadgets), error);
            return race;
        }

        private static Dictionary<string, DriverInput> NeutralInputs()
        {
            return new Dictionary<string, DriverInput>(StringComparer.Ordinal)
            {
                { "local", new DriverInput(0f, false, false, false, false,
                    false, false) }
            };
        }

        [Test]
        public void RacePublishesGadgetWorldAndCollections()
        {
            var content = GadgetContent(GadgetCourse());
            var race = GadgetRace(content, Catalog());
            var world = new FlatWorld();
            var state = race.Step(NeutralInputs(), world);
            Assert.IsNotNull(state.gadgets,
                "the authoritative snapshot carries the gadget world");
            Assert.AreEqual(3, state.gadgets.pickups.Count);
            // Drive the local racer over the first site: ~27 m at
            // 7 m/s^2 takes under 90 ticks; 120 leaves headroom.
            for (var i = 0; i < 120 && state.gadgets.pickups[0].active; i++)
                state = race.Step(NeutralInputs(), world);
            Assert.IsFalse(state.gadgets.pickups[0].active,
                "the host grants the pickup the local racer drove over");
            var localRow = state.gadgets.Racer("local");
            Assert.IsNotNull(localRow);
            Assert.IsFalse(string.IsNullOrEmpty(localRow.heldItemId));
        }

        [Test]
        public void PauseFreezesGadgetClocks()
        {
            var content = GadgetContent(GadgetCourse());
            var race = GadgetRace(content, Catalog());
            var world = new FlatWorld();
            var state = race.Step(NeutralInputs(), world);
            for (var i = 0; i < 120 && state.gadgets.pickups[0].active; i++)
                state = race.Step(NeutralInputs(), world);
            Assert.IsFalse(state.gadgets.pickups[0].active);
            var respawnBefore = state.gadgets.pickups[0].respawnTicksRemaining;
            var tickBefore = state.gadgets.tick;
            race.SetPaused(true);
            for (var i = 0; i < 90; i++)
                state = race.Step(NeutralInputs(), world);
            Assert.AreEqual(tickBefore, state.gadgets.tick,
                "a paused boundary never advances the gadget clock");
            Assert.AreEqual(respawnBefore,
                state.gadgets.pickups[0].respawnTicksRemaining,
                "respawn timers hold under pause");
            race.SetPaused(false);
            state = race.Step(NeutralInputs(), world);
            Assert.AreEqual(respawnBefore - 1,
                state.gadgets.pickups[0].respawnTicksRemaining,
                "resume continues the same countdown, not a restarted one");
        }

        [Test]
        public void AiTakeoverCannotEarnHumanGadgetCredit()
        {
            var content = GadgetContent(GadgetCourse());
            var race = GadgetRace(content, Catalog());
            var world = new FlatWorld();
            var state = race.Step(NeutralInputs(), world);
            // Let the local human collect the first pickup, then use it.
            for (var i = 0; i < 120
                && state.gadgets.Racer("local").heldItemId == ""; i++)
                state = race.Step(NeutralInputs(), world);
            Assert.IsFalse(string.IsNullOrEmpty(
                state.gadgets.Racer("local").heldItemId));
            // Human's use is a human fact.
            var press = new Dictionary<string, DriverInput>(
                StringComparer.Ordinal)
            {
                { "local", new DriverInput(0f, false, false, true,
                    false, false, false) }
            };
            state = race.Step(press, world);
            var humanUse = false;
            foreach (var ev in state.events)
                if (ev.kind == "gadget.used"
                    && ev.participantId == "local"
                    && ev.attribution == "human")
                    humanUse = true;
            Assert.IsTrue(humanUse,
                "the human's gadget use is attributed human");
            // Release, drive on, and collect the second on-lane site so
            // the seat is holding a fresh item when control transfers.
            for (var i = 0; i < 160
                && state.gadgets.Racer("local").heldItemId == ""; i++)
                state = race.Step(NeutralInputs(), world);
            Assert.IsFalse(string.IsNullOrEmpty(
                state.gadgets.Racer("local").heldItemId),
                "the human collects a second item before the takeover");
            // Hand the seat to the AI: whatever it does from here is an
            // AI fact, never retroactively human.
            race.SetControlSource("local", "ai");
            var aiUse = false;
            for (var i = 0; i < 400 && !aiUse; i++)
            {
                state = race.Step(NeutralInputs(), world);
                foreach (var ev in state.events)
                    if (ev.kind == "gadget.used"
                        && ev.participantId == "local")
                    {
                        Assert.AreEqual("ai", ev.attribution,
                            "gadget credit follows the control source at the action tick");
                        aiUse = true;
                    }
            }
            Assert.IsTrue(aiUse,
                "the AI seat eventually earns its own gadget credit");
        }

        [Test]
        public void HeldLatchCannotDeadlockAiTakeoverGadget()
        {
            // Regression: a human who disconnects while still holding
            // the gadget button leaves the rules' edge latch set. The
            // AI seat inheriting the held item must emit a release
            // boundary first — a pulse emitted while latched is
            // swallowed, and a swallowed pulse that marks the grant
            // spent would deadlock the slot forever.
            var content = GadgetContent(GadgetCourse());
            var race = GadgetRace(content, Catalog());
            var world = new FlatWorld();
            var state = race.Step(NeutralInputs(), world);
            // The human holds the button continuously — the press edge
            // latches on the first held boundary (empty slot, nothing
            // spent) and stays latched through the collection.
            var hold = new Dictionary<string, DriverInput>(
                StringComparer.Ordinal)
            {
                { "local", new DriverInput(0f, false, false, true,
                    false, false, false) }
            };
            for (var i = 0; i < 160
                && state.gadgets.Racer("local").heldItemId == ""; i++)
                state = race.Step(hold, world);
            var row = state.gadgets.Racer("local");
            Assert.IsFalse(string.IsNullOrEmpty(row.heldItemId),
                "the human collects while still holding the button");
            Assert.IsTrue(row.gadgetHeld,
                "the held button leaves the rules' edge latch set");
            // Disconnect mid-hold: the seat transfers to AI with the
            // latch set and an unspent item in the slot.
            race.SetControlSource("local", "ai");
            var used = false;
            for (var i = 0; i < 400 && !used; i++)
            {
                state = race.Step(NeutralInputs(), world);
                foreach (var ev in state.events)
                    if (ev.kind == "gadget.used"
                        && ev.participantId == "local")
                    {
                        Assert.AreEqual("ai", ev.attribution,
                            "the spend is the AI seat's fact, not the "
                            + "departed human's");
                        used = true;
                    }
            }
            Assert.IsTrue(used,
                "the AI releases the inherited latch, then spends the "
                + "item — the slot never deadlocks");
            Assert.IsTrue(string.IsNullOrEmpty(
                state.gadgets.Racer("local").heldItemId),
                "the slot clears once the inherited item is spent");
        }

        [Test]
        public void GadgetModifiersDriveTheArcadeSeam()
        {
            var catalog = Catalog();
            var world = World(catalog, null);
            Grant(world, "p0", "turbo-01");
            var active = Step(world, catalog,
                Racer("p0", 0, 0f, 0f, gadget: true)).state;
            var mods = GadgetRules.ModifierFor(active, "p0");
            // The modifier seam measurably changes the arcade motor:
            // with turbo, the speed ceiling scales by the authored cap.
            var tuning = VehicleTuning.Car01();
            var state = new MotorState { x = 0f, z = 0f, grounded = true };
            var neutral = MotorModifiers.Neutral();
            var w = new FlatWorld();
            for (var i = 0; i < 90; i++)
            {
                var stepped = ArcadeMotor.Step(state,
                    new DriverInput(0f, false, false, false, false,
                        false, false),
                    tuning, w, 1f / 30f, null, mods);
                state = stepped.state;
            }
            var boosted = state.speed;
            state = new MotorState { x = 0f, z = 0f, grounded = true };
            for (var i = 0; i < 90; i++)
            {
                var stepped = ArcadeMotor.Step(state,
                    new DriverInput(0f, false, false, false, false,
                        false, false),
                    tuning, w, 1f / 30f, null, neutral);
                state = stepped.state;
            }
            Assert.Greater(boosted, state.speed + 0.5f,
                "turbo's authored cap multiplier reaches the motor seam");
        }
    }
}
