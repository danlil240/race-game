"""Verify saved profiles across two real validation-player processes.

The runner owns a NEW evidence directory and its synthetic profile store. Both
players use that exact store through the development-only validation override;
each has a fresh capture directory. No saves are copied or recreated on restart.
The player is terminated only after its capture is durably finished. This tests
explicitly saved data, not graceful-exit autosave or the normal user profile path.
"""

import argparse
from contextlib import contextmanager
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import time
import uuid

from AgentScenario import Client, verify_trace


PROFILE_SETTINGS = {
    0: {"language": "he", "camera": "overhead", "reducedCameraShake": True,
        "musicVolume": 0.25, "effectsVolume": 0.5, "voiceVolume": 0.75},
    3: {"language": "en", "camera": "chase", "reducedCameraShake": False,
        "musicVolume": 0.75, "effectsVolume": 0.25, "voiceVolume": 0.5},
}


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def file_hash(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def build_fingerprint(executable):
    files = [executable]
    data = executable.with_name(executable.stem + "_Data")
    if data.is_dir():
        files.extend(sorted(path for path in data.rglob("*") if path.is_file()))
        player_library = executable.with_name("UnityPlayer.dll")
        if player_library.is_file():
            files.append(player_library)
    return {path.relative_to(executable.parent).as_posix(): file_hash(path) for path in files}


def profile_hashes(directory):
    files = sorted(directory.iterdir())
    expected = {"profile-0.json", "profile-0.json.bak", "profile-3.json", "profile-3.json.bak"}
    assert {path.name for path in files} == expected, "Unexpected synthetic profile files"
    assert all(path.is_file() and not path.is_symlink() for path in files), "Unsafe profile file"
    return {path.name: file_hash(path) for path in files}


def assert_profile(expected, actual, label):
    assert isinstance(actual, dict), label + ": profile missing"

    def same_value(key):
        left, right = expected[key], actual[key]
        if key not in ("musicVolume", "effectsVolume", "voiceVolume"):
            return type(left) is type(right) and left == right
        if any(type(value) not in (int, float) or not math.isfinite(value)
               for value in (left, right)):
            return False
        try:
            # These fields are C# floats; JSON writers may spell the same
            # binary32 value differently. Compare exact bits, not a tolerance.
            return struct.pack("!f", left) == struct.pack("!f", right)
        except (OverflowError, struct.error):
            return False

    differences = [key for key in sorted(set(expected) | set(actual))
                   if key not in expected or key not in actual or not same_value(key)]
    assert not differences, label + ": changed fields " + ", ".join(differences)


def wait_connection(process, directory, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        code = process.poll()
        assert code is None, "Player exited before validation startup (exit code %s)" % code
        path = directory / "connection.json"
        if path.is_file():
            try:
                info = json.loads(path.read_text(encoding="utf-8-sig"))
            except (json.JSONDecodeError, PermissionError):
                time.sleep(0.05)
                continue
            assert type(info.get("protocolVersion")) is int and info["protocolVersion"] == 1
            assert info.get("host") == "127.0.0.1" and info.get("participantId") == "local"
            assert type(info.get("port")) is int and 0 < info["port"] < 65536
            assert all(isinstance(info.get(key), str) and info[key] for key in ("runId", "token"))
            info["_directory"] = str(directory)
            return info
        time.sleep(0.05)
    raise AssertionError("Timed out waiting for player connection.json; inspect the player log")


@contextmanager
def player_run(player_command, root, phase, report, startup_timeout, shutdown_timeout):
    directory = root / (phase + "-run")
    evidence = root / (phase + "-evidence")
    evidence.mkdir()
    command = [*player_command, "--validation-dir", str(directory),
               "--validation-profile-dir", str(root / "profiles"),
               "-logFile", str(root / (phase + "-player.log"))]
    run = {"phase": phase, "command": command, "validationDirectory": str(directory),
           "profileDirectory": str(root / "profiles"), "evidenceDirectory": str(evidence),
           "processExited": False, "captureVerified": False}
    report["runs"].append(run)
    write_json(evidence / "launch.json", run)
    process, client, original_error, cleanup_error = None, None, None, None
    try:
        with (root / (phase + "-process.log")).open("wb") as output:
            process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT)
        run["pid"] = process.pid
        connection = wait_connection(process, directory, startup_timeout)
        run["runId"] = connection["runId"]
        client = Client(connection, evidence)
        snapshot = next(row for row in reversed(client.events) if row.get("type") == "snapshot")
        run["buildVersion"] = snapshot["buildVersion"]
        run["contentVersion"] = snapshot["contentVersion"]
        yield client
    except BaseException as error:
        original_error = error
        raise
    finally:
        if client is not None:
            try:
                client.send({"type": "health"})
                health = client.wait(lambda event: event.get("type") == "health")
                assert health["captureValid"], health
                client.send({"type": "finish"})
                finished = client.wait(lambda event: event.get("type") == "finished")
                assert finished["captureValid"], finished
                verify_trace(directory, client.info)
                run["captureVerified"] = True
            except Exception as error:
                run["captureError"] = str(error)
                cleanup_error = error
            finally:
                client.close()
        if process is not None:
            if process.poll() is None:
                run["shutdownMethod"] = "terminate after durable finish" if run["captureVerified"] else "terminate failed scenario"
                process.terminate()
                try:
                    process.wait(timeout=shutdown_timeout)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=shutdown_timeout)
                    run["shutdownMethod"] = "kill after termination timeout"
            run["exitCode"] = process.poll()
            run["processExited"] = run["exitCode"] is not None
        write_json(evidence / "process.json", run)
        if cleanup_error is not None and original_error is None:
            raise cleanup_error


def assert_start_screen(state):
    assert state["activeSlot"] == -1 and state["screen"] == "profiles", "Unexpected initial screen"
    assert not state["pendingSettings"], "Unexpected initial draft settings"
    assert len(state["slots"]) == 4 and {slot["slot"] for slot in state["slots"]} == set(range(4))


def run_scenario(player_command, evidence, startup_timeout=60, shutdown_timeout=10):
    """Own two player processes; return and persist a passing or failed report.

    player_command is an argument list (never a shell string). The CLI supplies
    only the executable. Tests supply a Python protocol fixture as the external
    boundary; that fixture is never used to claim the actual game passed.
    """
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed", "scope": "explicitly saved synthetic profiles across validation-player process restart",
        "profileDirectory": str(root / "profiles"), "runs": [], "checks": [],
        "notVerified": ["normal user profile path", "graceful-exit or unsaved-draft autosave",
                        "physical input, visual/Hebrew/audio quality", "Android device behavior"],
    }
    try:
        assert startup_timeout > 0 and shutdown_timeout > 0, "Timeouts must be positive"
        executable = Path(player_command[0]).resolve(strict=True)
        report["executable"] = {"path": str(executable), "sha256": file_hash(executable)}
        report["buildFiles"] = build_fingerprint(executable)
        player_command = [str(executable), *player_command[1:]]
        expected = {}
        with player_run(player_command, root, "first", report, startup_timeout, shutdown_timeout) as client:
            assert_start_screen(client.state)
            assert all(slot["status"] == "Missing" for slot in client.state["slots"]), "Profiles must start empty"
            for slot, changes in PROFILE_SETTINGS.items():
                created, _ = client.applied("profile.create", {"slot": slot, "language": "en"})
                profile = client.action_state(created["commandId"])["savedProfile"]
                assert uuid.UUID(profile["id"])
                assert profile["slot"] == slot
                path = root / "profiles" / ("profile-%d.json" % slot)
                assert_profile(profile, json.loads(path.read_text(encoding="utf-8-sig")), "Created file")
                client.applied("settings.update", changes)
                saved, _ = client.applied("settings.save")
                state = client.action_state(saved["commandId"])
                target = {**profile, **changes}
                assert not state["pendingSettings"], "Save left pending settings"
                assert_profile(target, state["savedProfile"], "First process saved profile")
                assert_profile(target, json.loads(path.read_text(encoding="utf-8-sig")), "Saved file")
                expected[str(slot)] = target
            assert len({profile["id"] for profile in expected.values()}) == len(expected), "Profile identities collided"
        assert report["runs"][0]["processExited"], "First process did not exit"
        report["profiles"] = expected
        before = profile_hashes(root / "profiles")
        report["saveHashesBeforeRestart"] = before
        write_json(root / "before-restart.json", {"profiles": expected, "saveHashes": before})
        report["checks"].append("Two synthetic profiles and all six settings saved by first process")
        assert build_fingerprint(executable) == report["buildFiles"], "Player build changed between processes"
        with player_run(player_command, root, "second", report, startup_timeout, shutdown_timeout) as client:
            assert report["runs"][0]["runId"] != report["runs"][1]["runId"], "Restart reused run identity"
            assert_start_screen(client.state)
            for slot in client.state["slots"]:
                key = str(slot["slot"])
                if key in expected:
                    assert slot["status"] == "Loaded", "Restart did not load the primary save"
                    assert_profile(expected[key], slot["profile"], "Restart initial slot " + key)
                else:
                    assert slot["status"] == "Missing", "Restart created an unexpected profile"
            assert profile_hashes(root / "profiles") == before, "Save files changed during process restart"
            for key, profile in expected.items():
                selected, _ = client.applied("profile.select", {"slot": key})
                state = client.action_state(selected["commandId"])
                assert state["activeSlot"] == int(key) and not state["pendingSettings"]
                assert_profile(profile, state["savedProfile"], "Restart selected profile " + key)
                assert_profile(profile, state["draftProfile"], "Restart selected draft " + key)
                assert state["language"] == profile["language"], "Selected language not restored"
        after = profile_hashes(root / "profiles")
        report["saveHashesAfterRestart"] = after
        assert before == after, "Save files changed during restart verification"
        assert build_fingerprint(executable) == report["buildFiles"], "Player build changed during restart verification"
        assert all(run["captureVerified"] and run["processExited"] for run in report["runs"])
        report["checks"].extend([
            "First process exited before a distinct second validation run started",
            "Restart initial snapshot loaded both original identities and every saved field",
            "Only profile.select commands issued after restart; both saved and draft values restored",
            "Same synthetic primary and backup paths retained identical SHA-256 hashes",
            "Both streamed traces contiguous, health valid and capture completion durable",
        ])
        report["status"] = "passed"
    except Exception as error:
        report["error"] = str(error) or type(error).__name__
    write_json(root / "result.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("player", type=Path, help="Windows development player executable")
    parser.add_argument("--evidence", type=Path, required=True, help="NEW directory; existing directories are refused")
    parser.add_argument("--startup-timeout", type=float, default=60)
    args = parser.parse_args()
    try:
        result = run_scenario([str(args.player)], args.evidence, startup_timeout=args.startup_timeout)
    except (FileExistsError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
