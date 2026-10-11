"""Behavioral tests for the restart evidence tool; the fixture is not Unity proof."""

import hashlib
import importlib
import json
import os
import socket
import struct
import sys
import tempfile
import time
import unittest
import uuid
from pathlib import Path


def fake_player():
    """Small external protocol fixture, exercising real processes, sockets and files."""
    args = sys.argv

    def option(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    directory = Path(option("--validation-dir"))
    profiles = Path(option("--validation-profile-dir"))
    log = Path(option("-logFile"))
    log.write_text("Synthetic protocol fixture, not a Unity player\n", encoding="utf-8")
    if "--exit-early" in args:
        return 7
    if directory.exists() and list(directory.iterdir()):
        return 8
    directory.mkdir(parents=True, exist_ok=True)
    profiles.mkdir(parents=True, exist_ok=True)
    slots = [{"slot": i, "status": "Missing", "profile": None} for i in range(4)]
    for slot in slots:
        path = profiles / ("profile-%d.json" % slot["slot"])
        if path.exists():
            profile = json.loads(path.read_text(encoding="utf-8"))
            slot.update(status="Loaded", profile=profile)
            if "--rewrite-on-load" in args:
                path.write_text(json.dumps(profile, indent=4), encoding="utf-8")
            damage = option("--damage-field")
            if damage:
                profile[damage] = {
                    "id": "00000000-0000-0000-0000-000000000000",
                    "language": "en", "camera": "chase", "reducedCameraShake": False,
                    "musicVolume": 1.0, "effectsVolume": 1.0, "voiceVolume": 1.0,
                }[damage]
    state = {
        "screen": "profiles", "language": "en", "activeSlot": -1, "paused": False,
        "tick": 0, "input": {"steering": 0, "drift": False, "boost": False,
                             "gadget": False, "recover": False, "changeCamera": False,
                             "pause": False},
        "slots": slots, "savedProfile": None, "draftProfile": None,
        "pendingSettings": False, "persistenceStatus": "", "captureValid": True,
        "captureError": "", "featureCoverage": {}, "presentation": {},
    }
    run_id = uuid.uuid4().hex
    token = uuid.uuid4().hex
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    connection = {"protocolVersion": 1, "host": "127.0.0.1", "port": server.getsockname()[1],
                  "runId": run_id, "token": token, "participantId": "local"}
    sequence = 0
    stream = (directory / "state.ndjson").open("w", encoding="utf-8")
    conn = None

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
        if conn:
            conn.sendall(line.encode("utf-8"))

    emit("snapshot", state=state)
    (directory / "connection.json").write_text(json.dumps(connection), encoding="utf-8")
    conn, _ = server.accept()
    reader = conn.makefile("rb")
    hello = json.loads(reader.readline())
    if hello.get("token") != token:
        return 9
    conn.sendall((json.dumps({"type": "hello", "protocolVersion": 1,
                              "runId": run_id}) + "\n").encode("utf-8"))
    emit("snapshot", state=state)
    while True:
        raw = reader.readline()
        if not raw:
            return 10
        frame = json.loads(raw)
        kind = frame["type"]
        if kind == "health":
            emit("health", captureValid=True, captureError="", pendingInbound=0,
                 pendingCommands=0, controllerAttached=True)
        elif kind == "finish":
            (directory / "capture-status.json").write_text(json.dumps({
                "captureValid": True, "captureComplete": True}), encoding="utf-8")
            emit("finished", captureValid=True)
            while True:
                time.sleep(1)
        elif kind == "command":
            action, params = frame["action"], frame["parameters"]
            if action in ("profile.create", "profile.select"):
                index = int(params["slot"])
                if action == "profile.create":
                    profile = {"schemaVersion": 1, "id": str(uuid.uuid4()), "slot": index,
                               "avatar": "comet", "colorIndex": index, "language": params["language"],
                               "camera": "chase", "reducedCameraShake": False,
                               "musicVolume": 1.0, "effectsVolume": 1.0, "voiceVolume": 1.0,
                               "coins": 0, "unlockedVehicles": ["starter"]}
                    slots[index].update(status="Loaded", profile=profile)
                    (profiles / ("profile-%d.json" % index)).write_text(json.dumps(profile), encoding="utf-8")
                profile = slots[index]["profile"]
                state.update(activeSlot=index, savedProfile=profile,
                             draftProfile=dict(profile), screen="settings", language=profile["language"],
                             pendingSettings=False)
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
            emit("result", commandId=frame["commandId"], status="applied", code="", duplicate=False)
        else:
            return 12


class RestartTests(unittest.TestCase):
    def runner(self):
        self.assertTrue(Path(__file__).with_name("RestartScenario.py").is_file(),
                        "Restart companion has not been implemented")
        return importlib.import_module("RestartScenario")

    def run_scenario(self, root, *flags):
        return self.runner().run_scenario(
            [sys.executable, str(Path(__file__).resolve()), "--fake-player", *flags],
            root, startup_timeout=5, shutdown_timeout=3)

    def test_profile_comparison_uses_exact_single_precision_volume_values(self):
        runner = self.runner()
        expected = {"musicVolume": 0.65, "effectsVolume": 0.85, "voiceVolume": 1.0}
        expanded = {
            key: struct.unpack("!f", struct.pack("!f", value))[0]
            for key, value in expected.items()
        }
        runner.assert_profile(expected, expanded, "Equivalent C# floats")
        runner.assert_profile(expanded, expected, "Equivalent C# floats reversed")
        next_float = struct.unpack("!f", bytes.fromhex("3f266667"))[0]
        with self.assertRaisesRegex(AssertionError, "musicVolume"):
            runner.assert_profile(expected, {**expanded, "musicVolume": next_float},
                                  "One representable float changed")

    def test_profile_comparison_keeps_identity_and_nonvolume_fields_exact(self):
        runner = self.runner()
        expected = {"id": "synthetic", "coins": 3, "reducedCameraShake": True,
                    "musicVolume": 0.5, "effectsVolume": 0.5, "voiceVolume": 1.0}
        for field, value in (("id", "different"), ("coins", 3.00000001),
                             ("coins", 3.0), ("reducedCameraShake", False),
                             ("reducedCameraShake", 1), ("extra", 1)):
            with self.subTest(field=field), self.assertRaisesRegex(AssertionError, field):
                runner.assert_profile(expected, {**expected, field: value}, "Changed field")
        for field in expected:
            missing = dict(expected)
            del missing[field]
            with self.subTest(missing=field), self.assertRaisesRegex(AssertionError, field):
                runner.assert_profile(expected, missing, "Missing field")

    def test_profile_comparison_rejects_invalid_volume_numbers(self):
        runner = self.runner()
        expected = {"musicVolume": 1.0}
        for value in (True, "1", None, float("nan"), float("inf"), 1e100):
            with self.subTest(value=value), self.assertRaisesRegex(AssertionError, "musicVolume"):
                runner.assert_profile(expected, {"musicVolume": value}, "Invalid volume")

    def test_build_fingerprint_includes_managed_code_not_only_launcher(self):
        runner = self.runner()
        with tempfile.TemporaryDirectory() as temp:
            executable = Path(temp) / "Player.exe"
            executable.write_bytes(b"unchanging launcher")
            managed = Path(temp) / "Player_Data" / "Managed"
            managed.mkdir(parents=True)
            runtime = managed / "FamilyRacing.Runtime.dll"
            runtime.write_bytes(b"first runtime")
            first = runner.build_fingerprint(executable)
            runtime.write_bytes(b"changed runtime")
            second = runner.build_fingerprint(executable)
            self.assertEqual(first["Player.exe"], second["Player.exe"])
            self.assertNotEqual(first["Player_Data/Managed/FamilyRacing.Runtime.dll"],
                                second["Player_Data/Managed/FamilyRacing.Runtime.dll"])

    def test_two_processes_load_same_save_and_preserve_all_capture_evidence(self):
        # Catches re-creating profiles, incomplete setting assertions, reused captures,
        # missing durable finish, or failure to stop the first process before restart.
        with tempfile.TemporaryDirectory(prefix="restart evidence ") as temp:
            root = Path(temp) / "evidence with spaces"
            result = self.run_scenario(root)
            self.assertEqual(result["status"], "passed", result)
            self.assertEqual(len(result["runs"]), 2)
            self.assertNotEqual(result["runs"][0]["runId"], result["runs"][1]["runId"])
            self.assertTrue(all(run["processExited"] for run in result["runs"]))
            self.assertEqual(result["saveHashesBeforeRestart"], result["saveHashesAfterRestart"])
            self.assertEqual(result["executable"]["sha256"], hashlib.sha256(Path(sys.executable).read_bytes()).hexdigest())
            self.assertEqual(set(result["profiles"]), {"0", "3"})
            for phase in ("first", "second"):
                for filename in ("state.ndjson", "capture-status.json", "connection.json"):
                    self.assertTrue((root / (phase + "-run") / filename).is_file())
                records = [json.loads(line) for line in (root / (phase + "-evidence") / "observed.ndjson").read_text().splitlines()]
                self.assertTrue(any(row["type"] == "health" and row["captureValid"] for row in records))
                self.assertEqual(records[-1]["type"], "finished")
            commands = [json.loads(line) for line in (root / "second-evidence" / "commands.ndjson").read_text().splitlines()]
            self.assertEqual([row["action"] for row in commands if row["type"] == "command"],
                             ["profile.select", "profile.select"])
            self.assertEqual(json.loads((root / "result.json").read_text()), result)

    def test_identity_or_any_saved_setting_loss_fails_after_restart(self):
        # Catches silently accepting a correct identity with one defaulted setting.
        for field in ("id", "language", "camera", "reducedCameraShake", "musicVolume", "effectsVolume", "voiceVolume"):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temp:
                result = self.run_scenario(Path(temp) / "evidence", "--damage-field", field)
                self.assertEqual(result["status"], "failed", result)
                self.assertIn(field, result["error"])
                self.assertTrue(all(run["processExited"] for run in result["runs"]))

    def test_logically_identical_rewritten_save_does_not_pass_same_file_evidence(self):
        # Catches validating JSON alone while silently replacing/rewriting the bytes.
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_scenario(Path(temp) / "evidence", "--rewrite-on-load")
            self.assertEqual(result["status"], "failed", result)
            self.assertIn("save files changed", result["error"].lower())

    def test_early_player_exit_is_recorded_as_failure_and_no_second_process_starts(self):
        # Catches waiting forever or reporting passed when startup dies.
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "evidence"
            result = self.run_scenario(root, "--exit-early")
            self.assertEqual(result["status"], "failed", result)
            self.assertIn("7", result["error"])
            self.assertEqual(len(result["runs"]), 1)
            self.assertEqual(result["runs"][0]["exitCode"], 7)
            self.assertTrue((root / "first-player.log").is_file())
            self.assertTrue((root / "result.json").is_file())

    def test_existing_evidence_directory_is_never_reused_or_changed(self):
        # Catches accidental access to existing saves instead of owned synthetic data.
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sentinel = root / "profile-0.json"
            sentinel.write_bytes(b"existing user data")
            with self.assertRaises(FileExistsError):
                self.run_scenario(root)
            self.assertEqual(list(root.iterdir()), [sentinel])
            self.assertEqual(sentinel.read_bytes(), b"existing user data")


if __name__ == "__main__":
    if "--fake-player" in sys.argv:
        sys.exit(fake_player())
    unittest.main()
