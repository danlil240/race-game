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
        // Task 9 LAN screens: the browser is the only navigable entry
        // point; lobby/race/results/recovery are phase-derived (see
        // FoundationService.Network.cs SyncLanScreen) so a rendered screen
        // can never disagree with the session phase.
        public const string ScreenLanBrowser = "lan-browser";
        public const string ScreenLanLobby = "lan-lobby";
        public const string ScreenLanRace = "lan-race";
        public const string ScreenLanResults = "lan-results";
        public const string ScreenLanRecovery = "lan-recovery";
        private const int MaxDrivingEvents = 128;

        private sealed class ParameterSpec
        {
            internal string Name;
            internal string Type;
            internal string[] AllowedValues;
            // Optional parameters may be omitted entirely; when present
            // they still validate by type before the command may apply.
            internal bool Optional;

            internal ParameterSpec(string name, string type, string[] allowedValues,
                bool optional = false)
            {
                Name = name;
                Type = type;
                AllowedValues = allowedValues;
                Optional = optional;
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
        // The local gameplay modes solo.start seats (M5 Task 4);
        // practice keeps race.start and lan keeps the session surface.
        private static readonly string[] SoloModes =
            { "campaign", "quickRace", "timeTrial" };
        // "driving" is never a navigation target: race.start enters it and
        // race.exit is the only way back out. The same rule applies to the
        // LAN phase screens — "lan-browser" is the only LAN target; the
        // lobby/race/results/recovery screens are reached by the session
        // phase, not by navigation.
        private static readonly string[] Screens =
            { "profiles", "settings", "sandbox", "practice", "lan-browser" };
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
                Param("pause", "boolean", Bools), Param("durationTicks", "integer", null),
                // Optional held demands (Task 7): absent throttle means
                // automatic throttle — never an ambiguous zero — while an
                // explicit 0 is a deliberate coast. Brake defaults to 0.
                OptionalParam("throttle", "decimal", null),
                OptionalParam("brake", "decimal", null)),
            Schema("input.clear"),
            Schema("app.focusLost"),
            Schema("camera.toggle"),
            Schema("pause.toggle"),
            Schema("race.start",
                Param("vehicleId", "string", null), Param("eventId", "string", null)),
            Schema("race.restart"),
            Schema("race.exit"),
            // M5 Task 4 local modes: the mode chooses the shared
            // four-racer coordinator (campaign | quickRace) or the lone
            // trial session (timeTrial); every other selection field is
            // validated mode-dependently inside SoloRaceSession.
            Schema("solo.start",
                Param("mode", "string", SoloModes),
                OptionalParam("eventId", "string", null),
                OptionalParam("courseId", "string", null),
                OptionalParam("classId", "string", null),
                OptionalParam("vehicleId", "string", null),
                OptionalParam("difficulty", "string", null)),
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
        private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
            new Dictionary<string, string>(0);

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
        // The injected vehicle simulation the solo session steps through
        // (Task 4 boundary). The runtime installs the factory once — Core
        // never constructs Unity objects; without it, the legacy
        // IMotorWorld adapter supplies an arcade simulation.
        private Func<DrivingContentCatalog, IVehicleSimulation>
            drivingSimulationFactory;
        private IVehicleSimulation drivingSimulation;
        private DrivingSession drivingSimulationFor;
        private IMotorWorld legacyMotorWorld;
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
                    Profile = ProfileCopy.DeepCopy(load.Profile)
                };
            }
        }

        public bool Paused { get { return paused; } }
        public bool Racing { get { return driving != null || soloRace != null; } }

        // The currently held (leased) local input. Task 5's LAN path feeds
        // this same sample through the validated network input path so the
        // local racer takes no special-case route.
        public DriverInput CurrentInput { get { return input; } }

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

        // The composition root injects the simulation factory once:
        // backend selection comes from DrivingContentCatalog.SimulationBackend
        // at race creation and is fixed for that race's lifetime. The
        // factory returns an uninitialized simulation; the service owns,
        // initializes and disposes it with the session it was built for.
        public void SetDrivingSimulationFactory(
            Func<DrivingContentCatalog, IVehicleSimulation> factory)
        {
            drivingSimulationFactory = factory;
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

        // One simulated driving boundary through the injected vehicle
        // simulation: prepare the rule request, land any pending reset on
        // the body before the batch, run the single request batch, then
        // complete the rules and land any rule-requested recovery reset —
        // all before the boundary publishes. Called before AdvanceTick so
        // the held-input sample (including its last leased tick) is
        // consumed by the session before the observation tick expires
        // leases. Frozen while paused: observation ticks continue,
        // simulation time does not.
        public void StepDriving()
        {
            if (driving == null || paused) return;
            var simulation = EnsureDrivingSimulation();
            if (simulation == null) return;
            var request = driving.PrepareStep(input);
            DrivingStepResult result;
            if (request == null)
            {
                // The session finalized: nothing steps, but the boundary
                // still drains staged events exactly like the old path.
                result = driving.CompleteStep(null);
            }
            else
            {
                if (request.reset != null)
                    simulation.Restore(request.reset.participantId,
                        request.reset.state);
                var results = simulation.Step(new[] { request });
                VehicleTickResult tickResult = null;
                if (results != null)
                    foreach (var candidate in results)
                        if (candidate != null
                            && candidate.participantId
                                == driving.ParticipantId)
                            tickResult = candidate;
                result = driving.CompleteStep(tickResult);
                if (result.reset != null)
                    simulation.Restore(result.reset.participantId,
                        result.reset.state);
            }
            AppendDrivingEvents(result.events);
            if (tutorial != null) AdvanceTutorial(result);
        }

        // Legacy call shape retained while callers migrate: binds the
        // motor world the runtime used to pass and steps through the same
        // seam via a lazily-wrapped arcade simulation.
        public void StepDriving(IMotorWorld world)
        {
            legacyMotorWorld = world;
            StepDriving();
        }

        // The simulation the active session steps through: built once per
        // session from the injected factory (or the legacy world adapter),
        // initialized with the session's own roster identity, and disposed
        // with the session — the solo screen can never own a stale world.
        private IVehicleSimulation EnsureDrivingSimulation()
        {
            if (driving == null) return null;
            if (drivingSimulation != null && drivingSimulationFor == driving)
                return drivingSimulation;
            ReleaseDrivingSimulation();
            if (drivingContent == null) return null;
            VehicleTuning tuning;
            drivingContent.Vehicles.TryGetValue(driving.VehicleId, out tuning);
            // Last line of defense inside the tick path: every session-
            // creation boundary enforces CatalogSeatsVehicle, but if one
            // ever slipped through, refuse to initialize rather than let
            // a backend throw mid-boundary on an incompatible roster.
            if (!CatalogSeatsVehicle(tuning)) return null;
            if (drivingSimulationFactory != null)
                drivingSimulation = drivingSimulationFactory(drivingContent);
            else if (legacyMotorWorld != null)
                drivingSimulation = new ArcadeVehicleSimulation(legacyMotorWorld);
            if (drivingSimulation == null) return null;
            drivingSimulation.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = driving.ParticipantId,
                    tuning = tuning,
                    initialState = driving.Snapshot().motor
                }
            });
            drivingSimulationFor = driving;
            return drivingSimulation;
        }

        private void ReleaseDrivingSimulation()
        {
            if (drivingSimulation != null)
                drivingSimulation.Dispose();
            drivingSimulation = null;
            drivingSimulationFor = null;
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
            var network = NetworkSnapshot();
            var networkRaceCopy = networkRaceState != null
                ? networkRaceState.Copy() : null;
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
                    pause = input.Pause,
                    brake = input.Brake,
                    hasThrottleOverride = input.HasThrottleOverride,
                    throttle = input.Throttle
                },
                slots = new List<SlotState>(),
                savedProfile = ProfileCopy.DeepCopy(savedProfile),
                draftProfile = ProfileCopy.DeepCopy(draftProfile),
                pendingSettings = pendingSettings,
                persistenceStatus = persistenceStatus,
                captureValid = captureValid,
                captureError = captureError,
                featureCoverage = new FeatureCoverage(),
                presentation = CopyPresentation(presentation),
                driving = null,
                drivingEvents = new List<DrivingEvent>(),
                droppedDrivingEvents = droppedDrivingEvents,
                network = network,
                networkRace = networkRaceCopy,
                // Contract: `prediction` is null on the host and outside
                // a LAN race. The replication bridge clears its own
                // facts on its next pump, which can lag the session's
                // role/race transition by a boundary — this record must
                // stay self-consistent, so gate the slice on THIS
                // record's own network slices (surfaced live 2026-10-14:
                // a `lan.leave` teardown boundary briefly published a
                // stale prediction slice with role=none).
                prediction = networkPrediction != null
                    && network.role == LanSessionService.RoleClient
                    && networkRaceCopy != null
                    ? networkPrediction.Copy() : null,
                remotePresentation = networkRemotePresentation != null
                    ? networkRemotePresentation.Copy() : null,
                networkLink = networkLinkState != null
                    ? networkLinkState.Copy() : null,
                // M5 Task 4 local modes: the live solo race session —
                // null unless campaign/quickRace/timeTrial is active.
                soloRace = soloRace != null ? soloRace.Snapshot() : null
            };
            if (driving != null)
            {
                state.driving = driving.Snapshot();
                state.driving.camera = cameraFacts != null ? cameraFacts.Copy() : new DrivingCameraState();
                state.driving.tutorial = tutorial != null ? tutorial.Snapshot() : null;
                FillDrivingControls(state.driving);
                // The live world's cost/shape facts ride the session
                // state when the backend exposes them — null before the
                // first stepped boundary, never fabricated (Task 11).
                var diagnostics = drivingSimulation
                    as IVehicleSimulationDiagnostics;
                if (diagnostics != null && drivingSimulationFor == driving)
                    state.driving.simulation = diagnostics.Diagnostics;
            }
            foreach (var ev in drivingEvents)
                state.drivingEvents.Add(ev.Copy());
            for (var slot = 0; slot < slots.Length; slot++)
            {
                state.slots.Add(new SlotState
                {
                    slot = slot,
                    status = slots[slot].Status,
                    profile = ProfileCopy.DeepCopy(slots[slot].Profile)
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
                        required = !spec.Optional,
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
            var brake = 0f;
            float? throttle = null;
            if (holdTicksRemaining > 0)
            {
                holdTicksRemaining--;
                if (holdTicksRemaining > 0)
                {
                    steering = input.Steering;
                    drift = input.Drift;
                    boost = input.Boost;
                    brake = input.Brake;
                    throttle = input.HasThrottleOverride
                        ? (float?)input.Throttle
                        : null;
                }
            }
            input = new DriverInput(steering, drift, boost, false, false,
                false, false, throttle, brake);
            // The session phase owns the LAN screens: connecting lives on
            // the browser, racing on the race screen, and teardown drops a
            // stranded phase screen back onto the browser on this boundary.
            SyncLanScreen();
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
            if (soloRace != null)
            {
                // Local-mode sessions carry the local racer's latched
                // one-shots through the same cancellation policy.
                soloRace.CancelPending("cleared");
                DrainSoloEvents();
            }
        }

        // Runtime-only callback the app wires to its physical input
        // pump's RequireRearm — focus-loss cleanup rearms held physical
        // input exactly the way OnApplicationFocus does, while Core
        // stays free of the presentation-layer dependency.
        public Action FocusCleanupHook;

        // Focus-loss cleanup is a first-class command so agent-driven
        // runs can drive the exact path OnApplicationFocus and
        // OnApplicationPause take: neutralize held input, cancel queued
        // driving actions, then rearm the physical input pump.
        private CommandResult ApplicationFocusLost(GameCommand command)
        {
            ClearInput();
            FocusCleanupHook?.Invoke();
            return Result(command, CommandResult.Applied, "");
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
                // Entering a LAN session while a solo race runs is refused:
                // hosting/joining/reconnecting would tear down shared state
                // the running race still claims. Session-internal lan.* and
                // lan.discover keep their own availability answers.
                if ((driving != null || soloRace != null)
                    && LanEntryBlockedDuringRace(command.Action))
                    return Result(command, CommandResult.Rejected, "raceActive");
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
                    case "app.focusLost": return ApplicationFocusLost(command);
                    case "camera.toggle": return ToggleCamera(command);
                    case "pause.toggle": return TogglePause(command);
                    case "race.start": return RaceStart(command);
                    case "race.restart": return RaceRestart(command);
                    case "race.exit": return RaceExit(command);
                    case "solo.start": return SoloStart(command);
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
                case "race.exit":
                case "solo.start":
                case "tutorial.start":
                case "tutorial.replay":
                case "tutorial.exit":
                    return true;
                default:
                    return false;
            }
        }

        // Session-entry actions that must not fire while a solo race is
        // live. Browsing (lan.discover) and session operations keep their
        // own availability answers; only actual session membership is
        // fenced off a running race.
        private static bool LanEntryBlockedDuringRace(string action)
        {
            switch (action)
            {
                case "lan.host":
                case "lan.join":
                case "lan.reconnect":
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
            EndSoloRace("lifecycle", command);
            slots[slot].Status = "Loaded";
            slots[slot].Profile = ProfileCopy.DeepCopy(created);
            savedProfile = ProfileCopy.DeepCopy(created);
            draftProfile = ProfileCopy.DeepCopy(created);
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
            EndSoloRace("lifecycle", command);
            savedProfile = ProfileCopy.DeepCopy(info.Profile);
            draftProfile = ProfileCopy.DeepCopy(info.Profile);
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
            // An active LAN session owns its screens: only the LAN browser
            // target is legal mid-session (the phase sync lands the user on
            // the phase-appropriate screen); leaving to a solo screen would
            // abandon the session silently. lan.leave is the way out.
            if (LanActive() && target != ScreenLanBrowser)
                return Result(command, CommandResult.Rejected, "networkActive");
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
            var draft = ProfileCopy.DeepCopy(savedProfile);
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
            // Settings save merges the draft's settings slice onto the
            // live profile's progression — a stale draft can never roll
            // coins, receipts or any schema-3 progression back.
            var candidate = ProfileCopy.DeepCopy(savedProfile);
            ProfileCopy.CopySettings(draftProfile, candidate);
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                // Restore the prior saved draft and visible language; the
                // persisted profile is unchanged by the store's safe save.
                draftProfile = ProfileCopy.DeepCopy(savedProfile);
                language = savedProfile.language;
                pendingSettings = false;
                persistenceStatus = "saveFailed";
                return Result(command, CommandResult.Failed, "saveFailed");
            }
            savedProfile = ProfileCopy.DeepCopy(candidate);
            draftProfile = ProfileCopy.DeepCopy(candidate);
            slots[activeSlot].Profile = ProfileCopy.DeepCopy(candidate);
            pendingSettings = false;
            persistenceStatus = "saved";
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult SetInput(GameCommand command)
        {
            var drivingActive = screen == ScreenDriving
                && (driving != null || soloRace != null);
            var lanRaceScreen = screen == ScreenLanRace && LanActive();
            if (screen != ScreenSandbox && !drivingActive && !lanRaceScreen)
                return Result(command, CommandResult.Rejected, "unavailable");
            var steering = ParseDecimal(command.Parameters["steering"]);
            if (float.IsNaN(steering) || float.IsInfinity(steering) || steering < -1f || steering > 1f)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            var duration = ParseInt(command.Parameters["durationTicks"]);
            if (duration < 1 || duration > MaxDurationTicks)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            // Optional held demands are parsed and range-checked before any
            // mutation — a malformed mixed sample must fail atomically,
            // never queue a recovery, toggle pause, or clobber held input.
            float? throttle = null;
            string throttleText;
            if (command.Parameters.TryGetValue("throttle", out throttleText))
            {
                var requested = ParseDecimal(throttleText);
                if (!InUnitRange(requested))
                    return Result(command, CommandResult.Rejected, "invalidParameter");
                throttle = requested;
            }
            var brake = 0f;
            string brakeText;
            if (command.Parameters.TryGetValue("brake", out brakeText))
            {
                brake = ParseDecimal(brakeText);
                if (!InUnitRange(brake))
                    return Result(command, CommandResult.Rejected, "invalidParameter");
            }
            var drift = IsTrue(command.Parameters["drift"]);
            var boost = IsTrue(command.Parameters["boost"]);
            var gadget = IsTrue(command.Parameters["gadget"]);
            var recover = IsTrue(command.Parameters["recover"]);
            var changeCamera = IsTrue(command.Parameters["changeCamera"]);
            var pausePulse = IsTrue(command.Parameters["pause"]);
            // M5 Task 5: gadget is a live held control — it rides the
            // same input sample everywhere and the authoritative rules
            // edge-latch it (one item per press, never two).
            // A recovery press is latched with its command identity until a
            // simulation boundary consumes it; overflow refuses before the
            // held-input sample mutates.
            if (drivingActive && recover)
            {
                string queueError;
                if (driving != null)
                {
                    if (!driving.QueuePending("recover", command.CommandId, out queueError))
                        return Result(command, CommandResult.Rejected, queueError);
                    DrainDrivingEvents();
                }
                else if (soloRace != null
                    && !soloRace.QueuePending("recover", command.CommandId, out queueError))
                {
                    return Result(command, CommandResult.Rejected, queueError);
                }
                DrainSoloEvents();
            }
            // LAN race input: the recover flag rides the input frame to the
            // host (which queues and acknowledges the action); a pause pulse
            // submits the shared pause request/toggle through the session
            // path — the solo paused flag is never touched mid-session.
            if (lanRaceScreen)
            {
                if (pausePulse && lanSession != null)
                {
                    lanSession.Execute(new GameCommand(
                        command.ProtocolVersion, command.RunId,
                        command.CommandId + "/pause", command.ParticipantId,
                        "pause.toggle", EmptyParameters));
                }
                // The recover flag does NOT ride the input frame on LAN:
                // the frame's edge-latched recover would queue an anonymous
                // second one-shot beside the deduplicated action. The app
                // submits recover as a one-shot action carrying this
                // command's identity (see FoundationApp.ExecuteAndEmit).
                // Gadget DOES ride the frame (M5 Task 5): it is a held
                // level the host's rules edge-latch — there is no
                // one-shot acknowledgment contract for it.
                input = new DriverInput(steering, drift, boost, gadget,
                    false, changeCamera, pausePulse, throttle, brake);
                holdTicksRemaining = duration;
                if (changeCamera && WritableSelected())
                {
                    draftProfile.camera = OtherCamera(draftProfile.camera);
                    pendingSettings = true;
                }
                return Result(command, CommandResult.Applied, "");
            }
            input = new DriverInput(steering, drift, boost, gadget, recover,
                changeCamera, pausePulse, throttle, brake);
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
                if (soloRace != null)
                {
                    soloRace.SetPaused(paused);
                    DrainSoloEvents();
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
            // The local session's own clock follows the same toggle —
            // entering pause freezes every race timer and cancels
            // latched one-shots; resume republishes race.resumed.
            if (soloRace != null)
            {
                soloRace.SetPaused(paused);
                DrainSoloEvents();
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
            if (driving != null || soloRace != null)
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
            // The backend is a race-level contract bound to the catalog:
            // a physically simulated race refuses to seat an arcade-tuned
            // vehicle here, at the command boundary — the same guard
            // NetworkRace.TryCreate applies — instead of failing inside
            // the first stepped tick when the physical sim cannot find a
            // valid physics tuning for it.
            if (!CatalogSeatsVehicle(tuning))
                return Result(command, CommandResult.Rejected, "invalidVehicle");
            driving = CreateSession(tuning, evt, command);
            screen = ScreenDriving;
            ClearInput();
            DrainDrivingEvents();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult RaceRestart(GameCommand command)
        {
            // One uniform retry affordance: a local-mode session replays
            // its stored request under a fresh race identity; the legacy
            // practice session rebuilds from the catalog exactly as
            // before.
            if (driving == null && soloRace != null)
                return SoloRestart(command);
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
            // Same backend contract as race.start: a content reload must
            // not let a retry seat an arcade tuning in a physical race.
            if (!CatalogSeatsVehicle(tuning))
                return Result(command, CommandResult.Rejected, "invalidVehicle");
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
            if (driving == null && soloRace == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            // One uniform exit affordance across practice and the local
            // modes: the solo path aborts without a rewardable result,
            // retires the unused context and emits race.exited.
            if (soloRace != null) EndSoloRace("raceExit", command);
            else EndRace("raceExit", command);
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
            if (driving != null || soloRace != null)
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
            // Same backend contract as race.start: a physical catalog's
            // tutorial cannot seat an arcade-only tuning — reject at the
            // command boundary rather than throwing inside the first
            // stepped tick (the trial stage shares the same contract and
            // is enforced again in TransitionToTrial).
            if (!CatalogSeatsVehicle(tuning))
                return Result(command, CommandResult.Rejected, "invalidVehicle");
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
            // Same backend contract as race.start: a swapped/reloaded
            // catalog must not let the trial seat an arcade-only tuning
            // in a physical race — end the tutorial honestly instead of
            // a session the first stepped tick would throw on.
            if (!CatalogSeatsVehicle(tuning))
            {
                EndRace("invalidVehicle", null);
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

        // The roster/backend contract NetworkRace.TryCreate enforces,
        // shared by every session-creation path (race.start, race.restart,
        // tutorial.start and the tutorial's trial transition): a physical
        // catalog seats only vehicles whose tuning declares the same
        // backend and carries a validated physics preset. Arcade catalogs
        // seat anything — LAN parity says a physical-declared vehicle
        // still drives its arcade fields in an arcade race.
        private bool CatalogSeatsVehicle(VehicleTuning tuning)
        {
            if (drivingContent == null
                || drivingContent.SimulationBackend
                    != VehicleSimulationBackend.PhysicalAssisted)
                return true;
            return tuning != null
                && tuning.simulationBackend
                    == VehicleSimulationBackend.PhysicalAssisted
                && tuning.physics != null;
        }

        private DrivingSession CreateSession(VehicleTuning tuning, EventDefinition evt,
            GameCommand command)
        {
            var course = drivingContent.Courses[evt.courseId];
            var raceId = "race-" + (++raceCounter).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            // A new session identity retires the simulation the previous
            // session owned — worlds are per-session, never shared across
            // race identities.
            ReleaseDrivingSimulation();
            // A new race identity always starts unpaused: pause is a race
            // affordance (sandbox/driving only) and must never leak into a
            // fresh session started from a screen without one.
            paused = false;
            // The canonical tuned-content fingerprint and the catalog's
            // race-level backend selection ride the session state so a
            // stream consumer binds physics evidence to a tuning
            // identity (Task 11). The fingerprint detects ANY canonical
            // tuning delta, not just authored version bumps.
            return new DrivingSession(raceId, tuning, course, evt,
                drivingContent.Version,
                command != null ? command.CommandId : "",
                null, null,
                drivingContent.CompatibilityFingerprint,
                drivingContent.SimulationBackend,
                gameplayCatalog != null && gameplayCatalog.Gadgets != null
                    ? gameplayCatalog.Gadgets : gadgetCatalog);
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
            ReleaseDrivingSimulation();
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
                // Profile-dependent availability answers (host/join) must
                // see the live context, not just the last command's.
                SyncNetworkContext();
                if (driving != null && LanEntryBlockedDuringRace(action))
                {
                    available = false;
                    reason = "raceActive";
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
                    if (screen != ScreenSandbox && screen != ScreenDriving
                        && screen != ScreenLanRace)
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
                    else if (driving != null || soloRace != null)
                    {
                        available = false;
                        reason = "raceActive";
                    }
                    break;
                case "race.restart":
                case "race.exit":
                    if (driving == null && soloRace == null)
                    {
                        available = false;
                        reason = "noActiveRace";
                    }
                    break;
                case "solo.start":
                    if (gameplayCatalog == null || gameplayCatalog.IsLegacy
                        || gameplayCatalog.Driving == null)
                    {
                        available = false;
                        reason = "contentUnavailable";
                    }
                    else if (activeSlot < 0)
                    {
                        available = false;
                        reason = "requiresSelectedProfile";
                    }
                    else if (driving != null || soloRace != null)
                    {
                        available = false;
                        reason = "raceActive";
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
                    else if (driving != null || soloRace != null)
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
                string value;
                if (!command.Parameters.TryGetValue(spec.Name, out value))
                {
                    if (spec.Optional) continue;
                    return "missingParameter";
                }
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

        // Profile copies all flow through ProfileCopy.DeepCopy — the
        // single lossless copy that keeps every snapshot, draft, slot
        // cache and reward candidate deeply independent of the live
        // profile's nested collections.

        private static KeyValuePair<string, ParameterSpec[]> Schema(string action, params ParameterSpec[] parameters)
        {
            return new KeyValuePair<string, ParameterSpec[]>(action, parameters);
        }

        private static ParameterSpec Param(string name, string type, string[] allowedValues)
        {
            return new ParameterSpec(name, type, allowedValues);
        }

        private static ParameterSpec OptionalParam(string name, string type, string[] allowedValues)
        {
            return new ParameterSpec(name, type, allowedValues, true);
        }

        private static Dictionary<string, ParameterSpec[]> BuildSchemaMap()
        {
            var map = new Dictionary<string, ParameterSpec[]>(StringComparer.Ordinal);
            foreach (var entry in Schemas) map[entry.Key] = entry.Value;
            return map;
        }
    }
}
