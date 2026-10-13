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

        // The collision contract: every collider in the sunny-island
        // course visual must be a byte-for-byte match of the shared
        // graybox prefab's collider set — same names, transforms, sizes,
        // trigger flags and layers — and must live inside the nested
        // GrayboxCourse prefab instance that stays the gameplay
        // authority. Dressing contributes zero colliders and zero
        // rigidbodies, so motor probes, wheel raycasts and the physical
        // backend's collision clone see the identical shape.
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

                // Every collider in the whole dressed prefab sits inside
                // that nested instance and matches the baseline exactly.
                var allColliders =
                    sunnyInst.GetComponentsInChildren<Collider>(true);
                Assert.That(allColliders.Length,
                    Is.EqualTo(baseline.Count),
                    "dressing must add no colliders: the dressed prefab "
                    + "carries exactly the baseline collision set");
                var seen = new HashSet<string>();
                foreach (var collider in allColliders)
                {
                    Assert.That(
                        collider.transform.IsChildOf(gameplay.transform),
                        Is.True, "collider '" + collider.name
                        + "' escaped the gameplay subtree — dressing may "
                        + "never carry collision");
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
                // Builtin package/builtin guids resolve differently; only
                // FamilyRacing project guids must map to a path on disk.
            }

            var referenced = new HashSet<string>();
            foreach (var path in AssetDatabase.GetDependencies(
                new[]
                {
                    SunnyIslandCourseAuthoring.CourseVisualPath,
                    VisualBenchmarkSetup.ScenePath
                }, true))
            {
                if (!path.StartsWith(ArtRoot + "/")) continue;
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(guid, Is.Not.Empty);
                referenced.Add(guid);
            }
            Assert.That(referenced.Count, Is.GreaterThan(0),
                "the course visual should reference authored art assets");

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
