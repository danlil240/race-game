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

        public static void Windows()
        {
            FoundationProjectSetup.Create();
            var report = Build("Builds/Windows/FamilyRacing.exe",
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            Report(report, "Windows Mono development build");
        }

        public static void Android()
        {
            // Retain the old entry point, but never implicitly invoke online
            // Gradle. The tooling supplies an isolated export directory.
            ExportAndroid();
        }

        public static void ExportAndroid()
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
                "../Builds/Android/offline")) + Path.DirectorySeparatorChar;
            if (!exportPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Android export must stay under Builds/Android/offline.");
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
                var report = Build(exportPath, BuildTarget.Android,
                    BuildOptions.Development | BuildOptions.AcceptExternalModificationsToPlayer);
                Report(report, "Android Gradle export (APK build runs separately offline)");
            }
            finally
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = oldExport;
                EditorUserBuildSettings.buildAppBundle = oldBundle;
            }
        }

        private static BuildReport Build(string location, BuildTarget target,
            BuildOptions options)
        {
            var scenes = EditorBuildSettings.scenes
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
