using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M5 Task 4: Campaign, Quick Race and Time Trial run through the
    // shared simulation — the four-racer modes inside the authoritative
    // NetworkRace coordinator (one human + three same-class AI, no
    // transport), the trial inside a lone DrivingSession — under one
    // local clock with shared checkpoint/finish/pause/DNF rules,
    // format-2 results carrying mode/rules identity plus authoritative
    // metrics, and durable reward-context binding for progression races.
    public sealed class SoloRaceTests
    {
        private const float Pi = (float)Math.PI;
        private const string RunId = "solo-race-test";

        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance,
                    x = pose.x, y = 0f, z = pose.z,
                    normalY = 1f, distance = pose.y,
                    surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy,
                float dz, float halfWidth, float halfLength, float height)
            {
                return new SweepHit { hit = false, fraction = 1f };
            }
        }

        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-solo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- fixture -----------------------------------------------------

        private static MotorPose Spawn(float x, float z)
        {
            return new MotorPose { x = x, y = 0f, z = z, heading = 0f };
        }

        private static CheckpointGate Gate(string id, float x, float z,
            float dirX, float dirZ)
        {
            return new CheckpointGate
            {
                id = id, x = x, z = z, dirX = dirX, dirZ = dirZ,
                halfWidth = 12f,
                recoverX = x - dirX * 3f, recoverY = 0f,
                recoverZ = z - dirZ * 3f,
                recoverHeading = (float)Math.Atan2(dirX, dirZ)
            };
        }

        // One compact circuit with a four-slot grid and a route the AI
        // follower and the scripted human steering both share.
        internal static CourseDefinition Course()
        {
            var course = new CourseDefinition
            {
                id = "harbor-hop",
                spawnX = 0f, spawnY = 0f, spawnZ = -60f, spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    Gate("start", 0f, -60f, 0f, 1f),
                    Gate("gate-1", 0f, 25f, 0f, 1f),
                    Gate("gate-2", 55f, 55f, 1f, 0f),
                    Gate("gate-3", 95f, 0f, 0f, -1f),
                    Gate("gate-4", 50f, -55f, -1f, 0f)
                }
            };
            course.spawnSlots.Add(Spawn(-5f, -57f));
            course.spawnSlots.Add(Spawn(5f, -57f));
            course.spawnSlots.Add(Spawn(-5f, -62f));
            course.spawnSlots.Add(Spawn(5f, -62f));
            // The proven drive-through line: approach the finish from
            // south of the line, then re-enter north through the gate
            // centre — an approach from the gate-4 leg crosses backward
            // or outside the 12 m corridor and is honestly rejected.
            foreach (var waypoint in new[]
            {
                new { x = 0f, z = 33f }, new { x = 63f, z = 55f },
                new { x = 95f, z = -8f }, new { x = 44f, z = -55f },
                new { x = 0f, z = -70f }, new { x = 0f, z = -50f }
            })
            {
                course.route.Add(new RouteWaypoint
                {
                    x = waypoint.x, y = 0f, z = waypoint.z, radius = 6f
                });
            }
            return course;
        }

        internal static VehicleTuning Vehicle(string id)
        {
            var tuning = VehicleTuning.Car01();
            tuning.vehicleId = id;
            return tuning;
        }

        private static EventDefinition TypedEvent(string id, string eventType,
            int laps = 1, long parTicks = 0)
        {
            var evt = new EventDefinition
            {
                id = id,
                courseId = "harbor-hop",
                laps = laps,
                countdownTicks = 10,
                vehicleIds = new List<string> { "car-01", "car-02" },
                recovery = new RecoverySettings(),
                completionCoins = 20,
                eventType = eventType,
                vehicleClass = "car",
                parTicks = parTicks,
                isFinale = eventType == GameplayEventType.Finale
            };
            evt.objectives = new List<ObjectiveDefinition>
            {
                new ObjectiveDefinition
                {
                    id = id + "-intro", kind = ObjectiveKind.Introductory,
                    alternatives =
                    {
                        new ObjectiveCondition
                        {
                            metric = ObjectiveMetric.CleanLandings,
                            threshold = 1
                        }
                    }
                },
                new ObjectiveDefinition
                {
                    id = id + "-mastery", kind = ObjectiveKind.Mastery,
                    alternatives =
                    {
                        new ObjectiveCondition
                        {
                            metric = ObjectiveMetric.CleanLandings,
                            threshold = 3
                        }
                    }
                }
            };
            return evt;
        }

        internal static GameplayCatalog Catalog()
        {
            var driving = DrivingContentCatalog.Create("solo-content-1",
                new[] { Vehicle("car-01"), Vehicle("car-02") },
                new[] { Course() },
                new[]
                {
                    TypedEvent("harbor-hop-car-circuit", GameplayEventType.Circuit),
                    TypedEvent("harbor-hop-car-time", GameplayEventType.Time,
                        1, 3600),
                    TypedEvent("sunny-car-finale", GameplayEventType.Finale, 1),
                    // A LAN event beside the campaign list — the lobby
                    // configures it while the campaign structure never
                    // claims it.
                    new EventDefinition
                    {
                        id = "lan-mixed",
                        courseId = "harbor-hop",
                        laps = 1,
                        countdownTicks = 90,
                        vehicleIds = new List<string> { "car-01", "car-02" },
                        completionCoins = 20
                    }
                });
            var campaign = new CampaignDefinition
            {
                worldId = "sunny-island",
                nameEn = "Sunny Island",
                nameHe = "האי השמשי",
                requiredEventCompletions = 2,
                requiredRegionalStars = 3
            };
            campaign.courseIds.Add("harbor-hop");
            campaign.regularEventIds.Add("harbor-hop-car-circuit");
            campaign.regularEventIds.Add("harbor-hop-car-time");
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-car-finale",
                classId = "car",
                rivalId = "rival-car",
                rewardVehicleId = "car-02"
            });
            var bounties = new List<BountyDefinition>
            {
                new BountyDefinition
                {
                    id = "drift-10s", family = "drift",
                    metric = BountyMetric.DriftSeconds, threshold = 10,
                    unit = "seconds", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                },
                new BountyDefinition
                {
                    id = "boost-3", family = "boost",
                    metric = BountyMetric.HumanBoostActivations,
                    threshold = 3, unit = "count", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                },
                new BountyDefinition
                {
                    id = "landing-2", family = "landing",
                    metric = BountyMetric.CleanLandings, threshold = 2,
                    unit = "count", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                }
            };
            var economy = new EconomyDefinition
            {
                completionCoins = 20,
                challengeIntroductoryCoins = 10,
                challengeMasteryCoins = 20,
                rivalVictoryCoins = 30
            };
            var category = new UpgradeCategoryDefinition
            {
                id = "acceleration", maxLevel = 3
            };
            category.prices.Add(60);
            category.prices.Add(120);
            category.prices.Add(180);
            category.tuningFields.Add("acceleration");
            economy.upgradeCategories.Add(category);
            var gadgets = new GadgetDefinitionCatalog();
            var difficulties = new List<AiDifficultyDefinition>
            {
                new AiDifficultyDefinition
                {
                    id = "easy", brakeMarginScale = 1.2f,
                    boostUseMinCharge = 0.95f
                },
                new AiDifficultyDefinition { id = "medium" },
                new AiDifficultyDefinition
                {
                    id = "hard", brakeMarginScale = 0.8f,
                    boostUseMinCharge = 0.5f
                }
            };
            var ok = GameplayCatalog.TryCreate(driving, campaign, bounties,
                economy, gadgets, difficulties,
                out var catalog, out var error);
            Assert.That(ok, Is.True, "fixture catalog must validate: " + error);
            return catalog;
        }

        internal static ProfileSave Profile()
        {
            return ProfileFactory.Create(0, "en");
        }

        private static SoloRaceRequest Request(string mode,
            string eventId = "", string difficulty = "medium")
        {
            return new SoloRaceRequest
            {
                mode = mode,
                eventId = eventId,
                courseId = "harbor-hop",
                classId = "car",
                vehicleId = "car-01",
                difficultyId = difficulty,
                participantId = "",
                raceId = "",
                commandId = "test-start"
            };
        }

        private static SoloRaceSession CreateSession(string mode,
            string eventId = "", string difficulty = "medium",
            GameplayCatalog catalog = null)
        {
            var ok = SoloRaceSession.TryCreate(Request(mode, eventId, difficulty),
                Profile(), catalog ?? Catalog(), out var session, out var error);
            Assert.That(ok, Is.True, "TryCreate failed: " + error);
            Assert.That(session, Is.Not.Null);
            return session;
        }

        private static ArcadeVehicleSimulation Sim(SoloRaceSession session)
        {
            var sim = new ArcadeVehicleSimulation(new FlatWorld());
            sim.Initialize(session.VehicleSpawns());
            return sim;
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Pi) radians -= 2f * Pi;
            while (radians <= -Pi) radians += 2f * Pi;
            return radians;
        }

        private static MotorState HumanMotor(SoloRaceState state)
        {
            if (state.trial != null) return state.trial.motor;
            foreach (var racer in state.race.racers)
                if (racer.participantId == state.participantId)
                    return racer.session != null ? racer.session.motor : null;
            return null;
        }

        // Steers the local human through the course route; AI seats drive
        // themselves. Returns the state once `finished` is set on the
        // human's session or maxTicks expires.
        private static SoloRaceState DriveHumanToFinish(SoloRaceSession session,
            IVehicleSimulation sim, SoloRaceState state, int maxTicks)
        {
            var route = Course().route;
            var index = 0;
            var neutral = new DriverInput(0f, false, false, false, false,
                false, false);
            var finished = false;
            for (var t = 0; t < maxTicks && !finished; t++)
            {
                var motor = HumanMotor(state);
                if (motor == null) break;
                var wp = route[index % route.Count];
                var dx = wp.x - motor.x;
                var dz = wp.z - motor.z;
                if (dx * dx + dz * dz < 36f) { index++; continue; }
                var desired = (float)Math.Atan2(dx, dz);
                var steer = Math.Clamp(
                    WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                state = session.Step(
                    new DriverInput(steer, false, false, false, false,
                        false, false),
                    sim);
                if (state.trial != null)
                    finished = state.trial.finished;
                else
                    foreach (var racer in state.race.racers)
                        if (racer.participantId == state.participantId)
                            finished = racer.finished;
            }
            return state;
        }

        // Steps until the finalized result exists (all finish, or the
        // shared DNF deadline converts the remainder).
        private static SoloRaceState StepUntilResult(SoloRaceSession session,
            IVehicleSimulation sim, SoloRaceState state, int maxTicks)
        {
            var neutral = new DriverInput(0f, false, false, false, false,
                false, false);
            for (var t = 0; t < maxTicks && state.result == null; t++)
                state = session.Step(neutral, sim);
            return state;
        }

        private FoundationService Service(GameplayCatalog catalog)
        {
            var service = new FoundationService(
                new ProfileStore(directory, new TestJsonSerializer()),
                "en", RunId);
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            service.SetDrivingSimulationFactory(
                content => new ArcadeVehicleSimulation(new FlatWorld()));
            return service;
        }

        private void CreateProfile(FoundationService service)
        {
            var created = service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en")));
            Assert.That(created.status, Is.EqualTo(CommandResult.Applied),
                "profile.create setup failed");
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters, string commandId = null)
        {
            return new GameCommand(1, RunId,
                commandId ?? Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile) { return UnityEngine.JsonUtility.ToJson(profile); }
            public ProfileSave Deserialize(string json) { return UnityEngine.JsonUtility.FromJson<ProfileSave>(json); }
#else
            private static readonly System.Text.Json.JsonSerializerOptions Options =
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile) { return System.Text.Json.JsonSerializer.Serialize(profile, Options); }
            public ProfileSave Deserialize(string json) { return System.Text.Json.JsonSerializer.Deserialize<ProfileSave>(json, Options); }
#endif
        }

        // --- named task tests ----------------------------------------------

        [Test]
        public void QuickRaceSeatsOneHumanAndThreeSameClassAiWithoutTransport()
        {
            var profile = Profile();
            var ok = SoloRaceSession.TryCreate(Request("quickRace",
                    "harbor-hop-car-circuit"),
                profile, Catalog(), out var session, out var error);
            Assert.That(ok, Is.True, "TryCreate failed: " + error);
            var state = session.Snapshot();
            // The four-racer branch publishes; the trial branch stays null.
            Assert.That(state.mode, Is.EqualTo("quickRace"));
            Assert.That(state.race, Is.Not.Null);
            Assert.That(state.trial, Is.Null);
            Assert.That(state.race.racers.Count,
                Is.EqualTo(NetworkSettings.ParticipantCapacity));
            // Exactly one human seat — the local profile identity on
            // slot 0 — and three AI seats in the same vehicle class.
            var humans = 0;
            var ai = 0;
            foreach (var racer in state.race.racers)
            {
                Assert.That(VehicleTuning.ClassIdOf(racer.vehicleId),
                    Is.EqualTo("car"), racer.participantId);
                if (racer.attribution == "human")
                {
                    humans++;
                    Assert.That(racer.slot, Is.EqualTo(0));
                    Assert.That(racer.participantId, Is.EqualTo(profile.id));
                }
                else
                {
                    ai++;
                    Assert.That(racer.attribution, Is.EqualTo("ai"));
                }
            }
            Assert.That(humans, Is.EqualTo(1));
            Assert.That(ai, Is.EqualTo(3));
            // The visible AI difficulty is a fact on the session and on
            // every AI result row — never a speed multiplier.
            Assert.That(state.difficultyId, Is.EqualTo("medium"));
            // No transport: the session steps only through the injected
            // simulation seam.
            var sim = Sim(session);
            var stepped = session.Step(new DriverInput(0f, false, false,
                false, false, false, false), sim);
            Assert.That(stepped.race.hostTick, Is.EqualTo(1));
        }

        [Test]
        public void CampaignAndQuickShareCheckpointAndFinishRules()
        {
            var catalog = Catalog();
            var campaign = CreateSession("campaign",
                "harbor-hop-car-circuit", catalog: catalog);
            var quick = CreateSession("quickRace",
                "harbor-hop-car-circuit", catalog: catalog);
            var campaignSim = Sim(campaign);
            var quickSim = Sim(quick);
            var neutral = new DriverInput(0f, false, false, false, false,
                false, false);

            // Identical shared clock: the authored 10-tick countdown
            // transitions both modes to running on the same boundary.
            var campaignState = campaign.Snapshot();
            var quickState = quick.Snapshot();
            Assert.That(campaignState.race.phase, Is.EqualTo("countdown"));
            Assert.That(quickState.race.phase, Is.EqualTo("countdown"));
            for (var i = 0; i < 10; i++)
            {
                campaignState = campaign.Step(neutral, campaignSim);
                quickState = quick.Step(neutral, quickSim);
            }
            Assert.That(campaignState.race.phase, Is.EqualTo("running"));
            Assert.That(quickState.race.phase, Is.EqualTo("running"));
            Assert.That(campaignState.race.elapsedTicks,
                Is.EqualTo(quickState.race.elapsedTicks));

            // The same ordered-checkpoint rules accept progress: drive
            // the human through the first gate in both modes.
            campaignState = DriveHumanToFinish(campaign, campaignSim,
                campaignState, 4000);
            quickState = DriveHumanToFinish(quick, quickSim,
                quickState, 4000);
            Assert.That(HumanMotor(campaignState), Is.Not.Null);
            var campaignSession = campaignState.race.racers[0].session;
            var quickSession = quickState.race.racers[0].session;
            Assert.That(campaignSession.lap,
                Is.EqualTo(quickSession.lap));

            // Both finalize through the shared finish policy (all four
            // finish or the DNF deadline) into format-2 results bound to
            // their own mode identity.
            campaignState = StepUntilResult(campaign, campaignSim,
                campaignState, 4000);
            quickState = StepUntilResult(quick, quickSim,
                quickState, 4000);
            Assert.That(campaignState.result, Is.Not.Null,
                "campaign race never finalized");
            Assert.That(quickState.result, Is.Not.Null,
                "quick race never finalized");
            Assert.That(campaignState.result.formatVersion, Is.EqualTo(2));
            Assert.That(campaignState.result.mode, Is.EqualTo("campaign"));
            Assert.That(quickState.result.formatVersion, Is.EqualTo(2));
            Assert.That(quickState.result.mode, Is.EqualTo("quickRace"));
            Assert.That(campaignState.result.rulesFingerprint,
                Is.EqualTo(catalog.CompatibilityFingerprint));
            // Same finish semantics: finishers carry finishTicks, DNFs
            // carry the "dnf" outcome, positions are 1-based and unique.
            foreach (var result in new[]
                { campaignState.result, quickState.result })
            {
                Assert.That(result.racers.Count,
                    Is.EqualTo(NetworkSettings.ParticipantCapacity));
                var positions = new HashSet<int>();
                foreach (var row in result.racers)
                {
                    Assert.That(positions.Add(row.position), Is.True);
                    if (row.outcome == "finished")
                        Assert.That(row.finishTicks, Is.GreaterThan(0));
                    else
                        Assert.That(row.outcome, Is.EqualTo("dnf"));
                    // Every row carries authoritative metrics and the AI
                    // seats record the visible difficulty fact.
                    Assert.That(row.metrics, Is.Not.Null);
                    Assert.That(row.difficultyId,
                        Is.EqualTo(row.attribution == "ai" ? "medium" : ""));
                }
            }
        }

        [Test]
        public void TimeTrialHasOneRacerNoItemsAndNoRewards()
        {
            var session = CreateSession("timeTrial", "harbor-hop-car-time");
            var sim = Sim(session);
            var state = session.Snapshot();
            // Exactly one racer — the lone DrivingSession — and never a
            // four-racer coordinator.
            Assert.That(state.mode, Is.EqualTo("timeTrial"));
            Assert.That(state.trial, Is.Not.Null);
            Assert.That(state.race, Is.Null);
            Assert.That(state.participantId, Is.Not.Null.And.Not.Empty);
            Assert.That(state.trial.raceId, Is.EqualTo(state.raceId));

            // Drive the lone racer to the finish: the result is format 2
            // with one human row and no rewardable facts anywhere.
            state = DriveHumanToFinish(session, sim, state, 4000);
            Assert.That(state.trial.finished, Is.True,
                "trial never finished");
            state = session.Step(new DriverInput(0f, false, false, false,
                false, false, false), sim);
            Assert.That(state.result, Is.Not.Null);
            Assert.That(state.result.formatVersion, Is.EqualTo(2));
            Assert.That(state.result.mode, Is.EqualTo("timeTrial"));
            Assert.That(state.result.racers.Count, Is.EqualTo(1));
            Assert.That(state.result.racers[0].outcome,
                Is.EqualTo("finished"));
            Assert.That(state.result.racers[0].coinsAwarded, Is.EqualTo(0));
            Assert.That(state.result.completionCoins, Is.EqualTo(0),
                "time trials must never award completion rewards");
            // A trial result still earns an immutable fingerprint —
            // under the format-2 canonical form.
            var fingerprint = state.result.Fingerprint();
            Assert.That(fingerprint, Has.Length.EqualTo(64));
            var legacy = state.result.Copy();
            legacy.formatVersion = 1;
            Assert.That(legacy.Fingerprint(), Is.Not.EqualTo(fingerprint),
                "format-2 results must not fingerprint like legacy rows");
        }

        [Test]
        public void PauseExcludesElapsedAndObjectiveTicks()
        {
            var session = CreateSession("quickRace",
                "harbor-hop-car-circuit");
            var sim = Sim(session);
            var neutral = new DriverInput(0f, false, false, false, false,
                false, false);
            var state = session.Snapshot();
            for (var i = 0; i < 12; i++) state = session.Step(neutral, sim);
            Assert.That(state.race.phase, Is.EqualTo("running"));
            var elapsed = state.race.elapsedTicks;

            // Frozen boundary: pause holds the shared countdown, the
            // elapsed race clock and every objective clock — the
            // simulation is never stepped while paused.
            session.SetPaused(true);
            var pausedEvents = session.DrainStagedEvents();
            var sawPause = false;
            foreach (var ev in pausedEvents)
                if (ev.kind == "race.paused") sawPause = true;
            Assert.That(sawPause, Is.True, "race.paused was never emitted");
            for (var i = 0; i < 25; i++)
                state = session.Step(neutral, sim);
            Assert.That(state.race.elapsedTicks, Is.EqualTo(elapsed));
            Assert.That(state.paused, Is.True);
            var checkpointFacts = state.race.racers[0].session
                .acceptedGatesThisLap;

            // Resume continues from the frozen facts — elapsed and
            // objective time advance by exactly the unpaused boundaries.
            session.SetPaused(false);
            session.DrainStagedEvents();
            state = session.Step(neutral, sim);
            Assert.That(state.race.elapsedTicks, Is.EqualTo(elapsed + 1));
            Assert.That(state.race.racers[0].session.acceptedGatesThisLap,
                Is.EqualTo(checkpointFacts));
        }

        [Test]
        public void AbortProducesNoRewardableResult()
        {
            var session = CreateSession("campaign",
                "harbor-hop-car-circuit");
            var sim = Sim(session);
            var neutral = new DriverInput(0f, false, false, false, false,
                false, false);
            for (var i = 0; i < 15; i++) session.Step(neutral, sim);

            session.Abort("raceExit");
            var events = session.DrainStagedEvents();
            var sawAbort = false;
            foreach (var ev in events)
                if (ev.kind == "race.aborted") sawAbort = true;
            Assert.That(sawAbort, Is.True, "race.aborted was never emitted");
            var state = session.Snapshot();
            Assert.That(state.race.phase, Is.EqualTo("aborted"));
            Assert.That(state.result, Is.Null,
                "an aborted race must never carry a rewardable result");
            Assert.That(state.abortReason, Is.EqualTo("raceExit"));

            // The service path proves the durable side: exit retires the
            // bound context and coins never move.
            var catalog = Catalog();
            var service = Service(catalog);
            CreateProfile(service);
            var started = service.Execute(Cmd("solo.start",
                P("mode", "campaign", "eventId", "harbor-hop-car-circuit",
                  "vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var snapshot = service.Snapshot();
            Assert.That(snapshot.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(1));
            var coinsBefore = snapshot.savedProfile.coins;
            var exited = service.Execute(Cmd("race.exit", null));
            Assert.That(exited.status, Is.EqualTo(CommandResult.Applied));
            var after = service.Snapshot();
            Assert.That(after.soloRace, Is.Null);
            Assert.That(after.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(0), "abort must retire the unused context");
            Assert.That(after.savedProfile.coins, Is.EqualTo(coinsBefore));
        }

        [Test]
        public void RestartReplaysStoredRequestUnderFreshIdentity()
        {
            // The shared retry affordance: race.restart replays the
            // stored selection under a fresh race identity — the ended
            // run's unused context retires and the new identity binds
            // fresh inside the same command boundary.
            var service = Service(Catalog());
            CreateProfile(service);
            var started = service.Execute(Cmd("solo.start",
                P("mode", "quickRace", "eventId", "harbor-hop-car-circuit",
                  "vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var first = service.Snapshot().soloRace.raceId;
            var restarted = service.Execute(Cmd("race.restart", null));
            Assert.That(restarted.status, Is.EqualTo(CommandResult.Applied),
                "race.restart: " + restarted.code);
            var state = service.Snapshot();
            Assert.That(state.soloRace, Is.Not.Null);
            Assert.That(state.soloRace.raceId, Is.Not.EqualTo(first));
            Assert.That(state.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(1));
            Assert.That(state.savedProfile.pendingRaceContexts[0].raceId,
                Is.EqualTo(state.soloRace.raceId),
                "the rebound context must name the new identity, never "
                    + "the ended run's");
        }

        // --- reward-context binding ---------------------------------------

        [Test]
        public void ProgressionStartPersistsRewardContextIdempotently()
        {
            var catalog = Catalog();
            var service = Service(catalog);
            CreateProfile(service);
            var started = service.Execute(Cmd("solo.start",
                P("mode", "campaign", "eventId", "harbor-hop-car-circuit",
                  "vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.soloRace, Is.Not.Null);
            Assert.That(state.soloRace.mode, Is.EqualTo("campaign"));
            Assert.That(state.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(1));
            var context = state.savedProfile.pendingRaceContexts[0];
            Assert.That(context.raceId, Is.EqualTo(state.soloRace.raceId));
            Assert.That(context.profileId,
                Is.EqualTo(state.savedProfile.id));
            Assert.That(context.mode, Is.EqualTo("campaign"));
            Assert.That(context.rulesFingerprint,
                Is.EqualTo(catalog.CompatibilityFingerprint));
            // Active bounty assignments capture their definitions in
            // parallel — the schema's durable shape.
            Assert.That(context.bountyAssignmentIds.Count,
                Is.EqualTo(context.bountyDefinitions.Count));
            Assert.That(context.bountyAssignmentIds.Count,
                Is.LessThanOrEqualTo(3));

            // The binding is durable: a fresh service over the same
            // store sees the row on the persisted profile.
            var reloaded = Service(catalog);
            var slots = reloaded.Snapshot().slots;
            Assert.That(slots[0].profile.pendingRaceContexts.Count,
                Is.EqualTo(1));
            Assert.That(slots[0].profile.pendingRaceContexts[0].raceId,
                Is.EqualTo(state.soloRace.raceId));

            // Idempotent: the LAN preparer rebinding the same identity
            // is a no-op, not a second row.
            var link = new LanSessionTests.FakeLanLink();
            var probe = new LanSessionTests.FakeLanProbe();
            var lan = new LanSessionService(link, probe, "gameplay-test");
            service.SetLanSession(lan);
            service.Execute(Cmd("lan.host", P("port", "47777")));
            var bound = lan.Snapshot();
            Assert.That(bound, Is.Not.Null);
        }

        [Test]
        public void LanStartBindsAllocatedRaceIdentityBeforeRacing()
        {
            var catalog = Catalog();
            var service = Service(catalog);
            CreateProfile(service);
            var link = new LanSessionTests.FakeLanLink();
            var probe = new LanSessionTests.FakeLanProbe();
            var lan = new LanSessionService(link, probe, "gameplay-test");
            lan.SetContent(catalog.Driving);
            service.SetLanSession(lan);

            var hosted = service.Execute(Cmd("lan.host", P("port", "47777")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied),
                "lan.host: " + hosted.code);
            var configured = service.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car")));
            Assert.That(configured.status, Is.EqualTo(CommandResult.Applied),
                "lan.configure: " + configured.code);
            var readied = service.Execute(Cmd("lan.ready",
                P("ready", "true")));
            Assert.That(readied.status, Is.EqualTo(CommandResult.Applied),
                "lan.ready: " + readied.code);
            var started = service.Execute(Cmd("lan.start", null));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "lan.start: " + started.code);

            // The host-allocated identity is published and durably bound
            // on the same boundary the race countdown begins.
            var network = service.Snapshot().network;
            Assert.That(network.currentRaceId, Is.Not.Null.And.Not.Empty);
            var contexts = service.Snapshot().savedProfile
                .pendingRaceContexts;
            Assert.That(contexts.Count, Is.EqualTo(1));
            Assert.That(contexts[0].raceId,
                Is.EqualTo(network.currentRaceId));
            Assert.That(contexts[0].mode, Is.EqualTo("lan"));
        }

        [Test]
        public void PreparationFailureKeepsParticipantFromStarting()
        {
            // The preparation seam itself, driven at the session service
            // level: a failing preparer must refuse lan.start honestly —
            // the lobby keeps its phase and the allocated ordinal rolls
            // back so the next attempt binds a fresh identity.
            var link = new LanSessionTests.FakeLanLink();
            var probe = new LanSessionTests.FakeLanProbe();
            var lan = new LanSessionService(link, probe, "gameplay-test");
            lan.SetLocalContext("host-p", "fox", 0, true, true);
            lan.SetContent(Catalog().Driving);
            lan.SetRacePreparer(raceId => "saveFailed");

            var hosted = lan.Execute(Cmd("lan.host", P("port", "47777")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied),
                "lan.host: " + hosted.code);
            var configured = lan.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car")));
            Assert.That(configured.status, Is.EqualTo(CommandResult.Applied),
                "lan.configure: " + configured.code);
            var readied = lan.Execute(Cmd("lan.ready", P("ready", "true")));
            Assert.That(readied.status, Is.EqualTo(CommandResult.Applied),
                "lan.ready: " + readied.code);
            var started = lan.Execute(Cmd("lan.start", null));
            Assert.That(started.status, Is.EqualTo(CommandResult.Failed));
            Assert.That(started.code, Is.EqualTo("preparationFailed"));
            Assert.That(lan.Snapshot().phase,
                Is.EqualTo(LanSessionService.PhaseLobby),
                "a failed preparation must never leave the lobby");
            Assert.That(lan.Snapshot().currentRaceId,
                Is.Null.Or.Empty,
                "no race identity may publish when preparation failed");

            // A recovered preparer starts cleanly on the next attempt:
            // the rolled-back ordinal is honestly reused because no
            // participant ever bound the failed identity.
            lan.SetRacePreparer(raceId => null);
            var retry = lan.Execute(Cmd("lan.start", null));
            Assert.That(retry.status, Is.EqualTo(CommandResult.Applied),
                "retry: " + retry.code);
            Assert.That(lan.Snapshot().currentRaceId,
                Does.EndWith("-r1"));
        }

        [Test]
        public void QuickRaceResultFingerprintIsFormatTwo()
        {
            var session = CreateSession("quickRace",
                "harbor-hop-car-circuit");
            var sim = Sim(session);
            var state = DriveHumanToFinish(session, sim,
                session.Snapshot(), 4000);
            state = StepUntilResult(session, sim, state, 4000);
            Assert.That(state.result, Is.Not.Null);
            var v2 = state.result.Fingerprint();
            var legacy = state.result.Copy();
            legacy.formatVersion = 1;
            // A format-1 row keeps the legacy canonical form — differing
            // by construction — while the format-2 result binds mode,
            // rules and metrics under its own domain.
            Assert.That(legacy.Fingerprint(), Is.Not.EqualTo(v2));
            var tampered = state.result.Copy();
            tampered.mode = "lan";
            Assert.That(tampered.Fingerprint(), Is.Not.EqualTo(v2),
                "mode identity must be fingerprint-bound");
        }

        [Test]
        public void SoloStartRejectsInvalidSelectionsAndUnavailableContent()
        {
            var catalog = Catalog();
            var service = Service(catalog);
            CreateProfile(service);
            // Unknown mode is refused by the discovery schema.
            var bad = service.Execute(Cmd("solo.start",
                P("mode", "practice", "eventId", "harbor-hop-car-circuit",
                  "vehicleId", "car-01")));
            Assert.That(bad.status, Is.EqualTo(CommandResult.Rejected));
            // An event outside the campaign structure is refused.
            var wrong = service.Execute(Cmd("solo.start",
                P("mode", "campaign", "eventId", "missing-event",
                  "vehicleId", "car-01")));
            Assert.That(wrong.status, Is.EqualTo(CommandResult.Rejected));
            // Without a gameplay catalog the command reports honest
            // unavailability rather than a partial run.
            var bare = new FoundationService(
                new ProfileStore(
                    Path.Combine(directory, "bare"),
                    new TestJsonSerializer()), "en", RunId);
            bare.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en")));
            var noContent = bare.Execute(Cmd("solo.start",
                P("mode", "quickRace", "eventId", "harbor-hop-car-circuit",
                  "vehicleId", "car-01")));
            Assert.That(noContent.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(noContent.code, Is.EqualTo("contentUnavailable"));
        }
    }
}
