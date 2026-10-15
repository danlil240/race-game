using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;

namespace FamilyRacing.Core.Racing
{
    // Deterministic canonical fingerprint of a DrivingContentCatalog's
    // tuned data — the admission token a join.request carries alongside
    // the human-readable content version. Two catalogs fingerprint
    // identically iff every replay-relevant tuned value is bit-identical;
    // the authored version string, dictionary insertion order and
    // delivery/presentation metadata are deliberately excluded.
    //
    // Canonical form: a binary stream hashed with SHA-256. Strings are
    // UTF-8 with a length prefix, integers little-endian fixed-width,
    // booleans one byte, and floats their raw IEEE-754 bits (sub-ULP
    // deltas are detected and no locale or formatting rule can round a
    // tuned value). All NaN payloads canonicalize to one quiet NaN.
    public static class ContentFingerprint
    {
        public static string Compute(DrivingContentCatalog catalog)
        {
            if (catalog == null) return "";
            using (var stream = new MemoryStream(4096))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(1); // canonical schema epoch
                    WriteDrivingBody(writer, catalog);
                    writer.Flush();
                    return Hex(stream);
                }
            }
        }

        // The shared driving body — byte-identical to the pre-M5 layout
        // so legacy driving fingerprints stay bit-stable.
        private static void WriteDrivingBody(BinaryWriter writer,
            DrivingContentCatalog catalog)
        {
            writer.Write((int)catalog.SimulationBackend);
            WriteVehicles(writer, catalog.Vehicles);
            WriteCourses(writer, catalog.Courses);
            WriteEvents(writer, catalog.Events);
        }

        // M5 Task 2: canonical fingerprint of a GameplayCatalog. It is a
        // superset stream: the same driving body plus every new
        // gameplay-affecting value (event objectives/par/type/finale
        // flag, authored gadget pickup sites, campaign structure and
        // gate, bounties, economy, gadgets, AI difficulty presets). Null
        // sections (the explicit legacy catalog) hash as absent markers
        // so a legacy catalog still gets a stable, distinguishable token.
        // M6 Task 1 bumps the epoch to 3: the campaign section is now the
        // ordered list of every world (membership/order/gates/rewards),
        // and translated display names — cosmetic text — are no longer
        // hashed at all.
        // M6 Task 4 bumps the epoch to 4: authored shortcut lanes are
        // gameplay content (the shortcut metrics credit their ids), so
        // each course's zone set now hashes beside its pickup sites.
        public static string Compute(GameplayCatalog catalog)
        {
            if (catalog == null) return "";
            using (var stream = new MemoryStream(8192))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(4); // canonical schema epoch: multiworld + shortcut lanes
                    if (catalog.Driving == null)
                    {
                        writer.Write(false);
                    }
                    else
                    {
                        writer.Write(true);
                        WriteDrivingBody(writer, catalog.Driving);
                        WriteEventGameplay(writer, catalog.Driving.Events);
                        WritePickupSites(writer, catalog.Driving.Courses);
                        WriteShortcutZones(writer, catalog.Driving.Courses);
                    }
                    WriteCampaigns(writer, catalog.Campaigns);
                    WriteBounties(writer, catalog.Bounties);
                    WriteEconomy(writer, catalog.Economy);
                    WriteGadgets(writer, catalog.Gadgets);
                    WriteDifficulties(writer, catalog.Difficulties);
                    writer.Flush();
                    return Hex(stream);
                }
            }
        }

        private static string Hex(MemoryStream stream)
        {
            var hash = SHA256.Create()
                .ComputeHash(stream.GetBuffer(), 0, (int)stream.Length);
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                hex.Append(b.ToString("x2",
                    System.Globalization.CultureInfo.InvariantCulture));
            return hex.ToString();
        }

        private static void WriteVehicles(BinaryWriter writer,
            IReadOnlyDictionary<string, VehicleTuning> vehicles)
        {
            var ids = SortedKeys(vehicles);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var tuning = vehicles[id];
                Str(writer, id);
                Str(writer, tuning.vehicleId);
                F(writer, tuning.maxSpeed);
                F(writer, tuning.acceleration);
                F(writer, tuning.steeringRate);
                F(writer, tuning.steeringReferenceSpeed);
                F(writer, tuning.grip);
                F(writer, tuning.driftMinSpeed);
                F(writer, tuning.driftSteeringFactor);
                F(writer, tuning.driftLateralAcceleration);
                F(writer, tuning.driftChargePerSecond);
                F(writer, tuning.boostMaxSpeed);
                F(writer, tuning.boostAcceleration);
                F(writer, tuning.boostDurationSeconds);
                F(writer, tuning.boostDrainPerSecond);
                F(writer, tuning.boostMinCharge);
                F(writer, tuning.boostChargeSeconds);
                F(writer, tuning.cleanLandingCredit);
                F(writer, tuning.landingMinAirSeconds);
                F(writer, tuning.landingMinDrop);
                F(writer, tuning.driftSteeringThreshold);
                F(writer, tuning.gravity);
                F(writer, tuning.terminalFallSpeed);
                F(writer, tuning.groundProbe);
                F(writer, tuning.rideHeight);
                F(writer, tuning.halfWidth);
                F(writer, tuning.halfLength);
                F(writer, tuning.height);
                F(writer, tuning.wallSpeedRetention);
                writer.Write((int)tuning.simulationBackend);
                WritePhysicsTuning(writer, tuning.physics);
            }
        }

        private static void WritePhysicsTuning(BinaryWriter writer,
            VehiclePhysicsTuning tuning)
        {
            writer.Write(tuning != null);
            if (tuning == null) return;
            F(writer, tuning.massKg);
            F(writer, tuning.comOffsetX);
            F(writer, tuning.comOffsetY);
            F(writer, tuning.comOffsetZ);
            F(writer, tuning.chassisLength);
            F(writer, tuning.chassisWidth);
            F(writer, tuning.chassisHeight);
            F(writer, tuning.inertiaMultiplierX);
            F(writer, tuning.inertiaMultiplierY);
            F(writer, tuning.inertiaMultiplierZ);
            var wheels = tuning.wheels;
            writer.Write(wheels != null ? wheels.Length : 0);
            if (wheels != null)
            {
                foreach (var wheel in wheels)
                {
                    var present = wheel != null;
                    writer.Write(present);
                    if (!present) continue;
                    F(writer, wheel.mountX);
                    F(writer, wheel.mountY);
                    F(writer, wheel.mountZ);
                    F(writer, wheel.radius);
                    writer.Write(wheel.steerable);
                    writer.Write(wheel.driven);
                }
            }
            F(writer, tuning.suspensionRestLength);
            F(writer, tuning.suspensionExtraDroop);
            F(writer, tuning.springRatePerWheel);
            F(writer, tuning.damperPerWheel);
            F(writer, tuning.maxNormalLoadPerWheel);
            F(writer, tuning.frictionCoefficient);
            F(writer, tuning.peakDriveForce);
            F(writer, tuning.peakBrakeForce);
            F(writer, tuning.cruiseSpeed);
            F(writer, tuning.boostSpeed);
            F(writer, tuning.dragCoefficient);
            F(writer, tuning.rollingResistance);
            F(writer, tuning.steerMaxRadians);
            F(writer, tuning.steerHighSpeedRadians);
            F(writer, tuning.steerReferenceSpeed);
            F(writer, tuning.steerRateRadiansPerSecond);
            F(writer, tuning.antiRollRate);
            F(writer, tuning.rollDamping);
            F(writer, tuning.assistMaxLeanRadians);
            F(writer, tuning.assistFallAngleRadians);
            F(writer, tuning.balanceGain);
            F(writer, tuning.balanceDamping);
        }

        private static void WriteCourses(BinaryWriter writer,
            IReadOnlyDictionary<string, CourseDefinition> courses)
        {
            var ids = SortedKeys(courses);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var course = courses[id];
                Str(writer, id);
                Str(writer, course.id);
                F(writer, course.spawnX);
                F(writer, course.spawnY);
                F(writer, course.spawnZ);
                F(writer, course.spawnHeading);
                var gates = course.checkpoints;
                writer.Write(gates != null ? gates.Count : 0);
                if (gates != null)
                {
                    foreach (var gate in gates)
                    {
                        var present = gate != null;
                        writer.Write(present);
                        if (!present) continue;
                        Str(writer, gate.id);
                        F(writer, gate.x);
                        F(writer, gate.y);
                        F(writer, gate.z);
                        F(writer, gate.halfWidth);
                        F(writer, gate.dirX);
                        F(writer, gate.dirZ);
                        F(writer, gate.recoverX);
                        F(writer, gate.recoverY);
                        F(writer, gate.recoverZ);
                        F(writer, gate.recoverHeading);
                    }
                }
                WritePoses(writer, course.spawnSlots);
                var route = course.route;
                writer.Write(route != null ? route.Count : 0);
                if (route != null)
                {
                    foreach (var waypoint in route)
                    {
                        var present = waypoint != null;
                        writer.Write(present);
                        if (!present) continue;
                        F(writer, waypoint.x);
                        F(writer, waypoint.y);
                        F(writer, waypoint.z);
                        F(writer, waypoint.radius);
                    }
                }
                var bounds = course.recoveryBounds;
                writer.Write(bounds != null ? bounds.Count : 0);
                if (bounds != null)
                {
                    foreach (var volume in bounds)
                    {
                        var present = volume != null;
                        writer.Write(present);
                        if (!present) continue;
                        F(writer, volume.minX);
                        F(writer, volume.minY);
                        F(writer, volume.minZ);
                        F(writer, volume.maxX);
                        F(writer, volume.maxY);
                        F(writer, volume.maxZ);
                    }
                }
                WritePoses(writer, course.safePoses);
            }
        }

        private static void WriteEvents(BinaryWriter writer,
            IReadOnlyDictionary<string, EventDefinition> events)
        {
            var ids = SortedKeys(events);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var evt = events[id];
                Str(writer, id);
                Str(writer, evt.id);
                Str(writer, evt.courseId);
                writer.Write(evt.laps);
                writer.Write(evt.countdownTicks);
                var vehicles = evt.vehicleIds;
                writer.Write(vehicles != null ? vehicles.Count : 0);
                if (vehicles != null)
                    foreach (var vehicleId in vehicles)
                        Str(writer, vehicleId);
                var recovery = evt.recovery;
                writer.Write(recovery != null);
                if (recovery != null)
                {
                    F(writer, recovery.offCourseSeconds);
                    F(writer, recovery.minCourseY);
                    F(writer, recovery.stuckSpeed);
                    F(writer, recovery.stuckSeconds);
                    F(writer, recovery.beachedSeconds);
                    F(writer, recovery.overturnedSeconds);
                    F(writer, recovery.outsideBoundsSeconds);
                }
                writer.Write(evt.completionCoins);
            }
        }

        // --- M5 gameplay sections ----------------------------------------

        // The gameplay fields added to EventDefinition: event type,
        // vehicle class, par ticks, finale flag and the objective pair.
        // Written separately from WriteEvents so the pre-M5 driving
        // fingerprint stays byte-identical.
        private static void WriteEventGameplay(BinaryWriter writer,
            IReadOnlyDictionary<string, EventDefinition> events)
        {
            var ids = SortedKeys(events);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var evt = events[id];
                Str(writer, id);
                Str(writer, evt.eventType);
                Str(writer, evt.vehicleClass);
                writer.Write(evt.parTicks);
                writer.Write(evt.isFinale);
                var objectives = evt.objectives;
                writer.Write(objectives != null ? objectives.Count : 0);
                if (objectives != null)
                    foreach (var objective in objectives)
                    {
                        var present = objective != null;
                        writer.Write(present);
                        if (!present) continue;
                        Str(writer, objective.id);
                        Str(writer, objective.kind);
                        WriteConditions(writer, objective.alternatives);
                    }
            }
        }

        private static void WriteConditions(BinaryWriter writer,
            List<ObjectiveCondition> conditions)
        {
            writer.Write(conditions != null ? conditions.Count : 0);
            if (conditions == null) return;
            foreach (var condition in conditions)
            {
                var present = condition != null;
                writer.Write(present);
                if (!present) continue;
                Str(writer, condition.metric);
                Str(writer, condition.comparator);
                writer.Write(condition.threshold);
                WriteConditions(writer, condition.alsoRequires);
            }
        }

        // Authored gadget pickup sites (M5 Task 5) live on the course
        // definition but drive only the authoritative gadget rules —
        // they hash in the epoch-2 gameplay stream, never the epoch-1
        // driving body. Each course key binds its site set (a site moved
        // between courses changes the fingerprint) and the sites write
        // canonically ordered by site id, so serialized list order is
        // presentation, never content.
        private static void WritePickupSites(BinaryWriter writer,
            IReadOnlyDictionary<string, CourseDefinition> courses)
        {
            var ids = SortedKeys(courses);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                Str(writer, id);
                var sites = new List<GadgetPickupSite>();
                var authored = courses[id].pickupSites;
                if (authored != null) sites.AddRange(authored);
                sites.Sort((a, b) => string.CompareOrdinal(
                    a != null ? a.id : null,
                    b != null ? b.id : null));
                writer.Write(sites.Count);
                foreach (var site in sites)
                {
                    var present = site != null;
                    writer.Write(present);
                    if (!present) continue;
                    Str(writer, site.id);
                    F(writer, site.x);
                    F(writer, site.y);
                    F(writer, site.z);
                    F(writer, site.radiusMeters);
                    F(writer, site.verticalRangeMeters);
                }
            }
        }

        // Authored optional shortcut lanes (M6 Task 4): like pickup
        // sites they live on the course definition and hash only in the
        // gameplay stream — the epoch-1 driving body stays bit-stable.
        // Zones write canonically ordered by id per course.
        private static void WriteShortcutZones(BinaryWriter writer,
            IReadOnlyDictionary<string, CourseDefinition> courses)
        {
            var ids = SortedKeys(courses);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                Str(writer, id);
                var zones = new List<ShortcutZone>();
                var authored = courses[id].shortcutZones;
                if (authored != null) zones.AddRange(authored);
                zones.Sort((a, b) => string.CompareOrdinal(
                    a != null ? a.id : null,
                    b != null ? b.id : null));
                writer.Write(zones.Count);
                foreach (var zone in zones)
                {
                    var present = zone != null;
                    writer.Write(present);
                    if (!present) continue;
                    Str(writer, zone.id);
                    F(writer, zone.x);
                    F(writer, zone.y);
                    F(writer, zone.z);
                    F(writer, zone.halfWidth);
                    F(writer, zone.dirX);
                    F(writer, zone.dirZ);
                }
            }
        }

        // M6 Task 1: every world in authored order. Membership lists
        // keep their authored order — world progression order and event
        // order are gameplay content. Display names are deliberately
        // excluded: they are translated/cosmetic text, and retitling a
        // world must never split compatibility.
        private static void WriteCampaigns(BinaryWriter writer,
            IReadOnlyList<CampaignDefinition> campaigns)
        {
            writer.Write(campaigns != null);
            if (campaigns == null) return;
            writer.Write(campaigns.Count);
            foreach (var campaign in campaigns)
            {
                var present = campaign != null;
                writer.Write(present);
                if (!present) continue;
                Str(writer, campaign.worldId);
                writer.Write(campaign.requiredEventCompletions);
                writer.Write(campaign.requiredRegionalStars);
                WriteStrings(writer, campaign.courseIds);
                WriteStrings(writer, campaign.regularEventIds);
                var finales = campaign.finales;
                writer.Write(finales != null ? finales.Count : 0);
                if (finales != null)
                    foreach (var finale in finales)
                    {
                        var finalePresent = finale != null;
                        writer.Write(finalePresent);
                        if (!finalePresent) continue;
                        Str(writer, finale.eventId);
                        Str(writer, finale.classId);
                        Str(writer, finale.rivalId);
                        Str(writer, finale.rewardVehicleId);
                    }
            }
        }

        private static void WriteBounties(BinaryWriter writer,
            IReadOnlyList<BountyDefinition> bounties)
        {
            writer.Write(bounties != null);
            if (bounties == null) return;
            writer.Write(bounties.Count);
            foreach (var bounty in bounties)
            {
                var present = bounty != null;
                writer.Write(present);
                if (!present) continue;
                Str(writer, bounty.id);
                Str(writer, bounty.family);
                Str(writer, bounty.metric);
                writer.Write(bounty.threshold);
                Str(writer, bounty.unit);
                writer.Write(bounty.rewardCoins);
                Str(writer, bounty.aggregation);
                writer.Write(bounty.repeatable);
                Str(writer, bounty.rivalId);
            }
        }

        private static void WriteEconomy(BinaryWriter writer,
            EconomyDefinition economy)
        {
            writer.Write(economy != null);
            if (economy == null) return;
            writer.Write(economy.completionCoins);
            writer.Write(economy.challengeIntroductoryCoins);
            writer.Write(economy.challengeMasteryCoins);
            writer.Write(economy.rivalVictoryCoins);
            var bonus = economy.placementBonusCoins;
            writer.Write(bonus != null ? bonus.Count : 0);
            if (bonus != null)
                foreach (var coins in bonus) writer.Write(coins);
            var categories = economy.upgradeCategories;
            writer.Write(categories != null ? categories.Count : 0);
            if (categories != null)
                foreach (var category in categories)
                {
                    var present = category != null;
                    writer.Write(present);
                    if (!present) continue;
                    Str(writer, category.id);
                    writer.Write(category.maxLevel);
                    var prices = category.prices;
                    writer.Write(prices != null ? prices.Count : 0);
                    if (prices != null)
                        foreach (var price in prices) writer.Write(price);
                    F(writer, category.levelMultiplierStep);
                    WriteStrings(writer, category.tuningFields);
                }
        }

        private static void WriteGadgets(BinaryWriter writer,
            GadgetDefinitionCatalog gadgets)
        {
            writer.Write(gadgets != null);
            if (gadgets == null) return;
            writer.Write(gadgets.pickupRespawnTicks);
            writer.Write(gadgets.heldSlotCapacity);
            var items = gadgets.gadgets;
            writer.Write(items != null ? items.Count : 0);
            if (items != null)
                foreach (var gadget in items)
                {
                    var present = gadget != null;
                    writer.Write(present);
                    if (!present) continue;
                    Str(writer, gadget.id);
                    Str(writer, gadget.kind);
                    writer.Write(gadget.durationTicks);
                    F(writer, gadget.accelerationMultiplier);
                    F(writer, gadget.speedCapMultiplier);
                    writer.Write(gadget.charges);
                    F(writer, gadget.patchRadiusMeters);
                    writer.Write(gadget.patchLifetimeTicks);
                    F(writer, gadget.slowMultiplier);
                    writer.Write(gadget.slowDurationTicks);
                    writer.Write(gadget.immunityTicks);
                }
        }

        private static void WriteDifficulties(BinaryWriter writer,
            IReadOnlyList<AiDifficultyDefinition> difficulties)
        {
            writer.Write(difficulties != null);
            if (difficulties == null) return;
            writer.Write(difficulties.Count);
            foreach (var difficulty in difficulties)
            {
                var present = difficulty != null;
                writer.Write(present);
                if (!present) continue;
                Str(writer, difficulty.id);
                F(writer, difficulty.brakeMarginScale);
                F(writer, difficulty.boostUseMinCharge);
                F(writer, difficulty.gadgetUseWillingness);
                F(writer, difficulty.shortcutWillingness);
            }
        }

        private static void WriteStrings(BinaryWriter writer,
            List<string> values)
        {
            writer.Write(values != null ? values.Count : 0);
            if (values == null) return;
            foreach (var value in values) Str(writer, value);
        }

        private static void WritePoses(BinaryWriter writer,
            List<MotorPose> poses)
        {
            writer.Write(poses != null ? poses.Count : 0);
            if (poses == null) return;
            foreach (var pose in poses)
            {
                F(writer, pose.x);
                F(writer, pose.y);
                F(writer, pose.z);
                F(writer, pose.heading);
            }
        }

        private static List<string> SortedKeys<T>(
            IReadOnlyDictionary<string, T> map)
        {
            var keys = new List<string>();
            if (map != null) keys.AddRange(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static void Str(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        // Raw IEEE-754 little-endian bits, canonicalizing every NaN
        // payload to one quiet NaN so a tuned value fingerprints on its
        // meaning, not its bit representation accident.
        private static void F(BinaryWriter writer, float value)
        {
            var bits = float.IsNaN(value)
                ? BitConverter.SingleToInt32Bits(float.NaN)
                : BitConverter.SingleToInt32Bits(value);
            writer.Write(bits);
        }
    }
}
