using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing.EditorTools
{
    // Repeatable, non-destructive driving-slice setup: creates only missing
    // generated content (materials, vehicle/track/event definitions, the
    // graybox course prefab) and wires DrivingWorld into the bootstrap
    // scene. Safe to re-run; authored assets are never overwritten, but a
    // missing DrivingWorld component or missing references are repaired.
    public static class DrivingPrototypeSetup
    {
        private const string DrivingDir = "Assets/FamilyRacing/Generated/Driving";
        private const string ScenePath = "Assets/FamilyRacing/Generated/Foundation.unity";
        private const string ContentVersion = "driving-0.1";

        public static void Create()
        {
            EnsureFolder("Assets/FamilyRacing/Generated", "Driving");
            var groundMat = EnsureMaterial("GrayboxGround.mat",
                new Color(0.83f, 0.80f, 0.72f));
            var wallMat = EnsureMaterial("GrayboxWall.mat",
                new Color(0.24f, 0.49f, 0.63f));
            var gateMat = EnsureMaterial("GrayboxGate.mat",
                new Color(0.91f, 0.51f, 0.42f));

            var car = EnsureCar();
            var moto = EnsureMotorcycle();
            var track = EnsureTrack();
            var practice = EnsureEvent("practice-02", track, laps: 2);
            var intro = EnsureEvent("intro-01", track, laps: 1);
            var course = EnsureCoursePrefab(groundMat, wallMat, gateMat);

            WireScene(car, moto, track, practice, intro, course);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolder(string parent, string name)
        {
            var path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static Material EnsureMaterial(string file, Color color)
        {
            var path = DrivingDir + "/" + file;
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            material = new Material(shader != null ? shader : Shader.Find("Standard"));
            material.color = color;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static T EnsureAsset<T>(string file) where T : ScriptableObject
        {
            var path = DrivingDir + "/" + file;
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static VehicleDefinitionAsset EnsureCar()
        {
            var asset = EnsureAsset<VehicleDefinitionAsset>("car-01.asset");
            asset.vehicleId = "car-01";
            asset.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
            asset.bodyColor = new Color(0.91f, 0.51f, 0.42f);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        private static VehicleDefinitionAsset EnsureMotorcycle()
        {
            var asset = EnsureAsset<VehicleDefinitionAsset>("motorcycle-01.asset");
            asset.vehicleId = "motorcycle-01";
            asset.maxSpeed = 20f;
            asset.acceleration = 7f;
            asset.steeringRate = 3.2f;
            asset.halfWidth = 0.45f;
            asset.halfLength = 1.1f;
            asset.height = 1.4f;
            asset.bodyScale = new Vector3(0.9f, 1.4f, 2.2f);
            asset.bodyColor = new Color(0.50f, 0.78f, 0.70f);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Graybox loop: spawn south heading north; gates sit at leg
        // midpoints of a counter-clockwise rectangle. Checkpoint rules are
        // Task 2 — these only need valid, ordered data now.
        private static TrackDefinitionAsset EnsureTrack()
        {
            var asset = EnsureAsset<TrackDefinitionAsset>("graybox-01.asset");
            asset.courseId = "graybox-01";
            asset.spawnPosition = new Vector3(0f, 0f, -60f);
            asset.spawnHeadingDegrees = 0f;
            asset.gates = new[]
            {
                Gate("start", 0f, -60f, 0f, 1f),
                Gate("gate-1", 0f, 25f, 0f, 1f),
                Gate("gate-2", 55f, 55f, 1f, 0f),
                Gate("gate-3", 95f, 0f, 0f, -1f),
                Gate("gate-4", 50f, -55f, -1f, 0f)
            };
            EditorUtility.SetDirty(asset);
            return asset;
        }

        private static TrackDefinitionAsset.Gate Gate(
            string id, float x, float z, float dirX, float dirZ)
        {
            return new TrackDefinitionAsset.Gate
            {
                id = id,
                center = new Vector3(x, 0f, z),
                halfWidth = 12f,
                direction = new Vector2(dirX, dirZ)
            };
        }

        private static EventDefinitionAsset EnsureEvent(
            string id, TrackDefinitionAsset track, int laps)
        {
            var asset = EnsureAsset<EventDefinitionAsset>(id + ".asset");
            asset.eventId = id;
            asset.track = track;
            asset.laps = laps;
            asset.countdownTicks = 90;
            asset.vehicleIds = new[] { "car-01", "motorcycle-01" };
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // The course prefab is pure graybox: one ground slab, perimeter
        // walls, two chicane barriers, one ramp, and trigger gate volumes.
        // Gate colliders are triggers so the motor's probe/sweep (which
        // ignores triggers) only ever collides with solid course geometry.
        private static GameObject EnsureCoursePrefab(
            Material groundMat, Material wallMat, Material gateMat)
        {
            var path = DrivingDir + "/GrayboxCourse.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var root = new GameObject("GrayboxCourse");
            try
            {
                Box(root, "Ground", new Vector3(0f, -0.5f, 0f),
                    new Vector3(240f, 1f, 160f), Quaternion.identity, groundMat);
                Box(root, "WallWest", new Vector3(-119.5f, 1.5f, 0f),
                    new Vector3(1f, 3f, 160f), Quaternion.identity, wallMat);
                Box(root, "WallEast", new Vector3(119.5f, 1.5f, 0f),
                    new Vector3(1f, 3f, 160f), Quaternion.identity, wallMat);
                Box(root, "WallNorth", new Vector3(0f, 1.5f, 79.5f),
                    new Vector3(240f, 3f, 1f), Quaternion.identity, wallMat);
                Box(root, "WallSouth", new Vector3(0f, 1.5f, -79.5f),
                    new Vector3(240f, 3f, 1f), Quaternion.identity, wallMat);
                Box(root, "BarrierA", new Vector3(25f, 1f, -25f),
                    new Vector3(6f, 2f, 30f), Quaternion.identity, wallMat);
                Box(root, "BarrierB", new Vector3(-30f, 1f, 10f),
                    new Vector3(30f, 2f, 6f), Quaternion.identity, wallMat);
                Box(root, "Ramp", new Vector3(-60f, 1.2f, 30f),
                    new Vector3(12f, 0.6f, 16f),
                    Quaternion.Euler(-14f, 0f, 0f), wallMat);

                var gates = new GameObject("Gates");
                gates.transform.SetParent(root.transform, false);
                GateBox(gates, "Gate_start", new Vector3(0f, 2f, -60f), 12f, 0f, gateMat);
                GateBox(gates, "Gate_gate-1", new Vector3(0f, 2f, 25f), 12f, 0f, gateMat);
                GateBox(gates, "Gate_gate-2", new Vector3(55f, 2f, 55f), 12f, 90f, gateMat);
                GateBox(gates, "Gate_gate-3", new Vector3(95f, 2f, 0f), 12f, 0f, gateMat);
                GateBox(gates, "Gate_gate-4", new Vector3(50f, 2f, -55f), 12f, 90f, gateMat);

                var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                return saved;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject Box(GameObject parent, string name, Vector3 position,
            Vector3 scale, Quaternion rotation, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = position;
            box.transform.localRotation = rotation;
            box.transform.localScale = scale;
            if (material != null)
                box.GetComponent<Renderer>().sharedMaterial = material;
            return box;
        }

        private static void GateBox(GameObject parent, string name, Vector3 center,
            float halfWidth, float yawDegrees, Material material)
        {
            var gate = Box(parent, name, center,
                new Vector3(halfWidth * 2f, 4f, 0.5f),
                Quaternion.Euler(0f, yawDegrees, 0f), material);
            gate.GetComponent<Collider>().isTrigger = true;
        }

        // Scene wiring mirrors RepairScenePanel: the existing bootstrap
        // scene is inspected, not trusted — a missing DrivingWorld
        // component or missing references are repaired in place. Scenes
        // already open stay open; scenes we opened are closed afterwards.
        private static void WireScene(Object car, Object moto, Object track,
            Object practice, Object intro, GameObject course)
        {
            var alreadyOpen = false;
            var scene = new Scene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (open.path == ScenePath) { scene = open; alreadyOpen = true; break; }
            }
            if (!alreadyOpen)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var dirty = false;
            foreach (var rootObj in scene.GetRootGameObjects())
            foreach (var app in rootObj.GetComponentsInChildren<FoundationApp>(true))
            {
                var world = app.GetComponent<DrivingWorld>();
                if (world == null)
                {
                    world = app.gameObject.AddComponent<DrivingWorld>();
                    dirty = true;
                }
                var serialized = new SerializedObject(world);
                dirty |= AssignArray(serialized, "vehicles", car, moto);
                dirty |= Assign(serialized, "track", track);
                dirty |= AssignArray(serialized, "events", practice, intro);
                dirty |= Assign(serialized, "coursePrefab", course);
                var version = serialized.FindProperty("contentVersion");
                if (version != null && version.stringValue != ContentVersion)
                {
                    version.stringValue = ContentVersion;
                    dirty = true;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            if (dirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            if (!alreadyOpen)
                EditorSceneManager.CloseScene(scene, true);
        }

        private static bool Assign(SerializedObject so, string property, Object value)
        {
            var p = so.FindProperty(property);
            if (p == null || p.objectReferenceValue == value) return false;
            p.objectReferenceValue = value;
            return true;
        }

        private static bool AssignArray(SerializedObject so, string property,
            params Object[] values)
        {
            var p = so.FindProperty(property);
            if (p == null || !p.isArray) return false;
            var dirty = false;
            if (p.arraySize != values.Length)
            {
                p.arraySize = values.Length;
                dirty = true;
            }
            for (var i = 0; i < values.Length; i++)
            {
                var element = p.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue == values[i]) continue;
                element.objectReferenceValue = values[i];
                dirty = true;
            }
            return dirty;
        }
    }
}
