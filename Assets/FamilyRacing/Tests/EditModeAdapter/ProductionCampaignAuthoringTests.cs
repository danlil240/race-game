using System;
using System.Linq;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;

namespace FamilyRacing.Tests
{
    public sealed class ProductionCampaignAuthoringTests
    {
        [OneTimeSetUp]
        public void Author()
        {
            ProductionCourseAuthoring.Create();
            ProductionCampaignAuthoring.Create();
        }

        private static T[] Assets<T>() where T : UnityEngine.Object =>
            AssetDatabase.FindAssets("t:" + typeof(T).Name, new[] { FullContentSetup.ContentDir })
                .Select(g => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(g))).ToArray();

        private static DrivingContentCatalog Driving() => DrivingContentCatalog.Create(
            FullContentSetup.DrivingVersion,
            Assets<VehicleDefinitionAsset>().Select(a => a.ToTuning(out _)),
            Assets<TrackDefinitionAsset>().Select(a => a.ToCourse(out _)),
            Assets<EventDefinitionAsset>().Select(a => a.ToEvent(out _)));

        [Test]
        public void ExactProductionInventoryConvertsAndOnlyMissingVoiceCanBlockIt()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameplayCatalogAsset>(ProductionCampaignAuthoring.CatalogPath);
            var driving = Driving();
            Assert.That(asset.ToCatalog(driving, out var error), Is.Not.Null, error);
            Assert.That(driving.Events.Count, Is.EqualTo(78));
            Assert.That(driving.Vehicles.Keys, Is.EquivalentTo(FullContentValidation.VehicleIds));
            Assert.That(FullContentValidation.Validate(asset, driving).Where(r => !r.StartsWith("voice", StringComparison.Ordinal)), Is.Empty);
            var vehicles = Assets<VehicleDefinitionAsset>();
            Assert.That(vehicles.Select(v => v.visual.prefab).Distinct().Count(), Is.EqualTo(8));
            foreach (var vehicle in vehicles)
            {
                Assert.That(vehicle.visual.Validate(out error), Is.True, error);
                foreach (var mount in vehicle.visual.wheelNames.Concat(new[] { "SeatMount", "GripL", "GripR" }))
                    Assert.That(vehicle.visual.prefab.GetComponentsInChildren<UnityEngine.Transform>(true).Any(t => t.name == mount), Is.True, vehicle.vehicleId + " " + mount);
                Assert.That(vehicle.visual.prefab.GetComponentsInChildren<UnityEngine.Collider>(true), Is.Empty);
            }
        }

        [Test]
        public void ObjectivesUseTheirEventSkillsAndAreAttainableFromAuthoredInventory()
        {
            foreach (var evt in Assets<EventDefinitionAsset>().Where(e => !e.isFinale))
            {
                var definition = evt.ToEvent(out var error);
                Assert.That(definition, Is.Not.Null, error);
                var metrics = new RaceMetrics { position = 1, finishTicks = evt.parTicks > 0 ? evt.parTicks : 3000,
                    boostActivations = 3, humanDistinctShortcuts = 2,
                    gadgetUses = 2, distinctGadgetTypes = 2, longestCleanStreakTicks = 900, recoveries = 0 };
                var result = Result(definition.id, metrics);
                Assert.That(ObjectiveEvaluator.EvaluateMask(definition, result, "driver"), Is.EqualTo(ObjectiveEvaluator.FullMask), evt.eventId);
                var expected = evt.eventType == "circuit" ? ObjectiveMetric.FinishPlace
                    : evt.eventType == "boost" ? ObjectiveMetric.HumanBoostActivations
                    : evt.eventType == "stunt" ? ObjectiveMetric.DistinctShortcuts
                    : evt.eventType == "time" ? ObjectiveMetric.FinishWithinTicks
                    : evt.eventType == "gadget" ? ObjectiveMetric.GadgetsCollectedAndUsed : ObjectiveMetric.CleanStreakSeconds;
                Assert.That(definition.objectives[0].alternatives[0].metric, Is.EqualTo(expected), evt.eventId);
                if (evt.eventType == "stunt") Assert.That(evt.track.shortcutLanes.Length, Is.GreaterThanOrEqualTo(2));
                if (evt.eventType == "gadget") Assert.That(evt.track.pickupSites.Length, Is.GreaterThanOrEqualTo(1));
                Assert.That(ObjectiveEvaluator.EvaluateMask(definition, Result(definition.id, new RaceMetrics { position = 4, finishTicks = 999999 }), "driver"), Is.EqualTo(ObjectiveEvaluator.CompletionBit), evt.eventId + " idle completion must not award skill stars");
            }
        }

        [Test]
        public void RepeatableBountiesRequireSkillsSupportedByGroundedProductionRoutes()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameplayCatalogAsset>(ProductionCampaignAuthoring.CatalogPath);
            var bounties = asset.bounties.ToDefinitions(out var error);
            Assert.That(bounties, Is.Not.Null, error);
            var repeatable = bounties.Where(b => b.repeatable).ToArray();
            Assert.That(repeatable.Length, Is.EqualTo(6));
            // These tracks have continuous road surfaces and optional branch
            // ribbons, not landing ramps. Never rotate in an aerial task.
            Assert.That(repeatable.Select(b => b.metric), Does.Not.Contain(BountyMetric.CleanLandings));
            var supported = new[] { BountyMetric.HumanFinishes, BountyMetric.HumanBoostActivations,
                BountyMetric.AcceptedShortcuts, BountyMetric.CleanStreakSeconds, BountyMetric.DriftSeconds };
            foreach (var bounty in repeatable)
            {
                Assert.That(supported, Does.Contain(bounty.metric), bounty.id);
                Assert.That(bounty.threshold, Is.GreaterThan(0), bounty.id);
                // Finish/boost/drift/shortcut tasks accumulate across races;
                // the only best-streak task needs a 20 second clean drive.
                Assert.That(bounty.aggregation, Is.EqualTo(bounty.metric == BountyMetric.CleanStreakSeconds
                    ? BountyAggregation.Max : BountyAggregation.Sum), bounty.id);
                if (bounty.metric == BountyMetric.AcceptedShortcuts)
                    foreach (var track in Assets<TrackDefinitionAsset>())
                        Assert.That(track.shortcutLanes.Length, Is.GreaterThanOrEqualTo(2), track.courseId);
                if (bounty.metric == BountyMetric.CleanStreakSeconds)
                    Assert.That(bounty.threshold, Is.LessThanOrEqualTo(20), bounty.id);
            }
            Assert.That(repeatable.Single(b => b.id == "shortcut-six").threshold, Is.EqualTo(6));
        }

        [Test]
        public void CompletionAndIntroStarsReachEveryWorldWithoutFutureRewardVehicles()
        {
            var driving = Driving();
            var catalog = AssetDatabase.LoadAssetAtPath<GameplayCatalogAsset>(ProductionCampaignAuthoring.CatalogPath).ToCatalog(driving, out var error);
            Assert.That(catalog, Is.Not.Null, error);
            var profile = new ProfileSave { eventProgress = new System.Collections.Generic.List<EventProgress>(), finaleProgress = new System.Collections.Generic.List<FinaleProgress>() };
            for (int w = 0; w < 3; w++)
            {
                var world = catalog.Campaigns[w];
                Assert.That(AdventureRules.Evaluate(catalog, profile).worlds[w].available, Is.True);
                foreach (var id in world.regularEventIds)
                {
                    Assert.That(driving.Events[id].vehicleIds, Does.Contain(driving.Events[id].vehicleClass + "-01"));
                    profile.eventProgress.Add(new EventProgress { eventId = id, completed = true, objectiveMask = ObjectiveEvaluator.CompletionBit | ObjectiveEvaluator.IntroductoryBit });
                }
                Assert.That(CampaignRules.Evaluate(world, profile).finalesAvailable, Is.True);
                foreach (var finale in world.finales)
                {
                    Assert.That(finale.rivalId, Is.EqualTo(FullContentValidation.RivalId(world.worldId, finale.classId)));
                    Assert.That("livery-" + finale.rivalId, Is.EqualTo(FullContentValidation.LiveryId(world.worldId, finale.classId)));
                    Assert.That(finale.rewardVehicleId, Is.EqualTo(FullContentValidation.RewardVehicleId(w, finale.classId)));
                    Assert.That(catalog.Bounties.Count(b => !b.repeatable && b.rivalId == finale.rivalId), Is.EqualTo(1));
                    Assert.That(driving.Events[finale.eventId].vehicleIds, Does.Contain(finale.classId + "-01"));
                    profile.finaleProgress.Add(new FinaleProgress { eventId = finale.eventId, completed = true, victorious = false });
                }
            }
            Assert.That(AdventureRules.Evaluate(catalog, profile).complete, Is.True);
        }

        [Test]
        public void ReauthoringPreservesGuidsRewardIdentityAndRulesFingerprint()
        {
            var paths = AssetDatabase.FindAssets("", new[] { FullContentSetup.ContentDir }).Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p)).ToArray();
            var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            var asset = AssetDatabase.LoadAssetAtPath<GameplayCatalogAsset>(ProductionCampaignAuthoring.CatalogPath);
            var before = asset.ToCatalog(Driving(), out _).CompatibilityFingerprint;
            ProductionCampaignAuthoring.Create();
            Assert.That(paths.Select(AssetDatabase.AssetPathToGUID), Is.EqualTo(guids));
            Assert.That(asset.ToCatalog(Driving(), out _).CompatibilityFingerprint, Is.EqualTo(before));
            Assert.That(Assets<EventDefinitionAsset>().SelectMany(e => e.objectives).Select(o => o.objectiveId).Distinct().Count(), Is.EqualTo(144));
        }

        private static RaceResult Result(string id, RaceMetrics metrics) => new RaceResult
        {
            finalized = true, eventId = id, mode = "campaign",
            racers = new System.Collections.Generic.List<RaceResultEntry> { new RaceResultEntry
            { participantId = "driver", attribution = "human", outcome = "finished", metrics = metrics } }
        };
    }
}
