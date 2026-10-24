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
            // Bundled tutorial prompt recordings live here; only licensed
            // local assets may be committed into it.
            EnsureFolder("Assets/FamilyRacing", "Audio");
            EnsureFolder("Assets/FamilyRacing/Audio", "Tutorial");
            var groundMat = EnsureMaterial("GrayboxGround.mat",
                new Color(0.83f, 0.80f, 0.72f));
            var wallMat = EnsureMaterial("GrayboxWall.mat",
                new Color(0.24f, 0.49f, 0.63f));
            var gateMat = EnsureMaterial("GrayboxGate.mat",
                new Color(0.91f, 0.51f, 0.42f));
            var driftMat = EnsureMaterial("GrayboxDrift.mat",
                new Color(0.36f, 0.36f, 0.40f));

            // Task 4: authored racer visuals/characters must exist before
            // the vehicle definitions bind them; ensure creates missing
            // assets only, so re-runs never clobber authored art.
            RacerVisualAuthoring.Ensure();
            // Task 5: the shared race presentation catalog (effect
            // prefabs + generated WAV bindings) — the driving slice
            // plays the same authored cues the benchmark verifies.
            RacePresentationAuthoring.Ensure();
            var car = EnsureCar();
            var moto = EnsureMotorcycle();
            var track = EnsureTrack();
            var practice = EnsureEvent("practice-02", track, laps: 2);
            var intro = EnsureEvent("intro-01", track, laps: 1);
            // The tutorial free-drive stage: laps are effectively
            // unbounded so a lap finish can never freeze the lesson sim.
            var free = EnsureEvent("tutorial-01", track, laps: 99);
            var tutorial = EnsureTutorial();
            // EnsureTutorial rebuilds the cue slots every run, so the
            // bundled recordings must be re-wired after it. Missing WAVs
            // leave honest null slots — the presenter reports the miss.
            WireTutorialCueClips(tutorial);
            var gadgetCatalog = EnsureGadgetCatalog();
            var course = EnsureCoursePrefab(groundMat, wallMat, gateMat, driftMat);

            WireScene(car, moto, track, practice, intro, free, tutorial,
                gadgetCatalog, course);
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
            // The Core preset is the single source of tuning truth; the
            // generated asset is rewritten from it so the two can never
            // diverge silently.
            asset.ApplyPreset(FamilyRacing.Core.Driving.VehicleTuning.Car01());
            asset.silhouette = "car";
            asset.leanDegrees = 4f;
            asset.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
            asset.bodyColor = new Color(0.91f, 0.51f, 0.42f);
            asset.visual = RacerVisual(true);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // The authored benchmark visual (Task 4): null-safe — a missing
        // asset leaves the proven primitive fallback bound.
        private static VehicleVisualDefinitionAsset RacerVisual(bool car)
        {
            return AssetDatabase.LoadAssetAtPath<VehicleVisualDefinitionAsset>(
                car ? RacerVisualAuthoring.CarVisualAssetPath
                    : RacerVisualAuthoring.MotoVisualAssetPath);
        }

        private static VehicleDefinitionAsset EnsureMotorcycle()
        {
            var asset = EnsureAsset<VehicleDefinitionAsset>("motorcycle-01.asset");
            asset.ApplyPreset(FamilyRacing.Core.Driving.VehicleTuning.Motorcycle01());
            asset.silhouette = "motorcycle";
            asset.leanDegrees = 24f;
            asset.bodyScale = new Vector3(0.9f, 1.4f, 2.2f);
            asset.bodyColor = new Color(0.50f, 0.78f, 0.70f);
            asset.visual = RacerVisual(false);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Graybox loop: spawn south heading north; gates sit at leg
        // midpoints of the rectangle. Gate 0 is the start/finish line and
        // is pre-crossed by the spawn. Each gate carries a safe respawn
        // pose just behind its plane along the travel direction.
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
            // The permitted space for the physical backend's
            // outside-bounds recovery: the whole walled arena plus the
            // ramp's jump airspace, so a designed flight is never a
            // failure. Falling below the slab still hits minCourseY.
            asset.recoveryBounds = new[]
            {
                new TrackDefinitionAsset.BoundsVolume
                {
                    min = new Vector3(-125f, -2f, -85f),
                    max = new Vector3(125f, 30f, 85f)
                }
            };
            // Deterministic respawn fallbacks beside the gate recover
            // points, tried in order when the primary safe pose is
            // occupied by another racer.
            asset.safePoses = new[]
            {
                SafePose(4.5f, -63f, 0f),
                SafePose(-4.5f, -63f, 0f),
                SafePose(5f, 22f, 0f),
                SafePose(52f, 50.5f, 90f),
                SafePose(90.5f, -5f, 180f),
                SafePose(55f, -52f, -90f)
            };
            // M5 Task 5 pickup columns: one per straight so every gate
            // leg passes a box, plus one beside the ramp landing. All
            // sit at road height (y=0) — the vertical band keeps airtime
            // above it clean of collection.
            asset.pickupSites = new[]
            {
                Pickup("pickup-south", 0f, -20f),
                Pickup("pickup-northwest", 0f, 45f),
                Pickup("pickup-northeast", 78f, 55f),
                Pickup("pickup-east", 95f, -28f),
                Pickup("pickup-southeast", 32f, -55f),
                Pickup("pickup-ramp", -60f, 42f)
            };
            EditorUtility.SetDirty(asset);
            return asset;
        }

        private static TrackDefinitionAsset.PickupSite Pickup(
            string id, float x, float z)
        {
            return new TrackDefinitionAsset.PickupSite
            {
                id = id,
                center = new Vector3(x, 0f, z),
                radiusMeters = 2f,
                verticalRangeMeters = 2.5f
            };
        }

        private static TrackDefinitionAsset.SpawnSlot SafePose(
            float x, float z, float headingDegrees)
        {
            return new TrackDefinitionAsset.SpawnSlot
            {
                position = new Vector3(x, 0f, z),
                headingDegrees = headingDegrees
            };
        }

        private static TrackDefinitionAsset.Gate Gate(
            string id, float x, float z, float dirX, float dirZ)
        {
            return new TrackDefinitionAsset.Gate
            {
                id = id,
                center = new Vector3(x, 0f, z),
                halfWidth = 12f,
                direction = new Vector2(dirX, dirZ),
                recoverPosition = new Vector3(x - dirX * 3f, 0f, z - dirZ * 3f),
                recoverHeadingDegrees = Mathf.Atan2(dirX, dirZ) * Mathf.Rad2Deg
            };
        }

        // The authored first-world gadget catalog (M5 Task 5): the
        // balanced pickup defaults — a five-second turbo at 1.75x
        // acceleration/1.50x cap, a one-charge bubble shield, and a
        // 3 m/180-tick foam trap slowing rivals to 0.70x for 45 ticks
        // with 60 ticks of post-hit immunity. The pickup respawn is
        // 300 ticks at one held slot. Re-runs rewrite the rows to the
        // contract so the asset can never drift silently.
        private static GadgetCatalogAsset EnsureGadgetCatalog()
        {
            var asset = EnsureAsset<GadgetCatalogAsset>(
                "gadget-catalog-01.asset");
            asset.pickupRespawnTicks = 300;
            asset.heldSlotCapacity = 1;
            asset.gadgets = new[]
            {
                new GadgetCatalogAsset.GadgetRow
                {
                    gadgetId = "turbo-01", kind = "turbo",
                    durationTicks = 150,
                    accelerationMultiplier = 1.75f,
                    speedCapMultiplier = 1.50f
                },
                new GadgetCatalogAsset.GadgetRow
                {
                    gadgetId = "bubble-shield-01", kind = "bubble-shield",
                    charges = 1
                },
                new GadgetCatalogAsset.GadgetRow
                {
                    gadgetId = "foam-trap-01", kind = "foam-trap",
                    patchRadiusMeters = 3f, patchLifetimeTicks = 180,
                    slowMultiplier = 0.70f, slowDurationTicks = 45,
                    immunityTicks = 60
                }
            };
            EditorUtility.SetDirty(asset);
            return asset;
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

        // The ordered onboarding: steering -> brake -> drift -> boost ->
        // jump -> recovery, then the intro-01 one-lap trial. Completion
        // rules are authored here as content; clip slots stay empty until
        // licensed EN/HE recordings are bundled under
        // Assets/FamilyRacing/Audio/Tutorial.
        private static TutorialDefinitionAsset EnsureTutorial()
        {
            // Distinct filename from the tutorial-01 free-drive *event*
            // asset — same-path assets of different ScriptableObject types
            // would overwrite each other.
            var asset = EnsureAsset<TutorialDefinitionAsset>("tutorial-def-01.asset");
            asset.tutorialId = "tutorial-01";
            asset.freeDriveEventId = "tutorial-01";
            asset.trialEventId = "intro-01";
            asset.lessons = new[]
            {
                Lesson("steering", "steerTicks", "", 0, 45, 0.3f,
                    "tutorial.cue.steering", "◀▶"),
                // Brief held-brake lesson between steering and drift:
                // ~0.8s of real brake demand teaches slowing before the
                // sliding lessons arrive.
                new TutorialDefinitionAsset.Lesson
                {
                    lessonId = "brake",
                    rule = "brakeTicks",
                    requiredBrakeTicks = 25,
                    minBrake = 0.5f,
                    cue = Cue("tutorial.cue.brake", "▼")
                },
                Lesson("drift", "event", "motor.driftStart", 1, 0, 0f,
                    "tutorial.cue.drift", "∿"),
                Lesson("boost", "event", "motor.boostStart", 1, 0, 0f,
                    "tutorial.cue.boost", "»"),
                Lesson("jump", "event", "motor.landed", 1, 0, 0f,
                    "tutorial.cue.jump", "⌃"),
                Lesson("recovery", "event", "race.recovered", 1, 0, 0f,
                    "tutorial.cue.recovery", "↻"),
                // M5 Task 5 gadget lesson: the child drives through one
                // authored pickup column and spends the granted item —
                // completion counts the real authoritative
                // "gadget.used" event, exactly like every other
                // event-rule lesson. On a course without pickup sites
                // the lesson never blocks graduation: the rules emit
                // no gadget events there, so keep it LAST so earlier
                // lessons cannot be skipped by a dead affordance.
                Lesson("gadget", "event", "gadget.used", 1, 0, 0f,
                    "tutorial.cue.gadget", "◆")
            };
            asset.trialCue = Cue("tutorial.cue.trial", "★");
            asset.finishedCue = Cue("tutorial.done", "★");
            EditorUtility.SetDirty(asset);
            return asset;
        }

        private static TutorialDefinitionAsset.Lesson Lesson(string id,
            string rule, string eventKind, int requiredCount, int steerTicks,
            float minSteering, string textKey, string icon)
        {
            return new TutorialDefinitionAsset.Lesson
            {
                lessonId = id,
                rule = rule,
                eventKind = eventKind,
                requiredCount = requiredCount > 0 ? requiredCount : 1,
                requiredSteerTicks = steerTicks > 0 ? steerTicks : 30,
                minSteering = minSteering > 0f ? minSteering : 0.25f,
                cue = Cue(textKey, icon)
            };
        }

        private static TutorialDefinitionAsset.Cue Cue(string textKey, string icon)
        {
            return new TutorialDefinitionAsset.Cue
            {
                textKey = textKey,
                icon = icon
            };
        }

        // Bundled recordings follow cue-<id>-<locale>.wav under
        // Assets/FamilyRacing/Audio/Tutorial — produced offline by
        // Tools/Generate-TutorialCues.py (Azure Speech, authoring time
        // only; see the audio README for the regeneration runbook).
        private static void WireTutorialCueClips(TutorialDefinitionAsset asset)
        {
            if (asset.lessons != null)
                foreach (var lesson in asset.lessons)
                    if (lesson != null) WireCue(lesson.cue, lesson.lessonId);
            WireCue(asset.trialCue, "trial");
            WireCue(asset.finishedCue, "finished");
        }

        private static void WireCue(TutorialDefinitionAsset.Cue cue, string cueId)
        {
            if (cue == null) return;
            cue.english = AssetDatabase.LoadAssetAtPath<AudioClip>(
                "Assets/FamilyRacing/Audio/Tutorial/cue-" + cueId + "-en.wav");
            cue.hebrew = AssetDatabase.LoadAssetAtPath<AudioClip>(
                "Assets/FamilyRacing/Audio/Tutorial/cue-" + cueId + "-he.wav");
        }

        // The course prefab is pure graybox: one ground slab, perimeter
        // walls, chicane barriers, one landmark ramp, two island blocks
        // that each split the route into a longer main branch and an
        // optional shortcut branch (both rejoin before the next mandatory
        // checkpoint), a flat drift-zone pad, and trigger gate volumes.
        // Gate/drift colliders are triggers so the motor's probe/sweep
        // (which ignores triggers) only ever collides with solid geometry.
        // The prefab is regenerated in place so authored layout changes
        // flow through; saving over the same path keeps its GUID.
        private static GameObject EnsureCoursePrefab(
            Material groundMat, Material wallMat, Material gateMat, Material driftMat)
        {
            var path = DrivingDir + "/GrayboxCourse.prefab";
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
                // Landmark ramp west of the loop with a forgiving flat
                // landing area around it.
                Box(root, "Ramp", new Vector3(-60f, 1.2f, 30f),
                    new Vector3(12f, 0.6f, 16f),
                    Quaternion.Euler(-14f, 0f, 0f), wallMat);
                // Islands block the straight gate-to-gate lines; each
                // leaves a longer main branch and a shorter inside branch
                // that both rejoin before the next mandatory checkpoint.
                Box(root, "IslandNorthEast", new Vector3(32f, 1f, 44f),
                    new Vector3(22f, 2f, 14f), Quaternion.identity, wallMat);
                Box(root, "IslandSouthEast", new Vector3(72f, 1f, -32f),
                    new Vector3(20f, 2f, 16f), Quaternion.identity, wallMat);

                // Drift zone: a flat visual-only pad over the wide
                // north-east sweep. Trigger so it never affects physics.
                var pad = Box(root, "DriftZone", new Vector3(45f, 0.02f, 66f),
                    new Vector3(40f, 0.04f, 22f), Quaternion.identity, driftMat);
                pad.GetComponent<Collider>().isTrigger = true;

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
            Object practice, Object intro, Object free, Object tutorial,
            Object gadgetCatalog, GameObject course)
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
                dirty |= AssignCharacters(serialized);
                dirty |= Assign(serialized, "track", track);
                dirty |= AssignArray(serialized, "events", practice, intro, free);
                dirty |= Assign(serialized, "tutorial", tutorial);
                // M5 Task 5: the authored gadget catalog feeds
                // practice/tutorial free-drive and LAN hosts; the
                // gameplay catalog reference stays optional separately.
                dirty |= Assign(serialized, "gadgetCatalog", gadgetCatalog);
                dirty |= Assign(serialized, "presentationCatalog",
                    AssetDatabase.LoadAssetAtPath<RacePresentationCatalogAsset>(
                        RacePresentationAuthoring.CatalogPath));
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

        // The four authored animals in slot order — the roster's AI
        // fallback cosmetics map slot N to catalog entry N.
        private static bool AssignCharacters(SerializedObject so)
        {
            var characters = new CharacterDefinitionAsset[
                RacerVisualAuthoring.CharacterIds.Length];
            for (var i = 0; i < characters.Length; i++)
                characters[i] = AssetDatabase
                    .LoadAssetAtPath<CharacterDefinitionAsset>(
                        RacerVisualAuthoring.CharacterAssetPath(
                            RacerVisualAuthoring.CharacterIds[i]));
            return AssignArray(so, "characters", characters);
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
