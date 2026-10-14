using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // M4 Task-3 Sunny Island benchmark environment: procedural, original
    // dressing over the proven graybox-01 layout, saved as
    // Art/VisualBenchmark/Environment/sunny-island-benchmark-01.prefab.
    //
    // Collision contract (binding, enforced by
    // VisualBenchmarkContentTests.DressingPreservesGameplayGeometry):
    //   - the nested GrayboxCourse prefab instance is the ONLY gameplay
    //     geometry — dressing meshes never carry colliders;
    //   - occluder-class dressing (anything tall enough to sit between a
    //     camera and the road) lives on the built-in Ignore Raycast layer
    //     (2): invisible to motor probes and wheel suspension raycasts
    //     (Physics.DefaultRaycastLayers), and paired with a dedicated
    //     non-trigger camera-occluder collider on the same layer so the
    //     driving camera's explicit ~0 occlusion ray pulls in BEFORE the
    //     lens can clip inside the visual;
    //   - flat dressing (roads, water, paint, wall cladding) stays on
    //     Default and carries no collider at all;
    //   - decorative barrier visuals always cover their collider — never
    //     recessed — so a driver may brush paint, never clip a gap
    //     (the coverage intervals are pinned by the EditMode test).
    //
    // Author() is deterministic: materials are created-or-updated in
    // place and the prefab is saved over its own path, so GUIDs survive
    // regeneration. VisualBenchmarkSetup calls Ensure() only, which runs
    // the full author solely when the prefab is missing — hand-authored
    // tweaks on top are never overwritten by setup.
    public static class SunnyIslandCourseAuthoring
    {
        public const string EnvironmentDir =
            "Assets/FamilyRacing/Art/VisualBenchmark/Environment";
        public const string MaterialsDir = EnvironmentDir + "/Materials";
        public const string CourseVisualPath =
            EnvironmentDir + "/sunny-island-benchmark-01.prefab";
        // The stable course-visual identity published through
        // PresentationState.courseVisualId: the prefab's root name.
        public const string CourseVisualId = "sunny-island-benchmark-01";
        // Built-in "Ignore Raycast" layer: excluded from every driving
        // and physics probe mask in this project, but still raycast-
        // visible to the driving camera's occlusion pull-in.
        public const int OccluderLayer = 2;

        private const string GrayboxCoursePath =
            "Assets/FamilyRacing/Generated/Driving/GrayboxCourse.prefab";
        private const string SimpleLitShader =
            "Universal Render Pipeline/Simple Lit";

        private const StaticEditorFlags DressingStatic =
            StaticEditorFlags.BatchingStatic
            | StaticEditorFlags.ContributeGI
            | StaticEditorFlags.OccluderStatic
            | StaticEditorFlags.OccludeeStatic;

        // Only when the course-visual prefab is missing does setup
        // (re)build the whole treatment; existing assets are authored
        // state and survive every setup re-run.
        public static void Ensure()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(CourseVisualPath)
                    != null)
                return;
            Author();
        }

        public static void Author()
        {
            EnsureFolders();
            var mats = EnsureMaterials();
            BuildPrefab(mats);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/FamilyRacing/Art"))
                AssetDatabase.CreateFolder("Assets/FamilyRacing", "Art");
            if (!AssetDatabase.IsValidFolder(
                    "Assets/FamilyRacing/Art/VisualBenchmark"))
                AssetDatabase.CreateFolder(
                    "Assets/FamilyRacing/Art", "VisualBenchmark");
            if (!AssetDatabase.IsValidFolder(EnvironmentDir))
                AssetDatabase.CreateFolder(
                    "Assets/FamilyRacing/Art/VisualBenchmark", "Environment");
            if (!AssetDatabase.IsValidFolder(MaterialsDir))
                AssetDatabase.CreateFolder(EnvironmentDir, "Materials");
        }

        // ---------------------------------------------------------------
        // Materials — one shared URP Simple Lit per palette role. The
        // "atlas" is the palette itself: flat colors, opaque, low
        // smoothness except water/hull. Existing assets are updated in
        // place (never recreated) so GUIDs and material overrides hold.
        private sealed class Palette
        {
            internal readonly Dictionary<string, Material> map =
                new Dictionary<string, Material>();
            internal Material Get(string name)
            {
                Material mat;
                return map.TryGetValue(name, out mat) ? mat : null;
            }
        }

        private static Palette EnsureMaterials()
        {
            var palette = new Palette();
            var shader = Shader.Find(SimpleLitShader);
            if (shader == null)
                throw new System.InvalidOperationException(
                    "SunnyIslandCourseAuthoring: missing shader '"
                    + SimpleLitShader + "'");
            Define(palette, shader, "Mat_DeckSand", Hex(0xE6, 0xD3, 0xA3), 0.05f);
            Define(palette, shader, "Mat_RoadDust", Hex(0x93, 0x77, 0x5C), 0.05f);
            Define(palette, shader, "Mat_Boardwalk", Hex(0xA8, 0x81, 0x4F), 0.10f);
            Define(palette, shader, "Mat_WallWhite", Hex(0xF2, 0xEF, 0xE5), 0.12f);
            Define(palette, shader, "Mat_TealTrim", Hex(0x1F, 0x94, 0x9E), 0.15f);
            Define(palette, shader, "Mat_Terracotta", Hex(0xCC, 0x61, 0x3D), 0.10f);
            Define(palette, shader, "Mat_Cream", Hex(0xF5, 0xE8, 0xCC), 0.10f);
            Define(palette, shader, "Mat_PastelBlue", Hex(0x8C, 0xBD, 0xD1), 0.10f);
            Define(palette, shader, "Mat_PastelYellow", Hex(0xF2, 0xD1, 0x73), 0.10f);
            Define(palette, shader, "Mat_PastelPink", Hex(0xE6, 0x99, 0x9E), 0.10f);
            Define(palette, shader, "Mat_Water", Hex(0x29, 0xA8, 0xB8), 0.55f);
            Define(palette, shader, "Mat_DeepWater", Hex(0x14, 0x66, 0x80), 0.45f);
            Define(palette, shader, "Mat_Foam", Hex(0xE6, 0xF8, 0xFF), 0.10f);
            Define(palette, shader, "Mat_Foliage", Hex(0x4D, 0x8F, 0x47), 0.10f);
            Define(palette, shader, "Mat_Trunk", Hex(0x80, 0x5C, 0x38), 0.10f);
            Define(palette, shader, "Mat_Grass", Hex(0x73, 0xA8, 0x57), 0.08f);
            Define(palette, shader, "Mat_StoneGray", Hex(0x99, 0x94, 0x88), 0.10f);
            Define(palette, shader, "Mat_GateYellow", Hex(0xFA, 0xC7, 0x2E), 0.15f);
            Define(palette, shader, "Mat_FlagRed", Hex(0xDB, 0x43, 0x38), 0.15f);
            Define(palette, shader, "Mat_CheckBlack", Hex(0x1F, 0x1C, 0x1F), 0.05f);
            Define(palette, shader, "Mat_CheckWhite", Hex(0xF0, 0xF0, 0xEB), 0.05f);
            Define(palette, shader, "Mat_HullBlue", Hex(0x4D, 0x80, 0x9E), 0.20f);
            Define(palette, shader, "Mat_DriftPaint", Hex(0xD9, 0x8C, 0x40), 0.10f);
            Define(palette, shader, "Mat_SandDark", Hex(0xC7, 0xAD, 0x80), 0.08f);
            return palette;
        }

        private static void Define(Palette palette, Shader shader,
            string name, Color color, float smoothness)
        {
            var path = MaterialsDir + "/" + name + ".mat";
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
            mat.SetColor("_Color", color); // legacy slot — same value
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Surface", 0f); // opaque — no transparency here
            palette.map[name] = mat;
            EditorUtility.SetDirty(mat);
        }

        private static Color Hex(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f);
        }

        // ---------------------------------------------------------------
        // The course-visual root: a nested GrayboxCourse prefab instance
        // (gameplay authority — every collider the motor probes) plus a
        // collider-free "Dressing" sibling. The graybox renderers are
        // disabled (ground keeps a material override) so each collision
        // surface is covered by authored dressing of matching or larger
        // extent — the visual never reads narrower than the real bound.
        private static void BuildPrefab(Palette mats)
        {
            var graybox = AssetDatabase.LoadAssetAtPath<GameObject>(
                GrayboxCoursePath);
            if (graybox == null)
                throw new System.InvalidOperationException(
                    "SunnyIslandCourseAuthoring: " + GrayboxCoursePath
                    + " is missing — run FoundationProjectSetup first.");

            var root = new GameObject(CourseVisualId);
            try
            {
                var gameplay = (GameObject)PrefabUtility.InstantiatePrefab(
                    graybox);
                gameplay.name = "GrayboxCourse";
                gameplay.transform.SetParent(root.transform, false);
                SkinGameplay(gameplay, mats);

                var dressing = new GameObject("Dressing");
                dressing.transform.SetParent(root.transform, false);
                var d = dressing.transform;
                BuildDeck(d, mats);
                BuildShoreline(d, mats);
                BuildHarborWall(d, mats);
                BuildBarrierStacks(d, mats);
                BuildIslands(d, mats);
                BuildRampDressing(d, mats);
                BuildGateDressing(d, mats);
                BuildProps(d, mats);

                PrefabUtility.SaveAsPrefabAsset(root, CourseVisualPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // The gameplay instance stays byte-authoritative for collision:
        // only renderer state is dressed (disabled visuals, a deck
        // material on the ground, static flags for batching/lightmaps).
        private static void SkinGameplay(GameObject gameplay, Palette mats)
        {
            foreach (var renderer in
                gameplay.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name == "Ground")
                {
                    renderer.sharedMaterial = mats.Get("Mat_DeckSand");
                    GameObjectUtility.SetStaticEditorFlags(
                        renderer.gameObject, DressingStatic);
                    continue;
                }
                renderer.enabled = false;
            }
        }

        // ---------------------------------------------------------------
        // Primitive helpers — colliders are stripped on every dressing
        // object, no exceptions.
        private static GameObject Prim(Transform parent, string name,
            PrimitiveType type, Vector3 pos, Vector3 scale, Quaternion rot,
            Material mat, int layer)
        {
            var obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = pos;
            obj.transform.localRotation = rot;
            obj.transform.localScale = scale;
            var collider = obj.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            obj.layer = layer;
            GameObjectUtility.SetStaticEditorFlags(obj, DressingStatic);
            if (mat != null)
                obj.GetComponent<Renderer>().sharedMaterial = mat;
            return obj;
        }

        private static GameObject Box(Transform parent, string name,
            float x, float y, float z, float sx, float sy, float sz,
            Material mat)
        {
            return Prim(parent, name, PrimitiveType.Cube,
                new Vector3(x, y, z), new Vector3(sx, sy, sz),
                Quaternion.identity, mat, 0);
        }

        private static GameObject Occ(Transform parent, string name,
            PrimitiveType type, Vector3 pos, Vector3 scale, Quaternion rot,
            Material mat)
        {
            return Prim(parent, name, type, pos, scale, rot, mat,
                OccluderLayer);
        }

        // Dedicated camera-occluder collider: a non-trigger BoxCollider
        // on a collider-free GameObject on the Ignore Raycast layer.
        // Motor probes, wheel suspension and the physical backend query
        // DefaultRaycastLayers (which excludes layer 2), so these never
        // change gameplay; the driving camera's ~0 occlusion ray DOES
        // hit them, so tall dressing genuinely pulls the camera in front
        // of its visuals instead of clipping through them.
        private static void CamOcc(Transform parent, string name,
            Vector3 localCenter, Vector3 size)
        {
            CamOcc(parent, name, localCenter, size, Quaternion.identity);
        }

        private static void CamOcc(Transform parent, string name,
            Vector3 localCenter, Vector3 size, Quaternion localRotation)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent, false);
            g.transform.localPosition = localCenter;
            g.transform.localRotation = localRotation;
            g.layer = OccluderLayer;
            var box = g.AddComponent<BoxCollider>();
            box.size = size;
            box.isTrigger = false; // the camera ray ignores triggers
        }

        // Flat top surfaces: road/lane/paint slabs — stacked at distinct
        // heights (road 0.025, boardwalk 0.045, paint 0.055-0.065) so
        // overlapping ribbons can never z-fight.
        private static void Slab(Transform parent, string name,
            float x, float z, float sx, float sz, float top, Material mat)
        {
            Prim(parent, name, PrimitiveType.Cube,
                new Vector3(x, top - 0.01f, z), new Vector3(sx, 0.02f, sz),
                Quaternion.identity, mat, 0);
        }

        // ---------------------------------------------------------------
        // Main-line road ribbons (Mat_RoadDust on deck sand) and
        // boardwalk shortcut lanes (Mat_Boardwalk). The two islands split
        // the route into a longer main branch and an inside shortcut —
        // both lanes read as ground, never as water.
        private static void BuildDeck(Transform d, Palette mats)
        {
            var road = mats.Get("Mat_RoadDust");
            var walk = mats.Get("Mat_Boardwalk");
            var yellow = mats.Get("Mat_GateYellow");
            var black = mats.Get("Mat_CheckBlack");
            var white = mats.Get("Mat_CheckWhite");
            var drift = mats.Get("Mat_DriftPaint");

            // South straight: spawn -> gate-1.
            Slab(d, "Road_South", 0f, -11.5f, 22f, 85f, 0.035f, road);
            // The two legal lanes around BarrierB (north and south).
            Slab(d, "Road_WestHigh", -25f, 24f, 54f, 18f, 0.035f, road);
            Slab(d, "Road_WestLow", -29f, 3f, 46f, 18f, 0.035f, road);
            // Ramp approach and landing, west leg x=-60.
            Slab(d, "Road_RampRun", -62f, 21f, 20f, 42f, 0.035f, road);
            // North leg through gate-2 to the drift apron.
            Slab(d, "Road_North", 12f, 58f, 156f, 18f, 0.035f, road);
            // Corner wedge into the east leg.
            Slab(d, "Road_NorthEast", 90f, 46f, 14f, 6f, 0.035f, road);
            // East leg through gate-3, wide enough for the island lane.
            Slab(d, "Road_East", 95f, -3f, 26f, 90f, 0.035f, road);
            // South return through gate-4 and the swing under the line.
            Slab(d, "Road_SouthReturn", 62f, -57f, 86f, 18f, 0.035f, road);
            Slab(d, "Road_SouthWest", -20f, -63f, 80f, 18f, 0.035f, road);

            // Boardwalk shortcut lanes: inside (south/east) of
            // IslandNorthEast and inside (west) of IslandSouthEast.
            Slab(d, "Lane_NE_Apron", 53f, 28f, 18f, 12f, 0.045f, walk);
            Slab(d, "Lane_NE_Face", 53f, 42f, 18f, 12f, 0.045f, walk);
            Slab(d, "Lane_NE_Entry", 40f, 24f, 14f, 10f, 0.045f, walk);
            Slab(d, "Lane_SE_Face", 53f, -29f, 16f, 42f, 0.045f, walk);
            Slab(d, "Lane_SE_Link", 64f, -10f, 10f, 10f, 0.045f, walk);

            // Drift pad paint inside the north-east apron.
            Slab(d, "DriftPad", 45f, 66f, 40f, 20f, 0.045f, drift);

            // Gate stripes on the mandatory line (yellow route language).
            Slab(d, "Gate_Start", 0f, -60f, 24f, 0.9f, 0.055f, yellow);
            Slab(d, "Gate_1", 0f, 25f, 24f, 0.9f, 0.055f, yellow);
            Slab(d, "Gate_2", 55f, 55f, 0.9f, 24f, 0.055f, yellow);
            Slab(d, "Gate_3", 95f, 0f, 24f, 0.9f, 0.055f, yellow);
            Slab(d, "Gate_4", 50f, -55f, 0.9f, 24f, 0.055f, yellow);

            // Start/finish checker line across the south straight.
            for (var i = 0; i < 12; i++)
                for (var row = 0; row < 2; row++)
                    Slab(d, "Check_" + i + "_" + row,
                        -11f + i * 2f, -61f + row * 1.3f,
                        2f, 1.3f, 0.065f,
                        ((i + row) & 1) == 0 ? black : white);

            // Direction chevrons before the four big turns.
            Chevron(d, -30f, 18f, -45f, yellow);  // into the west lanes
            Chevron(d, -60f, 54f, 60f, yellow);   // ramp landing -> east
            Chevron(d, 88f, 38f, 170f, yellow);   // into the east leg
            Chevron(d, 96f, -44f, 250f, yellow);  // into the south return
            Chevron(d, 10f, -64f, -5f, yellow);   // under the line -> north
        }

        private static void Chevron(Transform parent, float x, float z,
            float yaw, Material mat)
        {
            var rot = Quaternion.Euler(0f, yaw, 0f);
            Prim(parent, "Chevron", PrimitiveType.Cube,
                new Vector3(x, 0.055f, z), new Vector3(3.2f, 0.02f, 1.1f),
                rot, mat, 0);
        }

        // ---------------------------------------------------------------
        // Turquoise water all around, a darker deep ring, a sand skirt
        // sloping off the sea walls, a foam waterline and corner rocks.
        private static void BuildShoreline(Transform d, Palette mats)
        {
            var water = mats.Get("Mat_Water");
            var deep = mats.Get("Mat_DeepWater");
            var sand = mats.Get("Mat_SandDark");
            var foam = mats.Get("Mat_Foam");
            var stone = mats.Get("Mat_StoneGray");

            Prim(d, "Water_Deep", PrimitiveType.Plane,
                new Vector3(0f, -0.75f, 0f), new Vector3(80f, 1f, 60f),
                Quaternion.identity, deep, 0);
            Prim(d, "Water_Shallow", PrimitiveType.Plane,
                new Vector3(0f, -0.55f, 0f), new Vector3(52f, 1f, 40f),
                Quaternion.identity, water, 0);

            // Sand skirts sloping from each wall foot into the water.
            Skirt(d, "Skirt_N", 0f, 83.5f, 0f, sand);
            Skirt(d, "Skirt_S", 0f, -83.5f, 180f, sand);
            Skirt(d, "Skirt_E", 123.5f, 0f, 90f, sand);
            Skirt(d, "Skirt_W", -123.5f, 0f, -90f, sand);

            // Foam waterline hugging the skirts.
            Prim(d, "Foam_N", PrimitiveType.Cube,
                new Vector3(0f, -0.45f, 85f), new Vector3(238f, 0.06f, 1.6f),
                Quaternion.identity, foam, 0);
            Prim(d, "Foam_S", PrimitiveType.Cube,
                new Vector3(0f, -0.45f, -85f), new Vector3(238f, 0.06f, 1.6f),
                Quaternion.identity, foam, 0);
            Prim(d, "Foam_E", PrimitiveType.Cube,
                new Vector3(125f, -0.45f, 0f), new Vector3(1.6f, 0.06f, 158f),
                Quaternion.identity, foam, 0);
            Prim(d, "Foam_W", PrimitiveType.Cube,
                new Vector3(-125f, -0.45f, 0f), new Vector3(1.6f, 0.06f, 158f),
                Quaternion.identity, foam, 0);

            // Corner boulders closing the skirt corners.
            var rock = new Vector3(9f, 4f, 9f);
            Occ(d, "Rock_NE", PrimitiveType.Sphere,
                new Vector3(122f, -1f, 82f), rock, Quaternion.identity, stone);
            Occ(d, "Rock_NW", PrimitiveType.Sphere,
                new Vector3(-122f, -1f, 82f), rock, Quaternion.identity, stone);
            Occ(d, "Rock_SE", PrimitiveType.Sphere,
                new Vector3(122f, -1f, -82f), rock, Quaternion.identity, stone);
            Occ(d, "Rock_SW", PrimitiveType.Sphere,
                new Vector3(-122f, -1f, -82f), rock, Quaternion.identity, stone);

            // A pair of tiny palm islets in the water for the horizon.
            Islet(d, mats, 150f, 100f);
            Islet(d, mats, -160f, 60f);
        }

        private static void Skirt(Transform parent, string name,
            float x, float z, float yaw, Material mat)
        {
            var size = Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad)) > 0.5f
                ? new Vector3(9f, 0.7f, 244f)
                : new Vector3(244f, 0.7f, 9f);
            Prim(parent, name, PrimitiveType.Cube,
                new Vector3(x, -0.4f, z), size,
                Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(-14f, 0f, 0f),
                mat, 0);
        }

        private static void Islet(Transform parent, Palette mats,
            float x, float z)
        {
            var g = Group(parent, "Islet", x, z);
            Occ(g, "Mound", PrimitiveType.Sphere,
                new Vector3(0f, -1.6f, 0f), new Vector3(14f, 4f, 14f),
                Quaternion.identity, mats.Get("Mat_SandDark"));
            Palm(g, mats, 0f, 1.9f, 0f);
        }

        // ---------------------------------------------------------------
        // Sea-wall dressing: whitewashed parapet + teal cap rail + posts.
        // Cladding is always >= the collider face, never recessed.
        private static void BuildHarborWall(Transform d, Palette mats)
        {
            var white = mats.Get("Mat_WallWhite");
            var teal = mats.Get("Mat_TealTrim");
            Box(d, "WallClad_N", 0f, 1.5f, 79.55f, 241f, 3.1f, 1.2f, white);
            Box(d, "WallClad_S", 0f, 1.5f, -79.55f, 241f, 3.1f, 1.2f, white);
            Box(d, "WallClad_E", 119.55f, 1.5f, 0f, 1.2f, 3.1f, 161f, white);
            Box(d, "WallClad_W", -119.55f, 1.5f, 0f, 1.2f, 3.1f, 161f, white);
            Box(d, "Rail_N", 0f, 3.25f, 79.55f, 241f, 0.32f, 1.5f, teal);
            Box(d, "Rail_S", 0f, 3.25f, -79.55f, 241f, 0.32f, 1.5f, teal);
            Box(d, "Rail_E", 119.55f, 3.25f, 0f, 1.5f, 0.32f, 161f, teal);
            Box(d, "Rail_W", -119.55f, 3.25f, 0f, 1.5f, 0.32f, 161f, teal);
            for (var x = -105f; x <= 105f; x += 30f)
            {
                Box(d, "Post_N", x, 3.7f, 79.55f, 0.5f, 0.9f, 0.5f, teal);
                Box(d, "Post_S", x, 3.7f, -79.55f, 0.5f, 0.9f, 0.5f, teal);
            }
            for (var z = -75f; z <= 75f; z += 30f)
            {
                Box(d, "Post_E", 119.55f, 3.7f, z, 0.5f, 0.9f, 0.5f, teal);
                Box(d, "Post_W", -119.55f, 3.7f, z, 0.5f, 0.9f, 0.5f, teal);
            }
            // The rail + posts rise ~1.1 m above the 3 m wall colliders:
            // thin camera-only occluder bands along each parapet top.
            CamOcc(d, "WallTopOcc_N", new Vector3(0f, 3.7f, 79.55f),
                new Vector3(241f, 1.5f, 1.6f));
            CamOcc(d, "WallTopOcc_S", new Vector3(0f, 3.7f, -79.55f),
                new Vector3(241f, 1.5f, 1.6f));
            CamOcc(d, "WallTopOcc_E", new Vector3(119.55f, 3.7f, 0f),
                new Vector3(1.6f, 1.5f, 161f));
            CamOcc(d, "WallTopOcc_W", new Vector3(-119.55f, 3.7f, 0f),
                new Vector3(1.6f, 1.5f, 161f));
        }

        // ---------------------------------------------------------------
        // The chicane barriers dressed as painted cargo rows. Coverage is
        // exact: each collider is fully hidden — BarrierA (6x2x30 at
        // x=25, z in [-40,-10]) gets three 10.4 m crates at z=-15/-25/-35
        // (contiguous [-40.2,-9.8], no visual gap inside the solid);
        // BarrierB (30x2x6 at z=10, x in [-45,-15]) gets the same row
        // yawed 90 degrees at x=-40/-30/-20 (contiguous [-45.2,-14.8]).
        // Wider and taller than the collider on every axis, so a driver
        // may brush paint but can never see a bare collider face or aim
        // at a false opening. Pinned by DressingPreservesGameplayGeometry.
        private static void BuildBarrierStacks(Transform d, Palette mats)
        {
            CargoStack(d, mats, 25f, -15f, 0f, "CargoA1",
                mats.Get("Mat_PastelBlue"));
            CargoStack(d, mats, 25f, -25f, 0f, "CargoA2",
                mats.Get("Mat_PastelYellow"));
            CargoStack(d, mats, 25f, -35f, 0f, "CargoA3",
                mats.Get("Mat_PastelPink"));
            CargoStack(d, mats, -40f, 10f, 90f, "CargoB1",
                mats.Get("Mat_PastelPink"));
            CargoStack(d, mats, -30f, 10f, 90f, "CargoB2",
                mats.Get("Mat_PastelBlue"));
            CargoStack(d, mats, -20f, 10f, 90f, "CargoB3",
                mats.Get("Mat_PastelYellow"));
        }

        private static void CargoStack(Transform d, Palette mats,
            float x, float z, float yaw, string name, Material mat)
        {
            var g = Group(d, name, x, z);
            g.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Occ(g, "Crate", PrimitiveType.Cube,
                new Vector3(0f, 1.3f, 0f), new Vector3(6.5f, 2.6f, 10.4f),
                Quaternion.identity, mat);
            Occ(g, "Lid", PrimitiveType.Cube,
                new Vector3(0f, 2.72f, 0f), new Vector3(6.7f, 0.24f, 10.6f),
                Quaternion.identity, mats.Get("Mat_Boardwalk"));
            // The stack tops the 2 m collider by ~0.9 m — camera-only
            // occluder so the chase lens cannot clip a lid.
            CamOcc(g, "CamOcc", new Vector3(0f, 1.5f, 0f),
                new Vector3(6.7f, 3.0f, 10.6f));
        }

        // ---------------------------------------------------------------
        // The two islands: stone retaining base clad, garden top, village
        // houses (LOD), the lighthouse landmark on the north-east island
        // and palms. All occluder-class dressing sits on layer 2.
        private static void BuildIslands(Transform d, Palette mats)
        {
            var stone = mats.Get("Mat_StoneGray");
            var grass = mats.Get("Mat_Grass");

            Box(d, "IslandNE_Clad", 32f, 1f, 44f, 22.4f, 2.05f, 14.4f, stone);
            Box(d, "IslandNE_Top", 32f, 2.05f, 44f, 21f, 0.16f, 13f, grass);
            Box(d, "IslandSE_Clad", 72f, 1f, -32f, 20.4f, 2.05f, 16.4f, stone);
            Box(d, "IslandSE_Top", 72f, 2.05f, -32f, 19f, 0.16f, 15f, grass);

            var ne = Group(d, "Village_NE", 32f, 44f);
            ne.localPosition = new Vector3(32f, 2.13f, 44f);
            House(ne, mats, -4.5f, 0f, 0f, 7f, 4.4f, 6f,
                mats.Get("Mat_Cream"), 0f);
            House(ne, mats, 6.5f, 0f, 3.4f, 5.4f, 3.8f, 4.6f,
                mats.Get("Mat_PastelBlue"), 90f);
            Lighthouse(ne, mats, -8.4f, 4.4f);
            Palm(ne, mats, 3f, 0f, 4.8f);

            var se = Group(d, "Village_SE", 72f, -32f);
            se.localPosition = new Vector3(72f, 2.13f, -32f);
            House(se, mats, -4.5f, 0f, -2f, 6.4f, 4.2f, 5.6f,
                mats.Get("Mat_Cream"), 0f);
            House(se, mats, 5.6f, 0f, 3.4f, 5f, 3.6f, 4.4f,
                mats.Get("Mat_PastelYellow"), 90f);
            Palm(se, mats, -6f, 0f, -5f);
            Palm(se, mats, 7f, 0f, -4f);
        }

        private static Transform Group(Transform parent, string name,
            float x, float z)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent, false);
            g.transform.localPosition = new Vector3(x, 0f, z);
            return g.transform;
        }

        // A house: cream/pastel body, overhanging gable roof (45-degree
        // prism), teal door and two window blocks at LOD0; a single
        // silhouette body+roof at LOD1.
        private static void House(Transform parent, Palette mats,
            float x, float y, float z, float w, float h, float depth,
            Material bodyMat, float ridgeYaw)
        {
            var host = Group(parent, "House", x, z);
            host.localPosition = new Vector3(x, y, z);
            var lod0 = Group(host, "LOD0", 0f, 0f);
            var lod1 = Group(host, "LOD1", 0f, 0f);
            Occ(lod0, "Body", PrimitiveType.Cube,
                new Vector3(0f, h * 0.5f, 0f),
                new Vector3(w, h, depth), Quaternion.identity, bodyMat);
            Roof(lod0, mats, w, h, depth, ridgeYaw);
            Occ(lod0, "Door", PrimitiveType.Cube,
                new Vector3(0f, 0.9f, depth * 0.51f),
                new Vector3(1.1f, 1.8f, 0.2f), Quaternion.identity,
                mats.Get("Mat_TealTrim"));
            Occ(lod0, "WindowL", PrimitiveType.Cube,
                new Vector3(-w * 0.28f, h * 0.62f, depth * 0.51f),
                new Vector3(0.9f, 0.9f, 0.2f), Quaternion.identity,
                mats.Get("Mat_TealTrim"));
            Occ(lod0, "WindowR", PrimitiveType.Cube,
                new Vector3(w * 0.28f, h * 0.62f, depth * 0.51f),
                new Vector3(0.9f, 0.9f, 0.2f), Quaternion.identity,
                mats.Get("Mat_TealTrim"));
            Occ(lod1, "Body", PrimitiveType.Cube,
                new Vector3(0f, h * 0.5f, 0f),
                new Vector3(w, h, depth), Quaternion.identity, bodyMat);
            Roof(lod1, mats, w, h, depth, ridgeYaw);
            AddLod(host.gameObject, lod0, lod1, 0.10f);
            // Camera occluder over body + gable roof (apex ~h + 0.8w).
            var top = h + w * 0.85f;
            CamOcc(host, "CamOcc", new Vector3(0f, top * 0.5f, 0f),
                new Vector3(w * 1.3f, top, depth * 1.2f));
        }

        private static void Roof(Transform parent, Palette mats,
            float w, float h, float depth, float ridgeYaw)
        {
            // A cube rotated 45 degrees about the ridge axis makes a
            // diamond-section prism — the rounded chunky gable.
            Occ(parent, "Roof", PrimitiveType.Cube,
                new Vector3(0f, h + w * 0.18f, 0f),
                new Vector3(w * 1.15f, w * 0.62f, depth * 1.15f),
                Quaternion.Euler(0f, ridgeYaw, 45f),
                mats.Get("Mat_Terracotta"));
        }

        private static void Lighthouse(Transform parent, Palette mats,
            float x, float z)
        {
            var host = Group(parent, "Lighthouse", x, z);
            Occ(host, "Tower", PrimitiveType.Cylinder,
                new Vector3(0f, 4f, 0f), new Vector3(2.4f, 4f, 2.4f),
                Quaternion.identity, mats.Get("Mat_WallWhite"));
            Occ(host, "Band", PrimitiveType.Cylinder,
                new Vector3(0f, 6.4f, 0f), new Vector3(2.6f, 0.8f, 2.6f),
                Quaternion.identity, mats.Get("Mat_FlagRed"));
            Occ(host, "Gallery", PrimitiveType.Cylinder,
                new Vector3(0f, 8.2f, 0f), new Vector3(3.2f, 0.4f, 3.2f),
                Quaternion.identity, mats.Get("Mat_TealTrim"));
            Occ(host, "Lamp", PrimitiveType.Sphere,
                new Vector3(0f, 8.9f, 0f), new Vector3(1.4f, 1.4f, 1.4f),
                Quaternion.identity, mats.Get("Mat_GateYellow"));
            var lod1 = Group(host, "LOD1", 0f, 0f);
            var g1 = Occ(lod1, "Silhouette", PrimitiveType.Cylinder,
                new Vector3(0f, 4.4f, 0f), new Vector3(2.8f, 4.4f, 2.8f),
                Quaternion.identity, mats.Get("Mat_WallWhite"));
            // LOD0 is the tower's own transform (no dedicated group).
            var lod0 = new List<Renderer>();
            foreach (var r in host.GetComponentsInChildren<Renderer>(true))
                if (!r.transform.IsChildOf(lod1)) lod0.Add(r);
            var lodGroup = host.gameObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(new[]
            {
                new LOD(0.10f, lod0.ToArray()),
                new LOD(0.015f, new Renderer[]
                    { g1.GetComponent<Renderer>() })
            });
            // ~9.6 m to the lamp top — the tallest occluder on course.
            CamOcc(host, "CamOcc", new Vector3(0f, 4.9f, 0f),
                new Vector3(3.4f, 9.8f, 3.4f));
        }

        private static void Palm(Transform parent, Palette mats,
            float x, float y, float z)
        {
            var host = Group(parent, "Palm", x, z);
            host.localPosition = new Vector3(x, y, z);
            Occ(host, "Trunk", PrimitiveType.Cylinder,
                new Vector3(0f, 1.6f, 0f), new Vector3(0.36f, 1.6f, 0.36f),
                Quaternion.Euler(0f, 0f, 6f), mats.Get("Mat_Trunk"));
            var canopy = mats.Get("Mat_Foliage");
            for (var i = 0; i < 5; i++)
            {
                var a = i * (Mathf.PI * 2f / 5f);
                Occ(host, "Leaf" + i, PrimitiveType.Sphere,
                    new Vector3(Mathf.Cos(a) * 0.9f, 3.4f,
                        Mathf.Sin(a) * 0.9f),
                    new Vector3(1.9f, 0.7f, 1.9f),
                    Quaternion.Euler(0f, 0f, 0f), canopy);
            }
            Occ(host, "Crown", PrimitiveType.Sphere,
                new Vector3(0f, 3.5f, 0f), new Vector3(1.5f, 1.1f, 1.5f),
                Quaternion.identity, canopy);
            var lod1 = Group(host, "LOD1", 0f, 0f);
            var g1 = Occ(lod1, "Silhouette", PrimitiveType.Sphere,
                new Vector3(0f, 2.4f, 0f), new Vector3(2.4f, 3.4f, 2.4f),
                Quaternion.identity, canopy);
            var lod0 = new List<Renderer>();
            foreach (var r in host.GetComponentsInChildren<Renderer>(true))
                if (!r.transform.IsChildOf(lod1)) lod0.Add(r);
            var lodGroup = host.gameObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(new[]
            {
                new LOD(0.12f, lod0.ToArray()),
                new LOD(0.015f, new Renderer[]
                    { g1.GetComponent<Renderer>() })
            });
            // Canopy tops ~4.1 m — well inside the chase-camera arc.
            CamOcc(host, "CamOcc", new Vector3(0f, 2.3f, 0f),
                new Vector3(3.9f, 4.6f, 3.9f));
        }

        private static void AddLod(GameObject host, Transform lod0,
            Transform lod1, float threshold)
        {
            var near = new List<Renderer>();
            var far = new List<Renderer>();
            foreach (var r in lod0.GetComponentsInChildren<Renderer>(true))
                near.Add(r);
            foreach (var r in lod1.GetComponentsInChildren<Renderer>(true))
                far.Add(r);
            var group = host.AddComponent<LODGroup>();
            group.SetLODs(new[]
            {
                new LOD(threshold, near.ToArray()),
                new LOD(0.015f, far.ToArray())
            });
        }

        // ---------------------------------------------------------------
        // The landmark coastal jump: a boardwalk skin exactly covering
        // the rotated ramp collider (12 x 0.6 x 16 at -14 degrees),
        // side boards and red pennants — red exists only here.
        private static void BuildRampDressing(Transform d, Palette mats)
        {
            var rot = Quaternion.Euler(-14f, 0f, 0f);
            Prim(d, "RampDeck", PrimitiveType.Cube,
                new Vector3(-60f, 1.25f, 30f), new Vector3(12.3f, 0.7f, 16.3f),
                rot, mats.Get("Mat_Boardwalk"), 0);
            // Toe skirt so the ramp reads as meeting the ground.
            Box(d, "RampToe", -60f, 0.25f, 21.4f, 12.3f, 0.5f, 1.6f,
                mats.Get("Mat_Boardwalk"));
            // Side rails along the ramp edges (off the drive line).
            foreach (var side in new[] { -1f, 1f })
            {
                Prim(d, "RampRail", PrimitiveType.Cube,
                    new Vector3(-60f + side * 6.3f, 1.6f, 30f),
                    new Vector3(0.3f, 0.9f, 16f), rot,
                    mats.Get("Mat_Trunk"), 0);
                // Rails crest ~2.4 m above the ramp deck's own collider.
                CamOcc(d, "RampRailOcc",
                    new Vector3(-60f + side * 6.3f, 1.6f, 30f),
                    new Vector3(0.5f, 1.2f, 16.4f), rot);
            }
            // Red pennants at the crest and approach.
            Pennant(d, mats, -67f, 44f, mats.Get("Mat_FlagRed"));
            Pennant(d, mats, -53f, 44f, mats.Get("Mat_FlagRed"));
            Pennant(d, mats, -67f, 16f, mats.Get("Mat_FlagRed"));
            Pennant(d, mats, -53f, 16f, mats.Get("Mat_FlagRed"));
        }

        // ---------------------------------------------------------------
        // Gate dressing: yellow pennant posts at both ends of every gate
        // (outside the 24 m corridor) plus teal shortcut-mouth signs.
        private static void BuildGateDressing(Transform d, Palette mats)
        {
            var yellow = mats.Get("Mat_GateYellow");
            var teal = mats.Get("Mat_TealTrim");
            Pennant(d, mats, -14f, -60f, yellow);
            Pennant(d, mats, 14f, -60f, yellow);
            Pennant(d, mats, -14f, 25f, yellow);
            Pennant(d, mats, 14f, 25f, yellow);
            Pennant(d, mats, 55f, 41f, yellow);
            Pennant(d, mats, 55f, 69f, yellow);
            Pennant(d, mats, 95f, -15f, yellow);
            Pennant(d, mats, 95f, 15f, yellow);
            Pennant(d, mats, 50f, -42f, yellow);
            Pennant(d, mats, 50f, -69f, yellow);

            ShortcutSign(d, mats, 42f, 21f, 0f);    // NE mouth, west side
            ShortcutSign(d, mats, 57f, 53f, 180f);  // NE exit into gate-2
            ShortcutSign(d, mats, 55f, -5f, 0f);    // SE mouth, north end
            ShortcutSign(d, mats, 56f, -54f, 180f); // SE exit, south end
        }

        private static void Pennant(Transform d, Palette mats,
            float x, float z, Material flagMat)
        {
            var g = Group(d, "Pennant", x, z);
            Occ(g, "Pole", PrimitiveType.Cylinder,
                new Vector3(0f, 1.9f, 0f), new Vector3(0.16f, 1.9f, 0.16f),
                Quaternion.identity, mats.Get("Mat_Trunk"));
            Occ(g, "Flag", PrimitiveType.Cube,
                new Vector3(0.55f, 3.5f, 0f), new Vector3(1.1f, 0.55f, 0.08f),
                Quaternion.identity, flagMat);
            CamOcc(g, "CamOcc", new Vector3(0.3f, 2.0f, 0f),
                new Vector3(1.9f, 4.0f, 0.35f));
        }

        private static void ShortcutSign(Transform d, Palette mats,
            float x, float z, float yaw)
        {
            var g = Group(d, "ShortcutSign", x, z);
            g.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Occ(g, "Pole", PrimitiveType.Cylinder,
                new Vector3(0f, 1.2f, 0f), new Vector3(0.14f, 1.2f, 0.14f),
                Quaternion.identity, mats.Get("Mat_Trunk"));
            Occ(g, "Board", PrimitiveType.Cube,
                new Vector3(0f, 2.3f, 0f), new Vector3(1.5f, 0.7f, 0.12f),
                Quaternion.identity, mats.Get("Mat_TealTrim"));
            Occ(g, "Arrow", PrimitiveType.Cube,
                new Vector3(0.35f, 2.3f, 0.1f), new Vector3(0.7f, 0.34f, 0.06f),
                Quaternion.Euler(0f, 0f, 45f), mats.Get("Mat_CheckWhite"));
            CamOcc(g, "CamOcc", new Vector3(0.1f, 1.4f, 0f),
                new Vector3(1.7f, 2.8f, 0.4f));
        }

        // ---------------------------------------------------------------
        // Harbor props: anchored boats and pier posts in the water only.
        private static void BuildProps(Transform d, Palette mats)
        {
            Boat(d, mats, 70f, -93f, 8f);
            Boat(d, mats, -30f, -94f, -14f);
            Boat(d, mats, 132f, 30f, 90f);
            var trunk = mats.Get("Mat_Trunk");
            for (var x = -80f; x <= 80f; x += 32f)
                Occ(d, "PierPost", PrimitiveType.Cylinder,
                    new Vector3(x, 0.4f, -87f), new Vector3(0.3f, 2f, 0.3f),
                    Quaternion.identity, trunk);
        }

        private static void Boat(Transform d, Palette mats,
            float x, float z, float yaw)
        {
            var g = Group(d, "Boat", x, z);
            g.localRotation = Quaternion.Euler(0f, yaw, 0f);
            g.localPosition = new Vector3(x, -0.55f, z);
            Occ(g, "Hull", PrimitiveType.Cube,
                new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1.4f, 2.4f),
                Quaternion.identity, mats.Get("Mat_HullBlue"));
            Occ(g, "Bow", PrimitiveType.Cube,
                new Vector3(3.2f, 0.55f, 0f), new Vector3(1.6f, 1.3f, 1.6f),
                Quaternion.Euler(0f, 45f, 0f), mats.Get("Mat_HullBlue"));
            Occ(g, "Cabin", PrimitiveType.Cube,
                new Vector3(-1.2f, 1.6f, 0f), new Vector3(2f, 1.4f, 1.8f),
                Quaternion.identity, mats.Get("Mat_Cream"));
            Occ(g, "Mast", PrimitiveType.Cylinder,
                new Vector3(0.9f, 2.8f, 0f), new Vector3(0.16f, 2.8f, 0.16f),
                Quaternion.identity, mats.Get("Mat_Trunk"));
            var lod1 = Group(g, "LOD1", 0f, 0f);
            var g1 = Occ(lod1, "Silhouette", PrimitiveType.Cube,
                new Vector3(0f, 0.7f, 0f), new Vector3(6.6f, 1.8f, 2.6f),
                Quaternion.identity, mats.Get("Mat_HullBlue"));
            var lod0 = new List<Renderer>();
            foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                if (!r.transform.IsChildOf(lod1)) lod0.Add(r);
            var lodGroup = g.gameObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(new[]
            {
                new LOD(0.10f, lod0.ToArray()),
                new LOD(0.015f, new Renderer[]
                    { g1.GetComponent<Renderer>() })
            });
            // Hull + mast top out ~5.6 m in group space.
            CamOcc(g, "CamOcc", new Vector3(0.5f, 2.6f, 0f),
                new Vector3(7.4f, 6.0f, 3.4f));
        }
    }
}
