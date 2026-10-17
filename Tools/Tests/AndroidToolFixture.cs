using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

// External-process fixture for Test-AndroidOffline.ps1 and
// Test-FamilyReleaseTooling.ps1. The orchestration, filesystem checks,
// argument quoting and exit propagation remain real; Unity.exe, Gradle
// (java --project-dir), apksigner (java -jar), zipalign and aapt are
// simulated from per-tool behavior files.
internal static class AndroidToolFixture
{
    private static string Value(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ReadBehavior(string directory, string file, string fallback)
    {
        var path = Path.Combine(directory, file);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using (var writer = new StreamWriter(entry.Open()))
            writer.Write(content);
    }

    // Minimal valid ZIP posing as an unsigned APK: the signing
    // inspection only sees entry names, so a manifest placeholder and
    // the ARM64 payload path are enough.
    private static void WriteFixtureApk(string path, bool arm64)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "AndroidManifest.xml", "fixture manifest");
            WriteEntry(zip, "classes.dex", "fixture dex");
            if (arm64)
                WriteEntry(zip, "lib/arm64-v8a/libil2cpp.so", "fixture native");
        }
    }

    private static int RunApkSigner(string directory, string[] args)
    {
        File.AppendAllLines(Path.Combine(directory, "apksigner-arguments.txt"), args);
        var behavior = ReadBehavior(directory, "apksigner-behavior.txt", "success");
        var jarIndex = Array.IndexOf(args, "-jar");
        var rest = args.Skip(jarIndex + 2).ToArray();
        if (rest.Length == 0) return 95;
        if (rest[0] == "sign")
        {
            if (behavior == "sign-failure")
            {
                Console.Error.WriteLine("fixture apksigner: signing failed");
                return 43;
            }
            var outIndex = Array.IndexOf(rest, "--out");
            if (outIndex < 0 || outIndex + 1 >= rest.Length) return 96;
            var bytes = File.ReadAllBytes(rest[rest.Length - 1]);
            // "Signing" appends a marker so published bytes provably
            // came through the sign step, not a copy of the input.
            using (var output = new FileStream(rest[outIndex + 1], FileMode.Create))
            {
                output.Write(bytes, 0, bytes.Length);
                var marker = System.Text.Encoding.ASCII.GetBytes("SIGNED-BY-FIXTURE");
                output.Write(marker, 0, marker.Length);
            }
            Console.WriteLine("fixture apksigner: signed");
            return 0;
        }
        if (rest[0] == "verify")
        {
            if (behavior == "verify-failure") return 44;
            var digest = ReadBehavior(directory, "cert-sha256.txt",
                new string('a', 64));
            Console.Out.WriteLine("Verifies");
            Console.Out.WriteLine("Verified using v1 scheme (JAR signing): false");
            Console.Out.WriteLine("Verified using v2 scheme (APK Signature Scheme v2): true");
            Console.Out.WriteLine("Number of signers: 1");
            Console.Out.WriteLine("Signer #1 certificate DN: CN=Fixture Test Key");
            Console.Out.WriteLine("Signer #1 certificate SHA-256 digest: " + digest);
            return 0;
        }
        return 97;
    }

    private static int Main(string[] args)
    {
        var executable = Assembly.GetExecutingAssembly().Location;
        var directory = Path.GetDirectoryName(executable);
        var behavior = ReadBehavior(directory, "behavior.txt", "success");
        var name = Path.GetFileName(executable);

        if (name.Equals("zipalign.exe", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllLines(Path.Combine(directory, "zipalign-arguments.txt"), args);
            var zipBehavior = ReadBehavior(directory, "zipalign-behavior.txt", "success");
            if (zipBehavior == "zipalign-failure")
            {
                Console.Error.WriteLine("fixture zipalign: alignment failed");
                return 41;
            }
            // zipalign -f -p 4 <input> <output>
            File.Copy(args[args.Length - 2], args[args.Length - 1], true);
            return 0;
        }

        if (name.Equals("aapt.exe", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllLines(Path.Combine(directory, "aapt-arguments.txt"), args);
            var aaptBehavior = ReadBehavior(directory, "aapt-behavior.txt", "success");
            if (aaptBehavior == "aapt-failure") return 42;
            var badging = Path.Combine(directory, "badging.txt");
            if (File.Exists(badging))
                Console.Out.Write(File.ReadAllText(badging));
            return 0;
        }

        if (name.Equals("Unity.exe", StringComparison.OrdinalIgnoreCase))
        {
            var project = Value(args, "-projectPath");
            if (project == null || !Directory.Exists(project)) return 91;
            File.AppendAllLines(Path.Combine(project, "unity-arguments.txt"), args);
            var log = Value(args, "-logFile");
            if (log != null) File.WriteAllText(log, "Fixture export: " + behavior);
            if (behavior == "export-failure") return 29;
            if (behavior == "incomplete-export") return 0;
            var export = Value(args, "--android-export-dir");
            // No export dir means a non-export invocation (player build,
            // test run, setup): record it and succeed — the release
            // orchestrator also drives Windows/editor entry points.
            if (export == null) return 0;
            Directory.CreateDirectory(Path.Combine(export, "launcher"));
            Directory.CreateDirectory(Path.Combine(export, "unityLibrary"));
            File.WriteAllText(Path.Combine(export, "settings.gradle"), "include ':launcher', ':unityLibrary'");
            File.WriteAllText(Path.Combine(export, "build.gradle"), "// fixture");
            File.WriteAllText(Path.Combine(export, "launcher", "build.gradle"), "// fixture");
            File.WriteAllText(Path.Combine(export, "unityLibrary", "build.gradle"), "// fixture");
            return 0;
        }

        // java.exe: apksigner (Gradle export pipeline invokes it through
        // -jar apksigner.jar) or the Gradle launcher (--project-dir).
        if (Array.IndexOf(args, "-jar") >= 0)
            return RunApkSigner(directory, args);

        var gradleProject = Value(args, "--project-dir");
        if (gradleProject == null || !Directory.Exists(gradleProject)) return 93;
        File.WriteAllLines(Path.Combine(gradleProject, "java-arguments.txt"), args);
        var assembleRelease = Array.IndexOf(args, ":launcher:assembleRelease") >= 0;
        var assembleDebug = Array.IndexOf(args, ":launcher:assembleDebug") >= 0;
        // Wrong arguments must not produce an APK that makes the happy path pass.
        if (Array.IndexOf(args, "--offline") < 0 ||
            Array.IndexOf(args, "--no-daemon") < 0 ||
            (!assembleDebug && !assembleRelease)) return 94;
        var classpath = Value(args, "-classpath");
        if (classpath == null || !File.Exists(classpath)) return 95;
        if (behavior == "dependency-missing")
        {
            Console.Error.WriteLine("No cached version available for offline mode.");
            return 31;
        }
        if (behavior == "no-apk") return 0;
        if (assembleRelease)
        {
            // "presigned-gradle" models the debug-key fallback defect:
            // the exported launcher module carries a signing config and
            // Gradle emits a pre-signed launcher-release.apk instead of
            // the unsigned output the release pipeline requires.
            if (behavior == "presigned-gradle")
            {
                File.WriteAllText(
                    Path.Combine(gradleProject, "launcher", "build.gradle"),
                    "android { signingConfigs { release { storeFile file('debug.keystore') } }\n" +
                    "  buildTypes { release { signingConfig signingConfigs.release } } }");
                var presignedDir = Path.Combine(gradleProject, "launcher", "build", "outputs", "apk", "release");
                Directory.CreateDirectory(presignedDir);
                File.WriteAllText(Path.Combine(presignedDir, "launcher-release.apk"),
                    "pre-signed fixture APK");
                return 0;
            }
            var releaseDir = Path.Combine(gradleProject, "launcher", "build", "outputs", "apk", "release");
            Directory.CreateDirectory(releaseDir);
            var unsignedApk = Path.Combine(releaseDir, "launcher-release-unsigned.apk");
            if (behavior == "empty-apk")
                File.WriteAllText(unsignedApk, "");
            else
                WriteFixtureApk(unsignedApk, arm64: behavior != "no-arm64");
            Console.WriteLine("Fixture Gradle completed offline (release, unsigned).");
            return 0;
        }
        var apkDirectory = Path.Combine(gradleProject, "launcher", "build", "outputs", "apk", "debug");
        Directory.CreateDirectory(apkDirectory);
        File.WriteAllText(Path.Combine(apkDirectory, "launcher-debug.apk"),
            behavior == "empty-apk" ? "" : "fresh fixture APK");
        Console.WriteLine("Fixture Gradle completed offline.");
        return 0;
    }
}
