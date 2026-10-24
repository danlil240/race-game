using System.Collections.Generic;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    public sealed class CombatGadgetAdapterTests
    {
        [Test]
        public void AuthoredWeaponParametersReachCore()
        {
            var asset = ScriptableObject.CreateInstance<GadgetCatalogAsset>();
            try
            {
                asset.gadgets = new[] { new GadgetCatalogAsset.GadgetRow {
                    gadgetId = "rocket", kind = GadgetKind.HomingMissile, patchRadiusMeters = 1.5f,
                    patchLifetimeTicks = 90, slowMultiplier = .4f, slowDurationTicks = 27,
                    projectileSpeedMetersPerSecond = 47, targetingRangeMeters = 75, turnRadiansPerSecond = 2,
                    armingTicks = 21
                }};
                var catalog = asset.ToCatalog(out var error);
                Assert.IsNotNull(catalog, error);
                Assert.AreEqual(47, catalog.Gadget("rocket").projectileSpeedMetersPerSecond);
                Assert.AreEqual(75, catalog.Gadget("rocket").targetingRangeMeters);
                Assert.AreEqual(2, catalog.Gadget("rocket").turnRadiansPerSecond);
                Assert.AreEqual(21, catalog.Gadget("rocket").armingTicks);
                asset.gadgets[0].projectileSpeedMetersPerSecond = -1;
                Assert.IsNull(asset.ToCatalog(out error));
            }
            finally { Object.DestroyImmediate(asset); }
        }

        private static GadgetWorldState World()
        {
            var world = GadgetRules.Create(new GadgetDefinitionCatalog(), null, 42, new[] { "owner", "rival" });
            world.projectiles.Add(new GadgetProjectileState { projectileId = "missile-1", ownerParticipantId = "owner",
                targetParticipantId = "rival", x = 1, y = 2, z = 3, heading = .4f, speed = 45, turnRate = 3,
                radiusMeters = 1.5f, ticksRemaining = 90, slowMultiplier = .4f, slowTicks = 27, immunityTicks = 45 });
            world.patches.Add(new GadgetPatchState { patchId = "mine-1", ownerParticipantId = "owner", kind = GadgetKind.Mine,
                armingTicksRemaining = 18, radiusMeters = 2.5f, ticksRemaining = 360, slowMultiplier = .35f, slowTicks = 36 });
            world.pulses.Add(new GadgetPulseState { pulseId = "emp-1", ownerParticipantId = "owner", radiusMeters = 12, totalTicks = 15, ticksRemaining = 15 });
            return world;
        }

        private static NetworkSnapshot Snapshot(GadgetWorldState world)
        {
            return new NetworkSnapshot { sessionId = "s", raceId = "r", raceOrdinal = 1, snapshotSequence = 1,
                state = new NetworkRaceState { sessionId = "s", raceId = "r", gadgets = world } };
        }

        [Test]
        public void AllWeaponStateSurvivesWireRoundTrip()
        {
            Assert.IsTrue(NetworkWireCodec.TryEncodeSnapshot(Snapshot(World()), out var bytes, out var error), error);
            Assert.IsTrue(NetworkWireCodec.TryDecodeSnapshot(bytes, out var decoded, out error), error);
            Assert.AreEqual("rival", decoded.state.gadgets.projectiles[0].targetParticipantId);
            Assert.AreEqual(.4f, decoded.state.gadgets.projectiles[0].heading);
            Assert.AreEqual(18, decoded.state.gadgets.patches[0].armingTicksRemaining);
            Assert.AreEqual(12, decoded.state.gadgets.pulses[0].radiusMeters);
        }

        [TestCase("negative-speed")]
        [TestCase("unknown-owner")]
        [TestCase("negative-arming")]
        [TestCase("duplicate-missile")]
        [TestCase("pulse-clock")]
        public void InvalidWeaponSnapshotRejectsAtomically(string corruption)
        {
            var world = World();
            if (corruption == "negative-speed") world.projectiles[0].speed = -1;
            if (corruption == "unknown-owner") world.projectiles[0].ownerParticipantId = "absent";
            if (corruption == "negative-arming") world.patches[0].armingTicksRemaining = -1;
            if (corruption == "duplicate-missile") world.projectiles.Add(world.projectiles[0].Copy());
            if (corruption == "pulse-clock") world.pulses[0].ticksRemaining = 16;
            Assert.IsTrue(NetworkWireCodec.TryEncodeSnapshot(Snapshot(world), out var bytes, out var error), error);
            Assert.IsFalse(NetworkWireCodec.TryDecodeSnapshot(bytes, out _, out error), corruption);
        }

        [Test]
        public void MaximumWeaponListsFitSnapshotBudget()
        {
            var w = World();
            for (var i = 1; i < NetworkSettings.GadgetProjectileCapacity; i++)
            { var p = w.projectiles[0].Copy(); p.projectileId = "missile-" + (i + 1); w.projectiles.Add(p); }
            for (var i = 1; i < NetworkSettings.GadgetPatchCapacity; i++)
            { var p = w.patches[0].Copy(); p.patchId = "mine-" + (i + 1); w.patches.Add(p); }
            for (var i = 1; i < NetworkSettings.GadgetPulseCapacity; i++)
            { var p = w.pulses[0].Copy(); p.pulseId = "emp-" + (i + 1); w.pulses.Add(p); }
            for (var i = 0; i < NetworkSettings.GadgetPickupCapacity; i++)
                w.pickups.Add(new GadgetPickupState { siteId = "pickup-" + i });
            Assert.IsTrue(NetworkWireCodec.TryEncodeSnapshot(Snapshot(w), out var bytes, out var error), error);
            Assert.Less(bytes.Length, NetworkSettings.SnapshotCeilingBytes - 6000, "reserve room for vehicle state");
            Assert.IsTrue(NetworkWireCodec.TryDecodeSnapshot(bytes, out _, out error), error);
        }
    }
}
