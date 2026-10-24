using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // M5 Task 10 + M6 Task 3 stream tests: the local-mode `soloRace`
    // slice and the `progression` surface — including the schema-7
    // ordered `worlds` rows and `adventureComplete` — ride the real
    // StateStream writer: present from the first menu snapshot
    // (failed-save states included), deep-copy isolated from producer
    // mutation, and reconstructable through the independent
    // snapshot+delta merge an external consumer performs.
    public sealed class GameplayStreamPlayTests
    {
        private string directory;
        private StateStream stream;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "fr-loop-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            stream = new StateStream(directory, "run-loop-stream",
                "device-test", "participant-test", "build-test",
                () => "content-test");
            stream.Start();
        }

        [TearDown]
        public void TearDown()
        {
            if (stream != null) stream.Dispose();
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception) { }
        }

        private List<JObject> ReadRecords(int expected)
        {
            // The writer thread serializes asynchronously; poll briefly
            // so assertions never read a partially flushed trace.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            List<JObject> records;
            do
            {
                records = new List<JObject>();
                var path = Path.Combine(directory, "state.ndjson");
                if (File.Exists(path))
                    using (var reader = new StreamReader(
                        new FileStream(path, FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            if (line.Length > 0)
                                records.Add(JObject.Parse(line));
                    }
                if (records.Count >= expected) return records;
                Thread.Sleep(10);
            } while (DateTime.UtcNow < deadline);
            return records;
        }

        private static NetworkSessionState IdleNetwork()
        {
            return new NetworkSessionState
            {
                role = "none", phase = "idle",
                participants = new List<ParticipantState>(),
                discovered = new List<LanDiscoveredHost>(),
                results = new List<ResultDeliveryState>()
            };
        }

        // A menu-level schema-7 state: every slice populated the way the
        // service publishes it on the FIRST snapshot — progression is
        // always present (available=false without a catalog), soloRace
        // stays null until a local mode runs.
        private static FoundationState MenuState(long tick,
            string persistenceStatus)
        {
            return new FoundationState
            {
                screen = "sandbox",
                language = "en",
                activeSlot = 0,
                tick = tick,
                captureValid = true,
                captureError = "",
                persistenceStatus = persistenceStatus,
                input = new InputState(),
                slots = new List<SlotState>(),
                pendingSettings = false,
                featureCoverage = new FeatureCoverage(),
                presentation = new PresentationState
                {
                    textDirection = "ltr",
                    camera = new DrivingCameraState { mode = "none" }
                },
                drivingEvents = new List<DrivingEvent>(),
                droppedDrivingEvents = 0,
                network = IdleNetwork(),
                networkRace = null,
                prediction = null,
                remotePresentation = null,
                soloRace = null,
                progression = new ProgressionState
                {
                    available = false,
                    contentIdentity = "none"
                }
            };
        }

        // A live solo-race state: the four-racer coordinator slice plus
        // the resolved progression surface a campaign boundary emits.
        private static FoundationState SoloState(long tick)
        {
            var state = MenuState(tick, "");
            state.screen = "driving";
            state.soloRace = new SoloRaceState
            {
                mode = "campaign",
                sessionId = "solo-1",
                raceId = "local-0-a1",
                participantId = "local",
                phase = "running",
                difficultyId = "easy",
                tuningSource = "purchased",
                race = new NetworkRaceState
                {
                    sessionId = "solo-1",
                    raceId = "local-0-a1",
                    phase = "running",
                    elapsedTicks = 30,
                    racers = new List<NetworkRacerState>
                    {
                        new NetworkRacerState
                        {
                            participantId = "local", slot = 0,
                            vehicleId = "car-01", attribution = "human",
                            position = 1,
                            session = new DrivingSessionState
                            {
                                raceId = "local-0-a1",
                                vehicleId = "car-01",
                                phase = "racing",
                                motor = new MotorState { x = 1f, z = 2f }
                            }
                        },
                        new NetworkRacerState
                        {
                            participantId = "ai-1", slot = 1,
                            vehicleId = "car-02", attribution = "ai",
                            position = 2,
                            session = new DrivingSessionState
                            {
                                raceId = "local-0-a1",
                                vehicleId = "car-02",
                                phase = "racing",
                                motor = new MotorState { x = 4f, z = 6f }
                            }
                        }
                    },
                    standings = new List<int> { 0, 1 },
                    events = new List<DrivingEvent>()
                },
                metrics = new List<RaceMetrics>(),
                events = new List<DrivingEvent>()
            };
            state.progression = new ProgressionState
            {
                available = true,
                contentIdentity = "gameplay-loop-1|driving-1",
                campaignWorldId = "sunny-island",
                campaignCompletedEvents = 1,
                campaignStars = 2,
                campaignRequiredEvents = 2,
                campaignRequiredStars = 1,
                campaignFinalesOpen = false,
                campaignWorldComplete = false,
                rewardUnresolved = true,
                rewardUnresolvedRaceId = "local-0-a0",
                // Schema-7 ordered world rows: sunny selected and open,
                // redrock/snowy behind the world gate.
                worlds = new List<WorldState>
                {
                    new WorldState
                    {
                        worldId = "sunny-island", nameEn = "Sunny Island",
                        nameHe = "האי השמשי", selected = true,
                        available = true, lockedReason = "",
                        completedEvents = 1, requiredEvents = 24,
                        stars = 2, requiredStars = 30,
                        finalesOpen = false, completedFinales = 0,
                        totalFinales = 2, worldComplete = false,
                        rewardVehicleIds = new List<string>
                            { "car-02", "motorcycle-02" }
                    },
                    new WorldState
                    {
                        worldId = "redrock-canyon",
                        nameEn = "Redrock Canyon", nameHe = "קניון רדרוק",
                        selected = false, available = false,
                        lockedReason = "worldLocked",
                        completedEvents = 0, requiredEvents = 24,
                        stars = 0, requiredStars = 30,
                        finalesOpen = false, completedFinales = 0,
                        totalFinales = 2, worldComplete = false,
                        rewardVehicleIds = new List<string>
                            { "car-03", "motorcycle-03" }
                    },
                    new WorldState
                    {
                        worldId = "snowy-peaks",
                        nameEn = "Snowy Peaks", nameHe = "הפסגות המושלגות",
                        selected = false, available = false,
                        lockedReason = "worldLocked",
                        completedEvents = 0, requiredEvents = 24,
                        stars = 0, requiredStars = 30,
                        finalesOpen = false, completedFinales = 0,
                        totalFinales = 2, worldComplete = false,
                        rewardVehicleIds = new List<string>
                            { "car-04", "motorcycle-04" }
                    }
                },
                adventureComplete = false,
                campaignEvents = new List<CampaignEventState>
                {
                    new CampaignEventState
                    {
                        eventId = "harbor-car-circuit",
                        courseId = "harbor-hop", classId = "car",
                        eventType = "circuit",
                        objectiveMask = 7, stars = 2, completed = true,
                        locked = false, lockReason = ""
                    },
                    new CampaignEventState
                    {
                        eventId = "sunny-car-finale",
                        courseId = "harbor-hop", classId = "car",
                        eventType = "finale", finale = true,
                        rivalId = "rival-car",
                        rewardVehicleId = "car-02",
                        objectiveMask = 0, stars = 0, completed = false,
                        locked = true, lockReason = "eventLocked"
                    }
                },
                bounties = new List<BountyState>
                {
                    new BountyState
                    {
                        assignmentId = "assign-1",
                        bountyId = "drift-10s", family = "drift",
                        metric = "driftSeconds", threshold = 10,
                        progress = 4, unit = "seconds",
                        rewardCoins = 10, completed = false
                    },
                    new BountyState
                    {
                        assignmentId = "assign-2",
                        bountyId = "boost-3", family = "boost",
                        metric = "humanBoostActivations", threshold = 3,
                        progress = 0, unit = "count",
                        rewardCoins = 10, completed = false
                    }
                }
            };
            return state;
        }

        // Independent merge — mirrors the documented consumer semantics
        // in the stream contract, sharing no code with the producer's
        // diff.
        private static JObject ApplyDelta(JObject baseline, JObject delta)
        {
            var merged = (JObject)baseline.DeepClone();
            var removed = (JArray)delta["removed"];
            foreach (var pathToken in removed)
            {
                var path = (string)pathToken;
                var segments = path.Split('.');
                JObject node = merged;
                for (var i = 0; i < segments.Length - 1; i++)
                    node = (JObject)node[segments[i]];
                Assert.That(node.Remove(segments[segments.Length - 1]),
                    Is.True, "removed path must resolve: " + path);
            }
            Merge((JObject)delta["changed"], merged);
            return merged;
        }

        private static void Merge(JObject changed, JObject into)
        {
            foreach (var property in changed.Properties())
            {
                var current = into[property.Name];
                var child = property.Value as JObject;
                if (child != null && current is JObject)
                    Merge(child, (JObject)current);
                else
                    into[property.Name] = property.Value;
            }
        }

        [Test]
        public void MenuAndFailedSaveSnapshotsCarrySchema7Slices()
        {
            // Menus — before any catalog binds or race runs — already
            // publish the full schema-7 shape; a failed save keeps it.
            stream.EmitDeltaBoundary(MenuState(1, ""), null);
            stream.EmitDeltaBoundary(MenuState(2, "saveFailed"), null);
            var records = ReadRecords(2);

            var first = (JObject)records[0]["state"];
            Assert.That((int)records[0]["stateSchemaVersion"],
                Is.EqualTo(NetworkSettings.StateSchemaVersion));
            Assert.That((int)first["stateSchemaVersion"],
                Is.EqualTo(7),
                "the loop ships on state schema 7");
            Assert.That(first["soloRace"], Is.Not.Null,
                "the soloRace key exists from the first snapshot");
            Assert.That(first["soloRace"].Type,
                Is.EqualTo(JTokenType.Null),
                "no local mode runs at the menu — null, not fabricated");
            var progression = (JObject)first["progression"];
            Assert.That(progression, Is.Not.Null,
                "the progression slice exists from the first snapshot");
            Assert.That(progression["campaignEvents"], Is.Not.Null);
            Assert.That(progression["bounties"], Is.Not.Null);
            Assert.That(progression["worlds"], Is.Not.Null,
                "the schema-7 world rows exist from the first snapshot");
            Assert.That(progression["adventureComplete"], Is.Not.Null,
                "adventureComplete is part of the schema-7 contract");
            Assert.That((bool)progression["rewardUnresolved"], Is.False);

            // A failed save still carries the loop slices — the reader
            // never has to special-case the error boundary.
            var failed = ApplyDelta(first, records[1]);
            Assert.That((string)failed["persistenceStatus"],
                Is.EqualTo("saveFailed"));
            Assert.That(failed["progression"], Is.Not.Null);
            Assert.That(failed["progression"].Type,
                Is.EqualTo(JTokenType.Object));
        }

        [Test]
        public void SoloRaceBoundaryCarriesModeResultAndTuningSource()
        {
            stream.EmitDeltaBoundary(SoloState(5), "cmd-loop");
            var records = ReadRecords(1);
            var state = (JObject)records[0]["state"];

            var solo = (JObject)state["soloRace"];
            Assert.That((string)solo["mode"], Is.EqualTo("campaign"));
            Assert.That((string)solo["raceId"],
                Is.EqualTo("local-0-a1"));
            Assert.That((string)solo["phase"], Is.EqualTo("running"));
            Assert.That((string)solo["tuningSource"],
                Is.EqualTo("purchased"),
                "the human seat's tune provenance is streamed");
            Assert.That((string)solo["difficultyId"],
                Is.EqualTo("easy"));
            var race = (JObject)solo["race"];
            Assert.That((JArray)race["racers"], Is.Not.Null.And.Count
                .EqualTo(2));
            Assert.That((string)race["racers"][0]["attribution"],
                Is.EqualTo("human"));
            Assert.That(solo["trial"].Type, Is.EqualTo(JTokenType.Null),
                "a coordinator race never carries a lone session");
            Assert.That(solo["result"].Type, Is.EqualTo(JTokenType.Null),
                "no result until finalization");

            var progression = (JObject)state["progression"];
            Assert.That((bool)progression["available"], Is.True);
            Assert.That((string)progression["contentIdentity"],
                Is.EqualTo("gameplay-loop-1|driving-1"));
            Assert.That((int)progression["campaignStars"],
                Is.EqualTo(2));
            Assert.That((bool)progression["rewardUnresolved"], Is.True);
            Assert.That((string)progression["rewardUnresolvedRaceId"],
                Is.EqualTo("local-0-a0"),
                "the unresolved-reward gate names the retained race");
            var events = (JArray)progression["campaignEvents"];
            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That((bool)events[1]["locked"], Is.True);
            Assert.That((string)events[1]["lockReason"],
                Is.EqualTo("eventLocked"),
                "the honest lock reason rides the row");
            Assert.That((string)events[1]["rewardVehicleId"],
                Is.EqualTo("car-02"));
            var bounties = (JArray)progression["bounties"];
            Assert.That(bounties.Count, Is.EqualTo(2));
            Assert.That((int)bounties[0]["progress"], Is.EqualTo(4));
            Assert.That((int)bounties[0]["threshold"], Is.EqualTo(10));
            Assert.That((string)bounties[0]["assignmentId"],
                Is.EqualTo("assign-1"));

            // Schema-7: the ordered worlds surface — availability, the
            // machine-safe lock reason, regional counts and the
            // adventure-complete fact all ride the same boundary.
            var worlds = (JArray)progression["worlds"];
            Assert.That(worlds, Is.Not.Null.And.Count.EqualTo(3),
                "every authored world publishes a row");
            Assert.That((string)worlds[0]["worldId"],
                Is.EqualTo("sunny-island"));
            Assert.That((bool)worlds[0]["selected"], Is.True);
            Assert.That((bool)worlds[0]["available"], Is.True);
            Assert.That((int)worlds[0]["requiredEvents"], Is.EqualTo(24));
            Assert.That((int)worlds[0]["requiredStars"], Is.EqualTo(30));
            Assert.That((bool)worlds[1]["available"], Is.False);
            Assert.That((string)worlds[1]["lockedReason"],
                Is.EqualTo("worldLocked"),
                "a closed world carries the code the command rejects with");
            Assert.That((int)worlds[1]["totalFinales"], Is.EqualTo(2));
            Assert.That((JArray)worlds[2]["rewardVehicleIds"],
                Is.Not.Null.And.Count.EqualTo(2));
            Assert.That((bool)progression["adventureComplete"], Is.False);
        }

        [Test]
        public void EmittedSoloAndProgressionSlicesAreDeepCopyIsolated()
        {
            var state = SoloState(1);
            stream.EmitDeltaBoundary(state, null);
            // Mutate every schema-6 slice after emission — a shared
            // reference would corrupt the recorded boundary.
            state.soloRace.raceId = "mutated";
            state.soloRace.tuningSource = "mutated";
            state.soloRace.race.racers[0].participantId = "mutated";
            state.soloRace.race.standings[0] = 9;
            state.progression.campaignEvents[0].locked = true;
            state.progression.campaignEvents[1].stars = 99;
            state.progression.bounties[0].progress = 999;
            state.progression.rewardUnresolvedRaceId = "mutated";
            // The schema-7 world rows share the same isolation promise:
            // mutating them after emission must never corrupt the
            // recorded boundary.
            state.progression.worlds[0].worldId = "mutated";
            state.progression.worlds[1].available = true;
            state.progression.worlds[1].lockedReason = "";
            state.progression.worlds[0].rewardVehicleIds.Add("x");
            state.progression.worlds.Clear();
            state.progression.adventureComplete = true;
            var records = ReadRecords(1);
            var emitted = (JObject)records[0]["state"];
            Assert.That((string)emitted["soloRace"]["raceId"],
                Is.EqualTo("local-0-a1"));
            Assert.That((string)emitted["soloRace"]["tuningSource"],
                Is.EqualTo("purchased"));
            Assert.That((string)emitted["soloRace"]["race"]["racers"][0]
                ["participantId"], Is.EqualTo("local"));
            Assert.That((int)emitted["soloRace"]["race"]["standings"][0],
                Is.EqualTo(0));
            Assert.That((bool)emitted["progression"]["campaignEvents"][0]
                ["locked"], Is.False);
            Assert.That((int)emitted["progression"]["campaignEvents"][1]
                ["stars"], Is.EqualTo(0));
            Assert.That((int)emitted["progression"]["bounties"][0]
                ["progress"], Is.EqualTo(4));
            Assert.That((string)emitted["progression"]
                ["rewardUnresolvedRaceId"], Is.EqualTo("local-0-a0"));
            var emittedWorlds = (JArray)emitted["progression"]["worlds"];
            Assert.That(emittedWorlds.Count, Is.EqualTo(3));
            Assert.That((string)emittedWorlds[0]["worldId"],
                Is.EqualTo("sunny-island"));
            Assert.That((bool)emittedWorlds[1]["available"], Is.False);
            Assert.That((string)emittedWorlds[1]["lockedReason"],
                Is.EqualTo("worldLocked"));
            Assert.That((JArray)emittedWorlds[0]["rewardVehicleIds"],
                Has.Count.EqualTo(2));
            Assert.That((bool)emitted["progression"]["adventureComplete"],
                Is.False);
        }

        [Test]
        public void WorldSelectionAndUnlockBoundariesReconstruct()
        {
            // Baseline: the paused menu — selection is transient state,
            // so a paused boundary still carries the full world rows.
            var first = SoloState(1);
            first.soloRace = null;
            first.screen = "campaign";
            first.paused = true;
            stream.EmitDeltaBoundary(first, "cmd-pause");

            // Selection moves to redrock while paused: campaignWorldId
            // and the per-row selected flags move together.
            var second = SoloState(2);
            second.soloRace = null;
            second.screen = "campaign";
            second.paused = true;
            second.progression.campaignWorldId = "redrock-canyon";
            second.progression.worlds[0].selected = false;
            second.progression.worlds[1].selected = true;
            second.progression.worlds[1].available = true;
            second.progression.worlds[1].lockedReason = "";
            stream.EmitDeltaBoundary(second, "cmd-selectWorld");

            // A committed reward boundary unlocks the third world and
            // completes the second — the unlock facts land mid-history.
            var third = SoloState(3);
            third.soloRace = null;
            third.screen = "campaign";
            third.paused = true;
            third.progression.worlds[1].worldComplete = true;
            third.progression.worlds[1].completedFinales = 2;
            third.progression.worlds[2].available = true;
            third.progression.worlds[2].lockedReason = "";
            stream.EmitDeltaBoundary(third, "cmd-commit");

            // A failed save keeps every world row — the reader never
            // special-cases the error boundary — and the retry's commit
            // boundary carries the finished facts again.
            var fourth = SoloState(4);
            fourth.soloRace = null;
            fourth.screen = "campaign";
            fourth.paused = true;
            fourth.persistenceStatus = "saveFailed";
            stream.EmitDeltaBoundary(fourth, "cmd-saveFail");
            var fifth = SoloState(5);
            fifth.soloRace = null;
            fifth.screen = "campaign";
            fifth.paused = true;
            fifth.persistenceStatus = "saved";
            fifth.progression = third.progression;
            stream.EmitDeltaBoundary(fifth, "cmd-retrySave");

            // A profile switch re-derives the selection: a fresh profile
            // selects the first world again.
            var sixth = SoloState(6);
            sixth.soloRace = null;
            sixth.screen = "campaign";
            sixth.paused = false;
            sixth.activeSlot = 1;
            sixth.progression.campaignWorldId = "sunny-island";
            sixth.progression.worlds[0].selected = true;
            sixth.progression.worlds[1].selected = false;
            sixth.progression.worlds[1].available = false;
            sixth.progression.worlds[1].lockedReason = "worldLocked";
            sixth.progression.worlds[1].worldComplete = false;
            sixth.progression.worlds[1].completedFinales = 0;
            sixth.progression.worlds[2].available = false;
            sixth.progression.worlds[2].lockedReason = "worldLocked";
            stream.EmitDeltaBoundary(sixth, "cmd-profile");

            var records = ReadRecords(6);
            var baseline = (JObject)records[0]["state"].DeepClone();
            Assert.That((bool)baseline["paused"], Is.True);
            var baseWorlds = (JArray)baseline["progression"]["worlds"];
            Assert.That(baseWorlds.Count, Is.EqualTo(3));
            Assert.That((bool)baseWorlds[0]["selected"], Is.True);

            var selected = ApplyDelta(baseline, records[1]);
            Assert.That((string)selected["progression"]["campaignWorldId"],
                Is.EqualTo("redrock-canyon"),
                "selection while paused must reconstruct");
            var selWorlds = (JArray)selected["progression"]["worlds"];
            Assert.That((bool)selWorlds[1]["selected"], Is.True);
            Assert.That((bool)selWorlds[1]["available"], Is.True);

            var unlocked = ApplyDelta(selected, records[2]);
            var unWorlds = (JArray)unlocked["progression"]["worlds"];
            Assert.That((bool)unWorlds[1]["worldComplete"], Is.True,
                "the committed reward's world-complete fact reconstructs");
            Assert.That((bool)unWorlds[2]["available"], Is.True);
            Assert.That((string)unWorlds[2]["lockedReason"],
                Is.EqualTo(""));

            var failed = ApplyDelta(unlocked, records[3]);
            Assert.That((string)failed["persistenceStatus"],
                Is.EqualTo("saveFailed"));
            Assert.That(((JArray)failed["progression"]["worlds"]).Count,
                Is.EqualTo(3),
                "the failed-save boundary still carries every world row");

            var retried = ApplyDelta(failed, records[4]);
            Assert.That((string)retried["persistenceStatus"],
                Is.EqualTo("saved"));
            Assert.That((bool)((JArray)retried["progression"]["worlds"])
                [2]["available"], Is.True);

            var switched = ApplyDelta(retried, records[5]);
            Assert.That((int)switched["activeSlot"], Is.EqualTo(1));
            var swWorlds = (JArray)switched["progression"]["worlds"];
            Assert.That((string)switched["progression"]["campaignWorldId"],
                Is.EqualTo("sunny-island"),
                "the profile switch resets the selected world");
            Assert.That((bool)swWorlds[0]["selected"], Is.True);
            Assert.That((bool)swWorlds[1]["available"], Is.False);
        }

        [Test]
        public void DeltaReconstructionCoversProgressionChanges()
        {
            var first = SoloState(1);
            stream.EmitDeltaBoundary(first, null);

            // A finalized result + progression commit: stars advance,
            // the finale unlocks, a bounty completes and the unresolved
            // gate clears — exactly the shape a reward boundary emits.
            var second = SoloState(2);
            second.soloRace.phase = "finished";
            second.soloRace.result = new RaceResult
            {
                formatVersion = 2,
                resultId = "result-1",
                raceId = "local-0-a1",
                mode = "campaign",
                finalized = true,
                racers = new List<RaceResultEntry>
                {
                    new RaceResultEntry
                    {
                        participantId = "local", position = 1,
                        outcome = "finished", finishTicks = 900,
                        coinsAwarded = 25
                    }
                }
            };
            second.progression.campaignStars = 3;
            second.progression.campaignEvents[1].locked = false;
            second.progression.campaignEvents[1].lockReason = "";
            second.progression.bounties[0].completed = true;
            second.progression.rewardUnresolved = false;
            second.progression.rewardUnresolvedRaceId = "";
            stream.EmitDeltaBoundary(second, "cmd-result");

            var third = SoloState(3);
            third.soloRace = null;   // session retired by results.continue
            third.screen = "campaign";
            // The committed progression facts persist across the
            // transition — carry the second boundary's surface verbatim.
            third.progression = second.progression;
            stream.EmitDeltaBoundary(third, "cmd-continue");

            var records = ReadRecords(3);
            var baseline = (JObject)records[0]["state"].DeepClone();

            var delta1 = records[1];
            Assert.That((string)delta1["type"], Is.EqualTo("delta"));
            Assert.That((int)delta1["base"],
                Is.EqualTo((int)records[0]["sequence"]));
            var reconstructed = ApplyDelta(baseline, delta1);
            Assert.That((string)reconstructed["soloRace"]["phase"],
                Is.EqualTo("finished"));
            Assert.That((string)reconstructed["soloRace"]["result"]
                ["resultId"], Is.EqualTo("result-1"));
            Assert.That((bool)reconstructed["soloRace"]["result"]
                ["finalized"], Is.True);
            Assert.That((int)reconstructed["progression"]
                ["campaignStars"], Is.EqualTo(3));
            Assert.That((bool)reconstructed["progression"]
                ["campaignEvents"][1]["locked"], Is.False);
            Assert.That((bool)reconstructed["progression"]["bounties"][0]
                ["completed"], Is.True);
            Assert.That((bool)reconstructed["progression"]
                ["rewardUnresolved"], Is.False);

            var delta2 = records[2];
            Assert.That((int)delta2["base"],
                Is.EqualTo((int)delta1["sequence"]));
            reconstructed = ApplyDelta(reconstructed, delta2);
            Assert.That(reconstructed["soloRace"].Type,
                Is.EqualTo(JTokenType.Null),
                "the retired session reconstructs to null");
            Assert.That((string)reconstructed["screen"],
                Is.EqualTo("campaign"));
            Assert.That((int)reconstructed["progression"]
                ["campaignStars"], Is.EqualTo(3));
        }
    }
}
