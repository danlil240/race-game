using System;
using System.IO;
using System.Reflection;

// External-process fixture for Test-AndroidOffline.ps1. The orchestration,
// filesystem checks, argument quoting and exit propagation remain real.
internal static class AndroidToolFixture
{
    private static string Value(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Main(string[] args)
    {
        var executable = Assembly.GetExecutingAssembly().Location;
        var behaviorFile = Path.Combine(Path.GetDirectoryName(executable), "behavior.txt");
        var behavior = File.Exists(behaviorFile) ? File.ReadAllText(behaviorFile).Trim() : "success";
        if (Path.GetFileName(executable).Equals("Unity.exe", StringComparison.OrdinalIgnoreCase))
        {
            var project = Value(args, "-projectPath");
            if (project == null || !Directory.Exists(project)) return 91;
            File.WriteAllLines(Path.Combine(project, "unity-arguments.txt"), args);
            var log = Value(args, "-logFile");
            if (log != null) File.WriteAllText(log, "Fixture export: " + behavior);
            if (behavior == "export-failure") return 29;
            if (behavior == "incomplete-export") return 0;
            var export = Value(args, "--android-export-dir");
            if (export == null) return 92;
            Directory.CreateDirectory(Path.Combine(export, "launcher"));
            Directory.CreateDirectory(Path.Combine(export, "unityLibrary"));
            File.WriteAllText(Path.Combine(export, "settings.gradle"), "include ':launcher', ':unityLibrary'");
            File.WriteAllText(Path.Combine(export, "build.gradle"), "// fixture");
            File.WriteAllText(Path.Combine(export, "launcher", "build.gradle"), "// fixture");
            File.WriteAllText(Path.Combine(export, "unityLibrary", "build.gradle"), "// fixture");
            return 0;
        }

        var gradleProject = Value(args, "--project-dir");
        if (gradleProject == null || !Directory.Exists(gradleProject)) return 93;
        File.WriteAllLines(Path.Combine(gradleProject, "java-arguments.txt"), args);
        // Wrong arguments must not produce an APK that makes the happy path pass.
        if (Array.IndexOf(args, "--offline") < 0 ||
            Array.IndexOf(args, "--no-daemon") < 0 ||
            Array.IndexOf(args, ":launcher:assembleDebug") < 0) return 94;
        var classpath = Value(args, "-classpath");
        if (classpath == null || !File.Exists(classpath)) return 95;
        if (behavior == "dependency-missing")
        {
            Console.Error.WriteLine("No cached version available for offline mode.");
            return 31;
        }
        if (behavior == "no-apk") return 0;
        var apkDirectory = Path.Combine(gradleProject, "launcher", "build", "outputs", "apk", "debug");
        Directory.CreateDirectory(apkDirectory);
        File.WriteAllText(Path.Combine(apkDirectory, "launcher-debug.apk"),
            behavior == "empty-apk" ? "" : "fresh fixture APK");
        Console.WriteLine("Fixture Gradle completed offline.");
        return 0;
    }
}
