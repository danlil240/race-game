using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // M5 Task 8: the shared mode-eligibility rule. One CanEnter verdict
    // backs the garage/discovery pickers, the service's own CanEnter
    // seam and the raw start commands — same course/event/vehicle/
    // profile facts, same stable reason codes everywhere. LAN exposes
    // every implemented vehicle tier (normalized fairness) while
    // Campaign/Quick Race seat only owned vehicles.
    public sealed class ModeEligibilityTests
    {
        private const string RunId = "mode-eligibility";
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-mode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        private static GameplayCatalog Catalog()
        {
            return CampaignTestFixture.Catalog();
        }

        private FoundationService ServiceWithProfile(ProfileSave seeded,
            GameplayCatalog catalog)
        {
            var store = Store();
            if (seeded != null) store.Save(seeded);
            var service = new FoundationService(store, "en", RunId);
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.select",
                P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Applied));
            return service;
        }

        // --- the shared verdict ------------------------------------------------

        [Test]
        public void LockedVehiclesRejectInCampaignAndQuickRace()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            foreach (var mode in new[] { RaceMode.Campaign, RaceMode.QuickRace })
            {
                // car-02 is a reward vehicle — the factory profile does
                // not own it, so both progression modes refuse.
                var locked = RaceModeRules.CanEnter(mode, "",
                    "harbor-hop-car-circuit", "car-02", profile, catalog);
                Assert.That(locked.allowed, Is.False, "mode " + mode);
                Assert.That(locked.code, Is.EqualTo("vehicleLocked"),
                    "mode " + mode);

                // The owned tier-1 car enters the same event freely.
                var owned = RaceModeRules.CanEnter(mode, "",
                    "harbor-hop-car-circuit", "car-01", profile, catalog);
                Assert.That(owned.allowed, Is.True,
                    "mode " + mode + ": " + owned.code);
                Assert.That(owned.vehicleId, Is.EqualTo("car-01"));
            }
        }

        [Test]
        public void ButtonsAndRawCommandsShareTheSameVerdict()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            var service = ServiceWithProfile(profile, catalog);
            foreach (var mode in new[] { "campaign", "quickRace" })
            {
                // The UI path: the same verdict the pickers show before
                // the button is ever pressed.
                var parsed = mode == "campaign"
                    ? RaceMode.Campaign : RaceMode.QuickRace;
                var verdict = service.CanEnter(parsed, "",
                    "harbor-hop-car-circuit", "car-02");
                Assert.That(verdict.allowed, Is.False, "ui " + mode);
                Assert.That(verdict.code, Is.EqualTo("vehicleLocked"),
                    "ui " + mode);

                // The raw command path: solo.start surfaces the same
                // stable reason code.
                var rejected = service.Execute(Cmd("solo.start",
                    P("mode", mode,
                        "eventId", "harbor-hop-car-circuit",
                        "vehicleId", "car-02")));
                Assert.That(rejected.status,
                    Is.EqualTo(CommandResult.Rejected), "cmd " + mode);
                Assert.That(rejected.code, Is.EqualTo("vehicleLocked"),
                    "cmd " + mode);
            }
        }

        [Test]
        public void CampaignRequiresACampaignEventAndMatchingClass()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            // The LAN-only event is not part of the campaign.
            var decision = RaceModeRules.CanEnter(RaceMode.Campaign, "",
                "lan-mixed", "car-01", profile, catalog);
            // CampaignTestFixture has no untyped LAN event — any event
            // that exists but is not in the campaign refuses the same
            // way a lan-only event would.
            if (catalog.Driving.Events.ContainsKey("lan-mixed"))
            {
                Assert.That(decision.allowed, Is.False);
                Assert.That(decision.code, Is.EqualTo("eventNotCampaign"));
            }
            else
            {
                Assert.That(decision.allowed, Is.False);
                Assert.That(decision.code, Is.EqualTo("unknownEvent"));
            }
            // A quick race seats any known event — the same lookup that
            // rejected campaign accepts here.
            var quick = RaceModeRules.CanEnter(RaceMode.QuickRace, "",
                "harbor-hop-car-circuit", "car-01", profile, catalog);
            Assert.That(quick.allowed, Is.True);
            // A motorcycle in the car-only event is refused on event
            // membership, not on unlock state.
            var wrongClass = RaceModeRules.CanEnter(RaceMode.QuickRace, "",
                "harbor-hop-car-circuit", "motorcycle-01", profile,
                catalog);
            Assert.That(wrongClass.allowed, Is.False);
            Assert.That(wrongClass.code,
                Is.EqualTo("vehicleNotForEvent"));
            // An unknown event/vehicle fails honestly.
            Assert.That(RaceModeRules.CanEnter(RaceMode.QuickRace, "",
                    "nope", "car-01", profile, catalog).code,
                Is.EqualTo("unknownEvent"));
            Assert.That(RaceModeRules.CanEnter(RaceMode.QuickRace, "",
                    "harbor-hop-car-circuit", "car-99", profile,
                    catalog).code,
                Is.EqualTo("unknownVehicle"));
        }

        [Test]
        public void MissingProfileCannotEnterProgressionModes()
        {
            var catalog = Catalog();
            var decision = RaceModeRules.CanEnter(RaceMode.Campaign, "",
                "harbor-hop-car-circuit", "car-01", null, catalog);
            Assert.That(decision.allowed, Is.False);
            Assert.That(decision.code,
                Is.EqualTo("requiresSelectedProfile"));
            // No catalog at all is its own honest verdict.
            var noContent = RaceModeRules.CanEnter(RaceMode.Campaign, "",
                "harbor-hop-car-circuit", "car-01",
                CampaignTestFixture.Profile(), (GameplayCatalog)null);
            Assert.That(noContent.allowed, Is.False);
            Assert.That(noContent.code,
                Is.EqualTo("contentUnavailable"));
        }

        // --- LAN normalized access --------------------------------------------

        [Test]
        public void LanExposesEveryImplementedTierRegardlessOfUnlocks()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            // The shared verdict: both car tiers enter a LAN event even
            // though the profile only owns car-01 — LAN never consults
            // profile unlocks.
            Assert.That(RaceModeRules.CanEnter(RaceMode.Lan, "",
                    "harbor-hop-car-circuit", "car-01", profile,
                    catalog).allowed, Is.True);
            var tierTwo = RaceModeRules.CanEnter(RaceMode.Lan, "",
                "harbor-hop-car-circuit", "car-02", profile, catalog);
            Assert.That(tierTwo.allowed, Is.True,
                "locked tier-2 must still enter LAN: " + tierTwo.code);

            // The lobby surface proves the same thing through its own
            // command: configure the car class, then seat car-02.
            var link = new LanSessionTests.FakeLanLink();
            var lan = new LanSessionService(link,
                new LanSessionTests.FakeLanProbe(), "gameplay-test");
            lan.SetLocalContext(profile.id, "fox", 0, true, true);
            lan.SetContent(catalog.Driving);
            Assert.That(lan.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.configure",
                P("eventId", "harbor-hop-car-circuit",
                    "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            var seated = lan.Execute(Cmd("lan.vehicle",
                P("vehicleId", "car-02")));
            Assert.That(seated.status, Is.EqualTo(CommandResult.Applied),
                "LAN vehicle pick must expose the locked tier: "
                + seated.code);
        }

        // --- normalized tuning ---------------------------------------------------

        [Test]
        public void LanAndTimeTrialTuneEqualsBaselineForMaxedProfile()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = CampaignTestFixture.Profile();
            profile.coins = 2000;
            for (var level = 1; level <= 3; level++)
            {
                UpgradeService.Purchase("p-a" + level, "car-01",
                    "acceleration", level, profile, store, catalog);
                UpgradeService.Purchase("p-h" + level, "car-01",
                    "handling", level, profile, store, catalog);
                UpgradeService.Purchase("p-b" + level, "car-01",
                    "boost", level, profile, store, catalog);
            }
            var baseline = catalog.Driving.Vehicles["car-01"];
            foreach (var mode in new[] { RaceMode.Lan, RaceMode.TimeTrial })
            {
                var resolved = VehicleTuningResolver.Resolve(mode,
                    "car-01", profile, catalog);
                Assert.That(resolved, Is.Not.Null);
                // Field-exact baseline equality: the fixture categories
                // all target `acceleration`, so a non-normalized resolve
                // would read 6f*1.45 for the combined levels.
                Assert.That(resolved.acceleration,
                    Is.EqualTo(baseline.acceleration), "mode " + mode);
                Assert.That(resolved.maxSpeed,
                    Is.EqualTo(baseline.maxSpeed), "mode " + mode);
                Assert.That(resolved, Is.Not.SameAs(baseline));
            }
            // Control: campaign applies the levels.
            var campaign = VehicleTuningResolver.Resolve(
                RaceMode.Campaign, "car-01", profile, catalog);
            Assert.That(campaign.acceleration,
                Is.GreaterThan(baseline.acceleration));
        }

        [Test]
        public void TimeTrialResolvesItsNormalizedStarter()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            // The authored time event's first class-legal vehicle is the
            // starter — explicit or implicit, the answer is the same.
            var implicitPick = RaceModeRules.CanEnter(RaceMode.TimeTrial,
                "harbor-hop", "", "car-01", profile, catalog);
            Assert.That(implicitPick.allowed, Is.True,
                implicitPick.code);
            Assert.That(implicitPick.vehicleId, Is.EqualTo("car-01"));
            var explicitPick = RaceModeRules.CanEnter(RaceMode.TimeTrial,
                "", "harbor-hop-car-time", "car-01", profile, catalog);
            Assert.That(explicitPick.allowed, Is.True,
                explicitPick.code);
            // A non-starter vehicle is refused honestly — the trial
            // normalizes instead of substituting.
            var nonStarter = RaceModeRules.CanEnter(RaceMode.TimeTrial,
                "", "harbor-hop-car-time", "car-02", profile, catalog);
            Assert.That(nonStarter.allowed, Is.False);
            Assert.That(nonStarter.code, Is.EqualTo("invalidVehicle"));
            // A course/class pair without a time event fails honestly.
            var none = RaceModeRules.CanEnter(RaceMode.TimeTrial,
                "harbor-hop", "", "car-99", profile, catalog);
            Assert.That(none.allowed, Is.False);
        }

        // --- service integration ---------------------------------------------------

        [Test]
        public void SoloSessionSeatsThePurchasedTune()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = CampaignTestFixture.Profile();
            profile.coins = 2000;
            for (var level = 1; level <= 3; level++)
                UpgradeService.Purchase("p-a" + level, "car-01",
                    "acceleration", level, profile, store, catalog);
            var baseline = catalog.Driving.Vehicles["car-01"];
            var request = new SoloRaceRequest
            {
                mode = "campaign",
                eventId = "harbor-hop-car-circuit",
                vehicleId = "car-01",
                participantId = profile.id,
                raceId = "race-tuned",
                commandId = "cmd-tuned"
            };
            Assert.That(SoloRaceSession.TryCreate(request, profile,
                    catalog, out var session, out var error), Is.True,
                "solo session creation failed: " + error);
            var spawns = session.VehicleSpawns();
            Assert.That(spawns.Count,
                Is.EqualTo(NetworkSettings.ParticipantCapacity));
            // Seat 0 is the local human: its spawn carries the resolved
            // purchased tune (three acceleration levels on the fixture
            // categories → provably above baseline); the AI seats keep
            // the class baseline — purchases are never the field's.
            var human = spawns[0];
            Assert.That(human.tuning, Is.Not.SameAs(baseline));
            Assert.That(human.tuning.acceleration,
                Is.GreaterThan(baseline.acceleration));
            for (var i = 1; i < spawns.Count; i++)
            {
                var aiBaseline = catalog.Driving.Vehicles[
                    spawns[i].tuning.vehicleId];
                Assert.That(spawns[i].tuning.acceleration,
                    Is.EqualTo(aiBaseline.acceleration),
                    "AI seat " + i + " must keep the baseline");
            }
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, RunId,
                Guid.NewGuid().ToString("N"), "local", action,
                parameters);
        }

        private static Dictionary<string, string> P(
            params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile)
            {
                return JsonUtility.ToJson(profile);
            }
            public ProfileSave Deserialize(string json)
            {
                return Newtonsoft.Json.JsonConvert
                    .DeserializeObject<ProfileSave>(json);
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
                return JsonSerializer.Deserialize<ProfileSave>(json,
                    Options);
            }
#endif
        }
    }
}
