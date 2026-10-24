using System;
using System.Collections.Generic;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    public sealed class RearViewMirrorPlayTests
    {
        private GameObject host, main;
        private Component mirror;
        private VisualElement root;
        private FoundationState state;

        [SetUp] public void SetUp()
        {
            var type = typeof(FoundationApp).Assembly.GetType("FamilyRacing.RearViewMirror");
            Assert.That(type, Is.Not.Null, "A real rear-view presenter must exist.");
            host = new GameObject("mirror-test");
            mirror = host.AddComponent(type);
            main = new GameObject("Main Camera", typeof(Camera));
            main.tag = "MainCamera";
            main.transform.position = new Vector3(9, 12, 15);
            root = new VisualElement();
            root.Add(new VisualElement { name = "overlay.pause" });
            state = new FoundationState { screen = "driving", language = "en", drivingEvents = new List<DrivingEvent>(),
                soloRace = new SoloRaceState { raceId = "race-1", participantId = "me",
                    race = new NetworkRaceState { raceId = "race-1", phase = "running",
                        racers = new List<NetworkRacerState> { Racer("me", 0), Racer("enemy", -12) },
                        gadgets = new GadgetWorldState { patches = new List<GadgetPatchState> {
                            new GadgetPatchState { patchId = "mine", ownerParticipantId = "me" },
                            new GadgetPatchState { patchId = "theirs", ownerParticipantId = "enemy" } } } } } };
            Frame(0);
        }

        private static NetworkRacerState Racer(string id, float z) => new NetworkRacerState {
            participantId = id, session = new DrivingSessionState { motor = new MotorState { z = z } } };
        private void Frame(float dt) => mirror.GetType().GetMethod("ApplyFrame").Invoke(mirror, new object[] { state, root, dt });
        private Camera Camera => (Camera)mirror.GetType().GetProperty("RenderCamera").GetValue(mirror);
        private string Focus => (string)mirror.GetType().GetProperty("FocusedParticipantId").GetValue(mirror);
        private void Hit(string patch = "mine", string kind = GadgetRules.EventFoamHit)
        {
            state.drivingEvents.Add(new DrivingEvent { id = Guid.NewGuid().ToString(),
                kind = kind, detail = patch, participantId = "enemy" });
            Frame(0.01f);
        }

        [TearDown] public void TearDown()
        {
            if (host != null) Object.DestroyImmediate(host);
            if (main != null) Object.DestroyImmediate(main);
        }

        [Test] public void MirrorRendersBehindPlayerWithoutMovingMainCamera()
        {
            Assert.That(Camera.enabled, Is.True);
            Assert.That(Camera.targetTexture.width, Is.EqualTo(600));
            Assert.That(Vector3.Dot(Camera.transform.forward, Vector3.back), Is.GreaterThan(.8f));
            Assert.That(main.transform.position, Is.EqualTo(new Vector3(9, 12, 15)));
            Assert.That(Camera.GetComponent<AudioListener>(), Is.Null);
            Assert.That(root.Q("hud.mirror").pickingMode, Is.EqualTo(PickingMode.Ignore));
            Assert.That(root.IndexOf(root.Q("hud.mirror")), Is.LessThan(root.IndexOf(root.Q("overlay.pause"))));
        }

        [Test] public void LocalTrapHitFocusesEnemyAndDuplicateDoesNotExtendShot()
        {
            Hit();
            Assert.That(Focus, Is.EqualTo("enemy"));
            Assert.That(Camera.fieldOfView, Is.EqualTo(38f), "The hit shot must make the rival large enough to read.");
            Assert.That(root.Q<Label>("hud.mirror.status").text, Does.Contain("SLOWED"));
            Frame(2);
            Frame(1.2f);
            Assert.That(Focus, Is.Empty);
        }

        [Test] public void OpponentsTrapDoesNotClaimPlayerHit()
        {
            Hit("theirs");
            Assert.That(Focus, Is.Empty);
        }

        [Test] public void ShieldBlockIsNotReportedAsSlowdown()
        {
            Hit(kind: GadgetRules.EventShieldBlocked);
            Assert.That(Focus, Is.EqualTo("enemy"));
            Assert.That(root.Q<Label>("hud.mirror.status").text, Does.Contain("BLOCKED"));
        }

        [Test] public void PauseFreezesHitFeedbackAndResumeExpiresIt()
        {
            Hit(); state.paused = true; Frame(20);
            Assert.That(Focus, Is.EqualTo("enemy"));
            state.paused = false; Frame(3.2f);
            Assert.That(Focus, Is.Empty);
        }

        [Test] public void RestartBaselinesHistoryAndExitReleasesRenderTarget()
        {
            Hit(); state.soloRace.raceId = "race-2"; Frame(0);
            Assert.That(Focus, Is.Empty);
            state.screen = "sandbox"; state.soloRace = null; Frame(0);
            Assert.That(Camera == null || !Camera.enabled, Is.True);
            Assert.That(root.Q("hud.mirror"), Is.Null);
        }

        [Test] public void HebrewAndRebuiltHudKeepMirrorBound()
        {
            state.language = "he"; root.Clear(); Frame(0);
            Assert.That(root.Q<Label>("hud.mirror.title").text, Is.EqualTo("מבט לאחור"));
            Hit();
            Assert.That(root.Q<Label>("hud.mirror.status").text, Is.EqualTo("היריב הואט"));
        }

        [Test] public void LanUsesLocalParticipantAndIgnoresHitsOnLocalCar()
        {
            state.networkRace = state.soloRace.race;
            state.soloRace = null;
            state.screen = "lan-race";
            state.network = new FamilyRacing.Core.Networking.NetworkSessionState { localParticipantId = "me" };
            Frame(0);
            Hit();
            Assert.That(Focus, Is.EqualTo("enemy"));
            Frame(4);
            state.drivingEvents.Add(new DrivingEvent { id = "local-hit", kind = GadgetRules.EventFoamHit,
                detail = "theirs", participantId = "me" });
            Frame(0);
            Assert.That(Focus, Is.Empty);
        }

        [Test] public void AttachingToExistingRaceDoesNotReplayOldHits()
        {
            state.soloRace.raceId = "joined-race";
            state.drivingEvents.Add(new DrivingEvent { id = "historical-hit", kind = GadgetRules.EventFoamHit,
                detail = "mine", participantId = "enemy" });
            Frame(0);
            Assert.That(Focus, Is.Empty);
            Hit();
            Assert.That(Focus, Is.EqualTo("enemy"));
        }
    }
}
