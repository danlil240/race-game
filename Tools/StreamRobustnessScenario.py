"""Expected-fault qualification on fresh external Windows validation players.

Passing this scenario means faults were detected correctly. All three captures
remain INVALID; this tool never certifies missing history or tests ordinary saves.
"""

import argparse
import json
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

from AgentScenario import Client, SEQUENCED_TYPES, inputs, validate_record, validate_state_tick
from RestartScenario import build_fingerprint, file_hash, wait_connection, write_json


def validate_invalid_status(status, errors):
    assert status.get("captureValid") is False, "Fault capture is still valid"
    assert status.get("captureComplete") is False, "Fault capture was certified complete"
    assert status.get("captureError") in errors, ("Unexpected failure", status)


def validate_resume(state, profile_id, errors):
    assert state.get("captureValid") is False, "Reconnect restored capture validity"
    assert state.get("captureError") in errors, state.get("captureError")
    assert state["activeSlot"] == 0 and state["screen"] == "sandbox"
    assert state["savedProfile"]["id"] == profile_id, "Reconnect changed profile identity"
    held = state["input"]
    assert type(held["steering"]) in (int, float) and held["steering"] == 0
    assert all(held[key] is False for key in (
        "drift", "boost", "gadget", "recover", "changeCamera", "pause"
    )), "Old controller retained held input"


def validate_outcome(original, actual, duplicate):
    assert original["status"] == "applied"
    for key in ("commandId", "action", "status", "code", "tick"):
        assert type(actual[key]) is type(original[key]) and actual[key] == original[key], \
            "Original outcome changed: " + key
    assert actual["duplicate"] is duplicate, "Wrong duplicate-delivery flag"


def verify_failed_prefix(trace, info):
    """Validate the surviving history without requiring a healthy final record."""
    assert 0 < trace.stat().st_size <= 256 * 1024 * 1024
    sequence, timestamp, tick = None, None, None
    count = 0
    identity = None
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            assert line.endswith("\n"), "Truncated trace prefix"
            assert info["token"] not in line, "Credential leaked to trace"
            event = json.loads(line)
            assert event.get("type") in SEQUENCED_TYPES, "Unsequenced reply in trace prefix"
            assert event.get("type") != "finished", "Invalid capture appended a finish"
            current_identity = tuple(event[key] for key in (
                "deviceId", "participantId", "buildVersion", "contentVersion"))
            if identity is None:
                identity = current_identity
            assert current_identity == identity, "Trace identity changed within prefix"
            if count == 0:
                assert event.get("type") == "snapshot" and event.get("sequence") == 1
            sequence, timestamp = validate_record(event, info, sequence, timestamp)
            tick = validate_state_tick(event, tick)
            count += 1
    assert count > 0
    return {"records": count, "lastSequence": sequence,
            "bytes": trace.stat().st_size, "sha256": file_hash(trace)}


class FaultClient(Client):
    """Keep healthy Client strict; only this expected-fault observer accepts invalidity."""

    def read(self, remaining):
        self.sock.settimeout(max(0.01, remaining))
        line = self.reader.readline(1024 * 1024 + 1)
        assert line and line.endswith(b"\n") and len(line) <= 1024 * 1024, \
            "Closed, oversized or truncated server record"
        event = json.loads(line)
        self.last_sequence, self.last_time = validate_record(
            event, self.info, self.last_sequence, self.last_time)
        self.last_state_tick = validate_state_tick(event, self.last_state_tick)
        assert self.info["token"] not in line.decode("utf-8"), "Credential leaked"
        if "sequence" in event:
            identity = tuple(event[key] for key in (
                "deviceId", "participantId", "buildVersion", "contentVersion"))
            if not hasattr(self, "identity"):
                assert event["type"] == "snapshot", "Attachment did not begin with a snapshot"
                self.identity = identity
                self.first_snapshot = event
            assert identity == self.identity, "Stream identity changed within attachment"
        if event.get("type") in ("snapshot", "state"):
            self.state = event["state"]
            assert type(self.state["captureValid"]) is bool and len(self.state["slots"]) == 4
        self.incoming.write(json.dumps(event, ensure_ascii=False) + "\n")
        self.incoming.flush()
        self.events.append(event)
        return event

    def close(self):
        # Close the socket before its makefile: it otherwise remains connected
        # until the last file reference is closed. This also permits TCP reset.
        try:
            self.sock.close()
        finally:
            self.reader.close()
            self.incoming.close()
            self.outgoing.close()


def synthetic_hashes(directory):
    files = sorted(directory.iterdir())
    assert {path.name for path in files} <= {"profile-0.json", "profile-0.json.bak"}
    assert (directory / "profile-0.json").is_file(), "Synthetic save missing"
    assert all(path.is_file() and not path.is_symlink() for path in files)
    return {path.name: file_hash(path) for path in files}


def wait_invalid_status(process, directory, errors, deadline):
    path = directory / "capture-status.json"
    while time.monotonic() < deadline:
        assert process.poll() is None, "Player exited before independent fault detection"
        try:
            status = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            time.sleep(0.02)
            continue
        if status.get("captureValid") is False:
            validate_invalid_status(status, errors)
            return status
        time.sleep(0.02)
    raise AssertionError("Durable fault status did not become invalid within the deadline")


def open_client(info, evidence):
    # Allocate first so a failed inherited handshake can still close every
    # partially initialized resource; the healthy Client stays unchanged.
    client = FaultClient.__new__(FaultClient)
    try:
        FaultClient.__init__(client, info, evidence)
        return client
    except BaseException:
        for resource in ("sock", "reader", "incoming", "outgoing"):
            handle = getattr(client, resource, None)
            if handle is not None:
                handle.close()
        raise


def reconnect(info, evidence, deadline):
    """A new socket may briefly be rejected while the old session is being removed."""
    attempt = 0
    while time.monotonic() < deadline:
        attempt += 1
        folder = evidence / ("reconnect-%02d" % attempt)
        folder.mkdir()
        try:
            # The new snapshot establishes a fresh sequence/tick baseline.
            return open_client(info, folder)
        except (OSError, AssertionError) as error:
            write_json(folder / "attachment-error.json", {"error": str(error)})
            # Retry only an explicit busy rejection, not corrupt observations.
            events = [json.loads(line) for line in (folder / "observed.ndjson").read_text(
                encoding="utf-8").splitlines()] if (folder / "observed.ndjson").is_file() else []
            if not any(row.get("type") == "error" and row.get("code") == "controllerBusy"
                       for row in events):
                raise
            time.sleep(0.02)
    raise AssertionError("Controller slot did not become available for reconnect")


def exercise_fault(player_command, root, phase, report, startup_timeout, shutdown_timeout):
    directory = root / (phase + "-run")
    evidence = root / (phase + "-evidence")
    evidence.mkdir()
    first_evidence = evidence / "initial"
    first_evidence.mkdir()
    profiles = directory / "profiles"
    command = [*player_command, "--validation-dir", str(directory),
               "-logFile", str(root / (phase + "-player.log"))]
    run = {"phase": phase, "command": command, "validationDirectory": str(directory),
           "evidenceDirectory": str(evidence), "checks": [], "expectedCaptureValid": False,
           "processExited": False}
    report["runs"].append(run)
    write_json(evidence / "launch.json", run)
    process, client = None, None
    try:
        with (root / (phase + "-process.log")).open("wb") as output:
            process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT)
        run["pid"] = process.pid
        info = wait_connection(process, directory, startup_timeout)
        run["runId"] = info["runId"]
        client = open_client(info, first_evidence)
        initial_identity = client.identity
        assert client.state["captureValid"] is True and client.state["activeSlot"] == -1
        created, frame = client.applied("profile.create", {"slot": 0, "language": "en"})
        profile_id = client.action_state(created["commandId"])["savedProfile"]["id"]
        assert json.loads((profiles / "profile-0.json").read_text(
            encoding="utf-8-sig"))["id"] == profile_id
        saved_hashes = synthetic_hashes(profiles)
        run.update(profileId=profile_id, originalOutcome=created, profileHashes=saved_hashes)
        client.applied("ui.navigate", {"screen": "sandbox"})
        held, _ = client.applied("input.set", inputs(steering=1, drift=True, duration=150))
        held_state = client.action_state(held["commandId"])
        assert held_state["input"]["steering"] == 1 and held_state["input"]["drift"] is True
        before_sequence, before_tick = client.last_sequence, client.state["tick"]
        run["checks"].append("persisted synthetic profile and active held input before fault")

        errors = {"streamError"} if phase == "stalled" else (
            {"controllerDisconnected"} if phase == "disconnect" else
            {"controllerDisconnected", "streamError"})
        started = time.monotonic()
        if phase == "stalled":
            client.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024)
            run["receiveBufferBytes"] = client.sock.getsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF)
            # Legal frames only: 32 requests, each below the real 16 KiB limit.
            # Do not read again until durable failure has been observed.
            for index in range(32):
                identity = "stall-%02d-" % index + "x" * (16000 - len("stall-%02d-" % index))
                request = {"type": "lookup", "commandId": identity}
                assert len(json.dumps(request).encode("ascii")) < 16 * 1024
                client.send(request)
            run["stalledLoad"] = {"lookupFrames": 32, "identityBytes": 16000}
        else:
            if phase == "reset":
                linger = struct.pack("HH" if sys.platform == "win32" else "ii", 1, 0)
                client.sock.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, linger)
            else:
                client.sock.shutdown(socket.SHUT_RDWR)
            client.close()
            client = None

        status = wait_invalid_status(process, directory, errors, started + 8)
        run["faultDetectionSeconds"] = time.monotonic() - started
        run["statusBeforeReconnect"] = status
        run["checks"].append("independent durable invalid/incomplete status before finish or teardown")
        if client is not None:
            client.close()
            client = None
        client = reconnect(info, evidence, time.monotonic() + 10)
        snapshot = client.first_snapshot
        assert client.identity == initial_identity, "Reconnect changed run/build/device identity"
        assert snapshot["sequence"] > before_sequence and snapshot["tick"] > before_tick
        validate_resume(snapshot["state"], profile_id, errors)
        if phase != "stalled":
            assert snapshot["tick"] < held["tick"] + 150, \
                "Input expired before reconnect; this cannot prove disconnect cleanup"
        assert synthetic_hashes(profiles) == saved_hashes
        run["reconnectSnapshot"] = snapshot
        run["checks"].append("fresh same-run snapshot, progressing ticks and neutral input without revalidation")
        # The new snapshot has passed the single writer, so old queued trace
        # items have drained. Preserve and compare the exact prefix bytes.
        prefix = verify_failed_prefix(directory / "state.ndjson", info)
        client.send({"type": "lookup", "commandId": created["commandId"]})
        lookup = client.wait(lambda event: event.get("type") == "lookup"
                             and event.get("commandId") == created["commandId"])
        assert lookup["found"] is True
        validate_outcome(created, lookup["result"], duplicate=False)
        run["lookupOutcome"] = lookup
        run["checks"].append("lookup returns original terminal outcome and execution tick")
        client.send(frame)
        duplicate = client.wait(lambda event: event.get("type") == "result"
                                and event.get("commandId") == created["commandId"])
        validate_outcome(created, duplicate, duplicate=True)
        validate_resume(client.action_state(created["commandId"]), profile_id, errors)
        assert synthetic_hashes(profiles) == saved_hashes, "Duplicate rewrote a synthetic save"
        run["duplicateOutcome"] = duplicate
        run["checks"].append("identical redelivery is duplicate with unchanged synthetic save-file hashes")
        client.send({"type": "health"})
        health = client.wait(lambda event: event.get("type") == "health")
        assert health["captureValid"] is False and health["captureError"] in errors
        client.send({"type": "finish"})
        finished = client.wait(lambda event: event.get("type") == "finished", timeout=8)
        assert finished["captureValid"] is False and finished["captureError"] in errors
        final_status = wait_invalid_status(process, directory, errors, time.monotonic() + 2)
        after_prefix = verify_failed_prefix(directory / "state.ndjson", info)
        assert after_prefix == prefix, "Post-fault observation/commands/finish changed trace prefix"
        assert synthetic_hashes(profiles) == saved_hashes
        run.update(prefix=prefix, finished=finished, finalStatus=final_status,
                   captureValid=final_status["captureValid"],
                   expectedFaultVerified=True)
        run["checks"].append("finish remains invalid/incomplete and preserves exact trace prefix")
        report["checks"].extend(phase + ": " + check for check in run["checks"])
    finally:
        try:
            if client is not None:
                client.close()
        finally:
            if process is not None:
                if process.poll() is None:
                    run["shutdownMethod"] = "terminate owned expected-invalid validation player"
                    process.terminate()
                    try:
                        process.wait(timeout=shutdown_timeout)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=shutdown_timeout)
                        run["shutdownMethod"] = "kill owned player after termination timeout"
                run["exitCode"] = process.poll()
                run["processExited"] = run["exitCode"] is not None
            write_json(evidence / "process.json", run)


def run_scenario(player_command, evidence, startup_timeout=60, shutdown_timeout=10):
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed", "scope": "external Windows validation-controller expected faults",
        "captureCertification": "none: all fault-injected captures must remain invalid/incomplete",
        "runs": [], "checks": [],
        "notVerified": ["Android device/ADB behavior", "physical input/UI parity",
                        "human Hebrew/audio/visual acceptance", "ordinary profile storage",
                        "graceful-exit autosave", "LAN gameplay or racing features",
                        "stalled finish request on the original non-reading controller"],
    }
    try:
        if not __debug__:
            raise RuntimeError("Run without -O: scenario assertions must be enabled")
        assert startup_timeout > 0 and shutdown_timeout > 0
        executable = Path(player_command[0]).resolve(strict=True)
        player_command = [str(executable), *player_command[1:]]
        report["buildFiles"] = build_fingerprint(executable)
        tool_paths = [Path(__file__), Path(__file__).with_name("AgentScenario.py"),
                      Path(__file__).with_name("RestartScenario.py")]
        report["toolHashes"] = {path.name: file_hash(path) for path in tool_paths}
        for phase in ("disconnect", "reset", "stalled"):
            exercise_fault(player_command, root, phase, report, startup_timeout, shutdown_timeout)
        assert build_fingerprint(executable) == report["buildFiles"], "Build changed during qualification"
        assert {path.name: file_hash(path) for path in tool_paths} == report["toolHashes"], \
            "Scenario source changed during qualification"
        assert all(run["processExited"] and run.get("expectedFaultVerified")
                   for run in report["runs"])
        report["status"] = "passed"
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)
    finally:
        write_json(root / "result.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("player", type=Path)
    parser.add_argument("--evidence", type=Path, required=True,
                        help="NEW directory; existing paths are never overwritten")
    args = parser.parse_args()
    try:
        report = run_scenario([str(args.player)], args.evidence)
    except FileExistsError:
        print("Evidence directory already exists; choose a fresh path", file=sys.stderr)
        return 1
    print(json.dumps({"status": report["status"], "checks": report["checks"],
                      "error": report.get("error"), "evidence": str(args.evidence)}, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
