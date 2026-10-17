using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // Build entry points for the foundation shell. Called in batch mode via
    // -executeMethod; failures surface as logged errors plus a nonzero
    // editor exit through the exception.
    public static class FoundationBuild
    {
        [MenuItem("FamilyRacing/Project Setup")]
        public static void Setup()
        {
            FoundationProjectSetup.Create();
            Debug.Log("FoundationProjectSetup: done");
        }

        [MenuItem("FamilyRacing/Physics Lab Setup")]
        public static void PhysicsLabSetup()
        {
            VehiclePhysicsSetup.Create();
            Debug.Log("VehiclePhysicsSetup: done — open " +
                "Assets/FamilyRacing/Generated/Physics/PhysicsLab.unity " +
                "to drive the physical car laboratory.");
        }

        public static void Windows()
        {
            FoundationProjectSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            Report(report, "Windows Mono development build");
        }

        // Ordinary family build: same content and backend, no development
        // flag — validation endpoints are compiled out (DEVELOPMENT_BUILD
        // undefined), so --validation-dir is ignored and no socket opens.
        public static void WindowsOrdinary()
        {
            FoundationProjectSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-Ordinary/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.None);
            Report(report, "Windows Mono ordinary build (no validation)");
        }

        // Opt-in physical-backend development player: regenerates the
        // physics content (tunings, vehicles, course, lab + race scenes)
        // and builds the self-bootstrapping PhysicsRace scene — the same
        // validation endpoints the ordinary development player carries,
        // but with the PhysicalAssisted catalog and the shared graybox
        // course. Explicit scene override so the lab/race scene never
        // has to join EditorBuildSettings (it stays opt-in developer
        // content per VehiclePhysicsSetup's contract).
        public static void WindowsPhysics()
        {
            FoundationProjectSetup.Create();
            VehiclePhysicsSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-Physics/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.Development,
                new[]
                {
                    "Assets/FamilyRacing/Generated/Physics/PhysicsRace.unity"
                });
            Report(report, "Windows Mono physics development build");
        }

        // Visual benchmark (M4): repeatable benchmark scene + definition,
        // then build flavors for the measurement matrix. The benchmark
        // scene is passed as an explicit scene override so it never has
        // to join EditorBuildSettings — the graybox bootstrap stays the
        // default player scene.
        public static void VisualBenchmarkSetup()
        {
            FoundationProjectSetup.Create();
            FamilyRacing.EditorTools.VisualBenchmarkSetup.Create();
            Debug.Log("VisualBenchmarkSetup: done — benchmark scene at " +
                FamilyRacing.EditorTools.VisualBenchmarkSetup.ScenePath);
        }

        public static void WindowsVisualBenchmark()
        {
            FoundationProjectSetup.Create();
            FamilyRacing.EditorTools.VisualBenchmarkSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-VisualBenchmark/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.Development,
                BenchmarkScenes());
            Report(report, "Windows Mono visual-benchmark development build");
        }

        // Ordinary benchmark build: same visual-1 content and backend,
        // no development flag — validation endpoints are compiled out.
        public static void WindowsVisualBenchmarkOrdinary()
        {
            FoundationProjectSetup.Create();
            FamilyRacing.EditorTools.VisualBenchmarkSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-VisualBenchmark-Ordinary/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.None,
                BenchmarkScenes());
            Report(report, "Windows Mono visual-benchmark ordinary build (no validation)");
        }

        private static string[] BenchmarkScenes()
        {
            return new[]
            {
                FamilyRacing.EditorTools.VisualBenchmarkSetup.ScenePath
            };
        }

        // --- M6 Task 4: full-content flavors ---------------------------
        // The content gate runs BEFORE any other packaging step so an
        // incomplete inventory can never emit a "full-content" player:
        // on today's un-authored tree every flavor below fails at
        // FullContentSetup — that is the intended, test-pinned behavior.
        // The two preview flavors are development-only: they bind only
        // authored content through generic catalog validation and stamp
        // contentStage=partial, so a preview can never stand in as an
        // ordinary or accepted full-content candidate.

        public static void FullContentSetup()
        {
            FamilyRacing.EditorTools.FullContentSetup.Create();
            FoundationProjectSetup.Create();
            Debug.Log("FullContentSetup: production content valid");
        }

        public static void WindowsFullContent()
        {
            FamilyRacing.EditorTools.FullContentSetup.Create();
            FoundationProjectSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-FullContent/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            Report(report, "Windows Mono full-content development build");
            FamilyRacing.EditorTools.FullContentSetup.WriteManifest(
                "Builds/Windows-FullContent", "Development", "complete");
        }

        // Ordinary full-content build: same validated content, no
        // development flag — validation endpoints are compiled out.
        public static void WindowsFullContentOrdinary()
        {
            FamilyRacing.EditorTools.FullContentSetup.Create();
            FoundationProjectSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-FullContent-Ordinary/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.None);
            Report(report, "Windows Mono full-content ordinary build (no validation)");
            FamilyRacing.EditorTools.FullContentSetup.WriteManifest(
                "Builds/Windows-FullContent-Ordinary", "Ordinary", "complete");
        }

        // Development-only intermediate check: generic catalog
        // validation over whatever is authored, contentStage=partial.
        public static void WindowsFullContentPreview()
        {
            FamilyRacing.EditorTools.FullContentSetup.CreatePreview();
            FoundationProjectSetup.Create();
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-FullContent-Preview/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            Report(report, "Windows Mono full-content preview build (partial content)");
            FamilyRacing.EditorTools.FullContentSetup.WriteManifest(
                "Builds/Windows-FullContent-Preview", "Development", "partial");
        }

        public static void ExportAndroidFullContent()
        {
            ExportAndroid("Android-FullContent", development: true,
                visualBenchmark: false, contentStage: "complete");
        }

        public static void ExportAndroidFullContentOrdinary()
        {
            ExportAndroid("Android-FullContent-Ordinary", development: false,
                visualBenchmark: false, contentStage: "complete");
        }

        public static void ExportAndroidFullContentPreview()
        {
            ExportAndroid("Android-FullContent-Preview", development: true,
                visualBenchmark: false, contentStage: "partial");
        }

        public static void Android()
        {
            // Retain the old entry point, but never implicitly invoke online
            // Gradle. The tooling supplies an isolated export directory.
            ExportAndroid();
        }

        public static void ExportAndroid()
        {
            ExportAndroid("Android", development: true, visualBenchmark: false);
        }

        // Ordinary graybox export: same content without the development
        // flag — the previously documented-but-unautomated ordinary
        // Android path, now reachable through the offline wrapper.
        public static void ExportAndroidOrdinary()
        {
            ExportAndroid("Android-Ordinary", development: false, visualBenchmark: false);
        }

        public static void ExportAndroidVisualBenchmark()
        {
            ExportAndroid("Android-VisualBenchmark", development: true, visualBenchmark: true);
        }

        // Ordinary benchmark export: visual-1 content with the
        // development flag removed, so the validation bridge/listener is
        // compiled out of the APK exactly like the ordinary Windows
        // player.
        public static void ExportAndroidVisualBenchmarkOrdinary()
        {
            ExportAndroid("Android-VisualBenchmark-Ordinary", development: false, visualBenchmark: true);
        }

        // --- M7 Task 1: family release flavors --------------------------
        // Ordinary-only full-content entry points that stamp the durable
        // release identity (Release/release.json) onto the validated
        // player. The production content gate, generic setup and release
        // apply all run before packaging; a failed build can never leave
        // the temporary Android export flags flipped.
        public static void WindowsFamilyRelease()
        {
            FamilyRacing.EditorTools.FullContentSetup.Create();
            FoundationProjectSetup.Create();
            FamilyReleaseSettings.Apply(
                FamilyReleaseSettings.ResolveConfigPath());
            NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.StandaloneWindows64);
            var report = Build("Builds/Windows-FamilyRelease/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.None);
            Report(report, "Windows Mono family release build (no validation)");
            FamilyRacing.EditorTools.FullContentSetup.WriteManifest(
                "Builds/Windows-FamilyRelease", "Ordinary", "complete");
        }

        public static void ExportAndroidFamilyRelease()
        {
            ExportAndroid("Android-FamilyRelease", development: false,
                visualBenchmark: false, contentStage: "complete",
                releaseConfigPath: FamilyReleaseSettings.ResolveConfigPath());
        }

        private static void ExportAndroid(string flavor, bool development,
            bool visualBenchmark, string contentStage = null,
            string releaseConfigPath = null)
        {
            // The export flags are editor state, not per-build state:
            // capture them first, flip inside try, and restore in finally
            // so ANY failure — content gate, argument validation, missing
            // Android support, or the build itself — leaves the editor
            // exactly as it found it.
            var oldExport = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            var oldBundle = EditorUserBuildSettings.buildAppBundle;
            try
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
                EditorUserBuildSettings.buildAppBundle = false;

                // Full-content flavors run their content gate before any
                // other packaging step so a catalog defect can never
                // reach packaging.
                if (contentStage == "complete")
                    FamilyRacing.EditorTools.FullContentSetup.Create();
                else if (contentStage == "partial")
                    FamilyRacing.EditorTools.FullContentSetup.CreatePreview();
                else if (contentStage != null)
                    throw new ArgumentException("Unknown content stage: "
                        + contentStage);

                var args = Environment.GetCommandLineArgs();
                string exportPath = null;
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] != "--android-export-dir") continue;
                    if (exportPath != null || i + 1 == args.Length)
                        throw new ArgumentException("Expected one --android-export-dir value.");
                    exportPath = args[++i];
                }
                if (string.IsNullOrEmpty(exportPath))
                    throw new ArgumentException("Use Tools/Verify.ps1 -Mode Android to export and build offline.");
                exportPath = Path.GetFullPath(exportPath);
                var root = Path.GetFullPath(Path.Combine(Application.dataPath,
                    "../Builds/" + flavor + "/offline")) + Path.DirectorySeparatorChar;
                if (!exportPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Android export must stay under Builds/" + flavor + "/offline.");
                if (Directory.Exists(exportPath) || File.Exists(exportPath))
                    throw new IOException("Android export directory must be new: " + exportPath);
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                    throw new InvalidOperationException("Android Build Support is not installed.");

                FoundationProjectSetup.Create();
                // Release flavors re-apply the resolved config after
                // normal setup: the ordering guarantees the built player
                // carries the declared release identity even when the
                // config path is overridden by tooling.
                if (releaseConfigPath != null)
                    FamilyReleaseSettings.Apply(releaseConfigPath);
                if (visualBenchmark)
                    FamilyRacing.EditorTools.VisualBenchmarkSetup.Create();
                NetworkPrototypeSetup.VerifyBuildConfiguration(BuildTarget.Android);
                var report = Build(exportPath, BuildTarget.Android,
                    (development ? BuildOptions.Development : BuildOptions.None)
                        | BuildOptions.AcceptExternalModificationsToPlayer,
                    visualBenchmark ? BenchmarkScenes() : null);
                Report(report, "Android Gradle export"
                    + (development ? " (development" : " (ordinary")
                    + (visualBenchmark ? ", visual-benchmark" : "")
                    + (contentStage != null
                        ? ", full-content " + contentStage : "")
                    + "; APK build runs separately offline)");
            }
            finally
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = oldExport;
                EditorUserBuildSettings.buildAppBundle = oldBundle;
            }
            // The manifest lands only after a successful export — the
            // packaging claim can never outlive a failed build.
            if (contentStage != null)
                FamilyRacing.EditorTools.FullContentSetup.WriteManifest(
                    "Builds/" + flavor,
                    development ? "Development" : "Ordinary",
                    contentStage);
        }

        private static BuildReport Build(string location, BuildTarget target,
            BuildOptions options, string[] scenesOverride = null)
        {
            var scenes = scenesOverride != null && scenesOverride.Length > 0
                ? scenesOverride
                : EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .ToArray();
            if (scenes.Length == 0)
                throw new InvalidOperationException("No enabled scenes in build settings.");
            var buildOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = location,
                target = target,
                options = options
            };
            return BuildPipeline.BuildPlayer(buildOptions);
        }

        private static void Report(BuildReport report, string label)
        {
            var summary = report.summary;
            Debug.Log(label + ": result=" + summary.result +
                " errors=" + summary.totalErrors +
                " warnings=" + summary.totalWarnings +
                " size=" + summary.totalSize +
                " output=" + summary.outputPath);
            if (summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException(label + " failed: " + summary.result);
        }
    }
}
