"""Integrity checks for the stalled-finish scenario, not evidence that Unity passed."""

import importlib
import json
import socket
import sys
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path


def fake_player():
    """External protocol fixture emulating the stalled-finish outcomes.

    It speaks the real wire protocol and produces the real artifacts
    (connection.json, state.ndjson, capture-status.json, rejection frames)
    so the scenario's checks are exercised end to end -- it is never proof
    the actual game passed.
    """
    args = sys.argv

    def option(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    directory = Path(option("--validation-dir"))
    log = Path(option("-logFile"))
    log.write_text("Synthetic stalled-finish fixture, not a Unity player\n",
                   encoding="utf-8")
    if "--exit-early" in args:
        return 7
    directory.mkdir(parents=True, exist_ok=True)
    state = {
        "screen": "profiles", "language": "en", "activeSlot": -1, "paused": False,
        "tick": 0, "input": {"steering": 0, "drift": False, "boost": False,
                             "gadget": False, "recover": False, "changeCamera": False,
                             "pause": False},
        "slots": [{"slot": i, "status": "Missing", "profile": None} for i in range(4)],
        "savedProfile": None, "draftProfile": None, "pendingSettings": False,
        "captureValid": True, "captureError": "", "featureCoverage": {},
        "presentation": {},
    }
    run_id = uuid.uuid4().hex
    token = uuid.uuid4().hex
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(4)
    connection = {"protocolVersion": 1, "host": "127.0.0.1",
                  "port": server.getsockname()[1],
                  "runId": run_id, "token": token, "participantId": "local"}
    sequence = 0
    stream = (directory / "state.ndjson").open("w", encoding="utf-8")
    conn = {"sock": None, "dead": False}
    done = {"finish": False}

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
        if conn["sock"] is not None and not conn["dead"]:
            try:
                conn["sock"].sendall(line.encode("utf-8"))
            except OSError:
                conn["dead"] = True  # the stalled sink surfaced; keep tracing

    def reject(sock, code):
        try:
            payload = (json.dumps({"type": "error", "protocolVersion": 1,
                                   "runId": run_id, "code": code}) + "\n").encode()
            sock.settimeout(2)
            sock.sendall(payload)
            sock.settimeout(0.05)
            try:
                while sock.recv(4096):
                    pass
            except OSError:
                pass
            sock.shutdown(socket.SHUT_WR)
        except OSError:
            pass
        try:
            sock.close()
        except OSError:
            pass

    def rejector():
        # Every accept after the first session is a probe: captureClosed once
        # finish was processed, controllerBusy while the session still stands.
        while True:
            try:
                candidate, _ = server.accept()
            except OSError:
                return
            code = ("captureClosed"
                    if done["finish"] and "--reject-busy" not in args
                    else "controllerBusy")
            reject(candidate, code)

    emit("snapshot", state=state)
    (directory / "capture-status.json").write_text(json.dumps({
        "captureValid": True, "captureComplete": False, "captureError": "",
        "recordsEmitted": sequence}), encoding="utf-8")
    (directory / "connection.json").write_text(json.dumps(connection), encoding="utf-8")
    server.settimeout(10)
    session, _ = server.accept()
    server.settimeout(None)
    session.settimeout(0.3)
    conn["sock"] = session
    threading.Thread(target=rejector, daemon=True).start()
    reader = session.makefile("rb")
    try:
        hello = json.loads(reader.readline())
        if hello.get("token") != token:
            return 9
        session.sendall((json.dumps({"type": "hello", "protocolVersion": 1,
                                     "runId": run_id,
                                     "buildVersion": "foundation-0.1",
                                     "contentVersion": "foundation-0.1"})
                         + "\n").encode())
        emit("snapshot", state=state)
        while True:
            try:
                raw = reader.readline()
            except OSError:
                continue  # bounded receive window; keep waiting for frames
            if not raw:
                return 10
            frame = json.loads(raw)
            if frame["type"] == "lookup":
                emit("lookup", commandId=frame.get("commandId"),
                     found=False, result=None)
            elif frame["type"] == "finish":
                if "--never-invalidate" in args:
                    continue  # the fault is never detected: status stays valid
                done["finish"] = True
                (directory / "capture-status.json").write_text(json.dumps({
                    "captureValid": False, "captureComplete": False,
                    "captureError": "streamError", "recordsEmitted": sequence}),
                    encoding="utf-8")
                conn["dead"] = True  # finished can no longer reach its target
                if "--finish-valid" in args:
                    emit("finished", captureValid=True, captureError="")
                else:
                    emit("finished", captureValid=False, captureError="streamError")
                try:
                    session.close()
                except OSError:
                    pass
                while True:
                    time.sleep(1)
            else:
                return 12
    finally:
        reader.close()


class FinishDeliveryIntegrityTests(unittest.TestCase):
    def runner(self):
        self.assertTrue(Path(__file__).with_name("FinishDeliveryScenario.py").is_file(),
                        "Finish-delivery companion has not been implemented")
        return importlib.import_module("FinishDeliveryScenario")

    def run_scenario(self, root, *flags):
        return self.runner().run_scenario(
            [sys.executable, str(Path(__file__).resolve()), "--fake-player", *flags],
            root, startup_timeout=5, shutdown_timeout=3, fault_timeout=4)

    def event(self, sequence=1, kind="snapshot"):
        return {
            "type": kind, "protocolVersion": 1, "runId": "run",
            "deviceId": "device", "participantId": "local",
            "buildVersion": "foundation-0.1", "contentVersion": "foundation-0.1",
            "sequence": sequence, "monotonicMs": sequence * 10, "tick": sequence,
            "state": {"tick": sequence, "captureValid": True, "captureError": "",
                      "slots": [{}, {}, {}, {}]},
        }

    def test_terminal_code_set_is_exactly_stream_error(self):
        runner = self.runner()
        self.assertEqual(runner.TERMINAL_ERRORS, frozenset({"streamError"}))
        status = {"captureValid": False, "captureComplete": False,
                  "captureError": "streamError"}
        runner.validate_invalid_status(status, runner.TERMINAL_ERRORS)
        for key, value in (("captureValid", True), ("captureComplete", True),
                           ("captureError", "controllerDisconnected"),
                           ("captureError", "outgoingQueueFull"),
                           ("captureError", "")):
            with self.subTest(key=key, value=value), self.assertRaises(AssertionError):
                runner.validate_invalid_status(dict(status, **{key: value}),
                                               runner.TERMINAL_ERRORS)

    def finish_event(self, sequence, valid, error):
        row = self.event(sequence, "finished")
        del row["state"]  # finished records carry no state payload
        row.update(captureValid=valid, captureError=error)
        return row

    def test_stalled_trace_accepts_invalid_finish_as_last_record(self):
        runner = self.runner()
        info = {"runId": "run", "token": "never-leak-this"}
        with tempfile.TemporaryDirectory() as temp:
            trace = Path(temp) / "state.ndjson"
            rows = [self.event(), self.event(2, "state"),
                    dict(self.event(3, "lookup"), commandId="x", found=False,
                         result=None),
                    self.finish_event(4, valid=False, error="streamError")]
            trace.write_text("".join(json.dumps(row) + "\n" for row in rows),
                             encoding="utf-8")
            summary = runner.verify_stalled_trace(trace, info)
            self.assertEqual(summary["lastSequence"], 4)
            self.assertTrue(summary["finishedTraced"])

    def test_stalled_trace_rejects_broken_or_valid_claiming_prefixes(self):
        runner = self.runner()
        info = {"runId": "run", "token": "never-leak-this"}
        first, second = self.event(), self.event(2, "state")
        good = json.dumps(first) + "\n" + json.dumps(second) + "\n"
        valid_finish = self.finish_event(3, valid=True, error="")
        invalid_finish_then_state = (
            json.dumps(first) + "\n"
            + json.dumps(self.finish_event(3, valid=False,
                                           error="streamError")) + "\n"
            + json.dumps(self.event(4, "state")) + "\n")
        empty_error_finish = (
            json.dumps(first) + "\n"
            + json.dumps(self.finish_event(3, valid=False, error="")) + "\n")
        with tempfile.TemporaryDirectory() as temp:
            trace = Path(temp) / "state.ndjson"
            for bad in (
                "",
                good.rstrip("\n"),
                json.dumps(first) + "\n" + json.dumps(self.event(3, "state")) + "\n",
                json.dumps(self.event(2)) + "\n",
                good.replace('"run"', '"other"'),
                good.replace('"device"', '"never-leak-this"'),
                json.dumps(first) + "\n" + json.dumps(dict(second, deviceId="other")) + "\n",
                json.dumps(first) + "\n" + json.dumps(dict(second, type="hello")) + "\n",
                json.dumps(first) + "\n" + json.dumps(valid_finish) + "\n",
                invalid_finish_then_state,
                empty_error_finish,
            ):
                trace.write_text(bad, encoding="utf-8")
                with self.subTest(prefix=bad[:70]), self.assertRaises(AssertionError):
                    runner.verify_stalled_trace(trace, info)

    def test_rejection_reader_contract(self):
        runner = self.runner()
        for payload, tail, expected in (
            (json.dumps({"type": "error", "protocolVersion": 1, "runId": "run",
                         "code": "captureClosed"}) + "\n", "eof", "captureClosed"),
            (json.dumps({"type": "error", "protocolVersion": 1, "runId": "run",
                         "code": "controllerBusy"}) + "\n", "eof", "controllerBusy"),
            (json.dumps({"type": "error", "protocolVersion": 1, "runId": "other",
                         "code": "captureClosed"}) + "\n", "eof", "assert"),
            (json.dumps({"type": "hello", "protocolVersion": 1, "runId": "run"}) + "\n",
             "eof", "assert"),
            (json.dumps({"type": "error", "protocolVersion": 1, "runId": "run",
                         "code": "captureClosed"}), "eof", "assert"),
            (json.dumps({"type": "error", "protocolVersion": 1, "runId": "run",
                         "code": "captureClosed"}) + "\n", "open", "assert"),
        ):
            with self.subTest(payload=payload[:50], tail=tail), \
                    socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                listener.listen()
                listener.settimeout(3)

                def peer():
                    with listener.accept()[0] as connection:
                        connection.settimeout(3)
                        connection.sendall(payload.encode())
                        if tail == "eof":
                            connection.shutdown(socket.SHUT_WR)
                        else:
                            time.sleep(3.5)

                thread = threading.Thread(target=peer)
                thread.start()
                try:
                    if expected == "assert":
                        with self.assertRaises(AssertionError):
                            runner.read_rejection(
                                listener.getsockname()[1], "run", timeout=2)
                    else:
                        self.assertEqual(
                            runner.read_rejection(listener.getsockname()[1],
                                                  "run", timeout=2), expected)
                finally:
                    thread.join(5)
                self.assertFalse(thread.is_alive(), "Fixture thread leaked")

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
            self.assertEqual(json.loads((root / "result.json").read_text())["status"],
                             "failed")

    def test_end_to_end_stalled_finish_flow_passes_on_fixture(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_scenario(Path(temp) / "evidence")
            self.assertEqual(result["status"], "passed", result)
            self.assertEqual(len(result["runs"]), 1)
            run = result["runs"][0]
            self.assertTrue(run["processExited"])
            self.assertTrue(run["expectedFaultVerified"])
            self.assertEqual(run["terminalStatus"]["captureError"], "streamError")
            self.assertFalse(run["terminalStatus"]["captureValid"])
            self.assertFalse(run["terminalStatus"]["captureComplete"])
            self.assertEqual(run["rejection"]["code"], "captureClosed")
            self.assertTrue(run["prefix"]["finishedTraced"])
            command = run["command"]
            self.assertIn("--validation-dir", command)
            self.assertNotIn("--validation-normal-profiles", command)
            self.assertEqual(json.loads((Path(temp) / "evidence" / "result.json")
                                        .read_text())["status"], "passed")

    def test_finish_record_claiming_validity_fails(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_scenario(Path(temp) / "evidence", "--finish-valid")
            self.assertEqual(result["status"], "failed", result)
            self.assertIn("valid", result["error"].lower())

    def test_fault_never_detected_fails(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_scenario(Path(temp) / "evidence", "--never-invalidate")
            self.assertEqual(result["status"], "failed", result)

    def test_persistent_busy_rejection_fails(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_scenario(Path(temp) / "evidence", "--reject-busy")
            self.assertEqual(result["status"], "failed", result)
            self.assertIn("captureClosed", result["error"])

    def test_early_player_exit_is_recorded_as_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.run_scenario(Path(temp) / "evidence", "--exit-early")
            self.assertEqual(result["status"], "failed", result)
            self.assertIn("7", result["error"])
            self.assertEqual(result["runs"][0]["exitCode"], 7)


if __name__ == "__main__":
    if "--fake-player" in sys.argv:
        sys.exit(fake_player())
    unittest.main()
