using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class PlayfulChaosTests
    {
        internal static GameplayCatalog QuickCatalog()
        {
            var full = SoloRaceTests.Catalog();
            Assert.That(GameplayCatalog.TryCreateQuickRace(full.Driving,
                full.Gadgets, full.Difficulties, out var quick, out var error),
                Is.True, error);
            return quick;
        }

        [Test]
        public void QuickCatalogSeatsFourWithoutCampaignOrEconomy()
        {
            var catalog = QuickCatalog();
            Assert.That(catalog.QuickRaceOnly, Is.True);
            Assert.That(catalog.Campaigns, Is.Empty);
            Assert.That(catalog.Economy, Is.Null);
            var request = new SoloRaceRequest
            {
                mode = "quickRace", courseId = "harbor-hop", classId = "car",
                vehicleId = "car-01", difficultyId = "easy", raceId = "fun-test"
            };
            Assert.That(SoloRaceSession.TryCreate(request,
                ProfileFactory.Create(0, "en"), catalog, out var race,
                out var error), Is.True, error);
            Assert.That(race.Snapshot().race.racers.Count, Is.EqualTo(4));
            Assert.That(race.Snapshot().tuningSource, Is.EqualTo("normalized"));
        }

        [Test]
        public void QuickCatalogRejectsInvalidGadgetsAndDuplicateDifficulty()
        {
            var full = SoloRaceTests.Catalog();
            Assert.That(GameplayCatalog.TryCreateQuickRace(full.Driving,
                null, full.Difficulties, out var rejected, out _), Is.False);
            Assert.That(rejected, Is.Null);
            Assert.That(GameplayCatalog.TryCreateQuickRace(full.Driving,
                full.Gadgets, new[] { full.Difficulties[0], full.Difficulties[0] },
                out rejected, out _), Is.False);
            Assert.That(rejected, Is.Null);
        }

        [Test]
        public void QuickRaceCapabilityRejectsCampaignAndTrialEntry()
        {
            var catalog = QuickCatalog();
            var profile = ProfileFactory.Create(0, "en");
            foreach (var mode in new[] { RaceMode.Campaign, RaceMode.TimeTrial })
                Assert.That(RaceModeRules.CanEnter(mode, "harbor-hop",
                    "harbor-hop-car-time", "car-01", profile, catalog).code,
                    Is.EqualTo("contentUnavailable"));
        }

        [TestCase("course")]
        [TestCase("vehicle")]
        public void QuickCatalogRevalidatesEditedEventReferences(string brokenReference)
        {
            var full = SoloRaceTests.Catalog();
            var evt = full.Driving.Events["harbor-hop-car-circuit"];
            if (brokenReference == "course") evt.courseId = "missing-course";
            else evt.vehicleIds.Add("car-missing");
            Assert.That(GameplayCatalog.TryCreateQuickRace(full.Driving,
                full.Gadgets, full.Difficulties, out var rejected, out _), Is.False);
            Assert.That(rejected, Is.Null);
        }

        [Test]
        public void QuickCommandsDoNotBindRewardsOrExposeProgression()
        {
            var directory = Path.Combine(Path.GetTempPath(), "fun-race-" + Guid.NewGuid());
            try
            {
                var store = new ProfileStore(directory, new TestJsonSerializer());
                store.Save(ProfileFactory.Create(0, "en"));
                var service = new FoundationService(store, "en", "fun-run");
                var catalog = QuickCatalog();
                service.SetDrivingContent(catalog.Driving);
                service.SetGameplayCatalog(catalog);
                CommandResult Send(string action, Dictionary<string, string> args = null)
                    => service.Execute(new GameCommand(1, "fun-run", Guid.NewGuid().ToString(),
                        "local", action, args ?? new Dictionary<string, string>()));
                Assert.That(Send("profile.select", new Dictionary<string, string>
                    { { "slot", "0" } }).status, Is.EqualTo("applied"));
                foreach (var action in new[] { "campaign.open", "campaign.selectWorld",
                    "timeTrial.open", "garage.open", "bounties.open" })
                {
                    Assert.That(Send(action, action == "campaign.selectWorld"
                        ? new Dictionary<string, string> { { "worldId", "sunny-island" } }
                        : null).code, Is.EqualTo("contentUnavailable"), action);
                    foreach (var descriptor in service.Discover())
                        if (descriptor.action == action)
                            Assert.That(descriptor.reason, Is.EqualTo("contentUnavailable"), action);
                }
                Assert.That(Send("quickRace.start", new Dictionary<string, string>
                {
                    { "courseId", "harbor-hop" }, { "classId", "car" },
                    { "vehicleId", "car-01" }, { "difficulty", "easy" }
                }).status, Is.EqualTo("applied"));
                Assert.That(service.Snapshot().savedProfile.pendingRaceContexts, Is.Empty);
                Assert.That(service.Snapshot().progression.available, Is.False);
                Assert.That(Send("race.restart").status, Is.EqualTo("applied"));
                Assert.That(service.Snapshot().savedProfile.pendingRaceContexts, Is.Empty);
                var world = new FlatWorld();
                for (var round = 0; round < 3; round++)
                {
                    var driver = new AiDriver();
                    var raceId = service.Snapshot().soloRace.raceId;
                    for (var tick = 0; tick < 15000 && service.Snapshot().soloRace.result == null; tick++)
                    {
                        if (tick % 5 == 0)
                        {
                            var human = service.Snapshot().soloRace.race.racers[0];
                            var input = driver.Sample(human.session, catalog.Driving.Courses["harbor-hop"]);
                            Assert.That(Send("input.set", new Dictionary<string, string>
                            {
                                { "steering", input.Steering.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                { "drift", input.Drift ? "true" : "false" },
                                { "boost", input.Boost ? "true" : "false" },
                                { "recover", input.Recover ? "true" : "false" },
                                { "gadget", "false" }, { "changeCamera", "false" }, { "pause", "false" },
                                { "durationTicks", "5" }
                            }).status, Is.EqualTo("applied"));
                        }
                        service.StepSolo(world);
                        service.AdvanceTick();
                    }
                    Assert.That(service.Snapshot().soloRace.result, Is.Not.Null, "round " + round);
                    Assert.That(Send("results.retry").status, Is.EqualTo("applied"));
                    Assert.That(service.Snapshot().soloRace.raceId, Is.Not.EqualTo(raceId));
                    Assert.That(service.Snapshot().savedProfile.appliedResults, Is.Empty);
                }
                Assert.That(store.Load(0).Profile.coins, Is.EqualTo(0));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private sealed class TestJsonSerializer : IProfileSerializer
        {
            public string Serialize(ProfileSave profile)
            {
#if UNITY_5_3_OR_NEWER
                return UnityEngine.JsonUtility.ToJson(profile);
#else
                return System.Text.Json.JsonSerializer.Serialize(profile,
                    new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
#endif
            }
            public ProfileSave Deserialize(string json)
            {
#if UNITY_5_3_OR_NEWER
                return UnityEngine.JsonUtility.FromJson<ProfileSave>(json);
#else
                return System.Text.Json.JsonSerializer.Deserialize<ProfileSave>(json,
                    new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
#endif
            }
        }

        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
                => new GroundHit { hit = pose.y <= probeDistance, x = pose.x,
                    y = 0f, z = pose.z, normalY = 1f, distance = pose.y, surfaceId = "ground" };
            public SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
                float halfWidth, float halfLength, float height)
                => new SweepHit { hit = false, fraction = 1f };
        }
    }
}
