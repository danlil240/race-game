using System.Collections.Generic;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class CombatGadgetTests
    {
        private static GadgetDefinition Weapon(string kind)
        {
            return new GadgetDefinition
            {
                id = kind, kind = kind, patchRadiusMeters = kind == "emp-pulse" ? 12f : 2.5f,
                patchLifetimeTicks = kind == "mine" ? 360 : 90,
                durationTicks = 18, slowMultiplier = .4f, slowDurationTicks = 27,
                immunityTicks = 30
            };
        }

        private static GadgetDefinitionCatalog Catalog()
        {
            var c = new GadgetDefinitionCatalog();
            c.gadgets.Add(Weapon("homing-missile"));
            c.gadgets.Add(Weapon("mine"));
            c.gadgets.Add(Weapon("emp-pulse"));
            return c;
        }

        private static GadgetWorldState World(string kind)
        {
            var w = GadgetRules.Create(Catalog(), null, 42, new[] { "owner", "front", "rear" });
            var r = w.Racer("owner");
            r.heldItemId = kind; r.heldKind = kind;
            return w;
        }

        private static GadgetRacerInput Racer(string id, int slot, float x, float z, bool use = false)
        {
            return new GadgetRacerInput
            {
                participantId = id, slot = slot, x = x, z = z, gadgetHeld = use,
                segments = new List<GadgetSegment> { new GadgetSegment { ax = x, az = z, bx = x, bz = z } }
            };
        }

        private static GadgetStepResult Step(GadgetWorldState w, params GadgetRacerInput[] racers)
        {
            return GadgetRules.Step(new GadgetStepInput { tick = w.tick + 1, racers = new List<GadgetRacerInput>(racers) }, w, Catalog());
        }

        [TestCase("homing-missile")]
        [TestCase("mine")]
        [TestCase("emp-pulse")]
        public void NewWeaponsAreValidCatalogEntries(string kind)
        {
            Assert.IsTrue(Weapon(kind).Validate(out var error), error);
        }

        [Test]
        public void MissileTravelsBeforeHittingRivalAhead()
        {
            var w = World("homing-missile");
            w = Step(w, Racer("owner", 0, 0, 0, true), Racer("front", 1, 0, 20), Racer("rear", 2, 0, -10)).state;
            Assert.AreEqual(0, w.Racer("front").foamSlowTicksRemaining, "no instant hit");
            for (var i = 0; i < 20; i++)
                w = Step(w, Racer("owner", 0, 0, 0), Racer("front", 1, 0, 20), Racer("rear", 2, 0, -10)).state;
            Assert.Greater(w.Racer("front").foamSlowTicksRemaining, 0);
            Assert.AreEqual(0, w.Racer("owner").foamSlowTicksRemaining);
            Assert.AreEqual(0, w.Racer("rear").foamSlowTicksRemaining);
        }

        [Test]
        public void MineArmsThenResolvesOnlyOnce()
        {
            var w = Step(World("mine"), Racer("owner", 0, 0, 0, true), Racer("front", 1, 20, 20)).state;
            Assert.AreEqual(1, w.patches.Count);
            Assert.Less(w.patches[0].z, 0f, "dropped behind vehicle");
            var x = w.patches[0].x; var z = w.patches[0].z;
            for (var i = 0; i < 17; i++)
                w = Step(w, Racer("owner", 0, x, z), Racer("front", 1, x, z)).state;
            Assert.AreEqual(0, w.Racer("front").foamSlowTicksRemaining, "arming grace");
            w = Step(w, Racer("owner", 0, x, z), Racer("front", 1, x, z), Racer("rear", 2, x, z)).state;
            Assert.Greater(w.Racer("front").foamSlowTicksRemaining, 0);
            Assert.AreEqual(0, w.Racer("rear").foamSlowTicksRemaining, "one rival per mine");
            Assert.AreEqual(0, w.patches.Count, "consumed on impact");
            Assert.AreEqual(0, w.Racer("owner").foamSlowTicksRemaining);
        }

        [Test]
        public void EmpHitsAllNearbyRivalsButNotOwnerOrDistantRacer()
        {
            var w = Step(World("emp-pulse"), Racer("owner", 0, 0, 0, true), Racer("front", 1, 3, 4), Racer("rear", 2, -6, 0)).state;
            Assert.Greater(w.Racer("front").foamSlowTicksRemaining, 0);
            Assert.Greater(w.Racer("rear").foamSlowTicksRemaining, 0);
            Assert.AreEqual(0, w.Racer("owner").foamSlowTicksRemaining);
            w = Step(World("emp-pulse"), Racer("owner", 0, 0, 0, true), Racer("front", 1, 13, 0)).state;
            Assert.AreEqual(0, w.Racer("front").foamSlowTicksRemaining);
        }

        [Test]
        public void EmpRespectsShieldsImmunityHeightRecoveryAndFinish()
        {
            var w = World("emp-pulse"); w.Racer("front").shieldCharges = 1;
            var result = Step(w, Racer("owner", 0, 0, 0, true), Racer("front", 1, 1, 0));
            Assert.AreEqual(0, result.state.Racer("front").shieldCharges);
            Assert.AreEqual(0, result.state.Racer("front").foamSlowTicksRemaining);
            Assert.IsTrue(result.events.Exists(e => e.kind == GadgetRules.EventShieldBlocked));
            w = World("emp-pulse"); w.Racer("front").foamImmunityTicksRemaining = 50;
            Assert.AreEqual(0, Step(w, Racer("owner", 0, 0, 0, true), Racer("front", 1, 1, 0)).state.Racer("front").foamSlowTicksRemaining);
            for (var mode = 0; mode < 3; mode++)
            {
                var rival = Racer("front", 1, 1, 0);
                rival.y = mode == 0 ? 8f : 0f; rival.recovering = mode == 1; rival.finished = mode == 2;
                Assert.AreEqual(0, Step(World("emp-pulse"), Racer("owner", 0, 0, 0, true), rival).state.Racer("front").foamSlowTicksRemaining);
            }
        }

        [Test]
        public void FinishedRacerCannotFire()
        {
            var owner = Racer("owner", 0, 0, 0, true); owner.finished = true;
            var result = Step(World("emp-pulse"), owner, Racer("front", 1, 1, 0));
            Assert.AreEqual("emp-pulse", result.state.Racer("owner").heldItemId);
            Assert.IsFalse(result.events.Exists(e => e.kind == GadgetRules.EventUsed));
        }

        [Test]
        public void WeaponStepDoesNotMutatePreviousWorld()
        {
            var previous = World("emp-pulse");
            Step(previous, Racer("owner", 0, 0, 0, true), Racer("front", 1, 1, 0));
            Assert.AreEqual("emp-pulse", previous.Racer("owner").heldItemId);
            Assert.AreEqual(0, previous.Racer("front").foamSlowTicksRemaining);
        }

        [Test]
        public void MissileLocksOnlyAheadAndTurnsWithoutRetargeting()
        {
            var w = Step(World("homing-missile"), Racer("owner", 0, 0, 0, true),
                Racer("rear", 2, 0, -4), Racer("front", 1, 10, 30)).state;
            Assert.AreEqual("front", w.projectiles[0].targetParticipantId);
            Assert.Greater(w.projectiles[0].heading, 0);
            Assert.LessOrEqual(w.projectiles[0].heading, .10001f, "bounded steering");
            var rival = Racer("front", 1, 10, 30); rival.recovering = true;
            w = Step(w, Racer("owner", 0, 0, 0), rival, Racer("rear", 2, 0, 20)).state;
            Assert.AreEqual("", w.projectiles[0].targetParticipantId);
            var heading = w.projectiles[0].heading;
            w = Step(w, Racer("owner", 0, 0, 0), Racer("front", 1, 10, 30)).state;
            Assert.AreEqual(heading, w.projectiles[0].heading);
        }

        [Test]
        public void MissileSweepsMovingRacersAtMatchingTimes()
        {
            var w = World("homing-missile");
            w = Step(w, Racer("owner", 0, 0, 0, true)).state;
            var p = w.projectiles[0]; p.x = 0; p.z = 0; p.heading = 0; p.speed = 300;
            var rival = Racer("front", 1, 10, 5);
            rival.segments[0] = new GadgetSegment { ax = -10, az = 5, bx = 10, bz = 5 };
            w = Step(w, Racer("owner", 0, 30, 30), rival).state;
            Assert.AreEqual(0, w.projectiles.Count);
            Assert.Greater(w.Racer("front").foamSlowTicksRemaining, 0);
        }

        [Test]
        public void MissileBlockedByShieldAndMineConsumedByShield()
        {
            foreach (var kind in new[] { "homing-missile", "mine" })
            {
                var w = World(kind); w.Racer("front").shieldCharges = 1;
                w = Step(w, Racer("owner", 0, 0, 0, true)).state;
                if (kind == "mine") w.patches[0].armingTicksRemaining = 0;
                var z = kind == "mine" ? -3 : 8;
                for (var i = 0; i < 8; i++)
                    w = Step(w, Racer("owner", 0, 0, 0), Racer("front", 1, 0, z)).state;
                Assert.AreEqual(0, w.Racer("front").shieldCharges, kind);
                Assert.AreEqual(0, w.Racer("front").foamSlowTicksRemaining, kind);
                Assert.AreEqual(0, w.projectiles.Count + w.patches.Count, kind);
            }
        }

        [Test]
        public void NewEffectsDeepCopyExpireAndStayBounded()
        {
            var w = Step(World("homing-missile"), Racer("owner", 0, 0, 0, true)).state;
            var copy = w.Copy(); copy.projectiles[0].x = 99;
            Assert.AreNotEqual(99, w.projectiles[0].x);
            for (var i = 0; i < 92; i++) w = Step(w, Racer("owner", 0, 0, 0)).state;
            Assert.AreEqual(0, w.projectiles.Count);
            for (var i = 0; i < 25; i++)
            {
                w.Racer("owner").heldItemId = "homing-missile";
                w.Racer("owner").heldKind = "homing-missile";
                w = Step(w, Racer("owner", 0, 0, 0)).state;
                w = Step(w, Racer("owner", 0, 0, 0, true)).state;
                Assert.LessOrEqual(w.projectiles.Count, FamilyRacing.Core.Networking.NetworkSettings.GadgetProjectileCapacity);
            }
            Assert.IsTrue(w.ValidateWire(out var error), error);
            w.projectiles[0].speed = float.NaN;
            Assert.IsFalse(w.ValidateWire(out error));
        }

        [Test]
        public void UnknownWeaponOwnerCannotEnterSnapshot()
        {
            var w = Step(World("homing-missile"), Racer("owner", 0, 0, 0, true)).state;
            w.projectiles[0].ownerParticipantId = "missing";
            Assert.IsFalse(w.ValidateWire(out _));
        }

        [Test]
        public void ImmuneRivalConsumesMineWithoutAnotherSlow()
        {
            var w = Step(World("mine"), Racer("owner", 0, 0, 0, true)).state;
            w.patches[0].armingTicksRemaining = 0;
            w.Racer("front").foamImmunityTicksRemaining = 20;
            w = Step(w, Racer("owner", 0, 0, 0), Racer("front", 1, 0, -3)).state;
            Assert.AreEqual(0, w.patches.Count);
            Assert.AreEqual(0, w.Racer("front").foamSlowTicksRemaining);
        }

        [Test]
        public void HeadingRotatesLaunchAndMineDrop()
        {
            var owner = Racer("owner", 0, 0, 0, true); owner.heading = (float)System.Math.PI / 2f;
            var missileWorld = Step(World("homing-missile"), owner, Racer("front", 1, 25, 0)).state;
            Assert.AreEqual("front", missileWorld.projectiles[0].targetParticipantId);
            Assert.Greater(missileWorld.projectiles[0].x, 3);
            Assert.AreEqual(0, missileWorld.projectiles[0].z, .001);
            var mineWorld = Step(World("mine"), owner).state;
            Assert.AreEqual(-3, mineWorld.patches[0].x, .001);
            Assert.AreEqual(0, mineWorld.patches[0].z, .001);
        }

        [Test]
        public void MissileHitsDescendingRacerWhenHeightBandsOverlapLaterInTick()
        {
            var w = Step(World("homing-missile"), Racer("owner", 0, 0, 0, true)).state;
            w.projectiles[0].x = 0; w.projectiles[0].z = 0;
            var rival = Racer("front", 1, 0, 1); rival.y = 2.4f;
            rival.segments[0] = new GadgetSegment { ax = 0, ay = 2.6f, az = 1, bx = 0, by = 2.4f, bz = 1 };
            w = Step(w, Racer("owner", 0, 20, 20), rival).state;
            Assert.Greater(w.Racer("front").foamSlowTicksRemaining, 0);
            Assert.AreEqual(0, w.projectiles.Count);
        }

        [Test]
        public void MineSnapshotRequiresAnExistingOwner()
        {
            var w = Step(World("mine"), Racer("owner", 0, 0, 0, true)).state;
            w.patches[0].ownerParticipantId = "missing";
            Assert.IsFalse(w.ValidateWire(out _));
        }

        [Test]
        public void MineHitsFirstCrossingRacerRegardlessOfSlotOrder()
        {
            var w = Step(World("mine"), Racer("owner", 0, 0, 3, true)).state;
            w.patches[0].armingTicksRemaining = 0;
            var later = Racer("front", 1, 0, -2);
            later.segments[0] = new GadgetSegment { az = -3, bz = -2 };
            var earlier = Racer("rear", 2, 0, -1.7f);
            earlier.segments[0] = new GadgetSegment { az = -2.7f, bz = -1.7f };
            w = Step(w, Racer("owner", 0, 20, 20), later, earlier).state;
            Assert.AreEqual(0, w.Racer("front").foamSlowTicksRemaining);
            Assert.Greater(w.Racer("rear").foamSlowTicksRemaining, 0);
            Assert.AreEqual(0, w.patches.Count);
        }
    }
}
