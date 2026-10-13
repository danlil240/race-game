using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace FamilyRacing.EditorTools
{
    // Repeatable, non-destructive visual-benchmark setup (M4 Task 1):
    // creates the benchmark definition asset and the separate
    // VisualBenchmark scene under Assets/FamilyRacing/Generated/
    // VisualBenchmark, then keeps their contract references valid. The
    // scene binds the shared graybox content — same course, gates,
    // route, recovery, tunings and event entries — under the distinct
    // "visual-1" content identity, so a benchmark player can never
    // silently join a graybox LAN race. Safe to re-run: missing
    // scaffolding is created and contract references are repaired, but
    // authored meshes, materials, scene dressing and baked lighting are
    // never regenerated or overwritten.
    public static class VisualBenchmarkSetup
    {
        public const string BenchmarkDir =
            "Assets/FamilyRacing/Generated/VisualBenchmark";
        public const string DefinitionPath =
            BenchmarkDir + "/visual-benchmark-01.asset";
        public const string ScenePath = BenchmarkDir + "/VisualBenchmark.unity";
        public const string PresentationVersion = "visual-1";
        // M4 Task 2 render profiles: the A7 Lite tablet pipeline (0.75
        // internal render scale, simple shadows, restrained cost) and the
        // reference-PC pipeline (full scale, richer shadows/detail). The
        // benchmark scene's RenderProfileController selects between them at
        // startup; neither enables real-time reflections, motion blur or
        // full-screen effects, and UIToolkit stays at display resolution.
        public const string TabletPipelinePath =
            BenchmarkDir + "/VisualBenchmarkTabletURP.asset";
        public const string TabletRendererPath =
            BenchmarkDir + "/VisualBenchmarkTabletRenderer.asset";
        public const string PcPipelinePath =
            BenchmarkDir + "/VisualBenchmarkPcURP.asset";
        public const string PcRendererPath =
            BenchmarkDir + "/VisualBenchmarkPcRenderer.asset";
        private const string DrivingDir =
            "Assets/FamilyRacing/Generated/Driving";
        private const string TrackPath = DrivingDir + "/graybox-01.asset";
        private const string GrayboxCoursePath =
            DrivingDir + "/GrayboxCourse.prefab";
        private const string PanelSettingsPath =
            "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";
        private const string PanelProperty = "m_PanelSettings";

        public static void Create()
        {
            EnsureFolder("Assets/FamilyRacing/Generated", "VisualBenchmark");
            // M4 Task 3: the Sunny Island course visual is authored
            // content under Art/VisualBenchmark — created only when
            // missing, never regenerated (same rule as every other
            // authored asset here).
            SunnyIslandCourseAuthoring.Ensure();
            var definition = EnsureDefinition();
            string error;
            if (!definition.Validate(out error))
                throw new InvalidDataException(
                    "VisualBenchmarkSetup: " + error);
            EnsureRenderProfiles();
            EnsureScene();
            AssetDatabase.SaveAssets();
        }

        // The two authored profile pipelines. Existing assets keep authored
        // tuning — only missing assets are created — so hand-tuned profile
        // work survives a re-run like every other authored asset here.
        private static void EnsureRenderProfiles()
        {
            EnsurePipeline(TabletPipelinePath, TabletRendererPath,
                renderScale: 0.75f, shadowCascades: 1, shadowDistance: 30f,
                softShadows: false, hdr: false, msaa: 1);
            EnsurePipeline(PcPipelinePath, PcRendererPath,
                renderScale: 1.0f, shadowCascades: 2, shadowDistance: 80f,
                softShadows: true, hdr: true, msaa: 2);
        }

        private static void EnsurePipeline(string pipelinePath,
            string rendererPath, float renderScale, int shadowCascades,
            float shadowDistance, bool softShadows, bool hdr, int msaa)
        {
            if (AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                    pipelinePath) != null)
                return;
            var renderer = AssetDatabase
                .LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject
                    .CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.renderScale = renderScale;
            pipeline.shadowCascadeCount = shadowCascades;
            pipeline.shadowDistance = shadowDistance;
            pipeline.supportsHDR = hdr;
            pipeline.msaaSampleCount = msaa;
            // Simple vehicle shadows on the tablet and bounded cascades on
            // PC; no depth/opaque grabs or post-heavy features in either.
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            AssetDatabase.CreateAsset(pipeline, pipelinePath);
            // URP 17 derives supportsSoftShadows from the serialized
            // ShadowQuality enum — the public setter is internal, so soft
            // shadows are authored through the serialized property.
            var so = new SerializedObject(pipeline);
            var shadowType = so.FindProperty("m_ShadowType");
            if (shadowType != null)
            {
                shadowType.intValue = softShadows
                    ? (int)UnityEngine.Rendering.Universal.ShadowQuality.SoftShadows
                    : (int)UnityEngine.Rendering.Universal.ShadowQuality.HardShadows;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void EnsureFolder(string parent, string name)
        {
            var path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        // The definition is authored content: create it if missing and
        // fill empty slots with the shared defaults, but never rewrite a
        // bound value — the sole exception is the documented one-time
        // migration of the Task-1 graybox placeholder onto the Task-3
        // Sunny Island course visual (which nests the same graybox
        // collision, so gameplay geometry never moves).
        private static VisualBenchmarkDefinitionAsset EnsureDefinition()
        {
            var asset = AssetDatabase.LoadAssetAtPath
                <VisualBenchmarkDefinitionAsset>(DefinitionPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance
                    <VisualBenchmarkDefinitionAsset>();
                asset.presentationVersion = PresentationVersion;
                AssetDatabase.CreateAsset(asset, DefinitionPath);
            }
            var dirty = false;
            if (asset.sourceTrack == null)
            {
                asset.sourceTrack =
                    AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(
                        TrackPath);
                dirty = true;
            }
            if (asset.courseVisualPrefab == null)
            {
                // Prefer the authored Sunny Island course visual; the
                // shared graybox prefab stays the documented fallback
                // for a workspace where Task-3 art is absent.
                asset.courseVisualPrefab =
                    AssetDatabase.LoadAssetAtPath<GameObject>(
                        SunnyIslandCourseAuthoring.CourseVisualPath);
                if (asset.courseVisualPrefab == null)
                    asset.courseVisualPrefab =
                        AssetDatabase.LoadAssetAtPath<GameObject>(
                            GrayboxCoursePath);
                dirty = true;
            }
            else if (asset.courseVisualPrefab ==
                AssetDatabase.LoadAssetAtPath<GameObject>(GrayboxCoursePath))
            {
                // One-time Task-3 migration: the placeholder graybox
                // binding moves to the Sunny Island visual root once
                // that prefab exists (the nested graybox instance inside
                // it keeps identical collision — dressing never moves
                // gameplay geometry).
                var sunny = AssetDatabase.LoadAssetAtPath<GameObject>(
                    SunnyIslandCourseAuthoring.CourseVisualPath);
                if (sunny != null)
                {
                    asset.courseVisualPrefab = sunny;
                    dirty = true;
                }
            }
            if (dirty) EditorUtility.SetDirty(asset);
            return asset;
        }

        // Every reference the benchmark scene's DrivingWorld needs —
        // identical to the graybox slice's wiring. A missing asset fails
        // the build honestly instead of producing partial content; run
        // FoundationProjectSetup first to generate the shared content.
        private sealed class SceneContent
        {
            internal PanelSettings panel;
            internal VehicleDefinitionAsset car;
            internal VehicleDefinitionAsset moto;
            internal EventDefinitionAsset[] events;
            internal TutorialDefinitionAsset tutorial;
        }

        private static SceneContent LoadSceneContent()
        {
            var content = new SceneContent
            {
                panel = Required<PanelSettings>(PanelSettingsPath),
                car = Required<VehicleDefinitionAsset>(
                    DrivingDir + "/car-01.asset"),
                moto = Required<VehicleDefinitionAsset>(
                    DrivingDir + "/motorcycle-01.asset"),
                tutorial = Required<TutorialDefinitionAsset>(
                    DrivingDir + "/tutorial-def-01.asset")
            };
            var events = new List<EventDefinitionAsset>();
            foreach (var id in new[]
            {
                "practice-02", "intro-01", "tutorial-01",
                "lan-car-01", "lan-motorcycle-01"
            })
                events.Add(Required<EventDefinitionAsset>(
                    DrivingDir + "/" + id + ".asset"));
            content.events = events.ToArray();
            return content;
        }

        private static T Required<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidDataException("VisualBenchmarkSetup: "
                    + path + " is missing — run FoundationProjectSetup "
                    + "first to generate the shared driving content.");
            return asset;
        }

        // Every reference used for scene wiring is (re)loaded INSIDE the
        // scene step, after the last NewScene/OpenScene call: replacing
        // or opening scenes can unload assets held only by managed
        // references, which would silently serialize as {fileID: 0}.
        private static VisualBenchmarkDefinitionAsset LoadValidatedDefinition()
        {
            var definition = AssetDatabase.LoadAssetAtPath
                <VisualBenchmarkDefinitionAsset>(DefinitionPath);
            if (definition == null)
                throw new InvalidDataException("VisualBenchmarkSetup: "
                    + DefinitionPath + " is missing.");
            string error;
            if (!definition.Validate(out error))
                throw new InvalidDataException(
                    "VisualBenchmarkSetup: " + error);
            return definition;
        }

        // The benchmark bootstrap: the same self-bootstrapping stack the
        // graybox scene carries (FoundationApp + input + shell +
        // DrivingWorld) bound to the shared content under the "visual-1"
        // identity. An existing scene is authored state — only contract
        // references are repaired in place, exactly like
        // DrivingPrototypeSetup.WireScene / NetworkPrototypeSetup's LAN
        // event merge.
        private static void EnsureScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)
                    != null)
            {
                RepairScene();
                return;
            }
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            lightObject.transform.rotation =
                Quaternion.Euler(50f, -30f, 0f);

            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            // Sunny-sky placeholder until Task 3 authors the island
            // dressing and lighting.
            camera.backgroundColor = new Color(0.53f, 0.81f, 0.92f);
            cameraObject.tag = "MainCamera";

            // Load after NewScene: scene replacement can unload assets
            // referenced only from managed objects.
            var definition = LoadValidatedDefinition();
            CreateAppObject(definition, LoadSceneContent());
            EnsureSunnyIslandLighting(scene);

            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        // ---------------------------------------------------------------
        // M4 Task-3 Sunny Island scene lighting: the warm sun, turquoise
        // ambient/skybox and restrained horizon haze belong to the
        // benchmark scene, never the course prefab — the prefab stays
        // reusable under any sky. Applied only while the scene still
        // carries the Task-1 scaffold lighting (no fog, skybox ambient,
        // the built-in default skybox, a scaffold-named/tuned light), so
        // later hand-authored lighting is never rewritten.
        private const string SkyboxPath =
            SunnyIslandCourseAuthoring.MaterialsDir + "/Sky_SunnyIsland.mat";
        private static readonly Color ScaffoldSunColor =
            new Color(1f, 0.96f, 0.88f);

        // Returns true when the scaffold lighting was migrated onto the
        // Sunny Island treatment (the caller marks the scene dirty).
        private static bool EnsureSunnyIslandLighting(Scene scene)
        {
            var changed = false;
            var prevActive = SceneManager.GetActiveScene();
            if (prevActive != scene)
                SceneManager.SetActiveScene(scene);
            try
            {
                // Unity 6 carries the skybox material on the camera's
                // Skybox component — RenderSettings no longer holds it.
                Camera sceneCam = null;
                Skybox sceneSkybox = null;
                foreach (var rootObj in scene.GetRootGameObjects())
                foreach (var cam in rootObj
                    .GetComponentsInChildren<Camera>(true))
                {
                    if (sceneCam == null) sceneCam = cam;
                    if (sceneSkybox == null)
                        sceneSkybox = cam.GetComponent<Skybox>();
                }
                var authoredSky = sceneSkybox != null
                    && sceneSkybox.material != null
                    && sceneSkybox.material.name != "Default-Skybox";
                if (RenderSettings.fog
                    || RenderSettings.ambientMode != AmbientMode.Skybox
                    || authoredSky)
                    return false; // authored lighting — never rewritten

                var skyMat = EnsureSkyboxMaterial();
                if (sceneCam != null)
                {
                    if (sceneSkybox == null)
                        sceneSkybox =
                            sceneCam.gameObject.AddComponent<Skybox>();
                    sceneSkybox.material = skyMat;
                    sceneCam.clearFlags = CameraClearFlags.Skybox;
                }
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor =
                    new Color(0.55f, 0.80f, 0.88f);   // turquoise sky
                RenderSettings.ambientEquatorColor =
                    new Color(0.92f, 0.87f, 0.72f);   // warm horizon
                RenderSettings.ambientGroundColor =
                    new Color(0.58f, 0.47f, 0.32f);   // sand bounce
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = new Color(0.66f, 0.87f, 0.89f);
                RenderSettings.fogStartDistance = 130f;
                RenderSettings.fogEndDistance = 360f;

                foreach (var rootObj in scene.GetRootGameObjects())
                foreach (var light in rootObj
                    .GetComponentsInChildren<Light>(true))
                {
                    if (light.type != LightType.Directional) continue;
                    var c = light.color;
                    var scaffoldLight =
                        light.name == "Directional Light"
                        && Quaternion.Angle(light.transform.rotation,
                            Quaternion.Euler(50f, -30f, 0f)) < 0.5f
                        && Mathf.Abs(c.r - ScaffoldSunColor.r) < 0.02f
                        && Mathf.Abs(c.g - ScaffoldSunColor.g) < 0.02f
                        && Mathf.Abs(c.b - ScaffoldSunColor.b) < 0.02f;
                    if (scaffoldLight)
                    {
                        light.name = "Sun";
                        light.color = new Color(1f, 0.91f, 0.74f);
                        light.intensity = 1.15f;
                        light.transform.rotation =
                            Quaternion.Euler(42f, -38f, 0f);
                    }
                    if (RenderSettings.sun == null)
                        RenderSettings.sun = light;
                }
                changed = true;
            }
            finally
            {
                if (prevActive.IsValid() && prevActive != scene)
                    SceneManager.SetActiveScene(prevActive);
            }
            return changed;
        }

        private static Material EnsureSkyboxMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
            if (mat == null)
            {
                var shader = Shader.Find("Skybox/Procedural");
                if (shader == null)
                    throw new InvalidDataException(
                        "VisualBenchmarkSetup: Skybox/Procedural shader "
                        + "is unavailable.");
                mat = new Material(shader) { name = "Sky_SunnyIsland" };
                AssetDatabase.CreateAsset(mat, SkyboxPath);
            }
            mat.SetFloat("_SunSize", 0.06f);
            mat.SetFloat("_SunSizeConvergence", 4.2f);
            mat.SetFloat("_AtmosphereThickness", 0.82f);
            mat.SetColor("_SkyTint", new Color(0.45f, 0.72f, 0.78f));
            mat.SetColor("_GroundColor", new Color(0.78f, 0.68f, 0.5f));
            mat.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static GameObject CreateAppObject(
            VisualBenchmarkDefinitionAsset definition,
            SceneContent content)
        {
            var appObject = new GameObject("VisualBenchmarkApp");
            appObject.AddComponent<FoundationApp>();
            appObject.AddComponent<FoundationInput>();
            var document = appObject.AddComponent<UIDocument>();
            AssignPanel(document, content.panel);
            appObject.AddComponent<FoundationShell>();
            WireRenderProfiles(appObject);
            var world = appObject.AddComponent<DrivingWorld>();
            var serialized = new SerializedObject(world);
            Assign(serialized, "track", definition.sourceTrack);
            Assign(serialized, "coursePrefab",
                definition.courseVisualPrefab);
            Assign(serialized, "tutorial", content.tutorial);
            AssignArray(serialized, "vehicles",
                content.car, content.moto);
            AssignArray(serialized, "events", content.events);
            var version = serialized.FindProperty("contentVersion");
            if (version != null)
                version.stringValue = definition.presentationVersion;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return appObject;
        }

        // Existing scene: inspect, don't trust. A missing FoundationApp
        // composition is missing scaffolding — create it. A present one
        // gets its contract references repaired in place: track,
        // coursePrefab and contentVersion follow the authored
        // definition; vehicles/events gain missing entries without
        // reordering authored ones; a missing panel binding is fixed.
        private static void RepairScene()
        {
            var alreadyOpen = false;
            var scene = new Scene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (open.path == ScenePath)
                { scene = open; alreadyOpen = true; break; }
            }
            if (!alreadyOpen)
                scene = EditorSceneManager.OpenScene(
                    ScenePath, OpenSceneMode.Additive);
            // Load after the scene is open — the same unload hazard as
            // the create path applies to objects referenced only from
            // managed locals.
            var definition = LoadValidatedDefinition();
            var content = LoadSceneContent();
            var dirty = false;
            var found = false;
            foreach (var rootObj in scene.GetRootGameObjects())
            foreach (var app in rootObj
                .GetComponentsInChildren<FoundationApp>(true))
            {
                found = true;
                var world = app.GetComponent<DrivingWorld>();
                if (world == null)
                {
                    world = app.gameObject.AddComponent<DrivingWorld>();
                    dirty = true;
                }
                var serialized = new SerializedObject(world);
                dirty |= Assign(serialized, "track",
                    definition.sourceTrack);
                dirty |= Assign(serialized, "coursePrefab",
                    definition.courseVisualPrefab);
                dirty |= Assign(serialized, "tutorial", content.tutorial);
                dirty |= EnsureArrayContains(serialized, "vehicles",
                    content.car, content.moto);
                dirty |= EnsureArrayContains(serialized, "events",
                    content.events);
                var version = serialized.FindProperty("contentVersion");
                if (version != null
                    && version.stringValue != definition.presentationVersion)
                {
                    version.stringValue = definition.presentationVersion;
                    dirty = true;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                dirty |= WireRenderProfiles(app.gameObject);
                foreach (var document in app
                    .GetComponentsInChildren<UIDocument>(true))
                {
                    if (document.panelSettings == content.panel) continue;
                    AssignPanel(document, content.panel);
                    dirty = true;
                }
            }
            if (!found)
            {
                CreateAppObject(definition, content);
                dirty = true;
            }
            dirty |= EnsureSunnyIslandLighting(scene);
            if (dirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            if (!alreadyOpen)
                EditorSceneManager.CloseScene(scene, true);
        }

        // The scene's profile selector: add the component if missing and
        // keep its pipeline references bound to the two authored profile
        // assets. Returning true marks the scene dirty (repair path).
        private static bool WireRenderProfiles(GameObject appObject)
        {
            var controller = appObject.GetComponent<RenderProfileController>();
            var dirty = false;
            if (controller == null)
            {
                controller = appObject.AddComponent<RenderProfileController>();
                dirty = true;
            }
            var serialized = new SerializedObject(controller);
            dirty |= Assign(serialized, "tabletPipeline",
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                    TabletPipelinePath));
            dirty |= Assign(serialized, "pcPipeline",
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                    PcPipelinePath));
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return dirty;
        }

        // Serialized assignment: the property setter alone was observed
        // to leave m_PanelSettings at fileID 0 in saved scenes.
        private static void AssignPanel(UIDocument document,
            PanelSettings panel)
        {
            var serialized = new SerializedObject(document);
            var property = serialized.FindProperty(PanelProperty);
            if (property != null)
            {
                property.objectReferenceValue = panel;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                document.panelSettings = panel;
            }
        }

        private static bool Assign(SerializedObject so, string property,
            Object value)
        {
            var p = so.FindProperty(property);
            if (p == null || p.objectReferenceValue == value) return false;
            p.objectReferenceValue = value;
            return true;
        }

        private static void AssignArray(SerializedObject so,
            string property, params Object[] values)
        {
            var p = so.FindProperty(property);
            if (p == null || !p.isArray) return;
            p.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue =
                    values[i];
        }

        // Compact null slots out, then append missing expected entries
        // without reordering authored ones — the same merge spirit as
        // NetworkPrototypeSetup's LAN event merge, plus null repair: an
        // empty slot is broken scaffolding, not authored content.
        private static bool EnsureArrayContains(SerializedObject so,
            string property, params Object[] expected)
        {
            var p = so.FindProperty(property);
            if (p == null || !p.isArray) return false;
            var merged = new List<Object>();
            for (var i = 0; i < p.arraySize; i++)
            {
                var item = p.GetArrayElementAtIndex(i)
                    .objectReferenceValue;
                if (item != null) merged.Add(item);
            }
            foreach (var item in expected)
                if (item != null && !merged.Contains(item))
                    merged.Add(item);
            var same = merged.Count == p.arraySize;
            if (same)
                for (var i = 0; i < merged.Count; i++)
                    if (p.GetArrayElementAtIndex(i)
                            .objectReferenceValue != merged[i])
                    {
                        same = false;
                        break;
                    }
            if (same) return false;
            p.arraySize = merged.Count;
            for (var i = 0; i < merged.Count; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue =
                    merged[i];
            return true;
        }
    }
}
