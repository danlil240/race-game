"""Normal user profile path + graceful-exit evidence on real validation players.

Two real Windows development-player processes run with ``--validation-dir``
``--validation-normal-profiles``: the profile store is the REAL
``persistentDataPath\\Profiles`` directory, not a synthetic override. Any
pre-existing real store is moved to a sibling backup before the first launch
and restored in a finally block; the store the players created is quarantined
into the evidence directory so real-user state is never left clobbered.

Graceful exit is ``taskkill /pid <pid>`` WITHOUT /F (WM_CLOSE ->
OnApplicationQuit), which must stamp a terminal ``captureError`` of
``processExit``. Explicitly saved profiles must survive the restart; unsaved
``settings.update`` drafts must never reach the real store.
"""

import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from types import SimpleNamespace
import uuid

from AgentScenario import Client, verify_trace
from RestartScenario import (assert_profile, assert_start_screen,
                             build_fingerprint, file_hash, wait_connection,
                             write_json)


# First settings.save values; every following DRAFT_SETTINGS value differs so
# a leaked unsaved draft always changes at least one asserted field.
SAVED_SETTINGS = {"language": "he", "camera": "overhead", "reducedCameraShake": True,
                  "musicVolume": 0.25, "effectsVolume": 0.5, "voiceVolume": 0.75}
DRAFT_SETTINGS = {"language": "en", "camera": "chase", "reducedCameraShake": False,
                  "musicVolume": 0.9, "effectsVolume": 0.9, "voiceVolume": 0.1}

PROFILE_NAME = re.compile(r"profile-\d+\.json(\.bak)?$")
GRACEFUL_EXIT_METHOD = "taskkill /pid (no /F: WM_CLOSE graceful exit)"


def read_product_names(settings_path):
    """Parse companyName/productName from ProjectSettings.asset (YAML lines)."""
    text = Path(settings_path).read_text(encoding="utf-8-sig")
    names = {}
    for key in ("companyName", "productName"):
        match = re.search(r"^[ \t]*" + key + r":[ \t]*(.+?)[ \t]*$",
                          text, re.MULTILINE)
        assert match, "Missing " + key + " in " + str(settings_path)
        value = match.group(1).strip().strip("'\"")
        assert value, "Empty " + key + " in " + str(settings_path)
        names[key] = value
    return names["companyName"], names["productName"]


def default_local_low():
    """Windows standalone persistentDataPath root: <user>\\AppData\\LocalLow."""
    local = os.environ.get("LOCALAPPDATA")
    assert local, "LOCALAPPDATA is not set; cannot locate the real profile store"
    return Path(local).parent / "LocalLow"


def default_project_settings():
    return Path(__file__).resolve().parents[1] / "ProjectSettings" / "ProjectSettings.asset"


def compute_real_profiles_dir(project_settings, local_low):
    """The ordinary user store the player selects: LocalLow\\<company>\\<product>\\Profiles."""
    company, product = read_product_names(project_settings)
    return Path(local_low) / company / product / "Profiles"


def real_profile_hashes(directory):
    """SHA-256 of every real-store file; an absent store hashes to {}."""
    if not directory.exists():
        return {}
    assert directory.is_dir() and not directory.is_symlink(), "Unsafe real profile store"
    files = sorted(directory.iterdir())
    assert all(path.is_file() and not path.is_symlink() and PROFILE_NAME.match(path.name)
               for path in files), "Unexpected real-store content"
    return {path.name: file_hash(path) for path in files}


def protection_record(profiles_dir):
    """Mutable evidence record for preserved_profiles, live on failure paths too."""
    return {"profilesDirectory": str(profiles_dir), "existed": False,
            "backup": None, "createdProfiles": None,
            "restored": False, "restoreError": None}


@contextmanager
def preserved_profiles(profiles_dir, quarantine_dir, protection=None):
    """Park the real store for the whole scenario; restore it exactly on exit.

    An existing real store is renamed to a sibling backup (same volume). After
    the run, whatever was created at the real path while the store was parked
    is moved into the quarantine directory -- evidence, and nothing is deleted
    at the real location -- then the backup is renamed back. Every restore step
    is individually guarded in the finally: if the created tree cannot be
    quarantined it is renamed to a uuid sibling, and the parked backup restore
    ALWAYS runs last so real-user state can never stay clobbered.
    """
    if protection is None:
        protection = protection_record(profiles_dir)
    backup = None
    try:
        # Park inside the try: a partial park failure still reaches the
        # restoring finally below.
        assert not profiles_dir.is_symlink(), \
            "Refusing to protect a symlinked profile store"
        if profiles_dir.exists():
            assert profiles_dir.is_dir(), "Real profile store is not a directory"
            protection["existed"] = True
            candidate = profiles_dir.with_name(
                profiles_dir.name + ".validation-backup-" + uuid.uuid4().hex)
            profiles_dir.rename(candidate)
            backup = candidate  # set only after the rename succeeded
            protection["backup"] = str(backup)
            assert not profiles_dir.exists(), "Real store was not fully parked"
        yield protection
    finally:
        errors = []
        # Eviction is safe only when the pre-run state is provably absent or
        # parked: a still-standing directory after a failed park IS the real
        # store and must not be touched.
        evictable = backup is not None or not protection["existed"]
        if evictable and (profiles_dir.exists() or profiles_dir.is_symlink()):
            target = quarantine_dir / "created-profiles"
            try:
                assert not target.exists(), "Quarantine target already exists"
                quarantine_dir.mkdir(parents=True, exist_ok=True)
                shutil.move(str(profiles_dir), str(target))
                protection["createdProfiles"] = str(target)
            except Exception as error:
                errors.append("quarantine failed: " + str(error))
                # Last-resort eviction: a uuid sibling rename needs no new
                # directory and cannot collide, so the restore below still runs.
                try:
                    fallback = profiles_dir.with_name(
                        profiles_dir.name + ".validation-created-"
                        + uuid.uuid4().hex)
                    profiles_dir.rename(fallback)
                    protection["createdProfiles"] = str(fallback)
                except Exception as evict_error:
                    errors.append("eviction failed: " + str(evict_error))
        if backup is not None:
            # The parked real store goes back no matter what failed above.
            try:
                backup.rename(profiles_dir)
            except Exception as restore_error:
                errors.append("backup restore failed: " + str(restore_error))
        if backup is not None:
            protection["restored"] = (not backup.exists()
                                      and profiles_dir.is_dir()
                                      and not profiles_dir.is_symlink())
        else:
            # Nothing was displaced: restored means the pre-run presence state
            # (absent or present) is what stands at the path now.
            protection["restored"] = (
                profiles_dir.exists() == protection["existed"]
                and not profiles_dir.is_symlink())
        if errors:
            protection["restoreError"] = "; ".join(errors)


def request_graceful_exit(process):
    """Post WM_CLOSE via taskkill without /F: the player runs OnApplicationQuit."""
    assert sys.platform == "win32", "Graceful WM_CLOSE exit evidence is Windows-only"
    completed = subprocess.run(["taskkill", "/pid", str(process.pid)],
                               capture_output=True, text=True, timeout=15)
    assert completed.returncode == 0, "taskkill refused: " + (
        completed.stdout + completed.stderr).strip()


def wait_exit(process, timeout):
    try:
        return process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        raise AssertionError(
            "Player ignored graceful taskkill /pid within %ss" % timeout)


def validate_process_exit_status(status):
    assert status.get("captureValid") is False, \
        "Unfinished capture survived a graceful exit as valid"
    assert status.get("captureComplete") is False, \
        "Unfinished capture was certified complete at process exit"
    assert status.get("captureError") == "processExit", \
        ("Graceful exit must stamp processExit in a clean run", status)


@contextmanager
def player_session(player_command, root, phase, report, startup_timeout,
                   shutdown_timeout, verify_on_exit):
    """One --validation-dir + --validation-normal-profiles player process."""
    directory = root / (phase + "-run")
    evidence = root / (phase + "-evidence")
    evidence.mkdir()
    command = [*player_command, "--validation-dir", str(directory),
               "--validation-normal-profiles",
               "-logFile", str(root / (phase + "-player.log"))]
    run = {"phase": phase, "command": command, "validationDirectory": str(directory),
           "normalProfiles": True, "evidenceDirectory": str(evidence), "checks": [],
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
        yield SimpleNamespace(process=process, client=client,
                              directory=directory, run=run)
    except BaseException as error:
        original_error = error
        raise
    finally:
        if client is not None:
            if verify_on_exit and process is not None and process.poll() is None:
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
            try:
                client.close()
            except Exception:
                pass  # the peer may already be gone after a process exit
        if process is not None and process.poll() is None:
            run["shutdownMethod"] = ("terminate after durable finish"
                                     if run["captureVerified"] else "terminate failed scenario")
            process.terminate()
            try:
                process.wait(timeout=shutdown_timeout)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=shutdown_timeout)
                run["shutdownMethod"] = "kill after termination timeout"
        if process is not None:
            run["exitCode"] = process.poll()
            run["processExited"] = run["exitCode"] is not None
        write_json(evidence / "process.json", run)
        if cleanup_error is not None and original_error is None:
            raise cleanup_error


def run_scenario(player_command, evidence, profiles_dir=None, project_settings=None,
                 startup_timeout=60, shutdown_timeout=10, exit_timeout=20):
    """Own two player processes against the REAL persistentDataPath store."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "normal persistentDataPath profile store, graceful exit and "
                 "unsaved-draft separation across two validation-player processes",
        "runs": [], "checks": [],
        "notVerified": ["Android device/ADB behavior", "physical input/UI parity",
                        "human Hebrew/audio/visual acceptance", "synthetic-store "
                        "restart (covered by RestartScenario)",
                        "LAN gameplay or racing features"],
    }
    try:
        if not __debug__:
            raise RuntimeError("Run without -O: scenario assertions must be enabled")
        assert startup_timeout > 0 and shutdown_timeout > 0 and exit_timeout > 0
        assert all(SAVED_SETTINGS[key] != DRAFT_SETTINGS[key] for key in DRAFT_SETTINGS), \
            "Unsaved draft values are not distinguishable from saved values"
        executable = Path(player_command[0]).resolve(strict=True)
        player_command = [str(executable), *player_command[1:]]
        report["buildFiles"] = build_fingerprint(executable)
        tool_paths = [Path(__file__), Path(__file__).with_name("AgentScenario.py"),
                      Path(__file__).with_name("RestartScenario.py")]
        report["toolHashes"] = {path.name: file_hash(path) for path in tool_paths}
        if profiles_dir is None:
            profiles_dir = compute_real_profiles_dir(
                project_settings or default_project_settings(), default_local_low())
        profiles_dir = Path(profiles_dir)
        report["profilesDirectory"] = str(profiles_dir)
        report["exitMethod"] = GRACEFUL_EXIT_METHOD
        protection = protection_record(profiles_dir)
        # Bound before the with: the protection record is reported even when
        # the park itself fails, so a restore error is never silent.
        report["profileProtection"] = protection
        with preserved_profiles(profiles_dir, root, protection):
            target = None
            with player_session(player_command, root, "first", report,
                                startup_timeout, shutdown_timeout,
                                verify_on_exit=False) as first:
                client, run = first.client, first.run
                assert_start_screen(client.state)
                assert all(slot["status"] == "Missing" for slot in client.state["slots"]), \
                    "Parked real store must present four empty slots"
                created, _ = client.applied("profile.create", {"slot": 0, "language": "en"})
                profile = client.action_state(created["commandId"])["savedProfile"]
                assert uuid.UUID(profile["id"])
                save_path = profiles_dir / "profile-0.json"
                assert save_path.is_file(), \
                    "Created profile missing under the real profile store"
                assert_profile(profile, json.loads(save_path.read_text(
                    encoding="utf-8-sig")), "Real-store created profile")
                capture_profile = first.directory / "profiles" / "profile-0.json"
                assert not capture_profile.exists(), \
                    "Profile leaked into the capture directory store"
                client.applied("settings.update", SAVED_SETTINGS)
                saved, _ = client.applied("settings.save")
                state = client.action_state(saved["commandId"])
                target = {**profile, **SAVED_SETTINGS}
                assert not state["pendingSettings"], "Save left pending settings"
                assert_profile(target, state["savedProfile"], "Saved real profile")
                assert_profile(target, json.loads(save_path.read_text(
                    encoding="utf-8-sig")), "Saved real-store file")
                saved_hashes = real_profile_hashes(profiles_dir)
                run["realStoreAfterSave"] = saved_hashes
                run["checks"].append(
                    "profile.create + settings.save persisted under the REAL "
                    "persistentDataPath Profiles store, never the capture dir")

                draft, _ = client.applied("settings.update", DRAFT_SETTINGS)
                state = client.action_state(draft["commandId"])
                assert state["pendingSettings"], "Second update produced no draft"
                assert_profile(target, state["savedProfile"], "Draft changed saved profile")
                assert real_profile_hashes(profiles_dir) == saved_hashes, \
                    "Unsaved draft reached the real store"
                run["checks"].append(
                    "second settings.update stayed a memory-only draft; the "
                    "real store kept exact saved bytes")

                # Graceful exit while still attached: the process itself severs
                # the socket, so no controllerDisconnected can be stamped first.
                run["exitRequested"] = GRACEFUL_EXIT_METHOD + " pid=%d" % first.process.pid
                started = time.monotonic()
                request_graceful_exit(first.process)
                run["exitCode"] = wait_exit(first.process, exit_timeout)
                run["gracefulExitSeconds"] = time.monotonic() - started
                status = json.loads((first.directory / "capture-status.json")
                                    .read_text(encoding="utf-8-sig"))
                run["exitStatus"] = status
                validate_process_exit_status(status)
                assert real_profile_hashes(profiles_dir) == saved_hashes, \
                    "Graceful exit persisted the unsaved draft"
                run["processExitVerified"] = True
                run["checks"].append(
                    "taskkill /pid without /F exited within the timeout and "
                    "stamped terminal captureError processExit")
                report["checks"].extend("first: " + check for check in run["checks"])

            assert report["runs"][0]["processExited"], "First process did not exit"
            assert build_fingerprint(executable) == report["buildFiles"], \
                "Player build changed between processes"
            with player_session(player_command, root, "second", report,
                                startup_timeout, shutdown_timeout,
                                verify_on_exit=True) as second:
                client, run = second.client, second.run
                assert run["runId"] != report["runs"][0]["runId"], \
                    "Relaunch reused run identity"
                assert_start_screen(client.state)
                for slot in client.state["slots"]:
                    if slot["slot"] == 0:
                        assert slot["status"] == "Loaded", \
                            "Real store save was not loaded on relaunch"
                        assert_profile(target, slot["profile"],
                                       "Relaunch initial slot 0")
                    else:
                        assert slot["status"] == "Missing", \
                            "Relaunch created an unexpected profile"
                selected, _ = client.applied("profile.select", {"slot": 0})
                state = client.action_state(selected["commandId"])
                assert state["activeSlot"] == 0 and not state["pendingSettings"]
                assert_profile(target, state["savedProfile"], "Relaunch saved profile")
                assert_profile(target, state["draftProfile"], "Relaunch draft profile")
                assert state["language"] == target["language"], \
                    "Selected language not restored"
                assert real_profile_hashes(profiles_dir) == saved_hashes, \
                    "Relaunch rewrote the real store"
                run["checks"].append(
                    "relaunch loaded the saved profile into saved AND draft "
                    "state; unsaved draft values are absent")
            assert all(run.get("processExited") for run in report["runs"])
            assert report["runs"][0].get("processExitVerified"), \
                "First process exit was not verified graceful"
            assert report["runs"][1].get("captureVerified"), \
                "Second process capture did not finish durably"
            report["checks"].extend(
                "second: " + check for check in report["runs"][1]["checks"])
            report["checks"].extend([
                "first: explicitly saved profile survived taskkill graceful exit intact",
                "second: clean finish, health valid and trace complete on the real store",
            ])
        assert protection["restored"], "Real profile store was not restored"
        assert protection["restoreError"] is None, protection["restoreError"]
        report["status"] = "passed"
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)
    finally:
        write_json(root / "result.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("player", type=Path,
                        help="Windows development player executable")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="NEW directory; existing directories are refused")
    parser.add_argument("--profiles-dir", type=Path, default=None,
                        help="override the real profile store location "
                             "(test seam; default computes persistentDataPath "
                             "from ProjectSettings.asset and LOCALAPPDATA)")
    parser.add_argument("--project-settings", type=Path, default=None,
                        help="ProjectSettings.asset path for company/product names")
    parser.add_argument("--startup-timeout", type=float, default=60)
    parser.add_argument("--shutdown-timeout", type=float, default=10)
    parser.add_argument("--exit-timeout", type=float, default=20,
                        help="bound on graceful taskkill /pid process exit")
    args = parser.parse_args()
    try:
        report = run_scenario([str(args.player)], args.evidence,
                              profiles_dir=args.profiles_dir,
                              project_settings=args.project_settings,
                              startup_timeout=args.startup_timeout,
                              shutdown_timeout=args.shutdown_timeout,
                              exit_timeout=args.exit_timeout)
    except (FileExistsError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps({"status": report["status"], "checks": report["checks"],
                      "error": report.get("error"), "evidence": str(args.evidence)},
                     indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
