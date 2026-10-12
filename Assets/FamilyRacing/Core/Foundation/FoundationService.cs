using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using FamilyRacing.Core.Tutorial;

namespace FamilyRacing.Core.Foundation
{
    // Pure application service behind the foundation shell: profile
    // selection/settings drafts, a bounded input sandbox, navigation, and the
    // command/result/discovery contracts agents drive. No Unity or network
    // dependencies; the observation tick only advances via AdvanceTick and is
    // not a race clock. The LAN session half lives in
    // FoundationService.Network.cs.
    public sealed partial class FoundationService
    {
        public const int SupportedProtocolVersion = 1;
        public const string RequiredParticipant = "local";
        public const int IdentityCapacity = 10000;
        public const int MaxDurationTicks = 150;

        private const string ScreenProfiles = "profiles";
        private const string ScreenSettings = "settings";
        private const string ScreenSandbox = "sandbox";
        private const string ScreenPractice = "practice";
        private const string ScreenDriving = "driving";
        private const int MaxDrivingEvents = 128;

        private sealed class ParameterSpec
        {
            internal string Name;
            internal string Type;
            internal string[] AllowedValues;

            internal ParameterSpec(string name, string type, string[] allowedValues)
            {
                Name = name;
                Type = type;
                AllowedValues = allowedValues;
            }
        }

        private sealed class SlotInfo
        {
            internal string Status;
            internal ProfileSave Profile;
        }

        private sealed class CachedResult
        {
            internal CommandEnvelope Envelope;
            internal CommandResult Result;
        }

        // Immutable command snapshot used for identity matching. Structural
        // ordinal comparison of every header plus exact parameter
        // keys/values/count sorted by key — concatenating strings could
        // collide and cannot distinguish null from empty values.
        private sealed class CommandEnvelope
        {
            private readonly int protocolVersion;
            private readonly string runId;
            private readonly string participantId;
            private readonly string action;
            private readonly KeyValuePair<string, string>[] parameters;

            internal CommandEnvelope(GameCommand command)
            {
                protocolVersion = command.ProtocolVersion;
                runId = command.RunId;
                participantId = command.ParticipantId;
                action = command.Action;
                parameters = command.Parameters
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();
            }

            internal bool Matches(CommandEnvelope other)
            {
                if (other == null
                    || protocolVersion != other.protocolVersion
                    || !string.Equals(runId, other.runId, StringComparison.Ordinal)
                    || !string.Equals(participantId, other.participantId, StringComparison.Ordinal)
                    || !string.Equals(action, other.action, StringComparison.Ordinal)
                    || parameters.Length != other.parameters.Length)
                    return false;
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (!string.Equals(parameters[i].Key, other.parameters[i].Key, StringComparison.Ordinal)
                        || !string.Equals(parameters[i].Value, other.parameters[i].Value,
                            StringComparison.Ordinal))
                        return false;
                }
                return true;
            }
        }

        private static readonly string[] Languages = { "en", "he" };
        private static readonly string[] Cameras = { "chase", "overhead" };
        // "driving" is never a navigation target: race.start enters it and
        // race.exit is the only way back out.
        private static readonly string[] Screens = { "profiles", "settings", "sandbox", "practice" };
        private static readonly string[] Slots = { "0", "1", "2", "3" };
        private static readonly string[] Bools = { "true", "false" };

        private static readonly KeyValuePair<string, ParameterSpec[]>[] Schemas =
        {
            Schema("profile.create",
                Param("slot", "integer", Slots), Param("language", "string", Languages)),
            Schema("profile.select", Param("slot", "integer", Slots)),
            Schema("ui.navigate", Param("screen", "string", Screens)),
            Schema("ui.language", Param("language", "string", Languages)),
            Schema("settings.update",
                Param("language", "string", Languages), Param("camera", "string", Cameras),
                Param("reducedCameraShake", "boolean", Bools), Param("musicVolume", "decimal", null),
                Param("effectsVolume", "decimal", null), Param("voiceVolume", "decimal", null)),
            Schema("settings.save"),
            Schema("input.set",
                Param("steering", "decimal", null), Param("drift", "boolean", Bools),
                Param("boost", "boolean", Bools), Param("gadget", "boolean", Bools),
                Param("recover", "boolean", Bools), Param("changeCamera", "boolean", Bools),
                Param("pause", "boolean", Bools), Param("durationTicks", "integer", null)),
            Schema("input.clear"),
            Schema("camera.toggle"),
            Schema("pause.toggle"),
            Schema("race.start",
                Param("vehicleId", "string", null), Param("eventId", "string", null)),
            Schema("race.restart"),
            Schema("race.exit"),
            Schema("tutorial.start", Param("vehicleId", "string", null)),
            Schema("tutorial.replay"),
            Schema("tutorial.exit"),
            // LAN session surface (Core/Networking): asynchronous commands
            // return accepted and complete through DrainNetworkOutcomes +
            // CompleteNetworkCommand on a later boundary.
            Schema("lan.host", Param("port", "integer", null)),
            Schema("lan.discover"),
            Schema("lan.discovery.stop"),
            Schema("lan.join",
                Param("address", "string", null), Param("port", "integer", null)),
            Schema("lan.configure",
                Param("eventId", "string", null), Param("classId", "string", null)),
            Schema("lan.vehicle", Param("vehicleId", "string", null)),
            Schema("lan.ready", Param("ready", "boolean", Bools)),
            Schema("lan.start"),
            Schema("lan.pause.request"),
            Schema("lan.pause.set", Param("paused", "boolean", Bools)),
            Schema("lan.reconnect"),
            Schema("lan.results.request", Param("raceId", "string", null)),
            Schema("lan.results.retrySave"),
            Schema("lan.rematch"),
            Schema("lan.leave")
        };

        private static readonly Dictionary<string, ParameterSpec[]> SchemaMap =
            BuildSchemaMap();

        private readonly ProfileStore store;
        private readonly string runId;
        private readonly SlotInfo[] slots = new SlotInfo[ProfileValidation.SlotCount];
        private readonly Dictionary<string, CachedResult> results =
            new Dictionary<string, CachedResult>(StringComparer.Ordinal);

        private string language;
        private string screen = ScreenProfiles;
        private int activeSlot = -1;
        private bool paused;
        private long tick;
        private bool pendingSettings;
        private string persistenceStatus = "";
        private bool captureValid = true;
        private string captureError = "";
        private ProfileSave savedProfile;
        private ProfileSave draftProfile;
        private DriverInput input;
        private int holdTicksRemaining;
        private PresentationState presentation = new PresentationState();
        private DrivingContentCatalog drivingContent;
        private DrivingSession driving;
        private TutorialDefinition tutorialContent;
        private DrivingTutorial tutorial;
        private long raceCounter;
        private readonly List<DrivingEvent> drivingEvents = new List<DrivingEvent>();
        // Honest capacity accounting: evicted events are never silent —
        // every drop moves this marker into the published state.
        private int droppedDrivingEvents;
        private DrivingCameraState cameraFacts = new DrivingCameraState();

        public FoundationService(ProfileStore store, string defaultLanguage, string runId)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (!ProfileValidation.IsSupportedLanguage(defaultLanguage))
                throw new ArgumentException("Default language must be \"en\" or \"he\".", "defaultLanguage");
            if (string.IsNullOrEmpty(runId))
                throw new ArgumentException("Run id must not be empty.", "runId");
            this.store = store;
            this.runId = runId;
            language = defaultLanguage;
            for (var slot = 0; slot < ProfileValidation.SlotCount; slot++)
            {
                var load = store.Load(slot);
                slots[slot] = new SlotInfo
                {
                    Status = load.Status.ToString(),
                    Profile = CopyProfile(load.Profile)
                };
            }
        }

        public bool Paused { get { return paused; } }
        public bool Racing { get { return driving != null; } }

        // Content is versioned separately from the build: the runtime
        // injects the validated catalog before race.start can report
        // available.
        public string ContentVersion
        {
            get { return drivingContent != null ? drivingContent.Version : "none"; }
        }

        public void SetDrivingContent(DrivingContentCatalog catalog)
        {
            drivingContent = catalog;
            if (lanSession != null) lanSession.SetContent(catalog);
        }

        // The validated tutorial definition travels with the run like the
        // driving catalog; tutorial.start reports contentUnavailable until
        // the runtime injects one.
        public void SetTutorialDefinition(TutorialDefinition definition)
        {
            if (definition == null)
                throw new ArgumentException("Invalid tutorial definition: null");
            if (!definition.Validate(out var error))
                throw new ArgumentException("Invalid tutorial definition: " + error);
            tutorialContent = definition.Copy();
        }

        // Runtime camera facts travel with the state so captures can
        // reconstruct framing; the sim never reads them.
        public void SetDrivingCameraFacts(DrivingCameraState facts)
        {
            cameraFacts = facts != null ? facts.Copy() : new DrivingCameraState();
        }

        // One simulated driving boundary. Called before AdvanceTick so the
        // held-input sample (including its last leased tick) is consumed by
        // the session before the observation tick expires leases. Frozen
        // while paused: observation ticks continue, simulation time does not.
        public void StepDriving(IMotorWorld world)
        {
            if (driving == null || paused || world == null) return;
            var result = driving.Step(input, world);
            AppendDrivingEvents(result.events);
            if (tutorial != null) AdvanceTutorial(result);
        }

        public CommandResult Execute(GameCommand command)
        {
            if (command == null) throw new ArgumentNullException("command");
            if (string.IsNullOrEmpty(command.CommandId) || string.IsNullOrEmpty(command.RunId)
                || string.IsNullOrEmpty(command.ParticipantId) || string.IsNullOrEmpty(command.Action))
                return Result(command, CommandResult.Rejected, "invalidCommand");

            if (results.TryGetValue(command.CommandId, out var cached))
                return new CommandEnvelope(command).Matches(cached.Envelope)
                    ? Duplicated(cached.Result)
                    : Result(command, CommandResult.Rejected, "identityConflict");
            if (results.Count >= IdentityCapacity)
                return Result(command, CommandResult.Rejected, "commandCapacity");
            if (command.ProtocolVersion != SupportedProtocolVersion)
                return Record(command, Result(command, CommandResult.Rejected, "unsupportedProtocol"));
            if (command.RunId != runId)
                return Record(command, Result(command, CommandResult.Rejected, "wrongRun"));
            if (command.ParticipantId != RequiredParticipant)
                return Record(command, Result(command, CommandResult.Rejected, "wrongParticipant"));
            if (!SchemaMap.TryGetValue(command.Action, out var schema))
                return Record(command, Result(command, CommandResult.Rejected, "unknownAction"));
            var parameterError = ValidateParameters(command, schema);
            if (parameterError != null)
                return Record(command, Result(command, CommandResult.Rejected, parameterError));
            return Record(command, Dispatch(command));
        }

        // Read-only: returns a copied cached terminal result or null without
        // consuming identities and without routing another mutation.
        public CommandResult Lookup(string commandId)
        {
            if (string.IsNullOrEmpty(commandId)) return null;
            return results.TryGetValue(commandId, out var cached) ? cached.Result.Copy() : null;
        }

        public FoundationState Snapshot()
        {
            var state = new FoundationState
            {
                stateSchemaVersion = NetworkSettings.StateSchemaVersion,
                contentVersion = ContentVersion,
                screen = screen,
                language = language,
                activeSlot = activeSlot,
                paused = paused,
                tick = tick,
                input = new InputState
                {
                    steering = input.Steering,
                    drift = input.Drift,
                    boost = input.Boost,
                    gadget = input.Gadget,
                    recover = input.Recover,
                    changeCamera = input.ChangeCamera,
                    pause = input.Pause
                },
                slots = new List<SlotState>(),
                savedProfile = CopyProfile(savedProfile),
                draftProfile = CopyProfile(draftProfile),
                pendingSettings = pendingSettings,
                persistenceStatus = persistenceStatus,
                captureValid = captureValid,
                captureError = captureError,
                featureCoverage = new FeatureCoverage(),
                presentation = CopyPresentation(presentation),
                driving = null,
                drivingEvents = new List<DrivingEvent>(),
                droppedDrivingEvents = droppedDrivingEvents,
                network = NetworkSnapshot(),
                networkRace = null,
                prediction = null,
                remotePresentation = null
            };
            if (driving != null)
            {
                state.driving = driving.Snapshot();
                state.driving.camera = cameraFacts != null ? cameraFacts.Copy() : new DrivingCameraState();
                state.driving.tutorial = tutorial != null ? tutorial.Snapshot() : null;
                FillDrivingControls(state.driving);
            }
            foreach (var ev in drivingEvents)
                state.drivingEvents.Add(ev.Copy());
            for (var slot = 0; slot < slots.Length; slot++)
            {
                state.slots.Add(new SlotState
                {
                    slot = slot,
                    status = slots[slot].Status,
                    profile = CopyProfile(slots[slot].Profile)
                });
            }
            return state;
        }

        public IReadOnlyList<CommandDescriptor> Discover()
        {
            var descriptors = new List<CommandDescriptor>();
            foreach (var entry in Schemas)
            {
                Availability(entry.Key, out var available, out var reason);
                var parameters = new List<CommandParameterDescriptor>();
                foreach (var spec in entry.Value)
                {
                    parameters.Add(new CommandParameterDescriptor
                    {
                        name = spec.Name,
                        type = spec.Type,
                        required = true,
                        allowedValues = spec.AllowedValues == null
                            ? null
                            : new List<string>(spec.AllowedValues)
                    });
                }
                descriptors.Add(new CommandDescriptor
                {
                    action = entry.Key,
                    available = available,
                    reason = reason,
                    parameters = parameters
                });
            }
            return descriptors;
        }

        // The observation tick advances even while paused; it is not a race
        // clock. Holds expire after their bounded duration and one-tick
        // actions clear on the next tick.
        public void AdvanceTick()
        {
            tick++;
            var steering = 0f;
            var drift = false;
            var boost = false;
            if (holdTicksRemaining > 0)
            {
                holdTicksRemaining--;
                if (holdTicksRemaining > 0)
                {
                    steering = input.Steering;
                    drift = input.Drift;
                    boost = input.Boost;
                }
            }
            input = new DriverInput(steering, drift, boost, false, false, false, false);
        }

        // Neutralizes held input and cancels queued driving actions (pause,
        // lifecycle, focus loss, disconnect and explicit input.clear all
        // funnel through here).
        public void ClearInput()
        {
            holdTicksRemaining = 0;
            input = new DriverInput(0f, false, false, false, false, false, false);
            if (driving != null)
            {
                driving.CancelPending("cleared");
                DrainDrivingEvents();
            }
        }

        public void SetCaptureFailure(string code)
        {
            captureValid = false;
            captureError = code ?? "";
        }

        // Runtime adapters publish presentation facts through here; the
        // service stores an independent copy so callers can reuse objects.
        public void SetPresentation(PresentationState value)
        {
            presentation = CopyPresentation(value) ?? new PresentationState();
        }

        private static PresentationState CopyPresentation(PresentationState value)
        {
            return value?.Copy();
        }

        private CommandResult Dispatch(GameCommand command)
        {
            try
            {
                // LAN commands route to the session service; pause.toggle
                // is a shared toggle on the host and a pause request on a
                // client while a session is active.
                if (command.Action.StartsWith("lan.", StringComparison.Ordinal))
                    return NetworkExecute(command);
                if (command.Action == "pause.toggle" && LanActive())
                    return NetworkExecute(command);
                // Solo-only lifecycle commands are refused inside a LAN
                // session so they can never tear down the shared state.
                if (LanActive() && SoloBlockedDuringLan(command.Action))
                    return Result(command, CommandResult.Rejected, "networkActive");
                switch (command.Action)
                {
                    case "profile.create": return CreateProfile(command);
                    case "profile.select": return SelectProfile(command);
                    case "ui.navigate": return Navigate(command);
                    case "ui.language": return SetLanguage(command);
                    case "settings.update": return UpdateSettings(command);
                    case "settings.save": return SaveSettings(command);
                    case "input.set": return SetInput(command);
                    case "input.clear": ClearInput(); return Result(command, CommandResult.Applied, "");
                    case "camera.toggle": return ToggleCamera(command);
                    case "pause.toggle": return TogglePause(command);
                    case "race.start": return RaceStart(command);
                    case "race.restart": return RaceRestart(command);
                    case "race.exit": return RaceExit(command);
                    case "tutorial.start": return TutorialStart(command);
                    case "tutorial.replay": return TutorialReplay(command);
                    case "tutorial.exit": return TutorialExit(command);
                    default: return Result(command, CommandResult.Rejected, "unknownAction");
                }
            }
            catch (Exception)
            {
                // Command paths must never leak raw exceptions, save contents,
                // or filesystem paths into results.
                return Result(command, CommandResult.Failed, "internalError");
            }
        }

        // Commands that only make sense for a solo run; while a LAN
        // session is active they reject with networkActive instead of
        // mutating profile/identity or a parallel solo race.
        private static bool SoloBlockedDuringLan(string action)
        {
            switch (action)
            {
                case "profile.create":
                case "profile.select":
                case "race.start":
                case "race.restart":
                case "tutorial.start":
                case "tutorial.replay":
                case "tutorial.exit":
                    return true;
                default:
                    return false;
            }
        }

        private CommandResult CreateProfile(GameCommand command)
        {
            var slot = ParseInt(command.Parameters["slot"]);
            if (slot < 0 || slot >= ProfileValidation.SlotCount)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            if (slots[slot].Status != "Missing")
                return Result(command, CommandResult.Rejected, "unavailable");
            ProfileSave created;
            try
            {
                created = ProfileFactory.Create(slot, command.Parameters["language"]);
                store.Save(created);
            }
            catch (Exception)
            {
                // Failed creations leave selection and prior files unchanged.
                return Result(command, CommandResult.Failed, "saveFailed");
            }
            EndRace("lifecycle", command);
            slots[slot].Status = "Loaded";
            slots[slot].Profile = CopyProfile(created);
            savedProfile = CopyProfile(created);
            draftProfile = CopyProfile(created);
            activeSlot = slot;
            language = created.language;
            screen = ScreenSettings;
            pendingSettings = false;
            persistenceStatus = "saved";
            // Same stranded-pause hazard as ui.navigate: the settings
            // screen offers no pause.toggle, so a paused sandbox must not
            // carry the flag across profile creation.
            paused = false;
            ClearInput();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult SelectProfile(GameCommand command)
        {
            var slot = ParseInt(command.Parameters["slot"]);
            if (slot < 0 || slot >= ProfileValidation.SlotCount)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            var info = slots[slot];
            if (info.Status != "Loaded" && info.Status != "RecoveredFromBackup")
                return Result(command, CommandResult.Rejected, "unavailable");
            EndRace("lifecycle", command);
            savedProfile = CopyProfile(info.Profile);
            draftProfile = CopyProfile(info.Profile);
            activeSlot = slot;
            language = info.Profile.language;
            pendingSettings = false;
            persistenceStatus = info.Status == "RecoveredFromBackup" ? "recovered" : "";
            ClearInput();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult Navigate(GameCommand command)
        {
            var target = command.Parameters["screen"];
            // A running race is never abandoned silently: race.exit is the
            // only way off the driving screen.
            if (screen == ScreenDriving)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (target != ScreenProfiles && activeSlot < 0)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (target != screen)
            {
                // Leaving the sandbox clears input; entering it starts neutral.
                if (screen == ScreenSandbox || target == ScreenSandbox) ClearInput();
                // Pause is a sandbox/driving affordance: landing on any
                // other screen with paused=true would strand it where
                // pause.toggle is unavailable (the same hazard EndRace
                // clears for). "driving" is unreachable here, but the
                // predicate is written for the invariant, not the schema.
                if (target != ScreenSandbox && target != ScreenDriving)
                    paused = false;
                screen = target;
            }
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult SetLanguage(GameCommand command)
        {
            // Without a selected profile this only changes the default language
            // used for explicit profile creation.
            if (activeSlot >= 0)
                return Result(command, CommandResult.Rejected, "unavailable");
            language = command.Parameters["language"];
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult UpdateSettings(GameCommand command)
        {
            if (!WritableSelected())
                return Result(command, CommandResult.Rejected, "unavailable");
            var music = ParseDecimal(command.Parameters["musicVolume"]);
            var effects = ParseDecimal(command.Parameters["effectsVolume"]);
            var voice = ParseDecimal(command.Parameters["voiceVolume"]);
            if (!InUnitRange(music) || !InUnitRange(effects) || !InUnitRange(voice))
                return Result(command, CommandResult.Rejected, "invalidParameter");
            var draft = CopyProfile(savedProfile);
            draft.language = command.Parameters["language"];
            draft.camera = command.Parameters["camera"];
            draft.reducedCameraShake = command.Parameters["reducedCameraShake"] == "true";
            draft.musicVolume = music;
            draft.effectsVolume = effects;
            draft.voiceVolume = voice;
            draftProfile = draft;
            language = draft.language;
            pendingSettings = true;
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult SaveSettings(GameCommand command)
        {
            if (!WritableSelected())
                return Result(command, CommandResult.Rejected, "unavailable");
            var candidate = CopyProfile(draftProfile);
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                // Restore the prior saved draft and visible language; the
                // persisted profile is unchanged by the store's safe save.
                draftProfile = CopyProfile(savedProfile);
                language = savedProfile.language;
                pendingSettings = false;
                persistenceStatus = "saveFailed";
                return Result(command, CommandResult.Failed, "saveFailed");
            }
            savedProfile = CopyProfile(candidate);
            draftProfile = CopyProfile(candidate);
            slots[activeSlot].Profile = CopyProfile(candidate);
            pendingSettings = false;
            persistenceStatus = "saved";
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult SetInput(GameCommand command)
        {
            var drivingActive = screen == ScreenDriving && driving != null;
            if (screen != ScreenSandbox && !drivingActive)
                return Result(command, CommandResult.Rejected, "unavailable");
            var steering = ParseDecimal(command.Parameters["steering"]);
            if (float.IsNaN(steering) || float.IsInfinity(steering) || steering < -1f || steering > 1f)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            var duration = ParseInt(command.Parameters["durationTicks"]);
            if (duration < 1 || duration > MaxDurationTicks)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            var drift = IsTrue(command.Parameters["drift"]);
            var boost = IsTrue(command.Parameters["boost"]);
            var gadget = IsTrue(command.Parameters["gadget"]);
            var recover = IsTrue(command.Parameters["recover"]);
            var changeCamera = IsTrue(command.Parameters["changeCamera"]);
            var pausePulse = IsTrue(command.Parameters["pause"]);
            // Gadget actions are explicitly unavailable while driving
            // (gadget gameplay is deferred); the whole sample is refused
            // before any input state is touched.
            if (drivingActive && gadget)
                return Result(command, CommandResult.Rejected, "unavailable");
            // A recovery press is latched with its command identity until a
            // simulation boundary consumes it; overflow refuses before the
            // held-input sample mutates.
            if (drivingActive && recover)
            {
                string queueError;
                if (!driving.QueuePending("recover", command.CommandId, out queueError))
                    return Result(command, CommandResult.Rejected, queueError);
                DrainDrivingEvents();
            }
            input = new DriverInput(steering, drift, boost, gadget, recover, changeCamera, pausePulse);
            holdTicksRemaining = duration;
            // Gadget/recover/changeCamera/pause are one-tick pulses. A camera
            // pulse also toggles a writable draft camera; a pause pulse
            // toggles the service pause state.
            if (changeCamera && WritableSelected())
            {
                draftProfile.camera = OtherCamera(draftProfile.camera);
                pendingSettings = true;
            }
            // A pause pulse toggles service pause and (entering pause)
            // cancels queued actions; the pulse itself stays observable in
            // this tick's input sample, unlike pause.toggle which clears.
            if (pausePulse)
            {
                paused = !paused;
                if (paused && driving != null)
                {
                    driving.CancelPending("paused");
                    DrainDrivingEvents();
                }
            }
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult ToggleCamera(GameCommand command)
        {
            if (!WritableSelected())
                return Result(command, CommandResult.Rejected, "unavailable");
            draftProfile.camera = OtherCamera(draftProfile.camera);
            pendingSettings = true;
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult TogglePause(GameCommand command)
        {
            if (screen != ScreenSandbox && screen != ScreenDriving)
                return Result(command, CommandResult.Rejected, "unavailable");
            ApplyPauseToggle();
            return Result(command, CommandResult.Applied, "");
        }

        // Entering pause neutralizes held input and cancels queued actions;
        // the simulation simply stops stepping so simulation and elapsed
        // race time freeze while observation ticks continue.
        private void ApplyPauseToggle()
        {
            paused = !paused;
            if (paused && driving != null)
            {
                driving.CancelPending("paused");
                DrainDrivingEvents();
            }
            ClearInput();
        }

        // --- driving session commands --------------------------------------

        private CommandResult RaceStart(GameCommand command)
        {
            if (drivingContent == null)
                return Result(command, CommandResult.Rejected, "contentUnavailable");
            if (activeSlot < 0)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (driving != null)
                return Result(command, CommandResult.Rejected, "raceActive");
            var vehicleId = command.Parameters["vehicleId"];
            var eventId = command.Parameters["eventId"];
            VehicleTuning tuning;
            EventDefinition evt;
            if (!drivingContent.Vehicles.TryGetValue(vehicleId, out tuning))
                return Result(command, CommandResult.Rejected, "unknownVehicle");
            if (!drivingContent.Events.TryGetValue(eventId, out evt))
                return Result(command, CommandResult.Rejected, "unknownEvent");
            // The tutorial free-drive stage is internal content: it may
            // only be opened by tutorial.start, never as a bare race.
            if (tutorialContent != null
                && eventId == tutorialContent.freeDriveEventId)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (!evt.AllowsVehicle(vehicleId))
                return Result(command, CommandResult.Rejected, "vehicleNotForEvent");
            driving = CreateSession(tuning, evt, command);
            screen = ScreenDriving;
            ClearInput();
            DrainDrivingEvents();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult RaceRestart(GameCommand command)
        {
            if (driving == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            VehicleTuning tuning;
            if (!drivingContent.Vehicles.TryGetValue(driving.VehicleId, out tuning))
                return Result(command, CommandResult.Rejected, "contentUnavailable");
            // Inside a tutorial retry stays on the current stage: the
            // free-drive lesson event or the one-lap trial. Completed
            // lessons are kept; a graduated tutorial retries the trial.
            // Lookups are TryGetValue (not the throwing indexer): a
            // catalog that cannot supply the stage rejects honestly
            // instead of throwing inside a command boundary.
            var eventId = tutorial != null
                ? (tutorial.Phase == DrivingTutorial.PhaseLessons
                    ? tutorial.FreeDriveEventId : tutorial.TrialEventId)
                : driving.EventId;
            EventDefinition evt;
            if (!drivingContent.Events.TryGetValue(eventId, out evt))
                return Result(command, CommandResult.Rejected, "contentUnavailable");
            // The old identity ends; retry creates a new race identity
            // inside the same run. Duplicate delivery replays the cached
            // result and never reaches here.
            driving.CancelPending("restarted");
            driving.EmitLifecycle("race.ended", command.CommandId, "restarted");
            DrainDrivingEvents();
            driving = CreateSession(tuning, evt, command);
            if (tutorial != null)
            {
                tutorial.Restart();
                driving.EmitLifecycle("tutorial.cue", command.CommandId,
                    tutorial.Snapshot().cueId);
            }
            screen = ScreenDriving;
            ClearInput();
            DrainDrivingEvents();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult RaceExit(GameCommand command)
        {
            if (driving == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            EndRace("raceExit", command);
            screen = ScreenPractice;
            return Result(command, CommandResult.Applied, "");
        }

        // --- tutorial commands ----------------------------------------------
        // Ordered onboarding: tutorial.start opens the free-drive lesson
        // stage, tutorial.replay re-announces the current cue without any
        // progression, and tutorial.exit abandons the run. There is no
        // tutorial.complete or checkpoint-teleport action — lessons and the
        // graduation lap advance only from genuine driving events consumed
        // in AdvanceTutorial.

        private CommandResult TutorialStart(GameCommand command)
        {
            if (drivingContent == null || tutorialContent == null)
                return Result(command, CommandResult.Rejected, "contentUnavailable");
            if (activeSlot < 0)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (driving != null)
                return Result(command, CommandResult.Rejected, "raceActive");
            var vehicleId = command.Parameters["vehicleId"];
            VehicleTuning tuning;
            EventDefinition evt;
            EventDefinition trial;
            if (!drivingContent.Vehicles.TryGetValue(vehicleId, out tuning))
                return Result(command, CommandResult.Rejected, "unknownVehicle");
            if (!drivingContent.Events.TryGetValue(tutorialContent.freeDriveEventId, out evt))
                return Result(command, CommandResult.Rejected, "unknownEvent");
            // The trial is resolved up front, fail-closed: lessons can
            // only be entered when the graduation lap they unlock is
            // guaranteed to exist and to accept the chosen vehicle.
            if (!drivingContent.Events.TryGetValue(tutorialContent.trialEventId, out trial))
                return Result(command, CommandResult.Rejected, "unknownEvent");
            if (!evt.AllowsVehicle(vehicleId) || !trial.AllowsVehicle(vehicleId))
                return Result(command, CommandResult.Rejected, "vehicleNotForEvent");
            driving = CreateSession(tuning, evt, command);
            tutorial = new DrivingTutorial(tutorialContent);
            driving.EmitLifecycle("tutorial.started", command.CommandId,
                tutorial.CurrentLessonId);
            screen = ScreenDriving;
            ClearInput();
            DrainDrivingEvents();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult TutorialReplay(GameCommand command)
        {
            if (tutorial == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            tutorial.Replay();
            if (driving != null)
            {
                // Observable cue re-issue: the same objective/cue identity
                // with a bumped sequence, correlated to this command.
                driving.EmitLifecycle("tutorial.cue", command.CommandId,
                    tutorial.Snapshot().cueId);
                DrainDrivingEvents();
            }
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult TutorialExit(GameCommand command)
        {
            if (tutorial == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            EndRace("tutorialExit", command);
            screen = ScreenPractice;
            return Result(command, CommandResult.Applied, "");
        }

        // One tutorial Advance per simulated boundary: the consumed
        // session events are the only progression source. Lesson
        // completions emit correlated tutorial.lesson records; finishing
        // the last lesson swaps the session onto the one-lap trial (new
        // race identity inside the run) and a genuine race.finished on
        // that trial marks graduation.
        private void AdvanceTutorial(DrivingStepResult result)
        {
            var advance = tutorial.Advance(result.state, result.events);
            if (advance.completedLessonId != null && driving != null)
            {
                driving.EmitLifecycle("tutorial.lesson", "", advance.completedLessonId);
                DrainDrivingEvents();
            }
            if (advance.newPhase == DrivingTutorial.PhaseTrial)
                TransitionToTrial();
            else if (advance.newPhase == DrivingTutorial.PhaseGraduated && driving != null)
            {
                driving.EmitLifecycle("tutorial.graduated", "", tutorial.TrialEventId);
                DrainDrivingEvents();
            }
        }

        // The lessons are done: end the free-drive session and start the
        // introductory event as the graduation trial, same vehicle, new
        // race identity inside the same run. The trial was validated at
        // tutorial.start; if the catalog can no longer supply it the
        // tutorial ends honestly instead of throwing out of StepDriving.
        private void TransitionToTrial()
        {
            var session = driving;
            VehicleTuning tuning;
            EventDefinition evt;
            if (session == null
                || !drivingContent.Vehicles.TryGetValue(session.VehicleId, out tuning)
                || !drivingContent.Events.TryGetValue(tutorial.TrialEventId, out evt))
            {
                EndRace("tutorialContent", null);
                return;
            }
            session.CancelPending("tutorialTrial");
            session.EmitLifecycle("race.ended", "", "tutorialTrial");
            DrainSessionEvents(session);
            driving = CreateSession(tuning, evt, null);
            driving.EmitLifecycle("tutorial.trial", "", tutorial.TrialEventId);
            ClearInput();
            DrainDrivingEvents();
        }

        private DrivingSession CreateSession(VehicleTuning tuning, EventDefinition evt,
            GameCommand command)
        {
            var course = drivingContent.Courses[evt.courseId];
            var raceId = "race-" + (++raceCounter).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            // A new race identity always starts unpaused: pause is a race
            // affordance (sandbox/driving only) and must never leak into a
            // fresh session started from a screen without one.
            paused = false;
            return new DrivingSession(raceId, tuning, course, evt,
                drivingContent.Version, command != null ? command.CommandId : "");
        }

        // Ends the active session: latched actions cancel with their
        // reason, a correlated end event lands on this boundary, and held
        // input neutralizes. The driving screen falls back to practice.
        private void EndRace(string reason, GameCommand command)
        {
            if (driving == null) return;
            var session = driving;
            session.CancelPending(reason);
            // Any session end (exit, profile switch, lifecycle) also ends
            // an active tutorial — tutorial state is session-local, so it
            // simply dies with the run and reports why.
            if (tutorial != null)
            {
                session.EmitLifecycle("tutorial.exited",
                    command != null ? command.CommandId : "", reason);
                tutorial = null;
            }
            session.EmitLifecycle(
                reason == "raceExit" || reason == "tutorialExit"
                    ? "race.exited" : "race.ended",
                command != null ? command.CommandId : "", reason);
            driving = null;
            DrainSessionEvents(session);
            if (screen == ScreenDriving) screen = ScreenPractice;
            // Pause belongs to the race affordance: it cannot survive the
            // session end, or practice/profile screens (where pause.toggle
            // is unavailable) would be left invisibly frozen.
            paused = false;
            ClearInput();
        }

        private void DrainDrivingEvents()
        {
            if (driving == null) return;
            AppendDrivingEvents(driving.DrainEvents());
        }

        private void DrainSessionEvents(DrivingSession session)
        {
            AppendDrivingEvents(session.DrainEvents());
        }

        private void AppendDrivingEvents(IEnumerable<DrivingEvent> events)
        {
            foreach (var ev in events)
            {
                drivingEvents.Add(ev);
                // The event slice is bounded; eviction is allowed only
                // because the durable drop marker records every removal.
                if (drivingEvents.Count > MaxDrivingEvents)
                {
                    drivingEvents.RemoveAt(0);
                    droppedDrivingEvents++;
                }
            }
        }

        // Semantic HUD controls actually usable on the driving screen; the
        // shell mirrors discovery, the capture records what was operable.
        private void FillDrivingControls(DrivingSessionState state)
        {
            foreach (var action in new[]
                { "input.set", "input.clear", "camera.toggle", "pause.toggle",
                  "race.restart", "race.exit", "tutorial.replay", "tutorial.exit" })
            {
                bool available;
                string reason;
                Availability(action, out available, out reason);
                if (available) state.controls.Add(action);
            }
        }

        private bool WritableSelected()
        {
            return activeSlot >= 0 && slots[activeSlot].Status == "Loaded";
        }

        private void Availability(string action, out bool available, out string reason)
        {
            available = true;
            reason = "";
            if (action.StartsWith("lan.", StringComparison.Ordinal))
            {
                if (lanSession == null)
                {
                    available = false;
                    reason = "unavailable";
                    return;
                }
                lanSession.Availability(action, out available, out reason);
                return;
            }
            if (action == "pause.toggle" && LanActive())
            {
                lanSession.Availability(action, out available, out reason);
                return;
            }
            if (LanActive() && SoloBlockedDuringLan(action))
            {
                available = false;
                reason = "networkActive";
                return;
            }
            switch (action)
            {
                case "profile.create":
                    if (!slots.Any(s => s.Status == "Missing"))
                    {
                        available = false;
                        reason = "noMissingSlot";
                    }
                    break;
                case "profile.select":
                    if (!slots.Any(s => s.Status == "Loaded" || s.Status == "RecoveredFromBackup"))
                    {
                        available = false;
                        reason = "noSelectableSlot";
                    }
                    break;
                case "ui.language":
                    if (activeSlot >= 0)
                    {
                        available = false;
                        reason = "useSettingsUpdate";
                    }
                    break;
                case "settings.update":
                case "settings.save":
                case "camera.toggle":
                    if (activeSlot < 0)
                    {
                        available = false;
                        reason = "requiresSelectedProfile";
                    }
                    else if (!WritableSelected())
                    {
                        available = false;
                        reason = "readOnlyProfile";
                    }
                    break;
                case "input.set":
                case "pause.toggle":
                    if (screen != ScreenSandbox && screen != ScreenDriving)
                    {
                        available = false;
                        reason = "drivingOrSandboxOnly";
                    }
                    break;
                case "race.start":
                    if (drivingContent == null)
                    {
                        available = false;
                        reason = "contentUnavailable";
                    }
                    else if (activeSlot < 0)
                    {
                        available = false;
                        reason = "requiresSelectedProfile";
                    }
                    else if (driving != null)
                    {
                        available = false;
                        reason = "raceActive";
                    }
                    break;
                case "race.restart":
                case "race.exit":
                    if (driving == null)
                    {
                        available = false;
                        reason = "noActiveRace";
                    }
                    break;
                case "tutorial.start":
                    if (drivingContent == null || tutorialContent == null
                        || !drivingContent.Events.ContainsKey(tutorialContent.freeDriveEventId)
                        || !drivingContent.Events.ContainsKey(tutorialContent.trialEventId))
                    {
                        available = false;
                        reason = "contentUnavailable";
                    }
                    else if (activeSlot < 0)
                    {
                        available = false;
                        reason = "requiresSelectedProfile";
                    }
                    else if (driving != null)
                    {
                        available = false;
                        reason = "raceActive";
                    }
                    break;
                case "tutorial.replay":
                case "tutorial.exit":
                    if (tutorial == null)
                    {
                        available = false;
                        reason = "noActiveTutorial";
                    }
                    break;
                case "ui.navigate":
                    if (screen == ScreenDriving)
                    {
                        available = false;
                        reason = "useRaceExit";
                    }
                    break;
            }
        }

        private static string ValidateParameters(GameCommand command, ParameterSpec[] schema)
        {
            foreach (var name in command.Parameters.Keys)
            {
                var known = false;
                foreach (var spec in schema)
                    if (spec.Name == name) { known = true; break; }
                if (!known) return "unknownParameter";
            }
            foreach (var spec in schema)
            {
                if (!command.Parameters.ContainsKey(spec.Name)) return "missingParameter";
                var value = command.Parameters[spec.Name];
                switch (spec.Type)
                {
                    case "integer":
                        if (!IsWireInteger(value)) return "invalidParameter";
                        break;
                    case "decimal":
                        if (!IsWireDecimal(value)) return "invalidParameter";
                        break;
                    case "boolean":
                        if (value != "true" && value != "false") return "invalidParameter";
                        break;
                    default:
                        if (spec.AllowedValues != null && !spec.AllowedValues.Contains(value))
                            return "invalidParameter";
                        break;
                }
            }
            return null;
        }

        private static bool IsWireInteger(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var start = value[0] == '-' ? 1 : 0;
            if (start == value.Length) return false;
            for (var i = start; i < value.Length; i++)
                if (value[i] < '0' || value[i] > '9') return false;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        private static bool IsWireDecimal(string value)
        {
            if (string.IsNullOrEmpty(value) || value != value.Trim()) return false;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && !float.IsNaN(parsed) && !float.IsInfinity(parsed);
        }

        private static int ParseInt(string value)
        {
            return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static float ParseDecimal(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static bool InUnitRange(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 1f;
        }

        private static bool IsTrue(string value) { return value == "true"; }

        private static string OtherCamera(string camera)
        {
            return camera == "chase" ? "overhead" : "chase";
        }

        private CommandResult Result(GameCommand command, string status, string code)
        {
            return new CommandResult
            {
                commandId = command == null ? "" : command.CommandId ?? "",
                action = command == null ? "" : command.Action ?? "",
                status = status,
                code = code,
                tick = tick,
                duplicate = false
            };
        }

        private static CommandResult Duplicated(CommandResult stored)
        {
            var copy = stored.Copy();
            copy.duplicate = true;
            return copy;
        }

        // The stored copy and the returned copy are separate so a caller
        // mutating a result can never corrupt the identity cache.
        private CommandResult Record(GameCommand command, CommandResult result)
        {
            results[command.CommandId] = new CachedResult
            {
                Envelope = new CommandEnvelope(command),
                Result = result.Copy()
            };
            return result.Copy();
        }

        private static ProfileSave CopyProfile(ProfileSave profile)
        {
            if (profile == null) return null;
            return new ProfileSave
            {
                schemaVersion = profile.schemaVersion,
                id = profile.id,
                slot = profile.slot,
                avatar = profile.avatar,
                colorIndex = profile.colorIndex,
                language = profile.language,
                camera = profile.camera,
                reducedCameraShake = profile.reducedCameraShake,
                musicVolume = profile.musicVolume,
                effectsVolume = profile.effectsVolume,
                voiceVolume = profile.voiceVolume,
                coins = profile.coins,
                unlockedVehicles = profile.unlockedVehicles == null
                    ? null
                    : new List<string>(profile.unlockedVehicles)
            };
        }

        private static KeyValuePair<string, ParameterSpec[]> Schema(string action, params ParameterSpec[] parameters)
        {
            return new KeyValuePair<string, ParameterSpec[]>(action, parameters);
        }

        private static ParameterSpec Param(string name, string type, string[] allowedValues)
        {
            return new ParameterSpec(name, type, allowedValues);
        }

        private static Dictionary<string, ParameterSpec[]> BuildSchemaMap()
        {
            var map = new Dictionary<string, ParameterSpec[]>(StringComparer.Ordinal);
            foreach (var entry in Schemas) map[entry.Key] = entry.Value;
            return map;
        }
    }
}
