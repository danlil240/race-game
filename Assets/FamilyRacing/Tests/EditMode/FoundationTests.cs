using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // Pure service tests use the same real temp-file ProfileStore as the
    // profile suite; only the JSON serializer adapts to the host.
    public sealed class FoundationTests
    {
        private const string RunId = "foundation-test-run";
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "family-racing-foundation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test]
        public void InitialStateIsNeutralWithFourMissingSlots()
        {
            var state = Service().Snapshot();
            Assert.That(state.screen, Is.EqualTo("profiles"));
            Assert.That(state.language, Is.EqualTo("en"));
            Assert.That(state.activeSlot, Is.EqualTo(-1));
            Assert.That(state.paused, Is.False);
            Assert.That(state.tick, Is.Zero);
            Assert.That(state.savedProfile, Is.Null);
            Assert.That(state.draftProfile, Is.Null);
            Assert.That(state.pendingSettings, Is.False);
            Assert.That(state.captureValid, Is.True);
            Assert.That(state.captureError, Is.EqualTo(""));
            Assert.That(Neutral(state.input), Is.True);
            Assert.That(state.slots.Count, Is.EqualTo(4));
            for (var i = 0; i < 4; i++)
            {
                Assert.That(state.slots[i].slot, Is.EqualTo(i));
                Assert.That(state.slots[i].status, Is.EqualTo("Missing"));
                Assert.That(state.slots[i].profile, Is.Null);
            }
            Assert.That(state.featureCoverage.tutorial, Is.EqualTo("notImplemented"));
            Assert.That(state.featureCoverage.drivingSimulation, Is.EqualTo("implemented"));
            Assert.That(state.featureCoverage.campaign, Is.EqualTo("notImplemented"));
            Assert.That(state.featureCoverage.rewardsUpgradesCosmetics, Is.EqualTo("notImplemented"));
            Assert.That(state.featureCoverage.lan, Is.EqualTo("notImplemented"));
            Assert.That(state.featureCoverage.content, Is.EqualTo("graybox"));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void CreateAndSelectEachSlotSurvivesFreshService(int slot)
        {
            var service = Service();
            var created = service.Execute(Cmd("profile.create", CreateParams(slot, "he")));
            Assert.That(created.status, Is.EqualTo(CommandResult.Applied));
            var afterCreate = service.Snapshot();
            Assert.That(afterCreate.screen, Is.EqualTo("settings"));
            Assert.That(afterCreate.activeSlot, Is.EqualTo(slot));
            Assert.That(afterCreate.language, Is.EqualTo("he"));
            Assert.That(afterCreate.slots[slot].status, Is.EqualTo("Loaded"));
            Assert.That(afterCreate.savedProfile.language, Is.EqualTo("he"));

            var id = afterCreate.savedProfile.id;
            var reloaded = Service();
            Assert.That(reloaded.Snapshot().slots[slot].status, Is.EqualTo("Loaded"));
            Assert.That(reloaded.Snapshot().slots[slot].profile.id, Is.EqualTo(id));
            var selected = reloaded.Execute(Cmd("profile.select", P("slot", slot.ToString())));
            Assert.That(selected.status, Is.EqualTo(CommandResult.Applied));
            var state = reloaded.Snapshot();
            Assert.That(state.activeSlot, Is.EqualTo(slot));
            Assert.That(state.savedProfile.id, Is.EqualTo(id));
            Assert.That(state.language, Is.EqualTo("he"));
            Assert.That(Neutral(state.input), Is.True);
        }

        [Test]
        public void CreateOverBlockedSlotFailsSafely()
        {
            File.WriteAllText(Primary(0), "private corrupt content");
            File.WriteAllText(Backup(0), "also corrupt");
            var service = Service();
            Assert.That(service.Snapshot().slots[0].status, Is.EqualTo("Blocked"));
            var before = FilesJson();
            var result = service.Execute(Cmd("profile.create", CreateParams(0, "en")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Snapshot().activeSlot, Is.EqualTo(-1));
            Assert.That(FilesJson(), Is.EqualTo(before));
        }

        [Test]
        public void CreateOverOrphanedBackupFailsSafely()
        {
            File.WriteAllText(Backup(1), serializer.Serialize(ValidProfile(1)));
            var service = Service();
            // A valid orphaned backup loads as recovered, so the slot is not
            // Missing and creation must still be refused without touching it.
            Assert.That(service.Snapshot().slots[1].status, Is.EqualTo("RecoveredFromBackup"));
            var before = FilesJson();
            var result = service.Execute(Cmd("profile.create", CreateParams(1, "en")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(FilesJson(), Is.EqualTo(before));
        }

        [Test]
        public void CreateOnOccupiedSlotRejected()
        {
            var service = Service();
            CreateLoaded(service, 2, "en");
            var id = service.Snapshot().slots[2].profile.id;
            var result = service.Execute(Cmd("profile.create", CreateParams(2, "he")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Snapshot().slots[2].profile.id, Is.EqualTo(id));
        }

        [Test]
        public void SelectRejectsMissingAndBlockedSlots()
        {
            var service = Service();
            Assert.That(service.Execute(Cmd("profile.select", P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Rejected));
            File.WriteAllText(Primary(1), "corrupt");
            var blocked = Service();
            Assert.That(blocked.Execute(Cmd("profile.select", P("slot", "1"))).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(blocked.Snapshot().activeSlot, Is.EqualTo(-1));
        }

        [TestCase("protocol")]
        [TestCase("run")]
        [TestCase("participant")]
        [TestCase("commandId")]
        [TestCase("runId")]
        [TestCase("participantId")]
        [TestCase("action")]
        public void InvalidHeadersRejectWithoutMutation(string kind)
        {
            var service = Service();
            var command = kind == "protocol"
                ? new GameCommand(2, RunId, "c1", "local", "input.clear", null)
                : kind == "run"
                    ? new GameCommand(1, "other-run", "c1", "local", "input.clear", null)
                    : kind == "participant"
                        ? new GameCommand(1, RunId, "c1", "guest", "input.clear", null)
                        : kind == "commandId"
                            ? new GameCommand(1, RunId, "", "local", "input.clear", null)
                            : kind == "runId"
                                ? new GameCommand(1, "", "c1", "local", "input.clear", null)
                                : kind == "participantId"
                                    ? new GameCommand(1, RunId, "c1", "", "input.clear", null)
                                    : new GameCommand(1, RunId, "c1", "local", "", null);
            var beforeState = StateJson(service);
            var beforeFiles = FilesJson();
            var result = service.Execute(command);
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(StateJson(service), Is.EqualTo(beforeState));
            Assert.That(FilesJson(), Is.EqualTo(beforeFiles));
        }

        [Test]
        public void UnknownActionRejected()
        {
            var service = Service();
            var result = service.Execute(Cmd("warp.drive", null));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(result.action, Is.EqualTo("warp.drive"));
        }

        [Test]
        public void MissingRequiredParameterRejected()
        {
            var service = Service();
            Assert.That(service.Execute(Cmd("profile.create", P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Execute(Cmd("profile.select", null)).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Execute(Cmd("input.set", P("steering", "0"))).status,
                Is.EqualTo(CommandResult.Rejected));
        }

        [Test]
        public void UnknownParameterRejected()
        {
            var service = Service();
            Assert.That(service.Execute(Cmd("profile.create", CreateParams(0, "en", "speed"))).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Execute(Cmd("settings.save", P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Rejected));
        }

        [TestCase("slot", "-1")]
        [TestCase("slot", "4")]
        [TestCase("slot", "x")]
        [TestCase("slot", " 1")]
        [TestCase("slot", "1.5")]
        [TestCase("language", "fr")]
        [TestCase("language", "EN")]
        [TestCase("language", "")]
        public void CreateRejectsInvalidParameterValues(string name, string value)
        {
            var service = Service();
            var before = StateJson(service);
            var result = service.Execute(Cmd("profile.create", P("slot", "0", "language", "en", name, value)));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(StateJson(service), Is.EqualTo(before));
        }

        [TestCase("musicVolume", "NaN")]
        [TestCase("musicVolume", "Infinity")]
        [TestCase("musicVolume", "1.5")]
        [TestCase("musicVolume", "-0.1")]
        [TestCase("musicVolume", "abc")]
        [TestCase("musicVolume", "")]
        [TestCase("effectsVolume", "2")]
        [TestCase("voiceVolume", "0,5")]
        [TestCase("camera", "first")]
        [TestCase("camera", "Chase")]
        [TestCase("reducedCameraShake", "yes")]
        [TestCase("reducedCameraShake", "1")]
        [TestCase("language", "fr")]
        public void SettingsUpdateRejectsInvalidValuesWithoutDraftChange(string name, string value)
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var before = StateJson(service);
            var parameters = UpdateParams();
            parameters[name] = value;
            var result = service.Execute(Cmd("settings.update", parameters));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(StateJson(service), Is.EqualTo(before));
        }

        [TestCase("steering", "2")]
        [TestCase("steering", "-1.5")]
        [TestCase("steering", "NaN")]
        [TestCase("steering", "Infinity")]
        [TestCase("steering", "x")]
        [TestCase("drift", "yes")]
        [TestCase("boost", "1")]
        [TestCase("gadget", "TRUE")]
        [TestCase("durationTicks", "0")]
        [TestCase("durationTicks", "151")]
        [TestCase("durationTicks", "1.5")]
        [TestCase("durationTicks", "x")]
        public void InputSetRejectsInvalidValues(string name, string value)
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            var before = StateJson(service);
            var parameters = InputParams();
            parameters[name] = value;
            var result = service.Execute(Cmd("input.set", parameters));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(StateJson(service), Is.EqualTo(before));
        }

        [Test]
        public void ParameterMapMutationAfterConstructionDoesNotChangeCommand()
        {
            var service = Service();
            var parameters = CreateParams(0, "en");
            var command = new GameCommand(1, RunId, "c1", "local", "profile.create", parameters);
            parameters["language"] = "fr";
            parameters["evil"] = "x";
            var result = service.Execute(command);
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().savedProfile.language, Is.EqualTo("en"));
        }

        [Test]
        public void DraftUpdateDoesNotPersist()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var fileBefore = File.ReadAllText(Primary(0));
            var result = service.Execute(Cmd("settings.update",
                UpdateParams(language: "he", camera: "overhead", shake: "true",
                    music: "0.1", effects: "0.2", voice: "0.3")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.pendingSettings, Is.True);
            Assert.That(state.language, Is.EqualTo("he"));
            Assert.That(state.draftProfile.language, Is.EqualTo("he"));
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"));
            Assert.That(state.draftProfile.reducedCameraShake, Is.True);
            Assert.That(state.draftProfile.musicVolume, Is.EqualTo(0.1f));
            Assert.That(state.draftProfile.effectsVolume, Is.EqualTo(0.2f));
            Assert.That(state.draftProfile.voiceVolume, Is.EqualTo(0.3f));
            Assert.That(state.savedProfile.language, Is.EqualTo("en"));
            Assert.That(state.savedProfile.camera, Is.EqualTo("chase"));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(fileBefore));
            Assert.That(Service().Snapshot().slots[0].profile.language, Is.EqualTo("en"));
        }

        [Test]
        public void ExplicitSaveSurvivesRestart()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            service.Execute(Cmd("settings.update", UpdateParams(language: "he", camera: "overhead")));
            var saved = service.Execute(Cmd("settings.save", null));
            Assert.That(saved.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.pendingSettings, Is.False);
            Assert.That(state.persistenceStatus, Is.EqualTo("saved"));
            Assert.That(state.savedProfile.language, Is.EqualTo("he"));
            Assert.That(state.savedProfile.camera, Is.EqualTo("overhead"));
            var reloaded = Service().Snapshot().slots[0].profile;
            Assert.That(reloaded.language, Is.EqualTo("he"));
            Assert.That(reloaded.camera, Is.EqualTo("overhead"));
        }

        [Test]
        public void SaveFailureRestoresPriorDraftAndLanguage()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var fileBefore = File.ReadAllText(Primary(0));
            service.Execute(Cmd("settings.update", UpdateParams(language: "he", camera: "overhead")));
            using (new FileStream(Primary(0), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = service.Execute(Cmd("settings.save", null));
                Assert.That(result.status, Is.EqualTo(CommandResult.Failed));
                Assert.That(result.code, Is.EqualTo("saveFailed"));
            }
            var state = service.Snapshot();
            Assert.That(state.persistenceStatus, Is.EqualTo("saveFailed"));
            Assert.That(state.pendingSettings, Is.False);
            Assert.That(state.language, Is.EqualTo("en"));
            Assert.That(state.draftProfile.language, Is.EqualTo("en"));
            Assert.That(state.draftProfile.camera, Is.EqualTo("chase"));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(fileBefore));
        }

        [Test]
        public void RecoveredProfileIsReadOnly()
        {
            var saved = ValidProfile(1);
            File.WriteAllText(Primary(1), "private corrupt content");
            File.WriteAllText(Backup(1), serializer.Serialize(saved));
            var service = Service();
            Assert.That(service.Snapshot().slots[1].status, Is.EqualTo("RecoveredFromBackup"));
            Assert.That(service.Execute(Cmd("profile.select", P("slot", "1"))).status,
                Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.persistenceStatus, Is.EqualTo("recovered"));
            foreach (var action in new[] { "settings.update", "settings.save", "camera.toggle" })
            {
                var result = service.Execute(Cmd(action,
                    action == "settings.update" ? UpdateParams() : null));
                Assert.That(result.status, Is.EqualTo(CommandResult.Rejected), action);
            }
            Assert.That(service.Snapshot().draftProfile.language, Is.EqualTo("en"));
            Assert.That(service.Snapshot().pendingSettings, Is.False);
            var cameraResult = service.Execute(Cmd("profile.select", P("slot", "1")));
            Assert.That(cameraResult.status, Is.EqualTo(CommandResult.Applied));
            EnterSandbox(service);
            var input = service.Execute(Cmd("input.set", InputParams(camera: "true")));
            Assert.That(input.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().draftProfile.camera, Is.EqualTo("chase"),
                "camera pulse must not edit a read-only draft");
        }

        [Test]
        public void UiLanguageRejectedWhileProfileSelected()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var result = service.Execute(Cmd("ui.language", P("language", "he")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Snapshot().language, Is.EqualTo("en"));
        }

        [Test]
        public void UiLanguageChangesDefaultOnProfilesScreen()
        {
            var service = Service();
            var result = service.Execute(Cmd("ui.language", P("language", "he")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().language, Is.EqualTo("he"));
        }

        [Test]
        public void RepeatedIdenticalCommandAppliesOnce()
        {
            var service = Service();
            var first = service.Execute(Cmd("profile.create", CreateParams(0, "en"), "same-id"));
            Assert.That(first.status, Is.EqualTo(CommandResult.Applied));
            var id = service.Snapshot().savedProfile.id;
            service.AdvanceTick();
            service.AdvanceTick();
            var second = service.Execute(Cmd("profile.create", CreateParams(0, "en"), "same-id"));
            Assert.That(second.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(second.duplicate, Is.True);
            Assert.That(second.tick, Is.EqualTo(first.tick), "duplicate keeps original tick");
            Assert.That(service.Snapshot().savedProfile.id, Is.EqualTo(id));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [Test]
        public void ExposedParameterMapRejectsMutationAndLeavesCommandUsable()
        {
            var service = Service();
            var command = Cmd("profile.create", CreateParams(0, "en"));
            var writable = (IDictionary<string, string>)command.Parameters;
            Assert.Throws<NotSupportedException>(() => writable.Add("slot", "1"));
            Assert.Throws<NotSupportedException>(() => writable["language"] = "fr");
            var result = service.Execute(command);
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().savedProfile.language, Is.EqualTo("en"));
        }

        [Test]
        public void ResubmittedIdentityWithCollidingConcatenationIsConflict()
        {
            var service = Service();
            var first = service.Execute(Cmd("ui.language", P("language", "en"), "id-x"));
            Assert.That(first.status, Is.EqualTo(CommandResult.Applied));
            // Concatenated envelopes collide here; structural comparison must not.
            var conflict = service.Execute(new GameCommand(1, RunId, "id-x", "local",
                "ui.language\nlanguage=en", new Dictionary<string, string>()));
            Assert.That(conflict.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(conflict.code, Is.EqualTo("identityConflict"));
        }

        [Test]
        public void NullAndEmptyParameterValuesAreDistinctIdentities()
        {
            var service = Service();
            var first = service.Execute(Cmd("settings.save", P("extra", null), "id-null"));
            Assert.That(first.status, Is.EqualTo(CommandResult.Rejected));
            var conflict = service.Execute(Cmd("settings.save", P("extra", ""), "id-null"));
            Assert.That(conflict.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(conflict.code, Is.EqualTo("identityConflict"));
        }

        [Test]
        public void ParameterMapOrderingDoesNotChangeIdentity()
        {
            var service = Service();
            var ordered = new Dictionary<string, string> { { "slot", "0" }, { "language", "en" } };
            var reversed = new Dictionary<string, string> { { "language", "en" }, { "slot", "0" } };
            var first = service.Execute(new GameCommand(1, RunId, "id-1", "local", "profile.create", ordered));
            var second = service.Execute(new GameCommand(1, RunId, "id-1", "local", "profile.create", reversed));
            Assert.That(first.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(second.duplicate, Is.True);
        }

        [Test]
        public void ConflictingPayloadRejected()
        {
            var service = Service();
            Assert.That(service.Execute(Cmd("profile.create", CreateParams(0, "en"), "id-x")).status,
                Is.EqualTo(CommandResult.Applied));
            var conflict = service.Execute(Cmd("profile.create", CreateParams(1, "he"), "id-x"));
            Assert.That(conflict.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(conflict.code, Is.EqualTo("identityConflict"));
            Assert.That(service.Snapshot().slots[1].status, Is.EqualTo("Missing"));
        }

        [Test]
        public void RejectedResultsAreCachedAndDuplicated()
        {
            var service = Service();
            var first = service.Execute(Cmd("profile.select", P("slot", "0"), "sel-1"));
            Assert.That(first.status, Is.EqualTo(CommandResult.Rejected));
            var again = service.Execute(Cmd("profile.select", P("slot", "0"), "sel-1"));
            Assert.That(again.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(again.duplicate, Is.True);
            Assert.That(again.tick, Is.EqualTo(first.tick));
        }

        [Test]
        public void LookupReturnsCopiedCachedResultOrNull()
        {
            var service = Service();
            var result = service.Execute(Cmd("input.clear", null, "ic-1"));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var looked = service.Lookup("ic-1");
            Assert.That(looked.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(looked.action, Is.EqualTo("input.clear"));
            Assert.That(looked.tick, Is.EqualTo(result.tick));
            looked.code = "corrupted";
            looked.duplicate = true;
            var again = service.Lookup("ic-1");
            Assert.That(again.code, Is.EqualTo(""));
            Assert.That(again.duplicate, Is.False);
            Assert.That(service.Lookup("never-seen"), Is.Null);
            Assert.That(service.Lookup(null), Is.Null);
        }

        [Test]
        public void CacheCapacityRejectsNewIdentitiesWithoutEviction()
        {
            var service = Service();
            for (var i = 0; i < 10000; i++)
            {
                var r = service.Execute(Cmd("unknown.act", null, "fill-" + i));
                Assert.That(r.status, Is.EqualTo(CommandResult.Rejected));
            }
            var overflow = service.Execute(Cmd("profile.create", CreateParams(0, "en"), "overflow"));
            Assert.That(overflow.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(overflow.code, Is.EqualTo("commandCapacity"));
            Assert.That(service.Snapshot().slots[0].status, Is.EqualTo("Missing"));
            var oldest = service.Lookup("fill-0");
            Assert.That(oldest, Is.Not.Null, "capacity must never evict a stored identity");
            var repeat = service.Execute(Cmd("unknown.act", null, "fill-0"));
            Assert.That(repeat.duplicate, Is.True);
        }

        [Test]
        public void HeldInputAppliesThenExpiresAfterDuration()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            var result = service.Execute(Cmd("input.set",
                InputParams(steering: "-1", drift: "true", boost: "true", duration: "2")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var now = service.Snapshot().input;
            Assert.That(now.steering, Is.EqualTo(-1f));
            Assert.That(now.drift, Is.True);
            Assert.That(now.boost, Is.True);
            service.AdvanceTick();
            var held = service.Snapshot().input;
            Assert.That(held.steering, Is.EqualTo(-1f));
            Assert.That(held.drift, Is.True);
            service.AdvanceTick();
            Assert.That(Neutral(service.Snapshot().input), Is.True);
        }

        [TestCase("-1")]
        [TestCase("1")]
        [TestCase("0.5")]
        [TestCase("-0.25")]
        public void SteeringExtremesAndFractionsAccepted(string steering)
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            var result = service.Execute(Cmd("input.set", InputParams(steering: steering)));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().input.steering,
                Is.EqualTo(float.Parse(steering, System.Globalization.CultureInfo.InvariantCulture)));
        }

        [TestCase(1)]
        [TestCase(150)]
        public void DurationBoundsAccepted(int duration)
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            var result = service.Execute(Cmd("input.set",
                InputParams(steering: "1", duration: duration.ToString())));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
        }

        [Test]
        public void TransientPulsesClearOnNextTick()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            service.Execute(Cmd("input.set",
                InputParams(gadget: "true", recover: "true", camera: "true")));
            var input = service.Snapshot().input;
            Assert.That(input.gadget, Is.True);
            Assert.That(input.recover, Is.True);
            Assert.That(input.changeCamera, Is.True);
            service.AdvanceTick();
            var cleared = service.Snapshot().input;
            Assert.That(cleared.gadget, Is.False);
            Assert.That(cleared.recover, Is.False);
            Assert.That(cleared.changeCamera, Is.False);
        }

        [Test]
        public void PausedTicksStillAdvanceAndExpireInput()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            service.Execute(Cmd("pause.toggle", null));
            Assert.That(service.Snapshot().paused, Is.True);
            var tickBefore = service.Snapshot().tick;
            var result = service.Execute(Cmd("input.set", InputParams(steering: "1", duration: "1")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied), "input.set works while paused");
            service.AdvanceTick();
            var state = service.Snapshot();
            Assert.That(state.tick, Is.EqualTo(tickBefore + 1));
            Assert.That(state.paused, Is.True);
            Assert.That(Neutral(state.input), Is.True);
        }

        [Test]
        public void InputSetOutsideSandboxRejected()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var result = service.Execute(Cmd("input.set", InputParams(steering: "1")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(Neutral(service.Snapshot().input), Is.True);
        }

        [Test]
        public void LeavingSandboxClearsInput()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            service.Execute(Cmd("input.set", InputParams(steering: "1", boost: "true", duration: "150")));
            var result = service.Execute(Cmd("ui.navigate", P("screen", "settings")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(Neutral(service.Snapshot().input), Is.True);
        }

        [Test]
        public void SelectionChangeClearsInputAndPendingEdits()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            CreateLoaded(service, 1, "he");
            service.Execute(Cmd("profile.select", P("slot", "0")));
            service.Execute(Cmd("settings.update", UpdateParams(camera: "overhead")));
            EnterSandbox(service);
            service.Execute(Cmd("input.set", InputParams(steering: "1", duration: "150")));
            var result = service.Execute(Cmd("profile.select", P("slot", "1")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(Neutral(state.input), Is.True);
            Assert.That(state.pendingSettings, Is.False);
            Assert.That(state.language, Is.EqualTo("he"));
            Assert.That(state.draftProfile.language, Is.EqualTo("he"));
            Assert.That(state.draftProfile.camera, Is.EqualTo("chase"));
        }

        [Test]
        public void InputClearNeutralizesEverywhereEvenPaused()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            service.Execute(Cmd("input.set", InputParams(steering: "1", drift: "true", duration: "150")));
            service.Execute(Cmd("pause.toggle", null));
            service.Execute(Cmd("input.set", InputParams(steering: "-1", boost: "true", duration: "150")));
            Assert.That(service.Snapshot().paused, Is.True);
            var result = service.Execute(Cmd("input.clear", null));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(Neutral(service.Snapshot().input), Is.True);
            service.ClearInput();
            Assert.That(Neutral(service.Snapshot().input), Is.True);
        }

        [Test]
        public void PauseToggleTogglesAndClearsInputInSandboxOnly()
        {
            var service = Service();
            Assert.That(service.Execute(Cmd("pause.toggle", null)).status,
                Is.EqualTo(CommandResult.Rejected), "pause.toggle requires sandbox");
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            service.Execute(Cmd("input.set", InputParams(steering: "1", duration: "150")));
            Assert.That(service.Execute(Cmd("pause.toggle", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.paused, Is.True);
            Assert.That(Neutral(state.input), Is.True);
            Assert.That(service.Execute(Cmd("pause.toggle", null)).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().paused, Is.False);
        }

        [Test]
        public void ChangeCameraPulseTogglesDraftCamera()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            var result = service.Execute(Cmd("input.set", InputParams(camera: "true")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"));
            Assert.That(state.pendingSettings, Is.True);
            Assert.That(state.savedProfile.camera, Is.EqualTo("chase"));
        }

        [Test]
        public void CameraToggleRequiresWritableSelectedProfile()
        {
            var service = Service();
            Assert.That(service.Execute(Cmd("camera.toggle", null)).status,
                Is.EqualTo(CommandResult.Rejected), "camera.toggle requires a selected profile");
            CreateLoaded(service, 0, "en");
            var result = service.Execute(Cmd("camera.toggle", null));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"));
            Assert.That(state.pendingSettings, Is.True);
            service.Execute(Cmd("camera.toggle", null));
            Assert.That(service.Snapshot().draftProfile.camera, Is.EqualTo("chase"));
        }

        [Test]
        public void PausePulseInInputSetTogglesServicePaused()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            EnterSandbox(service);
            service.Execute(Cmd("input.set", InputParams(pause: "true")));
            var state = service.Snapshot();
            Assert.That(state.paused, Is.True);
            Assert.That(state.input.pause, Is.True);
            service.AdvanceTick();
            Assert.That(service.Snapshot().input.pause, Is.False);
            Assert.That(service.Snapshot().paused, Is.True, "service pause is not a one-tick flag");
            service.Execute(Cmd("input.set", InputParams(pause: "true")));
            Assert.That(service.Snapshot().paused, Is.False);
        }

        [Test]
        public void DiscoveryReflectsCurrentState()
        {
            var service = Service();
            var initial = service.Discover();
            Assert.That(Available(initial, "input.set"), Is.False);
            Assert.That(Available(initial, "pause.toggle"), Is.False);
            Assert.That(Available(initial, "settings.update"), Is.False);
            Assert.That(Available(initial, "settings.save"), Is.False);
            Assert.That(Available(initial, "camera.toggle"), Is.False);
            Assert.That(Available(initial, "profile.select"), Is.False);
            Assert.That(Available(initial, "ui.language"), Is.True);
            Assert.That(Available(initial, "profile.create"), Is.True);
            Assert.That(Available(initial, "input.clear"), Is.True);

            CreateLoaded(service, 0, "en");
            var selected = service.Discover();
            Assert.That(Available(selected, "settings.update"), Is.True);
            Assert.That(Available(selected, "settings.save"), Is.True);
            Assert.That(Available(selected, "profile.select"), Is.True);
            Assert.That(Available(selected, "ui.language"), Is.False);
            Assert.That(Reason(selected, "input.set"), Is.Not.Empty);

            EnterSandbox(service);
            var sandbox = service.Discover();
            Assert.That(Available(sandbox, "input.set"), Is.True);
            Assert.That(Available(sandbox, "pause.toggle"), Is.True);
        }

        [Test]
        public void DiscoveryMarksRecoveredProfileReadOnly()
        {
            File.WriteAllText(Primary(0), "corrupt");
            File.WriteAllText(Backup(0), serializer.Serialize(ValidProfile(0)));
            var service = Service();
            service.Execute(Cmd("profile.select", P("slot", "0")));
            var descriptors = service.Discover();
            Assert.That(Available(descriptors, "settings.update"), Is.False);
            Assert.That(Available(descriptors, "settings.save"), Is.False);
            Assert.That(Available(descriptors, "camera.toggle"), Is.False);
            Assert.That(Reason(descriptors, "settings.update"), Is.EqualTo("readOnlyProfile"));
        }

        [Test]
        public void DiscoverySchemasCoverAllActions()
        {
            var service = Service();
            var actions = new HashSet<string>();
            foreach (var descriptor in service.Discover())
            {
                actions.Add(descriptor.action);
                Assert.That(descriptor.parameters, Is.Not.Null, descriptor.action);
            }
            Assert.That(actions, Is.EquivalentTo(new[]
            {
                "profile.create", "profile.select", "ui.navigate", "ui.language",
                "settings.update", "settings.save", "input.set", "input.clear",
                "camera.toggle", "pause.toggle",
                "race.start", "race.restart", "race.exit",
                "tutorial.start", "tutorial.replay", "tutorial.exit"
            }));
            var create = Descriptor(service.Discover(), "profile.create");
            Assert.That(RequiredNames(create), Is.EquivalentTo(new[] { "slot", "language" }));
            var update = Descriptor(service.Discover(), "settings.update");
            Assert.That(RequiredNames(update), Is.EquivalentTo(new[]
            {
                "language", "camera", "reducedCameraShake",
                "musicVolume", "effectsVolume", "voiceVolume"
            }));
            var input = Descriptor(service.Discover(), "input.set");
            Assert.That(RequiredNames(input), Is.EquivalentTo(new[]
            {
                "steering", "drift", "boost", "gadget", "recover",
                "changeCamera", "pause", "durationTicks"
            }));
        }

        [Test]
        public void MutatingSnapshotNeverChangesServiceState()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var snapshot = service.Snapshot();
            snapshot.draftProfile.coins = 9999;
            snapshot.draftProfile.unlockedVehicles.Clear();
            snapshot.savedProfile.id = "tampered";
            snapshot.slots[0].profile.id = "tampered";
            snapshot.slots[0].status = "Blocked";
            snapshot.slots.Add(new SlotState());
            snapshot.input.steering = 0.75f;
            snapshot.input.boost = true;
            snapshot.featureCoverage.tutorial = "implemented";
            snapshot.screen = "sandbox";
            var fresh = service.Snapshot();
            Assert.That(fresh.draftProfile.coins, Is.Zero);
            Assert.That(fresh.draftProfile.unlockedVehicles.Count, Is.EqualTo(2));
            Assert.That(fresh.savedProfile.id, Is.Not.EqualTo("tampered"));
            Assert.That(fresh.slots[0].profile.id, Is.EqualTo(fresh.savedProfile.id));
            Assert.That(fresh.slots[0].status, Is.EqualTo("Loaded"));
            Assert.That(fresh.slots.Count, Is.EqualTo(4));
            Assert.That(fresh.input.steering, Is.Zero);
            Assert.That(fresh.input.boost, Is.False);
            Assert.That(fresh.featureCoverage.tutorial, Is.EqualTo("notImplemented"));
            Assert.That(fresh.screen, Is.EqualTo("settings"));
        }

        [Test]
        public void SnapshotNeverExposesStoragePaths()
        {
            var service = Service();
            CreateLoaded(service, 0, "en");
            var json = StateJson(service);
            Assert.That(json, Does.Not.Contain(directory));
            Assert.That(json, Does.Not.Contain("profile-0.json"));
        }

        [Test]
        public void SetCaptureFailureMarksHealth()
        {
            var service = Service();
            Assert.That(service.Snapshot().captureValid, Is.True);
            service.SetCaptureFailure("sinkWriteFailed");
            var state = service.Snapshot();
            Assert.That(state.captureValid, Is.False);
            Assert.That(state.captureError, Is.EqualTo("sinkWriteFailed"));
        }

        [Test]
        public void TickAdvancesOnlyViaAdvanceTick()
        {
            var service = Service();
            var t0 = service.Snapshot().tick;
            service.Execute(Cmd("input.clear", null));
            service.Execute(Cmd("profile.select", P("slot", "0")));
            Assert.That(service.Snapshot().tick, Is.EqualTo(t0), "commands do not advance the tick");
            service.AdvanceTick();
            Assert.That(service.Snapshot().tick, Is.EqualTo(t0 + 1));
        }

        private FoundationService Service() { return new FoundationService(Store(), "en", RunId); }
        private ProfileStore Store() { return new ProfileStore(directory, serializer); }
        private string Primary(int slot) { return Path.Combine(directory, "profile-" + slot + ".json"); }
        private string Backup(int slot) { return Primary(slot) + ".bak"; }

        private void CreateLoaded(FoundationService service, int slot, string language)
        {
            var result = service.Execute(Cmd("profile.create", CreateParams(slot, language)));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied), "create setup failed");
        }

        private void EnterSandbox(FoundationService service)
        {
            var result = service.Execute(Cmd("ui.navigate", P("screen", "sandbox")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied), "sandbox setup failed");
        }

        private static GameCommand Cmd(string action, Dictionary<string, string> parameters)
        {
            return Cmd(action, parameters, Guid.NewGuid().ToString("N"));
        }

        private static GameCommand Cmd(string action, Dictionary<string, string> parameters, string commandId)
        {
            return new GameCommand(1, RunId, commandId, "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private static Dictionary<string, string> CreateParams(int slot, string language)
        {
            return P("slot", slot.ToString(), "language", language);
        }

        private static Dictionary<string, string> CreateParams(int slot, string language, string extraKey)
        {
            var map = CreateParams(slot, language);
            map[extraKey] = "x";
            return map;
        }

        private static Dictionary<string, string> UpdateParams(string language = "en",
            string camera = "chase", string shake = "false",
            string music = "0.5", string effects = "0.5", string voice = "0.5")
        {
            return P("language", language, "camera", camera, "reducedCameraShake", shake,
                "musicVolume", music, "effectsVolume", effects, "voiceVolume", voice);
        }

        private static Dictionary<string, string> InputParams(string steering = "0",
            string drift = "false", string boost = "false", string gadget = "false",
            string recover = "false", string camera = "false", string pause = "false",
            string duration = "5")
        {
            return P("steering", steering, "drift", drift, "boost", boost, "gadget", gadget,
                "recover", recover, "changeCamera", camera, "pause", pause,
                "durationTicks", duration);
        }

        private static bool Neutral(InputState input)
        {
            return input.steering == 0f && !input.drift && !input.boost && !input.gadget
                && !input.recover && !input.changeCamera && !input.pause;
        }

        private static bool Available(IReadOnlyList<CommandDescriptor> descriptors, string action)
        {
            return Descriptor(descriptors, action).available;
        }

        private static string Reason(IReadOnlyList<CommandDescriptor> descriptors, string action)
        {
            return Descriptor(descriptors, action).reason;
        }

        private static CommandDescriptor Descriptor(IReadOnlyList<CommandDescriptor> descriptors, string action)
        {
            foreach (var descriptor in descriptors)
                if (descriptor.action == action) return descriptor;
            Assert.Fail("No descriptor for " + action);
            return null;
        }

        private static List<string> RequiredNames(CommandDescriptor descriptor)
        {
            var names = new List<string>();
            foreach (var parameter in descriptor.parameters)
                if (parameter.required) names.Add(parameter.name);
            return names;
        }

        private string StateJson(FoundationService service)
        {
#if UNITY_5_3_OR_NEWER
            return JsonUtility.ToJson(service.Snapshot());
#else
            return JsonSerializer.Serialize(service.Snapshot(), JsonOptions);
#endif
        }

        private string FilesJson()
        {
            var files = new List<string>();
            foreach (var path in Directory.GetFiles(directory))
                files.Add(Path.GetFileName(path) + "=" + File.ReadAllText(path));
            files.Sort(StringComparer.Ordinal);
#if UNITY_5_3_OR_NEWER
            return JsonUtility.ToJson(new FileMap { files = files });
#else
            return JsonSerializer.Serialize(files, JsonOptions);
#endif
        }

        private static ProfileSave ValidProfile(int slot)
        {
            return new ProfileSave
            {
                schemaVersion = 1,
                id = "11111111-1111-4111-8111-111111111111",
                slot = slot,
                avatar = "fox",
                colorIndex = 0,
                language = "en",
                camera = "chase",
                musicVolume = 0.65f,
                effectsVolume = 0.85f,
                voiceVolume = 1f,
                coins = 0,
                unlockedVehicles = new List<string> { "car-01", "motorcycle-01" }
            };
        }

        [Serializable]
        private sealed class FileMap
        {
            public List<string> files;
        }

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile) { return JsonUtility.ToJson(profile); }
            public ProfileSave Deserialize(string json) { return JsonUtility.FromJson<ProfileSave>(json); }
#else
            public string Serialize(ProfileSave profile) { return JsonSerializer.Serialize(profile, JsonOptions); }
            public ProfileSave Deserialize(string json) { return JsonSerializer.Deserialize<ProfileSave>(json, JsonOptions); }
#endif
        }

#if !UNITY_5_3_OR_NEWER
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions { IncludeFields = true };
#endif
    }
}
