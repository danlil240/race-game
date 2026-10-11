using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
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
        private DrivingWorld drivingWorld;
        private ValidationBridge bridge;
        private FoundationInput driverInput;
        private FoundationShell shell;
        private UIDocument document;
        private PresentationState presentation = new PresentationState();
        private string runId;
        private string lastInputSource = "";
        private string lastLanguage = "";
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
            if (drivingWorld != null)
            {
                var catalog = drivingWorld.Load();
                if (catalog != null) service.SetDrivingContent(catalog);
            }
            driverInput = GetComponent<FoundationInput>();
            shell = GetComponent<FoundationShell>();
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
            driverInput?.Bind(this);
            shell?.Bind(this);
            service.SetPresentation(presentation);
        }

        private void Update()
        {
            // Commands publish their action-boundary state+result before the
            // next observation tick is emitted.
            bridge?.Pump();
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
                service.AdvanceTick();
                // Presentation (input source, geometry, direction) is part of
                // the captured tick boundary — refresh before emitting.
                RefreshPresentation(service.Snapshot());
                bridge?.EmitTick();
            }
            presentation.backlogSteps = (int)(accumulator / step);
            if (steps == MaxStepsPerFrame && accumulator >= step)
                presentation.stepOverloads++;
            var state = service.Snapshot();
            if (drivingWorld != null)
            {
                drivingWorld.ApplyFrame(state);
                service.SetDrivingCameraFacts(drivingWorld.CameraFacts);
                state = service.Snapshot();
            }
            RefreshPresentation(state);
            shell?.Refresh(state);
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
            if (result != null) RefreshPresentation(service.Snapshot());
            bridge?.EmitCommandBoundary(result);
            return result;
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
