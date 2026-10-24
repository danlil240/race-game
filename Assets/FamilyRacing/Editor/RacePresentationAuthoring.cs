using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // M4 Task-5 race presentation authoring: generates the pooled effect
    // prefabs (procedural sprites/materials — original, US$0, per the
    // Sunny Island art guide) under Art/VisualBenchmark/Effects and binds
    // the committed generated WAVs under Audio/VisualBenchmark into the
    // race presentation catalog the runtime presenters consume.
    //
    // Idempotent: missing assets are created, catalog fields are
    // refreshed to the code contract (the catalog is authored data that
    // must match the presenters), authored prefab layout is never
    // regenerated. Audio files themselves come from
    // Tools/Generate-RaceAudio.py — a clip that is absent on disk stays
    // an honest empty slot on the catalog (the presenters then report
    // missing instead of playing).
    public static class RacePresentationAuthoring
    {
        public const string EffectsDir =
            "Assets/FamilyRacing/Art/VisualBenchmark/Effects";
        public const string AudioDir =
            "Assets/FamilyRacing/Audio/VisualBenchmark";
        public const string CatalogPath =
            "Assets/FamilyRacing/Generated/VisualBenchmark/"
            + "race-presentation-01.asset";
        public const string CatalogId = "race-presentation-01";

        [MenuItem("Family Racing/Author Race Presentation")]
        public static void AuthorMenu()
        {
            Ensure();
            AssetDatabase.SaveAssets();
        }

        public static void Ensure()
        {
            EnsureFolder("Assets/FamilyRacing/Art", "VisualBenchmark");
            EnsureFolder("Assets/FamilyRacing/Art/VisualBenchmark", "Effects");
            EnsureFolder("Assets/FamilyRacing/Audio", "VisualBenchmark");
            EnsureFolder("Assets/FamilyRacing/Generated", "VisualBenchmark");
            var mats = EnsureMaterials();
            var prefabs = EnsureEffectPrefabs(mats);
            EnsureCatalog(prefabs);
        }

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name))
                AssetDatabase.CreateFolder(parent, name);
        }

        // ---------------------------------------------------------------
        // Sprite textures: a soft radial puff and a four-point sparkle,
        // authored in code so the pack is fully procedural.
        private static Texture2D EnsureTexture(string name,
            System.Func<int, int, Color> shade)
        {
            var path = EffectsDir + "/" + name + ".png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32,
                false);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                texture.SetPixel(x, y, shade(x, y));
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
            Object.DestroyImmediate(texture);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D PuffTexture()
        {
            return EnsureTexture("tex-fx-puff", (x, y) =>
            {
                var dx = (x - 31.5f) / 31.5f;
                var dy = (y - 31.5f) / 31.5f;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                // Soft edge with a slightly denser core — a toy dust puff.
                var a = Mathf.Clamp01(1f - Mathf.SmoothStep(0f, 1f,
                    (r - 0.25f) / 0.75f));
                return new Color(1f, 1f, 1f, a);
            });
        }

        private static Texture2D SparkleTexture()
        {
            return EnsureTexture("tex-fx-sparkle", (x, y) =>
            {
                var dx = Mathf.Abs(x - 31.5f) / 31.5f;
                var dy = Mathf.Abs(y - 31.5f) / 31.5f;
                // Four-point star: thin diamonds on the axes.
                var diamond = Mathf.Clamp01(1f - (dx + dy) * 1.4f);
                var core = Mathf.Clamp01(1f - (dx * dx + dy * dy) * 3f);
                var axis = Mathf.Clamp01(1f - Mathf.Min(dx, dy) * 6f)
                    * Mathf.Clamp01(1f - Mathf.Max(dx, dy));
                return new Color(1f, 1f, 1f,
                    Mathf.Max(diamond, Mathf.Max(core * 0.8f, axis)));
            });
        }

        // ---------------------------------------------------------------
        // URP unlit transparent materials — the presenters drive the fade
        // through the _BaseColor alpha channel via MaterialPropertyBlock.
        private static Dictionary<string, Material> EnsureMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new InvalidDataException(
                    "RacePresentationAuthoring: URP/Unlit shader missing");
            var mats = new Dictionary<string, Material>();
            var puff = PuffTexture();
            var sparkle = SparkleTexture();
            Define(mats, shader, "Mat_FxDust", puff,
                new Color(0.93f, 0.85f, 0.66f), additive: false);
            Define(mats, shader, "Mat_FxFlare", puff,
                new Color(1f, 0.72f, 0.30f), additive: true);
            Define(mats, shader, "Mat_FxPuff", puff,
                new Color(0.98f, 0.96f, 0.88f), additive: false);
            Define(mats, shader, "Mat_FxSparkle", sparkle,
                new Color(1f, 0.88f, 0.35f), additive: true);
            return mats;
        }

        private static void Define(Dictionary<string, Material> mats,
            Shader shader, string name, Texture2D texture, Color color,
            bool additive)
        {
            var path = EffectsDir + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            mat.SetColor("_BaseColor", color);
            mat.SetTexture("_BaseMap", texture);
            mat.SetFloat("_Surface", 1f);                    // transparent
            mat.SetFloat("_Blend", additive ? 2f : 0f);      // add / alpha
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            mats[name] = mat;
            EditorUtility.SetDirty(mat);
        }

        // ---------------------------------------------------------------
        // Effect prefabs: a flat-lying quad for ground puffs, a
        // camera-facing quad for flares/sparkles, a small quad fan for the
        // finish burst. No colliders ever — render-only decoration.
        private static Dictionary<string, GameObject> EnsureEffectPrefabs(
            Dictionary<string, Material> mats)
        {
            var prefabs = new Dictionary<string, GameObject>();
            prefabs["drift-dust"] = EnsureQuadPrefab("fx-drift-dust",
                mats["Mat_FxDust"], flat: true, size: 1.6f);
            prefabs["boost-flare"] = EnsureQuadPrefab("fx-boost-flare",
                mats["Mat_FxFlare"], flat: false, size: 1.2f);
            prefabs["landing-puff"] = EnsureQuadPrefab("fx-landing-puff",
                mats["Mat_FxPuff"], flat: true, size: 2.0f);
            prefabs["wall-shunt"] = EnsureQuadPrefab("fx-wall-shunt",
                mats["Mat_FxPuff"], flat: false, size: 1.1f);
            prefabs["checkpoint-sparkle"] = EnsureQuadPrefab(
                "fx-checkpoint-sparkle", mats["Mat_FxSparkle"],
                flat: false, size: 1.4f);
            prefabs["finish-burst"] = EnsureBurstPrefab(mats["Mat_FxSparkle"]);
            return prefabs;
        }

        private static GameObject EnsureQuadPrefab(string name,
            Material material, bool flat, float size)
        {
            var path = EffectsDir + "/" + name + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var root = new GameObject(name);
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Quad";
            quad.transform.SetParent(root.transform, false);
            quad.transform.localRotation = flat
                ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
            quad.transform.localPosition = flat
                ? new Vector3(0f, 0.06f, 0f) : Vector3.zero;
            quad.transform.localScale = Vector3.one * size;
            var collider = quad.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            try
            {
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject EnsureBurstPrefab(Material sparkle)
        {
            var path = EffectsDir + "/fx-finish-burst.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var root = new GameObject("fx-finish-burst");
            for (var i = 0; i < 6; i++)
            {
                var angle = i * 60f;
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Sparkle-" + i;
                quad.transform.SetParent(root.transform, false);
                quad.transform.localPosition = new Vector3(
                    Mathf.Cos(angle * Mathf.Deg2Rad) * 0.8f,
                    0.6f + (i % 2) * 0.5f,
                    Mathf.Sin(angle * Mathf.Deg2Rad) * 0.8f);
                quad.transform.localScale = Vector3.one * 0.7f;
                var collider = quad.GetComponent<Collider>();
                if (collider != null) Object.DestroyImmediate(collider);
                quad.GetComponent<MeshRenderer>().sharedMaterial = sparkle;
            }
            try
            {
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------
        // The catalog: every stable id bound to its generated clip/prefab.
        // Refresh-on-ensure so contract fields (volumes, bounds, event
        // kinds) stay in step with the presenters; a clip that is absent
        // on disk binds as an honest empty slot, never a substitute.
        private static AudioClip Clip(string name)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>(
                AudioDir + "/" + name + ".wav");
        }

        private static void EnsureCatalog(
            Dictionary<string, GameObject> prefabs)
        {
            var catalog = AssetDatabase
                .LoadAssetAtPath<RacePresentationCatalogAsset>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject
                    .CreateInstance<RacePresentationCatalogAsset>();
                catalog.catalogId = CatalogId;
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.catalogId = CatalogId;
            catalog.cues = new[]
            {
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "landing", eventKind = "motor.landed",
                    clip = Clip("cue-landing"), volume = 0.9f,
                    minIntervalSeconds = 0.15f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "wall", eventKind = "motor.wallHit",
                    clip = Clip("cue-wall"), volume = 0.85f,
                    minIntervalSeconds = 0.25f
                },
                // The physical backend's chassis contact is the same
                // gameplay event as the arcade wallHit.
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "contact", eventKind = "motor.contact",
                    clip = Clip("cue-wall"), volume = 0.8f,
                    minIntervalSeconds = 0.25f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "nudge", eventKind = "motor.racerNudge",
                    clip = Clip("cue-nudge"), volume = 0.7f,
                    minIntervalSeconds = 0.2f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "boost", eventKind = "motor.boostStart",
                    clip = Clip("cue-boost"), volume = 0.8f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "air", eventKind = "motor.becameAirborne",
                    clip = Clip("cue-air"), volume = 0.6f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "checkpoint", eventKind = "checkpoint.crossed",
                    clip = Clip("cue-checkpoint"), volume = 0.75f,
                    minIntervalSeconds = 0.2f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "go", eventKind = "race.running",
                    clip = Clip("cue-go"), volume = 0.85f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "finish", eventKind = "race.finished",
                    clip = Clip("cue-finish"), volume = 0.9f
                },
                // LAN racer finishes ride the host-confirmed finalized
                // event — same fanfare.
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "finalize", eventKind = "race.finalized",
                    clip = Clip("cue-finish"), volume = 0.9f
                },
                // M5 Task 5 gadget cue slots: one per authoritative
                // gadget event kind. Clips are the committed generated
                // WAVs (Tools/Generate-RaceAudio.py); an absent clip is
                // the honest empty slot the audio presenter reports.
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "gadget-pickup", eventKind = "gadget.pickup",
                    clip = Clip("cue-gadget-pickup"), volume = 0.8f,
                    minIntervalSeconds = 0.1f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "gadget-use", eventKind = "gadget.used",
                    clip = Clip("cue-gadget-use"), volume = 0.85f,
                    minIntervalSeconds = 0.15f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "gadget-foam-drop",
                    eventKind = "gadget.foamDropped",
                    clip = Clip("cue-gadget-foam"), volume = 0.7f,
                    minIntervalSeconds = 0.2f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "gadget-foam-hit", eventKind = "gadget.foamHit",
                    clip = Clip("cue-gadget-foam"), volume = 0.85f,
                    minIntervalSeconds = 0.2f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "gadget-shield",
                    eventKind = "gadget.shieldBlocked",
                    clip = Clip("cue-gadget-shield"), volume = 0.85f,
                    minIntervalSeconds = 0.2f
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "gadget-respawn",
                    eventKind = "gadget.respawn",
                    clip = Clip("cue-gadget-pickup"), volume = 0.5f,
                    minIntervalSeconds = 0.4f
                }
            };
            catalog.loops = new[]
            {
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "engine.car", clip = Clip("engine-car"),
                    volume = 0.55f, maxVoices = 4
                },
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "engine.motorcycle",
                    clip = Clip("engine-motorcycle"),
                    volume = 0.55f, maxVoices = 4
                },
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "skid", clip = Clip("skid"),
                    volume = 0.45f, maxVoices = 3
                },
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "boost", clip = Clip("boost"),
                    volume = 0.5f, maxVoices = 3
                }
            };
            catalog.music = new[]
            {
                new RacePresentationCatalogAsset.MusicCue
                {
                    cueId = "music-race", phase = "racing",
                    clip = Clip("music-race"), volume = 0.6f
                },
                new RacePresentationCatalogAsset.MusicCue
                {
                    cueId = "music-results", phase = "results",
                    clip = Clip("music-results"), volume = 0.6f
                }
            };
            catalog.effects = new[]
            {
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "drift-dust", prefab = prefabs["drift-dust"],
                    poolSize = 10, lifetimeSeconds = 0.55f, growTo = 2.1f
                },
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "boost-flare", prefab = prefabs["boost-flare"],
                    poolSize = 8, lifetimeSeconds = 0.4f, growTo = 1.8f,
                    faceCamera = true
                },
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "landing-puff",
                    prefab = prefabs["landing-puff"], poolSize = 6,
                    lifetimeSeconds = 0.7f, growTo = 1.9f
                },
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "wall-shunt", prefab = prefabs["wall-shunt"],
                    poolSize = 6, lifetimeSeconds = 0.5f, growTo = 1.7f,
                    faceCamera = true
                },
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "checkpoint-sparkle",
                    prefab = prefabs["checkpoint-sparkle"], poolSize = 8,
                    lifetimeSeconds = 0.8f, growTo = 1.5f, faceCamera = true
                },
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "finish-burst",
                    prefab = prefabs["finish-burst"], poolSize = 4,
                    lifetimeSeconds = 1.4f, growTo = 1.6f
                }
            };
            catalog.effectTriggers = new[]
            {
                Trigger("motor.landed", "landing-puff", 2),
                Trigger("motor.wallHit", "wall-shunt", 1),
                Trigger("motor.contact", "wall-shunt", 1),
                Trigger("motor.racerNudge", "wall-shunt", 1),
                Trigger("motor.boostStart", "boost-flare", 2),
                Trigger("motor.becameAirborne", "drift-dust", 1),
                Trigger("checkpoint.crossed", "checkpoint-sparkle", 3),
                Trigger("race.finished", "finish-burst", 1),
                Trigger("race.finalized", "finish-burst", 1),
                // M5 Task 5 gadget bursts: pickups sparkle, foam drops
                // and hits puff at the racer, a blocked hit flares.
                Trigger("gadget.pickup", "checkpoint-sparkle", 2),
                Trigger("gadget.used", "boost-flare", 1),
                Trigger("gadget.foamDropped", "landing-puff", 1),
                Trigger("gadget.foamHit", "wall-shunt", 2),
                Trigger("gadget.shieldBlocked", "checkpoint-sparkle", 2),
                Trigger("gadget.respawn", "checkpoint-sparkle", 1)
            };
            string error;
            if (!catalog.Validate(out error))
                throw new InvalidDataException(
                    "RacePresentationAuthoring: " + error);
            EditorUtility.SetDirty(catalog);
        }

        private static RacePresentationCatalogAsset.EffectTrigger Trigger(
            string eventKind, string effectId, int count)
        {
            return new RacePresentationCatalogAsset.EffectTrigger
            {
                eventKind = eventKind, effectId = effectId, count = count
            };
        }
    }
}
