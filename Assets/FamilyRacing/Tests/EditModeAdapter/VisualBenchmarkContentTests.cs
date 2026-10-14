using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // M4 Task-3 content contract: the Sunny Island course visual dresses
    // the proven graybox-01 course without ever moving gameplay
    // geometry, and every referenced art asset carries a license/cost
    // entry in docs/ASSET_INVENTORY.md. These tests run against the real
    // generated assets (VisualBenchmarkSetup.Create() first) so they
    // prove what the benchmark actually loads.
    public sealed class VisualBenchmarkContentTests
    {
        private const string DrivingDir =
            "Assets/FamilyRacing/Generated/Driving";
        private const string GrayboxCoursePath =
            DrivingDir + "/GrayboxCourse.prefab";
        private const string DefinitionPath =
            "Assets/FamilyRacing/Generated/VisualBenchmark/" +
            "visual-benchmark-01.asset";
        private const string InventoryPath = "docs/ASSET_INVENTORY.md";
        private const string ArtRoot =
            "Assets/FamilyRacing/Art/VisualBenchmark";

        // The collision contract: every collider that GAMEPLAY can see
        // must be a byte-for-byte match of the shared graybox prefab's
        // collider set — same names, transforms, sizes, trigger flags
        // and layers — inside the nested GrayboxCourse prefab instance
        // that stays the gameplay authority. The only other colliders
        // allowed anywhere in the prefab are dedicated camera-occluder
        // boxes: non-trigger, renderer-free, on the built-in Ignore
        // Raycast layer (2) that DefaultRaycastLayers excludes, so
        // motor probes, wheel raycasts and the physical backend never
        // see them while the driving camera's ~0 occlusion ray does.
        [Test]
        public void DressingPreservesGameplayGeometry()
        {
            VisualBenchmarkSetup.Create();
            var definition =
                AssetDatabase.LoadAssetAtPath<VisualBenchmarkDefinitionAsset>(
                    DefinitionPath);
            Assert.That(definition, Is.Not.Null);
            Assert.That(definition.presentationVersion,
                Is.EqualTo("visual-1"));

            var sunny = definition.courseVisualPrefab;
            Assert.That(sunny, Is.Not.Null,
                "the definition must bind a course visual prefab");
            Assert.That(AssetDatabase.GetAssetPath(sunny),
                Is.EqualTo(SunnyIslandCourseAuthoring.CourseVisualPath),
                "the benchmark course visual is the authored sunny-island "
                + "prefab, not the graybox placeholder");
            Assert.That(sunny.name,
                Is.EqualTo(SunnyIslandCourseAuthoring.CourseVisualId),
                "the course-visual root carries the stable "
                + "sunny-island-benchmark-01 identity");

            var graybox = AssetDatabase.LoadAssetAtPath<GameObject>(
                GrayboxCoursePath);
            Assert.That(graybox, Is.Not.Null);

            var grayboxInst = (GameObject)PrefabUtility.InstantiatePrefab(
                graybox);
            var sunnyInst = (GameObject)PrefabUtility.InstantiatePrefab(
                sunny);
            try
            {
                var baseline = ColliderSignatures(grayboxInst.transform);
                Assert.That(baseline.Count, Is.EqualTo(16),
                    "graybox-01 baseline: 11 solid colliders + 5 trigger "
                    + "gates (the authored collision contract)");

                GameObject gameplay = null;
                foreach (Transform child in sunnyInst.transform)
                    if (child.name == "GrayboxCourse")
                        gameplay = child.gameObject;
                Assert.That(gameplay, Is.Not.Null,
                    "the sunny-island root must keep a nested "
                    + "GrayboxCourse prefab instance as gameplay "
                    + "authority");
                Assert.That(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(
                        gameplay),
                    Is.EqualTo(GrayboxCoursePath),
                    "the gameplay subtree must be a real instance of the "
                    + "shared GrayboxCourse prefab — never a copy");

                // Every collider that gameplay can reach sits inside the
                // nested instance and matches the baseline exactly. The
                // ONLY colliders permitted outside it are camera-only
                // occluders: non-trigger BoxColliders carrying no
                // renderer, on the Ignore Raycast layer, so they pull
                // the chase camera in without touching physics.
                var allColliders =
                    sunnyInst.GetComponentsInChildren<Collider>(true);
                var cameraOccluders = 0;
                var seen = new HashSet<string>();
                foreach (var collider in allColliders)
                {
                    if (!collider.transform.IsChildOf(
                        gameplay.transform))
                    {
                        var occ = collider as BoxCollider;
                        Assert.That(occ, Is.Not.Null,
                            "dressing collider '" + collider.name
                            + "' must be a BoxCollider camera occluder");
                        Assert.That(occ.isTrigger, Is.False,
                            "camera occluder '" + collider.name
                            + "' must be non-trigger — the camera ray "
                            + "uses QueryTriggerInteraction.Ignore");
                        Assert.That(collider.gameObject.layer,
                            Is.EqualTo(SunnyIslandCourseAuthoring
                                .OccluderLayer),
                            "camera occluder '" + collider.name
                            + "' must sit on the Ignore Raycast layer so "
                            + "DefaultRaycastLayers probes skip it");
                        Assert.That(
                            collider.GetComponent<MeshRenderer>(),
                            Is.Null,
                            "camera occluder '" + collider.name
                            + "' must be invisible — colliders never "
                            + "double as visuals");
                        cameraOccluders++;
                        continue;
                    }
                    var expected = baseline[collider.name];
                    Assert.That(collider.GetType(),
                        Is.EqualTo(expected.type), collider.name);
                    Assert.That(collider.isTrigger,
                        Is.EqualTo(expected.isTrigger), collider.name);
                    Assert.That(collider.gameObject.layer,
                        Is.EqualTo(expected.layer), collider.name);
                    var box = collider as BoxCollider;
                    Assert.That(box, Is.Not.Null, collider.name);
                    AssertVector(box.center, expected.center, 0.0001f,
                        collider.name + " center");
                    AssertVector(box.size, expected.size, 0.0001f,
                        collider.name + " size");
                    var t = collider.transform;
                    AssertVector(t.localPosition, expected.localPosition,
                        0.0001f, collider.name + " localPosition");
                    Assert.That(t.localRotation,
                        Is.EqualTo(expected.localRotation),
                        collider.name + " localRotation");
                    AssertVector(t.localScale, expected.localScale,
                        0.0001f, collider.name + " localScale");
                    seen.Add(collider.name);
                }
                Assert.That(seen.Count, Is.EqualTo(baseline.Count),
                    "every baseline collider is present and identical");
                Assert.That(cameraOccluders,
                    Is.GreaterThanOrEqualTo(20),
                    "tall dressing must carry dedicated camera occluders "
                    + "on the Ignore Raycast layer — found "
                    + cameraOccluders);

                // Visual coverage: every SOLID gameplay collider is fully
                // hidden by its dressing group — contiguous along its
                // long axis, covering across the cross axis, and at
                // least as tall — so no bare collider face or visual
                // gap can read as a passable opening.
                AssertColliderCovered(sunnyInst, gameplay,
                    "BarrierA", "CargoA");
                AssertColliderCovered(sunnyInst, gameplay,
                    "BarrierB", "CargoB");
                AssertColliderCovered(sunnyInst, gameplay,
                    "WallNorth", "WallClad_N");
                AssertColliderCovered(sunnyInst, gameplay,
                    "WallSouth", "WallClad_S");
                AssertColliderCovered(sunnyInst, gameplay,
                    "WallEast", "WallClad_E");
                AssertColliderCovered(sunnyInst, gameplay,
                    "WallWest", "WallClad_W");
                AssertColliderCovered(sunnyInst, gameplay,
                    "IslandNorthEast", "IslandNE_");
                AssertColliderCovered(sunnyInst, gameplay,
                    "IslandSouthEast", "IslandSE_");
                AssertColliderCovered(sunnyInst, gameplay,
                    "Ramp", "Ramp");

                // No rigidbodies anywhere: the physical backend clones
                // this prefab for collision — a body would change it.
                Assert.That(
                    sunnyInst.GetComponentsInChildren<Rigidbody>(true),
                    Is.Empty, "the course visual must stay static");

                // Layer discipline: dressing objects are Default (flat)
                // or the camera-occluder Ignore Raycast layer (tall) —
                // nothing else may invent layers physics could see.
                foreach (var t in
                    sunnyInst.GetComponentsInChildren<Transform>(true))
                {
                    if (t.IsChildOf(gameplay.transform)) continue;
                    Assert.That(
                        t.gameObject.layer == 0
                        || t.gameObject.layer
                            == SunnyIslandCourseAuthoring.OccluderLayer,
                        Is.True, "dressing object '" + t.name
                        + "' is on layer " + t.gameObject.layer
                        + " — only Default or Ignore Raycast are legal");
                }

                // Occluder-class dressing exists and is on the excluded
                // layer, proving the convention is applied, not just
                // asserted.
                var occluders = 0;
                foreach (var t in
                    sunnyInst.GetComponentsInChildren<Transform>(true))
                    if (!t.IsChildOf(gameplay.transform)
                        && t.gameObject.layer
                            == SunnyIslandCourseAuthoring.OccluderLayer)
                        occluders++;
                Assert.That(occluders, Is.GreaterThan(0),
                    "tall dressing must sit on the Ignore Raycast layer");

                // Renderer budget guard: flat-color static dressing must
                // stay cheap enough for the A7 Lite tablet profile.
                var dressingRenderers = 0;
                foreach (var r in
                    sunnyInst.GetComponentsInChildren<MeshRenderer>(true))
                    if (!r.transform.IsChildOf(gameplay.transform))
                        dressingRenderers++;
                Assert.That(dressingRenderers, Is.LessThanOrEqualTo(500),
                    "dressing renderer count " + dressingRenderers
                    + " exceeds the tablet budget");

                // Authored track data is the shared graybox-01 file,
                // unchanged: same gates/route/recovery the gameplay
                // slices measure.
                var track = definition.sourceTrack;
                Assert.That(track, Is.Not.Null);
                Assert.That(track.courseId, Is.EqualTo("graybox-01"));
                Assert.That(AssetDatabase.GetAssetPath(track),
                    Is.EqualTo(DrivingDir + "/graybox-01.asset"));
                Assert.That(track.gates.Length, Is.EqualTo(5));
                CollectionAssert.AreEqual(
                    new[] { "start", "gate-1", "gate-2", "gate-3",
                        "gate-4" },
                    System.Array.ConvertAll(track.gates, g => g.id));
                AssertVector(track.spawnPosition,
                    new Vector3(0f, 0f, -60f), 0.001f, "spawnPosition");
                Assert.That(track.aiRoute.Length, Is.EqualTo(22));
                Assert.That(track.safePoses.Length, Is.EqualTo(6));
                Assert.That(track.spawnSlots.Length, Is.EqualTo(4));
                Assert.That(track.recoveryBounds.Length, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(grayboxInst);
                Object.DestroyImmediate(sunnyInst);
            }
        }

        // Camera-occluder contract, proven on the real prefab: a chase
        // camera ray (DrivingCamera uses RaycastAll with ~0 and
        // QueryTriggerInteraction.Ignore) trailing a vehicle toward a
        // tall dressing element must hit a dedicated layer-2 occluder
        // BEFORE its visuals can clip the lens — and the identical ray
        // under DefaultRaycastLayers (motor probes, wheel suspension)
        // must not see that occluder at all.
        [Test]
        public void CameraOccludersPullInWithoutTouchingPhysics()
        {
            VisualBenchmarkSetup.Create();
            var sunny = AssetDatabase.LoadAssetAtPath<GameObject>(
                SunnyIslandCourseAuthoring.CourseVisualPath);
            Assert.That(sunny, Is.Not.Null);
            var sunnyInst = (GameObject)PrefabUtility.InstantiatePrefab(
                sunny);
            try
            {
                Physics.SyncTransforms();
                GameObject gameplay = null;
                foreach (Transform child in sunnyInst.transform)
                    if (child.name == "GrayboxCourse")
                        gameplay = child.gameObject;
                Assert.That(gameplay, Is.Not.Null);

                // Case 1 — the south-wall parapet band: a vehicle
                // driving north away from the wall at (0,-71) trails
                // the chase camera over the rail (ideal ~8m back and
                // ~3.3m above the 1.2m target height). The 3m wall
                // collider tops out at y=3 — without the layer-2 band
                // the ~4.2m-high ray would sail through rail and posts.
                AssertCameraRayHitsOccluder(
                    new Vector3(0f, 1.2f, -71f),
                    new Vector3(0f, 4.5f, -79.65f),
                    gameplay.transform);

                // Case 2 — the NE shortcut-mouth sign at (42,21): a
                // vehicle leaving the lane mouth trailing the camera
                // through the sign board must pull on the occluder,
                // never clip the pole/board visuals.
                AssertCameraRayHitsOccluder(
                    new Vector3(42f, 1.2f, 24f),
                    new Vector3(42f, 4.5f, 15.35f),
                    gameplay.transform);

                // Layer 2 stays out of the gameplay probe mask the
                // motor/suspension code queries.
                Assert.That(
                    (Physics.DefaultRaycastLayers
                        & (1 << SunnyIslandCourseAuthoring
                            .OccluderLayer)) == 0,
                    Is.True,
                    "Ignore Raycast must stay out of "
                    + "DefaultRaycastLayers — a camera occluder must "
                    + "never become a gameplay surface");
            }
            finally
            {
                Object.DestroyImmediate(sunnyInst);
            }
        }

        private static void AssertCameraRayHitsOccluder(
            Vector3 target, Vector3 ideal, Transform gameplay)
        {
            var delta = ideal - target;
            var distance = delta.magnitude;
            var dir = delta / distance;
            // Exactly the camera's query: every non-trigger collider.
            var hits = Physics.RaycastAll(target, dir, distance, ~0,
                QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits,
                (a, b) => a.distance.CompareTo(b.distance));
            Assert.That(hits.Length, Is.GreaterThan(0),
                "camera ray " + target + " -> " + ideal
                + " hit nothing — the occluder is missing");
            var first = hits[0];
            Assert.That(
                first.collider.transform.IsChildOf(gameplay),
                Is.False,
                "first camera hit '" + first.collider.name
                + "' is gameplay geometry — the occluder must screen "
                + "before or outside it");
            Assert.That(first.collider.gameObject.layer,
                Is.EqualTo(SunnyIslandCourseAuthoring.OccluderLayer),
                "first camera hit '" + first.collider.name
                + "' is on layer " + first.collider.gameObject.layer
                + ", not the camera-occluder layer");
            // The same ray under the gameplay probe mask must skip the
            // occluder entirely.
            var probe = Physics.RaycastAll(target, dir, distance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            foreach (var hit in probe)
                Assert.That(hit.collider.gameObject.layer,
                    Is.Not.EqualTo(
                        SunnyIslandCourseAuthoring.OccluderLayer),
                    "gameplay probe saw occluder '" + hit.collider.name
                    + "' on layer 2 — DefaultRaycastLayers must "
                    + "exclude it");
        }

        // Every renderer in the named dressing group must, together,
        // hide the whole collider: contiguous coverage along the
        // collider's long axis (no gap reads as an opening), each
        // covering renderer spanning the cross axis, at least as tall
        // as the collider, and reaching down to its base.
        private static void AssertColliderCovered(GameObject sunnyInst,
            GameObject gameplay, string colliderName, string prefix)
        {
            BoxCollider col = null;
            foreach (var c in
                gameplay.GetComponentsInChildren<BoxCollider>(true))
                if (c.name == colliderName) col = c;
            Assert.That(col, Is.Not.Null,
                "gameplay collider '" + colliderName + "' not found");
            var cb = col.bounds;
            var longX = cb.size.x >= cb.size.z;
            var loMin = longX ? cb.min.x : cb.min.z;
            var loMax = longX ? cb.max.x : cb.max.z;
            var crMin = longX ? cb.min.z : cb.min.x;
            var crMax = longX ? cb.max.z : cb.max.x;
            const float eps = 0.06f;

            var intervals = new List<Vector2>();
            var floor = float.MaxValue;
            var matched = 0;
            foreach (var r in
                sunnyInst.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.transform.IsChildOf(gameplay.transform)) continue;
                if (!UnderPrefix(r.transform, sunnyInst.transform,
                        prefix))
                    continue;
                matched++;
                var b = r.bounds;
                // Only renderers that span the collider's cross axis and
                // reach its top count toward coverage.
                var bCrMin = longX ? b.min.z : b.min.x;
                var bCrMax = longX ? b.max.z : b.max.x;
                if (bCrMin > crMin + eps || bCrMax < crMax - eps)
                    continue;
                if (b.max.y < cb.max.y - eps) continue;
                intervals.Add(new Vector2(
                    longX ? b.min.x : b.min.z,
                    longX ? b.max.x : b.max.z));
                if (b.min.y < floor) floor = b.min.y;
            }
            Assert.That(matched, Is.GreaterThan(0),
                "no dressing renderers match prefix '" + prefix
                + "' for collider '" + colliderName + "'");
            Assert.That(intervals.Count, Is.GreaterThan(0),
                "no '" + prefix + "' renderer spans and tops collider '"
                + colliderName + "'");
            Assert.That(floor, Is.LessThanOrEqualTo(cb.min.y + eps),
                "'" + prefix + "' dressing floats above the base of '"
                + colliderName + "'");

            // Merge intervals along the long axis: a single merged run
            // must contain the whole collider extent.
            intervals.Sort((a, b) => a.x.CompareTo(b.x));
            var curMin = intervals[0].x;
            var curMax = intervals[0].y;
            var contiguous = curMin <= loMin + eps;
            foreach (var iv in intervals)
            {
                if (iv.x > curMax + eps) contiguous = false;
                if (iv.y > curMax) curMax = iv.y;
            }
            Assert.That(curMin, Is.LessThanOrEqualTo(loMin + eps),
                "'" + prefix + "' dressing starts past the beginning of '"
                + colliderName + "' — the collider's leading face shows");
            Assert.That(curMax, Is.GreaterThanOrEqualTo(loMax - eps),
                "'" + prefix + "' dressing ends before the end of '"
                + colliderName + "' — the collider's trailing face shows");
            Assert.That(contiguous, Is.True,
                "'" + prefix + "' dressing has a gap along '"
                + colliderName + "' — a bare collider face would read "
                + "as a passable opening");
        }

        private static bool UnderPrefix(Transform t, Transform root,
            string prefix)
        {
            for (; t != null && t != root; t = t.parent)
                if (t.name.StartsWith(prefix)) return true;
            return false;
        }

        private sealed class Sig
        {
            internal System.Type type;
            internal bool isTrigger;
            internal int layer;
            internal Vector3 center, size;
            internal Vector3 localPosition, localScale;
            internal Quaternion localRotation;
        }

        private static Dictionary<string, Sig> ColliderSignatures(
            Transform root)
        {
            var map = new Dictionary<string, Sig>();
            foreach (var collider in
                root.GetComponentsInChildren<Collider>(true))
            {
                var box = collider as BoxCollider;
                var t = collider.transform;
                map[collider.name] = new Sig
                {
                    type = collider.GetType(),
                    isTrigger = collider.isTrigger,
                    layer = collider.gameObject.layer,
                    center = box != null ? box.center : Vector3.zero,
                    size = box != null ? box.size : Vector3.zero,
                    localPosition = t.localPosition,
                    localRotation = t.localRotation,
                    localScale = t.localScale
                };
            }
            return map;
        }

        private static void AssertVector(Vector3 actual, Vector3 expected,
            float tolerance, string label)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance),
                label + ".x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance),
                label + ".y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance),
                label + ".z");
        }

        // Every art asset the course visual or the benchmark scene
        // references must carry a docs/ASSET_INVENTORY.md row with
        // source, license, US$0 cost and role — and every inventoried
        // GUID must resolve to a real project asset.
        [Test]
        public void AllReferencedAssetsHaveInventoryEntries()
        {
            VisualBenchmarkSetup.Create();
            var inventoryFile = Path.Combine(
                Directory.GetCurrentDirectory(),
                InventoryPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(File.Exists(inventoryFile), Is.True,
                InventoryPath + " must exist — it is the license/cost "
                + "manifest for the benchmark's asset provenance");
            var rows = ParseInventory(File.ReadAllText(inventoryFile));
            Assert.That(rows.Count, Is.GreaterThan(0),
                "the inventory table has no guid rows");

            foreach (var row in rows)
            {
                Assert.That(row.path, Is.Not.Empty,
                    "inventory row " + row.guid + " has no path");
                Assert.That(row.source, Is.Not.Empty,
                    "inventory row " + row.guid + " has no source");
                Assert.That(row.license, Is.Not.Empty,
                    "inventory row " + row.guid + " has no license "
                    + "evidence");
                Assert.That(row.cost, Is.EqualTo("US$0"),
                    "inventory row " + row.guid + " records a non-zero "
                    + "cost — purchases need controller approval first");
                Assert.That(row.role, Is.Not.Empty,
                    "inventory row " + row.guid + " has no role");
                // Project-asset rows must resolve: the recorded GUID
                // maps to a real asset at exactly the recorded path —
                // a stale or invented GUID fails here.
                if (row.path.StartsWith("Assets/"))
                {
                    var resolved = AssetDatabase.GUIDToAssetPath(
                        row.guid);
                    Assert.That(resolved, Is.EqualTo(row.path),
                        "inventory guid " + row.guid
                        + " resolves to '" + resolved
                        + "', not the recorded '" + row.path + "'");
                }
            }

            // The two Unity built-in resource GUIDs are pinned to their
            // real meanings — they used to be swapped/misdescribed:
            //   e000...0000 = unity default resources = the primitive
            //     meshes (cube/sphere/cylinder/plane) the dressing is
            //     built from;
            //   f000...0000 = unity builtin-extra = the built-in shader
            //     classes (procedural skybox) — NOT primitive meshes.
            var byGuid = new Dictionary<string, Row>();
            foreach (var row in rows) byGuid[row.guid] = row;
            Assert.That(
                byGuid.ContainsKey(
                    "0000000000000000e000000000000000"),
                Is.True, "builtin primitive-mesh guid e000...0000 "
                + "needs an inventory row");
            Assert.That(
                byGuid.ContainsKey(
                    "0000000000000000f000000000000000"),
                Is.True, "builtin-extra shader guid f000...0000 "
                + "needs an inventory row");
            var eRow = byGuid["0000000000000000e000000000000000"];
            var fRow = byGuid["0000000000000000f000000000000000"];
            StringAssert.Contains("primitive",
                (eRow.path + " " + eRow.source + " " + eRow.role)
                    .ToLowerInvariant(),
                "e000...0000 must be described as the primitive-mesh "
                + "resource");
            StringAssert.Contains("shader",
                (fRow.path + " " + fRow.source + " " + fRow.role)
                    .ToLowerInvariant(),
                "f000...0000 must be described as the builtin-extra "
                + "shader resource");
            StringAssert.DoesNotContain("primitive",
                fRow.path.ToLowerInvariant(),
                "f000...0000 is builtin-extra shaders, not primitives "
                + "— do not describe it as the mesh resource");
            StringAssert.DoesNotContain("shader",
                eRow.path.ToLowerInvariant(),
                "e000...0000 is the primitive-mesh resource, not "
                + "shaders");

            var referenced = new HashSet<string>();
            // Every project asset the course visual depends on —
            // authored art AND the nested graybox gameplay prefab —
            // must be inventoried; the scene contributes its art-root
            // references (skybox, lighting).
            foreach (var path in AssetDatabase.GetDependencies(
                SunnyIslandCourseAuthoring.CourseVisualPath, true))
            {
                if (!path.StartsWith("Assets/")) continue;
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(guid, Is.Not.Empty);
                referenced.Add(guid);
            }
            foreach (var path in AssetDatabase.GetDependencies(
                VisualBenchmarkSetup.ScenePath, true))
            {
                if (!path.StartsWith(ArtRoot + "/")) continue;
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(guid, Is.Not.Empty);
                referenced.Add(guid);
            }
            Assert.That(referenced.Count, Is.GreaterThan(0),
                "the course visual should reference authored art assets");
            // Built-in dependencies cannot be enumerated by path —
            // the e000/f000 row pins above cover them.

            var inventoriedGuids = new HashSet<string>();
            foreach (var row in rows) inventoriedGuids.Add(row.guid);
            foreach (var guid in referenced)
                Assert.That(inventoriedGuids.Contains(guid), Is.True,
                    "referenced asset guid " + guid + " ("
                    + AssetDatabase.GUIDToAssetPath(guid)
                    + ") has no inventory entry — record source, license "
                    + "and US$0 cost in " + InventoryPath);
        }

        private sealed class Row
        {
            internal string guid, path, source, license, cost, role;
        }

        private static List<Row> ParseInventory(string markdown)
        {
            var rows = new List<Row>();
            var inTable = false;
            foreach (var raw in markdown.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Contains("INVENTORY-TABLE-BEGIN"))
                { inTable = true; continue; }
                if (line.Contains("INVENTORY-TABLE-END")) break;
                if (!inTable || !line.StartsWith("|")) continue;
                var cells = line.Split('|');
                if (cells.Length < 7) continue;
                var guid = cells[1].Trim();
                if (!Regex.IsMatch(guid, "^[0-9a-fA-F]{32}$")) continue;
                rows.Add(new Row
                {
                    guid = guid.ToLowerInvariant(),
                    path = cells[2].Trim(),
                    source = cells[3].Trim(),
                    license = cells[4].Trim(),
                    cost = cells[5].Trim(),
                    role = cells[6].Trim()
                });
            }
            return rows;
        }

        // A setup re-run must leave every byte of the authored Sunny
        // Island environment untouched — same rule the benchmark's own
        // generated folder already proves.
        [Test]
        public void SetupRerunKeepsSunnyIslandArt()
        {
            VisualBenchmarkSetup.Create();
            var before = HashTree(ArtRoot);
            Assert.That(before.Count, Is.GreaterThan(0),
                "the sunny-island environment must exist after setup");
            VisualBenchmarkSetup.Create();
            var after = HashTree(ArtRoot);
            Assert.That(after.Count, Is.EqualTo(before.Count));
            foreach (var pair in before)
                Assert.That(after[pair.Key], Is.EqualTo(pair.Value),
                    "setup re-run modified authored art: " + pair.Key);
        }

        private static Dictionary<string, string> HashTree(string relDir)
        {
            var map = new Dictionary<string, string>();
            var root = Directory.GetCurrentDirectory();
            var dir = Path.Combine(root, relDir.Replace('/', '\\'));
            if (!Directory.Exists(dir)) return map;
            using (var sha = SHA256.Create())
            foreach (var file in Directory.GetFiles(dir, "*",
                SearchOption.AllDirectories))
            {
                var rel = file.Substring(root.Length + 1)
                    .Replace('\\', '/');
                map[rel] = System.BitConverter.ToString(
                    sha.ComputeHash(File.ReadAllBytes(file)));
            }
            return map;
        }
    }
}
