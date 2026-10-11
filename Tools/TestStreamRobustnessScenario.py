"""Integrity checks for the fault scenario, not evidence that Unity passed."""

import importlib
import json
import socket
import tempfile
import threading
import unittest
from pathlib import Path


class StreamIntegrityTests(unittest.TestCase):
    def runner(self):
        self.assertTrue(Path(__file__).with_name("StreamRobustnessScenario.py").is_file(),
                        "External stream companion has not been implemented")
        return importlib.import_module("StreamRobustnessScenario")

    def event(self, sequence=1, kind="snapshot"):
        return {
            "type": kind, "protocolVersion": 1, "runId": "run",
            "deviceId": "device", "participantId": "local",
            "buildVersion": "foundation-0.1", "contentVersion": "foundation-0.1",
            "sequence": sequence, "monotonicMs": sequence * 10, "tick": sequence,
            "state": {"tick": sequence, "captureValid": True, "captureError": ""},
        }

    def state(self):
        return {
            "captureValid": False, "captureError": "controllerDisconnected",
            "activeSlot": 0, "screen": "sandbox", "savedProfile": {"id": "profile"},
            "input": {"steering": 0, "drift": False, "boost": False, "gadget": False,
                      "recover": False, "changeCamera": False, "pause": False},
        }

    def test_invalid_status_cannot_be_healthy_complete_or_wrong_failure(self):
        runner = self.runner()
        status = {"captureValid": False, "captureComplete": False,
                  "captureError": "streamError"}
        runner.validate_invalid_status(status, {"streamError"})
        for key, value in (("captureValid", True), ("captureValid", 0),
                           ("captureComplete", True), ("captureComplete", 0),
                           ("captureError", "outgoingQueueFull")):
            with self.subTest(key=key, value=value), self.assertRaises(AssertionError):
                runner.validate_invalid_status(dict(status, **{key: value}), {"streamError"})

    def test_resume_rejects_changed_profile_held_input_and_restored_capture(self):
        runner = self.runner()
        runner.validate_resume(self.state(), "profile", {"controllerDisconnected"})
        for key, value in (("captureValid", True), ("captureError", "streamError"),
                           ("savedProfile", {"id": "other"}), ("activeSlot", 1),
                           ("screen", "profiles")):
            with self.subTest(key=key), self.assertRaises(AssertionError):
                runner.validate_resume(dict(self.state(), **{key: value}),
                                       "profile", {"controllerDisconnected"})
        for key in self.state()["input"]:
            state = self.state()
            state["input"][key] = 1 if key == "steering" else True
            with self.subTest(input=key), self.assertRaises(AssertionError):
                runner.validate_resume(state, "profile", {"controllerDisconnected"})

    def test_duplicate_requires_original_terminal_outcome_and_tick(self):
        runner = self.runner()
        original = {"commandId": "create", "action": "profile.create", "status": "applied",
                    "code": "ok", "tick": 12, "duplicate": False}
        runner.validate_outcome(original, dict(original, duplicate=True), duplicate=True)
        runner.validate_outcome(original, original, duplicate=False)
        for key, value in (("tick", 13), ("commandId", "different"),
                           ("action", "settings.save"), ("status", "failed"),
                           ("code", "different"), ("duplicate", False)):
            with self.subTest(key=key), self.assertRaises(AssertionError):
                runner.validate_outcome(original, dict(original, duplicate=True, **{key: value})
                                        if key != "duplicate" else dict(original),
                                        duplicate=True)

    def test_failed_prefix_still_requires_contiguous_complete_credential_free_records(self):
        runner = self.runner()
        first, second = self.event(), self.event(2, "state")
        info = {"runId": "run", "token": "never-leak-this"}
        with tempfile.TemporaryDirectory() as temp:
            trace = Path(temp) / "state.ndjson"
            good = json.dumps(first) + "\n" + json.dumps(second) + "\n"
            trace.write_text(good, encoding="utf-8")
            summary = runner.verify_failed_prefix(trace, info)
            self.assertEqual(summary["lastSequence"], 2)
            for bad in (
                good.rstrip("\n"),
                json.dumps(first) + "\n" + json.dumps(self.event(3, "state")) + "\n",
                json.dumps(self.event(2)) + "\n",
                good.replace('"run"', '"other"'),
                good.replace('"device"', '"never-leak-this"'),
                json.dumps(first) + "\n" + json.dumps(dict(second, deviceId="other")) + "\n",
                json.dumps(first) + "\n" + json.dumps(dict(second, type="hello")) + "\n",
                json.dumps(first) + "\n" + json.dumps(dict(second, type="error")) + "\n",
                json.dumps(first) + "\n" + json.dumps(dict(second, type="finished",
                                                         captureValid=True)) + "\n",
            ):
                trace.write_text(bad, encoding="utf-8")
                with self.subTest(prefix=bad[:70]), self.assertRaises(AssertionError):
                    runner.verify_failed_prefix(trace, info)

    def test_existing_evidence_is_never_overwritten(self):
        runner = self.runner()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            marker = root / "result.json"
            marker.write_text("untouched", encoding="utf-8")
            with self.assertRaises(FileExistsError):
                runner.run_scenario(["missing-player"], root)
            self.assertEqual(marker.read_text(encoding="utf-8"), "untouched")

    def test_startup_failure_is_recorded_not_passing(self):
        runner = self.runner()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "new-evidence"
            result = runner.run_scenario([str(Path(temp) / "missing-player")], root)
            self.assertEqual(result["status"], "failed")
            self.assertEqual(result["checks"], [])
            self.assertEqual(json.loads((root / "result.json").read_text())["status"], "failed")

    def test_live_fault_observer_accepts_invalid_snapshot_but_not_a_sequence_gap(self):
        runner = self.runner()
        with tempfile.TemporaryDirectory() as temp, socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            listener.settimeout(3)
            info = {"port": listener.getsockname()[1], "runId": "run", "token": "secret"}
            snapshot = self.event(50)
            snapshot["state"].update(captureValid=False, captureError="controllerDisconnected",
                                     slots=[{}, {}, {}, {}])
            skipped = self.event(52, "state")
            skipped["state"].update(slots=[{}, {}, {}, {}])
            errors = []

            def peer():
                try:
                    with listener.accept()[0] as connection:
                        connection.settimeout(3)
                        reader = connection.makefile("rb")
                        try:
                            json.loads(reader.readline())
                            hello = {"type": "hello", "protocolVersion": 1, "runId": "run"}
                            connection.sendall(("".join(json.dumps(row) + "\n" for row in
                                                       (hello, snapshot, skipped))).encode())
                            reader.read(1)
                        finally:
                            reader.close()
                except Exception as error:
                    errors.append(error)

            thread = threading.Thread(target=peer)
            thread.start()
            client = None
            try:
                client = runner.open_client(info, Path(temp))
                self.assertFalse(client.state["captureValid"])
                self.assertEqual(client.last_sequence, 50)
                with self.assertRaisesRegex(AssertionError, "Missing/out-of-order"):
                    client.read(2)
            finally:
                if client is not None:
                    client.close()
                thread.join(4)
            self.assertFalse(thread.is_alive(), "Fixture thread leaked")
            self.assertEqual(errors, [])
            self.assertNotIn("secret", (Path(temp) / "commands.ndjson").read_text())


if __name__ == "__main__":
    unittest.main()
