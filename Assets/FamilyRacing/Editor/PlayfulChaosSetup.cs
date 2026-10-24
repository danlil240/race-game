using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    public static class PlayfulChaosSetup
    {
        public const string ContentDir = "Assets/FamilyRacing/Generated/PlayfulChaos";
        public const string ScenePath = ContentDir + "/PlayfulChaos.unity";
        private const string DrivingDir = "Assets/FamilyRacing/Generated/Driving";

        public static void Create()
        {
            FoundationProjectSetup.Create();
            VisualBenchmarkSetup.Create();
            if (!AssetDatabase.IsValidFolder(ContentDir))
                AssetDatabase.CreateFolder("Assets/FamilyRacing/Generated", "PlayfulChaos");
            if (!File.Exists(ScenePath))
                AssetDatabase.CopyAsset(VisualBenchmarkSetup.ScenePath, ScenePath);
            var scene = EditorSceneManager.OpenScene(ScenePath);
            foreach (var root in scene.GetRootGameObjects())
                foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                    if (camera.CompareTag("MainCamera") && camera.GetComponent<AudioListener>() == null)
                        camera.gameObject.AddComponent<AudioListener>();
            // Load after scene opening: Unity can unload references when switching scenes.
            Car("car-baseline.asset", false);
            var car = Car("car-responsive.asset", true);
            var track = AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(DrivingDir + "/graybox-01.asset");
            var evt = Event(track);
            var gameplay = AssetDatabase.LoadAssetAtPath<GameplayCatalogAsset>(ContentDir + "/quick-race.asset");
            if (gameplay == null)
            {
                gameplay = ScriptableObject.CreateInstance<GameplayCatalogAsset>();
                gameplay.quickRaceOnly = true;
                gameplay.gadgets = AssetDatabase.LoadAssetAtPath<GadgetCatalogAsset>(DrivingDir + "/gadget-catalog-01.asset");
                gameplay.difficulties = new[]
                {
                    Difficulty("easy", 1.35f, 0.95f),
                    Difficulty("medium", 1.1f, 0.7f),
                    Difficulty("hard", 0.9f, 0.45f)
                };
                AssetDatabase.CreateAsset(gameplay, ContentDir + "/quick-race.asset");
            }
            foreach (var root in scene.GetRootGameObjects())
            foreach (var app in root.GetComponentsInChildren<FoundationApp>(true))
            {
                var world = app.GetComponent<DrivingWorld>();
                var so = new SerializedObject(world);
                so.FindProperty("gameplayCatalog").objectReferenceValue = gameplay;
                so.FindProperty("gadgetCatalog").objectReferenceValue = gameplay.gadgets;
                so.FindProperty("contentVersion").stringValue = "playful-chaos-driving-1";
                var vehicles = so.FindProperty("vehicles");
                for (var i = 0; i < vehicles.arraySize; i++)
                    if (vehicles.GetArrayElementAtIndex(i).objectReferenceValue is VehicleDefinitionAsset v
                        && v.vehicleId == "car-01")
                        vehicles.GetArrayElementAtIndex(i).objectReferenceValue = car;
                var events = so.FindProperty("events");
                var found = false;
                for (var i = 0; i < events.arraySize; i++)
                    if (events.GetArrayElementAtIndex(i).objectReferenceValue == evt) found = true;
                if (!found)
                {
                    events.InsertArrayElementAtIndex(events.arraySize);
                    events.GetArrayElementAtIndex(events.arraySize - 1).objectReferenceValue = evt;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                var appSettings = new SerializedObject(app);
                appSettings.FindProperty("profileStorageNamespace").stringValue = "PlayfulChaos";
                appSettings.ApplyModifiedPropertiesWithoutUndo();
                var camera = app.GetComponent<DrivingCamera>();
                if (camera == null) camera = app.gameObject.AddComponent<DrivingCamera>();
                var cameraSettings = new SerializedObject(camera);
                cameraSettings.FindProperty("speedFeedbackEnabled").boolValue = true;
                cameraSettings.ApplyModifiedPropertiesWithoutUndo();
                var content = world.Load();
                if (content == null || world.Gameplay == null || !world.Gameplay.QuickRaceOnly)
                    throw new InvalidDataException("PlayfulChaos: invalid driving or Quick Race catalog.");
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("PlayfulChaos: playable Quick Race preview; contentStage=partial; isolated profiles.");
        }

        private static GameplayCatalogAsset.DifficultyRow Difficulty(string id, float margin, float charge)
        {
            return new GameplayCatalogAsset.DifficultyRow
            { difficultyId = id, brakeMarginScale = margin, boostUseMinCharge = charge,
                gadgetUseWillingness = 0.65f, shortcutWillingness = 0.4f };
        }

        private static VehicleDefinitionAsset Car(string name, bool responsive)
        {
            var path = ContentDir + "/" + name;
            var car = AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(path);
            if (car != null) return car;
            AssetDatabase.CopyAsset(DrivingDir + "/car-01.asset", path);
            car = AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(path);
            if (responsive)
            {
                car.acceleration = 8f;
                car.steeringRate = 2.3f;
                car.grip = 6f;
                car.driftSteeringFactor = 1.25f;
                car.driftLateralAcceleration = 4.5f;
                car.boostAcceleration = 15f;
                car.wallSpeedRetention = 0.4f;
                EditorUtility.SetDirty(car);
            }
            return car;
        }

        private static EventDefinitionAsset Event(TrackDefinitionAsset track)
        {
            var path = ContentDir + "/fun-graybox-car-01.asset";
            var evt = AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(path);
            if (evt != null) return evt;
            evt = ScriptableObject.CreateInstance<EventDefinitionAsset>();
            evt.eventId = "fun-graybox-car-01";
            evt.track = track;
            evt.laps = 2;
            evt.countdownTicks = 90;
            evt.vehicleIds = new[] { "car-01" };
            evt.eventType = GameplayEventType.Circuit;
            evt.vehicleClass = "car";
            evt.objectives = new[]
            {
                new EventDefinitionAsset.ObjectiveRow
                {
                    objectiveId = "fun-landing", kind = ObjectiveKind.Introductory,
                    alternatives = new[] { new EventDefinitionAsset.ObjectiveConditionRow
                        { metric = ObjectiveMetric.CleanLandings, threshold = 1 } }
                },
                new EventDefinitionAsset.ObjectiveRow
                {
                    objectiveId = "fun-podium", kind = ObjectiveKind.Mastery,
                    alternatives = new[] { new EventDefinitionAsset.ObjectiveConditionRow
                        { metric = ObjectiveMetric.FinishPlace, comparator = "atMost", threshold = 1 } }
                }
            };
            AssetDatabase.CreateAsset(evt, path);
            return evt;
        }
    }
}
