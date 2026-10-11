"""Integrity checks for the normal-profile scenario, not evidence that Unity passed."""

import importlib
import json
import os
import socket
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
import uuid
from pathlib import Path


def fake_player():
    """External protocol fixture emulating the normal-store + graceful-exit run.

    It speaks the real wire protocol and honors the real flag contract
    (--validation-dir required, --validation-normal-profiles required,
    --validation-profile-dir forbidden) plus fixture-only seams
    (--fake-profiles-dir, --stop-file) so the scenario can be exercised end to
    end without touching a real persistentDataPath. Never Unity proof.
    """
    args = sys.argv

    def option(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    directory = Path(option("--validation-dir"))
    log = Path(option("-logFile"))
    log.write_text("Synthetic normal-profile fixture, not a Unity player\n",
                   encoding="utf-8")
    if "--exit-early" in args:
        return 7
    if "--validation-normal-profiles" not in args:
        return 13  # scenario must select the real store explicitly
    if "--validation-profile-dir" in args:
        return 14  # mutually exclusive with --validation-normal-profiles
    profiles = (directory / "profiles") if "--use-capture-profiles" in args \
        else Path(option("--fake-profiles-dir"))
    stop_file = Path(option("--stop-file")) if "--stop-file" in args else None
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "profiles").mkdir(parents=True, exist_ok=True)
    profiles.mkdir(parents=True, exist_ok=True)
    slots = [{"slot": i, "status": "Missing", "profile": None} for i in range(4)]
    for slot in slots:
        path = profiles / ("profile-%d.json" % slot["slot"])
        if path.exists():
            profile = json.loads(path.read_text(encoding="utf-8"))
            slot.update(status="Loaded", profile=profile)
            if "--rewrite-on-load" in args:
                path.write_text(json.dumps(profile, indent=4), encoding="utf-8")
    state = {
        "screen": "profiles", "language": "en", "activeSlot": -1, "paused": False,
        "tick": 0, "input": {"steering": 0, "drift": False, "boost": False,
                             "gadget": False, "recover": False, "changeCamera": False,
                             "pause": False},
        "slots": slots, "savedProfile": None, "draftProfile": None,
        "pendingSettings": False, "captureValid": True, "captureError": "",
        "featureCoverage": {}, "presentation": {},
    }
    run_id = uuid.uuid4().hex
    token = uuid.uuid4().hex
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    connection = {"protocolVersion": 1, "host": "127.0.0.1",
                  "port": server.getsockname()[1],
                  "runId": run_id, "token": token, "participantId": "local"}
    sequence = 0
    stream = (directory / "state.ndjson").open("w", encoding="utf-8")
    conn = {"sock": None}

    def emit(kind, **payload):
        nonlocal sequence
        sequence += 1
        record = {"type": kind, "protocolVersion": 1, "runId": run_id,
                  "deviceId": "fixture-device", "participantId": "local",
                  "buildVersion": "foundation-0.1", "contentVersion": "foundation-0.1",
                  "sequence": sequence, "monotonicMs": sequence * 10, "tick": 0,
                  **payload}
        line = json.dumps(record) + "\n"
        stream.write(line)
        stream.flush()
        if conn["sock"] is not None:
            try:
                conn["sock"].sendall(line.encode("utf-8"))
            except OSError:
                pass

    def write_status(valid, complete, error):
        (directory / "capture-status.json").write_text(json.dumps({
            "captureValid": valid, "captureComplete": complete,
            "captureError": error, "recordsEmitted": sequence}), encoding="utf-8")

    boot = time.time()

    def watch_stop():
        # A real WM_CLOSE runs OnApplicationQuit: the stream disposes into a
        # terminal processExit status. Fixture mutants model the defect space.
        # Only a stop request written after THIS process booted counts -- the
        # same stop-file path is reused across the scenario's two launches.
        while True:
            try:
                fresh = (stop_file is not None and stop_file.exists()
                         and stop_file.stat().st_mtime >= boot)
            except OSError:
                fresh = False
            if fresh:
                if "--persist-drafts" in args and state["activeSlot"] >= 0 \
                        and state["draftProfile"]:
                    (profiles / ("profile-%d.json" % state["activeSlot"])
                     ).write_text(json.dumps(state["draftProfile"]), encoding="utf-8")
                if "--exit-violent" not in args:
                    code = ("controllerDisconnected" if "--wrong-exit-status" in args
                            else "processExit")
                    write_status(False, False, code)
                stream.close()
                os._exit(0)
            time.sleep(0.02)

    threading.Thread(target=watch_stop, daemon=True).start()
    emit("snapshot", state=state)
    write_status(True, False, "")
    (directory / "connection.json").write_text(json.dumps(connection), encoding="utf-8")
    conn["sock"], _ = server.accept()
    conn["sock"].settimeout(30)
    reader = conn["sock"].makefile("rb")
    try:
        hello = json.loads(reader.readline())
        if hello.get("token") != token:
            return 9
        conn["sock"].sendall((json.dumps({"type": "hello", "protocolVersion": 1,
                                          "runId": run_id}) + "\n").encode())
        emit("snapshot", state=state)
        while True:
            raw = reader.readline()
            if not raw:
                return 10
            frame = json.loads(raw)
            kind = frame["type"]
            if kind == "health":
                emit("health", captureValid=True, captureError="",
                     pendingCommands=0, outgoingRecords=0, controllerConnected=True)
            elif kind == "finish":
                write_status(True, True, "")
                emit("finished", captureValid=True, captureError="")
                while True:
                    time.sleep(1)
            elif kind == "command":
                action, params = frame["action"], frame["parameters"]
                if action in ("profile.create", "profile.select"):
                    index = int(params["slot"])
                    if action == "profile.create":
                        profile = {"schemaVersion": 1, "id": str(uuid.uuid4()),
                                   "slot": index, "avatar": "comet", "colorIndex": index,
                                   "language": params["language"], "camera": "chase",
                                   "reducedCameraShake": False, "musicVolume": 1.0,
                                   "effectsVolume": 1.0, "voiceVolume": 1.0,
                                   "coins": 0, "unlockedVehicles": ["starter"]}
                        slots[index].update(status="Loaded", profile=profile)
                        (profiles / ("profile-%d.json" % index)
                         ).write_text(json.dumps(profile), encoding="utf-8")
                    profile = slots[index]["profile"]
                    state.update(activeSlot=index, savedProfile=profile,
                                 draftProfile=dict(profile), screen="settings",
                                 language=profile["language"], pendingSettings=False)
                elif action == "settings.update":
                    changes = dict(params)
                    changes["reducedCameraShake"] = params["reducedCameraShake"] == "true"
                    for key in ("musicVolume", "effectsVolume", "voiceVolume"):
                        changes[key] = float(params[key])
                    state["draftProfile"].update(changes)
                    state.update(pendingSettings=True, language=changes["language"])
                elif action == "settings.save":
                    profile = dict(state["draftProfile"])
                    state.update(savedProfile=profile, pendingSettings=False)
                    slots[state["activeSlot"]]["profile"] = profile
                    path = profiles / ("profile-%d.json" % state["activeSlot"])
                    path.with_suffix(".json.bak").write_bytes(path.read_bytes())
                    path.write_text(json.dumps(profile), encoding="utf-8")
                else:
                    return 11
                emit("state", state=state, commandId=frame["commandId"])
                emit("result", commandId=frame["commandId"], status="applied",
                     code="", action=action, duplicate=False,
                     result={"status": "applied"})
            else:
                return 12
    finally:
        reader.close()


class NormalProfileIntegrityTests(unittest.TestCase):
    def runner(self):
        self.assertTrue(Path(__file__).with_name("NormalProfileScenario.py").is_file(),
                        "Normal-profile companion has not been implemented")
        return importlib.import_module("NormalProfileScenario")

    def run_scenario(self, root, profiles, *flags):
        runner = self.runner()
        stop_file = Path(root).parent / ("stop-request-" + uuid.uuid4().hex)
        original = runner.request_graceful_exit
        runner.request_graceful_exit = lambda process: stop_file.write_text(
            "exit", encoding="utf-8")
        try:
            return runner.run_scenario(
                [sys.executable, str(Path(__file__).resolve()), "--fake-player",
                 "--fake-profiles-dir", str(profiles),
                 "--stop-file", str(stop_file), *flags],
                root, profiles_dir=profiles, project_settings=self.settings,
                startup_timeout=5, shutdown_timeout=3, exit_timeout=5)
        finally:
            runner.request_graceful_exit = original

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.settings = self.root / "ProjectSettings.asset"
        self.settings.write_text(
            "  noise: value\n  companyName: Fixture Co\n"
            "  productName: Fixture Game\n", encoding="utf-8")
        self.local_low = self.root / "LocalLow"
        self.profiles = (self.local_low / "Fixture Co" / "Fixture Game" / "Profiles")

    def test_product_names_are_parsed_and_required(self):
        runner = self.runner()
        self.assertEqual(runner.read_product_names(self.settings),
                         ("Fixture Co", "Fixture Game"))
        for content in ("  companyName: Only Co\n", "  productName: Only Game\n",
                        "  companyName:   \n  productName: Game\n"):
            with self.subTest(content=content):
                bad = self.root / "bad-settings.asset"
                bad.write_text(content, encoding="utf-8")
                with self.assertRaises(AssertionError):
                    runner.read_product_names(bad)

    def test_real_path_computed_from_local_low_and_settings(self):
        runner = self.runner()
        profiles = runner.compute_real_profiles_dir(self.settings, self.local_low)
        self.assertEqual(profiles, self.profiles)
        # The path is pure env + project settings: LocalLow\<company>\<product>\Profiles.
        self.assertEqual(profiles.parts[-4:],
                         ("LocalLow", "Fixture Co", "Fixture Game", "Profiles"))

    def test_preserved_profiles_restores_existing_store_on_success_and_failure(self):
        runner = self.runner()
        self.profiles.mkdir(parents=True)
        (self.profiles / "profile-2.json").write_text('{"id":"real"}', encoding="utf-8")
        for raising in (False, True):
            with self.subTest(raising=raising):
                try:
                    with runner.preserved_profiles(self.profiles, self.root) as p:
                        self.assertTrue(p["existed"])
                        self.assertIsNotNone(p["backup"])
                        self.assertFalse(self.profiles.exists())
                        if raising:
                            raise RuntimeError("boom")
                except RuntimeError:
                    self.assertTrue(raising)
                self.assertTrue(self.profiles.is_dir())
                self.assertEqual(json.loads(
                    (self.profiles / "profile-2.json").read_text())["id"], "real")

    def test_preserved_profiles_quarantines_created_store_and_restores_absence(self):
        runner = self.runner()
        with runner.preserved_profiles(self.profiles, self.root) as p:
            self.assertFalse(p["existed"])
            self.profiles.mkdir(parents=True)
            (self.profiles / "profile-0.json").write_text("{}", encoding="utf-8")
        self.assertFalse(self.profiles.exists())
        self.assertTrue((self.root / "created-profiles" / "profile-0.json").is_file())

    def test_preserved_profiles_restores_backup_over_created_content(self):
        runner = self.runner()
        self.profiles.mkdir(parents=True)
        (self.profiles / "profile-3.json").write_text('{"id":"original"}',
                                                    encoding="utf-8")
        with runner.preserved_profiles(self.profiles, self.root) as p:
            self.profiles.mkdir(parents=True)
            (self.profiles / "profile-0.json").write_text("{}", encoding="utf-8")
        self.assertEqual(sorted(path.name for path in self.profiles.iterdir()),
                         ["profile-3.json"])
        self.assertTrue((self.root / "created-profiles" / "profile-0.json").is_file())
        self.assertTrue(p["restored"])

    def test_restore_still_runs_when_quarantine_fails(self):
        # If the created tree cannot be moved into evidence, it is evicted to a
        # uuid sibling and the parked real store is still restored.
        runner = self.runner()
        self.profiles.mkdir(parents=True)
        (self.profiles / "profile-3.json").write_text('{"id":"orig"}',
                                                    encoding="utf-8")
        with mock.patch.object(runner, "shutil") as fake_shutil:
            fake_shutil.move.side_effect = OSError("denied")
            with runner.preserved_profiles(self.profiles, self.root) as p:
                self.profiles.mkdir(parents=True)
                (self.profiles / "profile-0.json").write_text("{}", encoding="utf-8")
        self.assertEqual([path.name for path in self.profiles.iterdir()],
                         ["profile-3.json"])
        siblings = [d for d in self.profiles.parent.iterdir()
                    if d.name.startswith("Profiles.validation-created-")]
        self.assertEqual(len(siblings), 1)
        self.assertTrue((siblings[0] / "profile-0.json").is_file())
        self.assertEqual(p["createdProfiles"], str(siblings[0]))
        self.assertTrue(p["restored"])
        self.assertIn("quarantine failed", p["restoreError"])
        self.assertFalse(any(d.name.startswith("Profiles.validation-backup-")
                             for d in self.profiles.parent.iterdir()))

    def test_failed_park_never_touches_or_evicts_the_real_store(self):
        runner = self.runner()
        self.profiles.mkdir(parents=True)
        (self.profiles / "profile-3.json").write_text('{"id":"orig"}',
                                                    encoding="utf-8")
        protection = runner.protection_record(self.profiles)
        with mock.patch.object(Path, "rename", side_effect=OSError("denied")):
            with self.assertRaises(OSError):
                with runner.preserved_profiles(self.profiles, self.root, protection):
                    pass
        # The real store was never displaced and was not mistaken for created
        # content: it stands exactly where it was.
        self.assertEqual(sorted(d.name for d in self.profiles.parent.iterdir()),
                         ["Profiles"])
        self.assertTrue((self.profiles / "profile-3.json").is_file())
        self.assertTrue(protection["restored"])
        self.assertIsNone(protection["restoreError"])
        self.assertIsNone(protection["createdProfiles"])

    def test_backup_restore_failure_is_flagged_and_data_survives_at_backup(self):
        runner = self.runner()
        self.profiles.mkdir(parents=True)
        (self.profiles / "profile-3.json").write_text('{"id":"orig"}',
                                                    encoding="utf-8")
        real_rename = Path.rename

        def flaky(self, target):
            # Fail only the restore hop (backup -> Profiles), never the park.
            if "validation-backup" in self.name and Path(target).name == "Profiles":
                raise OSError("denied")
            return real_rename(self, target)

        protection = runner.protection_record(self.profiles)
        with mock.patch.object(Path, "rename", flaky):
            with runner.preserved_profiles(self.profiles, self.root, protection):
                self.profiles.mkdir(parents=True)
                (self.profiles / "profile-0.json").write_text("{}", encoding="utf-8")
        self.assertFalse(protection["restored"])
        self.assertIn("backup restore failed", protection["restoreError"])
        backups = [d for d in self.profiles.parent.iterdir()
                   if d.name.startswith("Profiles.validation-backup-")]
        self.assertEqual(len(backups), 1)
        self.assertEqual(json.loads(
            (backups[0] / "profile-3.json").read_text())["id"], "orig")
        self.assertTrue((self.root / "created-profiles" / "profile-0.json").is_file())

    def test_process_exit_status_contract(self):
        runner = self.runner()
        runner.validate_process_exit_status(
            {"captureValid": False, "captureComplete": False,
             "captureError": "processExit"})
        for mutant in ({"captureValid": True, "captureComplete": False,
                        "captureError": "processExit"},
                       {"captureValid": False, "captureComplete": True,
                        "captureError": "processExit"},
                       {"captureValid": False, "captureComplete": False,
                        "captureError": "controllerDisconnected"},
                       {"captureValid": False, "captureComplete": False,
                        "captureError": ""}):
            with self.subTest(mutant=mutant), self.assertRaises(AssertionError):
                runner.validate_process_exit_status(mutant)

    def test_existing_evidence_is_never_overwritten(self):
        runner = self.runner()
        root = self.root / "evidence"
        root.mkdir()
        marker = root / "result.json"
        marker.write_text("untouched", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            runner.run_scenario(["missing-player"], root, profiles_dir=self.profiles,
                                project_settings=self.settings)
        self.assertEqual(marker.read_text(encoding="utf-8"), "untouched")

    def test_end_to_end_normal_store_passes_and_restores_real_profiles(self):
        self.profiles.mkdir(parents=True)
        (self.profiles / "profile-3.json").write_text(
            json.dumps({"schemaVersion": 1, "id": "user-real", "slot": 3,
                        "language": "en", "camera": "chase",
                        "reducedCameraShake": False, "musicVolume": 1.0,
                        "effectsVolume": 1.0, "voiceVolume": 1.0, "coins": 9}),
            encoding="utf-8")
        evidence = self.root / "evidence"
        result = self.run_scenario(evidence, self.profiles)
        self.assertEqual(result["status"], "passed", result)
        self.assertEqual(len(result["runs"]), 2)
        first, second = result["runs"]
        self.assertIn("--validation-normal-profiles", first["command"])
        self.assertNotIn("--validation-profile-dir", first["command"])
        self.assertEqual(first["exitStatus"]["captureError"], "processExit")
        self.assertTrue(first["processExitVerified"])
        self.assertTrue(second["captureVerified"])
        self.assertNotEqual(first["runId"], second["runId"])
        # The real user store is back, byte-identical, and the fixture-created
        # store was quarantined into the evidence directory.
        self.assertEqual([path.name for path in self.profiles.iterdir()],
                         ["profile-3.json"])
        self.assertEqual(json.loads((self.profiles / "profile-3.json").read_text())["id"],
                         "user-real")
        created = evidence / "created-profiles"
        self.assertTrue((created / "profile-0.json").is_file())
        self.assertTrue((created / "profile-0.json.bak").is_file())
        protection = result["profileProtection"]
        self.assertTrue(protection["existed"])
        self.assertTrue(protection["restored"])
        self.assertEqual(json.loads((evidence / "result.json").read_text())["status"],
                         "passed")

    def test_unsaved_draft_persisted_on_exit_fails(self):
        result = self.run_scenario(self.root / "evidence", self.profiles,
                                   "--persist-drafts")
        self.assertEqual(result["status"], "failed", result)

    def test_wrong_terminal_status_code_fails(self):
        result = self.run_scenario(self.root / "evidence", self.profiles,
                                   "--wrong-exit-status")
        self.assertEqual(result["status"], "failed", result)
        self.assertIn("processExit", result["error"])

    def test_violent_exit_without_terminal_status_fails(self):
        result = self.run_scenario(self.root / "evidence", self.profiles,
                                   "--exit-violent")
        self.assertEqual(result["status"], "failed", result)

    def test_profile_written_to_capture_store_fails(self):
        result = self.run_scenario(self.root / "evidence", self.profiles,
                                   "--use-capture-profiles")
        self.assertEqual(result["status"], "failed", result)
        self.assertIn("real profile store", result["error"])

    def test_rewritten_store_on_reload_fails(self):
        # A logically identical but byte-different rewrite must be caught.
        result = self.run_scenario(self.root / "evidence", self.profiles,
                                   "--rewrite-on-load")
        self.assertEqual(result["status"], "failed", result)
        self.assertIn("rewrote", result["error"])

    def test_early_player_exit_is_recorded_as_failure(self):
        result = self.run_scenario(self.root / "evidence", self.profiles,
                                   "--exit-early")
        self.assertEqual(result["status"], "failed", result)
        self.assertIn("7", result["error"])
        self.assertEqual(result["runs"][0]["exitCode"], 7)


if __name__ == "__main__":
    if "--fake-player" in sys.argv:
        sys.exit(fake_player())
    unittest.main()
