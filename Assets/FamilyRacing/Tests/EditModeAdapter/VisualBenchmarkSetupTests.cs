using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing.Tests
{
    // M4 Task-1 reproducible-content proof: VisualBenchmarkSetup.Create()
    // builds the benchmark scene + definition once, validates references,
    // and never regenerates authored content on re-run. The benchmark is
    // the SAME course/rules/tuning as the graybox driving slice under a
    // distinct content version ("visual-1"), so a benchmark player can
    // never silently join a graybox LAN race.
    public sealed class VisualBenchmarkSetupTests
    {
        private const string BenchmarkDir =
            "Assets/FamilyRacing/Generated/VisualBenchmark";
        private const string DrivingDir =
            "Assets/FamilyRacing/Generated/Driving";
        private const string DefinitionPath =
            BenchmarkDir + "/visual-benchmark-01.asset";
        private const string ScenePath =
            BenchmarkDir + "/VisualBenchmark.unity";
        private const string TrackPath = DrivingDir + "/graybox-01.asset";

        private static Dictionary<string, string> GuidSnapshot()
        {
            var map = new Dictionary<string, string>();
            foreach (var guid in AssetDatabase.FindAssets(
                "", new[] { BenchmarkDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                map[path] = guid;
            }
            return map;
        }

        private static Dictionary<string, string> HashSnapshot()
        {
            var map = new Dictionary<string, string>();
            var root = Directory.GetCurrentDirectory();
            var dir = Path.Combine(root, BenchmarkDir);
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

        // A re-run must produce the same asset set, the same GUIDs and
        // byte-identical files: authored meshes, materials, lighting and
        // definition edits must survive repeated setup untouched.
        [Test]
        public void SetupTwicePreservesAuthoredAssets()
        {
            VisualBenchmarkSetup.Create();
            var firstGuids = GuidSnapshot();
            var firstHashes = HashSnapshot();
            Assert.That(firstGuids.Count, Is.GreaterThanOrEqualTo(2),
                "the benchmark folder holds the definition and the scene");

            VisualBenchmarkSetup.Create();
            var secondGuids = GuidSnapshot();
            var secondHashes = HashSnapshot();
            Assert.That(secondGuids.Count, Is.EqualTo(firstGuids.Count),
                "a re-run must not grow the generated asset set");
            foreach (var pair in firstGuids)
            {
                string guid;
                Assert.That(secondGuids.TryGetValue(pair.Key, out guid),
                    Is.True, "second run lost generated asset " + pair.Key);
                Assert.That(guid, Is.EqualTo(pair.Value),
                    "GUID drift on " + pair.Key
                    + " — generated assets must keep their identities");
            }
            Assert.That(secondHashes.Count, Is.EqualTo(firstHashes.Count),
                "a re-run must not add or drop files");
            foreach (var pair in firstHashes)
            {
                string hash;
                Assert.That(secondHashes.TryGetValue(pair.Key, out hash),
                    Is.True, "second run lost file " + pair.Key);
                Assert.That(hash, Is.EqualTo(pair.Value),
                    "content changed on " + pair.Key
                    + " — setup must not regenerate authored assets");
            }
        }

        // The benchmark scene runs the same validated rules as the
        // graybox slice: the shared graybox-01 course (identical gates,
        // route, recovery), the same car/motorcycle tunings and the same
        // event set — only the content identity differs ("visual-1" vs
        // "driving-0.1") so LAN admission can never mix the two.
        [Test]
        public void BenchmarkSceneUsesExistingRules()
        {
            VisualBenchmarkSetup.Create();
            var definition =
                AssetDatabase.LoadAssetAtPath<VisualBenchmarkDefinitionAsset>(
                    DefinitionPath);
            Assert.That(definition, Is.Not.Null,
                "the benchmark definition asset must be generated");
            string definitionError;
            Assert.That(definition.Validate(out definitionError), Is.True,
                "the generated definition must validate: " + definitionError);
            Assert.That(definition.presentationVersion,
                Is.EqualTo("visual-1"));
            var sourceTrack =
                AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(TrackPath);
            Assert.That(definition.sourceTrack, Is.EqualTo(sourceTrack),
                "the benchmark is measured on the shared graybox-01 course");
            Assert.That(definition.courseVisualPrefab, Is.Not.Null,
                "a visible course prefab must be bound");

            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath),
                Is.Not.Null, "VisualBenchmark.unity must be generated");
            var scene = EditorSceneManager.OpenScene(
                ScenePath, OpenSceneMode.Additive);
            // The setup's scene generation may leave this as the ONLY
            // loaded scene — closing the last scene is unsupported, so
            // close only when something else is still open.
            var canClose = SceneManager.sceneCount > 1;
            try
            {
                DrivingWorld world = null;
                FoundationApp app = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (app == null)
                        app = root.GetComponentInChildren<FoundationApp>(
                            true);
                    if (world == null)
                        world = root.GetComponentInChildren<DrivingWorld>(
                            true);
                }
                Assert.That(app, Is.Not.Null,
                    "the scene carries the app composition");
                Assert.That(world, Is.Not.Null,
                    "the scene carries a DrivingWorld");

                var serialized = new SerializedObject(world);
                var contentVersion =
                    serialized.FindProperty("contentVersion");
                Assert.That(contentVersion, Is.Not.Null,
                    "the world's content version field exists");
                Assert.That(contentVersion.stringValue,
                    Is.EqualTo(definition.presentationVersion),
                    "the scene's content identity is the definition's "
                    + "presentation version");
                Assert.That(contentVersion.stringValue,
                    Is.Not.EqualTo("driving-0.1"),
                    "the benchmark must not reuse the graybox content "
                    + "version — LAN admission requires matching versions");

                var track = serialized.FindProperty("track");
                Assert.That(track.objectReferenceValue,
                    Is.EqualTo(definition.sourceTrack),
                    "the world's track is the definition's sourceTrack");
                var coursePrefab =
                    serialized.FindProperty("coursePrefab");
                Assert.That(coursePrefab.objectReferenceValue,
                    Is.EqualTo(definition.courseVisualPrefab),
                    "the visible course is the definition's prefab");

                // Same course/gates as the shared track: the catalog must
                // validate into the identical checkpoint sequence.
                string courseError;
                var course = definition.sourceTrack.ToCourse(
                    out courseError);
                Assert.That(course, Is.Not.Null, courseError);
                Assert.That(course.checkpoints.Count, Is.EqualTo(5),
                    "the authored five-gate graybox loop is intact");
                CollectionAssert.AreEqual(
                    new[] { "start", "gate-1", "gate-2", "gate-3",
                        "gate-4" },
                    course.checkpoints.ConvertAll(g => g.id),
                    "gate order matches the shared course rules");

                // Same tuning: both generated vehicles validate against
                // the authored Core presets.
                var vehicles = serialized.FindProperty("vehicles");
                Assert.That(vehicles.arraySize, Is.EqualTo(2),
                    "car and motorcycle are wired");
                var tunings = new List<VehicleTuning>();
                var vehicleIds = new List<string>();
                for (var i = 0; i < vehicles.arraySize; i++)
                {
                    var vehicle = vehicles.GetArrayElementAtIndex(i)
                        .objectReferenceValue as VehicleDefinitionAsset;
                    Assert.That(vehicle, Is.Not.Null,
                        "vehicle slot " + i + " resolves");
                    string error;
                    var tuning = vehicle.ToTuning(out error);
                    Assert.That(tuning, Is.Not.Null, error);
                    tunings.Add(tuning);
                    vehicleIds.Add(tuning.vehicleId);
                }
                CollectionAssert.AreEquivalent(
                    new[] { "car-01", "motorcycle-01" }, vehicleIds);

                // Same events as the graybox slice: solo practice/intro/
                // free-drive plus both LAN class events.
                var events = serialized.FindProperty("events");
                var eventIds = new List<string>();
                var defs = new List<EventDefinition>();
                for (var i = 0; i < events.arraySize; i++)
                {
                    var evt = events.GetArrayElementAtIndex(i)
                        .objectReferenceValue as EventDefinitionAsset;
                    Assert.That(evt, Is.Not.Null,
                        "event slot " + i + " resolves");
                    string error;
                    var def = evt.ToEvent(out error);
                    Assert.That(def, Is.Not.Null, error);
                    defs.Add(def);
                    eventIds.Add(def.id);
                }
                CollectionAssert.AreEquivalent(
                    new[] { "practice-02", "intro-01", "tutorial-01",
                        "lan-car-01", "lan-motorcycle-01" },
                    eventIds,
                    "the benchmark offers the same race entries as the "
                    + "graybox scene");
                Assert.That(
                    serialized.FindProperty("tutorial")
                        .objectReferenceValue,
                    Is.EqualTo(AssetDatabase.LoadAssetAtPath
                        <TutorialDefinitionAsset>(
                            DrivingDir + "/tutorial-def-01.asset")),
                    "the tutorial definition is wired");

                var catalog = DrivingContentCatalog.Create(
                    definition.presentationVersion, tunings,
                    new[] { course }, defs,
                    VehicleSimulationBackend.Arcade);
                Assert.That(catalog.SimulationBackend,
                    Is.EqualTo(VehicleSimulationBackend.Arcade),
                    "the benchmark keeps the shipped arcade backend — "
                    + "physics stays opt-in");
            }
            finally
            {
                if (canClose && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
