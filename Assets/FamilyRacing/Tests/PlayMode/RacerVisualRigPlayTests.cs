using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using FamilyRacing.EditorTools;
using UnityEditor;
#endif

namespace FamilyRacing.Tests
{
    // M4 Task-4 rig contract: recognizable authored vehicles + seated
    // animals replace the primitive silhouettes, while every existing
    // presentation guarantee — interpolation, pose sources, physical
    // full attitude, recovery snapping, no gameplay colliders, bounded
    // reuse — must hold on the authored path exactly. Cosmetics resolve
    // per the roster rules: solo from the selected profile, humans from
    // participants, AI from the stable slot mapping.
    // Editor-only: the authored benchmark assets are loaded from the
    // Art/ folder, so these tests run where AssetDatabase exists.
    [UnityPlatform(RuntimePlatform.WindowsEditor,
        RuntimePlatform.OSXEditor, RuntimePlatform.LinuxEditor)]
    public sealed class RacerVisualRigPlayTests
    {
        private const string DrivingDir =
            "Assets/FamilyRacing/Generated/Driving";

        private GameObject host;
        private DrivingWorld world;
        private VehicleDefinitionAsset car;
        private VehicleDefinitionAsset moto;
        private VehicleVisualDefinitionAsset carVisual;
        private VehicleVisualDefinitionAsset motoVisual;
        private CharacterDefinitionAsset[] characters;
        private readonly List<Object> assets = new List<Object>();
        private long tick;

        [SetUp]
        public void SetUp()
        {
#if UNITY_EDITOR
            RacerVisualAuthoring.Ensure();
            car = AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                DrivingDir + "/car-01.asset");
            moto = AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                DrivingDir + "/motorcycle-01.asset");
            carVisual = AssetDatabase
                .LoadAssetAtPath<VehicleVisualDefinitionAsset>(
                    RacerVisualAuthoring.CarVisualAssetPath);
            motoVisual = AssetDatabase
                .LoadAssetAtPath<VehicleVisualDefinitionAsset>(
                    RacerVisualAuthoring.MotoVisualAssetPath);
            characters = new CharacterDefinitionAsset[
                RacerVisualAuthoring.CharacterIds.Length];
            for (var i = 0; i < characters.Length; i++)
                characters[i] = AssetDatabase
                    .LoadAssetAtPath<CharacterDefinitionAsset>(
                        RacerVisualAuthoring.CharacterAssetPath(
                            RacerVisualAuthoring.CharacterIds[i]));
#endif
            Assert.That(car, Is.Not.Null,
                "car-01.asset missing — run DrivingPrototypeSetup");
            Assert.That(moto, Is.Not.Null);
            Assert.That(car.visual, Is.EqualTo(carVisual),
                "the generated car-01 definition must bind its authored "
                + "visual — DrivingPrototypeSetup wiring");
            Assert.That(moto.visual, Is.EqualTo(motoVisual));
            Assert.That(carVisual, Is.Not.Null,
                "authored car visual missing — RacerVisualAuthoring.Ensure");
            Assert.That(motoVisual, Is.Not.Null);
            Assert.That(characters[0], Is.Not.Null);
            Assert.That(characters[3], Is.Not.Null);

            host = new GameObject("racer-rig-tests");
            host.SetActive(false);
            world = host.AddComponent<DrivingWorld>();
            Inject(world, "vehicles", new[] { car, moto });
            Inject(world, "characters", characters);
#if UNITY_EDITOR
            // The catalog fail-closes on a partial set: vehicles alone
            // cannot build it — wire the generated course + events + a
            // tutorial so Load() validates like the real scene.
            Inject(world, "track",
                AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(
                    DrivingDir + "/graybox-01.asset"));
            Inject(world, "events", new[]
            {
                AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(
                    DrivingDir + "/intro-01.asset"),
                AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(
                    DrivingDir + "/practice-02.asset")
            });
            Inject(world, "tutorial",
                AssetDatabase.LoadAssetAtPath<TutorialDefinitionAsset>(
                    DrivingDir + "/tutorial-def-01.asset"));
#endif
            host.SetActive(true);
            Assert.That(world.Load(), Is.Not.Null,
                "catalog must build for the racer tests");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.Destroy(host);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
        }

        private static void Inject(object target, string field,
            object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, value);
        }

        private T Asset<T>(System.Action<T> configure)
            where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            assets.Add(asset);
            return asset;
        }

        private MotorState Motor(float x, float z, float speed,
            float steering)
        {
            return new MotorState
            {
                x = x,
                z = z,
                y = 0f,
                speed = speed,
                heading = 0f,
                grounded = true,
                tick = ++tick
            };
        }

        private static NetworkRacerState Racer(string participantId,
            int slot, string vehicleId, MotorState motor,
            float steering)
        {
            return new NetworkRacerState
            {
                participantId = participantId,
                slot = slot,
                vehicleId = vehicleId,
                attribution = "human",
                session = new DrivingSessionState
                {
                    vehicleId = vehicleId,
                    phase = "running",
                    motor = motor,
                    input = new InputState { steering = steering }
                }
            };
        }

        private static ParticipantState Participant(string id, int slot,
            string avatar, int colorIndex)
        {
            return new ParticipantState
            {
                participantId = id,
                slot = slot,
                occupied = true,
                connected = true,
                avatar = avatar,
                colorIndex = colorIndex,
                controlSource = "local"
            };
        }

        private static NetworkRaceState Race(
            params NetworkRacerState[] racers)
        {
            return new NetworkRaceState
            {
                raceId = "rig-test",
                phase = "running",
                racers = new List<NetworkRacerState>(racers)
            };
        }

        // Four human racers, one per profile slot: each must render a
        // DISTINCT authored animal + accent color resolved from the
        // participant's own avatar/colorIndex — never a slot-mapped
        // guess — and the streamed facts must report exactly that.
        [UnityTest]
        public IEnumerator FourRacersKeepDistinctCosmetics()
        {
            var racers = new[]
            {
                Racer("p0", 0, "car-01", Motor(-9f, 0f, 8f, 0.4f), 0.4f),
                Racer("p1", 1, "motorcycle-01", Motor(-3f, 0f, 8f, -0.3f),
                    -0.3f),
                Racer("p2", 2, "car-01", Motor(3f, 0f, 8f, 0f), 0f),
                Racer("p3", 3, "motorcycle-01", Motor(9f, 0f, 8f, 0.2f),
                    0.2f)
            };
            var participants = new List<ParticipantState>
            {
                Participant("p0", 0, "fox", 0),
                Participant("p1", 1, "rabbit", 1),
                Participant("p2", 2, "panda", 2),
                Participant("p3", 3, "raccoon", 3)
            };
            world.ApplyRaceFrame(Race(racers), 1f, null, participants);
            yield return null;

            var seenCharacters = new HashSet<string>();
            var seenColors = new HashSet<int>();
            var block = new MaterialPropertyBlock();
            for (var i = 0; i < 4; i++)
            {
                var participant = participants[i];
                var pres = world.RacerPresentation(participant.participantId);
                Assert.That(pres, Is.Not.Null,
                    "a racer view exists for " + participant.participantId);
                Assert.That(pres.Rig, Is.Not.Null,
                    participant.participantId
                    + " must render the authored rig, not primitives");
                Assert.That(pres.BoundCharacterId,
                    Is.EqualTo(participant.avatar),
                    participant.participantId
                    + " keeps its own avatar, not the slot default");
                Assert.That(pres.BoundColorIndex,
                    Is.EqualTo(participant.colorIndex));
                seenCharacters.Add(pres.BoundCharacterId);
                seenColors.Add(pres.BoundColorIndex);

                // Rigs are render-only — no gameplay collider may exist
                // anywhere under a racer view.
                Assert.That(
                    pres.GetComponentsInChildren<Collider>(true),
                    Is.Empty,
                    participant.participantId
                    + " rig carries a collider — visuals must never "
                    + "enter gameplay collision");

                // The seat must be occupied by the animal, arms aimed at
                // the vehicle grips, and the accent tint actually on
                // the renderers via a material property override.
                var rider = pres.Rig.Rider;
                Assert.That(rider, Is.Not.Null);
                Assert.That(rider.name, Does.StartWith("racer-"));
                var scarf = FindRenderer(rider, "ScarfAccent");
                Assert.That(scarf, Is.Not.Null,
                    "the shared skeleton keeps the ScarfAccent node");
                scarf.GetPropertyBlock(block);
                AssertColor(block.GetColor("_BaseColor"),
                    RacerVisualRig.SlotColor(participant.colorIndex),
                    participant.participantId + " scarf accent");

                // Both vehicle classes mount the same animal: check the
                // grip-mounted arms aim at a real grip transform.
                var armL = FindTransform(rider, "ArmL");
                Assert.That(armL, Is.Not.Null);
            }
            Assert.That(seenCharacters.Count, Is.EqualTo(4),
                "all four species are on screen at once");
            Assert.That(seenColors.Count, Is.EqualTo(4),
                "all four profile colors are on screen at once");

            // Streamed racer facts report the actual bound identities.
            Assert.That(world.RacerFacts.Count, Is.EqualTo(4));
            foreach (var fact in world.RacerFacts)
            {
                var participant = participants.Find(
                    p => p.participantId == fact.participantId);
                Assert.That(participant, Is.Not.Null,
                    "fact " + fact.participantId);
                Assert.That(fact.slot, Is.EqualTo(participant.slot));
                Assert.That(fact.characterId,
                    Is.EqualTo(participant.avatar));
                Assert.That(fact.colorIndex,
                    Is.EqualTo(participant.colorIndex));
                var expectedVisual = fact.vehicleId == "car-01"
                    ? RacerVisualAuthoring.CarVisualId
                    : RacerVisualAuthoring.MotoVisualId;
                Assert.That(fact.vehicleVisualId,
                    Is.EqualTo(expectedVisual));
            }
        }

        // Disconnect -> reserved AI racer -> rejoin: the participant's
        // identity never falls through to the slot mapping, and the
        // local racer's rendered view is the same object throughout —
        // that object is what the camera follows through the local
        // participantId, so the camera target survives the rejoin.
        [UnityTest]
        public IEnumerator RejoinKeepsCharacterAndLocalCameraTarget()
        {
            var local = "p-local";
            var racers = new[]
            {
                Racer(local, 0, "car-01", Motor(0f, 0f, 6f, 0f), 0f),
                Racer("ai-slot-1", 1, "motorcycle-01",
                    Motor(-4f, 0f, 6f, 0f), 0f)
            };
            var participants = new List<ParticipantState>
            {
                Participant(local, 0, "raccoon", 3)
            };
            world.ApplyRaceFrame(Race(racers), 1f, null, participants);
            yield return null;

            var pres = world.RacerPresentation(local);
            Assert.That(pres, Is.Not.Null);
            Assert.That(pres.BoundCharacterId, Is.EqualTo("raccoon"));
            var rigBefore = pres.Rig;
            var viewRoot = pres.transform;
            var aiPres = world.RacerPresentation("ai-slot-1");
            Assert.That(aiPres, Is.Not.Null);
            Assert.That(aiPres.BoundCharacterId, Is.EqualTo("rabbit"),
                "AI cosmetics come from the stable slot mapping — "
                + "slot 1 is the rabbit");

            // Disconnect: the reserved participant stays in the roster
            // (controlSource flips to AI) — identity must not change.
            participants[0].connected = false;
            participants[0].controlSource = "ai";
            world.ApplyRaceFrame(Race(racers), 1f, null, participants);
            yield return null;
            Assert.That(world.RacerPresentation(local),
                Is.SameAs(pres),
                "disconnect must not rebind or replace the local view");
            Assert.That(pres.BoundCharacterId, Is.EqualTo("raccoon"));
            Assert.That(pres.Rig, Is.SameAs(rigBefore),
                "no new rig materializes across a disconnect");

            // Rejoin: the same participant resumes human control — same
            // view object, same rig, same cosmetics.
            participants[0].connected = true;
            participants[0].controlSource = "local";
            world.ApplyRaceFrame(Race(racers), 1f, null, participants);
            yield return null;
            Assert.That(world.RacerPresentation(local),
                Is.SameAs(pres));
            Assert.That(pres.transform, Is.EqualTo(viewRoot));
            Assert.That(pres.Rig, Is.SameAs(rigBefore));
            Assert.That(pres.BoundCharacterId, Is.EqualTo("raccoon"));
            Assert.That(pres.BoundColorIndex, Is.EqualTo(3));

            // Facts keep reporting the participant identity — the local
            // racer the camera follows is still slot 0's raccoon.
            var fact = world.RacerFacts.Find(
                f => f.participantId == local);
            Assert.That(fact, Is.Not.Null);
            Assert.That(fact.characterId, Is.EqualTo("raccoon"));
            Assert.That(fact.slot, Is.EqualTo(0));
        }

        // The physical backend owns the lean: committed body attitude
        // reaches the view root unchanged, the cosmetic lean pivot stays
        // identity, and the rig must not add a second roll — the authored
        // path keeps exactly the guarantee the primitive path proved.
        [UnityTest]
        public IEnumerator PhysicalLeanIsNotAppliedTwice()
        {
            var tuning = Asset<VehiclePhysicsTuningAsset>(
                a => a.ApplyPreset(VehiclePhysicsTuning.Car01()));
            var physCar = Asset<VehicleDefinitionAsset>(a =>
            {
                var preset = VehicleTuning.Car01();
                preset.vehicleId = "car-physics-01";
                preset.simulationBackend =
                    VehicleSimulationBackend.PhysicalAssisted;
                a.ApplyPreset(preset);
                a.simulationBackend =
                    VehicleSimulationBackend.PhysicalAssisted;
                a.physicsTuning = tuning;
                a.silhouette = "car";
                // Deliberately non-zero: if the cosmetic lean ever leaked
                // into the physical path this value would double it.
                a.leanDegrees = 8f;
                a.visual = carVisual;
            });
            // A fresh world: Load() builds the vehicle catalog once, so
            // the physical definition needs its own host.
            var physHost = new GameObject("phys-rig-host");
            var physWorld = physHost.AddComponent<DrivingWorld>();
            Inject(physWorld, "vehicles", new[] { physCar });
            Inject(physWorld, "characters", characters);
#if UNITY_EDITOR
            var physTrack =
                AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(
                    DrivingDir + "/graybox-01.asset");
            Inject(physWorld, "track", physTrack);
#endif
            // The catalog validates event vehicleIds against the world's
            // vehicles — the generated events name car-01/motorcycle-01,
            // so the physical fixture gets an in-memory event.
            var physEvent = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "phys-01";
                a.track = physTrack;
                a.vehicleIds = new[] { "car-physics-01" };
            });
            Inject(physWorld, "events", new[] { physEvent });
            try
            {
                Assert.That(physWorld.Load(), Is.Not.Null);

            var attitude = Quaternion.Euler(0f, 30f, -22f);
            var motor = Motor(0f, 0f, 10f, 1f);
            motor.physics = PhysicalState(attitude, 5, wheels: true);
            var racers = new[]
            {
                Racer("p0", 0, "car-physics-01", motor, 1f)
            };
            var participants = new List<ParticipantState>
            {
                Participant("p0", 0, "fox", 0)
            };
            physWorld.ApplyRaceFrame(Race(racers), 1f, null, participants);
            physWorld.ApplyRaceFrame(Race(racers), 1f, null, participants);
            yield return null;

            var pres = physWorld.RacerPresentation("p0");
            Assert.That(pres, Is.Not.Null);
            Assert.That(pres.Rig, Is.Not.Null,
                "the authored rig must render on the physical path too");
            // Full attitude on the root — roll included, exactly the
            // committed quaternion (alpha 1 lands on the latest commit).
            Assert.That(
                Quaternion.Angle(pres.transform.rotation, attitude),
                Is.LessThan(1f),
                "root must carry the committed full attitude");
            Assert.That(pres.LeanRoll, Is.EqualTo(0f),
                "cosmetic lean roll must stay zero on the physical path");
            var leanPivot = FindTransform(pres.transform, "LeanPivot");
            Assert.That(leanPivot, Is.Not.Null);
            Assert.That(
                Quaternion.Angle(leanPivot.localRotation,
                    Quaternion.identity),
                Is.LessThan(0.01f),
                "the lean pivot must stay identity — no second roll");
            // The rider adds no roll of its own on the physical path.
            var rider = pres.Rig.Rider;
            Assert.That(rider, Is.Not.Null);
            var riderEuler = rider.localRotation.eulerAngles;
            var roll = Mathf.Abs(Mathf.DeltaAngle(0f, riderEuler.z));
            Assert.That(roll, Is.LessThan(0.5f),
                "rider must not stack a cosmetic roll on the physical "
                + "attitude — lean belongs to physics");

            // Physical wheels follow committed suspension: the authored
            // front pivot sits at the body-space mount minus the
            // compressed spring, steered by the committed road-wheel
            // angle, with roll spin on the Spin child.
            var pivot = pres.Rig.WheelPivots[0]; // tuning order: FL
            Assert.That(pivot, Is.Not.Null);
            var spec = tuning.wheels[0];
            var expectedY = spec.mount.y
                - (tuning.suspensionRestLength - 0.10f)
                + carVisual.bodyOriginHeight;
            Assert.That(pivot.localPosition.y,
                Is.EqualTo(expectedY).Within(0.001f),
                "front-left wheel pivot follows committed compression "
                + "through the authored mount");
            var steer = Quaternion.Euler(0f, 0.30f * Mathf.Rad2Deg, 0f);
            Assert.That(Quaternion.Angle(pivot.localRotation, steer),
                Is.LessThan(1f),
                "steerable wheel pivots carry the committed steer");
            var spin = pres.Rig.WheelSpins[0];
            Assert.That(spin, Is.Not.Null,
                "each authored wheel pivot owns a Spin child");
            Assert.That(
                Quaternion.Angle(spin.localRotation,
                    Quaternion.identity),
                Is.GreaterThan(5f),
                "the committed spin angle reaches the Spin child");

            // A recovery-epoch bump snaps to the new commit — the pose
            // never sweeps through a teleport the sim never made.
            var snapped = Quaternion.Euler(15f, 210f, 30f);
            var motor2 = Motor(4f, 3f, 0f, 0f);
            motor2.physics = PhysicalState(snapped, 6, wheels: true);
            racers[0].session.motor = motor2;
            physWorld.ApplyRaceFrame(Race(racers), 0.5f, null,
                participants);
            yield return null;
            Assert.That(
                Quaternion.Angle(pres.transform.rotation, snapped),
                Is.LessThan(1f),
                "recovery-epoch bump snaps the pose instead of "
                + "interpolating through the teleport");
            }
            finally
            {
                Object.Destroy(physHost);
            }
        }

        // Rematch + snapshot churn: repeated race start/end cycles and
        // repeated identical frames must never stack rigs, materials or
        // spawned parts — the world reuses the same views.
        [UnityTest]
        public IEnumerator RematchDoesNotAccumulateRigsOrMaterials()
        {
            var participants = new List<ParticipantState>
            {
                Participant("p0", 0, "fox", 0),
                Participant("p1", 1, "rabbit", 1)
            };
            var baselineMaterials =
                Resources.FindObjectsOfTypeAll<Material>().Length;

            for (var rematch = 0; rematch < 3; rematch++)
            {
                var racers = new[]
                {
                    Racer("p0", 0, "car-01",
                        Motor(-2f, 0f, 8f, 0f), 0f),
                    Racer("p1", 1, "motorcycle-01",
                        Motor(2f, 0f, 8f, 0f), 0f)
                };
                world.ApplyRaceFrame(Race(racers), 1f, null, participants);
                yield return null;
                Assert.That(
                    Object.FindObjectsByType<RacerVisualRig>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None).Length,
                    Is.EqualTo(2),
                    "rematch " + rematch
                    + ": exactly one rig per presented racer");
                // Same-frame churn: identical snapshots must rebind to
                // nothing.
                for (var i = 0; i < 5; i++)
                {
                    world.ApplyRaceFrame(Race(racers), 1f, null,
                        participants);
                    yield return null;
                }
                var pres = world.RacerPresentation("p0");
                Assert.That(pres.transform
                    .GetComponentsInChildren<Transform>(true).Length,
                    Is.LessThan(120),
                    "repeated identical snapshots stacked rig parts");
                // Race ends: every racer view is destroyed immediately.
                world.ApplyRaceFrame(null, 1f, null, participants);
                yield return null;
                Assert.That(
                    Object.FindObjectsByType<RacerVisualRig>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None).Length,
                    Is.EqualTo(0),
                    "rematch " + rematch + ": no rig survives race end");
                Assert.That(world.RacerPresentation("p0"), Is.Null);
            }
            yield return null;
            var afterMaterials =
                Resources.FindObjectsOfTypeAll<Material>().Length;
            Assert.That(afterMaterials,
                Is.LessThanOrEqualTo(baselineMaterials + 1),
                "rematch churn leaked materials: "
                + baselineMaterials + " -> " + afterMaterials);
        }

        // Every missing/broken visual path reports an honest fallback:
        // a vehicle with no authored visual logs + falls back to
        // primitives, a class-mismatched visual warns, and a racer with
        // an unresolvable character gets the neutral placeholder rider —
        // and the streamed facts name the fallback, never stale ids.
        [UnityTest]
        public IEnumerator MissingVisualReportsFallback()
        {
            // 1) Vehicle definition with no authored visual at all.
            var noVisual = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "ghost-car";
                a.silhouette = "car";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
            });
            LogAssert.Expect(LogType.Log,
                "VehiclePresentation: vehicle 'ghost-car' has no "
                + "authored visual — primitive fallback silhouette.");
            var view = new GameObject("no-visual-view");
            var pres = view.AddComponent<VehiclePresentation>();
            pres.SetVehicle(noVisual);
            Assert.That(pres.BoundVisualId,
                Is.EqualTo(RacerVisualRig.PrimitiveFallbackVisualId));
            Assert.That(pres.BoundCharacterId,
                Is.EqualTo(RacerVisualRig.NoCharacterId));
            Object.Destroy(view);

            // 2) A visual whose class cannot serve the silhouette:
            //    warns and falls back rather than mounting the wrong
            //    rig.
            var wrongClass = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "mismatch";
                a.silhouette = "car";
                a.visual = motoVisual;
            });
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    "VehiclePresentation: visual 'moto-01' cannot serve "
                    + "vehicle 'mismatch'"));
            view = new GameObject("mismatch-view");
            pres = view.AddComponent<VehiclePresentation>();
            pres.SetVehicle(wrongClass);
            Assert.That(pres.BoundVisualId,
                Is.EqualTo(RacerVisualRig.PrimitiveFallbackVisualId));
            Assert.That(pres.Rig == null || !pres.Rig.HasVehicleVisual,
                Is.True);
            Object.Destroy(view);

            // 3) World level: an unknown vehicle id keeps the existing
            //    generic-fallback warning AND reports the fallback fact.
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    "DrivingWorld: no definition asset for vehicle "
                    + "'ghost-99'"));
            var racers = new[]
            {
                Racer("p0", 0, "ghost-99", Motor(0f, 0f, 4f, 0f), 0f),
                Racer("p1", 1, "car-01", Motor(4f, 0f, 4f, 0f), 0f)
            };
            world.ApplyRaceFrame(Race(racers), 1f, null,
                new List<ParticipantState>
                {
                    Participant("p0", 0, "fox", 0),
                    Participant("p1", 1, "rabbit", 1)
                });
            yield return null;
            var fact = world.RacerFacts.Find(f => f.participantId == "p0");
            Assert.That(fact, Is.Not.Null);
            Assert.That(fact.vehicleVisualId,
                Is.EqualTo(RacerVisualRig.PrimitiveFallbackVisualId));
            Assert.That(fact.characterId,
                Is.EqualTo(RacerVisualRig.NoCharacterId));

            // 4) Authored vehicle + resolvable racer but an empty
            //    character catalog: the seat gets the honest generic
            //    rider, fact says "fallback" — never a stale species.
            Inject(world, "characters",
                new CharacterDefinitionAsset[0]);
            var soloRacers = new[]
            {
                Racer("solo", 0, "car-01", Motor(0f, 5f, 4f, 0f), 0f)
            };
            world.ApplyRaceFrame(Race(soloRacers), 1f, null, null);
            yield return null;
            var solo = world.RacerPresentation("solo");
            Assert.That(solo, Is.Not.Null);
            Assert.That(solo.BoundVisualId,
                Is.EqualTo(RacerVisualAuthoring.CarVisualId));
            Assert.That(solo.BoundCharacterId,
                Is.EqualTo(RacerVisualRig.FallbackCharacterId));
            var soloFact = world.RacerFacts.Find(
                f => f.participantId == "solo");
            Assert.That(soloFact, Is.Not.Null);
            Assert.That(soloFact.characterId,
                Is.EqualTo(RacerVisualRig.FallbackCharacterId));
            var rider = solo.Rig.Rider;
            Assert.That(rider, Is.Not.Null);
            Assert.That(rider.name, Is.EqualTo("Character-fallback"));
        }

        // The shared-skeleton contract every species prefab keeps: the
        // same named mount chain exists in all four animals and the rig
        // pieces never carry colliders.
        [Test]
        public void SharedSkeletonConventionsHoldForAllSpecies()
        {
#if UNITY_EDITOR
            var required = new[]
            {
                "Rig", "Hips", "Spine", "Head", "ArmL", "ArmR",
                "LegL", "LegR", "Tail", "ScarfAccent"
            };
            foreach (var id in RacerVisualAuthoring.CharacterIds)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    RacerVisualAuthoring.CharacterPrefabPath(id));
                Assert.That(prefab, Is.Not.Null, id);
                var inst = Object.Instantiate(prefab);
                try
                {
                    foreach (var name in required)
                        Assert.That(
                            FindTransform(inst.transform, name),
                            Is.Not.Null,
                            id + " is missing shared node " + name);
                    Assert.That(
                        inst.GetComponentsInChildren<Collider>(true),
                        Is.Empty, id + " prefab carries a collider");
                }
                finally
                {
                    Object.DestroyImmediate(inst);
                }
            }
            foreach (var path in new[]
            {
                RacerVisualAuthoring.CarPrefabPath,
                RacerVisualAuthoring.MotoPrefabPath
            })
            {
                var prefab =
                    AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                var inst = Object.Instantiate(prefab);
                try
                {
                    Assert.That(
                        inst.GetComponentsInChildren<Collider>(true),
                        Is.Empty,
                        path + " carries a collider");
                    Assert.That(FindTransform(inst.transform,
                        "SeatMount"), Is.Not.Null, path);
                    Assert.That(FindTransform(inst.transform,
                        "GripL"), Is.Not.Null, path);
                    Assert.That(FindTransform(inst.transform,
                        "GripR"), Is.Not.Null, path);
                }
                finally
                {
                    Object.DestroyImmediate(inst);
                }
            }
#endif
        }

        // The character catalog order is the AI/slot fallback mapping —
        // it must match the profile avatars the roster assigns, or an AI
        // racer would silently wear the wrong species.
        [Test]
        public void CatalogSlotOrderMatchesProfileAvatars()
        {
            for (var i = 0; i < characters.Length; i++)
            {
                var profile = ProfileFactory.Create(i, "en");
                Assert.That(characters[i].characterId,
                    Is.EqualTo(profile.avatar),
                    "catalog slot " + i + " must be the slot avatar");
            }
        }

        // --------------------------------------------------------------
        // helpers

        private static Transform FindTransform(Transform root,
            string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindTransform(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static Renderer FindRenderer(Transform root,
            string name)
        {
            var t = FindTransform(root, name);
            return t != null ? t.GetComponent<Renderer>() : null;
        }

        private static void AssertColor(Color actual, Color expected,
            string label)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.02f),
                label + " .r");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.02f),
                label + " .g");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.02f),
                label + " .b");
        }

        private static VehiclePhysicsState PhysicalState(
            Quaternion attitude, long recoveryEpoch, bool wheels)
        {
            var state = new VehiclePhysicsState
            {
                attitudeX = attitude.x,
                attitudeY = attitude.y,
                attitudeZ = attitude.z,
                attitudeW = attitude.w,
                grounded = true,
                recoveryEpoch = recoveryEpoch
            };
            if (wheels)
            {
                state.wheels = new WheelPhysicsState[4];
                for (var i = 0; i < 4; i++)
                    state.wheels[i] = new WheelPhysicsState
                    {
                        index = i,
                        grounded = true,
                        contactValid = true,
                        compression = 0.10f,
                        steering = i < 2 ? 0.30f : 0f,
                        spin = 3.5f
                    };
            }
            return state;
        }
    }
}
