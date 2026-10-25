using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // Original production routes: one sampled centerline supplies collision,
    // presentation, AI and recovery. Optional ribbons branch from and rejoin
    // that road inside a single gate interval. No downloaded assets are used.
    public static class ProductionCourseAuthoring
    {
        public const string Dir = FullContentSetup.ContentDir + "/Courses";
        public const string PrefabPath = Dir + "/ProductionWorld.prefab";
        private const int Samples = 240;
        private const float MainHalfWidth = 8f;
        private const float ShortcutHalfWidth = 4.5f;
        private static readonly string[] Ids = {
            "harbor-hop", "lighthouse-loop", "canyon-dash", "mesa-skyway", "snowpine-circuit", "aurora-pass"
        };
        private static readonly string[] Names = {
            "Harbor Hop", "Lighthouse Loop", "Canyon Dash", "Mesa Skyway", "Snowpine Circuit", "Aurora Pass"
        };
        private static readonly string[] Hebrew = {
            "קפיצת הנמל", "סיבוב המגדלור", "דהרת הקניון", "דרך שמי הרמה", "מסלול אורני השלג", "מעבר הזוהר"
        };
        private static readonly int[] Gates = { 0, 80, 120, 200 };
        private static readonly int[] Entries = { 20, 140 };
        private static readonly int[] Exits = { 65, 185 };

        public static string TrackPath(int index)
        {
            if (index < 0 || index >= Ids.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return Dir + "/" + Ids[index] + ".asset";
        }

        [MenuItem("Family Racing/Production/Author Six Courses")]
        public static void Create()
        {
            Directory.CreateDirectory(Dir);
            AssetDatabase.Refresh();
            var root = new GameObject("ProductionWorld");
            root.AddComponent<LongRaceVisibility>();
            try
            {
                for (int i = 0; i < Ids.Length; i++) BuildCourse(root.transform, i);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static Vector3[] Route(int course)
        {
            var points = new Vector3[Samples];
            var radii = new[] { 175f, 205f, 225f, 250f, 215f, 270f };
            for (int i = 0; i < Samples; i++)
            {
                float a = i * Mathf.PI * 2f / Samples;
                // All are loops, but each has its own bends and aspect ratio.
                float ripple = course == 0 ? .055f * Mathf.Sin(a * 3) :
                    course == 1 ? .085f * Mathf.Sin(a * 2 + .4f) :
                    course == 2 ? .10f * Mathf.Sin(a * 3 - .6f) :
                    course == 3 ? .07f * Mathf.Cos(a * 4) :
                    course == 4 ? .045f * Mathf.Sin(a * 3 + .7f) : .10f * Mathf.Cos(a * 2 - .4f);
                float r = radii[course] * (1 + ripple);
                float aspect = new[] { 1.12f, .91f, 1.16f, .94f, 1.07f, 1.11f }[course];
                float hill = course / 2 == 0 ? .8f : course / 2 == 1 ? 3.2f : 2.5f;
                float y = .2f + hill * Mathf.Sin(a * 2) + hill * .3f * Mathf.Sin(a * 3);
                points[i] = new Vector3(course * 1600f + Mathf.Sin(a) * r * aspect, y, -Mathf.Cos(a) * r);
            }
            return points;
        }

        private static Vector3 Tangent(Vector3[] route, int index, bool loop)
        {
            int before = loop ? (index - 1 + route.Length) % route.Length : Mathf.Max(0, index - 1);
            int after = loop ? (index + 1) % route.Length : Mathf.Min(route.Length - 1, index + 1);
            return (route[after] - route[before]).normalized;
        }
        private static Vector3 Right(Vector3 direction) => new Vector3(direction.z, 0, -direction.x).normalized;
        private static float Heading(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private static Vector3[] Shortcut(Vector3[] route, int lane)
        {
            var start = route[Entries[lane]];
            var end = route[Exits[lane]];
            float handle = Vector3.Distance(start, end) * .18f;
            var b = start + Tangent(route, Entries[lane], true) * handle;
            var c = end - Tangent(route, Exits[lane], true) * handle;
            var points = new Vector3[49];
            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (float)(points.Length - 1), u = 1 - t;
                points[i] = u * u * u * start + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * end;
            }
            return points;
        }

        private static void BuildCourse(Transform world, int index)
        {
            var root = Node(world, Ids[index]);
            var main = Route(index);
            var branches = new[] { Shortcut(main, 0), Shortcut(main, 1) };
            int theme = index / 2;
            var asphalt = Material("Asphalt" + theme, theme == 0 ? "596E79" : theme == 1 ? "756761" : "687C91");
            var shortcut = Material("Shortcut" + theme, theme == 0 ? "C4AA76" : theme == 1 ? "C98251" : "B9D7E3");
            var rail = Material("Rail" + theme, theme == 0 ? "E8D8A6" : theme == 1 ? "E9BB7D" : "9CE1EA");
            var paint = Material("RoadPaint", "FFF3D3");
            Ribbon(root, "MainRoad", main, true, MainHalfWidth, asphalt);
            for (int lane = 0; lane < branches.Length; lane++)
                Ribbon(root, "Shortcut" + (lane + 1) + "Road", branches[lane], false, ShortcutHalfWidth, shortcut);
            var jumpConnections = LandmarkJump(root, main, rail, paint);
            Barriers(root, main, true, MainHalfWidth + .7f, rail, branches.Concat(jumpConnections).ToArray(), null);
            for (int lane = 0; lane < branches.Length; lane++)
                Barriers(root, branches[lane], false, ShortcutHalfWidth + .7f, rail, null, main);
            for (int i = 0; i < Samples; i += 4)
            {
                var dash = Box(root, "CenterStripe", main[i] + Vector3.up * .025f, new Vector3(.18f, .015f, 2.6f), paint);
                dash.transform.rotation = Quaternion.LookRotation(Tangent(main, i, true));
            }
            var track = AuthorTrack(index, main, branches, root);
            foreach (var gate in track.gates) Gantry(root, gate, rail, paint);
            foreach (var pickup in track.pickupSites)
            {
                var ring = Primitive(root, PrimitiveType.Sphere, "PickupMarker", pickup.center + Vector3.up * 2.4f,
                    new Vector3(.7f, .7f, .7f), Material("PickupGold", "F5C34C"));
                ring.transform.rotation = Quaternion.Euler(0, 45, 0);
            }
            Scenery(root, index, main);
        }

        // A third optional spur, independent of both shortcut detectors and the
        // ordinary AI line. The launch has a gentle climb followed by a flat
        // deck: the motor departs a level edge without scraping a sloped lip.
        // A six-metre gap opens onto a 39-metre, twelve-metre-wide landing.
        private static Vector3[][] LandmarkJump(Transform course, Vector3[] main, Material material, Material paint)
        {
            var root = Node(course, "LandmarkJump");
            var forward = Tangent(main, 100, true); forward.y = 0; forward.Normalize();
            var center = main[100] + Right(forward) * 48;
            var approach = center - forward * 40;
            var runout = center + forward * 50;
            var entry = Connector(main[86], approach, Tangent(main, 86, true), forward);
            var exit = Connector(runout, main[116], forward, Tangent(main, 116, true));
            Ribbon(root, "EntryRoad", entry, false, 6f, material);
            Ribbon(root, "ExitRoad", exit, false, 6f, material);
            var ramp = new List<Vector3>();
            for (int metre = -40; metre <= 5; metre++)
            {
                float height = metre <= -25 ? 0 : metre < -1 ? (metre + 25) / 24f * 3 : 3;
                ramp.Add(center + forward * metre + Vector3.up * height);
            }
            Ribbon(root, "RampRoad", ramp.ToArray(), false, 6f, material);
            var landing = Enumerable.Range(11, 40).Select(metre => center + forward * metre).ToArray();
            Ribbon(root, "LandingRoad", landing, false, 6f, material);
            Marker(root, "ApproachStart", approach);
            Marker(root, "Launch", center + forward * 5 + Vector3.up * 3);
            Marker(root, "LandingStart", landing[0]);
            Marker(root, "Runout", runout);
            var stripe = Box(root, "LaunchStripe", center + forward * 4 + Vector3.up * 3.025f,
                new Vector3(12, .015f, .65f), paint);
            stripe.transform.rotation = Quaternion.LookRotation(forward);
            // Painted edge posts frame the landmark; colliders remain outside
            // its generous driving corridor and never span the flight path.
            foreach (int side in new[] { -1, 1 })
            {
                Box(root, "JumpBeacon", center + forward * 5 + Right(forward) * side * 7.5f + Vector3.up * 5,
                    new Vector3(.7f, 4, .7f), paint);
                Box(root, "LandingBeacon", landing[0] + Right(forward) * side * 7.5f + Vector3.up * 2,
                    new Vector3(.7f, 4, .7f), paint);
            }
            return new[] { entry, exit };
        }

        private static void Marker(Transform root, string name, Vector3 position) => Node(root, name).position = position;

        private static Vector3[] Connector(Vector3 start, Vector3 end, Vector3 incoming, Vector3 outgoing)
        {
            float handle = Vector3.Distance(start, end) * .35f;
            var b = start + incoming * handle;
            var c = end - outgoing * handle;
            return Enumerable.Range(0, 33).Select(n => {
                float t = n / 32f, u = 1 - t;
                return start * (u * u * u) + b * (3 * u * u * t) + c * (3 * u * t * t) + end * (t * t * t);
            }).ToArray();
        }

        private static Vector3 PoseBefore(Vector3[] route, int at, float behind, float lane)
        {
            int end = at;
            while (true)
            {
                int start = (end - 1 + route.Length) % route.Length;
                float length = Vector3.Distance(route[start], route[end]);
                if (behind <= length)
                    return Vector3.Lerp(route[end], route[start], behind / length)
                        + Right(Tangent(route, end, true)) * lane + Vector3.up * .08f;
                behind -= length; end = start;
            }
        }

        private static TrackDefinitionAsset AuthorTrack(int index, Vector3[] main, Vector3[][] branches, Transform geometry)
        {
            var track = ScriptableObject.CreateInstance<TrackDefinitionAsset>();
            track.name = Ids[index]; track.courseId = Ids[index];
            track.nameEn = Names[index]; track.nameHe = Hebrew[index]; track.isOpenRoute = false;
            var startDirection = Tangent(main, 0, true);
            track.spawnPosition = PoseBefore(main, 0, 12, 0);
            track.spawnHeadingDegrees = Heading(startDirection);
            track.spawnSlots = Enumerable.Range(0, 4).Select(slot => new TrackDefinitionAsset.SpawnSlot {
                position = PoseBefore(main, 0, 12 + slot / 2 * 7, slot % 2 == 0 ? -2.4f : 2.4f),
                headingDegrees = Heading(startDirection)
            }).ToArray();
            track.aiRoute = main.Select(p => new TrackDefinitionAsset.RoutePoint { x = p.x, z = p.z, radius = 4f }).ToArray();
            track.gates = Gates.Select((at, n) => {
                var direction = Tangent(main, at, true);
                return new TrackDefinitionAsset.Gate {
                    id = n == 0 ? "start" : "gate-" + n, center = main[at], halfWidth = 8f,
                    direction = new Vector2(direction.x, direction.z), recoverPosition = PoseBefore(main, at, 5, 0),
                    recoverHeadingDegrees = Heading(direction)
                };
            }).ToArray();
            track.safePoses = Gates.Select(at => new TrackDefinitionAsset.SpawnSlot {
                position = PoseBefore(main, at, 8, 0),
                headingDegrees = Heading(Tangent(main, at, true))
            }).ToArray();
            track.pickupSites = new[] { 12, 90, 130, 210 }.SelectMany((at, group) => new[] { -3f, 0f, 3f }.Select((offset, lane) => new TrackDefinitionAsset.PickupSite {
                id = Ids[index] + "-pickup-" + group + "-" + lane,
                center = main[at] + Right(Tangent(main, at, true)) * offset, radiusMeters = 1.65f, verticalRangeMeters = 2.5f
            })).ToArray();
            track.shortcutLanes = branches.Select((branch, lane) => {
                int middle = branch.Length / 2;
                var direction = Tangent(branch, middle, false);
                return new TrackDefinitionAsset.ShortcutLane {
                    id = Ids[index] + "-shortcut-" + (lane + 1), center = branch[middle],
                    direction = new Vector2(direction.x, direction.z), halfWidth = ShortcutHalfWidth
                };
            }).ToArray();
            var bounds = new Bounds(main[0], Vector3.zero);
            // Derive permitted airspace from every actual road vertex, including
            // spur connectors, launch deck and the full-width landing apron.
            foreach (var collider in geometry.GetComponentsInChildren<MeshCollider>())
                foreach (var vertex in collider.sharedMesh.vertices)
                    bounds.Encapsulate(collider.transform.TransformPoint(vertex));
            bounds.Expand(new Vector3(20, 35, 20));
            track.recoveryBounds = new[] { new TrackDefinitionAsset.BoundsVolume { min = bounds.min, max = bounds.max } };
            if (track.ToCourse(out var error) == null) throw new InvalidOperationException(error);
            return Save(track, TrackPath(index));
        }

        private static void Ribbon(Transform root, string name, Vector3[] points, bool loop, float halfWidth, Material material)
        {
            int rows = points.Length + (loop ? 1 : 0);
            var vertices = new Vector3[rows * 2]; var uv = new Vector2[rows * 2];
            var triangles = new int[(rows - 1) * 6];
            float distance = 0;
            for (int n = 0; n < rows; n++)
            {
                int at = n % points.Length;
                if (n > 0) distance += Vector3.Distance(points[(n - 1) % points.Length], points[at]);
                var lateral = Right(Tangent(points, at, loop)) * halfWidth;
                vertices[n * 2] = points[at] - lateral; vertices[n * 2 + 1] = points[at] + lateral;
                uv[n * 2] = new Vector2(0, distance / 8); uv[n * 2 + 1] = new Vector2(1, distance / 8);
                if (n < rows - 1)
                {
                    int v = n * 2, t = n * 6;
                    triangles[t] = v; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
                    triangles[t + 3] = v + 1; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
                }
            }
            var key = (root.name == "LandmarkJump" ? root.parent.name + "_Jump" : root.name) + "_" + name;
            var mesh = new Mesh { name = key, vertices = vertices, triangles = triangles, uv = uv };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mesh = Save(mesh, Dir + "/" + key + ".asset");
            var node = Node(root, name).gameObject;
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            node.AddComponent<MeshRenderer>().sharedMaterial = material;
            node.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static float DistanceToRoute(Vector3 point, Vector3[] route, bool loop)
        {
            float best = float.PositiveInfinity;
            int count = loop ? route.Length : route.Length - 1;
            for (int i = 0; i < count; i++)
            {
                var a = route[i]; var ab = route[(i + 1) % route.Length] - a;
                float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / Mathf.Max(ab.sqrMagnitude, .001f));
                best = Mathf.Min(best, Vector3.Distance(point, a + ab * t));
            }
            return best;
        }

        private static void Barriers(Transform root, Vector3[] route, bool loop, float offset, Material material,
            Vector3[][] openings, Vector3[] main)
        {
            int count = loop ? route.Length : route.Length - 1;
            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % route.Length;
                foreach (int side in new[] { -1, 1 })
                {
                    var a = route[i] + Right(Tangent(route, i, loop)) * offset * side;
                    var b = route[next] + Right(Tangent(route, next, loop)) * offset * side;
                    var middle = (a + b) * .5f;
                    float padding = Vector3.Distance(a, b) * .5f;
                    // Open main guardrails wherever a shortcut's real road meets
                    // them; branch rails begin only beyond the ordinary roadway.
                    if (openings != null && openings.Any(p => DistanceToRoute(middle, p, false) < 6f + padding + 1.5f)) continue;
                    if (main != null && DistanceToRoute(middle, main, true) < MainHalfWidth + padding + 2f) continue;
                    var barrier = Box(root, "Barrier_" + (loop ? "Main" : "Branch") + "_" + i + "_" + side,
                        middle + Vector3.up * .65f, new Vector3(.35f, 1.3f, Vector3.Distance(a, b) + .18f), material, true);
                    barrier.transform.rotation = Quaternion.LookRotation(b - a);
                }
            }
        }

        private static void Gantry(Transform root, TrackDefinitionAsset.Gate gate, Material rail, Material paint)
        {
            var direction = new Vector3(gate.direction.x, 0, gate.direction.y);
            foreach (int side in new[] { -1, 1 })
                Box(root, "GatePost", gate.center + Right(direction) * 9.5f * side + Vector3.up * 3.2f,
                    new Vector3(.55f, 6.4f, .55f), rail);
            var beam = Box(root, "GateBeam", gate.center + Vector3.up * 6.3f, new Vector3(19.5f, .6f, .7f), rail);
            beam.transform.rotation = Quaternion.LookRotation(direction);
            var line = Box(root, "GateStripe", gate.center + Vector3.up * .025f, new Vector3(16f, .015f, .5f), paint);
            line.transform.rotation = Quaternion.LookRotation(direction);
        }

        private static void Scenery(Transform root, int index, Vector3[] route)
        {
            int theme = index / 2;
            var earth = Material("Ground" + theme, theme == 0 ? "C9B984" : theme == 1 ? "B96843" : "DDECF0");
            var accent = Material("Scenery" + theme, theme == 0 ? "E2A46D" : theme == 1 ? "D99560" : "477B7A");
            Vector3 center = new Vector3(index * 1600, -8, 0);
            Primitive(root, PrimitiveType.Cylinder, "Landscape", center, new Vector3(830, 1, 830), earth);
            if (theme == 0)
                Box(root, "Ocean", center - Vector3.up * 2, new Vector3(1300, .1f, 1300), Material("Ocean", "388FA4"));
            // Set pieces stay well outside the roadway, including its branches.
            for (int n = 0; n < 36; n++)
            {
                int at = n * Samples / 36;
                var point = route[at] + Right(Tangent(route, at, true)) * (27 + n % 3 * 9);
                if (theme == 0)
                {
                    var building = Box(root, index == 0 ? "HarborWarehouse" : "CoastalCottage", point + Vector3.up * 3,
                        new Vector3(8 + n % 3 * 2, 6, 9), n % 2 == 0 ? accent : earth);
                    building.transform.rotation = Quaternion.Euler(0, n * 37, 0);
                    Box(root, "Roof", point + Vector3.up * 6.3f, new Vector3(10 + n % 3 * 2, .7f, 11), Material("Roof", "637A86"));
                }
                else if (theme == 1)
                {
                    float height = 15 + n % 5 * 5;
                    Primitive(root, PrimitiveType.Cylinder, index == 2 ? "SandstoneSpire" : "LayeredMesa",
                        point + Vector3.up * (height * .5f - 4), new Vector3(13 + n % 4 * 4, height * .5f, 13), accent);
                    Primitive(root, PrimitiveType.Cylinder, "MesaCap", point + Vector3.up * (height - 4),
                        new Vector3(17 + n % 4 * 4, .8f, 17), earth);
                }
                else
                {
                    Box(root, "PineTrunk", point + Vector3.up * 2.2f, new Vector3(.9f, 4.4f, .9f), Material("PineTrunk", "6C635C"));
                    Primitive(root, PrimitiveType.Capsule, "SnowPine", point + Vector3.up * 6,
                        new Vector3(5, 5, 5), accent);
                    Primitive(root, PrimitiveType.Sphere, "PineSnow", point + Vector3.up * 9,
                        new Vector3(4.3f, 2, 4.3f), earth);
                }
            }
            if (index == 1)
            {
                var point = new Vector3(index * 1600 + 45, 9, 0);
                Primitive(root, PrimitiveType.Cylinder, "Lighthouse", point, new Vector3(9, 18, 9), Material("LighthouseIvory", "FFF1CB"));
                Primitive(root, PrimitiveType.Cylinder, "LanternRoom", point + Vector3.up * 19, new Vector3(12, 2, 12), Material("Lantern", "FFD67B"));
            }
            if (index == 3)
                Primitive(root, PrimitiveType.Cylinder, "CentralSkyMesa", new Vector3(index * 1600, 8, 0), new Vector3(100, 14, 90), accent);
            if (index == 5)
            {
                var ice = Material("AuroraIce", "89C8DC");
                for (int n = 0; n < 7; n++)
                    Primitive(root, PrimitiveType.Cube, "AuroraIceNeedle", new Vector3(index * 1600 + (n - 3) * 16, 9 + n % 3 * 4, 0),
                        new Vector3(6, 28 + n % 3 * 8, 6), ice).transform.rotation = Quaternion.Euler(0, n * 22, 12 - n * 4);
            }
        }

        private static Transform Node(Transform parent, string name)
        {
            var node = new GameObject(name).transform; node.SetParent(parent, false); return node;
        }
        private static GameObject Primitive(Transform parent, PrimitiveType type, string name, Vector3 position, Vector3 scale, Material material)
        {
            var node = GameObject.CreatePrimitive(type); node.name = name; node.transform.SetParent(parent, false);
            node.transform.position = position; node.transform.localScale = scale;
            node.GetComponent<Renderer>().sharedMaterial = material; Object.DestroyImmediate(node.GetComponent<Collider>()); return node;
        }
        private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 scale, Material material, bool solid = false)
        {
            var node = Primitive(parent, PrimitiveType.Cube, name, position, scale, material);
            if (solid) node.AddComponent<BoxCollider>(); return node;
        }
        private static Material Material(string name, string hex)
        {
            string path = Dir + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { name = name }; AssetDatabase.CreateAsset(material, path);
            }
            ColorUtility.TryParseHtmlString("#" + hex, out var color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .18f);
            material.enableInstancing = true; EditorUtility.SetDirty(material); return material;
        }
        private static T Save<T>(T authored, string path) where T : Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(authored, path); return authored; }
            EditorUtility.CopySerialized(authored, existing); Object.DestroyImmediate(authored);
            EditorUtility.SetDirty(existing); return existing;
        }
    }
}
