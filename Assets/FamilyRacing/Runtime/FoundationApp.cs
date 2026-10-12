using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Composition root: one FoundationService, optional validation bridge
    // (editor/development only), physical input adapter and UI shell share
    // the same Execute path so agent and human commands are equivalent.
    public sealed class FoundationApp : MonoBehaviour
    {
        public const int ProtocolVersion = ValidationBridge.ProtocolVersion;
        public const float TickRate = 30f;
        // Catch-up work per frame is bounded; unsimulated backlog is kept
        // (never dropped) and reported through presentation diagnostics.
        private const int MaxStepsPerFrame = 8;

        private FoundationService service;
        private Networking.LanSessionLink lanLink;
        private Networking.NetworkRaceBridge raceBridge;
        private DrivingWorld drivingWorld;
        private DrivingCamera drivingCamera;
        private TutorialPresenter tutorialPresenter;
        private ValidationBridge bridge;
        private FoundationInput driverInput;
        private FoundationShell shell;
        private UIDocument document;
        private PresentationState presentation = new PresentationState();
        private string runId;
        private string lastInputSource = "";
        private string lastLanguage = "";
        // Shared-pause edge detector: entering a network pause clears held
        // input exactly like a focus loss — resume requires fresh input.
        private bool lastNetworkPaused;
        private float accumulator;
        private ulong commandCounter;

        public FoundationService Service => service;
        public FoundationInput Input => driverInput;
        public string RunId => runId;
        public bool AgentControlsInput => bridge != null && bridge.ControllerAttached;
        public string CurrentScreen => service != null ? service.Snapshot().screen : "profiles";

        private void Awake()
        {
            var validationDir = ValidationDirectory();
            var profilesDir = SelectProfilesDirectory(validationDir,
                Application.persistentDataPath, Environment.GetCommandLineArgs());
            // In validation mode the bridge owns the capture directory (it
            // requires a new/empty dir); ProfileStore.Save creates the
            // selected profile directory on first write, including a shared
            // --validation-profile-dir override.
            if (validationDir == null)
                Directory.CreateDirectory(profilesDir);
            var store = new ProfileStore(profilesDir, new UnityProfileSerializer());
            runId = Guid.NewGuid().ToString("N");
            service = new FoundationService(store, "en", runId);
            // Load and validate driving content before the bridge opens so
            // race.start availability and record contentVersion are honest
            // from sequence 1. A validation failure leaves no catalog and
            // race.start reports contentUnavailable.
            drivingWorld = GetComponent<DrivingWorld>();
            tutorialPresenter = GetComponent<TutorialPresenter>();
            if (tutorialPresenter == null)
                tutorialPresenter = gameObject.AddComponent<TutorialPresenter>();
            Core.Racing.DrivingContentCatalog loadedCatalog = null;
            if (drivingWorld != null)
            {
                var catalog = drivingWorld.Load();
                loadedCatalog = catalog;
                if (catalog != null) service.SetDrivingContent(catalog);
                // Validated tutorial content enables tutorial.start; its
                // asset carries the presenter's bundled clip slots.
                if (drivingWorld.Tutorial != null)
                    service.SetTutorialDefinition(drivingWorld.Tutorial);
                tutorialPresenter.Bind(drivingWorld.TutorialAsset);
            }
            driverInput = GetComponent<FoundationInput>();
            shell = GetComponent<FoundationShell>();
            // The Task-4 presentation camera owns real chase/overhead framing
            // whenever a session is active; added here so the bootstrap scene
            // needs no wiring and fixtures without it keep the world's
            // temporary overview fallback.
            drivingCamera = GetComponent<DrivingCamera>();
            if (drivingCamera == null) drivingCamera = gameObject.AddComponent<DrivingCamera>();
            // With a live presentation camera the world's temporary overview
            // fallback stays off — exactly one system owns Camera.main each
            // frame, and a disengaged presentation camera cannot expose a
            // stale overview pose. ApplyFrame is driven from Update on this
            // reference regardless of the enabled flag, so the bind follows
            // the reference, not the flag.
            if (drivingWorld != null && drivingCamera != null)
                drivingWorld.BindPresentationCamera();
            document = GetComponent<UIDocument>();
            presentation.buildTarget = Application.platform.ToString();
            if (validationDir != null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // A validation agent must not depend on window focus: a paused
                // player would freeze Pump and the controller could never attach.
                Application.runInBackground = true;
                bridge = new ValidationBridge(service, runId, validationDir,
                    ExecuteAndEmit, MarkInputSource);
                presentation.validationActive = true;
#else
                Debug.LogError("--validation-dir ignored: validation endpoints are disabled in regular builds.");
#endif
            }
            // LAN session service stays Unity-free; the runtime link is its
            // only window onto the real transport and discovery sockets.
            // The replication bridge shares the same transport so gameplay
            // packets ride the sequenced channel while session control
            // stays reliable-ordered.
            var lanTransport = new Networking.LanTransport();
            var lanDiscovery = new Networking.LanDiscovery();
            lanLink = new Networking.LanSessionLink(lanTransport, lanDiscovery);
            var lan = new Core.Networking.LanSessionService(
                lanLink, lanLink, Application.version);
            // Task 7: the private credential store lives inside the
            // profile-private directory — it is what survives a
            // client-process restart so a reserved slot can be reclaimed.
            // Its content is scoped to the local profile's participant id
            // and never enters observation state, streams, or logs.
            lan.SetReconnectStore(new Networking.ReconnectCredentialStore(
                Path.Combine(profilesDir, "lan-reconnect.json")));
            lanLink.Bind(lan);
            service.SetLanSession(lan);
            raceBridge = new Networking.NetworkRaceBridge(lanTransport, lan);
            if (loadedCatalog != null) raceBridge.SetContent(loadedCatalog);
            driverInput?.Bind(this);
            shell?.Bind(this);
            service.SetPresentation(presentation);
        }

        private void Update()
        {
            // Commands publish their action-boundary state+result before the
            // next observation tick is emitted.
            bridge?.Pump();
            // Transport/discovery callbacks run on this thread and queue
            // session outcomes; monotonic wall time expires joins and
            // request round-trips whether or not the app is paused.
            lanLink?.Pump(DateTime.UtcNow);
            service.AdvanceNetworkWallTime(Time.realtimeSinceStartupAsDouble);
            DrainNetworkBoundary();
            // Replication boundary: lifecycle diffs (race create/end, pause,
            // control/epoch transfers) and the reliable event-batch flush
            // run once per frame, after transport callbacks have landed.
            raceBridge?.Pump();
            driverInput?.Pump(AgentControlsInput, CurrentScreen);
            accumulator += Time.unscaledDeltaTime;
            var step = 1f / TickRate;
            var steps = 0;
            while (accumulator >= step && steps < MaxStepsPerFrame)
            {
                accumulator -= step;
                steps++;
                // Boundary order per the timing contract: the session
                // samples held input and consumes pending actions first,
                // then the observation tick expires leases and publishes.
                service.StepDriving(drivingWorld != null ? drivingWorld.MotorWorld : null);
                // One local 30 Hz boundary on the replication path: host
                // consumes validated inputs and steps the authoritative
                // race (publishing at 15 Hz); a client emits one sample.
                raceBridge?.StepTick(service.CurrentInput,
                    drivingWorld != null ? drivingWorld.MotorWorld : null);
                var raceEvents = raceBridge != null ? raceBridge.DrainRaceEvents() : null;
                if (raceEvents != null && raceEvents.Count > 0)
                    service.PublishNetworkRaceEvents(raceEvents);
                service.SetNetworkRaceState(
                    raceBridge != null ? raceBridge.LatestState : null);
                // Task 6: predicted-motor/correction facts and remote
                // interpolation facts ride their own typed slices —
                // predicted data never aliases onto `driving`.
                service.SetPredictionState(
                    raceBridge != null ? raceBridge.Prediction : null);
                service.SetRemotePresentationState(
                    raceBridge != null ? raceBridge.RemotePresentation
                        : null);
                service.AdvanceTick();
                // One snapshot per tick boundary: the presentation refresh
                // mutates service state after the snapshot is taken, so the
                // refreshed facts are folded back before emission — same
                // record content without a second deep copy.
                var tickState = service.Snapshot();
                RefreshPresentation(tickState);
                tickState.presentation = presentation.Copy();
                bridge?.EmitTick(tickState);
            }
            presentation.backlogSteps = (int)(accumulator / step);
            if (steps == MaxStepsPerFrame && accumulator >= step)
                presentation.stepOverloads++;
            var state = service.Snapshot();
            if (drivingWorld != null)
            {
                // Fraction of the current sim tick already elapsed — the
                // vehicle visual interpolates between committed poses by
                // this alpha instead of stepping at the 30 Hz tick rate.
                drivingWorld.ApplyFrame(state,
                    Mathf.Clamp01(accumulator / step));
                if (drivingCamera != null)
                {
                    // Applied after the world visuals so its transform wins;
                    // the published facts describe the camera the player
                    // actually sees this frame.
                    drivingCamera.ApplyFrame(state);
                    service.SetDrivingCameraFacts(drivingCamera.Facts);
                }
                else
                {
                    service.SetDrivingCameraFacts(drivingWorld.CameraFacts);
                }
                // Fold the just-published camera facts into this frame's
                // snapshot instead of re-snapshotting after SetFacts.
                if (state.driving != null)
                    state.driving.camera = (drivingCamera != null
                        ? drivingCamera.Facts : drivingWorld.CameraFacts).Copy();
            }
            // Entering the shared network pause neutralizes held local
            // input and requires re-arm, the same cleanup focus loss runs:
            // held steering must not seep through the resume boundary.
            var networkPaused = state.network != null && state.network.paused;
            if (networkPaused && !lastNetworkPaused) ObserveInputCleanup();
            lastNetworkPaused = networkPaused;
            RefreshPresentation(state);
            state.presentation = presentation.Copy();
            shell?.Refresh(state);
            // Cue announcements and icon demonstrations follow the latest
            // published tutorial state — never the other way around.
            if (tutorialPresenter != null)
                tutorialPresenter.ApplyFrame(state,
                    shell != null ? shell.TutorialCueIcon : null);
        }

        // Shared entry for every UI/physical command; the agent channel calls
        // ExecuteAndEmit directly with a fully-formed GameCommand.
        public CommandResult Dispatch(string action,
            IReadOnlyDictionary<string, string> parameters)
        {
            var command = new GameCommand(ProtocolVersion, runId,
                "local-" + (++commandCounter).ToString("x8"), "local", action,
                parameters ?? EmptyParameters.Value);
            return ExecuteAndEmit(command);
        }

        internal CommandResult ExecuteAndEmit(GameCommand command)
        {
            var result = service.Execute(command);
            // Presentation must reflect the post-command state (language,
            // geometry, ownership) before the boundary snapshot is emitted,
            // or a record can claim e.g. language=he with direction=ltr.
            if (result != null)
            {
                var boundaryState = service.Snapshot();
                RefreshPresentation(boundaryState);
                boundaryState.presentation = presentation.Copy();
                bridge?.EmitCommandBoundary(result, boundaryState);
            }
            return result;
        }

        // Async network transitions emit the same state+result boundary a
        // synchronous command does: outcomes land in the identity cache
        // exactly once, then a schema-3 boundary snapshot is published.
        private void DrainNetworkBoundary()
        {
            var outcomes = service.DrainNetworkOutcomes();
            foreach (var outcome in outcomes)
            {
                service.CompleteNetworkCommand(outcome);
                var boundaryState = service.Snapshot();
                RefreshPresentation(boundaryState);
                boundaryState.presentation = presentation.Copy();
                bridge?.EmitCommandBoundary(outcome, boundaryState);
            }
        }

        internal void MarkInputSource(string source)
        {
            lastInputSource = source;
        }

        private void RefreshPresentation(FoundationState state)
        {
            if (state.language != lastLanguage)
            {
                presentation.textDirection = state.language == "he" ? "rtl" : "ltr";
                lastLanguage = state.language;
            }
            // Font/ATG readiness is recomputed at every refresh boundary,
            // not just on locale changes: a broken binding must surface as a
            // warning even in English because the Hebrew control is visible.
            presentation.hebrewFontAvailable = FoundationFonts.IsReady(document);
            presentation.fontWarning = presentation.hebrewFontAvailable
                ? "" : FoundationFonts.Warning;
            var source = AgentControlsInput
                ? "agent"
                : driverInput != null && driverInput.PhysicalActive ? "physical" : "none";
            presentation.inputSource = source;
            lastInputSource = source;
            presentation.screenWidth = Screen.width;
            presentation.screenHeight = Screen.height;
            presentation.validationActive = bridge != null;
            service.SetPresentation(presentation);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) ObserveInputCleanup();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) ObserveInputCleanup();
        }

        // Focus-loss input cleanup goes through the shared command path so
        // the trace shows a real action boundary, not a silent mutation.
        private void ObserveInputCleanup()
        {
            if (service == null) return;
            Dispatch("input.clear", EmptyParameters.Value);
            driverInput?.RequireRearm();
        }

        private void OnApplicationQuit()
        {
            bridge?.Dispose();
            raceBridge?.Dispose();
            lanLink?.Dispose();
        }

        // A development/editor validation launch may point profiles at a
        // separately owned synthetic store shared across fresh capture
        // directories, or at the real user store via the valueless
        // --validation-normal-profiles flag (mutually exclusive with the
        // override); malformed combinations fail loudly instead of silently
        // exercising the wrong store.
        private static string SelectProfilesDirectory(string validationDirectory,
            string persistentDataPath, string[] arguments)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (validationDirectory != null)
            {
                string overrideDirectory = null;
                var normalProfiles = false;
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (arguments[i] == "--validation-normal-profiles")
                    {
                        if (normalProfiles)
                            throw new ArgumentException(
                                "--validation-normal-profiles may be specified at most once.");
                        normalProfiles = true;
                        continue;
                    }
                    if (arguments[i] != "--validation-profile-dir") continue;
                    if (overrideDirectory != null)
                        throw new ArgumentException(
                            "--validation-profile-dir may be specified at most once.");
                    if (i + 1 >= arguments.Length)
                        throw new ArgumentException(
                            "--validation-profile-dir requires a directory value.");
                    var value = arguments[++i];
                    if (string.IsNullOrWhiteSpace(value)
                        || value.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException(
                            "--validation-profile-dir requires a directory value.");
                    overrideDirectory = value;
                }
                if (normalProfiles && overrideDirectory != null)
                    throw new ArgumentException(
                        "--validation-normal-profiles and --validation-profile-dir are mutually exclusive.");
                if (normalProfiles)
                    return Path.Combine(persistentDataPath, "Profiles");
                return overrideDirectory != null
                    ? Path.GetFullPath(overrideDirectory)
                    : Path.Combine(validationDirectory, "profiles");
            }
#endif
            return Path.Combine(persistentDataPath, "Profiles");
        }

        private static string ValidationDirectory()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "--validation-dir")
                    return args[i + 1];
#endif
            return null;
        }

        private static readonly Lazy<IReadOnlyDictionary<string, string>> EmptyParameters =
            new Lazy<IReadOnlyDictionary<string, string>>(
                () => new Dictionary<string, string>(0));
    }
}
