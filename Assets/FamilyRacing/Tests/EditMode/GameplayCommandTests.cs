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
#if UNITY_5_3_OR_NEWER
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // M5 Task 10: the complete local gameplay loop exposed through the
    // semantic command surface. UI callbacks and agent commands share the
    // same dispatch, so these tests pin the command contract: campaign
    // map access, Quick Race / Time Trial setup, the garage and bounty
    // board screens, results replay/continue/save-retry, honest locked
    // reasons, and the rule that no command grants rewards or bypasses
    // traversal. LAN admission fingerprint coverage lives here too —
    // protocol-3 peers must bind the gameplay catalog fingerprint, with
    // driving-only fallback for legacy catalogs.
    public sealed class GameplayCommandTests
    {
        private const string RunId = "gameplay-cmd-test";

        private string directory;
        private TestJsonSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-cmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- helpers ----------------------------------------------------

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        private static GameplayCatalog Catalog()
        {
            return CampaignTestFixture.Catalog();
        }

        private FoundationService Service(GameplayCatalog catalog,
            ProfileStore store)
        {
            var service = new FoundationService(store, "en", RunId);
            if (catalog != null)
            {
                service.SetDrivingContent(catalog.Driving);
                service.SetGameplayCatalog(catalog);
            }
            service.SetDrivingSimulationFactory(
                content => new ArcadeVehicleSimulation(new FlatWorld()));
            return service;
        }

        private FoundationService ServiceWithProfile(GameplayCatalog catalog,
            ProfileStore store, ProfileSave seeded)
        {
            if (seeded != null) store.Save(seeded);
            var service = Service(catalog, store);
            var selected = service.Execute(Cmd("profile.select",
                P("slot", "0")));
            Assert.That(selected.status, Is.EqualTo(CommandResult.Applied),
                "profile.select setup failed: " + selected.code);
            return service;
        }

        private FoundationService ServiceWithProfile(GameplayCatalog catalog)
        {
            return ServiceWithProfile(catalog, Store(),
                CampaignTestFixture.Profile());
        }

        // Steps the local race under neutral input until the shared
        // coordinator produces a finalized result (AI seats finish; the
        // idle human is DNF'd by the shared grace deadline) or the tick
        // budget expires.
        private static SoloRaceState StepUntilResult(
            FoundationService service, int maxTicks)
        {
            var state = service.Snapshot().soloRace;
            for (var t = 0; t < maxTicks && (state == null
                    || state.result == null); t++)
            {
                service.StepSolo();
                state = service.Snapshot().soloRace;
            }
            return state;
        }

        private static (bool available, string reason) Availability(
            FoundationService service, string action)
        {
            foreach (var descriptor in service.Discover())
                if (descriptor.action == action)
                    return (descriptor.available, descriptor.reason);
            Assert.Fail("action not discovered: " + action);
            return (false, "");
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

        // --- command discovery ------------------------------------------

        [Test]
        public void LoopActionsAdvertisePinnedSchemas()
        {
            var service = new FoundationService(Store(), "en", RunId);
            var actions = new Dictionary<string, List<string>>();
            foreach (var descriptor in service.Discover())
            {
                var required = new List<string>();
                foreach (var p in descriptor.parameters)
                    if (p.required) required.Add(p.name);
                actions[descriptor.action] = required;
            }
            foreach (var action in new[]
            {
                "campaign.open", "campaign.start",
                "quickRace.open", "quickRace.start",
                "timeTrial.open", "timeTrial.start",
                "garage.open", "bounties.open",
                "upgrade.purchase", "cosmetic.equip",
                "results.retrySave", "results.retry", "results.continue"
            })
                Assert.That(actions.ContainsKey(action), Is.True,
                    "missing semantic action: " + action);
            Assert.That(actions["campaign.start"], Is.EquivalentTo(
                new[] { "eventId", "vehicleId", "difficulty" }));
            Assert.That(actions["quickRace.start"], Is.EquivalentTo(
                new[] { "courseId", "classId", "vehicleId", "difficulty" }));
            Assert.That(actions["timeTrial.start"], Is.EquivalentTo(
                new[] { "courseId", "classId" }));
            Assert.That(actions["upgrade.purchase"], Is.EquivalentTo(
                new[] { "purchaseId", "vehicleId", "category",
                    "expectedLevel" }));
            Assert.That(actions["cosmetic.equip"], Is.EquivalentTo(
                new[] { "slot", "cosmeticId" }));
            foreach (var action in new[]
            {
                "campaign.open", "quickRace.open", "timeTrial.open",
                "garage.open", "bounties.open",
                "results.retrySave", "results.retry", "results.continue"
            })
                Assert.That(actions[action], Is.Empty,
                    action + " must take no required parameters");
        }

        // --- campaign map ------------------------------------------------

        [Test]
        public void CampaignOpenRequiresContentThenProfile()
        {
            var store = Store();
            store.Save(CampaignTestFixture.Profile());
            var service = Service(null, store);
            var noContent = service.Execute(Cmd("campaign.open", null));
            Assert.That(noContent.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(noContent.code, Is.EqualTo("contentUnavailable"));

            var bound = Service(null, store);
            bound.SetDrivingContent(Catalog().Driving);
            bound.SetGameplayCatalog(Catalog());
            var noProfile = bound.Execute(Cmd("campaign.open", null));
            Assert.That(noProfile.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(noProfile.code,
                Is.EqualTo("requiresSelectedProfile"));
        }

        [Test]
        public void CampaignOpenPublishesAccessSlice()
        {
            var service = ServiceWithProfile(Catalog());
            var opened = service.Execute(Cmd("campaign.open", null));
            Assert.That(opened.status, Is.EqualTo(CommandResult.Applied),
                "campaign.open failed: " + opened.code);
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("campaign"));
            // The schema-6 progression slice: campaign access is a
            // first-class observable — world identity, per-event rows in
            // authored order, and the unresolved-reward gate.
            Assert.That(state.progression, Is.Not.Null);
            Assert.That(state.progression.available, Is.True);
            Assert.That(state.progression.campaignWorldId,
                Is.EqualTo("sunny-island"));
            Assert.That(state.progression.campaignEvents.Count,
                Is.EqualTo(26), "24 regular events + 2 finales");
            var first = state.progression.campaignEvents[0];
            Assert.That(first.eventId,
                Is.EqualTo("harbor-hop-car-circuit"));
            Assert.That(first.locked, Is.False);
            Assert.That(first.stars, Is.EqualTo(0));
            var finale = state.progression.campaignEvents[
                state.progression.campaignEvents.Count - 1];
            Assert.That(finale.eventId,
                Is.EqualTo("sunny-island-motorcycle-finale"));
            Assert.That(finale.finale, Is.True);
            Assert.That(finale.locked, Is.True,
                "a fresh profile must see the gated finale as locked");
            Assert.That(finale.lockReason, Is.EqualTo("eventLocked"));
            Assert.That(state.progression.rewardUnresolved, Is.False);
        }

        [Test]
        public void CampaignStartSeatsTheSameSessionAsSoloStart()
        {
            var service = ServiceWithProfile(Catalog());
            var started = service.Execute(Cmd("campaign.start",
                P("eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01", "difficulty", "easy")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "campaign.start failed: " + started.code);
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("driving"));
            Assert.That(state.soloRace, Is.Not.Null);
            Assert.That(state.soloRace.mode, Is.EqualTo("campaign"));
            Assert.That(state.soloRace.race, Is.Not.Null,
                "campaign runs the shared four-racer coordinator");
            Assert.That(state.soloRace.race.racers.Count, Is.EqualTo(4));
            // The progression race bound its durable reward context
            // before the first simulated boundary.
            Assert.That(state.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(1));
            Assert.That(state.soloRace.tuningSource,
                Is.EqualTo("purchased"),
                "the human seat resolves the profile's purchased tune");
        }

        [Test]
        public void CampaignStartFinaleRejectsEventLocked()
        {
            var service = ServiceWithProfile(Catalog());
            var locked = service.Execute(Cmd("campaign.start",
                P("eventId", "sunny-island-car-finale",
                    "vehicleId", "car-01", "difficulty", "easy")));
            Assert.That(locked.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(locked.code, Is.EqualTo("eventLocked"));
            Assert.That(service.Snapshot().soloRace, Is.Null,
                "a locked event must never start a session");
        }

        // --- quick race / time trial --------------------------------------

        [Test]
        public void QuickRaceStartResolvesTheAuthoredCircuit()
        {
            var service = ServiceWithProfile(Catalog());
            var started = service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-01", "difficulty", "medium")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "quickRace.start failed: " + started.code);
            var state = service.Snapshot();
            Assert.That(state.soloRace, Is.Not.Null);
            Assert.That(state.soloRace.mode, Is.EqualTo("quickRace"));
            Assert.That(state.soloRace.race, Is.Not.Null);
            Assert.That(state.soloRace.race.racers.Count, Is.EqualTo(4));
            Assert.That(state.soloRace.result, Is.Null);
        }

        [Test]
        public void QuickRaceStartRejectsDishonestPicks()
        {
            var service = ServiceWithProfile(Catalog());
            var badCourse = service.Execute(Cmd("quickRace.start",
                P("courseId", "nowhere", "classId", "car",
                    "vehicleId", "car-01", "difficulty", "easy")));
            Assert.That(badCourse.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(badCourse.code, Is.EqualTo("unknownEvent"),
                "a course with no authored circuit reports honestly");
            var wrongClass = service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "motorcycle",
                    "vehicleId", "car-01", "difficulty", "easy")));
            Assert.That(wrongClass.status,
                Is.EqualTo(CommandResult.Rejected));
            var lockedVehicle = service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-02", "difficulty", "easy")));
            Assert.That(lockedVehicle.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(lockedVehicle.code, Is.EqualTo("vehicleLocked"),
                "a garage-locked vehicle can never race");
            var missing = service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-01")));
            Assert.That(missing.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(missing.code, Is.EqualTo("missingParameter"),
                "difficulty is a pinned required parameter");
            Assert.That(service.Snapshot().soloRace, Is.Null);
        }

        [Test]
        public void TimeTrialStartSeatsNormalizedStarter()
        {
            var service = ServiceWithProfile(Catalog());
            var started = service.Execute(Cmd("timeTrial.start",
                P("courseId", "harbor-hop", "classId", "car")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "timeTrial.start failed: " + started.code);
            var state = service.Snapshot();
            Assert.That(state.soloRace, Is.Not.Null);
            Assert.That(state.soloRace.mode, Is.EqualTo("timeTrial"));
            Assert.That(state.soloRace.trial, Is.Not.Null);
            Assert.That(state.soloRace.race, Is.Null);
            Assert.That(state.soloRace.tuningSource,
                Is.EqualTo("normalized"),
                "the trial seats the authored normalized starter");
            Assert.That(state.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(0), "time trials never bind a reward context");
        }

        // --- garage / bounties screens -------------------------------------

        [Test]
        public void GarageAndBountiesOpenThroughCommands()
        {
            var service = ServiceWithProfile(Catalog());
            var garage = service.Execute(Cmd("garage.open", null));
            Assert.That(garage.status, Is.EqualTo(CommandResult.Applied),
                "garage.open failed: " + garage.code);
            Assert.That(service.Snapshot().screen, Is.EqualTo("garage"));

            var board = service.Execute(Cmd("bounties.open", null));
            Assert.That(board.status, Is.EqualTo(CommandResult.Applied),
                "bounties.open failed: " + board.code);
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("bounties"));
            Assert.That(state.progression.bounties.Count, Is.EqualTo(3),
                "exactly three active assignments ride the board");
            foreach (var row in state.progression.bounties)
            {
                Assert.That(row.assignmentId, Is.Not.Empty);
                Assert.That(row.bountyId, Is.Not.Empty);
                Assert.That(row.resolved, Is.True,
                    "seeded assignments resolve against the catalog");
            }
        }

        [Test]
        public void LoopScreensRejectDuringRaceAndLan()
        {
            var service = ServiceWithProfile(Catalog());
            Assert.That(service.Execute(Cmd("campaign.start",
                P("eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01", "difficulty", "easy"))).status,
                Is.EqualTo(CommandResult.Applied));
            var during = service.Execute(Cmd("campaign.open", null));
            Assert.That(during.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(during.code, Is.EqualTo("raceActive"),
                "navigation can never abandon a live race");
            Assert.That(service.Execute(Cmd("race.exit", null)).status,
                Is.EqualTo(CommandResult.Applied));

            var link = new LanSessionTests.FakeLanLink();
            var lan = LanSessionTests.StandaloneService(link,
                new LanSessionTests.FakeLanProbe(),
                service.Snapshot().savedProfile.id);
            service.SetLanSession(lan);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            foreach (var action in new[]
            {
                "campaign.open", "quickRace.open", "timeTrial.open",
                "garage.open", "bounties.open",
                "campaign.start", "quickRace.start", "timeTrial.start",
                "results.retry", "results.continue"
            })
            {
                var command = action == "campaign.start"
                    ? Cmd(action, P("eventId", "harbor-hop-car-circuit",
                        "vehicleId", "car-01", "difficulty", "easy"))
                    : action == "quickRace.start"
                    ? Cmd(action, P("courseId", "harbor-hop",
                        "classId", "car", "vehicleId", "car-01",
                        "difficulty", "easy"))
                    : action == "timeTrial.start"
                    ? Cmd(action, P("courseId", "harbor-hop",
                        "classId", "car"))
                    : Cmd(action, null);
                var blocked = service.Execute(command);
                Assert.That(blocked.status,
                    Is.EqualTo(CommandResult.Rejected), action);
                Assert.That(blocked.code, Is.EqualTo("networkActive"),
                    action + " must refuse inside a LAN session");
            }
        }

        // --- garage commands ---------------------------------------------

        [Test]
        public void CosmeticEquipRequiresItsAuthoredSlot()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            profile.ownedCosmetics.Add("livery-rival-car");
            var service = ServiceWithProfile(catalog, Store(), profile);

            var missing = service.Execute(Cmd("cosmetic.equip",
                P("cosmeticId", "livery-rival-car")));
            Assert.That(missing.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(missing.code, Is.EqualTo("missingParameter"),
                "slot is a pinned required parameter");

            var wrong = service.Execute(Cmd("cosmetic.equip",
                P("slot", "paint", "cosmeticId", "livery-rival-car")));
            Assert.That(wrong.status, Is.EqualTo(CommandResult.Rejected),
                "a mismatched slot is rejected honestly");
            Assert.That(wrong.code, Is.EqualTo("slotMismatch"));

            var equipped = service.Execute(Cmd("cosmetic.equip",
                P("slot", "livery", "cosmeticId", "livery-rival-car")));
            Assert.That(equipped.status, Is.EqualTo(CommandResult.Applied),
                "equip failed: " + equipped.code);
            Assert.That(service.Snapshot().savedProfile.equippedCosmetics,
                Is.EquivalentTo(new[] { "livery-rival-car" }));
        }

        // --- results / replay / save retry --------------------------------

        [Test]
        public void ResultsCommandsGateOnAFinishedRace()
        {
            var service = ServiceWithProfile(Catalog());
            var idle = service.Execute(Cmd("results.retry", null));
            Assert.That(idle.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(idle.code, Is.EqualTo("unavailable"));
            Assert.That(service.Execute(Cmd("results.continue", null))
                .status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Execute(Cmd("results.retrySave", null))
                .code, Is.EqualTo("noPendingResult"));

            Assert.That(service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-01", "difficulty", "easy"))).status,
                Is.EqualTo(CommandResult.Applied));
            var running = service.Execute(Cmd("results.retry", null));
            Assert.That(running.status, Is.EqualTo(CommandResult.Rejected),
                "replay is a results affordance, not a mid-race one");
            Assert.That(running.code, Is.EqualTo("unavailable"));

            var finished = StepUntilResult(service, 9000);
            Assert.That(finished, Is.Not.Null);
            Assert.That(finished.result, Is.Not.Null,
                "the coordinator must finalize within the tick budget");
            var firstRaceId = finished.raceId;

            var retried = service.Execute(Cmd("results.retry", null));
            Assert.That(retried.status, Is.EqualTo(CommandResult.Applied),
                "results.retry failed: " + retried.code);
            var replay = service.Snapshot().soloRace;
            Assert.That(replay, Is.Not.Null);
            Assert.That(replay.raceId, Is.Not.EqualTo(firstRaceId),
                "replay mints a fresh race identity");
            Assert.That(replay.mode, Is.EqualTo("quickRace"));
            Assert.That(service.Snapshot().screen, Is.EqualTo("driving"));

            // Finish the replay, then continue back to the setup screen.
            finished = StepUntilResult(service, 9000);
            Assert.That(finished.result, Is.Not.Null);
            var continued = service.Execute(Cmd("results.continue", null));
            Assert.That(continued.status,
                Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.soloRace, Is.Null,
                "continue retires the finished session");
            Assert.That(state.screen, Is.EqualTo("quickRace"),
                "continue returns to the mode's own setup screen");
        }

        [Test]
        public void ResultsRetrySaveReplaysTheRetainedContext()
        {
            var catalog = Catalog();
            var toggling = new TogglingSerializer();
            var store = new ProfileStore(directory, toggling);
            var service = ServiceWithProfile(catalog, store,
                CampaignTestFixture.Profile());
            // An unresolved result can arrive from either loop — this
            // one comes from a finalized LAN result (the same shape the
            // Task-7 gate tests produce), then the session is left so the
            // local command surface owns the retry.
            var link = new LanSessionTests.FakeLanLink();
            var lan = LanSessionTests.StandaloneService(link,
                new LanSessionTests.FakeLanProbe(),
                service.Snapshot().savedProfile.id);
            service.SetLanSession(lan);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.configure",
                P("eventId", "harbor-hop-car-circuit",
                    "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var raceId = lan.Snapshot().currentRaceId;
            toggling.failing = true;
            var result = new RaceResult
            {
                formatVersion = 2, resultId = raceId + "/result",
                sessionId = "lan-s", raceId = raceId,
                eventId = "harbor-hop-car-circuit",
                contentVersion = "gameplay-loop-1",
                mode = "lan",
                rulesFingerprint = catalog.CompatibilityFingerprint,
                finalized = true, finalizedHostTick = 300,
                finalizedElapsedTicks = 4000, completionCoins = 20
            };
            result.racers.Add(new RaceResultEntry
            {
                position = 1, slot = 0,
                participantId = service.Snapshot().savedProfile.id,
                vehicleId = "car-01", attribution = "human",
                outcome = "finished", finishTicks = 3900,
                lapsCompleted = 2, coinsAwarded = 20
            });
            Assert.That(lan.PublishResult(result), Is.True);
            Assert.That(service.RewardUnresolved, Is.True);
            Assert.That(service.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));

            var state = service.Snapshot();
            Assert.That(state.progression.rewardUnresolved, Is.True,
                "the failed save must stay observable on every snapshot");
            Assert.That(state.progression.rewardUnresolvedRaceId,
                Is.EqualTo(raceId));

            // Still failing: the retry reports the honest save failure
            // and keeps the retained pair for a later retry.
            var stillFailing = service.Execute(Cmd("results.retrySave", null));
            Assert.That(stillFailing.status,
                Is.EqualTo(CommandResult.Failed));
            Assert.That(stillFailing.code, Is.EqualTo("saveFailed"));
            Assert.That(service.RewardUnresolved, Is.True);

            toggling.failing = false;
            var retried = service.Execute(Cmd("results.retrySave", null));
            Assert.That(retried.status, Is.EqualTo(CommandResult.Applied),
                "retrySave failed: " + retried.code);
            Assert.That(service.RewardUnresolved, Is.False);
            Assert.That(service.Snapshot().progression.rewardUnresolved,
                Is.False);
            Assert.That(service.Snapshot().savedProfile.coins,
                Is.EqualTo(25), "20 completion + 5 first-place bonus");
            // Replaying the command id is a duplicate, not a second grant.
            var dup = service.Execute(Cmd("results.retrySave", null));
            Assert.That(dup.code, Is.EqualTo("noPendingResult"));
        }

        [Test]
        public void ResultsContinueReturnsToCampaignMap()
        {
            var service = ServiceWithProfile(Catalog());
            Assert.That(service.Execute(Cmd("campaign.open", null)).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Execute(Cmd("campaign.start",
                P("eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01", "difficulty", "easy"))).status,
                Is.EqualTo(CommandResult.Applied));
            var finished = StepUntilResult(service, 9000);
            Assert.That(finished.result, Is.Not.Null);
            var continued = service.Execute(Cmd("results.continue", null));
            Assert.That(continued.status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().screen, Is.EqualTo("campaign"),
                "a campaign finish continues back to the map");
        }

        // Task 10 review fix: a finished result whose commit failed must
        // gate replay/continue (and every progression write) BEFORE any
        // session teardown — the finished session's overlay is the only
        // retrySave surface, so a rejected command leaves it mounted.
        [Test]
        public void UnresolvedSaveGatesReplayContinueAndProgressionStarts()
        {
            var toggling = new TogglingSerializer();
            var store = new ProfileStore(directory, toggling);
            var service = ServiceWithProfile(Catalog(), store,
                CampaignTestFixture.Profile());
            Assert.That(service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-01", "difficulty", "easy"))).status,
                Is.EqualTo(CommandResult.Applied));
            // The context bind already committed; only the RESULT
            // transaction fails now.
            toggling.failing = true;
            var finished = StepUntilResult(service, 9000);
            Assert.That(finished, Is.Not.Null);
            Assert.That(finished.result, Is.Not.Null,
                "the coordinator must finalize within the tick budget");
            Assert.That(service.RewardUnresolved, Is.True,
                "the failed commit retains the result+context pair");
            var raceId = finished.raceId;

            // Discovery and the handlers agree on every gated surface —
            // an enabled affordance may never hide a rewardUnresolved
            // rejection.
            foreach (var action in new[] { "results.retry",
                "results.continue", "race.restart" })
            {
                var availability = Availability(service, action);
                Assert.That(availability.available, Is.False,
                    action + " must advertise the unresolved gate");
                Assert.That(availability.reason,
                    Is.EqualTo("rewardUnresolved"),
                    action + " availability reason");
            }
            Assert.That(
                Availability(service, "results.retrySave").available,
                Is.True, "the remedy itself never gates");

            var retry = service.Execute(Cmd("results.retry", null));
            Assert.That(retry.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(retry.code, Is.EqualTo("rewardUnresolved"));
            var kept = service.Snapshot();
            Assert.That(kept.soloRace, Is.Not.Null,
                "a rejected retry must not tear the session down");
            Assert.That(kept.soloRace.raceId, Is.EqualTo(raceId));
            Assert.That(kept.soloRace.result, Is.Not.Null,
                "the finished result keeps retrySave mounted");
            Assert.That(kept.screen, Is.EqualTo("driving"));

            var continued = service.Execute(Cmd("results.continue", null));
            Assert.That(continued.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(continued.code, Is.EqualTo("rewardUnresolved"));
            Assert.That(service.Snapshot().soloRace.result,
                Is.Not.Null);
            Assert.That(service.Snapshot().screen,
                Is.EqualTo("driving"),
                "continue may not leave the driving screen while gated");

            // The pause-overlay restart routes through SoloRestart —
            // the same gate must fire before it could end the session.
            var restart = service.Execute(Cmd("race.restart", null));
            Assert.That(restart.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(restart.code, Is.EqualTo("rewardUnresolved"));
            Assert.That(service.Snapshot().soloRace.raceId,
                Is.EqualTo(raceId),
                "restart must not retire the session");

            // Resolve the debt: a still-failing store reports the honest
            // save failure, then the committed retry frees the loop.
            var stillFailing = service.Execute(
                Cmd("results.retrySave", null));
            Assert.That(stillFailing.status,
                Is.EqualTo(CommandResult.Failed));
            Assert.That(stillFailing.code, Is.EqualTo("saveFailed"));
            toggling.failing = false;
            var saved = service.Execute(Cmd("results.retrySave", null));
            Assert.That(saved.status, Is.EqualTo(CommandResult.Applied),
                "retrySave failed: " + saved.code);
            Assert.That(service.RewardUnresolved, Is.False);
            Assert.That(Availability(service, "results.retry").available,
                Is.True, "replay re-advertises once the gate clears");
            var retried = service.Execute(Cmd("results.retry", null));
            Assert.That(retried.status, Is.EqualTo(CommandResult.Applied),
                "results.retry after the save resolves: " + retried.code);
            Assert.That(service.Snapshot().soloRace.raceId,
                Is.Not.EqualTo(raceId),
                "the replay mints a fresh race identity");
        }

        // Task 10 review fix (parity tail): race.exit stays the
        // deliberate escape valve — the debt survives the session and
        // keeps gating progression writes, which availability must now
        // advertise with the same reason the handlers reject with.
        [Test]
        public void UnresolvedSaveStillGatesStartsAfterExit()
        {
            var toggling = new TogglingSerializer();
            var store = new ProfileStore(directory, toggling);
            var service = ServiceWithProfile(Catalog(), store,
                CampaignTestFixture.Profile());
            Assert.That(service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-01", "difficulty", "easy"))).status,
                Is.EqualTo(CommandResult.Applied));
            toggling.failing = true;
            var finished = StepUntilResult(service, 9000);
            Assert.That(finished.result, Is.Not.Null);
            Assert.That(service.RewardUnresolved, Is.True);

            Assert.That(service.Execute(Cmd("race.exit", null)).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(service.RewardUnresolved, Is.True,
                "exit abandons the session, never the debt");
            Assert.That(service.Snapshot().progression.rewardUnresolved,
                Is.True);

            foreach (var action in new[] { "campaign.start",
                "quickRace.start", "upgrade.purchase" })
            {
                var availability = Availability(service, action);
                Assert.That(availability.available, Is.False,
                    action + " must advertise the gate after exit");
                Assert.That(availability.reason,
                    Is.EqualTo("rewardUnresolved"),
                    action + " availability reason");
            }
            // The handlers allow these while the debt is pending —
            // availability must not over-gate.
            Assert.That(Availability(service, "timeTrial.start").available,
                Is.True, "a trial binds no progression context");
            Assert.That(Availability(service, "cosmetic.equip").available,
                Is.True, "equips are not progression transactions");
            Assert.That(
                Availability(service, "results.retrySave").available,
                Is.True);

            var blockedStart = service.Execute(Cmd("campaign.start",
                P("eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01", "difficulty", "easy")));
            Assert.That(blockedStart.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(blockedStart.code,
                Is.EqualTo("rewardUnresolved"));

            toggling.failing = false;
            var saved = service.Execute(Cmd("results.retrySave", null));
            Assert.That(saved.status, Is.EqualTo(CommandResult.Applied),
                "retrySave failed: " + saved.code);
            var reattempt = service.Execute(Cmd("quickRace.start",
                P("courseId", "harbor-hop", "classId", "car",
                    "vehicleId", "car-01", "difficulty", "easy")));
            Assert.That(reattempt.status,
                Is.EqualTo(CommandResult.Applied),
                "a resolved gate frees progression starts: "
                    + reattempt.code);
        }


        [Test]
        public void NoLoopCommandGrantsCoinsOrSkipsTraversal()
        {
            var service = ServiceWithProfile(Catalog());
            // Every advertised action exercised with an empty parameter
            // set: none may mutate the wallet or mark progress.
            foreach (var descriptor in service.Discover())
            {
                var command = service.Execute(Cmd(descriptor.action,
                    new Dictionary<string, string>()));
                var state = service.Snapshot();
                Assert.That(state.savedProfile.coins, Is.EqualTo(0),
                    descriptor.action + " granted coins");
                Assert.That(state.savedProfile.eventProgress.Count,
                    Is.EqualTo(0),
                    descriptor.action + " marked progress");
            }
            // Even a completed purchase is an authored transaction with
            // an exact debit — never a grant.
            Assert.That(service.Snapshot().savedProfile.coins,
                Is.EqualTo(0));
        }

        [Test]
        public void ProgressionSliceIsDeepCopied()
        {
            var service = ServiceWithProfile(Catalog());
            Assert.That(service.Execute(Cmd("campaign.open", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var snapshot = service.Snapshot();
            snapshot.progression.campaignWorldId = "tampered";
            snapshot.progression.campaignEvents[0].stars = 3;
            snapshot.progression.campaignEvents[0].locked = true;
            snapshot.progression.campaignEvents.Clear();
            snapshot.progression.bounties.Clear();
            snapshot.progression.rewardUnresolved = true;
            var fresh = service.Snapshot();
            Assert.That(fresh.progression.campaignWorldId,
                Is.EqualTo("sunny-island"));
            Assert.That(fresh.progression.campaignEvents.Count,
                Is.EqualTo(26));
            Assert.That(fresh.progression.campaignEvents[0].stars,
                Is.EqualTo(0));
            Assert.That(fresh.progression.campaignEvents[0].locked,
                Is.False);
            Assert.That(fresh.progression.bounties.Count, Is.EqualTo(3));
            Assert.That(fresh.progression.rewardUnresolved, Is.False);
        }

        [Test]
        public void FirstSnapshotCarriesTheProgressionSlice()
        {
            // Menus and the empty-capture start must carry the slice too —
            // a consumer reconstructing from record 1 sees honest
            // availability, never a missing key.
            var service = new FoundationService(Store(), "en", RunId);
            var state = service.Snapshot();
            Assert.That(state.stateSchemaVersion,
                Is.EqualTo(NetworkSettings.StateSchemaVersion));
            Assert.That(state.progression, Is.Not.Null);
            Assert.That(state.progression.available, Is.False,
                "no content bound yet — honest unavailability");
            Assert.That(state.progression.campaignEvents, Is.Not.Null);
            Assert.That(state.progression.bounties, Is.Not.Null);
        }

        // --- LAN admission fingerprint ------------------------------------

        [Test]
        public void LanAdmissionBindsTheGameplayFingerprint()
        {
            // With a non-legacy gameplay catalog bound, the admission
            // fingerprint is the gameplay catalog's — two peers with the
            // same driving content but different economy/campaign rules
            // can never share a race.
            var link = new LanSessionTests.FakeLanLink();
            var probe = new LanSessionTests.FakeLanProbe();
            var host = new LanSessionService(link, probe, "gameplay-3");
            host.SetLocalContext("host-p", "fox", 0, true, true);
            host.SetContent(LanSessionTests.LanCatalog());
            var catalog = Catalog();
            host.SetGameplayContent(catalog);
            Assert.That(host.Execute(LanSessionTests.Cmd("lan.host",
                LanSessionTests.P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));

            // The advertised compat token binds the gameplay fingerprint.
            Assert.That(probe.LastCompat, Does.Contain(
                catalog.CompatibilityFingerprint));

            // A peer still offering the driving-only fingerprint is an
            // incompatible peer — refused before any seat is allocated.
            host.OnLinkConnected(9);
            host.OnLinkMessage(9, LanSessionTests.JoinRequest("p-1",
                gameplayVersion: "gameplay-3",
                fingerprint: LanSessionTests.LanCatalog()
                    .CompatibilityFingerprint));
            var rejects = link.DrainSent();
            Assert.That(rejects.Count, Is.EqualTo(1));
            string type; Dictionary<string, string> fields;
            Assert.That(NetworkProtocol.TryDecode(rejects[0], out type,
                out fields), Is.True);
            Assert.That(type, Is.EqualTo(NetworkProtocol.MsgJoinReject));
            Assert.That(fields["code"], Is.EqualTo("incompatible"));
            Assert.That(host.Snapshot().participants[1].occupied,
                Is.False, "a rejected peer must never hold a seat");

            // The gameplay fingerprint is admitted.
            host.OnLinkConnected(10);
            host.OnLinkMessage(10, LanSessionTests.JoinRequest("p-2",
                gameplayVersion: "gameplay-3",
                fingerprint: catalog.CompatibilityFingerprint));
            var replies = link.DrainSent();
            Assert.That(NetworkProtocol.TryDecode(replies[0], out type,
                out fields), Is.True);
            Assert.That(type, Is.EqualTo(NetworkProtocol.MsgJoinAccept));
        }

        [Test]
        public void LanLegacyCatalogFallsBackToDrivingFingerprint()
        {
            // A legacy gameplay catalog authors no rules — the driving
            // fingerprint stays the admission token so unupgraded peers
            // remain interoperable.
            var link = new LanSessionTests.FakeLanLink();
            var probe = new LanSessionTests.FakeLanProbe();
            var host = new LanSessionService(link, probe, "gameplay-3");
            host.SetLocalContext("host-p", "fox", 0, true, true);
            host.SetContent(LanSessionTests.LanCatalog());
            host.SetGameplayContent(GameplayCatalog.CreateLegacy(
                LanSessionTests.LanCatalog()));
            Assert.That(host.Execute(LanSessionTests.Cmd("lan.host",
                LanSessionTests.P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            host.OnLinkConnected(9);
            host.OnLinkMessage(9, LanSessionTests.JoinRequest("p-1",
                gameplayVersion: "gameplay-3",
                fingerprint: LanSessionTests.LanCatalog()
                    .CompatibilityFingerprint));
            var replies = link.DrainSent();
            string type; Dictionary<string, string> fields;
            Assert.That(NetworkProtocol.TryDecode(replies[0], out type,
                out fields), Is.True);
            Assert.That(type, Is.EqualTo(NetworkProtocol.MsgJoinAccept),
                "legacy catalogs keep driving-fingerprint admission");
        }

        [Test]
        public void Protocol3FencesLegacyPeerBeforeSeatAllocation()
        {
            var link = new LanSessionTests.FakeLanLink();
            var host = LanSessionTests.StandaloneHost(link,
                new LanSessionTests.FakeLanProbe());
            host.OnLinkConnected(9);
            host.OnLinkMessage(9, LanSessionTests.JoinRequest("p-1",
                protocolVersion: "2"));
            var replies = link.DrainSent();
            Assert.That(replies.Count, Is.EqualTo(1));
            string type; Dictionary<string, string> fields;
            Assert.That(NetworkProtocol.TryDecode(replies[0], out type,
                out fields), Is.True);
            Assert.That(type, Is.EqualTo(NetworkProtocol.MsgJoinReject));
            Assert.That(fields["code"], Is.EqualTo("incompatible"));
            Assert.That(host.Snapshot().participants[1].occupied,
                Is.False);
        }

        // --- fixtures ------------------------------------------------------

        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose,
                float probeDistance)
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

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile)
            {
                return UnityEngine.JsonUtility.ToJson(profile);
            }
            public ProfileSave Deserialize(string json)
            {
                return UnityEngine.JsonUtility.FromJson<ProfileSave>(json);
            }
#else
            private static readonly JsonSerializerOptions Options =
                new JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile)
            {
                return JsonSerializer.Serialize(profile, Options);
            }
            public ProfileSave Deserialize(string json)
            {
                return JsonSerializer.Deserialize<ProfileSave>(json, Options);
            }
#endif
        }

        private sealed class TogglingSerializer : IProfileSerializer
        {
            internal bool failing;
            private readonly TestJsonSerializer inner =
                new TestJsonSerializer();
            public string Serialize(ProfileSave profile)
            {
                if (failing) throw new InvalidOperationException("down");
                return inner.Serialize(profile);
            }
            public ProfileSave Deserialize(string json)
            {
                return inner.Deserialize(json);
            }
        }
    }
}
