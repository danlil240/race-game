using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FamilyRacing
{
    // M4 Task 2: automatic tablet/PC render profiles. The component carries
    // the two authored URP pipeline assets (wired by VisualBenchmarkSetup
    // under Generated/VisualBenchmark) and Apply(bool) selects exactly one:
    // the A7 Lite tablet profile (30 fps cap, 0.75 internal render scale,
    // baked environment lighting and simple vehicle shadows, restrained
    // transparency) or the reference-PC profile (60 fps target at 1080p,
    // richer shadows/detail). Neither profile enables real-time
    // reflections, motion blur or expensive full-screen effects, and UI
    // Toolkit panels render at display resolution regardless of render
    // scale, so the interface stays sharp.
    //
    // The component owns NO gameplay state: it never touches collision,
    // checkpoints, route data or visibility layers — the equality test
    // pins that contract.
    public sealed class RenderProfileController : MonoBehaviour
    {
        public const string ProfileTablet = "tablet";
        public const string ProfilePc = "pc";
        public const string ProfileNone = "none";
        public const string ProfileMissing = "missing";

        public const int TabletTargetFrameRate = 30;
        public const int PcTargetFrameRate = 60;
        public const float TabletRenderScale = 0.75f;
        public const float PcRenderScale = 1.0f;

        [SerializeField] private UniversalRenderPipelineAsset tabletPipeline;
        [SerializeField] private UniversalRenderPipelineAsset pcPipeline;

        // "tablet" | "pc" once a profile is actually applied; "none" before
        // the first Apply and "missing" after a refused one — a profile is
        // reported only when its pipeline asset was really installed.
        public string ActiveProfile { get; private set; } = ProfileNone;
        public float ActiveRenderScale { get; private set; }
        public bool TabletActive => ActiveProfile == ProfileTablet;

        // Platform/override resolution, kept static so tests can pin it:
        // --render-profile tablet|pc forces a profile (benchmark evidence);
        // otherwise Android is the tablet target and everything else is PC.
        public static bool ResolveTablet(RuntimePlatform platform,
            string[] commandLineArgs)
        {
            if (commandLineArgs != null)
            {
                for (var i = 0; i < commandLineArgs.Length - 1; i++)
                {
                    if (commandLineArgs[i] != "--render-profile") continue;
                    var value = commandLineArgs[i + 1];
                    if (value == ProfileTablet) return true;
                    if (value == ProfilePc) return false;
                }
            }
            return platform == RuntimePlatform.Android;
        }

        // Select and apply one authored profile. A missing pipeline asset
        // refuses honestly — the previous profile (or none) stays active and
        // ActiveProfile reports "missing", never a false success.
        public void Apply(bool tablet)
        {
            var pipeline = tablet ? tabletPipeline : pcPipeline;
            var wanted = tablet ? ProfileTablet : ProfilePc;
            if (pipeline == null)
            {
                ActiveProfile = ProfileMissing;
                Debug.LogError("RenderProfileController: " + wanted
                    + " pipeline asset is not wired; profile not applied.");
                return;
            }
            // Per-quality-level pipeline wins over GraphicsSettings in
            // players; the render scale lives on the authored asset so the
            // applied scale is read back, not assumed.
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = tablet
                ? TabletTargetFrameRate : PcTargetFrameRate;
            // Unity disables Swappy on GE8320. Avoid its software 30-fps
            // wait while retaining the authored image quality.
            if (tablet && Application.platform == RuntimePlatform.Android
                && SystemInfo.graphicsDeviceName.IndexOf("PowerVR Rogue GE8320",
                    System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Application.targetFrameRate = PcTargetFrameRate;
            }
            ActiveProfile = wanted;
            var urp = QualitySettings.renderPipeline
                as UniversalRenderPipelineAsset;
            ActiveRenderScale = urp != null ? urp.renderScale
                : (tablet ? TabletRenderScale : PcRenderScale);
        }
    }
}
