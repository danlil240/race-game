using System.Collections.Generic;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M6 Task 2: the ordered adventure view. World one is open on a
    // fresh profile; a world opens when the immediately previous world
    // is complete (both finales — wins optional); completed worlds stay
    // open; every world's regional gate counts ONLY its own events.
    // Pure evaluation: nothing here mutates the profile.
    public sealed class AdventureRulesTests
    {
        private static GameplayCatalog Catalog()
        {
            return CampaignTestFixture.MultiCatalog();
        }

        private static List<bool> Availability(AdventureAccess access)
        {
            var flags = new List<bool>();
            foreach (var world in access.worlds)
                flags.Add(world != null && world.available);
            return flags;
        }

        private static WorldAccess World(AdventureAccess access,
            string worldId)
        {
            var world = AdventureRules.FindWorld(access, worldId);
            Assert.That(world, Is.Not.Null,
                "the adventure must report world " + worldId);
            return world;
        }

        // --- world availability -------------------------------------------

        [Test]
        public void FreshProfileOpensOnlyTheFirstWorld()
        {
            var catalog = Catalog();
            var access = AdventureRules.Evaluate(catalog,
                CampaignTestFixture.Profile());

            Assert.That(access.worlds.Count, Is.EqualTo(3));
            Assert.That(access.worlds[0].worldId,
                Is.EqualTo("sunny-island"));
            Assert.That(access.worlds[1].worldId,
                Is.EqualTo("redrock-canyon"));
            Assert.That(access.worlds[2].worldId,
                Is.EqualTo("snowy-peaks"));
            Assert.That(Availability(access),
                Is.EqualTo(new[] { true, false, false }),
                "only Sunny Island is open on a fresh profile");
            Assert.That(access.worlds[0].lockedReason, Is.EqualTo(""));
            Assert.That(access.worlds[1].lockedReason,
                Is.EqualTo(AdventureRules.LockedWorldReason));
            Assert.That(access.worlds[2].lockedReason,
                Is.EqualTo(AdventureRules.LockedWorldReason));
            Assert.That(access.complete, Is.False);

            // The open world exposes all 24 regular events immediately
            // and keeps its finales behind the regional gate.
            var sunny = access.worlds[0].campaign;
            Assert.That(sunny.availableEventIds, Is.EqualTo(
                CampaignTestFixture.RegularEventsFor("sunny-island")));
            Assert.That(sunny.finalesAvailable, Is.False);
            Assert.That(sunny.availableFinaleIds, Is.Empty);
            Assert.That(sunny.requiredEventCompletions, Is.EqualTo(24));
            Assert.That(sunny.requiredRegionalStars, Is.EqualTo(30));
        }

        [Test]
        public void RegionalGateBoundariesStayClosed()
        {
            var catalog = Catalog();

            // 24 completions but only 29 regional stars: one star short.
            var shortStars = CampaignTestFixture.Profile();
            CampaignTestFixture.CompleteWorldEvents(shortStars,
                "sunny-island", 24, 5);
            var access = AdventureRules.Evaluate(catalog, shortStars);
            var sunny = access.worlds[0].campaign;
            Assert.That(sunny.completedEvents, Is.EqualTo(24));
            Assert.That(sunny.regionalStars, Is.EqualTo(29));
            Assert.That(sunny.finalesAvailable, Is.False,
                "24 completions + 29 stars stays gated");
            Assert.That(sunny.availableFinaleIds, Is.Empty);

            // 30 stars but only 23 completions: the completion count
            // must still reach every authored regular event.
            var shortEvents = CampaignTestFixture.Profile();
            CampaignTestFixture.CompleteWorldEvents(shortEvents,
                "sunny-island", 23, 7);
            access = AdventureRules.Evaluate(catalog, shortEvents);
            sunny = access.worlds[0].campaign;
            Assert.That(sunny.completedEvents, Is.EqualTo(23));
            Assert.That(sunny.regionalStars, Is.EqualTo(30));
            Assert.That(sunny.finalesAvailable, Is.False,
                "23 completions + 30 stars stays gated");
        }

        [Test]
        public void OpenGateExposesOnlyTheOwningWorldsFinales()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            CampaignTestFixture.CompleteWorldEvents(profile,
                "sunny-island", 24, 6); // 24 completions + 30 stars

            var access = AdventureRules.Evaluate(catalog, profile);

            var sunny = access.worlds[0].campaign;
            Assert.That(sunny.finalesAvailable, Is.True);
            Assert.That(sunny.availableFinaleIds, Is.EquivalentTo(new[]
            {
                "sunny-island-car-finale",
                "sunny-island-motorcycle-finale"
            }));
            Assert.That(sunny.availableEventIds,
                Does.Contain("sunny-island-car-finale"));

            // The open gate touches nothing outside Sunny Island: the
            // redrock gate still needs ITS OWN 24 completions and 30
            // regional stars, and the world itself stays closed until
            // sunny is complete (finales, not just the gate).
            var redrock = World(access, "redrock-canyon");
            Assert.That(redrock.available, Is.False,
                "an open gate is not world completion — redrock stays "
                + "locked until both sunny finales are done");
            Assert.That(redrock.campaign.finalesAvailable, Is.False);
            Assert.That(redrock.campaign.availableFinaleIds, Is.Empty);
            Assert.That(access.complete, Is.False);
        }

        [Test]
        public void CompletingBothFinalesWithoutWinsOpensTheNextWorld()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            CampaignTestFixture.CompleteWorldEvents(profile,
                "sunny-island", 24, 6);
            CampaignTestFixture.CompleteWorldFinales(profile,
                "sunny-island", victorious: false);

            var access = AdventureRules.Evaluate(catalog, profile);

            Assert.That(access.worlds[0].campaign.worldComplete, Is.True,
                "both finales completed — wins are never required");
            Assert.That(access.worlds[0].campaign.victoriousFinaleIds,
                Is.Empty);
            Assert.That(Availability(access),
                Is.EqualTo(new[] { true, true, false }),
                "redrock opens; snowy still waits on redrock");
            Assert.That(access.complete, Is.False);

            // Completing redrock (still without wins) opens snowy; the
            // completed worlds never relock.
            CampaignTestFixture.CompleteWorldFinales(profile,
                "redrock-canyon", victorious: false);
            access = AdventureRules.Evaluate(catalog, profile);
            Assert.That(Availability(access),
                Is.EqualTo(new[] { true, true, true }));
            Assert.That(access.complete, Is.False);
        }

        [Test]
        public void FinalWorldCompletionIsAdventureComplete()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            foreach (var worldId in CampaignTestFixture.WorldIds)
            {
                CampaignTestFixture.CompleteWorldEvents(profile, worldId,
                    24, 6);
                CampaignTestFixture.CompleteWorldFinales(profile, worldId,
                    victorious: false);
            }

            var access = AdventureRules.Evaluate(catalog, profile);

            Assert.That(access.complete, Is.True,
                "every world's finales completed ends the adventure");
            Assert.That(Availability(access),
                Is.EqualTo(new[] { true, true, true }),
                "completion never relocks a world");
            // Replay stays legal: every event row stays enterable.
            foreach (var world in access.worlds)
                Assert.That(world.campaign.availableEventIds.Count,
                    Is.EqualTo(26), world.worldId + " keeps 24 + 2 rows");
        }

        // --- regional isolation --------------------------------------------

        [Test]
        public void CrossWorldStarsAndCompletionsNeverCount()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            // Sunny: 23 completions + 7 extra bits = 30 regional stars,
            // one event short of the completion gate. Redrock: ALL 24
            // events at full marks = 72 stars of foreign credit.
            CampaignTestFixture.CompleteWorldEvents(profile,
                "sunny-island", 23, 7);
            CampaignTestFixture.CompleteWorldEvents(profile,
                "redrock-canyon", 24, 48);

            var access = AdventureRules.Evaluate(catalog, profile);

            var sunny = access.worlds[0].campaign;
            Assert.That(sunny.completedEvents, Is.EqualTo(23),
                "redrock completions can never inflate sunny's count");
            Assert.That(sunny.regionalStars, Is.EqualTo(30),
                "72 foreign stars must not add to sunny's 30");
            Assert.That(sunny.finalesAvailable, Is.False,
                "foreign stars/completions cannot open sunny's gate");

            // Redrock's own gate IS satisfied — but the world stays
            // locked because sunny is not complete. Progress parked in
            // a closed world never leaks.
            var redrock = World(access, "redrock-canyon");
            Assert.That(redrock.campaign.completedEvents, Is.EqualTo(24));
            Assert.That(redrock.campaign.regionalStars, Is.EqualTo(72));
            Assert.That(redrock.campaign.finalesAvailable, Is.True,
                "the regional math is per-world even while locked");
            Assert.That(redrock.available, Is.False);
            Assert.That(access.worlds[2].available, Is.False);
        }

        // --- course access ---------------------------------------------------

        [Test]
        public void CourseAccessFollowsWorldUnlocks()
        {
            var catalog = Catalog();
            var fresh = CampaignTestFixture.Profile();
            foreach (var courseId in CampaignTestFixture.CourseIds)
                Assert.That(AdventureRules.CanAccessCourse(catalog, fresh,
                        courseId), Is.True, courseId);
            foreach (var courseId in CampaignTestFixture.RedrockCourseIds)
                Assert.That(AdventureRules.CanAccessCourse(catalog, fresh,
                        courseId), Is.False, courseId);
            foreach (var courseId in CampaignTestFixture.SnowyCourseIds)
                Assert.That(AdventureRules.CanAccessCourse(catalog, fresh,
                        courseId), Is.False, courseId);

            // A course no world claims is not adventure content — no
            // world can open it.
            Assert.That(AdventureRules.CanAccessCourse(catalog, fresh,
                "no-such-course"), Is.False);
            Assert.That(AdventureRules.CanAccessCourse(catalog, fresh,
                null), Is.False);
            Assert.That(AdventureRules.CanAccessCourse(catalog, fresh,
                ""), Is.False);

            // Completing sunny opens exactly the redrock courses.
            var progressed = CampaignTestFixture.Profile();
            CampaignTestFixture.CompleteWorldFinales(progressed,
                "sunny-island", victorious: false);
            foreach (var courseId in CampaignTestFixture.RedrockCourseIds)
                Assert.That(AdventureRules.CanAccessCourse(catalog,
                        progressed, courseId), Is.True, courseId);
            Assert.That(AdventureRules.CanAccessCourse(catalog,
                progressed, "snowpine-circuit"), Is.False);
        }

        // --- degenerate inputs ----------------------------------------------

        [Test]
        public void NullAndLegacyCatalogsEvaluateSafely()
        {
            var access = AdventureRules.Evaluate(null,
                CampaignTestFixture.Profile());
            Assert.That(access.worlds, Is.Empty);
            Assert.That(access.complete, Is.False);
            Assert.That(AdventureRules.FindWorld(access, "sunny-island"),
                Is.Null);

            // A legacy catalog carries no worlds: nothing is available,
            // nothing is complete — and the call still cannot throw.
            var legacy = GameplayCatalog.CreateLegacy(
                CampaignTestFixture.MultiCatalog().Driving);
            access = AdventureRules.Evaluate(legacy,
                CampaignTestFixture.Profile());
            Assert.That(access.worlds, Is.Empty);
            Assert.That(access.complete, Is.False);
            Assert.That(AdventureRules.CanAccessCourse(legacy,
                CampaignTestFixture.Profile(), "harbor-hop"), Is.False);

            // A null profile still gets the structural answer: world one
            // open, everything gated.
            access = AdventureRules.Evaluate(Catalog(), null);
            Assert.That(Availability(access),
                Is.EqualTo(new[] { true, false, false }));
        }

        [Test]
        public void EvaluationIsDeterministicAndLeavesInputsUntouched()
        {
            var catalog = Catalog();
            var profile = CampaignTestFixture.Profile();
            CampaignTestFixture.CompleteWorldEvents(profile,
                "sunny-island", 24, 6);
            var before = CampaignTestFixture.ProfileSnapshot(profile);

            var a = AdventureRules.Evaluate(catalog, profile);
            var b = AdventureRules.Evaluate(catalog, profile);
            Assert.That(Availability(a), Is.EqualTo(Availability(b)));
            Assert.That(a.complete, Is.EqualTo(b.complete));
            for (var i = 0; i < a.worlds.Count; i++)
            {
                Assert.That(a.worlds[i].campaign.regionalStars,
                    Is.EqualTo(b.worlds[i].campaign.regionalStars));
                Assert.That(a.worlds[i].campaign.availableEventIds,
                    Is.EqualTo(b.worlds[i].campaign.availableEventIds));
            }

            Assert.That(CampaignTestFixture.ProfileSnapshot(profile),
                Is.EqualTo(before),
                "evaluation must not mutate the source profile");
        }
    }
}
