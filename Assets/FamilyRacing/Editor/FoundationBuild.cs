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

        private static void ExportAndroid(string flavor, bool development,
            bool visualBenchmark)
        {
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

            var oldExport = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            var oldBundle = EditorUserBuildSettings.buildAppBundle;
            try
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
                EditorUserBuildSettings.buildAppBundle = false;
                FoundationProjectSetup.Create();
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
                    + "; APK build runs separately offline)");
            }
            finally
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = oldExport;
                EditorUserBuildSettings.buildAppBundle = oldBundle;
            }
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
