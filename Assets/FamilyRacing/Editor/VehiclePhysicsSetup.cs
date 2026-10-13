using FamilyRacing.Core.Driving;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        private const string ScenePath = PhysicsDir + "/PhysicsLab.unity";
        private const string PrefabPath = PhysicsDir + "/PhysicsLabCourse.prefab";

        public static void Create()
        {
            EnsureFolder("Assets/FamilyRacing/Generated", "Physics");
            var carTuning = EnsureCarTuning();
            EnsureHighComTuning();
            EnsureVehicle(carTuning);
            var course = EnsureCoursePrefab();
            EnsureLabScene(course, carTuning);
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

        // Physical vehicle definition: same authored VehicleTuning as
        // car-01 but selecting the opt-in PhysicalAssisted backend.
        private static void EnsureVehicle(VehiclePhysicsTuningAsset tuning)
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
            asset.leanDegrees = 4f;
            asset.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
            asset.bodyColor = new Color(0.42f, 0.62f, 0.91f);
            EditorUtility.SetDirty(asset);
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
