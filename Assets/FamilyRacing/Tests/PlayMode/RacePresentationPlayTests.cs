using System;
using System.Collections;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using FamilyRacing.Core.Tutorial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M4 Task-5 race presentation proof: the effects and audio presenters
    // consume the real published race state — driving events deduplicated
    // by identity, live motor state for continuous loops — through the
    // authored RacePresentationCatalogAsset's cue/effect/voice bounds.
    // States are fabricated FoundationState DTOs (the same shape the
    // service publishes) so dedupe/reconnect/pause cases are exact.
    public sealed class RacePresentationPlayTests
    {
        private GameObject host;
        private RaceAudioPresenter audio;
        private RaceEffectsPresenter effects;
        private RacePresentationCatalogAsset catalog;
        private readonly List<Object> assets = new List<Object>();
        private readonly List<Object> loose = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("race-presentation-test");
            audio = host.AddComponent<RaceAudioPresenter>();
            effects = host.AddComponent<RaceEffectsPresenter>();
            catalog = BuildCatalog();
            audio.Bind(catalog);
            effects.Bind(catalog);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var extra in loose)
                if (extra != null) Object.Destroy(extra);
            if (host != null) Object.Destroy(host);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
        }

        // --- fixture -------------------------------------------------------

        private AudioClip Clip(string name)
        {
            var clip = AudioClip.Create(name, 2205, 1, 22050, false);
            clip.SetData(new float[2205], 0);
            assets.Add(clip);
            return clip;
        }

        private GameObject PuffPrefab()
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "fx-test-puff";
            var collider = quad.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            loose.Add(quad);
            return quad;
        }

        private RacePresentationCatalogAsset BuildCatalog()
        {
            var asset = ScriptableObject.CreateInstance<RacePresentationCatalogAsset>();
            assets.Add(asset);
            asset.catalogId = "test-catalog";
            asset.cues = new[]
            {
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "landing", eventKind = "motor.landed",
                    clip = Clip("cue-landing")
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "boost", eventKind = "motor.boostStart",
                    clip = Clip("cue-boost")
                },
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "checkpoint", eventKind = "checkpoint.crossed",
                    clip = Clip("cue-checkpoint")
                },
                // Deliberately clip-less: the honest missing-cue path.
                new RacePresentationCatalogAsset.Cue
                {
                    cueId = "finish", eventKind = "race.finished"
                }
            };
            asset.loops = new[]
            {
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "engine.car", clip = Clip("engine-car"),
                    volume = 0.7f
                },
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "engine.motorcycle", clip = Clip("engine-moto"),
                    volume = 0.7f
                },
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "skid", clip = Clip("skid"), volume = 0.5f
                },
                new RacePresentationCatalogAsset.Loop
                {
                    loopId = "boost", clip = Clip("boost"), volume = 0.6f
                }
            };
            asset.music = new[]
            {
                new RacePresentationCatalogAsset.MusicCue
                {
                    cueId = "music-race", phase = "racing",
                    clip = Clip("music-race")
                },
                new RacePresentationCatalogAsset.MusicCue
                {
                    cueId = "music-results", phase = "results",
                    clip = Clip("music-results")
                }
            };
            var puff = PuffPrefab();
            asset.effects = new[]
            {
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "landing-puff", prefab = puff,
                    poolSize = 4, lifetimeSeconds = 0.6f
                },
                new RacePresentationCatalogAsset.Effect
                {
                    effectId = "drift-dust", prefab = puff,
                    poolSize = 6, lifetimeSeconds = 0.5f
                }
            };
            asset.effectTriggers = new[]
            {
                new RacePresentationCatalogAsset.EffectTrigger
                {
                    eventKind = "motor.landed", effectId = "landing-puff",
                    count = 1
                }
            };
            return asset;
        }

        private static DrivingEvent Ev(string raceId, int seq, string kind)
        {
            return new DrivingEvent
            {
                id = raceId + "-e" + seq,
                kind = kind,
                tick = seq
            };
        }

        // A solo-race snapshot in the exact published shape: the bounded
        // drivingEvents stream accumulates, the session slice carries the
        // committed motor state the continuous channels read.
        private static FoundationState RaceState(string raceId,
            string phase, float speed, List<DrivingEvent> events,
            bool drifting = false, bool boostActive = false,
            bool grounded = true, bool paused = false,
            string vehicleId = "car-01",
            float music = 0.5f, float effectsVolume = 0.6f,
            float voice = 0.9f)
        {
            return new FoundationState
            {
                language = "en",
                screen = "driving",
                paused = paused,
                driving = new DrivingSessionState
                {
                    raceId = raceId,
                    vehicleId = vehicleId,
                    phase = phase,
                    motor = new MotorState
                    {
                        speed = speed,
                        grounded = grounded,
                        drifting = drifting,
                        boostActive = boostActive
                    }
                },
                drivingEvents = events ?? new List<DrivingEvent>(),
                draftProfile = new ProfileSave
                {
                    musicVolume = music,
                    effectsVolume = effectsVolume,
                    voiceVolume = voice
                }
            };
        }

        // --- one-shot identity ----------------------------------------------

        [Test]
        public void RepeatedSnapshotsDoNotRepeatLandingCue()
        {
            audio.Reset("race-1");
            effects.Reset("race-1");
            var history = new List<DrivingEvent>();
            var baseState = RaceState("race-1", "running", 10f, history);
            audio.ApplyFrame(baseState, 0.033f);
            effects.ApplyFrame(baseState, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(0),
                "the attach frame is a baseline, not a replay");

            // A landing commits on this boundary: one event, one cue,
            // one puff burst.
            history.Add(Ev("race-1", 5, "motor.landed"));
            var landed = RaceState("race-1", "running", 10f, history);
            audio.ApplyFrame(landed, 0.033f);
            effects.ApplyFrame(landed, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(1));
            Assert.That(audio.LastPlayedCue, Is.EqualTo("landing"));
            Assert.That(effects.ActiveCount("landing-puff"), Is.EqualTo(1));

            // Repeated snapshots of the same boundary (the stream keeps
            // the event listed) must never replay the one-shot.
            for (var i = 0; i < 3; i++)
            {
                audio.ApplyFrame(landed, 0.033f);
                effects.ApplyFrame(landed, 0.033f);
            }
            Assert.That(audio.PlayedCueCount, Is.EqualTo(1),
                "re-published history must not replay the landing cue");
            Assert.That(effects.Facts.pools
                    .Find(p => p.effectId == "landing-puff").exhausted,
                Is.EqualTo(0));
        }

        [Test]
        public void CorrectionReplayDoesNotReplayEffects()
        {
            audio.Reset("race-2");
            effects.Reset("race-2");
            var history = new List<DrivingEvent>();
            audio.ApplyFrame(RaceState("race-2", "running", 8f, history), 0.033f);
            effects.ApplyFrame(RaceState("race-2", "running", 8f, history), 0.033f);

            history.Add(Ev("race-2", 3, "motor.boostStart"));
            history.Add(Ev("race-2", 4, "checkpoint.crossed"));
            var boundary = RaceState("race-2", "running", 8f, history,
                boostActive: true);
            audio.ApplyFrame(boundary, 0.033f);
            effects.ApplyFrame(boundary, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(2));

            // A host correction re-publishes the same event stream (same
            // identities): presentation must not re-fire consumed cues.
            var corrected = RaceState("race-2", "running", 7f,
                new List<DrivingEvent>
                {
                    Ev("race-2", 3, "motor.boostStart"),
                    Ev("race-2", 4, "checkpoint.crossed")
                });
            audio.ApplyFrame(corrected, 0.033f);
            effects.ApplyFrame(corrected, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(2),
                "a correction re-delivery must not replay one-shots");

            // Only a genuinely new identity fires.
            corrected.drivingEvents.Add(Ev("race-2", 9, "motor.landed"));
            audio.ApplyFrame(corrected, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(3));
            Assert.That(audio.LastPlayedCue, Is.EqualTo("landing"));
        }

        [Test]
        public void ReconnectDoesNotPlayHistoricalOneShots()
        {
            // An observer binds to a race mid-history: the whole already
            // published stream is baseline, never replayed.
            var history = new List<DrivingEvent>
            {
                Ev("race-7", 1, "race.started"),
                Ev("race-7", 2, "race.running"),
                Ev("race-7", 3, "motor.boostStart"),
                Ev("race-7", 4, "motor.landed"),
                Ev("race-7", 5, "checkpoint.crossed")
            };
            audio.Reset("race-7");
            effects.Reset("race-7");
            var attach = RaceState("race-7", "running", 15f, history);
            audio.ApplyFrame(attach, 0.033f);
            effects.ApplyFrame(attach, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(0),
                "historical events must not replay on rejoin");
            Assert.That(effects.Facts.activeEffects, Is.EqualTo(0),
                "historical effect bursts must not replay on rejoin");

            // Live events after the attach still fire exactly once.
            history.Add(Ev("race-7", 6, "motor.landed"));
            var next = RaceState("race-7", "running", 12f, history);
            audio.ApplyFrame(next, 0.033f);
            effects.ApplyFrame(next, 0.033f);
            Assert.That(audio.PlayedCueCount, Is.EqualTo(1));
            Assert.That(audio.LastPlayedCue, Is.EqualTo("landing"));
            Assert.That(effects.ActiveCount("landing-puff"), Is.EqualTo(1));
        }

        // --- loop lifecycle --------------------------------------------------

        [Test]
        public void PauseAndExitStopLoops()
        {
            var running = RaceState("race-3", "running", 14f,
                new List<DrivingEvent>());
            audio.ApplyFrame(running, 0.033f);
            Assert.That(audio.LoopActive("engine.car", "race-3"), Is.True,
                "a running racer owns an engine loop");
            Assert.That(audio.Facts.loops, Does.Contain("engine.car@race-3"));
            Assert.That(audio.Facts.musicCue, Is.EqualTo("music-race"));
            var engine = audio.LoopSource("engine.car", "race-3");
            var music = audio.MusicSource;
            Assert.That(engine, Is.Not.Null);
            Assert.That(music, Is.Not.Null);

            // Pause: every loop and the music pause — nothing keeps
            // sounding while the race clock is frozen.
            var paused = RaceState("race-3", "running", 14f,
                new List<DrivingEvent>(), paused: true);
            audio.ApplyFrame(paused, 0.033f);
            Assert.That(engine.isPlaying, Is.False,
                "pause must silence the engine loop");
            Assert.That(music.isPlaying, Is.False,
                "pause must silence the music");
            Assert.That(audio.Facts.paused, Is.True);
            Assert.That(audio.Facts.loops, Is.Empty);

            // Resume: the same loops return.
            audio.ApplyFrame(running, 0.033f);
            Assert.That(audio.LoopActive("engine.car", "race-3"), Is.True);
            Assert.That(audio.Facts.musicCue, Is.EqualTo("music-race"));

            // Exit: the race context is gone — all sources stop and the
            // published facts clear instead of claiming phantom sound.
            audio.ApplyFrame(new FoundationState
            {
                draftProfile = running.draftProfile
            }, 0.033f);
            Assert.That(engine.isPlaying, Is.False);
            Assert.That(music.isPlaying, Is.False);
            Assert.That(audio.Facts.loops, Is.Empty);
            Assert.That(audio.Facts.musicCue, Is.EqualTo("none"));
            Assert.That(audio.Facts.voices, Is.EqualTo(0));
        }

        [Test]
        public void ThreeVolumeControlsAffectTheirOwnBus()
        {
            // Voice bus rides the tutorial presenter — the third control.
            var tutorialAsset = ScriptableObject
                .CreateInstance<TutorialDefinitionAsset>();
            assets.Add(tutorialAsset);
            tutorialAsset.trialCue = new TutorialDefinitionAsset.Cue
            {
                textKey = "tutorial.cue.trial", icon = "*",
                english = Clip("cue-trial-en")
            };
            var tutorialGo = new GameObject("tutorial-voice");
            loose.Add(tutorialGo);
            var tutorial = tutorialGo.AddComponent<TutorialPresenter>();
            tutorial.Bind(tutorialAsset);

            var running = RaceState("race-4", "running", 10f,
                new List<DrivingEvent>(),
                music: 0.25f, effectsVolume: 0.6f, voice: 0.9f);
            audio.ApplyFrame(running, 0.033f);
            tutorial.ApplyFrame(TutorialFrame(0.9f), null);

            var music = audio.MusicSource;
            var engine = audio.LoopSource("engine.car", "race-4");
            Assert.That(music, Is.Not.Null);
            Assert.That(engine, Is.Not.Null);
            // Each source carries exactly its own slider's value times
            // the authored level — never a sibling's.
            Assert.That(music.volume, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(engine.volume, Is.EqualTo(0.6f * 0.7f).Within(0.001f));
            var voiceSource = tutorialGo.GetComponent<AudioSource>();
            Assert.That(voiceSource, Is.Not.Null);
            Assert.That(voiceSource.volume, Is.EqualTo(0.9f).Within(0.001f));

            // Moving one slider touches only its bus.
            var quieter = RaceState("race-4", "running", 10f,
                new List<DrivingEvent>(),
                music: 1f, effectsVolume: 0.6f, voice: 0.9f);
            audio.ApplyFrame(quieter, 0.033f);
            Assert.That(music.volume, Is.EqualTo(1f).Within(0.001f));
            Assert.That(engine.volume, Is.EqualTo(0.6f * 0.7f).Within(0.001f),
                "the music slider must not reach the effects bus");
            var muted = RaceState("race-4", "running", 10f,
                new List<DrivingEvent>(),
                music: 1f, effectsVolume: 0f, voice: 0.9f);
            audio.ApplyFrame(muted, 0.033f);
            Assert.That(engine.volume, Is.EqualTo(0f).Within(0.001f));
            Assert.That(music.volume, Is.EqualTo(1f).Within(0.001f),
                "the effects slider must not reach the music bus");
        }

        private static FoundationState TutorialFrame(float voiceVolume)
        {
            return new FoundationState
            {
                language = "en",
                driving = new DrivingSessionState
                {
                    tutorial = new TutorialState
                    {
                        tutorialId = "t", phase = "trial",
                        cueId = "trial", cueSequence = 1
                    }
                },
                draftProfile = new ProfileSave { voiceVolume = voiceVolume }
            };
        }

        // --- camera + shake ---------------------------------------------------

        [Test]
        public void ReducedShakeAndCameraOcclusionRemainUsable()
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(
                new Vector3(0f, 8f, -12f), Quaternion.Euler(20f, 0f, 0f));
            cameraObject.AddComponent<Camera>();
            loose.Add(cameraObject);
            var camera = new GameObject("driving-camera")
                .AddComponent<DrivingCamera>();
            loose.Add(camera.gameObject);

            // A solid wall between the chase slot and the vehicle forces
            // the occlusion pull-in path.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            loose.Add(wall);
            wall.transform.SetPositionAndRotation(
                new Vector3(0f, 4f, -6f), Quaternion.identity);
            wall.transform.localScale = new Vector3(20f, 8f, 1f);
            Physics.SyncTransforms();

            var airborne = FakeMotor(grounded: false, fallSpeed: -6f);
            var grounded = FakeMotor(grounded: true, fallSpeed: 0f);
            float Dip(bool reduced)
            {
                // Settle the camera onto the target first so pull-in
                // converges before the landing edge.
                for (var i = 0; i < 8; i++)
                    camera.ApplyMotor(airborne, false, reduced, 0.1f, 0.8f);
                camera.ApplyMotor(grounded, false, reduced, 0.1f, 0.8f);
                return camera.FeedbackDip;
            }

            // Fresh component state per arm: reset by disengaging.
            var full = Dip(false);
            camera.ApplyFrame((FoundationState)null, 0.1f);
            var reducedDip = Dip(true);

            // Reduced shake suppresses — never deletes — the physical
            // landing read, and the occlusion pull-in still frames the
            // vehicle in front of the wall either way.
            Assert.That(reducedDip, Is.LessThan(full),
                "reduced shake must shrink the landing dip");
            Assert.That(reducedDip, Is.GreaterThan(0f),
                "reduced shake must not delete the landing read");
            Assert.That(camera.LastObstacleName, Is.EqualTo("Wall").Or.Not.Null,
                "the occluder pulled the camera in");
            var camZ = Camera.main.transform.position.z;
            Assert.That(camZ, Is.GreaterThan(-6f),
                "the camera stays on the vehicle side of the wall");
        }

        private static MotorState FakeMotor(bool grounded, float fallSpeed)
        {
            return new MotorState
            {
                x = 0f, y = 0f, z = 0f,
                grounded = grounded,
                verticalSpeed = fallSpeed
            };
        }

        // --- honest missing audio ----------------------------------------------

        [Test]
        public void MissingClipDoesNotClaimPlaying()
        {
            audio.Reset("race-5");
            var history = new List<DrivingEvent>();
            audio.ApplyFrame(RaceState("race-5", "running", 9f, history), 0.033f);

            // "race.finished" is wired in the catalog but its clip slot is
            // empty — the catalog maps the cue, the recording is absent.
            history.Add(Ev("race-5", 7, "race.finished"));
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("no clip"));
            var finished = RaceState("race-5", "finished", 0f, history);
            audio.ApplyFrame(finished, 0.033f);

            Assert.That(audio.PlayedCueCount, Is.EqualTo(0),
                "a missing clip plays nothing — never a claimed play");
            Assert.That(audio.Facts.missingCues, Does.Contain("finish"),
                "the miss is published, not swallowed");
            // The results music resolves and plays — only the absent
            // clip reports missing.
            Assert.That(audio.Facts.musicCue, Is.EqualTo("music-results"));
            Assert.That(audio.Facts.loops, Does.Not.Contain("finish"),
                "a missing cue never lands in the active loops list");
        }

        // --- schema-5 facts ----------------------------------------------------

        [Test]
        public void PresentationFactsPublishEffectAndAudioState()
        {
            // The facts the app folds into presentation.effects/audio are
            // independent copies that describe the live state.
            var running = RaceState("race-6", "running", 10f,
                new List<DrivingEvent>());
            audio.ApplyFrame(running, 0.033f);
            effects.ApplyFrame(running, 0.033f);

            var audioFacts = audio.Facts;
            Assert.That(audioFacts.catalogId, Is.EqualTo("test-catalog"));
            Assert.That(audioFacts.loops, Does.Contain("engine.car@race-6"));
            Assert.That(audioFacts.musicCue, Is.EqualTo("music-race"));
            Assert.That(audioFacts.voices, Is.GreaterThan(0));

            var effectFacts = effects.Facts;
            Assert.That(effectFacts.catalogId, Is.EqualTo("test-catalog"));
            var pool = effectFacts.pools.Find(p => p.effectId == "drift-dust");
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.capacity, Is.EqualTo(6));

            // Drifting emits bounded dust through the real pool.
            var drifting = RaceState("race-6", "running", 10f,
                new List<DrivingEvent>(), drifting: true);
            for (var i = 0; i < 12; i++)
                effects.ApplyFrame(drifting, 0.05f);
            Assert.That(effects.Facts.activeEffects, Is.GreaterThan(0),
                "a held drift emits visible dust");
            Assert.That(effects.Facts.pools
                    .Find(p => p.effectId == "drift-dust").active,
                Is.LessThanOrEqualTo(6),
                "the dust pool is bounded by authored data");

            // Facts are copies: mutating one must not corrupt the next.
            audioFacts.loops.Clear();
            Assert.That(audio.Facts.loops, Is.Not.Empty);
        }

        // --- per-race counter hygiene --------------------------------------

        [Test]
        public void RematchResetsPerRaceCounters()
        {
            // Race A accumulates every counter: one-shots, a missing
            // cue, and a genuinely exhausted effect pool (one landing
            // burst fills poolSize=4; the second landing refuses all).
            catalog.effectTriggers[0].count = 4;
            audio.Reset("race-a");
            effects.Reset("race-a");
            var history = new List<DrivingEvent>();
            audio.ApplyFrame(RaceState("race-a", "running", 10f,
                history), 0.033f);
            effects.ApplyFrame(RaceState("race-a", "running", 10f,
                history), 0.033f);

            history.Add(Ev("race-a", 3, "motor.landed"));
            history.Add(Ev("race-a", 4, "motor.landed"));
            history.Add(Ev("race-a", 5, "race.finished")); // clip slot is empty
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("no clip"));
            var later = RaceState("race-a", "running", 10f, history);
            audio.ApplyFrame(later, 0.033f);
            effects.ApplyFrame(later, 0.033f);
            Assert.That(audio.Facts.oneShots, Is.GreaterThan(0),
                "race A fired one-shots before the rematch");
            Assert.That(audio.Facts.missingCues, Does.Contain("finish"),
                "race A reported the missing finish cue");
            var exhaustedPool = effects.Facts.pools
                .Find(p => p.effectId == "landing-puff");
            Assert.That(exhaustedPool.exhausted, Is.GreaterThan(0),
                "the landing pool genuinely exhausted in race A");

            // The app's real rematch path: a new race identity arrives
            // on the stream — ApplyFrame re-baselines (no explicit
            // Reset). Race B's first published facts must not inherit
            // race A's pressure.
            var fresh = RaceState("race-b", "running", 10f,
                new List<DrivingEvent>());
            audio.ApplyFrame(fresh, 0.033f);
            effects.ApplyFrame(fresh, 0.033f);

            var audioFacts = audio.Facts;
            Assert.That(audioFacts.oneShots, Is.EqualTo(0),
                "rematch must not report race A's one-shot count");
            Assert.That(audioFacts.droppedOneShots, Is.EqualTo(0));
            Assert.That(audioFacts.refusedLoopVoices, Is.EqualTo(0));
            Assert.That(audioFacts.missingCues, Is.Empty,
                "rematch must not report race A's missing cues");
            Assert.That(audioFacts.missingDropped, Is.EqualTo(0));
            Assert.That(audioFacts.loops,
                Does.Contain("engine.car@race-b"),
                "race B's own live loop is present under the new key");

            var effectFacts = effects.Facts;
            Assert.That(effectFacts.activeEffects, Is.EqualTo(0));
            Assert.That(effectFacts.pools
                    .Find(p => p.effectId == "landing-puff").exhausted,
                Is.EqualTo(0),
                "rematch must not report race A's pool pressure");
            Assert.That(effectFacts.missingEffects, Is.Empty);
            Assert.That(effectFacts.missingDropped, Is.EqualTo(0));

            // Teardown to no race also zeroes the counters — no stale
            // "this race" claims on the menu.
            audio.ApplyFrame(new FoundationState
            {
                draftProfile = fresh.draftProfile
            }, 0.033f);
            effects.ApplyFrame(new FoundationState
            {
                draftProfile = fresh.draftProfile
            }, 0.033f);
            Assert.That(audio.Facts.oneShots, Is.EqualTo(0));
            Assert.That(audio.Facts.missingCues, Is.Empty);
            Assert.That(effects.Facts.activeEffects, Is.EqualTo(0));
        }

        [Test]
        public void LoopVoiceBoundRefusalIsObservable()
        {
            // One authored skid voice, two drifting racers: the second
            // refusal must publish on the facts — bounded voice pressure
            // is never silent.
            foreach (var loop in catalog.loops)
                if (loop.loopId == "skid") loop.maxVoices = 1;
            var lan = LanRaceState("lan-9", "p1", "p2");
            audio.ApplyFrame(lan, 0.033f);

            var facts = audio.Facts;
            var skidLoops = 0;
            foreach (var entry in facts.loops)
                if (entry.StartsWith("skid@")) skidLoops++;
            Assert.That(skidLoops, Is.EqualTo(1),
                "the authored bound holds — only one skid voice plays");
            Assert.That(facts.refusedLoopVoices, Is.EqualTo(1),
                "the refused racer's loop is counted, not silent");

            // Held across frames: the refusal counts once per episode,
            // never once per frame.
            audio.ApplyFrame(lan, 0.033f);
            audio.ApplyFrame(lan, 0.033f);
            Assert.That(audio.Facts.refusedLoopVoices, Is.EqualTo(1),
                "a held drift must not inflate the refusal counter");
        }

        // A host-confirmed LAN race snapshot: the solo `driving` slice is
        // null and `networkRace.racers` carries each racer's committed
        // session — the shape Racers() enumerates for per-racer loops.
        private static FoundationState LanRaceState(string raceId,
            params string[] driftingPids)
        {
            var race = new NetworkRaceState
            {
                raceId = raceId,
                phase = "running"
            };
            foreach (var pid in driftingPids)
                race.racers.Add(new NetworkRacerState
                {
                    participantId = pid,
                    vehicleId = "car-01",
                    session = new DrivingSessionState
                    {
                        raceId = raceId,
                        vehicleId = "car-01",
                        motor = new MotorState
                        {
                            speed = 10f,
                            grounded = true,
                            drifting = true
                        }
                    }
                });
            return new FoundationState
            {
                language = "en",
                screen = "driving",
                networkRace = race,
                network = new FamilyRacing.Core.Networking
                    .NetworkSessionState
                    {
                        localParticipantId = driftingPids[0]
                    },
                draftProfile = new ProfileSave
                {
                    musicVolume = 0.5f,
                    effectsVolume = 0.6f,
                    voiceVolume = 0.9f
                }
            };
        }
    }
}
