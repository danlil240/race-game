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
    // Production data authoring only. Course collision/geometry belongs to
    // ProductionCourseAuthoring; the scene author binds these asset references.
    public static class ProductionCampaignAuthoring
    {
        private const string Dir = FullContentSetup.ContentDir + "/Campaign";
        public const string CatalogPath = Dir + "/production-gameplay.asset";
        public static string VehiclePath(string id) => Dir + "/Vehicles/" + id + ".asset";
        public static string EventPath(string id) => Dir + "/Events/" + id + ".asset";
        private static readonly string[] English = { "Sunny Island", "Redrock Canyon", "Snowy Peaks" };
        private static readonly string[] Hebrew = { "אי השמש", "קניון הסלע האדום", "פסגות מושלגות" };

        [MenuItem("Family Racing/Author Production Campaign")]
        public static void Create()
        {
            // Fail before changing content if the separate course author has
            // not run. Course order is the fixed production contract.
            var tracks = Enumerable.Range(0, 6).Select(i =>
                Require<TrackDefinitionAsset>(ProductionCourseAuthoring.TrackPath(i))).ToArray();
            for (int i = 0; i < tracks.Length; i++)
                if (tracks[i].courseId != FullContentValidation.WorldCourses[i / 2][i % 2])
                    throw new InvalidDataException("Production campaign: unexpected track order at " + i);
            foreach (var folder in new[] { Dir, Dir + "/Vehicles", Dir + "/Events", Dir + "/Visuals" })
                Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            foreach (var classId in FullContentValidation.ClassIds)
                for (int tier = 0; tier < 4; tier++) AuthorVehicle(classId, tier);

            var worlds = new List<CampaignDefinitionAsset>();
            var bounties = new List<BountyCatalogAsset.BountyRow>
            {
                Bounty("finish-three", "finish", BountyMetric.HumanFinishes, 3, 15),
                Bounty("boost-four", "boost", BountyMetric.HumanBoostActivations, 4, 15),
                Bounty("shortcut-three", "shortcut", BountyMetric.AcceptedShortcuts, 3, 20),
                Bounty("clean-twenty", "clean-driving", BountyMetric.CleanStreakSeconds, 20, 20),
                Bounty("drift-twelve", "drift", BountyMetric.DriftSeconds, 12, 15),
                // Production roads are continuously grounded: route mastery
                // replaces the prototype's unattainable landing challenge.
                Bounty("shortcut-six", "shortcut", BountyMetric.AcceptedShortcuts, 6, 20)
            };
            bounties[3].aggregation = BountyAggregation.Max;
            for (int w = 0; w < 3; w++)
            {
                var world = ScriptableObject.CreateInstance<CampaignDefinitionAsset>();
                world.worldId = FullContentValidation.WorldIds[w];
                world.nameEn = English[w]; world.nameHe = Hebrew[w];
                world.courseIds = (string[])FullContentValidation.WorldCourses[w].Clone();
                var regular = new List<string>();
                for (int c = 0; c < 2; c++)
                    foreach (var classId in FullContentValidation.ClassIds)
                        foreach (var suffix in FullContentValidation.TypeSuffixes)
                            regular.Add(AuthorEvent(tracks[w * 2 + c], classId, suffix, w).eventId);
                world.regularEventIds = regular.ToArray();
                // All 24 finishes plus six extra skill stars. Introductory
                // stars alone comfortably meet the gate; mastery is optional.
                world.requiredEventCompletions = 24;
                world.requiredRegionalStars = 30;
                world.finales = FullContentValidation.ClassIds.Select(classId =>
                {
                    var rival = FullContentValidation.RivalId(world.worldId, classId);
                    var finale = AuthorEvent(tracks[w * 2 + 1], classId, "finale", w);
                    var bounty = Bounty("bounty-" + rival, "rival", BountyMetric.HumanFinishes, 1, 30);
                    bounty.repeatable = false; bounty.rivalId = rival;
                    bounties.Add(bounty);
                    // Core reward service derives livery-<rivalId> from this
                    // same one-time victory identity; no parallel cosmetic
                    // asset format is required by the current runtime.
                    return new CampaignDefinitionAsset.FinaleRow
                    {
                        eventId = finale.eventId, classId = classId, rivalId = rival,
                        rewardVehicleId = FullContentValidation.RewardVehicleId(w, classId)
                    };
                }).ToArray();
                worlds.Add(Save(world, Dir + "/" + world.worldId + ".asset"));
            }
            var economy = ScriptableObject.CreateInstance<EconomyDefinitionAsset>();
            economy.upgradeCategories = new[]
            {
                Category("acceleration", "acceleration"),
                Category("handling", "steeringRate", "grip"),
                Category("boost", "boostAcceleration", "boostMaxSpeed")
            };
            var bountyCatalog = ScriptableObject.CreateInstance<BountyCatalogAsset>();
            bountyCatalog.bounties = bounties.ToArray();
            var gadgets = Object.Instantiate(Require<GadgetCatalogAsset>("Assets/FamilyRacing/Generated/Driving/gadget-catalog-01.asset"));
            var catalog = ScriptableObject.CreateInstance<GameplayCatalogAsset>();
            catalog.campaigns = worlds.ToArray();
            catalog.economy = Save(economy, Dir + "/production-economy.asset");
            catalog.bounties = Save(bountyCatalog, Dir + "/production-bounties.asset");
            catalog.gadgets = Save(gadgets, Dir + "/production-gadgets.asset");
            catalog.difficulties = new[]
            {
                Difficulty("easy", 1.3f, 1f, .25f, .15f),
                Difficulty("medium", 1f, .9f, .5f, .5f),
                Difficulty("hard", .9f, .75f, .8f, .8f)
            };
            var prior = AssetDatabase.LoadAssetAtPath<GameplayCatalogAsset>(CatalogPath);
            if (prior != null) catalog.voicePrompts = prior.voicePrompts;
            Save(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            Debug.Log("Production campaign authored: 3 worlds, 8 vehicles, 72 skill events, 6 rival finales. Final bilingual voice clips remain a separate production gate.");
        }

        private static void AuthorVehicle(string classId, int tier)
        {
            var vehicle = Object.Instantiate(Require<VehicleDefinitionAsset>("Assets/FamilyRacing/Generated/Driving/" + classId + "-01.asset"));
            var id = classId + "-0" + (tier + 1);
            vehicle.vehicleId = id;
            vehicle.maxSpeed += tier * 1.2f;
            vehicle.acceleration += tier * .45f;
            vehicle.grip += tier * .2f;
            vehicle.boostMaxSpeed += tier * 1.2f;
            vehicle.boostAcceleration += tier * .6f;
            vehicle.bodyColor = Color.HSVToRGB((tier * .19f + (classId == "car" ? .02f : .5f)) % 1f, .7f, .95f);
            var sourceVisual = Require<VehicleVisualDefinitionAsset>("Assets/FamilyRacing/Art/VisualBenchmark/Vehicles/vehicle-visual-" + (classId == "car" ? "car" : "moto") + "-01.asset");
            var visual = Object.Instantiate(sourceVisual);
            visual.visualId = "production-" + id;
            var instance = Object.Instantiate(sourceVisual.prefab);
            instance.name = "Production-" + id;
            try
            {
                // Independently authored model variants retain the proven
                // seat, steering and wheel pivots. Chassis changes are within
                // the source collision envelope; no visual gets a collider.
                var transforms = instance.GetComponentsInChildren<Transform>(true);
                foreach (var part in transforms)
                {
                    if (classId == "car" && (part.name == "AccentHood" || part.name == "Nose" || part.name == "Tub"))
                        part.localScale = Vector3.Scale(part.localScale, new Vector3(1f - tier * .035f, 1f + tier * .12f, 1f - tier * .03f));
                    if (classId == "motorcycle" && (part.name == "Tank" || part.name == "Tail" || part.name == "PlateAccent"))
                        part.localScale = Vector3.Scale(part.localScale, new Vector3(1f + tier * .08f, 1f + tier * .08f, 1f - tier * .05f));
                }
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                material.color = vehicle.bodyColor;
                material = Save(material, Dir + "/Visuals/" + id + "-body.mat");
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                    if (renderer.name.Contains("Accent") || renderer.name == "Tank") renderer.sharedMaterial = material;
                // Tier-specific exhaust/bumper detailing makes the silhouette
                // readable even when a profile overrides the accent color.
                for (int bar = 0; bar <= tier; bar++)
                {
                    var detail = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    detail.name = "ModelDetail-" + bar;
                    detail.transform.SetParent(instance.transform, false);
                    detail.transform.localPosition = classId == "car"
                        ? new Vector3(-.45f + bar * .3f, .55f, -1.35f)
                        : new Vector3(.24f, .5f + bar * .12f, -.65f);
                    detail.transform.localScale = classId == "car" ? new Vector3(.12f, .15f, .2f) : new Vector3(.14f, .08f, .35f);
                    Object.DestroyImmediate(detail.GetComponent<Collider>());
                    detail.GetComponent<Renderer>().sharedMaterial = material;
                }
                foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                visual.prefab = PrefabUtility.SaveAsPrefabAsset(instance, Dir + "/Visuals/" + id + ".prefab");
            }
            finally { Object.DestroyImmediate(instance); }
            vehicle.visual = Save(visual, Dir + "/Visuals/" + id + "-visual.asset");
            Save(vehicle, VehiclePath(id));
        }

        private static EventDefinitionAsset AuthorEvent(TrackDefinitionAsset track, string classId, string suffix, int world)
        {
            var evt = ScriptableObject.CreateInstance<EventDefinitionAsset>();
            evt.eventId = suffix == "finale" ? FullContentValidation.FinaleId(FullContentValidation.WorldIds[world], classId)
                : FullContentValidation.RegularEventId(track.courseId, classId, suffix);
            evt.track = track; evt.vehicleClass = classId;
            evt.eventType = FullContentValidation.EventTypeOfSuffix(suffix);
            evt.isFinale = suffix == "finale";
            evt.laps = suffix == "time" ? 1 : evt.isFinale ? 3 : 2;
            evt.vehicleIds = Enumerable.Range(1, 4).Select(i => classId + "-0" + i).ToArray();
            evt.completionCoins = 20;
            if (suffix == "time") evt.parTicks = ParTicks(track, classId);
            evt.objectives = evt.isFinale ? Array.Empty<EventDefinitionAsset.ObjectiveRow>() : Objectives(evt);
            return Save(evt, EventPath(evt.eventId));
        }

        private static int ParTicks(TrackDefinitionAsset track, string classId)
        {
            var route = track.aiRoute;
            float length = 0f;
            for (int i = 0; i < route.Length; i++)
            {
                var next = route[(i + 1) % route.Length];
                length += Vector2.Distance(new Vector2(route[i].x, route[i].z), new Vector2(next.x, next.z));
            }
            // Starter vehicle at 42% top speed, plus 20 seconds for launch
            // and corners. No purchased upgrade or future reward is needed.
            return Mathf.Max(1800, Mathf.CeilToInt(length / ((classId == "car" ? 18f : 20f) * .42f) * 30f) + 600);
        }

        private static EventDefinitionAsset.ObjectiveRow[] Objectives(EventDefinitionAsset evt)
        {
            string metric; long intro, mastery; var comparator = ObjectiveComparator.AtLeast;
            switch (evt.eventType)
            {
                case "circuit": metric = ObjectiveMetric.FinishPlace; intro = 3; mastery = 1; comparator = ObjectiveComparator.AtMost; break;
                case "boost": metric = ObjectiveMetric.HumanBoostActivations; intro = 1; mastery = 3; break;
                case "stunt": metric = ObjectiveMetric.DistinctShortcuts; intro = 1; mastery = 2; break;
                case "time": metric = ObjectiveMetric.FinishWithinTicks; intro = evt.parTicks * 3L / 2; mastery = evt.parTicks; comparator = ObjectiveComparator.AtMost; break;
                case "gadget": metric = ObjectiveMetric.GadgetsCollectedAndUsed; intro = 1; mastery = 2; break;
                default: metric = ObjectiveMetric.CleanStreakSeconds; intro = 8; mastery = 20; break;
            }
            var rows = new[]
            {
                Objective(evt.eventId + "-intro", ObjectiveKind.Introductory, Condition(metric, intro, comparator)),
                Objective(evt.eventId + "-mastery", ObjectiveKind.Mastery, Condition(metric, mastery, comparator))
            };
            if (evt.eventType == "circuit") rows[1].alternatives = new[] { rows[1].alternatives[0], Condition(ObjectiveMetric.DistinctShortcuts, 2) };
            if (evt.eventType == GameplayEventType.CleanDriving)
                rows[1].alternatives[0].alsoRequires = new[] { new EventDefinitionAsset.ObjectiveConditionLeaf
                { metric = ObjectiveMetric.Recoveries, comparator = ObjectiveComparator.AtMost, threshold = 0 } };
            return rows;
        }

        private static EventDefinitionAsset.ObjectiveConditionRow Condition(string metric, long threshold, string comparator = "atLeast") =>
            new EventDefinitionAsset.ObjectiveConditionRow { metric = metric, threshold = threshold, comparator = comparator };
        private static EventDefinitionAsset.ObjectiveRow Objective(string id, string kind, params EventDefinitionAsset.ObjectiveConditionRow[] conditions) =>
            new EventDefinitionAsset.ObjectiveRow { objectiveId = id, kind = kind, alternatives = conditions };
        private static EconomyDefinitionAsset.CategoryRow Category(string id, params string[] fields) =>
            new EconomyDefinitionAsset.CategoryRow { categoryId = id, tuningFields = fields, prices = new[] { 60, 120, 180 }, maxLevel = 3, levelMultiplierStep = .05f };
        private static BountyCatalogAsset.BountyRow Bounty(string id, string family, string metric, long threshold, int coins) =>
            new BountyCatalogAsset.BountyRow { bountyId = id, family = family, metric = metric, threshold = threshold, rewardCoins = coins,
                unit = metric == BountyMetric.DriftSeconds || metric == BountyMetric.CleanStreakSeconds ? "seconds" : "count" };
        private static GameplayCatalogAsset.DifficultyRow Difficulty(string id, float brake, float boost, float gadget, float shortcut) =>
            new GameplayCatalogAsset.DifficultyRow { difficultyId = id, brakeMarginScale = brake, boostUseMinCharge = boost, gadgetUseWillingness = gadget, shortcutWillingness = shortcut };
        private static T Require<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path)
            ?? throw new InvalidDataException("Production campaign requires " + path);
        private static T Save<T>(T value, string path) where T : Object
        {
            value.name = Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(value, path); return value; }
            // Updating the existing object preserves both GUID and file id.
            EditorUtility.CopySerialized(value, existing);
            Object.DestroyImmediate(value);
            EditorUtility.SetDirty(existing);
            return existing;
        }
    }
}
