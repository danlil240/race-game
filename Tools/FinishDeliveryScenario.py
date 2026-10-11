"""Expected-fault qualification: a finish frame from a stalled non-reading controller.

The original controller shrinks its receive buffer, fills the server-to-client
pipe with legal lookup replies, then sends ``{"type":"finish"}`` without ever
reading again. Passing this scenario means the player detected the stalled
sink: a durable terminal invalid capture status, a game thread responsive
enough to reject a fresh controller with ``captureClosed``, and a trace prefix
that never claims a valid finish. The capture must remain INVALID -- this
scenario certifies correct fault detection, not capture completeness.
"""

import argparse
import json
import socket
import subprocess
import sys
import time
from pathlib import Path

from AgentScenario import SEQUENCED_TYPES, validate_record, validate_state_tick
from RestartScenario import build_fingerprint, file_hash, wait_connection, write_json
from StreamRobustnessScenario import (
    open_client, validate_invalid_status, wait_invalid_status)


# Explicit terminal-code contract for this fault (decided from
# StateStream.RequestFinish/SendFinished and ValidationBridge.AcceptLoop):
# the finish work item is queued behind backed-up lookup replies, so the
# writer's first stalled socket write fails under
# ControllerSession.SendTimeoutMs and Invalidate stamps "streamError" --
# SendFinished itself reports the same code when it cannot deliver.
# "outgoingQueueFull" cannot occur (the bounded queue holds 256 items; this
# run enqueues fewer than 40) and "controllerDisconnected" cannot be the
# first failure because the client socket stays connected -- it merely stops
# reading. The set is therefore exactly {"streamError"}.
TERMINAL_ERRORS = frozenset({"streamError"})

LOOKUP_FRAMES = 32
LOOKUP_IDENTITY_BYTES = 16000  # below the real 16 KiB frame limit
RECEIVE_BUFFER = 1024
SEND_TIMEOUT = 15  # bound on every client-side send, including finish


def verify_stalled_trace(trace, info):
    """Validate the frozen trace prefix of a finish-delivery failure.

    Like verify_failed_prefix, but a traced ``finished`` record is legal here:
    when RequestFinish ran before the writer's invalidation it carries
    traceWrite=true and is patched at write time, so it may appear -- only as
    the last record and only while admitting captureValid:false. A finished
    record that claims validity is the exact false-complete this scenario
    exists to catch.
    """
    assert 0 < trace.stat().st_size <= 256 * 1024 * 1024
    sequence, timestamp, tick = None, None, None
    count = 0
    identity = None
    finished_seen = False
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            assert line.endswith("\n"), "Truncated trace prefix"
            assert info["token"] not in line, "Credential leaked to trace"
            event = json.loads(line)
            assert event.get("type") in SEQUENCED_TYPES, "Unsequenced reply in trace prefix"
            assert not finished_seen, "Records continued after a traced finish"
            if event.get("type") == "finished":
                assert event.get("captureValid") is False, \
                    "Finished record claims a valid capture"
                assert type(event.get("captureError")) is str and event["captureError"], \
                    "Invalid finished record carries no failure code"
                finished_seen = True
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
    return {"records": count, "lastSequence": sequence, "finishedTraced": finished_seen,
            "bytes": trace.stat().st_size, "sha256": file_hash(trace)}


def read_rejection(port, run_id, timeout=3):
    """Read exactly one rejection frame; the server must then half-close."""
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=timeout) as sock:
            sock.settimeout(timeout)
            data = b""
            while b"\n" not in data and len(data) <= 4096:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                data += chunk
            assert data.endswith(b"\n"), "Rejection frame was not delivered"
            frame = json.loads(data)
            assert frame.get("type") == "error" and frame.get("protocolVersion") == 1, \
                "Unexpected post-finish accept reply"
            assert frame.get("runId") == run_id, \
                "Rejection carries the wrong run identity"
            # RejectAndClose drains unread bytes, then Shutdown(Send) and
            # closes: an immediate EOF proves the game loop is alive and
            # serving accepts.
            assert sock.recv(1) == b"", "Rejected socket was not closed by the server"
            return frame["code"]
    except socket.timeout as error:
        raise AssertionError("Rejection probe timed out") from error


def wait_capture_closed(info, process, deadline, run):
    """Probe fresh connections until the listener rejects them captureClosed."""
    probes = []
    run["rejectionProbes"] = probes
    while time.monotonic() < deadline:
        assert process.poll() is None, "Player exited before rejection evidence"
        try:
            code = read_rejection(info["port"], info["runId"])
        except (OSError, AssertionError) as error:
            code = "probeError:" + str(error)
        probes.append(code)
        if code == "captureClosed":
            return {"code": code, "attempts": len(probes)}
        # controllerBusy is legal only while the doomed session is still being
        # reaped; anything else (e.g. an attached hello) means the finish was
        # dropped or the accept loop misbehaved -- fail after the deadline with
        # the full probe history as evidence.
        assert code == "controllerBusy" or code.startswith("probeError:"), \
            "Unexpected post-finish accept behavior: " + code
        time.sleep(0.1)
    raise AssertionError(
        "Post-finish controllers were not rejected captureClosed: " + repr(probes))


def wait_frozen_trace(trace, process, deadline, quiet=0.5):
    """Wait until the trace byte content stops changing; finish ends all emits."""
    last = None
    stable_since = time.monotonic()
    while time.monotonic() < deadline:
        assert process.poll() is None, "Player exited while the trace was still open"
        try:
            signature = (trace.stat().st_size, file_hash(trace))
        except OSError:
            signature = None
        if signature != last:
            last = signature
            stable_since = time.monotonic()
        elif signature is not None and time.monotonic() - stable_since >= quiet:
            return {"bytes": signature[0], "sha256": signature[1]}
        time.sleep(0.05)
    raise AssertionError("Trace did not stop changing after the stalled finish")


def exercise_stalled_finish(player_command, root, report, startup_timeout,
                            shutdown_timeout, fault_timeout):
    phase = "stalled-finish"
    directory = root / (phase + "-run")
    evidence = root / (phase + "-evidence")
    evidence.mkdir()
    first_evidence = evidence / "initial"
    first_evidence.mkdir()
    command = [*player_command, "--validation-dir", str(directory),
               "-logFile", str(root / (phase + "-player.log"))]
    run = {"phase": phase, "command": command, "validationDirectory": str(directory),
           "evidenceDirectory": str(evidence), "checks": [],
           "expectedCaptureValid": False, "processExited": False}
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
        assert client.state["captureValid"] is True and client.state["activeSlot"] == -1
        run["checks"].append("valid capture with an authenticated attached controller")

        # Stall the sink: tiny receive buffer, read only the handshake
        # snapshot, then never read again -- even while requesting finish.
        client.sock.settimeout(SEND_TIMEOUT)
        client.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, RECEIVE_BUFFER)
        run["receiveBufferBytes"] = client.sock.getsockopt(
            socket.SOL_SOCKET, socket.SO_RCVBUF)
        # Legal frames only: 32 requests, each below the real 16 KiB limit.
        for index in range(LOOKUP_FRAMES):
            identity = ("stall-%02d-" % index
                        + "x" * (LOOKUP_IDENTITY_BYTES - len("stall-%02d-" % index)))
            request = {"type": "lookup", "commandId": identity}
            assert len(json.dumps(request).encode("ascii")) < 16 * 1024
            client.send(request)
        run["stalledLoad"] = {"lookupFrames": LOOKUP_FRAMES,
                              "identityBytes": LOOKUP_IDENTITY_BYTES}
        # The finish frame must be queued while the session is still attached
        # and alive; the send itself is bounded by the socket timeout, never
        # by the dead downstream pipe.
        started = time.monotonic()
        client.send({"type": "finish"})
        run["finishSendSeconds"] = time.monotonic() - started
        run["checks"].append(
            "32 legal lookup frames then the finish frame sent on the stalled socket")

        status = wait_invalid_status(process, directory, TERMINAL_ERRORS,
                                     started + fault_timeout)
        run["faultDetectionSeconds"] = time.monotonic() - started
        run["terminalStatus"] = status
        run["checks"].append(
            "durable terminal status: captureValid/captureComplete false, "
            "captureError streamError")

        rejection = wait_capture_closed(info, process,
                                        time.monotonic() + fault_timeout, run)
        run["rejection"] = rejection
        run["checks"].append(
            "fresh controllers rejected captureClosed: the game thread never "
            "blocked on the stalled finish write")

        frozen = wait_frozen_trace(directory / "state.ndjson", process,
                                   time.monotonic() + fault_timeout)
        run["frozenTrace"] = frozen
        prefix = verify_stalled_trace(directory / "state.ndjson", info)
        assert prefix["bytes"] == frozen["bytes"] and prefix["sha256"] == frozen["sha256"], \
            "Trace changed after it was observed frozen"
        run["prefix"] = prefix
        assert process.poll() is None, "Player died while handling the stalled finish"
        run["checks"].append(
            "trace prefix complete, contiguous, credential-free; no finished "
            "record claims validity")
        run["checks"].append("player stayed alive through fault detection and rejection")
        run["expectedFaultVerified"] = True
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


def run_scenario(player_command, evidence, startup_timeout=60, shutdown_timeout=10,
                 fault_timeout=15):
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "external Windows validation controller stalled during finish",
        "captureCertification":
            "none: the fault-injected capture must remain invalid/incomplete",
        "terminalErrors": sorted(TERMINAL_ERRORS),
        "runs": [], "checks": [],
        "notVerified": ["Android device/ADB behavior", "physical input/UI parity",
                        "human Hebrew/audio/visual acceptance",
                        "graceful reconnected-reader finish (covered by "
                        "StreamRobustnessScenario)", "LAN gameplay or racing features"],
    }
    try:
        if not __debug__:
            raise RuntimeError("Run without -O: scenario assertions must be enabled")
        assert startup_timeout > 0 and shutdown_timeout > 0 and fault_timeout > 0
        executable = Path(player_command[0]).resolve(strict=True)
        player_command = [str(executable), *player_command[1:]]
        report["buildFiles"] = build_fingerprint(executable)
        tool_paths = [Path(__file__), Path(__file__).with_name("AgentScenario.py"),
                      Path(__file__).with_name("RestartScenario.py"),
                      Path(__file__).with_name("StreamRobustnessScenario.py")]
        report["toolHashes"] = {path.name: file_hash(path) for path in tool_paths}
        exercise_stalled_finish(player_command, root, report,
                                startup_timeout, shutdown_timeout, fault_timeout)
        assert build_fingerprint(executable) == report["buildFiles"], \
            "Build changed during qualification"
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
    parser.add_argument("--startup-timeout", type=float, default=60)
    parser.add_argument("--shutdown-timeout", type=float, default=10)
    parser.add_argument("--fault-timeout", type=float, default=15,
                        help="bound on fault detection, rejection and trace freeze")
    args = parser.parse_args()
    try:
        report = run_scenario([str(args.player)], args.evidence,
                              startup_timeout=args.startup_timeout,
                              shutdown_timeout=args.shutdown_timeout,
                              fault_timeout=args.fault_timeout)
    except (FileExistsError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps({"status": report["status"], "checks": report["checks"],
                      "error": report.get("error"), "evidence": str(args.evidence)},
                     indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
