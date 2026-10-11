using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Automation;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using UnityEngine;

namespace FamilyRacing
{
    // Composition root: one FoundationService, optional validation bridge
    // (editor/development only), physical input adapter and UI shell share
    // the same Execute path so agent and human commands are equivalent.
    public sealed class FoundationApp : MonoBehaviour
    {
        public const int ProtocolVersion = ValidationBridge.ProtocolVersion;
        public const float TickRate = 30f;

        private FoundationService service;
        private ValidationBridge bridge;
        private FoundationInput driverInput;
        private FoundationShell shell;
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
            driverInput = GetComponent<FoundationInput>();
            shell = GetComponent<FoundationShell>();
            presentation.buildTarget = Application.platform.ToString();
            if (validationDir != null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
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
            while (accumulator >= step)
            {
                accumulator -= step;
                service.AdvanceTick();
                // Presentation (input source, geometry, direction) is part of
                // the captured tick boundary — refresh before emitting.
                RefreshPresentation(service.Snapshot());
                bridge?.EmitTick();
            }
            var state = service.Snapshot();
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
            var languageChanged = state.language != lastLanguage;
            if (languageChanged)
            {
                presentation.textDirection = state.language == "he" ? "rtl" : "ltr";
                // No Hebrew font asset ships in this iteration: an explicit
                // qualification warning replaces silently wrong glyphs.
                presentation.hebrewFontAvailable = false;
                presentation.fontWarning = state.language == "he"
                    ? "Hebrew font asset missing; text shows fallback glyphs (qualification gap)."
                    : "";
                lastLanguage = state.language;
            }
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
            driverInput?.ResetPhysical();
        }

        private void OnApplicationQuit()
        {
            bridge?.Dispose();
        }

        // A development/editor validation launch may point profiles at a
        // separately owned synthetic store shared across fresh capture
        // directories; malformed overrides fail loudly instead of silently
        // exercising the wrong store.
        private static string SelectProfilesDirectory(string validationDirectory,
            string persistentDataPath, string[] arguments)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (validationDirectory != null)
            {
                string overrideDirectory = null;
                for (var i = 0; i < arguments.Length; i++)
                {
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
