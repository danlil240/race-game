using FamilyRacing.Core.Driving;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace FamilyRacing.EditorTools
{
    // Repeatable, non-destructive physical-backend setup: generates the
    // lab tuning assets, the collision/validation fixture prefab, a
    // physical vehicle definition, and the developer-playable PhysicsLab
    // scene under Assets/FamilyRacing/Generated/Physics. Nothing outside
    // that folder is written — generated driving/foundation assets are
    // never touched — and the physical backend stays opt-in: the lab
    // scene is generated for developers but is NOT added to build
    // settings and ordinary content keeps the arcade motor.
    public static class VehiclePhysicsSetup
    {
        private const string PhysicsDir = "Assets/FamilyRacing/Generated/Physics";
        private const string DrivingDir = "Assets/FamilyRacing/Generated/Driving";
        private const string ScenePath = PhysicsDir + "/PhysicsLab.unity";
        private const string PrefabPath = PhysicsDir + "/PhysicsLabCourse.prefab";
        private const string RaceScenePath = PhysicsDir + "/PhysicsRace.unity";
        // Task 9 pins the physical content identity at "physics-1" —
        // cataloged as "physics-1|sim=1" through CompatibilityVersion so
        // an arcade build can never silently join a physical LAN race.
        private const string RaceContentVersion = "physics-1";

        public static void Create()
        {
            EnsureFolder("Assets/FamilyRacing/Generated", "Physics");
            var carTuning = EnsureCarTuning();
            EnsureHighComTuning();
            var bikeTuning = EnsureBikeTuning();
            var carVehicle = EnsureVehicle(carTuning);
            var bikeVehicle = EnsureBikeVehicle(bikeTuning);
            var course = EnsureCoursePrefab();
            EnsureLabScene(course, carTuning);
            // The physical race content reuses the shared graybox track
            // (it already authors physical recovery volumes, safe poses
            // and the four-slot LAN grid) — the backend is bound to the
            // content identity, not to a player-facing toggle. If the
            // driving slice has not been generated yet, this stays
            // honest: warn and skip instead of writing a dangling event.
            var physicsEvent = EnsurePhysicsEvent();
            EnsureRaceScene(physicsEvent, carVehicle, bikeVehicle);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolder(string parent, string name)
        {
            var path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        // Stock car preset: the authored source of truth is
        // VehiclePhysicsTuning.Car01(); the generated asset is rewritten
        // from it so the two can never drift apart silently.
        private static VehiclePhysicsTuningAsset EnsureCarTuning()
        {
            var asset = EnsureAsset<VehiclePhysicsTuningAsset>(
                "car-physics-01.asset");
            asset.ApplyPreset(VehiclePhysicsTuning.Car01());
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // The labeled high-COM untripped-roll preset: stock geometry with
        // the COM lifted above the body origin and extra grip, so a flat
        // full-lock turn demonstrates a genuine grip rollover — never a
        // scripted flip. Marked by filename so nobody mistakes it for a
        // shipping tune.
        private static VehiclePhysicsTuningAsset EnsureHighComTuning()
        {
            var asset = EnsureAsset<VehiclePhysicsTuningAsset>(
                "car-physics-highcom-01.asset");
            var preset = VehiclePhysicsTuning.Car01();
            preset.comOffsetY = 0.55f;
            preset.frictionCoefficient = 1.8f;
            // Keep low-speed steering authority at test speed.
            preset.steerReferenceSpeed = 60f;
            preset.cruiseSpeed = 30f;
            preset.rollDamping = 0f;
            asset.ApplyPreset(preset);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Motorcycle preset: same authored-source convention — the asset
        // is rewritten from VehiclePhysicsTuning.Motorcycle01() so the
        // combined bike/rider tune (balance gains included) can never
        // drift from the Core preset.
        private static VehiclePhysicsTuningAsset EnsureBikeTuning()
        {
            var asset = EnsureAsset<VehiclePhysicsTuningAsset>(
                "motorcycle-physics-01.asset");
            asset.ApplyPreset(VehiclePhysicsTuning.Motorcycle01());
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Physical vehicle definition: same authored VehicleTuning as
        // car-01 but selecting the opt-in PhysicalAssisted backend.
        private static VehicleDefinitionAsset EnsureVehicle(
            VehiclePhysicsTuningAsset tuning)
        {
            var asset = EnsureAsset<VehicleDefinitionAsset>(
                "car-physics-01-vehicle.asset");
            var preset = VehicleTuning.Car01();
            preset.vehicleId = "car-physics-01";
            preset.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            asset.ApplyPreset(preset);
            asset.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            asset.physicsTuning = tuning;
            asset.silhouette = "car";
            // Zero cosmetic lean: physical roll is real and the
            // presentation must not double-apply it (Task 8 owns that
            // visual path).
            asset.leanDegrees = 0f;
            asset.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
            asset.bodyColor = new Color(0.42f, 0.62f, 0.91f);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Physical motorcycle definition: the arcade-era motorcycle
        // envelope values selecting PhysicalAssisted, paired with the
        // motorcycle physics tuning asset. Cosmetic leanDegrees stays 0 —
        // the physical chassis roll is real and the presentation layer
        // must not double-apply it (Task 8 owns that visual path).
        private static VehicleDefinitionAsset EnsureBikeVehicle(
            VehiclePhysicsTuningAsset tuning)
        {
            var asset = EnsureAsset<VehicleDefinitionAsset>(
                "motorcycle-physics-01-vehicle.asset");
            var preset = VehicleTuning.Motorcycle01();
            preset.vehicleId = "motorcycle-physics-01";
            preset.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            asset.ApplyPreset(preset);
            asset.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            asset.physicsTuning = tuning;
            asset.silhouette = "motorcycle";
            asset.leanDegrees = 0f;
            asset.bodyScale = new Vector3(0.5f, 0.9f, 2.2f);
            asset.bodyColor = new Color(0.50f, 0.78f, 0.70f);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // The physical solo/LAN event: same graybox loop, one extra lap
        // of buffer, and only the physical-declared vehicles may enter.
        // The shared track asset is referenced, never rewritten — the
        // physical recovery volumes and safe poses it authors are the
        // single source for both backends' content.
        private static EventDefinitionAsset EnsurePhysicsEvent()
        {
            var track = AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(
                DrivingDir + "/graybox-01.asset");
            if (track == null)
            {
                Debug.LogWarning("VehiclePhysicsSetup: graybox-01.asset is "
                    + "missing — run DrivingPrototypeSetup first; the "
                    + "physics event and race scene are skipped.");
                return null;
            }
            var asset = EnsureAsset<EventDefinitionAsset>(
                "physics-01.asset");
            asset.eventId = "physics-01";
            asset.track = track;
            asset.laps = 2;
            asset.countdownTicks = 90;
            asset.vehicleIds = new[]
                { "car-physics-01", "motorcycle-physics-01" };
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Developer-playable physical race scene: the same self-
        // bootstrapping stack the bootstrap scene carries (FoundationApp
        // + input + shell + DrivingWorld), but the world is wired to the
        // physical vehicle definitions, the shared graybox content and
        // the PhysicalAssisted backend — the race-level selector that
        // binds the backend to this content identity. Deliberately not
        // added to EditorBuildSettings: opt-in developer content like
        // the lab. An existing scene is authored state and never
        // overwritten.
        private static void EnsureRaceScene(EventDefinitionAsset evt,
            VehicleDefinitionAsset car, VehicleDefinitionAsset bike)
        {
            if (evt == null || car == null || bike == null) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(RaceScenePath)
                    != null)
                return; // authored scene exists: never overwrite
            var coursePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                DrivingDir + "/GrayboxCourse.prefab");
            var track = evt.track;
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(
                "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset");

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.65f, 0.78f);
            cameraObject.tag = "MainCamera";

            var appObject = new GameObject("PhysicsRaceApp");
            appObject.AddComponent<FoundationApp>();
            appObject.AddComponent<FoundationInput>();
            var document = appObject.AddComponent<UIDocument>();
            if (panel != null)
            {
                var panelSerialized = new SerializedObject(document);
                var panelProperty =
                    panelSerialized.FindProperty("m_PanelSettings");
                if (panelProperty != null)
                {
                    panelProperty.objectReferenceValue = panel;
                    panelSerialized.ApplyModifiedPropertiesWithoutUndo();
                }
                else
                {
                    document.panelSettings = panel;
                }
            }
            appObject.AddComponent<FoundationShell>();
            var world = appObject.AddComponent<DrivingWorld>();
            var serialized = new SerializedObject(world);
            AssignObjectArray(serialized, "vehicles", car, bike);
            AssignObject(serialized, "track", track);
            AssignObjectArray(serialized, "events", evt);
            AssignObject(serialized, "coursePrefab", coursePrefab);
            var version = serialized.FindProperty("contentVersion");
            if (version != null) version.stringValue = RaceContentVersion;
            var backend = serialized.FindProperty("simulationBackend");
            if (backend != null)
                backend.enumValueIndex =
                    (int)VehicleSimulationBackend.PhysicalAssisted;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, RaceScenePath);
        }

        private static void AssignObject(SerializedObject so,
            string property, Object value)
        {
            var p = so.FindProperty(property);
            if (p != null) p.objectReferenceValue = value;
        }

        private static void AssignObjectArray(SerializedObject so,
            string property, params Object[] values)
        {
            var p = so.FindProperty(property);
            if (p == null || values == null) return;
            p.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue =
                    values[i];
        }

        private static T EnsureAsset<T>(string file) where T : ScriptableObject
        {
            var path = PhysicsDir + "/" + file;
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        // The lab fixture, all primitive boxes (collision-only when the
        // sim clones it; renderers stay so developers can see the lab):
        //   * flat accel/brake lane 30 x 300 m, top at y=0
        //   * slope ramp: 12 x 0.6 x 16 pitched -14 deg at the lane end
        //   * ordinary-turn skidpad: 100 x 100 flat pad east of the lane
        //   * slalom cones: six 0.4 m posts alternating +-4 m at z 20..70
        //   * aggressive curb strip: 1.2 x 0.45 x 30 at x=-6, z=60
        //   * jump ramp 8 x 0.6 x 12 pitched -18 deg with a flat landing
        // Reproducible geometry for the prototype report; the roll
        // fixtures used by the PlayMode tests are built independently in
        // PhysicsTestWorld.
        private static GameObject EnsureCoursePrefab()
        {
            var root = new GameObject("PhysicsLabCourse");
            try
            {
                Box(root, "Lane", new Vector3(0f, -0.5f, 0f),
                    new Vector3(30f, 1f, 300f));
                // Ordinary-turn skidpad east of the lane.
                Box(root, "Skidpad", new Vector3(60f, -0.5f, 80f),
                    new Vector3(100f, 1f, 100f));
                // Slope at the north end of the lane.
                Box(root, "Slope", new Vector3(0f, 1.2f, 140f),
                    new Vector3(12f, 0.6f, 16f), Quaternion.Euler(-14f, 0f, 0f));
                // Slalom posts down the mid-lane.
                for (var i = 0; i < 6; i++)
                    Box(root, "Slalom" + i,
                        new Vector3(i % 2 == 0 ? -4f : 4f, 0.3f, 20f + i * 10f),
                        new Vector3(0.4f, 0.6f, 0.4f));
                // Aggressive curb the PlayMode trip fixture mirrors.
                Box(root, "TripCurb", new Vector3(-6f, 0.225f, 60f),
                    new Vector3(1.2f, 0.45f, 30f));
                // Jump ramp + flat landing zone (the lane itself).
                Box(root, "JumpRamp", new Vector3(10f, 1.0f, -60f),
                    new Vector3(8f, 0.6f, 12f), Quaternion.Euler(-18f, 0f, 0f));

                var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                return saved;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void Box(GameObject parent, string name,
            Vector3 position, Vector3 size)
        {
            Box(parent, name, position, size, Quaternion.identity);
        }

        private static void Box(GameObject parent, string name,
            Vector3 position, Vector3 size, Quaternion rotation)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = position;
            box.transform.localRotation = rotation;
            box.transform.localScale = size;
        }

        // Developer-playable lab scene: a PhysicsLabWorld wired to the
        // generated course and tuning, plus a light and camera. Deliberately
        // not added to EditorBuildSettings — it is an opt-in developer
        // scene, opened by hand or by validation tooling.
        private static void EnsureLabScene(GameObject course,
            VehiclePhysicsTuningAsset carTuning)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                return; // authored scene exists: never overwrite
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.65f, 0.78f);
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 6f, -152f);

            var labObject = new GameObject("PhysicsLab");
            var lab = labObject.AddComponent<PhysicsLabWorld>();
            var serialized = new SerializedObject(lab);
            var courseProperty = serialized.FindProperty("coursePrefab");
            if (courseProperty != null)
                courseProperty.objectReferenceValue = course;
            var tuningProperty = serialized.FindProperty("physicsTuning");
            if (tuningProperty != null)
                tuningProperty.objectReferenceValue = carTuning;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
        }
    }
}
