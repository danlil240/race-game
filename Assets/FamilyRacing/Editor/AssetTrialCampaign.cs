using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // A small authored campaign on the existing course, scoped to the art-trial
    // build. It is not the planned multi-world production adventure.
    public static class AssetTrialCampaign
    {
        private const string Dir = AssetTrialAuthoring.Output + "/Campaign";

        public static void Bind(SerializedObject world)
        {
            Directory.CreateDirectory(Dir);
            AssetDatabase.Refresh();
            var events = new List<EventDefinitionAsset>();
            for (int place = 0; place < 3; place++)
            {
                var track = AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(AssetTrialCircuits.TrackPath(place));
                if (track == null) throw new InvalidOperationException("Trial campaign needs its driving course");
                foreach (var classId in new[] { "car", "motorcycle" })
                {
                    events.Add(Event(track, classId, GameplayEventType.Circuit, 1, place));
                    events.Add(Event(track, classId, GameplayEventType.Time, 2, place));
                    if (place == 0) events.Add(Event(track, classId, GameplayEventType.Finale, 2, place));
                }
            }
            // One championship owns all three locations. Existing rules make
            // every regular race available, so no place is hidden behind a
            // previous-world finale just to try a different track.
            var championship = ScriptableObject.CreateInstance<CampaignDefinitionAsset>();
            championship.worldId = "sunny-island-trial";
            championship.nameEn = "Race Planet";
            championship.nameHe = "כוכב המרוצים";
            championship.courseIds = AssetTrialCircuits.Ids;
            championship.regularEventIds = events.Where(e => !e.isFinale).Select(e => e.eventId).ToArray();
            championship.requiredEventCompletions = championship.regularEventIds.Length;
            championship.requiredRegionalStars = 18;
            championship.finales = new[] { "car", "motorcycle" }.Select(c => new CampaignDefinitionAsset.FinaleRow
            {
                eventId = Id(c, GameplayEventType.Finale, 0), classId = c,
                rivalId = "trial-rival-0-" + c, rewardVehicleId = c == "car" ? "car-01" : "motorcycle-01"
            }).ToArray();
            championship = Save(championship, "sunny-island-starter");
            var economy = ScriptableObject.CreateInstance<EconomyDefinitionAsset>();
            economy.upgradeCategories = new[]
            {
                Category("acceleration", "acceleration"),
                Category("handling", "steeringRate", "grip"),
                Category("boost", "boostAcceleration", "boostMaxSpeed")
            };
            economy = Save(economy, "starter-economy");
            var bounties = ScriptableObject.CreateInstance<BountyCatalogAsset>();
            bounties.bounties = new[]
            {
                Bounty("finish-three", "finish", BountyMetric.HumanFinishes, 3, "count", 10),
                Bounty("boost-three", "boost", BountyMetric.HumanBoostActivations, 3, "count", 10),
                Bounty("clean-ten", "clean-driving", BountyMetric.CleanStreakSeconds, 10, "seconds", 10)
            };
            bounties = Save(bounties, "starter-bounties");
            var catalog = ScriptableObject.CreateInstance<GameplayCatalogAsset>();
            catalog.campaigns = new[] { championship };
            catalog.economy = economy;
            catalog.bounties = bounties;
            catalog.gadgets = AssetDatabase.LoadAssetAtPath<GadgetCatalogAsset>("Assets/FamilyRacing/Generated/Driving/gadget-catalog-01.asset");
            catalog.difficulties = new[]
            {
                Difficulty("easy", 1.3f, 1f, .25f, .15f),
                Difficulty("medium", 1f, .9f, .5f, .5f),
                Difficulty("hard", .9f, .75f, .8f, .8f)
            };
            catalog = Save(catalog, "starter-gameplay");
            world.FindProperty("gameplayCatalog").objectReferenceValue = catalog;
            world.FindProperty("gadgetCatalog").objectReferenceValue = catalog.gadgets;
            var slots = world.FindProperty("events");
            // Rebinding remains idempotent even outside the Create-from-benchmark path.
            var prior = Enumerable.Range(0, slots.arraySize).Select(i => slots.GetArrayElementAtIndex(i).objectReferenceValue as EventDefinitionAsset)
                .Where(e => e != null && !e.eventId.StartsWith("trial-sunny-", StringComparison.Ordinal)
                    && !e.eventId.StartsWith("trial-pine-forest-", StringComparison.Ordinal)
                    && !e.eventId.StartsWith("trial-desert-canyon-", StringComparison.Ordinal)).ToList();
            prior.AddRange(events);
            slots.arraySize = prior.Count;
            for (int i = 0; i < prior.Count; i++) slots.GetArrayElementAtIndex(i).objectReferenceValue = prior[i];
        }

        private static string Id(string classId, string kind, int place) => (place == 0 ? "trial-sunny-" : "trial-" + AssetTrialCircuits.Ids[place] + "-") + classId + "-" + kind;

        private static EventDefinitionAsset Event(TrackDefinitionAsset track, string classId, string kind, int laps, int place)
        {
            var evt = Object.Instantiate(AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>("Assets/FamilyRacing/Generated/Driving/intro-01.asset"));
            evt.eventId = Id(classId, kind, place);
            evt.name = evt.eventId;
            evt.track = track; evt.laps = laps;
            evt.vehicleIds = new[] { classId == "car" ? "car-01" : "motorcycle-01" };
            evt.eventType = kind; evt.vehicleClass = classId;
            evt.isFinale = kind == GameplayEventType.Finale;
            evt.completionCoins = 20; // Awarded once through the persisted campaign result.
            evt.parTicks = kind == GameplayEventType.Time ? 2700 : 0;
            evt.objectives = evt.isFinale ? Array.Empty<EventDefinitionAsset.ObjectiveRow>() : new[]
            {
                Objective(evt.eventId + "-intro", "introductory", kind == GameplayEventType.Time ? ObjectiveMetric.FinishWithinTicks : ObjectiveMetric.FinishPlace,
                    kind == GameplayEventType.Time ? 4050 : 3),
                Objective(evt.eventId + "-mastery", "mastery", kind == GameplayEventType.Time ? ObjectiveMetric.FinishWithinTicks : ObjectiveMetric.FinishPlace,
                    kind == GameplayEventType.Time ? 2700 : 1)
            };
            return Save(evt, evt.eventId);
        }

        private static EventDefinitionAsset.ObjectiveRow Objective(string id, string kind, string metric, long limit) => new EventDefinitionAsset.ObjectiveRow
        {
            objectiveId = id, kind = kind,
            alternatives = new[] { new EventDefinitionAsset.ObjectiveConditionRow { metric = metric, comparator = "atMost", threshold = limit } }
        };

        private static EconomyDefinitionAsset.CategoryRow Category(string id, params string[] fields) => new EconomyDefinitionAsset.CategoryRow
        { categoryId = id, tuningFields = fields, prices = new[] { 60, 120, 180 }, maxLevel = 3, levelMultiplierStep = .05f };

        private static BountyCatalogAsset.BountyRow Bounty(string id, string family, string metric, int threshold, string unit, int coins) => new BountyCatalogAsset.BountyRow
        { bountyId = "trial-" + id, family = family, metric = metric, threshold = threshold, unit = unit, rewardCoins = coins };

        private static GameplayCatalogAsset.DifficultyRow Difficulty(string id, float brake, float boost, float gadget, float shortcut) => new GameplayCatalogAsset.DifficultyRow
        { difficultyId = id, brakeMarginScale = brake, boostUseMinCharge = boost, gadgetUseWillingness = gadget, shortcutWillingness = shortcut };

        private static T Save<T>(T value, string name) where T : Object
        {
            var path = Dir + "/" + name + ".asset";
            var prior = AssetDatabase.LoadAssetAtPath<T>(path);
            if (prior == null) { AssetDatabase.CreateAsset(value, path); return value; }
            EditorUtility.CopySerialized(value, prior); Object.DestroyImmediate(value);
            EditorUtility.SetDirty(prior); return prior;
        }

        public static void Validate(SerializedObject world)
        {
            var asset = world.FindProperty("gameplayCatalog").objectReferenceValue as GameplayCatalogAsset;
            if (asset == null) throw new InvalidOperationException("Trial Campaign is unavailable: no gameplay catalog assigned");
            var vehicles = world.FindProperty("vehicles");
            var events = world.FindProperty("events");
            var tuning = Enumerable.Range(0, vehicles.arraySize).Select(i => ((VehicleDefinitionAsset)vehicles.GetArrayElementAtIndex(i).objectReferenceValue).ToTuning(out _));
            var defs = Enumerable.Range(0, events.arraySize).Select(i => ((EventDefinitionAsset)events.GetArrayElementAtIndex(i).objectReferenceValue).ToEvent(out _));
            var tracks = world.FindProperty("tracks");
            var courses = Enumerable.Range(0, tracks.arraySize).Select(i => ((TrackDefinitionAsset)tracks.GetArrayElementAtIndex(i).objectReferenceValue).ToCourse(out _));
            var driving = DrivingContentCatalog.Create(world.FindProperty("contentVersion").stringValue, tuning, courses, defs);
            var gameplay = asset.ToCatalog(driving, out var error);
            if (gameplay == null) throw new InvalidOperationException("Trial Campaign invalid: " + error);
            Debug.Log("Trial Campaign validated: 3 available places, 12 regular events, 2 gated finales, economy, gadgets and difficulty presets.");
        }
    }
}
