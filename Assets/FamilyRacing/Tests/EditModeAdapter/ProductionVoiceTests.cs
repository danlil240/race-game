using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class ProductionVoiceTests
    {
        private static FoundationState State()
        {
            return new FoundationState
            {
                activeSlot = 1, language = "en", screen = "campaign",
                progression = new ProgressionState
                {
                    available = true, campaignWorldId = "sunny-island",
                    worlds = new List<WorldState>
                    {
                        new WorldState { worldId = "sunny-island", available = true },
                        new WorldState { worldId = "redrock-canyon", available = false }
                    }
                }
            };
        }

        [Test]
        public void RepeatedFramesDoNotRepeatWorldOrUnlockSpeech()
        {
            var observer = new ProductionVoiceTransitions();
            var state = State();
            CollectionAssert.AreEqual(new[] { "world.intro.sunny-island" }, observer.Observe(state));
            Assert.That(observer.Observe(state), Is.Empty);
            state.progression.worlds[1].available = true;
            CollectionAssert.AreEqual(new[] { "world.unlocked" }, observer.Observe(state));
            Assert.That(observer.Observe(state), Is.Empty);
        }

        [Test]
        public void LoadingCompletedSaveDoesNotPretendAdventureWasJustCompleted()
        {
            var observer = new ProductionVoiceTransitions();
            var state = State();
            state.progression.adventureComplete = true;
            CollectionAssert.DoesNotContain(observer.Observe(state), "adventure.complete");
            state.progression.adventureComplete = false;
            observer.Observe(state);
            state.progression.adventureComplete = true;
            CollectionAssert.AreEqual(new[] { "adventure.complete" }, observer.Observe(state));
            Assert.That(observer.Observe(state), Is.Empty);
        }

        [TestCase(0, "objective.completion")]
        [TestCase(1, "objective.introductory")]
        [TestCase(3, "objective.mastery")]
        public void FinaleAnnouncesItsRivalAndNextUnearnedObjectiveOnce(int mask, string objective)
        {
            var state = State();
            var observer = new ProductionVoiceTransitions();
            observer.Observe(state);
            state.screen = "driving";
            state.progression.campaignEvents.Add(new CampaignEventState
            {
                eventId = "sunny-island-car-finale", finale = true,
                rivalId = "sunny-island-car-rival", objectiveMask = mask
            });
            state.soloRace = new SoloRaceState
            {
                mode = "campaign", raceId = "race-1", phase = "countdown",
                race = new NetworkRaceState
                {
                    racers = new List<NetworkRacerState>
                    {
                        new NetworkRacerState { session = new DrivingSessionState { eventId = "sunny-island-car-finale" } }
                    }
                }
            };
            CollectionAssert.AreEqual(new[] { "rival.intro.sunny-island-car-rival", objective }, observer.Observe(state));
            Assert.That(observer.Observe(state), Is.Empty);
            state.soloRace.raceId = "race-2";
            Assert.That(observer.Observe(state).Count, Is.EqualTo(2));
        }

        [Test]
        public void ProfileSwitchRebaselinesWorldAvailability()
        {
            var state = State();
            var observer = new ProductionVoiceTransitions();
            observer.Observe(state);
            state.activeSlot = 2;
            state.progression.worlds[1].available = true;
            CollectionAssert.DoesNotContain(observer.Observe(state), "world.unlocked");
        }
    }
}
