"""Lead-authored foundation scenario against a running validation build.

Uses only Python's standard library. This verifies actual command/state behavior,
not visual quality, physical input, or the unimplemented racing features.
"""

import argparse
import json
import math
import socket
import sys
import time
import uuid
from collections import deque
from pathlib import Path


SEQUENCED_TYPES = {"snapshot", "state", "result", "discovery", "lookup", "health", "finished"}


def validate_presentation(state, language):
    """Check the runtime's bilingual configuration at a command boundary."""
    assert state["language"] == language, "Locale command was not applied"
    presentation = state["presentation"]
    assert presentation["hebrewFontAvailable"] is True, "Packaged bilingual font is unavailable"
    assert presentation["fontWarning"] == "", presentation["fontWarning"]
    assert presentation["textDirection"] == ("rtl" if language == "he" else "ltr"), \
        "Text direction does not match the applied language"


def validate_record(event, info, previous_sequence, previous_time):
    assert type(event.get("protocolVersion")) is int and event["protocolVersion"] == 1
    assert event.get("runId") == info["runId"], "Wrong run identity"
    if event.get("type") not in SEQUENCED_TYPES:
        assert event.get("type") in ("hello", "error"), "Unknown record type"
        return previous_sequence, previous_time
    for key in ("deviceId", "participantId", "buildVersion", "contentVersion"):
        assert isinstance(event.get(key), str) and event[key], "Missing record identity: " + key
    assert event["participantId"] == "local"
    sequence = event.get("sequence")
    assert type(sequence) is int and sequence > 0, "Missing/invalid stream sequence"
    if previous_sequence is not None:
        assert sequence == previous_sequence + 1, "Missing/out-of-order records"
    now = event.get("monotonicMs")
    assert type(now) in (int, float) and math.isfinite(now) and now >= 0, "Invalid stream time"
    assert previous_time is None or now >= previous_time, "Time moved backwards"
    assert type(event.get("tick")) is int and event["tick"] >= 0, "Missing observation tick"
    return sequence, now


def validate_state_tick(event, previous_tick):
    if event.get("type") not in ("state", "snapshot"):
        return previous_tick
    tick = event["state"].get("tick")
    assert type(tick) is int and tick == event["tick"], "State/record tick mismatch"
    if previous_tick is not None:
        assert previous_tick <= tick <= previous_tick + 1, "Missing state boundary or tick rollback"
    return tick


def verify_trace(directory, info):
    trace = directory / "state.ndjson"
    assert trace.stat().st_size <= 256 * 1024 * 1024, "Trace exceeded configured storage bound"
    sequence, timestamp = None, None
    state_tick = None
    first = True
    last = None
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            assert line.endswith("\n"), "Incomplete trace record"
            assert info["token"] not in line, "Credential leaked to trace"
            event = json.loads(line)
            if first:
                assert event.get("type") == "snapshot" and event.get("sequence") == 1
                first = False
            sequence, timestamp = validate_record(event, info, sequence, timestamp)
            state_tick = validate_state_tick(event, state_tick)
            if event.get("type") in ("state", "snapshot"):
                assert event["state"]["captureValid"], event["state"].get("captureError")
            last = event
    assert last and last["type"] == "finished" and last["captureValid"], "Trace did not finish"
    status = json.loads((directory / "capture-status.json").read_text(encoding="utf-8-sig"))
    assert status["captureValid"] and status["captureComplete"], "Capture not durably completed"


class Client:
    def __init__(self, connection, evidence):
        self.info = connection
        self.evidence = evidence
        self.events = deque(maxlen=2048)
        self.last_sequence = None
        self.last_time = None
        self.last_state_tick = None
        self.state = None
        self.sock = socket.create_connection(("127.0.0.1", connection["port"]), timeout=10)
        self.reader = self.sock.makefile("rb")
        self.incoming = (evidence / "observed.ndjson").open("x", encoding="utf-8")
        self.outgoing = (evidence / "commands.ndjson").open("x", encoding="utf-8")
        self.send({
            "type": "hello", "protocolVersion": 1,
            "runId": connection["runId"], "token": connection["token"],
        })
        self.wait(lambda event: event.get("type") == "hello")
        self.wait(lambda event: event.get("type") == "snapshot")

    def send(self, frame):
        logged = dict(frame)
        if "token" in logged:
            logged["token"] = "<redacted>"
        self.outgoing.write(json.dumps(logged, ensure_ascii=False) + "\n")
        self.outgoing.flush()
        self.sock.sendall((json.dumps(frame) + "\n").encode("utf-8"))

    def read(self, remaining):
        self.sock.settimeout(max(0.01, remaining))
        line = self.reader.readline(1024 * 1024 + 1)
        if not line:
            raise AssertionError("Connection closed before the expected outcome")
        if len(line) > 1024 * 1024 or not line.endswith(b"\n"):
            raise AssertionError("Oversized or truncated server frame")
        event = json.loads(line)
        self.last_sequence, self.last_time = validate_record(
            event, self.info, self.last_sequence, self.last_time)
        self.last_state_tick = validate_state_tick(event, self.last_state_tick)
        assert self.info["token"] not in line.decode("utf-8"), "Credential leaked"
        if event.get("type") in ("snapshot", "state"):
            self.state = event["state"]
            assert self.state["captureValid"], self.state.get("captureError")
            assert len(self.state["slots"]) == 4, "Incomplete profile state"
        self.incoming.write(json.dumps(event, ensure_ascii=False) + "\n")
        self.incoming.flush()
        self.events.append(event)
        return event

    def wait(self, predicate, timeout=20):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            event = self.read(deadline - time.monotonic())
            if predicate(event):
                return event
        raise AssertionError("Timed out waiting for a recorded outcome")

    def command(self, action, parameters=None, command_id=None):
        command_id = command_id or str(uuid.uuid4())
        frame = {
            "type": "command", "protocolVersion": 1,
            "runId": self.info["runId"], "commandId": command_id,
            "participantId": "local", "action": action,
            "parameters": {key: str(value).lower() if isinstance(value, bool)
                           else str(value) for key, value in (parameters or {}).items()},
        }
        self.send(frame)
        result = self.wait(lambda event: event.get("type") == "result"
                           and event.get("commandId") == command_id)
        return result, frame

    def applied(self, action, parameters=None):
        result, frame = self.command(action, parameters)
        assert result["status"] == "applied", (action, result)
        return result, frame

    def action_state(self, command_id):
        for event in reversed(self.events):
            if event.get("type") == "state" and event.get("commandId") == command_id:
                return event["state"]
        raise AssertionError("Missing command-correlated action-boundary state")

    def close(self):
        self.reader.close()
        self.sock.close()
        self.incoming.close()
        self.outgoing.close()


def settings(language="he", camera="chase"):
    return {
        "language": language, "camera": camera, "reducedCameraShake": True,
        "musicVolume": 0.25, "effectsVolume": 0.5, "voiceVolume": 0.75,
    }


def inputs(steering=0, duration=10, drift=False, boost=False, gadget=False,
           recover=False, change_camera=False):
    return {
        "steering": steering, "drift": drift, "boost": boost, "gadget": gadget,
        "recover": recover, "changeCamera": change_camera, "pause": False,
        "durationTicks": duration,
    }


def run(connection, evidence):
    client = Client(connection, evidence)
    checks = []
    try:
        assert client.state["activeSlot"] == -1 and client.state["screen"] == "profiles"
        assert all(slot["status"] == "Missing" for slot in client.state["slots"])
        checks.append("isolated fresh profiles")
        client.send({"type": "discover"})
        discovery = client.wait(lambda event: event.get("type") == "discovery")
        actions = {entry["action"] for entry in discovery["commands"]}
        assert actions == {
            "profile.create", "profile.select", "ui.navigate", "ui.language",
            "settings.update", "settings.save", "input.set", "input.clear",
            "camera.toggle", "pause.toggle",
        }, actions
        assert discovery["featureCoverage"], "Missing future-feature coverage gaps"
        checks.append("command discovery")

        for language in ("he", "en"):
            localized, _ = client.applied("ui.language", {"language": language})
            validate_presentation(client.action_state(localized["commandId"]), language)
        checks.append("packaged bilingual fonts and locale command boundaries")

        created, _ = client.applied("profile.create", {"slot": 0, "language": "en"})
        state = client.action_state(created["commandId"])
        profile_id = state["savedProfile"]["id"]
        assert uuid.UUID(profile_id)
        assert state["screen"] == "settings" and state["savedProfile"]["coins"] == 0
        save_path = Path(connection["_directory"]) / "profiles" / "profile-0.json"
        assert json.loads(save_path.read_text(encoding="utf-8-sig"))["id"] == profile_id
        checks.append("real profile creation and persistence")

        draft, _ = client.applied("settings.update", settings())
        state = client.action_state(draft["commandId"])
        assert state["pendingSettings"] and state["draftProfile"]["language"] == "he"
        assert state["savedProfile"]["language"] == "en"
        assert json.loads(save_path.read_text(encoding="utf-8-sig"))["language"] == "en"
        saved, frame = client.applied("settings.save")
        state = client.action_state(saved["commandId"])
        assert not state["pendingSettings"] and state["savedProfile"]["language"] == "he"
        assert state["savedProfile"]["musicVolume"] == 0.25
        original = save_path.read_bytes()
        client.send(frame)
        duplicate = client.wait(lambda event: event.get("type") == "result"
                                and event.get("commandId") == saved["commandId"])
        assert duplicate["status"] == "applied" and duplicate["duplicate"]
        assert duplicate["tick"] == saved["tick"], "Duplicate acquired a new execution tick"
        assert save_path.read_bytes() == original
        client.send({"type": "lookup", "commandId": saved["commandId"]})
        lookup = client.wait(lambda event: event.get("type") == "lookup")
        assert lookup["found"] and lookup["result"]["status"] == "applied"
        checks.append("draft/save separation, duplicate protection, outcome lookup")

        conflict, _ = client.command("camera.toggle", command_id=saved["commandId"])
        assert conflict["status"] == "rejected" and conflict["code"] == "identityConflict"
        invalid = settings()
        invalid["musicVolume"] = 1.1
        rejected, _ = client.command("settings.update", invalid)
        assert rejected["status"] == "rejected"
        assert save_path.read_bytes() == original
        checks.append("conflicting identity and invalid settings do not persist")

        client.applied("profile.create", {"slot": 1, "language": "he"})
        selected, _ = client.applied("profile.select", {"slot": 0})
        state = client.action_state(selected["commandId"])
        assert state["savedProfile"]["id"] == profile_id
        assert state["savedProfile"]["language"] == "he"
        checks.append("profile switching preserves identity and settings")

        client.applied("ui.navigate", {"screen": "sandbox"})
        drive, _ = client.applied("input.set", inputs(
            steering=1, drift=True, boost=True, gadget=True, recover=True,
            change_camera=True))
        state = client.action_state(drive["commandId"])
        assert state["input"]["steering"] == 1 and state["input"]["drift"]
        assert state["input"]["boost"] and state["input"]["gadget"]
        assert state["input"]["recover"] and state["input"]["changeCamera"]
        assert state["draftProfile"]["camera"] == "overhead"
        client.wait(lambda event: event.get("type") == "state"
                    and event["state"]["tick"] >= drive["tick"] + 10
                    and event["state"]["input"]["steering"] == 0)
        checks.append("held controls, transient pulses, camera, bounded expiry")

        toggled, _ = client.applied("camera.toggle")
        assert client.action_state(toggled["commandId"])["draftProfile"]["camera"] == "chase"
        checks.append("camera toggle command updates selected profile draft")

        paused, _ = client.applied("pause.toggle")
        assert client.action_state(paused["commandId"])["paused"]
        held, _ = client.applied("input.set", inputs(steering=-1, boost=True, duration=2))
        client.wait(lambda event: event.get("type") == "state"
                    and event["state"]["paused"]
                    and event["state"]["tick"] >= held["tick"] + 2
                    and event["state"]["input"]["steering"] == 0)
        client.applied("pause.toggle")
        client.applied("input.clear")
        checks.append("paused observation and input cleanup")

        unsupported, _ = client.command("race.start")
        assert unsupported["status"] == "rejected"
        client.send({"type": "health"})
        health = client.wait(lambda event: event.get("type") == "health")
        assert health["captureValid"], health
        client.send({"type": "finish"})
        finished = client.wait(lambda event: event.get("type") == "finished")
        assert finished["captureValid"], finished
        verify_trace(Path(connection["_directory"]), connection)
        checks.append("unimplemented features rejected and capture finished cleanly")
        return checks
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection_file", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed", "scope": "foundation command/state integration"}
    try:
        connection = json.loads(args.connection_file.read_text(encoding="utf-8-sig"))
        assert connection["protocolVersion"] == 1
        connection["_directory"] = str(args.connection_file.resolve().parent)
        result["checks"] = run(connection, args.evidence)
        result["status"] = "passed"
        result["runId"] = connection["runId"]
        result["notVerified"] = [
            "physical keyboard/touch input and UI parity", "visual/Hebrew/audio quality",
            "restart/reconnect/failure injection scenarios", "Android device behavior",
            "race simulation, campaign, rewards/upgrades/cosmetics, LAN and content",
        ]
    except Exception as error:
        result["error"] = str(error)
    (args.evidence / "result.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
