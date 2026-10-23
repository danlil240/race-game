using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    [UnityPlatform(RuntimePlatform.WindowsEditor)]
    public sealed class PlayerPresentationPlayTests
    {
        private GameObject host, view;
        private FoundationApp app;
        private FoundationShell shell;
        private UIDocument document;
        private PanelSettings panel;
        private string directory;

        [UnitySetUp] public IEnumerator SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "player-ui-" + Guid.NewGuid().ToString("N"));
            var service = new FoundationService(new ProfileStore(directory, new UnityProfileSerializer()), "en", "player-ui");
            host = new GameObject("player-ui-host");
            host.SetActive(false);
            app = host.AddComponent<FoundationApp>();
            typeof(FoundationApp).GetField("service", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, service);
            typeof(FoundationApp).GetField("runId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, "player-ui");
#if UNITY_EDITOR
            panel = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(
                "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset"));
#endif
            view = new GameObject("player-ui-view");
            view.SetActive(false);
            document = view.AddComponent<UIDocument>();
            document.panelSettings = panel;
            shell = view.AddComponent<FoundationShell>();
            view.AddComponent<AssetTrialHud>();
            view.SetActive(true);
            shell.Bind(app);
            yield return null;
        }

        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(view);
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(panel);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private void Dispatch(string action, params string[] pairs)
        {
            var values = new Dictionary<string, string>();
            for (var i = 0; i < pairs.Length; i += 2) values[pairs[i]] = pairs[i + 1];
            Assert.That(app.Dispatch(action, values).status, Is.EqualTo("applied"), action);
            shell.Refresh(app.Service.Snapshot());
        }

        [UnityTest] public IEnumerator HomeHasPlayerActionsAndNoInputDiagnosticsInBothLanguages()
        {
            Dispatch("profile.create", "slot", "0", "language", "en");
            Dispatch("ui.navigate", "screen", "sandbox");
            foreach (var locale in new[] { "en", "he" })
            {
                var localized = app.Service.Snapshot();
                localized.language = locale;
                shell.Refresh(localized);
                yield return null;
                var root = document.rootVisualElement;
                Assert.That(root.Q<Label>("home.title"), Is.Not.Null);
                Assert.That(root.Q("indicator.steering"), Is.Null);
                Assert.That(root.Q<Button>("loop.quickRace"), Is.Not.Null);
                Assert.That(root.Q<Button>("action.settings").enabledSelf, Is.True);
                Assert.That(root.Q<Button>("loop.quickRace").enabledSelf, Is.False,
                    "The player menu must preserve the missing-content gate.");
                Assert.That(root.languageDirection, Is.EqualTo(locale == "he" ? LanguageDirection.RTL : LanguageDirection.LTR));
                var button = root.Q<Button>("action.practice");
                shell.Refresh(localized);
                Assert.That(root.Q<Button>("action.practice"), Is.SameAs(button), "Stable refresh keeps pointer targets.");
            }
        }

        [UnityTest] public IEnumerator CreatingAProfileFromACardGoesStraightToTheHomeMenu()
        {
            shell.Refresh(app.Service.Snapshot());
            yield return null;
            var button = document.rootVisualElement.Q<Button>("profile.0.select");
            var touch = new Touch { fingerId = 0, position = button.worldBound.center, phase = TouchPhase.Began };
            using (var e = PointerDownEvent.GetPooled(touch, EventModifiers.None)) { e.target = button; button.SendEvent(e); }
            touch.phase = TouchPhase.Ended;
            using (var e = PointerUpEvent.GetPooled(touch, EventModifiers.None)) { e.target = button; button.SendEvent(e); }
            shell.Refresh(app.Service.Snapshot());
            yield return null;
            Assert.That(app.Service.Snapshot().screen, Is.EqualTo("sandbox"));
            Assert.That(document.rootVisualElement.Q("home.title"), Is.Not.Null);
        }

        [UnityTest] public IEnumerator DrivingHudHidesCheckpointDebugAndKeepsPauseAndResultsAboveGadgetCard()
        {
            var state = app.Service.Snapshot();
            state.screen = "driving";
            state.persistenceStatus = "saved";
            state.soloRace = new Core.Racing.SoloRaceState
            {
                mode = "quickRace",
                phase = "running",
                participantId = "local",
                race = new Core.Racing.NetworkRaceState
                {
                    phase = "running",
                    elapsedTicks = 90,
                    gadgets = new Core.Racing.GadgetWorldState
                    {
                        racers = new List<Core.Racing.GadgetRacerState>
                        {
                            new Core.Racing.GadgetRacerState { participantId = "local", heldItemId = "item-1", heldKind = "turbo" }
                        }
                    },
                    racers = new List<Core.Racing.NetworkRacerState>
                    {
                        new Core.Racing.NetworkRacerState
                        {
                            participantId = "local",
                            position = 2,
                            session = new Core.Racing.DrivingSessionState
                            {
                                phase = "running",
                                lap = 0,
                                lapsRequired = 3,
                                elapsedTicks = 90,
                                nextCheckpoint = 1,
                                checkpoints = new List<Core.Racing.CheckpointMark>
                                {
                                    new Core.Racing.CheckpointMark { id = "start" },
                                    new Core.Racing.CheckpointMark { id = "gate-1" }
                                },
                                motor = new Core.Driving.MotorState { speed = 20f, boostCharge = 0.5f }
                            }
                        }
                    }
                }
            };
            shell.Refresh(state);
            yield return null;
            var root = document.rootVisualElement;
            Assert.That(root.Q<Label>("hud.position"), Is.Not.Null);
            Assert.That(root.Q<Label>("hud.position").text, Does.Contain("2/1"));
            Assert.That(root.Q<Label>("hud.gate").style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(root.Q<Label>("status.persistence").text, Is.Empty);
            Assert.That(root.Q("hud.gadget.card"), Is.Not.Null);
            Assert.That(root.IndexOf(root.Q("hud.gadget.card")),
                Is.LessThan(root.IndexOf(root.Q("overlay.pause"))));
            Assert.That(root.IndexOf(root.Q("hud.gadget.card")),
                Is.LessThan(root.IndexOf(root.Q("overlay.results"))));
            Assert.That(root.Q("overlay.pause").Q("pause.panel"), Is.Not.Null);
            Assert.That(root.Q("overlay.results").Q("results.panel"), Is.Not.Null);
            Assert.That(root.Q("overlay.pause").Q<Label>("hud.keys"), Is.Not.Null);
            var steerLeft = root.Q<Button>("driving.steerLeft");
            state.tick++;
            shell.Refresh(state);
            Assert.That(root.Q<Button>("driving.steerLeft"), Is.SameAs(steerLeft));
        }
    }
}
