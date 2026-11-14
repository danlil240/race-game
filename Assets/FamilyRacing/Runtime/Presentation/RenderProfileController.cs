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
        // Experiment 2026-11-14: uncapped the A7 Lite lands on a 33/33/16 ms
        // mix (~36 fps); a 30 cap is tried for an even 33 ms cadence.
        public const int GE8320TargetFrameRate = 30;
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
        // True on an actual Android tablet: open-world scenery, fog and the
        // rear-view mirror scale down so the A7 Lite GPU keeps a playable rate.
        public static bool LowEndDevice { get; private set; }

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
                Application.targetFrameRate = GE8320TargetFrameRate;
            }
            ActiveProfile = wanted;
            // Real tablet hardware (not a forced benchmark profile on PC)
            // also trims the long-race view distance; see LongRaceVisibility.
            LowEndDevice = tablet && Application.platform == RuntimePlatform.Android;
            var urp = QualitySettings.renderPipeline
                as UniversalRenderPipelineAsset;
            // A shorter shadow range on the device itself (never in the
            // editor or on PC). Resolution stays at the authored scale:
            // cutting it made the game look visibly worse.
            if (LowEndDevice && urp != null)
            {
                urp.shadowDistance = Mathf.Min(urp.shadowDistance, 20f);
            }
            ActiveRenderScale = urp != null ? urp.renderScale
                : (tablet ? TabletRenderScale : PcRenderScale);
        }
        // Field render diagnostics on real tablets: one RENDER_STATS line
        // every two seconds with what a frame actually costs (draw calls,
        // batches, set-pass calls, triangles, CPU/GPU frame time), so
        // tablet optimisation targets the measured cost.
        private Unity.Profiling.ProfilerRecorder drawCalls, batches, setPass, triangles, vertices, mainThread;
        private readonly FrameTiming[] timings = new FrameTiming[1];
        private float nextStats;
        private void OnEnable()
        {
            drawCalls = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Draw Calls Count");
            batches = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Batches Count");
            setPass = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "SetPass Calls Count");
            triangles = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Triangles Count");
            vertices = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Vertices Count");
            mainThread = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Internal, "Main Thread", 30);
        }
        private void OnDisable()
        {
            drawCalls.Dispose(); batches.Dispose(); setPass.Dispose();
            triangles.Dispose(); vertices.Dispose(); mainThread.Dispose();
        }
        private void Update()
        {
            if (!LowEndDevice) return;
            FrameTimingManager.CaptureFrameTimings();
            if (Time.unscaledTime < nextStats) return;
            nextStats = Time.unscaledTime + 2f;
            var got = FrameTimingManager.GetLatestTimings(1, timings);
            double mainMs = 0; var n = mainThread.Count;
            for (var i = 0; i < n; i++) mainMs += mainThread.GetSample(i).Value * 1e-6;
            if (n > 0) mainMs /= n;
            var cam = Camera.main;
            Debug.Log("RENDER_STATS draws=" + drawCalls.LastValue
                + " batches=" + batches.LastValue
                + " setpass=" + setPass.LastValue
                + " tris=" + triangles.LastValue
                + " verts=" + vertices.LastValue
                + " mainMs=" + mainMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " cpuMs=" + (got > 0 ? timings[0].cpuFrameTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "na")
                + " gpuMs=" + (got > 0 ? timings[0].gpuFrameTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "na")
                + " screen=" + Screen.width + "x" + Screen.height
                + " scale=" + ActiveRenderScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " far=" + (cam != null ? cam.farClipPlane.ToString("0") : "na")
                + " gpu=" + SystemInfo.graphicsDeviceName
                + " fps=" + (1f / Mathf.Max(0.0001f, Time.smoothDeltaTime)).ToString("0"));
        }
    }
}
