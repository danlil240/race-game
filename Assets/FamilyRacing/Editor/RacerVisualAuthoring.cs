using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // M4 Task-4 racer authoring: generates the authored rally car, dirt
    // motorcycle and the four seated animal racers (fox/rabbit/panda/
    // raccoon) from Unity primitives into Art/VisualBenchmark — original,
    // procedural, US$0 content per the art guide (chunky toy shapes, flat
    // matte colors, readable at chase and overhead distance).
    //
    // Conventions the runtime RacerVisualRig binds against (mirrored in
    // VehicleVisualDefinitionAsset / CharacterDefinitionAsset docs):
    //   * prefab roots sit on the ground plane facing +Z;
    //   * SeatMount / GripL / GripR are the character contact points;
    //   * wheel pivots named in tuning order each carry a "Spin" child;
    //   * every "*Accent*" renderer is tint-free authored white and gets
    //     the racer's profile color via material property override;
    //   * no colliders anywhere — these are render-only hierarchies.
    public static class RacerVisualAuthoring
    {
        public const string VehiclesDir =
            "Assets/FamilyRacing/Art/VisualBenchmark/Vehicles";
        public const string CharactersDir =
            "Assets/FamilyRacing/Art/VisualBenchmark/Characters";
        public const string CarPrefabPath = VehiclesDir + "/rally-car-01.prefab";
        public const string MotoPrefabPath = VehiclesDir + "/moto-01.prefab";
        public const string CarVisualAssetPath =
            VehiclesDir + "/vehicle-visual-car-01.asset";
        public const string MotoVisualAssetPath =
            VehiclesDir + "/vehicle-visual-moto-01.asset";
        public const string CarVisualId = "rally-car-01";
        public const string MotoVisualId = "moto-01";

        // Slot order is the AI/roster cosmetic mapping: character index
        // N is slot N's fallback animal — the same order
        // ProfileValidation assigns avatars (fox, rabbit, panda,
        // raccoon). A PlayMode test pins this order.
        public static readonly string[] CharacterIds =
            { "fox", "rabbit", "panda", "raccoon" };

        public static string CharacterPrefabPath(string id)
        {
            return CharactersDir + "/racer-" + id + ".prefab";
        }

        public static string CharacterAssetPath(string id)
        {
            return CharactersDir + "/character-" + id + ".asset";
        }

        [MenuItem("Family Racing/Author Visual Benchmark Racers")]
        public static void AuthorMenu()
        {
            Author();
        }

        // Idempotent ensure: creates whatever is missing and refreshes
        // every field on the definition assets (they're authored data
        // that must match the code contract), never deletes.
        public static void Ensure()
        {
            EnsureFolders();
            var mats = EnsureMaterials();
            var car = EnsurePrefab(CarPrefabPath,
                () => BuildCar(mats));
            var moto = EnsurePrefab(MotoPrefabPath,
                () => BuildMotorcycle(mats));
            var characters = new CharacterDefinitionAsset[
                CharacterIds.Length];
            for (var i = 0; i < CharacterIds.Length; i++)
            {
                var id = CharacterIds[i];
                var prefab = EnsurePrefab(CharacterPrefabPath(id),
                    () => BuildCharacter(id, mats));
                characters[i] = EnsureCharacter(id, prefab);
            }
            EnsureCarVisual(car);
            EnsureMotoVisual(moto);
        }

        // Full re-author (menu + tests): rebuilds prefabs in place so
        // GUID references survive, then refreshes every definition.
        public static void Author()
        {
            EnsureFolders();
            var mats = EnsureMaterials();
            SavePrefab(BuildCar(mats), CarPrefabPath);
            SavePrefab(BuildMotorcycle(mats), MotoPrefabPath);
            for (var i = 0; i < CharacterIds.Length; i++)
            {
                var id = CharacterIds[i];
                SavePrefab(BuildCharacter(id, mats),
                    CharacterPrefabPath(id));
            }
            var car = AssetDatabase.LoadAssetAtPath<GameObject>(
                CarPrefabPath);
            var moto = AssetDatabase.LoadAssetAtPath<GameObject>(
                MotoPrefabPath);
            for (var i = 0; i < CharacterIds.Length; i++)
                EnsureCharacter(CharacterIds[i],
                    AssetDatabase.LoadAssetAtPath<GameObject>(
                        CharacterPrefabPath(CharacterIds[i])));
            EnsureCarVisual(car);
            EnsureMotoVisual(moto);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/FamilyRacing/Art"))
                AssetDatabase.CreateFolder(
                    "Assets/FamilyRacing", "Art");
            if (!AssetDatabase.IsValidFolder(
                "Assets/FamilyRacing/Art/VisualBenchmark"))
                AssetDatabase.CreateFolder(
                    "Assets/FamilyRacing/Art", "VisualBenchmark");
            if (!AssetDatabase.IsValidFolder(VehiclesDir))
                AssetDatabase.CreateFolder(
                    "Assets/FamilyRacing/Art/VisualBenchmark", "Vehicles");
            if (!AssetDatabase.IsValidFolder(CharactersDir))
                AssetDatabase.CreateFolder(
                    "Assets/FamilyRacing/Art/VisualBenchmark",
                    "Characters");
        }

        // ---------------------------------------------------------------
        // Shared materials: flat matte Simple Lit, one material per role.
        // Accent materials author white so the per-racer color override
        // reads true.
        private static Dictionary<string, Material> EnsureMaterials()
        {
            var mats = new Dictionary<string, Material>();
            var shader = Shader.Find(
                "Universal Render Pipeline/Simple Lit");
            if (shader == null)
                throw new System.InvalidOperationException(
                    "RacerVisualAuthoring: missing URP Simple Lit shader");
            Define(mats, shader, VehiclesDir, "Mat_CarBody",
                Hex(0xE8, 0x82, 0x6B), 0.10f);   // warm coral
            Define(mats, shader, VehiclesDir, "Mat_CarDark",
                Hex(0x29, 0x27, 0x2B), 0.06f);   // tires/trim
            Define(mats, shader, VehiclesDir, "Mat_CarCream",
                Hex(0xF5, 0xE8, 0xCC), 0.10f);   // tub/bumpers/hubs
            Define(mats, shader, VehiclesDir, "Mat_CarGlass",
                Hex(0x8C, 0xBD, 0xD1), 0.25f);   // headlights
            Define(mats, shader, VehiclesDir, "Mat_MotoBody",
                Hex(0x80, 0xC7, 0xB3), 0.10f);   // seafoam
            Define(mats, shader, VehiclesDir, "Mat_MotoDark",
                Hex(0x29, 0x27, 0x2B), 0.06f);
            Define(mats, shader, VehiclesDir, "Mat_MotoMetal",
                Hex(0x9B, 0x9B, 0xA3), 0.45f);   // forks/exhaust
            Define(mats, shader, VehiclesDir, "Mat_VehicleAccent",
                Color.white, 0.15f);             // slot-tinted
            Define(mats, shader, CharactersDir, "Mat_Fox",
                Hex(0xDE, 0x8A, 0x4F), 0.10f);   // soft orange
            Define(mats, shader, CharactersDir, "Mat_FoxCream",
                Hex(0xF5, 0xE8, 0xCC), 0.10f);
            Define(mats, shader, CharactersDir, "Mat_Rabbit",
                Hex(0xC9, 0xC4, 0xDD), 0.10f);   // lilac-gray
            Define(mats, shader, CharactersDir, "Mat_RabbitInner",
                Hex(0xE6, 0x99, 0x9E), 0.10f);   // pastel pink
            Define(mats, shader, CharactersDir, "Mat_Panda",
                Hex(0xF2, 0xEA, 0xD8), 0.10f);   // warm cream
            Define(mats, shader, CharactersDir, "Mat_PandaDark",
                Hex(0x28, 0x25, 0x24), 0.08f);
            Define(mats, shader, CharactersDir, "Mat_Raccoon",
                Hex(0x99, 0x92, 0x84), 0.10f);   // warm gray
            Define(mats, shader, CharactersDir, "Mat_RaccoonDark",
                Hex(0x3B, 0x37, 0x33), 0.08f);
            Define(mats, shader, CharactersDir, "Mat_EyeDark",
                Hex(0x1F, 0x1C, 0x1F), 0.05f);
            Define(mats, shader, CharactersDir, "Mat_CharAccent",
                Color.white, 0.12f);             // scarf — slot-tinted
            return mats;
        }

        private static void Define(Dictionary<string, Material> mats,
            Shader shader, string dir, string name, Color color,
            float smoothness)
        {
            var path = dir + "/" + name + ".mat";
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
            mat.SetColor("_Color", color);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Surface", 0f);
            mats[name] = mat;
            EditorUtility.SetDirty(mat);
        }

        private static Color Hex(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f);
        }

        // ---------------------------------------------------------------
        // Prefab save/load helpers.
        private static GameObject EnsurePrefab(string path,
            System.Func<GameObject> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            return SavePrefab(build(), path);
        }

        private static GameObject SavePrefab(GameObject root, string path)
        {
            try
            {
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static T Ensure<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(asset, path);
            }
            return asset;
        }

        private static void EnsureCarVisual(GameObject prefab)
        {
            var asset = Ensure<VehicleVisualDefinitionAsset>(
                CarVisualAssetPath);
            asset.visualId = CarVisualId;
            asset.vehicleClass = "car";
            asset.prefab = prefab;
            asset.seatMountName = "SeatMount";
            asset.gripLeftName = "GripL";
            asset.gripRightName = "GripR";
            asset.dashWheelName = "DashWheel";
            // Physics tuning order: front-left, front-right, rear-left,
            // rear-right — the rig requires the same order here.
            asset.wheelNames = new[]
                { "WheelFL", "WheelFR", "WheelRL", "WheelRR" };
            asset.wheelSteerable = new[] { true, true, false, false };
            asset.wheelRadius = 0.32f;
            asset.steerDegrees = 25f;
            // Car-01 physical tuning: mountY -0.05, rest 0.40, radius
            // 0.32 -> ground plane sits 0.77m below the body origin.
            asset.bodyOriginHeight = 0.77f;
            asset.riderLeanDegrees = 7f;
            EditorUtility.SetDirty(asset);
        }

        private static void EnsureMotoVisual(GameObject prefab)
        {
            var asset = Ensure<VehicleVisualDefinitionAsset>(
                MotoVisualAssetPath);
            asset.visualId = MotoVisualId;
            asset.vehicleClass = "motorcycle";
            asset.prefab = prefab;
            asset.seatMountName = "SeatMount";
            asset.gripLeftName = "GripL";
            asset.gripRightName = "GripR";
            // The bars/forks live on the steered front pivot — no
            // separate dash wheel.
            asset.dashWheelName = "";
            asset.wheelNames = new[] { "WheelF", "WheelR" };
            asset.wheelSteerable = new[] { true, false };
            asset.wheelRadius = 0.30f;
            asset.steerDegrees = 26f;
            // Moto-01 physical tuning: mountY -0.10, rest 0.30, radius
            // 0.30 -> body origin 0.70m above the ground plane.
            asset.bodyOriginHeight = 0.70f;
            asset.riderLeanDegrees = 12f;
            EditorUtility.SetDirty(asset);
        }

        private static CharacterDefinitionAsset EnsureCharacter(
            string id, GameObject prefab)
        {
            var asset = Ensure<CharacterDefinitionAsset>(
                CharacterAssetPath(id));
            asset.characterId = id;
            asset.prefab = prefab;
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // ---------------------------------------------------------------
        // Part helpers. Every authored part is a stripped primitive — no
        // collider ever survives authoring.
        private static GameObject Prim(GameObject parent,
            PrimitiveType type, string name, Material material,
            Vector3 position, Vector3 scale)
        {
            return Prim(parent, type, name, material, position, scale,
                Quaternion.identity);
        }

        private static GameObject Prim(GameObject parent,
            PrimitiveType type, string name, Material material,
            Vector3 position, Vector3 scale, Quaternion rotation)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = material;
            return go;
        }

        private static GameObject Node(GameObject parent, string name,
            Vector3 position)
        {
            return Node(parent, name, position, Quaternion.identity);
        }

        private static GameObject Node(GameObject parent, string name,
            Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            return go;
        }

        private static GameObject Wheel(GameObject parent, string name,
            Vector3 center, float radius, float width,
            Material tire, Material hub)
        {
            var pivot = Node(parent, name, center);
            var spin = Node(pivot, "Spin", Vector3.zero);
            var axle = Quaternion.Euler(0f, 0f, 90f);
            Prim(spin, PrimitiveType.Cylinder, "Tire", tire,
                Vector3.zero,
                new Vector3(radius * 2f, width * 0.5f, radius * 2f),
                axle);
            Prim(spin, PrimitiveType.Cylinder, "Hub", hub,
                Vector3.zero,
                new Vector3(radius * 1.05f, width * 0.5f + 0.02f,
                    radius * 1.05f), axle);
            return pivot;
        }

        // ---------------------------------------------------------------
        // rally-car-01: a chunky open-top rally buggy — low wide body,
        // scooped nose, exposed tub with roll bar, tail spoiler, fat
        // tires. Reads as a friendly race car from chase and overhead.
        private static GameObject BuildCar(
            Dictionary<string, Material> m)
        {
            var root = new GameObject(CarVisualId);
            var body = m["Mat_CarBody"];
            var dark = m["Mat_CarDark"];
            var cream = m["Mat_CarCream"];
            var glass = m["Mat_CarGlass"];
            var accent = m["Mat_VehicleAccent"];

            Prim(root, PrimitiveType.Cube, "Body", body,
                new Vector3(0f, 0.52f, 0f), new Vector3(1.75f, 0.5f, 3.1f));
            Prim(root, PrimitiveType.Cube, "Nose", body,
                new Vector3(0f, 0.72f, 1.15f), new Vector3(1.45f, 0.26f, 0.85f),
                Quaternion.Euler(-6f, 0f, 0f));
            Prim(root, PrimitiveType.Cube, "AccentHood", accent,
                new Vector3(0f, 0.855f, 1.13f), new Vector3(0.34f, 0.03f, 0.8f),
                Quaternion.Euler(-6f, 0f, 0f));
            Prim(root, PrimitiveType.Cube, "Tub", cream,
                new Vector3(0f, 0.80f, -0.55f), new Vector3(1.35f, 0.34f, 1.1f));
            Prim(root, PrimitiveType.Cube, "SeatBack", dark,
                new Vector3(0f, 1.02f, -1.02f), new Vector3(1.05f, 0.45f, 0.14f),
                Quaternion.Euler(-10f, 0f, 0f));
            Node(root, "SeatMount", new Vector3(0f, 0.80f, -0.42f));
            Prim(root, PrimitiveType.Cube, "Dash", dark,
                new Vector3(0f, 0.98f, 0.32f), new Vector3(1.25f, 0.16f, 0.22f));
            var dashWheel = Prim(root, PrimitiveType.Cylinder, "DashWheel",
                dark, new Vector3(0f, 1.10f, 0.47f),
                new Vector3(0.42f, 0.035f, 0.42f),
                Quaternion.Euler(-24f, 0f, 0f));
            Node(dashWheel, "GripL", new Vector3(-0.16f, -0.05f, 0.02f));
            Node(dashWheel, "GripR", new Vector3(0.16f, -0.05f, 0.02f));
            Prim(root, PrimitiveType.Cylinder, "RollBarL", dark,
                new Vector3(-0.45f, 1.16f, -0.95f), new Vector3(0.07f, 0.30f, 0.07f),
                Quaternion.Euler(12f, 0f, 0f));
            Prim(root, PrimitiveType.Cylinder, "RollBarR", dark,
                new Vector3(0.45f, 1.16f, -0.95f), new Vector3(0.07f, 0.30f, 0.07f),
                Quaternion.Euler(12f, 0f, 0f));
            Prim(root, PrimitiveType.Cylinder, "RollBarTop", dark,
                new Vector3(0f, 1.42f, -1.02f), new Vector3(0.07f, 0.45f, 0.07f),
                Quaternion.Euler(0f, 0f, 90f));
            Prim(root, PrimitiveType.Cube, "SpoilerAccent", accent,
                new Vector3(0f, 1.16f, -1.52f), new Vector3(1.5f, 0.07f, 0.34f));
            Prim(root, PrimitiveType.Cube, "SpoilerStrutL", dark,
                new Vector3(-0.45f, 1.06f, -1.5f), new Vector3(0.08f, 0.2f, 0.18f));
            Prim(root, PrimitiveType.Cube, "SpoilerStrutR", dark,
                new Vector3(0.45f, 1.06f, -1.5f), new Vector3(0.08f, 0.2f, 0.18f));
            Prim(root, PrimitiveType.Cube, "BumperF", cream,
                new Vector3(0f, 0.40f, 1.62f), new Vector3(1.7f, 0.24f, 0.16f));
            Prim(root, PrimitiveType.Cube, "BumperR", cream,
                new Vector3(0f, 0.40f, -1.62f), new Vector3(1.7f, 0.24f, 0.16f));
            Prim(root, PrimitiveType.Sphere, "HeadlightL", glass,
                new Vector3(-0.52f, 0.62f, 1.57f), new Vector3(0.2f, 0.2f, 0.14f));
            Prim(root, PrimitiveType.Sphere, "HeadlightR", glass,
                new Vector3(0.52f, 0.62f, 1.57f), new Vector3(0.2f, 0.2f, 0.14f));
            // Accent fenders over each wheel — the overhead view reads
            // the racer's color from these plus the spoiler/stripe.
            Prim(root, PrimitiveType.Cube, "AccentFenderFL", accent,
                new Vector3(-0.86f, 0.66f, 1.2f), new Vector3(0.30f, 0.07f, 0.78f));
            Prim(root, PrimitiveType.Cube, "AccentFenderFR", accent,
                new Vector3(0.86f, 0.66f, 1.2f), new Vector3(0.30f, 0.07f, 0.78f));
            Prim(root, PrimitiveType.Cube, "AccentFenderRL", accent,
                new Vector3(-0.86f, 0.66f, -1.2f), new Vector3(0.30f, 0.07f, 0.78f));
            Prim(root, PrimitiveType.Cube, "AccentFenderRR", accent,
                new Vector3(0.86f, 0.66f, -1.2f), new Vector3(0.30f, 0.07f, 0.78f));
            Wheel(root, "WheelFL", new Vector3(-0.8f, 0.32f, 1.2f),
                0.32f, 0.28f, dark, cream);
            Wheel(root, "WheelFR", new Vector3(0.8f, 0.32f, 1.2f),
                0.32f, 0.28f, dark, cream);
            Wheel(root, "WheelRL", new Vector3(-0.8f, 0.32f, -1.2f),
                0.32f, 0.28f, dark, cream);
            Wheel(root, "WheelRR", new Vector3(0.8f, 0.32f, -1.2f),
                0.32f, 0.28f, dark, cream);
            return root;
        }

        // ---------------------------------------------------------------
        // moto-01: a small dirt bike — narrow spine, tank hump, flat
        // seat, high front fender and wide bars. Reads as a motorcycle
        // even in a single-sprite overhead view.
        private static GameObject BuildMotorcycle(
            Dictionary<string, Material> m)
        {
            var root = new GameObject(MotoVisualId);
            var body = m["Mat_MotoBody"];
            var dark = m["Mat_MotoDark"];
            var metal = m["Mat_MotoMetal"];
            var cream = m["Mat_CarCream"];
            var accent = m["Mat_VehicleAccent"];

            Prim(root, PrimitiveType.Cube, "Frame", body,
                new Vector3(0f, 0.60f, 0.02f), new Vector3(0.32f, 0.26f, 1.30f));
            Prim(root, PrimitiveType.Sphere, "Tank", body,
                new Vector3(0f, 0.80f, 0.14f), new Vector3(0.44f, 0.28f, 0.60f));
            Prim(root, PrimitiveType.Cube, "Seat", dark,
                new Vector3(0f, 0.74f, -0.48f), new Vector3(0.34f, 0.10f, 0.70f));
            Node(root, "SeatMount", new Vector3(0f, 0.80f, -0.40f));
            Prim(root, PrimitiveType.Cube, "Tail", body,
                new Vector3(0f, 0.72f, -0.92f), new Vector3(0.28f, 0.16f, 0.40f));
            Prim(root, PrimitiveType.Cube, "TailAccent", accent,
                new Vector3(0f, 0.72f, -1.13f), new Vector3(0.30f, 0.11f, 0.05f));
            Prim(root, PrimitiveType.Cube, "PlateAccent", accent,
                new Vector3(0f, 0.92f, 0.52f), new Vector3(0.24f, 0.30f, 0.05f),
                Quaternion.Euler(-16f, 0f, 0f));
            Prim(root, PrimitiveType.Cylinder, "Exhaust", metal,
                new Vector3(0.16f, 0.36f, -0.82f), new Vector3(0.09f, 0.35f, 0.09f),
                Quaternion.Euler(90f, 0f, 0f));
            Prim(root, PrimitiveType.Cube, "Engine", metal,
                new Vector3(0f, 0.42f, -0.02f), new Vector3(0.38f, 0.28f, 0.50f));

            // Front pivot: tire + fork + bars + fender steer together.
            var front = Node(root, "WheelF",
                new Vector3(0f, 0.30f, 0.725f));
            var spinF = Node(front, "Spin", Vector3.zero);
            var axle = Quaternion.Euler(0f, 0f, 90f);
            Prim(spinF, PrimitiveType.Cylinder, "Tire", dark,
                Vector3.zero, new Vector3(0.60f, 0.08f, 0.60f), axle);
            Prim(spinF, PrimitiveType.Cylinder, "Hub", cream,
                Vector3.zero, new Vector3(0.30f, 0.10f, 0.30f), axle);
            Prim(front, PrimitiveType.Cylinder, "ForkL", metal,
                new Vector3(-0.12f, 0.34f, -0.06f),
                new Vector3(0.045f, 0.40f, 0.045f),
                Quaternion.Euler(-14f, 0f, 0f));
            Prim(front, PrimitiveType.Cylinder, "ForkR", metal,
                new Vector3(0.12f, 0.34f, -0.06f),
                new Vector3(0.045f, 0.40f, 0.045f),
                Quaternion.Euler(-14f, 0f, 0f));
            Prim(front, PrimitiveType.Cylinder, "Bars", dark,
                new Vector3(0f, 0.64f, -0.20f),
                new Vector3(0.05f, 0.30f, 0.05f), axle);
            Node(front, "GripL", new Vector3(-0.30f, 0.64f, -0.20f));
            Node(front, "GripR", new Vector3(0.30f, 0.64f, -0.20f));
            Prim(front, PrimitiveType.Sphere, "FenderAccent", accent,
                new Vector3(0f, 0.36f, 0.02f), new Vector3(0.40f, 0.10f, 0.75f));
            Prim(front, PrimitiveType.Sphere, "Headlight", cream,
                new Vector3(0f, 0.52f, -0.14f),
                new Vector3(0.16f, 0.16f, 0.10f));

            var rear = Node(root, "WheelR",
                new Vector3(0f, 0.30f, -0.725f));
            var spinR = Node(rear, "Spin", Vector3.zero);
            Prim(spinR, PrimitiveType.Cylinder, "Tire", dark,
                Vector3.zero, new Vector3(0.60f, 0.08f, 0.60f), axle);
            Prim(spinR, PrimitiveType.Cylinder, "Hub", cream,
                Vector3.zero, new Vector3(0.30f, 0.10f, 0.30f), axle);
            Prim(rear, PrimitiveType.Cube, "SwingArm", dark,
                new Vector3(0f, 0.02f, 0.32f), new Vector3(0.09f, 0.08f, 0.55f));
            return root;
        }

        // ---------------------------------------------------------------
        // The shared seated skeleton every species prefab keeps:
        //   Root/Rig/Hips — sits on the vehicle SeatMount
        //   LegL/LegR on Hips (posed per class at bind time)
        //   Tail on Hips, Spine (torso + ScarfAccent + ArmL/ArmR) on
        //   Hips, Head on Spine — plus species signature parts.
        private static GameObject BuildCharacter(string id,
            Dictionary<string, Material> m)
        {
            var root = new GameObject("racer-" + id);
            var rig = Node(root, "Rig", Vector3.zero);
            var hips = Node(rig, "Hips", Vector3.zero);
            var spine = Node(hips, "Spine", new Vector3(0f, 0.26f, 0f));
            var head = Node(spine, "Head", new Vector3(0f, 0.58f, 0.02f));
            var tail = Node(hips, "Tail", new Vector3(0f, 0.10f, -0.24f),
                Quaternion.Euler(-35f, 0f, 0f));

            var body = CharacterBody(m, id);
            var limb = CharacterLimb(m, id);
            var eye = m["Mat_EyeDark"];
            var accent = m["Mat_CharAccent"];

            Prim(hips, PrimitiveType.Sphere, "BellyMesh", body,
                new Vector3(0f, 0.08f, 0.02f),
                new Vector3(0.50f, 0.38f, 0.46f));
            for (var side = -1; side <= 1; side += 2)
            {
                var leg = Node(hips, side < 0 ? "LegL" : "LegR",
                    new Vector3(side * 0.15f, 0.04f, 0.12f));
                Prim(leg, PrimitiveType.Capsule,
                    leg.name + "Mesh", limb,
                    new Vector3(0f, -0.12f, 0.02f),
                    new Vector3(0.15f, 0.17f, 0.15f));
                var arm = Node(spine, side < 0 ? "ArmL" : "ArmR",
                    new Vector3(side * 0.31f, 0.40f, 0.05f));
                Prim(arm, PrimitiveType.Capsule,
                    arm.name + "Mesh", limb,
                    new Vector3(0f, -0.16f, 0f),
                    new Vector3(0.14f, 0.20f, 0.14f));
            }
            Prim(spine, PrimitiveType.Capsule, "Torso", body,
                new Vector3(0f, 0.20f, 0f),
                new Vector3(0.52f, 0.30f, 0.44f));
            Prim(spine, PrimitiveType.Cylinder, "ScarfAccent", accent,
                new Vector3(0f, 0.44f, 0.03f),
                new Vector3(0.40f, 0.045f, 0.34f));
            Prim(head, PrimitiveType.Sphere, "HeadMesh", body,
                new Vector3(0f, 0.14f, 0.02f),
                new Vector3(0.56f, 0.54f, 0.54f));
            Prim(head, PrimitiveType.Sphere, "EyeL", eye,
                new Vector3(-0.14f, 0.20f, 0.235f),
                new Vector3(0.07f, 0.085f, 0.05f));
            Prim(head, PrimitiveType.Sphere, "EyeR", eye,
                new Vector3(0.14f, 0.20f, 0.235f),
                new Vector3(0.07f, 0.085f, 0.05f));

            switch (id)
            {
                case "fox": DressFox(root, hips, spine, head, tail, m); break;
                case "rabbit": DressRabbit(root, hips, spine, head, tail, m); break;
                case "panda": DressPanda(root, hips, spine, head, tail, m); break;
                case "raccoon": DressRaccoon(root, hips, spine, head, tail, m); break;
            }
            return root;
        }

        private static Material CharacterBody(
            Dictionary<string, Material> m, string id)
        {
            switch (id)
            {
                case "fox": return m["Mat_Fox"];
                case "rabbit": return m["Mat_Rabbit"];
                case "panda": return m["Mat_Panda"];
                case "raccoon": return m["Mat_Raccoon"];
            }
            return m["Mat_CharAccent"];
        }

        private static Material CharacterLimb(
            Dictionary<string, Material> m, string id)
        {
            // Panda limbs are dark — part of the species read.
            if (id == "panda") return m["Mat_PandaDark"];
            return CharacterBody(m, id);
        }

        private static void DressFox(GameObject root, GameObject hips,
            GameObject spine, GameObject head, GameObject tail,
            Dictionary<string, Material> m)
        {
            var fur = m["Mat_Fox"];
            var cream = m["Mat_FoxCream"];
            var dark = m["Mat_EyeDark"];
            for (var side = -1; side <= 1; side += 2)
            {
                var ear = Node(head, side < 0 ? "EarL" : "EarR",
                    new Vector3(side * 0.16f, 0.38f, -0.02f),
                    Quaternion.Euler(0f, 0f, -side * 14f));
                Prim(ear, PrimitiveType.Cube, ear.name + "Mesh", fur,
                    new Vector3(0f, 0.12f, 0f),
                    new Vector3(0.13f, 0.30f, 0.08f));
                Prim(ear, PrimitiveType.Cube, ear.name + "Tip", dark,
                    new Vector3(0f, 0.28f, 0f),
                    new Vector3(0.11f, 0.10f, 0.07f));
            }
            Prim(head, PrimitiveType.Cube, "Muzzle", cream,
                new Vector3(0f, 0.02f, 0.30f),
                new Vector3(0.20f, 0.14f, 0.22f));
            Prim(head, PrimitiveType.Sphere, "Nose", dark,
                new Vector3(0f, 0.08f, 0.42f),
                new Vector3(0.07f, 0.07f, 0.07f));
            Prim(tail, PrimitiveType.Cylinder, "TailPlume", fur,
                new Vector3(0f, 0.22f, 0f),
                new Vector3(0.22f, 0.30f, 0.22f));
            Prim(tail, PrimitiveType.Sphere, "TailTip", cream,
                new Vector3(0f, 0.52f, 0f),
                new Vector3(0.24f, 0.24f, 0.24f));
        }

        private static void DressRabbit(GameObject root, GameObject hips,
            GameObject spine, GameObject head, GameObject tail,
            Dictionary<string, Material> m)
        {
            var fur = m["Mat_Rabbit"];
            var inner = m["Mat_RabbitInner"];
            var cream = m["Mat_FoxCream"];
            for (var side = -1; side <= 1; side += 2)
            {
                var ear = Node(head, side < 0 ? "EarL" : "EarR",
                    new Vector3(side * 0.11f, 0.40f, -0.04f),
                    Quaternion.Euler(-6f, 0f, -side * 9f));
                Prim(ear, PrimitiveType.Capsule, ear.name + "Mesh", fur,
                    new Vector3(0f, 0.22f, 0f),
                    new Vector3(0.11f, 0.26f, 0.08f));
                Prim(ear, PrimitiveType.Capsule, ear.name + "Inner",
                    inner, new Vector3(0f, 0.20f, 0.035f),
                    new Vector3(0.05f, 0.20f, 0.03f));
            }
            Prim(head, PrimitiveType.Sphere, "Muzzle", cream,
                new Vector3(0f, 0.0f, 0.30f),
                new Vector3(0.20f, 0.15f, 0.14f));
            Prim(head, PrimitiveType.Cube, "Teeth", cream,
                new Vector3(0f, -0.07f, 0.35f),
                new Vector3(0.07f, 0.07f, 0.03f));
            Prim(tail, PrimitiveType.Sphere, "TailPuff", cream,
                new Vector3(0f, 0.04f, -0.02f),
                new Vector3(0.24f, 0.24f, 0.24f));
        }

        private static void DressPanda(GameObject root, GameObject hips,
            GameObject spine, GameObject head, GameObject tail,
            Dictionary<string, Material> m)
        {
            var cream = m["Mat_Panda"];
            var dark = m["Mat_PandaDark"];
            for (var side = -1; side <= 1; side += 2)
            {
                Prim(head, PrimitiveType.Sphere,
                    side < 0 ? "EarL" : "EarR", dark,
                    new Vector3(side * 0.20f, 0.42f, -0.03f),
                    new Vector3(0.17f, 0.17f, 0.10f));
                Prim(head, PrimitiveType.Sphere,
                    side < 0 ? "EyePatchL" : "EyePatchR", dark,
                    new Vector3(side * 0.14f, 0.18f, 0.215f),
                    new Vector3(0.15f, 0.17f, 0.07f));
            }
            Prim(head, PrimitiveType.Sphere, "Muzzle", cream,
                new Vector3(0f, -0.02f, 0.30f),
                new Vector3(0.22f, 0.15f, 0.14f));
            Prim(head, PrimitiveType.Sphere, "Nose", dark,
                new Vector3(0f, 0.03f, 0.40f),
                new Vector3(0.08f, 0.08f, 0.08f));
            Prim(tail, PrimitiveType.Sphere, "TailNub", dark,
                new Vector3(0f, 0.04f, -0.02f),
                new Vector3(0.15f, 0.15f, 0.15f));
        }

        private static void DressRaccoon(GameObject root, GameObject hips,
            GameObject spine, GameObject head, GameObject tail,
            Dictionary<string, Material> m)
        {
            var fur = m["Mat_Raccoon"];
            var dark = m["Mat_RaccoonDark"];
            var cream = m["Mat_FoxCream"];
            for (var side = -1; side <= 1; side += 2)
            {
                var ear = Node(head, side < 0 ? "EarL" : "EarR",
                    new Vector3(side * 0.17f, 0.40f, -0.02f),
                    Quaternion.Euler(0f, 0f, -side * 12f));
                Prim(ear, PrimitiveType.Cube, ear.name + "Mesh", fur,
                    new Vector3(0f, 0.09f, 0f),
                    new Vector3(0.12f, 0.20f, 0.07f));
            }
            Prim(head, PrimitiveType.Capsule, "Mask", dark,
                new Vector3(0f, 0.16f, 0.26f),
                new Vector3(0.44f, 0.10f, 0.07f),
                Quaternion.Euler(0f, 0f, 90f));
            Prim(head, PrimitiveType.Sphere, "Muzzle", cream,
                new Vector3(0f, -0.02f, 0.30f),
                new Vector3(0.20f, 0.15f, 0.14f));
            Prim(head, PrimitiveType.Sphere, "Nose", dark,
                new Vector3(0f, 0.04f, 0.39f),
                new Vector3(0.07f, 0.07f, 0.07f));
            Prim(tail, PrimitiveType.Cylinder, "TailPlume", fur,
                new Vector3(0f, 0.24f, 0f),
                new Vector3(0.20f, 0.32f, 0.20f));
            for (var i = 0; i < 3; i++)
                Prim(tail, PrimitiveType.Cylinder,
                    "TailRing" + (i + 1), dark,
                    new Vector3(0f, 0.10f + i * 0.14f, 0f),
                    new Vector3(0.21f, 0.035f, 0.21f));
            Prim(tail, PrimitiveType.Sphere, "TailTip", fur,
                new Vector3(0f, 0.56f, 0f),
                new Vector3(0.19f, 0.19f, 0.19f));
        }
    }
}
