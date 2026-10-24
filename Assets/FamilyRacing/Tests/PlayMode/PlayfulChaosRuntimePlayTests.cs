using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using TouchPhase = UnityEngine.TouchPhase;

namespace FamilyRacing.Tests
{
    public sealed class PlayfulChaosRuntimePlayTests : InputTestFixture
    {
        private FoundationApp app;
        private UIDocument document;
        private string directory;
        private Scene previewScene;

        [SetUp]
        public void AddTouchDevice() { InputSystem.AddDevice<Touchscreen>(); }

        [UnitySetUp]
        public IEnumerator LoadPreview()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                FamilyRacing.EditorTools.PlayfulChaosSetup.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            app = UnityEngine.Object.FindFirstObjectByType<FoundationApp>();
            previewScene = app.gameObject.scene;
            document = app.GetComponent<UIDocument>();
            directory = Path.Combine(Path.GetTempPath(), "fr-preview-" + Guid.NewGuid().ToString("N"));
            var service = new FoundationService(new ProfileStore(directory, new UnityProfileSerializer()),
                "en", app.RunId);
            var world = app.GetComponent<DrivingWorld>();
            service.SetDrivingContent(world.Catalog);
            service.SetGameplayCatalog(world.Gameplay);
            service.SetTutorialDefinition(world.Tutorial);
            service.SetDrivingSimulationFactory(world.CreateSimulation);
            typeof(FoundationApp).GetField("service", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(app, service);
            Send("profile.create", new Dictionary<string, string> { { "slot", "0" }, { "language", "en" } });
            Send("ui.navigate", new Dictionary<string, string> { { "screen", "sandbox" } });
            for (var frame = 0; frame < 4; frame++) yield return null;
        }

        [UnityTearDown]
        public IEnumerator ClosePreview()
        {
            var empty = SceneManager.CreateScene("preview-test-cleanup");
            SceneManager.SetActiveScene(empty);
            if (previewScene.IsValid() && previewScene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(previewScene);
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private void Send(string action, Dictionary<string, string> args = null)
        {
            var result = app.Dispatch(action, args ?? new Dictionary<string, string>());
            Assert.That(result.status, Is.EqualTo("applied"), action + ": " + result.code);
        }

        [UnityTest]
        public IEnumerator TouchPauseStaysInSafeAreaDockBelowModalOverlays()
        {
            Send("quickRace.start", new Dictionary<string, string>
            { { "courseId", "graybox-01" }, { "classId", "car" }, { "vehicleId", "car-01" }, { "difficulty", "easy" } });
            for (var frame = 0; frame < 4; frame++) yield return null;
            var root = document.rootVisualElement;
            var deck = root.Q<VisualElement>("hud.controls");
            var pause = root.Q<Button>("driving.pause");
            Assert.That(pause.parent.ClassListContains("race-utilities"), Is.True,
                "Touch pause must retain the shared utility dock's safe-area geometry.");
            Assert.That(deck.Contains(pause), Is.True);
            Assert.That(root.IndexOf(deck), Is.LessThan(root.IndexOf(root.Q("overlay.pause"))),
                "The pause overlay must render above its launch control.");
            Assert.That(root.IndexOf(deck), Is.LessThan(root.IndexOf(root.Q("overlay.results"))),
                "Results must render above touch controls.");
        }

        [UnityTest]
        public IEnumerator TouchRecoveryRemainsReachableAndDispatchesNormalInput()
        {
            Send("quickRace.start", new Dictionary<string, string>
            { { "courseId", "graybox-01" }, { "classId", "car" }, { "vehicleId", "car-01" }, { "difficulty", "easy" } });
            for (var frame = 0; frame < 4; frame++) yield return null;
            var recover = document.rootVisualElement.Q<Button>("driving.recover");
            Assert.That(recover, Is.Not.Null);
            Assert.That(recover.worldBound.height, Is.GreaterThan(0), "touch recovery must be visible");
            var touch = new Touch { fingerId = 1, position = recover.worldBound.center, phase = TouchPhase.Began };
            using (var down = PointerDownEvent.GetPooled(touch, EventModifiers.None))
            { down.target = recover; recover.SendEvent(down); }
            touch.phase = TouchPhase.Ended;
            using (var up = PointerUpEvent.GetPooled(touch, EventModifiers.None))
            { up.target = recover; recover.SendEvent(up); }
            app.Input.Pump(false, "driving");
            Assert.That(app.Service.Snapshot().input.recover, Is.True,
                "the visible control must dispatch ordinary input.set recovery");
        }

        [UnityTest]
        public IEnumerator TutorialDoesNotOverlapCompactReadoutsInEitherLanguage()
        {
            foreach (var locale in new[] { "en", "he" })
            {
                Send("ui.navigate", new Dictionary<string, string> { { "screen", "settings" } });
                Send("settings.update", new Dictionary<string, string>
                {
                    { "language", locale }, { "camera", "chase" }, { "reducedCameraShake", "false" },
                    { "musicVolume", "0.65" }, { "effectsVolume", "0.85" }, { "voiceVolume", "1" }
                });
                Send("settings.save");
                Send("ui.navigate", new Dictionary<string, string> { { "screen", "practice" } });
                Send("tutorial.start", new Dictionary<string, string> { { "vehicleId", "car-01" } });
                for (var frame = 0; frame < 4; frame++) yield return null;
                var root = document.rootVisualElement;
                var tutorial = root.Q<VisualElement>("tutorial.panel");
                var boost = root.Q<VisualElement>("hud.boostRow");
                Assert.That(tutorial.worldBound.height, Is.GreaterThan(0), locale);
                Assert.That(tutorial.worldBound.yMin, Is.GreaterThan(boost.worldBound.yMax + 5),
                    locale + ": the lesson must be below both HUD rows");
                Send("tutorial.exit");
            }
        }
    }
}
