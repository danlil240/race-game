using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Composition root: one FoundationService, optional validation bridge
    // (editor/development only), physical input adapter and UI shell share
    // the same Execute path so agent and human commands are equivalent.
    public sealed class FoundationApp : MonoBehaviour
    {
        public const int ProtocolVersion = ValidationBridge.ProtocolVersion;
        // The local logical step cadence is the network simulation rate:
        // the same loop drives both solo sessions and raceBridge.StepTick.
        public const float TickRate =
            (float)Core.Networking.NetworkSettings.SimulationTicksPerSecond;
        // Catch-up work per frame is bounded; unsimulated backlog is kept
        // (never dropped) and reported through presentation diagnostics.
        private const int MaxStepsPerFrame = 8;

        private FoundationService service;
        private Networking.LanSessionLink lanLink;
        private Networking.NetworkRaceBridge raceBridge;
        private DrivingWorld drivingWorld;
        private DrivingCamera drivingCamera;
        private TutorialPresenter tutorialPresenter;
        // M4 Task 5: the race effects/audio presenters consume the same
        // published state (deduped event stream + live motor poses)
        // through the authored presentation catalog. They never mutate
        // simulation state — they are listeners on the boundary.
        private RaceEffectsPresenter raceEffects;
        private RaceAudioPresenter raceAudio;
        private GadgetPresenter gadgetPresenter;
        private ValidationBridge bridge;
        private FoundationInput driverInput;
        private FoundationShell shell;
        private UIDocument document;
        // M4 Task 2: the applied tablet/PC render profile plus the bounded
        // frame-interval recorder. Both are absent-legal: a scene without a
        // controller reports profile "none", and --perf-capture off gives
        // matched runs a no-capture arm for overhead measurement.
        private RenderProfileController renderProfiles;
        private RenderPerformanceRecorder perfRecorder;
        private bool perfRenderingAvailable;
        private string perfReportDirectory;
        private PresentationState presentation = new PresentationState();
        private string runId;
        private string lastInputSource = "";
        private string lastLanguage = "";
        // Shared-pause edge detector: entering a network pause clears held
        // input exactly like a focus loss — resume requires fresh input.
        private bool lastNetworkPaused;
        private Core.Racing.DrivingCameraState currentCameraFacts =
            new Core.Racing.DrivingCameraState();
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
            // Focus-loss cleanup rearms the physical input pump through
            // the Core-side hook: the app.focusLost command and the
            // Unity OnApplicationFocus/OnApplicationPause callbacks
            // share exactly this one path.
            service.FocusCleanupHook = () => driverInput?.RequireRearm();
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
                // M5 Task 4: the gameplay-loop catalog rides the same
                // injection contract — absent or failed conversion means
                // solo.start reports contentUnavailable honestly while
                // practice/LAN stay live.
                if (catalog != null)
                    service.SetGameplayCatalog(drivingWorld.Gameplay);
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
            // Task-5 presentation listeners: bound to the authored
            // catalog once — ApplyFrame then keys every (re)bind off the
            // published race identity itself, so rejoins and fresh races
            // reset cursors without any scene bookkeeping here.
            raceEffects = GetComponent<RaceEffectsPresenter>();
            if (raceEffects == null)
                raceEffects = gameObject.AddComponent<RaceEffectsPresenter>();
            raceAudio = GetComponent<RaceAudioPresenter>();
            if (raceAudio == null)
                raceAudio = gameObject.AddComponent<RaceAudioPresenter>();
            // M5 Task 5: the gadget world presenter mirrors the
            // authoritative pickups/patches/shields onto pooled views —
            // no catalog binding needed (primitive placeholders match
            // the graybox course style; authored effects/cues still ride
            // the presentation catalog through the event triggers).
            gadgetPresenter = GetComponent<GadgetPresenter>();
            if (gadgetPresenter == null)
                gadgetPresenter = gameObject.AddComponent<GadgetPresenter>();
            raceEffects.Bind(
                drivingWorld != null ? drivingWorld.PresentationCatalog : null);
            raceAudio.Bind(
                drivingWorld != null ? drivingWorld.PresentationCatalog : null);
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
            // Render profile applies before the first rendered frame:
            // Android takes the tablet profile, other platforms the PC one;
            // --render-profile tablet|pc overrides for benchmark evidence.
            perfRenderingAvailable = !Application.isBatchMode
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            renderProfiles = GetComponent<RenderProfileController>();
            if (renderProfiles != null)
                renderProfiles.Apply(RenderProfileController.ResolveTablet(
                    Application.platform, Environment.GetCommandLineArgs()));
            if (PerfCaptureEnabled(Environment.GetCommandLineArgs()))
            {
                string memoryMetric, memorySource;
                if (Application.platform == RuntimePlatform.Android)
                {
                    memoryMetric = "process.totalPss";
                    memorySource = "adb dumpsys meminfo";
                }
                else
                {
                    memoryMetric = "process.workingSet";
                    memorySource = "psapi GetProcessMemoryInfo";
                }
                perfRecorder = new RenderPerformanceRecorder(
                    renderProfiles != null ? renderProfiles.ActiveProfile : "none",
                    Application.version + (Debug.isDebugBuild
                        ? "-development" : "-ordinary"),
                    service.ContentVersion,
                    Application.platform.ToString(),
                    perfRenderingAvailable,
                    processMemoryMetric: memoryMetric,
                    processMemorySource: memorySource,
                    processMemoryUnits: "bytes",
                    clockSeconds: () => Time.realtimeSinceStartupAsDouble);
            }
            perfReportDirectory = PerfReportDirectory(
                Environment.GetCommandLineArgs());
            if (validationDir != null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // A validation agent must not depend on window focus: a paused
                // player would freeze Pump and the controller could never attach.
                Application.runInBackground = true;
                bridge = new ValidationBridge(service, runId, validationDir,
                    ExecuteAndEmit, MarkInputSource,
                    ValidationTraceLimitBytes());
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
            // Admission binds the pinned gameplay contract, not the
            // app marketing version: every protocol-3 peer must report
            // exactly "gameplay-4" plus the catalog compatibility token
            // and tuned-content fingerprint before a slot is dealt.
            var lan = new Core.Networking.LanSessionService(
                lanLink, lanLink, Core.Networking.NetworkSettings.GameplayVersion);
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
            // M5 Task 5: the authored gadget catalog seeds the host's
            // authoritative gadget world — absent catalog means
            // gadget-free races, never fabricated defaults. The same
            // validated catalog feeds solo session creation so
            // practice/tutorial free-drive and LAN use one source.
            if (drivingWorld != null)
            {
                raceBridge.SetGadgetContent(drivingWorld.Gadgets);
                service.SetGadgetCatalog(drivingWorld.Gadgets);
            }
            // Task 4: the vehicle-simulation seam is owned here — the solo
            // service and the replication bridge both build their worlds
            // through DrivingWorld, which maps the catalog's backend to an
            // arcade adapter or a dedicated physics scene. Solo and LAN
            // never own a physical world on the same screen: the solo
            // session is null while LAN races and vice versa.
            if (drivingWorld != null)
            {
                service.SetDrivingSimulationFactory(drivingWorld.CreateSimulation);
                raceBridge.SimulationProvider = drivingWorld.CreateSimulation;
                // Task 10: the client's prediction world is a separate
                // single-body scene — never the host's shared roster.
                raceBridge.PredictionSimulationProvider =
                    drivingWorld.CreatePredictionSimulation;
            }
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
            // A shared network pause suppresses held physical input like
            // agent ownership does: the race clock is frozen, so holding
            // controls would only queue input that resumes could consume
            // unseen. Re-arm comes from the pause-edge cleanup below.
            driverInput?.Pump(AgentControlsInput || lastNetworkPaused,
                CurrentScreen);
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
                // The local gameplay modes share this same boundary:
                // campaign/quickRace step their four-racer coordinator,
                // timeTrial its lone session — same input, same clock,
                // same paused freeze as the practice path above.
                service.StepSolo(drivingWorld != null ? drivingWorld.MotorWorld : null);
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
                // Task 11: the bridge's own packet/loss counters ride the
                // same boundary — transport starvation vs session
                // rejection stays observable instead of inferred.
                service.SetNetworkLinkState(
                    raceBridge != null ? raceBridge.LinkState() : null);
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
                var alpha = Mathf.Clamp01(accumulator / step);
                var netRace = state.networkRace;
                var lanRace = netRace != null && netRace.racers != null
                    && netRace.racers.Count > 0;
                // M5 Task 4: campaign/quickRace publish their shared
                // four-racer coordinator state under soloRace.race —
                // same roster render path as LAN but every pose is
                // locally authoritative (no prediction, no transport).
                var soloRaceFrame = state.soloRace != null
                    && state.soloRace.race != null
                    && state.soloRace.race.racers != null
                    && state.soloRace.race.racers.Count > 0;
                // M5 Task 8: the racer views bind the selected profile's
                // durable facts (installed upgrade parts, equipped
                // cosmetics) for the seat it owns.
                drivingWorld.SetLocalProfile(state.savedProfile);
                drivingWorld.ApplyFrame(state, alpha);
                // The LAN roster renders from the host-confirmed race
                // state; the local racer's pose is the smoothed predicted
                // pose on a client, and remote racers interpolate on the
                // client. Neither affects roster/identity facts.
                MotorState lanLocalPose = null;
                if (lanRace)
                    lanLocalPose = ApplyNetworkFrame(netRace, state, alpha);
                else if (soloRaceFrame)
                    lanLocalPose = ApplySoloRaceFrame(state.soloRace, state, alpha);
                else
                {
                    lastPoseSources.Clear();
                    drivingWorld.ApplyRaceFrame(null); // retire racer views
                }
                if (drivingCamera != null)
                {
                    // Applied after the world visuals so its transform wins;
                    // the published facts describe the camera the player
                    // actually sees this frame. During a LAN race the
                    // camera follows the locally owned racer's rendered
                    // (predicted/interpolated) pose — never a remote pose.
                    if (lanRace || soloRaceFrame)
                    {
                        var overhead = state.draftProfile != null
                            && state.draftProfile.camera == "overhead";
                        var reduced = state.draftProfile != null
                            && state.draftProfile.reducedCameraShake;
                        drivingCamera.ApplyMotor(lanLocalPose, overhead,
                            reduced, Time.unscaledDeltaTime,
                            state.draftProfile != null
                                ? state.draftProfile.effectsVolume : 0.8f);
                    }
                    else
                    {
                        drivingCamera.ApplyFrame(state);
                    }
                    service.SetDrivingCameraFacts(drivingCamera.Facts);
                    currentCameraFacts = drivingCamera.Facts;
                }
                else
                {
                    service.SetDrivingCameraFacts(drivingWorld.CameraFacts);
                    currentCameraFacts = drivingWorld.CameraFacts;
                }
                // Fold the just-published camera facts into this frame's
                // snapshot instead of re-snapshotting after SetFacts.
                if (state.driving != null)
                    state.driving.camera = (drivingCamera != null
                        ? drivingCamera.Facts : drivingWorld.CameraFacts).Copy();
                else if (state.soloRace != null && state.soloRace.trial != null)
                    state.soloRace.trial.camera = (drivingCamera != null
                        ? drivingCamera.Facts : drivingWorld.CameraFacts).Copy();
            }
            // Task-5 presentation: the effects/audio presenters read the
            // same latest snapshot — after world/camera applied, before
            // the presentation fold — so their published facts describe
            // this rendered frame exactly. Pause/exit context comes from
            // the state itself: no race context stops every loop, a
            // paused frame suspends them, a fresh race identity resets
            // the dedupe cursor without replaying history.
            if (raceEffects != null)
                raceEffects.ApplyFrame(state, Time.unscaledDeltaTime);
            if (raceAudio != null)
                raceAudio.ApplyFrame(state, Time.unscaledDeltaTime);
            // The pooled gadget world views mirror the latest published
            // state — pickups, foam patches and shield bubbles read the
            // authoritative slice exactly like the effects presenter.
            if (gadgetPresenter != null)
                gadgetPresenter.ApplyFrame(state, Time.unscaledDeltaTime);
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
            // One rendered-frame interval per Update, classified by the
            // published phase/camera/vehicle — unscaled wall time, the same
            // clock the step accumulator reads.
            if (perfRecorder != null)
            {
                perfRecorder.RecordFrame(Time.unscaledDeltaTime * 1000.0,
                    PerfPhase(state), PerfCameraMode(), PerfVehicleClass(state));
                DrainPerformanceReportEvents();
            }
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
            if (result == null) return null;
            var boundaryState = service.Snapshot();
            // A LAN recovery press becomes a deduplicated one-shot action
            // on the bridge — the command id is the action identity, and
            // the host's action.queued/consumed/cancelled events are its
            // honest acknowledgment. The flag itself never rides the input
            // frame (FoundationService suppresses it on lan-race) so a
            // press can never queue two recoveries.
            if (result.status == CommandResult.Applied
                && command.Action == "input.set"
                && raceBridge != null
                && boundaryState.screen == FoundationService.ScreenLanRace)
            {
                string recoverValue;
                if (command.Parameters != null
                    && command.Parameters.TryGetValue("recover", out recoverValue)
                    && recoverValue == "true")
                    raceBridge.SubmitAction(command.CommandId);
            }
            RefreshPresentation(boundaryState);
            boundaryState.presentation = presentation.Copy();
            bridge?.EmitCommandBoundary(result, boundaryState);
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

        // Publishes the LAN race frame into the world with per-participant
        // pose overrides: the local racer renders its smoothed predicted
        // pose (host: the authoritative pose is already local truth),
        // remote racers render interpolated poses where a client-side lane
        // exists. Returns the locally owned racer's rendered pose for the
        // camera to follow; null when the local participant is not seated.
        private MotorState ApplyNetworkFrame(NetworkRaceState race,
            FoundationState state, float alpha)
        {
            var localId = state.network != null
                ? state.network.localParticipantId : "";
            var now = Time.realtimeSinceStartupAsDouble;
            var overrides = new Dictionary<string, MotorState>(
                StringComparer.Ordinal);
            lastPoseSources.Clear();
            MotorState localPose = null;
            foreach (var racer in race.racers)
            {
                if (racer == null || string.IsNullOrEmpty(racer.participantId))
                    continue;
                // Default: the committed host state is the pose source.
                lastPoseSources[racer.participantId] = "authoritative";
                if (racer.participantId == localId)
                {
                    var predicted = raceBridge != null
                        ? raceBridge.SmoothedLocalMotor(now) : null;
                    localPose = predicted;
                    if (localPose == null && racer.session != null)
                        localPose = racer.session.motor;
                    if (localPose != null)
                        overrides[racer.participantId] = localPose;
                    if (predicted != null)
                        lastPoseSources[racer.participantId] = "predicted";
                }
                else
                {
                    var remote = raceBridge != null
                        ? raceBridge.SampleRemotePose(racer.participantId, now)
                        : null;
                    if (remote != null)
                    {
                        overrides[racer.participantId] = remote;
                        lastPoseSources[racer.participantId] =
                            "interpolated";
                    }
                }
            }
            // The roster resolves human cosmetics (avatar + color slot);
            // racers without a roster entry are AI and map by slot.
            drivingWorld.ApplyRaceFrame(race, alpha, overrides,
                state.network != null ? state.network.participants : null);
            return localPose;
        }

        // Publishes a local four-racer frame (M5 Task 4 campaign /
        // quickRace) into the world: every pose is locally authoritative
        // — no prediction, no interpolation, no transport — and the
        // roster carries exactly the one human seat so cosmetics resolve
        // like a LAN local racer while AI seats fall to slot mapping.
        // Returns the human racer's pose for the camera to follow.
        private MotorState ApplySoloRaceFrame(SoloRaceState solo,
            FoundationState state, float alpha)
        {
            lastPoseSources.Clear();
            MotorState localPose = null;
            foreach (var racer in solo.race.racers)
            {
                if (racer == null || string.IsNullOrEmpty(racer.participantId))
                    continue;
                lastPoseSources[racer.participantId] = "authoritative";
                if (racer.participantId == solo.participantId
                    && racer.session != null)
                    localPose = racer.session.motor;
            }
            // The cosmetics seam reads avatar/color off participant rows;
            // the local human is the only rostered participant.
            var roster = new List<Core.Networking.ParticipantState>(1);
            if (state.draftProfile != null)
            {
                roster.Add(new Core.Networking.ParticipantState
                {
                    participantId = solo.participantId,
                    slot = 0,
                    occupied = true,
                    connected = true,
                    controlSource = "local",
                    avatar = state.draftProfile.avatar ?? "",
                    colorIndex = state.draftProfile.colorIndex
                });
            }
            drivingWorld.ApplyRaceFrame(solo.race, alpha, null, roster);
            return localPose;
        }

        // The last ApplyNetworkFrame's pose-source per participant — the
        // world writes "authoritative" on every fact; these labels
        // override it where a predicted/interpolated pose drove the view.
        private readonly Dictionary<string, string> lastPoseSources =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Snapshot of the world's racer facts with the app's pose-source
        // labels merged in — the facts the schemas actually stream.
        private List<RacerPresentationState> BuildRacerFacts()
        {
            var source = drivingWorld != null ? drivingWorld.RacerFacts : null;
            var list = new List<RacerPresentationState>(
                source != null ? source.Count : 0);
            if (source != null)
                foreach (var fact in source)
                {
                    var copy = fact != null ? fact.Copy() : null;
                    if (copy != null)
                    {
                        string poseSource;
                        if (copy.participantId != null
                            && lastPoseSources.TryGetValue(
                                copy.participantId, out poseSource))
                            copy.poseSource = poseSource;
                    }
                    list.Add(copy);
                }
            return list;
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
            // The actual render camera is per-device presentation truth:
            // solo also carries it under driving.camera, LAN racers carry
            // it here because networkRace is host-confirmed shared state.
            presentation.camera = currentCameraFacts != null
                ? currentCameraFacts.Copy() : null;
            // Schema-5 presentation truth: the profile actually applied and
            // the bounded performance summary ride every boundary, so
            // paused/menu transitions publish the same facts racing does.
            presentation.renderProfileId = renderProfiles != null
                ? renderProfiles.ActiveProfile : "none";
            presentation.renderScale = renderProfiles != null
                ? renderProfiles.ActiveRenderScale : 0f;
            presentation.targetFrameRate = Application.targetFrameRate;
            presentation.renderingAvailable = perfRenderingAvailable;
            // The course visual actually bound on this device — the
            // dressed sunny-island root on the benchmark, never silently
            // the graybox placeholder.
            presentation.courseVisualId = drivingWorld != null
                ? drivingWorld.CourseVisualId : "none";
            // Schema-5 racer facts: the actual bound visual/character
            // identities (or honest fallback tokens) with the real
            // pose-source label overlaid.
            presentation.racers = BuildRacerFacts();
            presentation.performance = perfRecorder != null
                ? perfRecorder.Summary() : null;
            // Schema-5 effects/audio truth (M4 Task 5): publish only
            // while a presentation catalog is bound — an unbound scene
            // reports null slices, never a fabricated catalog identity.
            presentation.effects = raceEffects != null && raceEffects.Bound
                ? raceEffects.Facts : null;
            presentation.audio = raceAudio != null && raceAudio.Bound
                ? raceAudio.Facts : null;
            service.SetPresentation(presentation);
        }

        // Recorder phase vocabulary mirrors the published lifecycle: paused
        // (either pause flag), the solo driving phase, the LAN session
        // phase, otherwise menu. "loading" is reserved for a real loading
        // state; this build never fabricates one.
        private static string PerfPhase(FoundationState state)
        {
            if (state == null) return RenderPerformanceCapture.PhaseMenu;
            if (state.paused || (state.network != null && state.network.paused))
                return RenderPerformanceCapture.PhasePaused;
            if (state.driving != null && state.driving.phase == "countdown")
                return RenderPerformanceCapture.PhaseCountdown;
            if (state.driving != null && state.driving.phase == "running")
                return RenderPerformanceCapture.PhaseRacing;
            if (state.driving != null && state.driving.phase == "finished")
                return RenderPerformanceCapture.PhaseResults;
            var net = state.network;
            if (net != null)
            {
                if (net.phase == "countdown")
                    return RenderPerformanceCapture.PhaseCountdown;
                if (net.phase == "racing")
                    return RenderPerformanceCapture.PhaseRacing;
                if (net.phase == "results")
                    return RenderPerformanceCapture.PhaseResults;
            }
            return RenderPerformanceCapture.PhaseMenu;
        }

        private string PerfCameraMode()
        {
            return currentCameraFacts != null && currentCameraFacts.mode != null
                ? currentCameraFacts.mode : "none";
        }

        // The vehicle class the frame rendered: the solo vehicle id, or the
        // locally owned LAN racer's id — never a remote racer's.
        private static string PerfVehicleClass(FoundationState state)
        {
            if (state == null) return "";
            if (state.driving != null && state.driving.vehicleId != null)
                return state.driving.vehicleId;
            var race = state.networkRace;
            var localId = state.network != null
                ? state.network.localParticipantId : null;
            if (race != null && race.racers != null && localId != null)
                foreach (var racer in race.racers)
                    if (racer != null && racer.participantId == localId)
                        return racer.vehicleId ?? "";
            return "";
        }

        // Report-write outcomes become sequenced stream records when a
        // validation stream exists; ordinary builds rely on the report file
        // and the presentation summary's reportStatus/reportError fields.
        private void DrainPerformanceReportEvents()
        {
            if (perfRecorder == null || bridge == null) return;
            var events = perfRecorder.DrainReportEvents();
            if (events == null) return;
            foreach (var reportEvent in events)
                bridge.EmitPerformance(reportEvent.file ?? "",
                    reportEvent.status, reportEvent.error,
                    service.Snapshot().tick);
        }

        private static bool PerfCaptureEnabled(string[] args)
        {
            if (args == null) return true;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--perf-capture") continue;
                var value = args[i + 1];
                return !(value == "off" || value == "0" || value == "false");
            }
            return true;
        }

        // --perf-report-dir <dir>: compact perf-report-<runId>.json written
        // at quit on a background writer. Without the flag the report still
        // lands under persistentDataPath/performance so ordinary benchmark
        // builds produce the same evidence without any control socket.
        private static string PerfReportDirectory(string[] args)
        {
            if (args != null)
                for (var i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--perf-report-dir")
                    {
                        var value = args[i + 1];
                        if (!string.IsNullOrWhiteSpace(value)
                            && !value.StartsWith("--",
                                StringComparison.Ordinal))
                            return Path.GetFullPath(value);
                    }
            return Path.Combine(Application.persistentDataPath,
                "performance");
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
        // app.focusLost carries the whole cleanup (held input + queued
        // actions + physical-input rearm via FocusCleanupHook), so the OS
        // callback and the agent command exercise identical code.
        private void ObserveInputCleanup()
        {
            if (service == null) return;
            Dispatch("app.focusLost", EmptyParameters.Value);
        }

        private void OnApplicationQuit()
        {
            // The compact performance report is a mandatory artifact in both
            // development and ordinary builds: queue the write on the
            // recorder's writer thread, flush it before exit, and emit the
            // outcome record while the stream can still carry it.
            if (perfRecorder != null)
            {
                try
                {
                    perfRecorder.RequestReport(Path.Combine(
                        perfReportDirectory, "perf-report-" + runId + ".json"));
                    perfRecorder.FlushReportWrites();
                    DrainPerformanceReportEvents();
                }
                catch (Exception) { }
                perfRecorder.Dispose();
            }
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

        // --validation-trace-mb <N>: the still-bounded capture ceiling for
        // this run, in MiB. The default is the shared 256 MiB bound;
        // physical-diagnostic streams run ~500 KB/s of boundary data, so
        // qualification launches raise the bound deliberately (e.g. 1024)
        // instead of dropping required fields. Values outside 64-2048 MiB
        // clamp into range — never unbounded, never absurdly small.
        private static long ValidationTraceLimitBytes()
        {
            const long mib = 1024L * 1024L;
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--validation-trace-mb") continue;
                int mib_;
                if (int.TryParse(args[i + 1], out mib_))
                    return Math.Max(64, Math.Min(2048, mib_)) * mib;
            }
            return Automation.StateStream.DefaultMaxTraceBytes;
        }

        private static readonly Lazy<IReadOnlyDictionary<string, string>> EmptyParameters =
            new Lazy<IReadOnlyDictionary<string, string>>(
                () => new Dictionary<string, string>(0));
    }
}
