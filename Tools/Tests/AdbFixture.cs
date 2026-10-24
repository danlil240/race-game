using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// External-process fixture simulating `adb` for Test-FamilyProfileBackup.ps1.
// The backup/restore tools, filesystem checks, hashing, argument plumbing
// and exit propagation stay real; only the device side is simulated.
//
// Behavior files live next to this executable (per-tool fixture convention):
//   devices.txt      lines "<serial> <state>" (state: device|offline); absent
//                    or empty means no devices attached.
//   device-root.txt  absolute host path emulating the device filesystem root;
//                    defaults to "<exedir>\device-root".
//   pidof.txt        text printed by `shell pidof <pkg>`; empty/absent means
//                    the game is not running (pidof exits 1, like the real one).
//   installed.txt    package ids reported by `shell pm path` (one per line);
//                    defaults to com.daniel.familyracing.
//   model.txt        value returned by `shell getprop ro.product.model`.
//
// Emulated commands: devices, get-state, shell (pidof, getprop, pm path, ls,
// test, mkdir -p, cp, mv, rm, cat), pull, push. Remote paths are mapped under
// the device root and `..` escapes are refused.
internal static class AdbFixture
{
    private static string _dir;
    private static string _deviceRoot;
    private static Dictionary<string, string> _devices;

    private static string ReadFile(string name, string fallback)
    {
        var path = Path.Combine(_dir, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback;
    }

    private static string[] ReadLines(string name)
    {
        var path = Path.Combine(_dir, name);
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray()
            : new string[0];
    }

    private static void LoadDevices()
    {
        _devices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadLines("devices.txt"))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) _devices[parts[0]] = parts[1];
        }
    }

    // Maps a device-absolute path ("/sdcard/...") under the emulated device
    // root; refuses traversal outside the root.
    private static string MapRemote(string remote)
    {
        var rel = remote.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_deviceRoot, rel));
        var rootFull = Path.GetFullPath(_deviceRoot);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    private static int NotFound(string serial)
    {
        Console.Error.WriteLine("error: device '" + serial + "' not found");
        return 1;
    }

    // Returns 0 when the requested serial is attached and usable.
    private static int CheckSerial(string serial)
    {
        if (serial == null)
        {
            // No -s: real adb requires exactly one attached device.
            var online = _devices.Where(d => d.Value == "device").ToList();
            if (online.Count == 1) return 0;
            if (online.Count == 0)
            {
                Console.Error.WriteLine("error: no devices/emulators found");
                return 1;
            }
            Console.Error.WriteLine("error: more than one device/emulator");
            return 1;
        }
        string state;
        if (!_devices.TryGetValue(serial, out state)) return NotFound(serial);
        if (state != "device")
        {
            Console.Error.WriteLine("error: device " + state);
            return 1;
        }
        return 0;
    }

    private static int Shell(string[] tokens)
    {
        if (tokens.Length == 0) return 2;
        switch (tokens[0])
        {
            case "pidof":
            {
                var pid = ReadFile("pidof.txt", "");
                if (pid.Length == 0) return 1;
                Console.Out.WriteLine(pid);
                return 0;
            }
            case "getprop":
                if (tokens.Length > 1 && tokens[1] == "ro.product.model")
                    Console.Out.WriteLine(ReadFile("model.txt", "SM-T220"));
                return 0;
            case "pm":
                if (tokens.Length >= 3 && tokens[1] == "path")
                {
                    var installed = ReadLines("installed.txt");
                    if (installed.Length == 0) installed = new[] { "com.daniel.familyracing" };
                    if (installed.Contains(tokens[2], StringComparer.Ordinal))
                    {
                        Console.Out.WriteLine("package:/data/app/~~x9==/" + tokens[2] + "-a1==/base.apk");
                        return 0;
                    }
                    return 1;
                }
                return 2;
            case "ls":
            {
                var path = tokens[tokens.Length - 1];
                var local = MapRemote(path);
                if (local == null || !Directory.Exists(local))
                {
                    Console.Error.WriteLine("ls: " + path + ": No such file or directory");
                    return 1;
                }
                foreach (var entry in Directory.GetFileSystemEntries(local))
                    Console.Out.WriteLine(Path.GetFileName(entry));
                return 0;
            }
            case "test":
            {
                if (tokens.Length < 3) return 2;
                var local = MapRemote(tokens[2]);
                if (local == null) return 1;
                switch (tokens[1])
                {
                    case "-f": return File.Exists(local) ? 0 : 1;
                    case "-d": return Directory.Exists(local) ? 0 : 1;
                    case "-e": return (File.Exists(local) || Directory.Exists(local)) ? 0 : 1;
                    default: return 2;
                }
            }
            case "mkdir":
            {
                var local = MapRemote(tokens[tokens.Length - 1]);
                if (local == null) return 1;
                Directory.CreateDirectory(local);
                return 0;
            }
            case "cp":
            {
                if (tokens.Length < 3) return 2;
                var src = MapRemote(tokens[tokens.Length - 2]);
                var dst = MapRemote(tokens[tokens.Length - 1]);
                if (src == null || dst == null || !File.Exists(src)) return 1;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
                return 0;
            }
            case "mv":
            {
                var rest = tokens.Skip(1).Where(t => t != "-f").ToArray();
                if (rest.Length < 2) return 2;
                var src = MapRemote(rest[0]);
                var dst = MapRemote(rest[1]);
                if (src == null || dst == null || !File.Exists(src)) return 1;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
                return 0;
            }
            case "rm":
            {
                var rest = tokens.Skip(1).Where(t => !t.StartsWith("-")).ToArray();
                var code = 0;
                foreach (var token in rest)
                {
                    var local = MapRemote(token);
                    if (local == null) { code = 1; continue; }
                    if (File.Exists(local)) File.Delete(local);
                    else if (Directory.Exists(local)) Directory.Delete(local, true);
                    else code = 1;
                }
                return code;
            }
            case "cat":
            {
                var local = MapRemote(tokens[tokens.Length - 1]);
                if (local == null || !File.Exists(local)) return 1;
                Console.Out.Write(File.ReadAllText(local));
                return 0;
            }
            default:
                Console.Error.WriteLine("/system/bin/sh: " + tokens[0] + ": not found");
                return 127;
        }
    }

    private static int Main(string[] args)
    {
        _dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _deviceRoot = ReadFile("device-root.txt", Path.Combine(_dir, "device-root"));
        LoadDevices();

        var list = new List<string>(args);
        string serial = null;
        var sIndex = list.IndexOf("-s");
        if (sIndex >= 0 && sIndex + 1 < list.Count)
        {
            serial = list[sIndex + 1];
            list.RemoveRange(sIndex, 2);
        }
        if (list.Count == 0) return 2;

        switch (list[0])
        {
            case "devices":
                Console.Out.WriteLine("List of devices attached");
                foreach (var d in _devices)
                    Console.Out.WriteLine(d.Key + "\t" + d.Value);
                Console.Out.WriteLine();
                return 0;
            case "start-server":
            case "kill-server":
                return 0;
        }

        var serialCheck = CheckSerial(serial);
        if (serialCheck != 0) return serialCheck;

        switch (list[0])
        {
            case "get-state":
                Console.Out.WriteLine(serial == null
                    ? _devices.First(d => d.Value == "device").Value
                    : _devices[serial]);
                return 0;
            case "shell":
                return Shell(list.Skip(1).ToArray());
            case "pull":
            {
                if (list.Count < 3) return 2;
                var src = MapRemote(list[1]);
                var dst = list[2];
                if (src == null || !File.Exists(src))
                {
                    Console.Error.WriteLine("adb: error: remote object '" + list[1] + "' does not exist");
                    return 1;
                }
                if (Directory.Exists(dst)) dst = Path.Combine(dst, Path.GetFileName(src));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst)));
                File.Copy(src, dst, true);
                Console.Out.WriteLine(list[1] + ": 1 file pulled.");
                return 0;
            }
            case "push":
            {
                if (list.Count < 3) return 2;
                var src = list[1];
                var dst = MapRemote(list[2]);
                if (dst == null || !File.Exists(src))
                {
                    Console.Error.WriteLine("adb: error: cannot stat '" + src + "'");
                    return 1;
                }
                if (Directory.Exists(dst)) dst = Path.Combine(dst, Path.GetFileName(src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
                Console.Out.WriteLine(src + ": 1 file pushed.");
                return 0;
            }
            default:
                Console.Error.WriteLine("adb: unknown command " + list[0]);
                return 2;
        }
    }
}
