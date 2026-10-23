using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace FamilyRacing.EditorTools
{
    // All changes belong to the opt-in trial. Gameplay colliders remain authoritative.
    public static class AssetTrialCoast
    {
        private const string Root = AssetTrialAuthoring.Root;
        private const string Output = AssetTrialAuthoring.Output;

        private static T Required<T>(string path) where T : UnityEngine.Object
        {
            var value = AssetDatabase.LoadAssetAtPath<T>(path);
            if (value == null) throw new InvalidOperationException("Coastal trial missing " + path);
            return value;
        }

        private static Material Material(string name, Color color, string shader = "Universal Render Pipeline/Simple Lit")
        {
            var path = Output + "/Materials/" + name + ".mat";
            var value = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (value == null)
            {
                value = new Material(Shader.Find(shader)) { name = name };
                AssetDatabase.CreateAsset(value, path);
            }
            if (value.HasProperty("_BaseColor")) value.SetColor("_BaseColor", color);
            if (value.HasProperty("_Smoothness")) value.SetFloat("_Smoothness", .12f);
            EditorUtility.SetDirty(value);
            return value;
        }

        private static Bounds Bounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private static GameObject Model(Transform parent, string pack, string name, Vector3 position, Vector2 footprint, float yaw)
        {
            var go = UnityEngine.Object.Instantiate(Required<GameObject>(Root + "/" + pack + "/" + name + ".fbx"));
            go.name = name;
            // Preserve the root-axis conversion written by Unity's FBX importer.
            go.transform.rotation = Quaternion.Euler(0, yaw, 0) * go.transform.rotation;
            var bounds = Bounds(go);
            go.transform.localScale *= Mathf.Min(footprint.x / bounds.size.x, footprint.y / bounds.size.z);
            bounds = Bounds(go);
            go.transform.position += position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            go.transform.SetParent(parent, true);
            var material = Material("Coast_" + pack, Color.white);
            var texture = Required<Texture2D>(Root + "/" + pack + (pack == "KenneySuburban" ? "/variation-c.png" : "/colormap.png"));
            material.SetTexture("_BaseMap", texture);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => material).ToArray();
            foreach (var collider in go.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = SunnyIslandCourseAuthoring.OccluderLayer;
            bounds = Bounds(go);
            var occluder = new GameObject("CoastCameraOccluder");
            occluder.layer = SunnyIslandCourseAuthoring.OccluderLayer;
            occluder.transform.SetParent(parent, false);
            occluder.transform.position = bounds.center;
            occluder.AddComponent<BoxCollider>().size = bounds.size;
            return go;
        }

        public static void Dress(GameObject course)
        {
            // The source E/W skirts swap dimensions and then yaw them again,
            // turning their 244 m length into a ~60 m vertical sand slope.
            // Author all skirts in one local basis, then rotate once toward sea.
            foreach (var skirt in course.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Skirt_", StringComparison.Ordinal)))
            {
                bool eastWest = skirt.name == "Skirt_E" || skirt.name == "Skirt_W";
                float yaw = skirt.name == "Skirt_E" ? 90 : skirt.name == "Skirt_W" ? -90 : skirt.name == "Skirt_S" ? 180 : 0;
                skirt.localScale = new Vector3(eastWest ? 164 : 244, .7f, 9);
                skirt.localRotation = Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(14, 0, 0);
            }
            var root = new GameObject("CoastalCircuitScenery").transform;
            root.SetParent(course.transform, false);
            ReplaceHouses(course, root);
            var water = Material("CoastWater", Color.white, "FamilyRacing/Trial Coastal Water");
            foreach (var renderer in course.GetComponentsInChildren<Renderer>(true))
                if (renderer.name.StartsWith("Water_", StringComparison.Ordinal))
                { renderer.sharedMaterial = water; renderer.shadowCastingMode = ShadowCastingMode.Off; }

            // Boats and docks sit beyond the course's real sea-wall colliders.
            Model(root, "KenneyWatercraft", "boat-sail-a", new Vector3(137, -.9f, 15), new Vector2(5, 10), 180);
            Model(root, "KenneyWatercraft", "boat-fishing-small", new Vector3(135, -.8f, -9), new Vector2(6, 9), 20);
            Model(root, "KenneyWatercraft", "boat-speed-a", new Vector3(-137, -.8f, 35), new Vector2(4, 8), -20);
            Model(root, "KenneyWatercraft", "boat-sail-a", new Vector3(55, -.9f, 103), new Vector2(5, 11), 65);
            var wood = Required<Material>(Output + "/Materials/Surface_wood_Lane_NE_Entry.mat");
            Box(root, "HarborDock", new Vector3(130, -.1f, 3), new Vector3(13, .7f, 5), wood);
            Box(root, "HarborDock", new Vector3(-130, -.1f, 29), new Vector3(13, .7f, 4), wood);
            for (int i = 0; i < 5; i++)
                Model(root, "KenneyWatercraft", "buoy", new Vector3(148 + i * 4, -.7f, -28 + i * 10), new Vector2(1.4f, 1.4f), i * 31);

            RoadEdges(root, course);
            DistantCoast(root);
        }

        private static void ReplaceHouses(GameObject course, Transform parent)
        {
            var houses = course.GetComponentsInChildren<Transform>(true).Where(t => t.name == "House").ToArray();
            var models = new[] { "building-type-a", "building-type-d", "building-type-k", "building-type-m" };
            for (var i = 0; i < houses.Length; i++)
            {
                var house = houses[i];
                var cameraBox = house.GetComponentInChildren<BoxCollider>();
                var footprint = cameraBox == null ? new Vector2(7, 6) : new Vector2(cameraBox.size.x / 1.3f, cameraBox.size.z / 1.2f);
                foreach (var lod in house.GetComponentsInChildren<LODGroup>(true)) lod.enabled = false;
                foreach (var renderer in house.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                Model(parent, "KenneySuburban", models[i % models.Length], house.position, footprint, i * 90);
            }
        }

        private static void Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            UnityEngine.Object.DestroyImmediate(box.GetComponent<Collider>());
            box.transform.SetParent(parent, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;
            box.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static void RoadEdges(Transform parent, GameObject course)
        {
            var cream = Material("CoastCurbCream", new Color(.88f, .86f, .74f));
            var navy = Material("CoastCurbNavy", new Color(.09f, .25f, .34f));
            foreach (var road in course.GetComponentsInChildren<Transform>(true)
                .Where(t => new[] { "Road_South", "Road_North", "Road_East", "Road_SouthReturn", "Road_RampRun" }.Contains(t.name)))
            {
                bool alongZ = road.localScale.z > road.localScale.x;
                float length = alongZ ? road.localScale.z : road.localScale.x;
                float width = alongZ ? road.localScale.x : road.localScale.z;
                for (var parity = 0; parity < 2; parity++)
                {
                    var vertices = new List<Vector3>(); var triangles = new List<int>();
                    for (var side = -1; side <= 1; side += 2)
                    for (float offset = -length / 2 + parity * 2; offset < length / 2; offset += 4)
                    {
                        var c = road.position + (alongZ ? Vector3.forward : Vector3.right) * (offset + 1)
                            + (alongZ ? Vector3.right : Vector3.forward) * (side * (width / 2 - .35f));
                        c.y = .075f;
                        var u = (alongZ ? Vector3.right : Vector3.forward) * .3f;
                        var v = (alongZ ? Vector3.forward : Vector3.right) * Mathf.Min(1, (length / 2 - offset) / 2);
                        int start = vertices.Count;
                        vertices.Add(c - u - v); vertices.Add(c - u + v); vertices.Add(c + u + v); vertices.Add(c + u - v);
                        if (alongZ) triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                        else triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
                    }
                    var mesh = new Mesh { name = road.name + "_Edge_" + parity };
                    mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals();
                    var path = Output + "/" + mesh.name + ".asset";
                    var previous = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (previous == null) AssetDatabase.CreateAsset(mesh, path);
                    else { EditorUtility.CopySerialized(mesh, previous); UnityEngine.Object.DestroyImmediate(mesh); mesh = previous; }
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(parent, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = parity == 0 ? cream : navy;
                }
            }
        }

        private static void DistantCoast(Transform parent)
        {
            // Render-only scenery outside the outer walls; no road furniture is
            // allowed to imply a new solid obstacle on the drivable platform.
            var rockSource = Required<GameObject>(Root + "/Quaternius/Rock_Medium_2.fbx");
            var treeSource = Required<GameObject>(Root + "/Quaternius/CommonTree_3.fbx");
            var rockMat = Required<Material>(Output + "/Materials/Rocks_Diffuse.mat");
            var bark = Required<Material>(Output + "/Materials/Bark_NormalTree.mat");
            var leaf = Required<Material>(Output + "/Materials/Leaves_NormalTree_C.mat");
            var positions = new[] { new Vector3(-154,-2,115), new Vector3(-160,-2,-110), new Vector3(175,-2,110), new Vector3(35,-2,-128) };
            for (int i = 0; i < positions.Length; i++)
            {
                var rock = UnityEngine.Object.Instantiate(rockSource);
                rock.name = "DistantRockIsland";
                rock.transform.SetParent(parent, true);
                var bounds = Bounds(rock);
                rock.transform.localScale *= (18 + i * 2) / bounds.size.y;
                bounds = Bounds(rock);
                rock.transform.position += positions[i] - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                foreach (var renderer in rock.GetComponentsInChildren<Renderer>()) renderer.sharedMaterial = rockMat;
                for (var j = 0; j < 3; j++)
                {
                    var tree = UnityEngine.Object.Instantiate(treeSource);
                    tree.name = "DistantIslandTree";
                    tree.transform.SetParent(parent, true);
                    var b = Bounds(tree);
                    tree.transform.localScale *= (10 + j) / b.size.y;
                    b = Bounds(tree);
                    tree.transform.position += positions[i] + new Vector3((j - 1) * 7, 11, j * 3) - new Vector3(b.center.x, b.min.y, b.center.z);
                    foreach (var renderer in tree.GetComponentsInChildren<Renderer>())
                        renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m.name.Contains("Bark") ? bark : leaf).ToArray();
                }
            }
        }

        public static void ConfigureScene(Scene scene)
        {
            var sky = Material("CoastSky", Color.white, "FamilyRacing/Trial Coastal Sky");
            foreach (var camera in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true)))
            {
                camera.clearFlags = CameraClearFlags.Skybox;
                var component = camera.GetComponent<Skybox>();
                if (component == null) component = camera.gameObject.AddComponent<Skybox>();
                component.material = sky;
            }
            RenderSettings.skybox = sky;
            RenderSettings.fogColor = new Color(.53f, .72f, .77f);
            var app = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<FoundationApp>(true)).Single();
            var drivingCamera = app.GetComponent<DrivingCamera>();
            if (drivingCamera == null) drivingCamera = app.gameObject.AddComponent<DrivingCamera>();
            var so = new SerializedObject(drivingCamera);
            so.FindProperty("chaseBack").floatValue = 8.5f;
            so.FindProperty("chaseUp").floatValue = 3.25f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
