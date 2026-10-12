using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FamilyRacing.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.EditorTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FamilyRacing.Tests
{
    // Editor-side adapters: JsonUtility serializer parity, strict wire
    // parsing, and repeatable project setup.
    public sealed class AdapterEditModeTests
    {
        [Test]
        public void UnityProfileSerializerRoundTripsProfile()
        {
            var serializer = new UnityProfileSerializer();
            var profile = ProfileFactory.Create(2, "he");
            var json = serializer.Serialize(profile);
            var back = serializer.Deserialize(json);
            Assert.That(back, Is.Not.Null);
            Assert.That(back.id, Is.EqualTo(profile.id));
            Assert.That(back.slot, Is.EqualTo(profile.slot));
            Assert.That(back.language, Is.EqualTo(profile.language));
            Assert.That(back.schemaVersion, Is.EqualTo(profile.schemaVersion));
        }

        [Test]
        public void UnityProfileSerializerProducesJsonUtilityReadableOutput()
        {
            var serializer = new UnityProfileSerializer();
            var profile = ProfileFactory.Create(0, "en");
            var json = serializer.Serialize(profile);
            var back = JsonUtility.FromJson<ProfileSave>(json);
            Assert.That(back.id, Is.EqualTo(profile.id));
        }

        [Test]
        public void ValidationJsonAcceptsWellFormedFrame()
        {
            Assert.That(ValidationJson.TryParseFrame(
                "{\"type\":\"health\",\"protocolVersion\":1}",
                out var frame, out var error), Is.True);
            Assert.That(error, Is.Null);
            Assert.That((string)frame["type"], Is.EqualTo("health"));
        }

        [Test]
        public void ValidationJsonRejectsOversizeFrame()
        {
            var line = "{\"type\":\"x\",\"pad\":\"" + new string('a', ValidationJson.MaxFrameChars) + "\"}";
            Assert.That(ValidationJson.TryParseFrame(line, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo("frameTooLarge"));
        }

        [Test]
        public void ValidationJsonRejectsDeepFrame()
        {
            var builder = new StringBuilder("{\"type\":\"x\",\"a\":");
            for (var i = 0; i < 40; i++) builder.Append("{\"b\":");
            builder.Append("1");
            for (var i = 0; i < 40; i++) builder.Append('}');
            builder.Append('}');
            Assert.That(ValidationJson.TryParseFrame(builder.ToString(), out _, out _), Is.False);
        }

        [Test]
        public void ValidationJsonRejectsDuplicateFields()
        {
            Assert.That(ValidationJson.TryParseFrame(
                "{\"type\":\"x\",\"type\":\"y\"}", out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo("invalidJson"));
        }

        [Test]
        public void ValidationJsonRejectsNonObject()
        {
            Assert.That(ValidationJson.TryParseFrame("[1,2,3]", out _, out _), Is.False);
            Assert.That(ValidationJson.TryParseFrame("", out _, out _), Is.False);
            Assert.That(ValidationJson.TryParseFrame("not json", out _, out _), Is.False);
        }

        [Test]
        public void ValidationJsonRejectsTrailingContent()
        {
            // A second JSON value after the object is rejected, never applied.
            Assert.That(ValidationJson.TryParseFrame(
                "{\"type\":\"x\"}{\"b\":1}", out _, out _), Is.False);
            // Non-JSON junk after the object is invalid, never applied.
            Assert.That(ValidationJson.TryParseFrame(
                "{\"type\":\"x\"} junk", out _, out _), Is.False);
            // Trailing whitespace is still a single well-formed frame.
            Assert.That(ValidationJson.TryParseFrame(
                "{\"type\":\"x\"}   ", out _, out var clean), Is.True);
            Assert.That(clean, Is.Null);
        }

        [Test]
        public void ValidationJsonIntRejectsOverflowSafely()
        {
            Assert.That(ValidationJson.Int(
                JObject.Parse("{\"n\":99999999999999999999}"), "n"), Is.Null);
            Assert.That(ValidationJson.Int(
                JObject.Parse("{\"n\":3000000000}"), "n"), Is.Null);
            Assert.That(ValidationJson.Int(
                JObject.Parse("{\"n\":42}"), "n"), Is.EqualTo(42));
            Assert.That(ValidationJson.Int(
                JObject.Parse("{\"n\":\"x\"}"), "n"), Is.Null);
        }

        [Test]
        public void NullTutorialLessonEntryFailsValidation()
        {
            // The tutorial definition asset is a ScriptableObject, so the
            // check lives in the Unity-side adapter suite: a null lesson
            // element must fail closed — never silently shrink the list.
            var asset = ScriptableObject.CreateInstance<TutorialDefinitionAsset>();
            try
            {
                asset.tutorialId = "tutorial-null";
                asset.freeDriveEventId = "tutorial-test";
                asset.trialEventId = "intro-test";
                asset.lessons = new TutorialDefinitionAsset.Lesson[]
                {
                    new TutorialDefinitionAsset.Lesson
                    {
                        lessonId = "steering",
                        rule = "steerTicks",
                        requiredSteerTicks = 20,
                        minSteering = 0.25f,
                        cue = new TutorialDefinitionAsset.Cue
                        {
                            textKey = "tutorial.cue.steering",
                            icon = "<>"
                        }
                    },
                    null
                };
                asset.trialCue = new TutorialDefinitionAsset.Cue
                    { textKey = "tutorial.cue.trial", icon = "*" };
                asset.finishedCue = new TutorialDefinitionAsset.Cue
                    { textKey = "tutorial.done", icon = "*" };
                string error;
                Assert.That(asset.ToDefinition(out error), Is.Null);
                Assert.That(error, Does.Contain("lesson"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static TutorialDefinitionAsset LoadTutorialAsset()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TutorialDefinitionAsset>(
                "Assets/FamilyRacing/Generated/Driving/tutorial-def-01.asset");
            Assert.That(asset, Is.Not.Null, "Generated tutorial definition must exist.");
            return asset;
        }

        private static string[] ExpectedCueIds(TutorialDefinitionAsset asset)
        {
            return asset.lessons.Select(l => l.lessonId)
                .Concat(new[] { "trial", "finished" })
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        // The TTS manifest is the single source for cue ids; a renamed or
        // dropped lesson must fail here instead of silently losing a voice.
        [Test]
        public void TutorialCueIdsMatchManifest()
        {
            var asset = LoadTutorialAsset();
            var manifest = JObject.Parse(File.ReadAllText(
                Path.Combine(Directory.GetCurrentDirectory(),
                    "Tools", "tutorial-cues.json")));
            var manifestIds = ((JObject)manifest["cues"]).Properties()
                .Select(p => p.Name)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            Assert.That(manifestIds, Is.EqualTo(ExpectedCueIds(asset)),
                "Tools/tutorial-cues.json cue ids must cover every lesson " +
                "plus 'trial' and 'finished'.");
        }

        // Open acceptance gate: clips require the owner's Azure Speech
        // credentials at generation time (see Assets/FamilyRacing/Audio/
        // Tutorial/README.md runbook). Un-ignore once WAVs are committed.
        [Test]
        [Ignore("voice clips pending AZURE_SPEECH_KEY credential — open gate")]
        public void EveryCueHasWiredClipForBothLocales()
        {
            var asset = LoadTutorialAsset();
            foreach (var cueId in ExpectedCueIds(asset))
            {
                Assert.That(asset.ClipFor(cueId, "en"), Is.Not.Null,
                    cueId + " missing English clip");
                Assert.That(asset.ClipFor(cueId, "he"), Is.Not.Null,
                    cueId + " missing Hebrew clip");
            }
        }

        // Snapshot→JObject copy happens once per boundary and once per
        // frame; measure it on a live service so the record carries a real
        // number instead of a guess. The bound is a tripwire (two orders
        // under the 33 ms frame), not a performance gate.
        [Test]
        public void SnapshotJsonCopyCostMeasured()
        {
            var profiles = Path.Combine(Path.GetTempPath(),
                "fr-profiles-" + Guid.NewGuid().ToString("N"));
            var service = new FoundationService(
                new ProfileStore(profiles, new UnityProfileSerializer()),
                "en", "run-measure");
            const int iterations = 200;
            // Warm-up pass so JIT costs stay out of the measurement.
            JObject.FromObject(service.Snapshot());
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
                JObject.FromObject(service.Snapshot());
            watch.Stop();
            var perCopyMs = watch.Elapsed.TotalMilliseconds / iterations;
            UnityEngine.Debug.Log(
                $"Snapshot→JObject copy cost: {perCopyMs:F3} ms " +
                $"({iterations} iterations)");
            Assert.That(perCopyMs, Is.LessThan(1.0),
                "A boundary copy must stay far under the 33 ms frame budget");
        }

        [Test]
        public void FoundationProjectSetupEnforcesUrpRenderScale()
        {
            FoundationProjectSetup.Create();
            var urp = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(
                "Assets/FamilyRacing/Generated/FoundationURP.asset");
            Assert.That(urp, Is.Not.Null);
            var serialized = new SerializedObject(urp);
            Assert.That(serialized.FindProperty("m_RenderScale").floatValue,
                Is.EqualTo(0.75f), "Brief requires 0.75 Android render scale.");
        }

        [Test]
        public void FoundationSceneUidocumentBindsGeneratedPanel()
        {
            // Setup must repair an existing generated scene, not just check
            // that the asset exists: a broken m_PanelSettings renders blank.
            FoundationProjectSetup.Create();
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(
                "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset");
            Assert.That(panel, Is.Not.Null, "Generated PanelSettings must exist.");
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/FamilyRacing/Generated/Foundation.unity",
                UnityEditor.SceneManagement.OpenSceneMode.Additive);
            try
            {
                UnityEngine.UIElements.UIDocument doc = null;
                foreach (var go in scene.GetRootGameObjects())
                    if (doc == null)
                        doc = go.GetComponentInChildren<UnityEngine.UIElements.UIDocument>(true);
                Assert.That(doc, Is.Not.Null,
                    "Generated scene must contain a UIDocument.");
                Assert.That(doc.panelSettings, Is.EqualTo(panel),
                    "Scene UIDocument must reference the generated PanelSettings asset.");
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
                Assert.That(doc.rootVisualElement, Is.Not.Null,
                    "UIDocument must produce a live root.");
                Assert.That(doc.rootVisualElement.panel, Is.Not.Null,
                    "UIDocument root must be attached to a live panel.");
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void FoundationProjectSetupIsIdempotentAndComplete()
        {
            FoundationProjectSetup.Create();
            FoundationProjectSetup.Create();

            var urp = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(
                "Assets/FamilyRacing/Generated/FoundationURP.asset");
            Assert.That(urp, Is.Not.Null, "URP asset must exist after setup.");
            Assert.That(GraphicsSettings.defaultRenderPipeline, Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(
                "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset"), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(
                "Assets/FamilyRacing/Generated/Foundation.unity"), Is.Not.Null);

            var count = 0;
            foreach (var scene in EditorBuildSettings.scenes)
                if (scene.path == "Assets/FamilyRacing/Generated/Foundation.unity")
                    count++;
            Assert.That(count, Is.EqualTo(1), "Scene must be in build settings exactly once.");
            Assert.That(PlayerSettings.productName, Is.EqualTo("Family Racing"));
        }
    }
}
