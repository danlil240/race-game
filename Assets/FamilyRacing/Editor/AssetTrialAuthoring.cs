using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FamilyRacing.EditorTools
{
    // Opt-in, throwaway art study. Never called by production setup/builds.
    public static class AssetTrialAuthoring
    {
        public const string Root = "Assets/FamilyRacing/Art/AssetTrial";
        public const string Output = "Assets/FamilyRacing/Generated/AssetTrial";
        public const string ScenePath = Output + "/SunnyIslandAssetTrial.unity";

        [MenuItem("Family Racing/Asset Trial/Create Sunny Island Trial")]
        public static void Create()
        {
            // Source content must already exist: do not regenerate the user's art.
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(VisualBenchmarkSetup.ScenePath) == null)
                throw new InvalidOperationException("Create the existing VisualBenchmark scene first.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Output + "/Materials");
            AssetDatabase.Refresh();
            ConfigureTextures();
            AssetTrialRacers.BuildCharacters();
            var car = BuildCar();
            SurfaceTexture("road"); SurfaceTexture("sand");
            var course = AssetTrialCircuits.Build();
            // Save-as FIRST: scene wiring below can only affect the opt-in trial.
            var scene = EditorSceneManager.OpenScene(VisualBenchmarkSetup.ScenePath);
            if (!EditorSceneManager.SaveScene(scene, ScenePath, false))
                throw new IOException("Could not save the separate asset trial scene.");
            // Opening a scene can unload assets referenced only by managed locals.
            car = Required<VehicleDefinitionAsset>(Output + "/car-01.asset");
            course = Required<GameObject>(AssetTrialCircuits.PrefabPath);
            var world = scene.GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var hud = world.GetComponent<AssetTrialHud>() ?? world.gameObject.AddComponent<AssetTrialHud>();
            hud.portraits = RacerVisualAuthoring.CharacterIds
                .Select(id => Required<Texture2D>(AssetTrialRacers.PortraitPath(id))).ToArray();
            hud.coursePreviews = AssetTrialCircuits.Ids
                .Select(id => Required<Texture2D>(AssetTrialCircuits.PreviewPath(id))).ToArray();
            EditorUtility.SetDirty(hud);
            var so = new SerializedObject(world);
            so.FindProperty("coursePrefab").objectReferenceValue = course;
            so.FindProperty("contentVersion").stringValue = "closed-circuits-2";
            var vehicles = so.FindProperty("vehicles");
            for (var i = 0; i < vehicles.arraySize; i++)
            {
                var slot = vehicles.GetArrayElementAtIndex(i);
                var vehicle = slot.objectReferenceValue as VehicleDefinitionAsset;
                if (vehicle != null && vehicle.vehicleId == "car-01")
                    slot.objectReferenceValue = car;
            }
            var characters = so.FindProperty("characters");
            characters.arraySize = RacerVisualAuthoring.CharacterIds.Length;
            for (var i = 0; i < characters.arraySize; i++)
                characters.GetArrayElementAtIndex(i).objectReferenceValue = Required<CharacterDefinitionAsset>(
                    AssetTrialRacers.CharacterPath(RacerVisualAuthoring.CharacterIds[i]));
            AssetTrialCircuits.Bind(so);
            AssetTrialCampaign.Bind(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            // Warm directional light and restrained ambient so textured surfaces read.
            foreach (var light in scene.GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<Light>(true)))
            {
                if (light.type != LightType.Directional) continue;
                light.intensity = .85f;
                light.color = new Color(1f, .95f, .85f);
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.32f, .42f, .50f);
            RenderSettings.ambientEquatorColor = new Color(.36f, .35f, .31f);
            RenderSettings.ambientGroundColor = new Color(.17f, .21f, .18f);
            AssetTrialCoast.ConfigureScene(scene);
            foreach(var camera in scene.GetRootGameObjects().SelectMany(go=>go.GetComponentsInChildren<Camera>(true))) camera.farClipPlane=220;
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Validate();
            Debug.Log("Asset trial created: " + ScenePath + ". Press Play, choose practice and car-01.");
        }

        private static void ConfigureTextures()
        {
            foreach (var file in Directory.GetFiles(Root, "*.png", SearchOption.AllDirectories))
            {
                var path = file.Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                importer.textureShape = TextureImporterShape.Texture2D;
                importer.maxTextureSize = 1024;
                importer.mipmapEnabled = true;
                importer.textureCompression = TextureImporterCompression.Compressed;
                if (path.Contains("_Normal.png")) importer.textureType = TextureImporterType.NormalMap;
                importer.SaveAndReimport();
            }
        }

        private static T Required<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("Asset trial missing " + path);
            return asset;
        }

        private static T SaveAsset<T>(T value, string path) where T : UnityEngine.Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(value, path); return value; }
            EditorUtility.CopySerialized(value, existing);
            UnityEngine.Object.DestroyImmediate(value);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        private static Bounds BoundsOf(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) throw new InvalidOperationException("No model renderers: " + go.name);
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private static Material MaterialFor(Material source, bool kenney)
        {
            var name = kenney ? "KenneyColormap" : source.name.Replace(" (Instance)", "");
            var path = Output + "/Materials/" + name.Replace('/', '_') + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Texture texture = null;
            if (kenney) texture = Required<Texture2D>(Root + "/Kenney/colormap.png");
            else
            {
                var stem = name;
                if (name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0) stem = "Rocks_Diffuse";
                if (name == "Leaves_NormalTree" || name == "Leaves_TwistedTree") stem += "_C";
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Quaternius/Textures/" + stem + ".png");
            }
            if (texture == null) throw new InvalidOperationException("Missing texture for " + source.name);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Simple Lit")) { name = name };
            mat.SetTexture("_BaseMap", texture);
            mat.SetTexture("_MainTex", texture);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetColor("_Color", Color.white);
            mat.SetFloat("_Smoothness", .12f);
            mat.SetFloat("_Cull", 0f);
            if (!kenney && (name.Contains("Leaves") || name.Contains("Grass") || name.Contains("Flower")))
            {
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_Cutoff", .4f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)RenderQueue.AlphaTest;
            }
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        internal static void ConvertMaterials(GameObject model, bool kenney)
        {
            foreach (var renderer in model.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m =>
                    m == null ? throw new InvalidOperationException("Missing model material") : MaterialFor(m, kenney)).ToArray();
            foreach (var collider in model.GetComponentsInChildren<Collider>())
                UnityEngine.Object.DestroyImmediate(collider);
        }

        private static VehicleDefinitionAsset BuildCar()
        {
            // Preserve the tested wheel pivots, seat and hand mounts. The imported
            // chassis replaces the body; the shared rider rig still drives it.
            var root = UnityEngine.Object.Instantiate(Required<GameObject>(RacerVisualAuthoring.CarPrefabPath));
            root.name = "KenneyRacerTrial";
            try
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>())
                {
                    bool wheel = false;
                    for (var t = renderer.transform; t != root.transform; t = t.parent)
                        if (t.name.StartsWith("Wheel", StringComparison.Ordinal)) wheel = true;
                    renderer.enabled = wheel || renderer.name == "DashWheel";
                }
                var model = UnityEngine.Object.Instantiate(Required<GameObject>(Root + "/Kenney/race-future.fbx"));
                ConvertMaterials(model, true);
                // Retain the FBX importer's authored axis conversion.
                var bounds = BoundsOf(model);
                model.transform.localScale *= 3.35f / bounds.size.z;
                bounds = BoundsOf(model);
                model.transform.position -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                // The original animated wheels are aligned to gameplay contact points.
                foreach (var renderer in model.GetComponentsInChildren<Renderer>())
                    if (renderer.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0)
                        renderer.enabled = false;
                model.transform.SetParent(root.transform, true);
                model.name = "KenneyChassis";
                AssetTrialRacers.DetailCar(root, model);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, Output + "/KenneyRacerTrial.prefab");
                var visual = UnityEngine.Object.Instantiate(Required<VehicleVisualDefinitionAsset>(RacerVisualAuthoring.CarVisualAssetPath));
                visual.visualId = "kenney-racer-trial";
                visual.prefab = prefab;
                visual = SaveAsset(visual, Output + "/kenney-visual.asset");
                var vehicle = UnityEngine.Object.Instantiate(Required<VehicleDefinitionAsset>("Assets/FamilyRacing/Generated/Driving/car-01.asset"));
                vehicle.visual = visual;
                vehicle.maxSpeed = 25.2f;
                vehicle.acceleration = 9.5f;
                vehicle.boostMaxSpeed = 34f;
                vehicle.boostAcceleration = 16f;
                return SaveAsset(vehicle, Output + "/car-01.asset");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static GameObject BuildCourse()
        {
            var root = UnityEngine.Object.Instantiate(Required<GameObject>(SunnyIslandCourseAuthoring.CourseVisualPath));
            root.name = "sunny-island-asset-trial";
            try
            {
                var dressing = new GameObject("QuaterniusTrialSection");
                dressing.transform.SetParent(root.transform, false);
                SurfacePass(root);
                // Garden corners on the existing NE island, away from the track and houses.
                Prop(dressing.transform, "CommonTree_1", new Vector3(23f, 2.13f, 39f), 5.5f, 20f);
                Prop(dressing.transform, "CommonTree_3", new Vector3(41f, 2.13f, 39f), 5f, 150f);
                Prop(dressing.transform, "Rock_Medium_1", new Vector3(24f, 2.13f, 48f), 1.3f, 45f);
                Prop(dressing.transform, "Rock_Medium_2", new Vector3(40f, 2.13f, 49f), 1.2f, 100f);
                for (var i = 0; i < 6; i++)
                {
                    Prop(dressing.transform, i % 2 == 0 ? "Bush_Common" : "Bush_Common_Flowers",
                        new Vector3(24f + i * 3f, 2.13f, 38.2f), .9f, i * 43f);
                    Prop(dressing.transform, "Flower_3_Group",
                        new Vector3(24f + i * 3f, 2.13f, 49.2f), .35f, i * 37f);
                }
                // A small offshore garden alongside the opening straight: from the
                // chase camera the trial is visible before reaching the NE village.
                for (var i = 0; i < 3; i++)
                {
                    var p = new Vector3(-18f, -.25f, 7f + i * 12f);
                    Prop(dressing.transform, "Rock_Medium_2", p, 3f, i * 67f);
                    Prop(dressing.transform, i % 2 == 0 ? "CommonTree_1" : "CommonTree_3",
                        p + Vector3.up * 1.4f, 5.5f, i * 80f);
                    Prop(dressing.transform, "Bush_Common", p + new Vector3(1.2f, 1f, .4f), 1.5f, 45f);
                }
                AssetTrialCoast.Dress(root);
                return PrefabUtility.SaveAsPrefabAsset(root, Output + "/SunnyIslandTrial.prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void Prop(Transform parent, string modelName, Vector3 position, float height, float yaw)
        {
            var go = UnityEngine.Object.Instantiate(Required<GameObject>(Root + "/Quaternius/" + modelName + ".fbx"));
            go.name = modelName;
            ConvertMaterials(go, false);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * go.transform.rotation;
            var bounds = BoundsOf(go);
            go.transform.localScale *= height / Mathf.Max(bounds.size.y, .01f);
            bounds = BoundsOf(go);
            go.transform.position += position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            go.transform.SetParent(parent, true);
            bounds = BoundsOf(go);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = SunnyIslandCourseAuthoring.OccluderLayer;
            // Camera sees dressing; driving physics explicitly excludes this layer.
            var occluder = new GameObject("CameraOccluder");
            occluder.layer = SunnyIslandCourseAuthoring.OccluderLayer;
            occluder.transform.SetParent(parent, false);
            occluder.transform.position = bounds.center;
            occluder.AddComponent<BoxCollider>().size = bounds.size;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                r.receiveShadows = true;
        }

        private static void SurfacePass(GameObject course)
        {
            foreach (var renderer in course.GetComponentsInChildren<MeshRenderer>(true))
            {
                var source = renderer.sharedMaterial;
                if (source == null) continue;
                var role = source.name;
                string kind = role == "Mat_DeckSand" ? "sand" : role == "Mat_RoadDust" ? "road"
                    : role == "Mat_Boardwalk" ? "wood" : null;
                if (kind == null) continue;
                // Distinct tiling per slab keeps grain at a consistent world scale.
                var scale = renderer.transform.lossyScale;
                var tiling = new Vector2(Mathf.Max(1, Mathf.Abs(scale.x) / 4f), Mathf.Max(1, Mathf.Abs(scale.z) / 4f));
                var path = Output + "/Materials/Surface_" + kind + "_" + renderer.name + ".mat";
                var mat = new Material(source) { name = "Trial_" + kind + "_" + renderer.name };
                mat.SetColor("_BaseColor", Color.white);
                mat.SetColor("_Color", Color.white);
                mat.SetTexture("_BaseMap", SurfaceTexture(kind));
                mat.SetTextureScale("_BaseMap", tiling);
                mat.SetFloat("_Smoothness", .06f);
                renderer.sharedMaterial = SaveAsset(mat, path);
            }
        }

        private static Texture2D SurfaceTexture(string kind)
        {
            var path = Output + "/Materials/Texture_" + kind + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            const int size = 256;
            var texture = existing != null ? existing : new Texture2D(size, size, TextureFormat.RGB24, true)
            { name = "Trial_" + kind, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var pixels = new Color[size * size];
            var baseColor = kind == "road" ? new Color(.28f, .31f, .32f)
                : kind == "wood" ? new Color(.52f, .35f, .20f) : new Color(.74f, .68f, .53f);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Periodic noise avoids seams on the repeated surface texture.
                float u = x * Mathf.PI * 2f / size, v = y * Mathf.PI * 2f / size;
                uint hash = unchecked((uint)x * 374761393u + (uint)y * 668265263u + 17u);
                hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
                hash ^= hash >> 16;
                float grain = (hash & 65535u) / 32767.5f - 1f;
                float broad = Mathf.Sin(u * 3f + Mathf.Cos(v * 2f)) * Mathf.Cos(v * 4f);
                var value = (kind == "road" ? .045f : .022f) * grain + .006f * broad;
                if (kind == "wood")
                {
                    value = .04f * Mathf.Sin(u * 47f + Mathf.Sin(v * 3f));
                    if (y % 32 < 2) value -= .17f;
                    if (x % 128 < 2) value -= .08f;
                }
                pixels[y * size + x] = new Color(baseColor.r + value, baseColor.g + value, baseColor.b + value);
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            if (existing == null) AssetDatabase.CreateAsset(texture, path);
            else EditorUtility.SetDirty(texture);
            return texture;
        }

        public static void Inspect()
        {
            foreach (var path in AssetDatabase.FindAssets("t:Model", new[] { Root })
                .Select(AssetDatabase.GUIDToAssetPath))
            {
                var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                Debug.Log("ARTMODEL " + path);
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    Debug.Log("ARTPART " + r.name + " center=" + r.bounds.center.ToString("F3")
                        + " size=" + r.bounds.size.ToString("F3") + " materials="
                        + string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name)));
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        public static void Build()
        {
            BuildPlayer("Builds/Windows-Circuits/FamilyRacing.exe", BuildOptions.Development);
        }

        public static void BuildOrdinary()
        {
            BuildPlayer("Builds/Windows-Circuits-Ordinary/FamilyRacing.exe", BuildOptions.None);
        }

        public static void BuildBoth()
        {
            Build();
            BuildOrdinary();
        }

        public static void CreateAndBuildBoth()
        {
            Create();
            Build();
            BuildOrdinary();
        }

        private static void BuildPlayer(string locationPathName, BuildOptions options)
        {
            Required<SceneAsset>(ScenePath);
            Validate();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = locationPathName,
                target = BuildTarget.StandaloneWindows64,
                options = options
            });
            Debug.Log("Asset trial build (" + locationPathName + "): " + report.summary.result + ", errors=" + report.summary.totalErrors);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Asset trial player build failed: " + locationPathName);
        }

        public static void Validate()
        {
            var trial = Required<GameObject>(AssetTrialCircuits.PrefabPath);
            AssetTrialCircuits.Validate();
            foreach (var shore in trial.GetComponentsInChildren<Renderer>(true).Where(r => r.name.StartsWith("Skirt_", StringComparison.Ordinal)))
                if (shore.bounds.size.y > 4f)
                    throw new InvalidOperationException("Shoreline exceeds its visual height budget: " + shore.name + " height=" + shore.bounds.size.y);
            Func<GameObject, string[]> collision = go => go.GetComponentsInChildren<Collider>(true)
                .Where(c => c.gameObject.layer != SunnyIslandCourseAuthoring.OccluderLayer)
                .Select(c => c.name + ":" + c.GetType().Name + ":" + c.transform.position.ToString("F4")
                    + ":" + c.transform.lossyScale.ToString("F4") + ":" + c.transform.rotation.ToString("F4")
                    + ":" + (c is BoxCollider box ? box.center.ToString("F4") + box.size.ToString("F4") : "")
                    + ":" + c.isTrigger + ":" + c.enabled).OrderBy(s => s).ToArray();
            var vehicle = Required<VehicleDefinitionAsset>(Output + "/car-01.asset");
            // Unity can sanitize malformed bounds in the loaded Mesh accessor;
            // check the persisted generated data too, before packaging it.
            foreach (var meshPath in Directory.GetFiles(Output + "/Racers", "*.asset"))
            {
                var serialized = File.ReadAllText(meshPath);
                if (serialized.Contains(": NaN") || serialized.Contains(": -Infinity") || serialized.Contains(": Infinity"))
                    throw new InvalidOperationException("Non-finite serialized racer mesh: " + meshPath);
            }
            if (vehicle.visual == null || !vehicle.visual.Validate(out var error))
                throw new InvalidOperationException("Trial car visual invalid");
            var racerRenderers = new System.Collections.Generic.List<Renderer>();
            foreach (var id in RacerVisualAuthoring.CharacterIds)
            {
                var character = Required<CharacterDefinitionAsset>(AssetTrialRacers.CharacterPath(id));
                if (character.characterId != id || !character.Validate(out _))
                    throw new InvalidOperationException("Invalid trial character " + id);
                if (character.prefab.GetComponentsInChildren<Collider>(true).Length != 0)
                    throw new InvalidOperationException("Character has gameplay collision: " + id);
                foreach (var bone in new[] { "Hips", "Spine", "Head", "ArmL", "ArmR", "LegL", "LegR", "Tail" })
                    if (!character.prefab.GetComponentsInChildren<Transform>(true).Any(t => t.name == bone))
                        throw new InvalidOperationException("Missing trial character bone " + id + "/" + bone);
                racerRenderers.AddRange(character.prefab.GetComponentsInChildren<Renderer>(true));
                foreach (var filter in character.prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    for (var sub = 0; sub < filter.sharedMesh.subMeshCount; sub++)
                    {
                        var bounds = filter.sharedMesh.GetSubMesh(sub).bounds;
                        float size = bounds.center.x + bounds.center.y + bounds.center.z + bounds.extents.x + bounds.extents.y + bounds.extents.z;
                        if (float.IsNaN(size) || float.IsInfinity(size))
                            throw new InvalidOperationException("Non-finite character mesh bounds: " + id + "/" + filter.sharedMesh.name);
                    }
                    foreach (var vertex in filter.sharedMesh.vertices)
                    {
                        float sum = vertex.x + vertex.y + vertex.z;
                        if (float.IsNaN(sum) || float.IsInfinity(sum))
                            throw new InvalidOperationException("Non-finite character mesh vertex: " + id + "/" + filter.sharedMesh.name);
                    }
                }
            }
            if (vehicle.visual.prefab.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("Trial kart has gameplay collision");
            foreach (var renderer in trial.GetComponentsInChildren<Renderer>(true)
                .Concat(vehicle.visual.prefab.GetComponentsInChildren<Renderer>(true)).Concat(racerRenderers))
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || mat.shader == null || !mat.shader.isSupported)
                    throw new InvalidOperationException("Invalid material on " + renderer.name);
                if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") is Cubemap)
                    throw new InvalidOperationException("Cubemap on 2D surface: " + mat.name);
            }
            var scene = EditorSceneManager.OpenScene(ScenePath);
            var world = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var hud = world.GetComponent<AssetTrialHud>();
            if (hud == null || hud.portraits == null || hud.portraits.Length != 4 || hud.portraits.Any(p => p == null))
                throw new InvalidOperationException("Trial scene is missing generated racer portraits on AssetTrialHud");
            if (hud.coursePreviews == null || hud.coursePreviews.Length != 3 || hud.coursePreviews.Any(p => p == null))
                throw new InvalidOperationException("Trial scene is missing generated course previews on AssetTrialHud");
            var so = new SerializedObject(world);
            var vehicles = so.FindProperty("vehicles");
            for (var i = 0; i < vehicles.arraySize; i++)
                if (vehicles.GetArrayElementAtIndex(i).objectReferenceValue == null)
                    throw new InvalidOperationException("Trial scene has a missing vehicle");
            if (so.FindProperty("coursePrefab").objectReferenceValue == null)
                throw new InvalidOperationException("Trial scene has no course");
            var characters = so.FindProperty("characters");
            if (characters.arraySize != 4) throw new InvalidOperationException("Trial character roster incomplete");
            for (var i = 0; i < characters.arraySize; i++)
                if (characters.GetArrayElementAtIndex(i).objectReferenceValue != Required<CharacterDefinitionAsset>(
                    AssetTrialRacers.CharacterPath(RacerVisualAuthoring.CharacterIds[i])))
                    throw new InvalidOperationException("Trial scene has a stale character reference");
            AssetTrialCampaign.Validate(so);
            Debug.Log("Asset trial validation passed: closed circuits, vehicle/scene references and materials valid.");
        }

        public static void CreateAndBuild() { Create(); Build(); }
        public static void CreateBuildPreview() { Create(); Build(); AssetTrialCircuits.Preview(); AssetTrialRacers.Preview(); }

        // A staged in-engine art view, separate from the actual driving screenshots.
        public static void Preview()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            foreach (var root in scene.GetRootGameObjects()) root.SetActive(false);
            UnityEngine.Object.Instantiate(Required<GameObject>(Output + "/SunnyIslandTrial.prefab"));
            var light = new GameObject("PreviewSun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = .9f;
            light.color = new Color(1f, .96f, .88f);
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            var car = new GameObject("PreviewRacer").AddComponent<VehiclePresentation>();
            car.SetRacer(Required<CharacterDefinitionAsset>(AssetTrialRacers.CharacterPath("fox")), 0);
            car.SetVehicle(Required<VehicleDefinitionAsset>(Output + "/car-01.asset"));
            car.ApplyPose(new Core.Driving.MotorState { x = 25f, z = 32f, heading = .25f, tick = 1, grounded = true }, 0f);
            var camera = new GameObject("PreviewCamera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.55f, .77f, .85f);
            camera.fieldOfView = 48f;
            camera.transform.position = new Vector3(18f, 5f, 25f);
            camera.transform.LookAt(new Vector3(26f, 2f, 36f));
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var previousPipeline = QualitySettings.renderPipeline;
            var previousActive = RenderTexture.active;
            var rt = new RenderTexture(1280, 720, 24);
            var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                QualitySettings.renderPipeline = Required<UniversalRenderPipelineAsset>(VisualBenchmarkSetup.PcPipelinePath);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                image.Apply();
                Directory.CreateDirectory("docs/qa/asset-trial-2026-10-23");
                File.WriteAllBytes("docs/qa/asset-trial-2026-10-23/staged-art-preview.png", image.EncodeToPNG());
                Debug.Log("Asset trial staged art preview captured.");
            }
            finally
            {
                RenderTexture.active = previousActive;
                QualitySettings.renderPipeline = previousPipeline;
                UnityEngine.Object.DestroyImmediate(image);
                UnityEngine.Object.DestroyImmediate(rt);
                // Discard the temporary composition rather than saving it to the playable scene.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
    }
}
