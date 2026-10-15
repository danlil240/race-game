"""Lead-authored foundation scenario against a running validation build.

Uses only Python's standard library. This verifies actual command/state behavior,
not visual quality, physical input, or the unimplemented racing features.
"""

import argparse
import copy
import json
import math
import queue
import select
import socket
import subprocess
import sys
import threading
import time
import uuid
from collections import deque
from pathlib import Path


SEQUENCED_TYPES = {"snapshot", "state", "delta", "result", "discovery",
                   "lookup", "health", "screenshot", "finished"}

# State-carrying boundary records: snapshot/state embed the full state;
# delta names the boundary it extends plus changed/removed fields.
BOUNDARY_TYPES = {"snapshot", "state", "delta"}

# Schemas this consumer can interpret. Records without the field are
# treated as schema 1 (pre-driving baseline); anything outside the
# supported set must be rejected before driving evidence is trusted.
# Schema 3 adds the LAN session/race/prediction/interpolation slices.
# Schema 4 adds the protocol-2 physical surfaces: discovered hosts and
# session descriptors carry the tuned-content fingerprint, and LAN race
# snapshots may carry per-racer physical DTOs.
# Schema 5 (M4) is additive presentation truth: the applied render
# profile and a bounded render-performance summary on `presentation` —
# bounded scalars only, never frame-sized buffers.
# Schema 6 (M5 Task 10) adds the local solo-race slice (`soloRace`) and
# the resolved progression surface (`progression`: campaign access,
# bounty tray, unresolved-reward gate) — both present from the first
# boundary, menus and failed saves included.
SUPPORTED_STATE_SCHEMAS = {1, 2, 3, 4, 5, 6}


def _remove_path(state, path):
    """Apply one delta `removed` dot-path; unresolved paths are corrupt."""
    assert type(path) is str and path, "Removed path must be a non-empty string"
    segments = path.split(".")
    node = state
    for segment in segments[:-1]:
        assert isinstance(node, dict), "Removed path crosses a non-object"
        node = node.get(segment)
    assert isinstance(node, dict) and segments[-1] in node, \
        "Removed path does not resolve: " + path
    del node[segments[-1]]


def _merge_changed(state, changed):
    """Recursive object merge: object values merge, everything else
    (scalars, arrays, null) replaces wholesale."""
    for key, value in changed.items():
        current = state.get(key)
        if isinstance(value, dict) and isinstance(current, dict):
            _merge_changed(current, value)
        else:
            state[key] = value


class StateReconstructor:
    """Independent snapshot+delta consumer for the schema-2 stream.

    Rebuilds the complete application state at every boundary without
    sharing any producer diff logic: `snapshot`/`state` records replace
    the baseline; `delta` records must name the applied boundary
    sequence in `base` and carry a nested `changed` merge object plus a
    `removed` list of dot-paths. A delta record is never silently read
    as a full state; a corrupt/missing base or unresolved path fails
    reconstruction. A fresh snapshot restores observation after a gap
    but cannot repair missing history: contiguous sequence validation
    still owns that check.
    """

    def __init__(self):
        self._baseline = None
        self.base_sequence = None

    def apply(self, event):
        """Apply one sequenced record; return the reconstructed boundary
        state dict, or None for non-boundary records.

        The returned dict is a fresh copy: caller mutations can never
        corrupt the internal baseline that later deltas extend."""
        kind = event.get("type")
        if kind == "snapshot" or kind == "state":
            state = event.get("state")
            assert isinstance(state, dict), "Boundary record without a full state"
            self._baseline = copy.deepcopy(state)
            self.base_sequence = event["sequence"]
            return copy.deepcopy(self._baseline)
        if kind == "delta":
            assert self._baseline is not None, "Delta arrived before any baseline"
            base = event.get("base")
            assert type(base) is int and base == self.base_sequence, \
                "Delta base does not name the applied boundary"
            changed = event.get("changed")
            removed = event.get("removed", [])
            assert isinstance(changed, dict) and isinstance(removed, list), \
                "Malformed delta payload"
            merged = copy.deepcopy(self._baseline)
            for path in removed:
                _remove_path(merged, path)
            _merge_changed(merged, changed)
            self._baseline = merged
            self.base_sequence = event["sequence"]
            return copy.deepcopy(merged)
        return None


def validate_state_schema(event, state):
    """Reject boundary records carrying a schema this consumer cannot read.

    `state` is the reconstructed boundary state (for delta records, the
    result of StateReconstructor.apply) — a delta is never itself
    interpreted as a full state.
    """
    if event.get("type") not in BOUNDARY_TYPES:
        return
    schema = event.get("stateSchemaVersion", 1)
    assert type(schema) is int and schema in SUPPORTED_STATE_SCHEMAS, \
        "Unsupported state schema: " + repr(schema)
    if schema >= 2:
        assert "driving" in state and "drivingEvents" in state, \
            "Schema-2 state is missing the driving slice"
        assert state.get("stateSchemaVersion") == schema, \
            "Nested state schema marker disagrees with the record"
    if schema >= 3:
        # The network slices are part of the schema-3 contract: the
        # session view is always populated (idle/none without a session)
        # and the race/prediction/interpolation keys exist even when
        # null outside a LAN race.
        for key in ("network", "networkRace", "prediction",
                    "remotePresentation"):
            assert key in state, "Schema-3 state is missing " + key
        # Schema-4 adds the replication-link counter slice: always a
        # present key (null until a replication bridge exists) so a
        # consumer can tell transport starvation from rejected traffic.
        if schema >= 4:
            assert "networkLink" in state, \
                "Schema-4 state is missing networkLink"
        session = state["network"]
        assert isinstance(session, dict), "network must be an object"
        for key in ("role", "phase", "localParticipantId", "participants",
                    "paused", "pauseRequests", "lastError",
                    "pendingOperations", "discovered", "results"):
            assert key in session, "network slice is missing " + key
        # Credentials and transport identities must never be observable:
        # reject any session field that could carry reconnect material.
        forbidden = {"credential", "cred", "token", "password",
                     "storedCredential", "joinSecret"}
        def scan(node):
            if isinstance(node, dict):
                for name, value in node.items():
                    assert name not in forbidden, \
                        "Credential material leaked into state: " + name
                    scan(value)
            elif isinstance(node, list):
                for item in node:
                    scan(item)
        scan(session)
        # Predicted-vs-authoritative separation: a live LAN race never
        # writes the solo `driving` slice, and `prediction` is client-only
        # (the host's copy of the race is already authoritative).
        if state.get("networkRace") is not None:
            assert state.get("driving") is None, \
                "LAN race state must not masquerade as solo driving state"
        if state.get("prediction") is not None:
            assert session.get("role") == "client" \
                and state.get("networkRace") is not None, \
                "prediction exists only on a client inside a LAN race"
    if schema >= 4:
        # Protocol-2 compatibility surfaces: a live session descriptor
        # binds the canonical tuned-content fingerprint and every
        # discovered host carries its advertised compat token.
        descriptor = session.get("descriptor")
        if descriptor is not None:
            assert isinstance(descriptor.get("contentFingerprint"), str), \
                "Schema-4 descriptor is missing contentFingerprint"
        for host in session.get("discovered") or []:
            assert isinstance(host, dict) and "compat" in host, \
                "Schema-4 discovered host is missing compat"
    if schema >= 5:
        validate_schema5_presentation(state)
    if schema >= 6:
        validate_schema6_progression(state)


def validate_schema6_progression(state):
    """The schema-6 local-loop contract a boundary state must carry.

    Additive over schema 5: `soloRace` (null outside a local mode —
    campaign and quickRace populate `race`, timeTrial `trial`; a
    finalized run carries the immutable `result`) and `progression`,
    the resolved campaign/bounty/reward-gate view rebuilt from durable
    profile facts. Both keys exist on EVERY schema-6 boundary — a menu
    snapshot or a failed save must never omit them.
    """
    for key in ("soloRace", "progression"):
        assert key in state, "Schema-6 state is missing " + key
    solo = state["soloRace"]
    if solo is not None:
        assert isinstance(solo, dict), "soloRace must be an object or null"
        for key in ("mode", "raceId", "phase", "race", "trial",
                    "result", "tuningSource"):
            assert key in solo, "soloRace slice is missing " + key
        assert solo["mode"] in ("campaign", "quickRace", "timeTrial",
                                "practice"), \
            "Unknown solo mode " + repr(solo["mode"])
        # A running LAN race never publishes a local session either.
        assert state.get("networkRace") is None or solo is None, \
            "LAN race state must not masquerade as a local solo session"
    progression = state["progression"]
    assert isinstance(progression, dict), \
        "progression must be an object on every boundary"
    for key in ("available", "contentIdentity", "campaignEvents",
                "bounties", "rewardUnresolved", "rewardUnresolvedRaceId"):
        assert key in progression, "progression slice is missing " + key
    assert type(progression["available"]) is bool
    assert type(progression["rewardUnresolved"]) is bool
    assert isinstance(progression["campaignEvents"], list)
    assert isinstance(progression["bounties"], list)
    for row in progression["campaignEvents"]:
        assert isinstance(row, dict), "campaignEvents rows are objects"
        for key in ("eventId", "stars", "locked", "lockReason",
                    "completed", "objectiveMask"):
            assert key in row, "campaignEvents row missing " + key
    for row in progression["bounties"]:
        assert isinstance(row, dict), "bounties rows are objects"
        for key in ("assignmentId", "bountyId", "progress", "threshold",
                    "rewardCoins", "completed", "resolved"):
            assert key in row, "bounties row missing " + key


def validate_schema5_presentation(state):
    """The schema-5 presentation contract a boundary state must carry.

    Additive over schema 4: `presentation.renderProfileId` names the applied
    tablet/PC pipeline (never assumed — "none"/"missing" are honest
    states), `renderingAvailable` separates rendered-player evidence from
    headless captures, and `performance` is the bounded summary slice
    (null only when capture is disabled — key must exist either way).
    """
    presentation = state.get("presentation")
    assert isinstance(presentation, dict), \
        "Schema-5 state is missing the presentation slice"
    profile = presentation.get("renderProfileId")
    assert profile in ("tablet", "pc", "none", "missing"), \
        "Unknown renderProfileId " + repr(profile)
    assert type(presentation.get("renderingAvailable")) is bool, \
        "Schema-5 presentation is missing renderingAvailable"
    assert "performance" in presentation, \
        "Schema-5 presentation is missing the performance key"
    # Task-4 racer facts: a bounded list naming the presented visual
    # identity per racer (authored ids or honest fallback tokens) and the
    # pose source driving it — "authoritative", "predicted" or
    # "interpolated".
    racers = presentation.get("racers")
    assert isinstance(racers, list), \
        "Schema-5 presentation is missing the racers list"
    for racer in racers:
        assert isinstance(racer, dict), "racers entries must be objects"
        for key in ("participantId", "vehicleId", "vehicleVisualId",
                    "characterId", "poseSource"):
            assert isinstance(racer.get(key), str), \
                "racers entry missing " + key
        for key in ("slot", "colorIndex"):
            assert type(racer.get(key)) is int, \
                "racers entry missing int " + key
        assert racer["poseSource"] in (
            "authoritative", "predicted", "interpolated", ""), \
            "unknown poseSource " + repr(racer["poseSource"])
    perf = presentation["performance"]
    if perf is not None:
        assert isinstance(perf, dict), "performance must be an object or null"
        for key in ("profile", "captureValid", "frames", "racingSamples",
                    "reportStatus", "reportError", "gpuTimingStatus",
                    "processMemoryStatus", "processMemoryMetric",
                    "processMemorySource", "processMemoryUnits"):
            assert key in perf, "performance summary missing " + key
        assert perf["profile"] in ("tablet", "pc", "none", "missing"), \
            "performance.profile disagrees with known profiles"
        # Metric values stay absent (null) when unavailable — a null p95 is
        # honest; a zero would masquerade as measurement.
        for key in ("medianFrameMs", "p95FrameMs", "maxFrameMs"):
            value = perf.get(key)
            assert value is None or type(value) in (int, float), \
                "performance." + key + " must be a number or null"


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


def validate_state_tick(state, event_tick, previous_tick):
    """Boundary tick continuity on the reconstructed state."""
    tick = state.get("tick")
    assert type(tick) is int and tick == event_tick, "State/record tick mismatch"
    if previous_tick is not None:
        assert previous_tick <= tick <= previous_tick + 1, "Missing state boundary or tick rollback"
    return tick


def verify_trace(directory, info):
    trace = directory / "state.ndjson"
    # The bound is whatever the run configured: connection.json carries
    # traceLimitBytes; legacy descriptors without it are held to the
    # shared 256 MiB default.
    bound = int((info or {}).get("traceLimitBytes")
                or 256 * 1024 * 1024)
    assert trace.stat().st_size <= bound, \
        "Trace exceeded configured storage bound"
    sequence, timestamp = None, None
    state_tick = None
    first = True
    last = None
    reconstructor = StateReconstructor()
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            assert line.endswith("\n"), "Incomplete trace record"
            assert info["token"] not in line, "Credential leaked to trace"
            event = json.loads(line)
            if first:
                assert event.get("type") == "snapshot" and event.get("sequence") == 1
                first = False
            sequence, timestamp = validate_record(event, info, sequence, timestamp)
            boundary = reconstructor.apply(event)
            if boundary is not None:
                validate_state_schema(event, boundary)
                state_tick = validate_state_tick(boundary, event["tick"], state_tick)
                assert boundary["captureValid"], boundary.get("captureError")
            last = event
    assert last and last["type"] == "finished" and last["captureValid"], "Trace did not finish"
    status = json.loads((directory / "capture-status.json").read_text(encoding="utf-8-sig"))
    assert status["captureValid"] and status["captureComplete"], "Capture not durably completed"


# Multi-peer runs read endpoints sequentially: a wait on one client can
# leave another's socket unread long enough for its validation stream to
# back up and die. Every connected client registers here so any wait()
# drains the peers it is not blocked on, keeping every sink consumed.
_ACTIVE_CLIENTS = []


class Client:
    def __init__(self, connection, evidence):
        self.info = connection
        self.evidence = evidence
        self.events = deque(maxlen=2048)
        self.last_sequence = None
        self.last_time = None
        self.last_state_tick = None
        self.reconstructor = StateReconstructor()
        self.state = None
        self.action_states = {}
        self.sock = socket.create_connection(("127.0.0.1", connection["port"]), timeout=10)
        self.reader = self.sock.makefile("rb")
        # Command-latency accounting: every command frame sent is owed a
        # result inside the documented deadline (COMMANDS.md). A command
        # that outlives the deadline is stuck — overdue_commands() names
        # it so a stuck input fails instead of hiding behind resends.
        self.pending_commands = {}
        self.results = {}
        # The first non-EOF failure observed while draining this client
        # from another peer's wait — recorded, never swallowed silently.
        self.drain_error = None
        self._drained_terminal = False
        # A dedicated pump consumes this socket continuously and queues raw
        # frames: the player's stream writer fails honestly after a bounded
        # send timeout, so a scenario thread blocked waiting on ANOTHER
        # peer (e.g. a relaunching process mid-boot) must never let this
        # sink back up. read() pulls from the queue — validation and state
        # stay on the calling thread. The pump uses raw select+recv, never
        # a makefile readline: closing the socket from close() must always
        # unblock it, which a BufferedReader io-lock cannot guarantee.
        # Fault-observer subclasses set _uses_pump = False and read the
        # makefile directly — they exist to observe exactly the stalls the
        # pump exists to prevent.
        self._lines = queue.Queue()
        self._terminal = None
        self._closing = False
        self._pump = None
        if self._uses_pump:
            self._pump = threading.Thread(
                target=self._pump_lines, daemon=True,
                name="validation-pump-%s" % connection.get("port"))
            self._pump.start()
        self.incoming = (evidence / "observed.ndjson").open("x", encoding="utf-8")
        self.outgoing = (evidence / "commands.ndjson").open("x", encoding="utf-8")
        _ACTIVE_CLIENTS.append(self)
        self.send({
            "type": "hello", "protocolVersion": 1,
            "runId": connection["runId"], "token": connection["token"],
        })
        hello = self.wait(lambda event: event.get("type") == "hello")
        assert hello.get("stateSchemaVersion", 1) in SUPPORTED_STATE_SCHEMAS, \
            "Server advertises an unsupported state schema"
        self.hello = hello
        self.wait(lambda event: event.get("type") == "snapshot")

    def send(self, frame):
        logged = dict(frame)
        if "token" in logged:
            logged["token"] = "<redacted>"
        self.outgoing.write(json.dumps(logged, ensure_ascii=False) + "\n")
        self.outgoing.flush()
        self.sock.sendall((json.dumps(frame) + "\n").encode("utf-8"))
        command_id = frame.get("commandId")
        if command_id is not None:
            self.pending_commands[command_id] = time.monotonic()

    _MAX_LINE = 1024 * 1024 + 1
    _uses_pump = True

    def _pump_lines(self):
        buf = bytearray()
        try:
            while not self._closing:
                try:
                    ready = select.select([self.sock], [], [], 0.5)[0]
                except (OSError, ValueError):
                    break
                if not ready:
                    continue
                chunk = self.sock.recv(65536)
                if not chunk:
                    if not self._closing:
                        self._lines.put((None, "eof"))
                    return
                buf += chunk
                while True:
                    end = buf.find(b"\n")
                    if end < 0:
                        break
                    self._lines.put((bytes(buf[:end + 1]), None))
                    del buf[:end + 1]
                # readline's oversized path: a line that never terminates
                # within the cap is emitted whole so read() flags it.
                if len(buf) > self._MAX_LINE:
                    self._lines.put((bytes(buf), None))
                    del buf[:]
        except Exception as error:
            if not self._closing:
                self._lines.put((None, error))

    def read(self, remaining):
        if self._terminal is not None:
            raise self._terminal
        try:
            line, error = self._lines.get(timeout=max(0.01, remaining))
        except queue.Empty:
            raise socket.timeout("timed out")
        if error == "eof":
            self._terminal = AssertionError(
                "Connection closed before the expected outcome")
            raise self._terminal
        if error is not None:
            self._terminal = error
            raise error
        if len(line) > 1024 * 1024 or not line.endswith(b"\n"):
            raise AssertionError("Oversized or truncated server frame")
        event = json.loads(line)
        self.last_sequence, self.last_time = validate_record(
            event, self.info, self.last_sequence, self.last_time)
        boundary = self.reconstructor.apply(event)
        assert self.info["token"] not in line.decode("utf-8"), "Credential leaked"
        if boundary is not None:
            validate_state_schema(event, boundary)
            self.last_state_tick = validate_state_tick(
                boundary, event["tick"], self.last_state_tick)
            self.state = boundary
            if event.get("commandId") is not None:
                self.action_states[event["commandId"]] = boundary
            assert self.state["captureValid"], self.state.get("captureError")
            assert len(self.state["slots"]) == 4, "Incomplete profile state"
        self.incoming.write(json.dumps(event, ensure_ascii=False) + "\n")
        self.incoming.flush()
        if event.get("type") == "result" \
                and event.get("commandId") is not None:
            # Any result proves the frame reached the command pipeline —
            # an intermediate "accepted" counts; the terminal outcome is
            # still owned by the waiting caller's own bounded wait.
            self.results[event["commandId"]] = event.get("status")
            self.pending_commands.pop(event["commandId"], None)
        self.events.append(event)
        return event

    # The documented command deadline (COMMANDS.md): an unanswered
    # command older than this is stuck, not "in flight".
    COMMAND_RESULT_DEADLINE_SECONDS = 5.0

    def overdue_commands(self,
            deadline=COMMAND_RESULT_DEADLINE_SECONDS):
        """Commands sent longer ago than `deadline` with no result at all.
        Returns {commandId: age_seconds} — the watchdog input for drivers
        that fire-and-forget (LanDriver sends input.set without an
        immediate wait, so only this check can name a stuck command)."""
        # A closed endpoint (process killed by fault injection, capture
        # torn down) can never answer again — its unanswered frames are
        # honestly recorded in commands.ndjson, but they are dead, not
        # stuck, so they must not trip the deadline watchdog.
        if self._closing:
            return {}
        now = time.monotonic()
        # Snapshot the pairs: a peer drain on another client's wait
        # thread resolves results into this same dict concurrently.
        return {command_id: now - sent
                for command_id, sent in list(self.pending_commands.items())
                if now - sent > deadline}

    def _drain_peers(self):
        """Consume whatever other live clients already sent; otherwise a
        long wait on this endpoint starves their validation sinks.

        Pumped peers buffer lines on their pump thread, so draining reads
        the queued backlog — polling the raw socket would always look
        empty while the pump races us and would leave the backlog to age.
        A terminal peer (honest EOF from a finished capture) is skipped.
        A non-terminal read failure — validation assert, evidence-write
        error — is a live defect: recorded on the peer, persisted to its
        evidence directory, and never retried silently while the backlog
        keeps flowing. wait() then fails fast with the peer named, so a
        frozen backlog can never age into an unattributable timeout."""
        for other in list(_ACTIVE_CLIENTS):
            if other is self or other._drained_terminal \
                    or other.drain_error is not None:
                continue
            if getattr(other, "_uses_pump", False):
                budget = other._lines.qsize()
            else:
                sock = getattr(other, "sock", None)
                try:
                    if sock is None or sock.fileno() < 0 \
                            or not select.select([sock], [], [], 0)[0]:
                        continue
                except (OSError, ValueError, AttributeError):
                    continue
                budget = 256
            for _ in range(min(budget, 512)):
                try:
                    other.read(0.02)
                except socket.timeout:
                    break
                except AssertionError as error:
                    if getattr(other, "_terminal", None) is not None:
                        # Honest EOF: the peer's server side closed the
                        # session (a finished capture does this
                        # deliberately) — not a drain target any more.
                        other._drained_terminal = True
                    else:
                        other._record_drain_error(error)
                    break
                except (OSError, ValueError) as error:
                    other._record_drain_error(error)
                    break

    def _record_drain_error(self, error):
        """Persist a drain failure so the peer's evidence names the real
        cause — the write itself is best-effort (the evidence path may be
        the component that failed)."""
        self.drain_error = error
        try:
            (self.evidence / "drain-error.txt").write_text(
                repr(error) + "\n", encoding="utf-8")
        except Exception:
            pass

    def _peer_drain_errors(self):
        """Live drain failures recorded on any active peer."""
        return [(other.info.get("port", "?"), other.drain_error)
                for other in list(_ACTIVE_CLIENTS)
                if other.drain_error is not None]

    def _raise_peer_drain_error(self):
        failures = self._peer_drain_errors()
        if failures:
            port, error = failures[0]
            raise AssertionError(
                "Peer drain for port %s failed: %r" % (port, error))

    def wait(self, predicate, timeout=20, on_event=None):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            # A recorded peer drain failure is a live defect — fail now
            # with its real cause rather than waiting out the deadline
            # while its backlog stays frozen.
            self._raise_peer_drain_error()
            try:
                # Bounded slices keep the drain-health check reachable
                # even when no events arrive.
                event = self.read(min(0.5, deadline - time.monotonic()))
            except (socket.timeout, TimeoutError):
                self._drain_peers()
                continue
            if on_event is not None:
                on_event(event)
            if predicate(event):
                return event
            self._drain_peers()
        hint = ""
        failures = self._peer_drain_errors()
        if failures:
            hint = " (peer drain failures: %s)" % "; ".join(
                "port %s: %r" % (port, error)
                for port, error in failures)
        raise AssertionError(
            "Timed out waiting for a recorded outcome" + hint)

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
        state = self.action_states.get(command_id)
        if state is None:
            raise AssertionError("Missing command-correlated action-boundary state")
        return state

    def close(self):
        if self in _ACTIVE_CLIENTS:
            _ACTIVE_CLIENTS.remove(self)
        # The pump selects on this socket in 0.5 s slices; closing the
        # descriptors cancels any pending recv — with no thread inside a
        # makefile read on the healthy path, no io-lock can hold this up.
        self._closing = True
        try:
            self.sock.close()
        except Exception:
            pass
        try:
            self.reader.close()
        except Exception:
            pass
        self.incoming.close()
        self.outgoing.close()
        # Dead endpoint: pending frames stay visible in commands.ndjson
        # (sent, never answered) but leave the watchdog map — nothing
        # will ever resolve them and they are not a stuck-command defect.
        self.pending_commands.clear()
        # A drain failure recorded while a peer waited on us must not die
        # silently at teardown either — name it here as well.
        if self.drain_error is not None:
            raise AssertionError(
                "Client closed with an unresolved drain failure: %r"
                % self.drain_error)


# --- external process-memory sampling (M4 Task 2) -------------------------
#
# Process memory is measured OUTSIDE the player once per second, never
# from Unity heap counters: Android reports the app's total PSS through
# `adb dumpsys meminfo`, Windows reports the process working set through
# psapi. Every record names its metric, source and units, and a failed
# sample is an explicit "unavailable" record — never a fabricated zero.
# Devices compare only against their own baseline; PSS and working set are
# differently-defined quantities and are never compared to each other.

def _windows_process_sample(pid):
    """(working-set bytes, creation stamp) via psapi; raises on failure.

    A terminated pid can still resolve: while any handle keeps the
    process object alive, OpenProcess succeeds and GetProcessMemoryInfo
    reports a ~0-byte working set for the dead process — a fabricated
    measurement. WaitForSingleObject on a SYNCHRONIZE handle separates
    "alive" from "exited", and the GetProcessTimes creation stamp lets
    the caller spot a reused pid instead of silently sampling a
    different process as "ok".
    """
    import ctypes
    from ctypes import wintypes

    class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    SYNCHRONIZE = 0x00100000
    WAIT_OBJECT_0 = 0
    handle = kernel32.OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, False, pid)
    if not handle:
        raise OSError("OpenProcess failed for pid %d" % pid)
    try:
        if kernel32.WaitForSingleObject(handle, 0) == WAIT_OBJECT_0:
            raise OSError("pid %d has exited" % pid)
        counters = PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(
                handle, ctypes.byref(counters), counters.cb):
            raise OSError("GetProcessMemoryInfo failed for pid %d" % pid)
        created = wintypes.FILETIME()
        if not kernel32.GetProcessTimes(
                handle, ctypes.byref(created),
                ctypes.byref(wintypes.FILETIME()),
                ctypes.byref(wintypes.FILETIME()),
                ctypes.byref(wintypes.FILETIME())):
            raise OSError("GetProcessTimes failed for pid %d" % pid)
        value = int(counters.WorkingSetSize)
        if value <= 0:
            raise OSError(
                "pid %d reported a zero working set" % pid)
        stamp = (created.dwHighDateTime << 32) | created.dwLowDateTime
        return value, stamp
    finally:
        kernel32.CloseHandle(handle)


def process_start_stamp(pid):
    """Process-start identity for a pid — the creation stamp that
    separates a live process from a recycled pid number. Returns None
    when the platform has no stamp concept or the probe failed; the
    caller records the gap, never fabricates one."""
    if sys.platform != "win32":
        return None
    try:
        _value, stamp = _windows_process_sample(pid)
        return stamp
    except Exception:
        return None


def _android_total_pss_bytes(serial, package, adb="adb"):
    """App total PSS via `adb shell dumpsys meminfo`; raises on failure."""
    import re
    command = [adb]
    if serial:
        command += ["-s", serial]
    command += ["shell", "dumpsys", "meminfo", package]
    output = subprocess.run(command, capture_output=True, text=True,
                            timeout=20)
    if output.returncode != 0:
        raise OSError("dumpsys meminfo failed: " + output.stderr.strip())
    # The summary block ends with "Total PSS: <n> kB" (also rendered as a
    # "TOTAL" row on older dumpsys output). Capture either form in kB.
    match = re.search(r"TOTAL\s+PSS[:\s]+([\d,]+)\s*(?:kB|KB)?",
                      output.stdout, re.IGNORECASE)
    if match is None:
        raise OSError("dumpsys meminfo carried no TOTAL PSS row")
    pss = int(match.group(1).replace(",", "")) * 1024
    if pss <= 0:
        raise OSError(
            "dumpsys meminfo reported zero PSS for " + package)
    return pss


class MemorySampler:
    """Once-per-second external process-memory sampler.

    One daemon thread appends `memory.ndjson` under the evidence directory;
    every line names metric/source/units and either carries `value` bytes
    or `status: "unavailable"` with a reason. Stop() joins the writer.
    """

    def __init__(self, evidence_dir, pid=None, adb_serial=None,
                 package="com.daniel.familyracing", interval=1.0):
        assert pid is not None or adb_serial is not None, \
            "MemorySampler needs a Windows pid or an adb device serial"
        self._path = Path(evidence_dir) / "memory.ndjson"
        self._pid = pid
        self._pid_created = None
        self._serial = adb_serial
        self._package = package
        self._interval = interval
        self._sequence = 0
        self._clock = time.monotonic()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True,
                                        name="memory-sampler")

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *exc):
        self.stop()
        return False

    def start(self):
        self._thread.start()
        return self

    def stop(self, timeout=5.0):
        self._stop.set()
        # ident is None until the thread is started — joining a
        # never-started thread raises RuntimeError.
        if self._thread.ident is not None:
            self._thread.join(timeout)

    def _sample(self):
        if self._pid is not None:
            value, created = _windows_process_sample(self._pid)
            # The sampler never rebinds: a relaunched player is a new
            # measurement target. A reused pid (same number, different
            # creation stamp) must record unavailable, not silently
            # sample a different process as "ok".
            if self._pid_created is None:
                self._pid_created = created
            elif created != self._pid_created:
                raise OSError(
                    "pid %d was reused by a new process; "
                    "sampler stays bound to the original" % self._pid)
            return ("process.workingSet",
                    "psapi GetProcessMemoryInfo", value)
        return ("process.totalPss",
                "adb dumpsys meminfo",
                _android_total_pss_bytes(self._serial, self._package))

    def _run(self):
        with self._path.open("x", encoding="utf-8") as sink:
            while not self._stop.is_set():
                self._sequence += 1
                record = {
                    "type": "memory",
                    "sequence": self._sequence,
                    "monotonicMs": int((time.monotonic() - self._clock)
                                       * 1000),
                    "units": "bytes",
                }
                try:
                    metric, source, value = self._sample()
                    record.update(metric=metric, source=source,
                                  status="ok", value=value)
                except Exception as error:
                    record.update(
                        metric=("process.workingSet" if self._pid is not None
                                else "process.totalPss"),
                        source=("psapi GetProcessMemoryInfo"
                                if self._pid is not None
                                else "adb dumpsys meminfo"),
                        status="unavailable",
                        reason=type(error).__name__ + ": " + str(error))
                sink.write(json.dumps(record) + "\n")
                sink.flush()
                self._stop.wait(self._interval)


def settings(language="he", camera="chase"):
    return {
        "language": language, "camera": camera, "reducedCameraShake": True,
        "musicVolume": 0.25, "effectsVolume": 0.5, "voiceVolume": 0.75,
    }


def inputs(steering=0, duration=10, drift=False, boost=False, gadget=False,
           recover=False, change_camera=False, brake=None, throttle=None):
    params = {
        "steering": steering, "drift": drift, "boost": boost, "gadget": gadget,
        "recover": recover, "changeCamera": change_camera, "pause": False,
        "durationTicks": duration,
    }
    # Optional Task-7 demands: absent throttle means automatic throttle;
    # an explicit 0 is a deliberate coast.
    if brake is not None:
        params["brake"] = brake
    if throttle is not None:
        params["throttle"] = throttle
    return params


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
            "race.start", "race.restart", "race.exit",
            "tutorial.start", "tutorial.replay", "tutorial.exit",
            # LAN session surface (schema 3): async commands report
            # accepted now and complete through a terminal outcome.
            "lan.host", "lan.discover", "lan.discovery.stop", "lan.join",
            "lan.configure", "lan.vehicle", "lan.ready", "lan.start",
            "lan.pause.request", "lan.pause.set", "lan.reconnect",
            "lan.results.request", "lan.results.retrySave",
            "lan.rematch", "lan.leave",
        }, actions
        # There is deliberately no tutorial.complete or teleport command:
        # onboarding can only advance on genuine driving events.
        assert not any(action.startswith("tutorial.complete")
                       or "teleport" in action for action in actions)
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
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state["tick"] >= drive["tick"] + 10
                    and client.state["input"]["steering"] == 0)
        checks.append("held controls, transient pulses, camera, bounded expiry")

        toggled, _ = client.applied("camera.toggle")
        assert client.action_state(toggled["commandId"])["draftProfile"]["camera"] == "chase"
        checks.append("camera toggle command updates selected profile draft")

        paused, _ = client.applied("pause.toggle")
        assert client.action_state(paused["commandId"])["paused"]
        held, _ = client.applied("input.set", inputs(steering=-1, boost=True, duration=2))
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state["paused"]
                    and client.state["tick"] >= held["tick"] + 2
                    and client.state["input"]["steering"] == 0)
        client.applied("pause.toggle")
        client.applied("input.clear")
        checks.append("paused observation and input cleanup")

        # --- tutorial onboarding ------------------------------------------
        # The tutorial free-drive stage is internal content: race.start
        # must refuse it, and only tutorial.start may open it.
        reserved, _ = client.command(
            "race.start", {"vehicleId": "car-01", "eventId": "tutorial-01"})
        assert reserved["status"] == "rejected" \
            and reserved["code"] == "unavailable", reserved
        checks.append("internal tutorial stage rejected by race.start")

        # Ordered lessons start through the shared command path and
        # advance only on genuine driving output; replay re-issues the cue
        # without progress, exit abandons cleanly.
        begun, _ = client.applied("tutorial.start", {"vehicleId": "car-01"})
        state = client.action_state(begun["commandId"])
        assert state["screen"] == "driving" and state["driving"]
        tutorial = state["driving"]["tutorial"]
        assert tutorial["phase"] == "lessons" \
            and tutorial["lessonId"] == "steering" \
            and tutorial["objective"] == "tutorial.cue.steering", tutorial
        checks.append("tutorial.start enters the ordered lesson stage")

        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state.get("driving")
                    and client.state["driving"]["phase"] == "running",
                    timeout=30)
        replayed, _ = client.applied("tutorial.replay")
        state = client.action_state(replayed["commandId"])
        assert state["driving"]["tutorial"]["cueSequence"] \
            == tutorial["cueSequence"] + 1
        assert state["driving"]["tutorial"]["lessonId"] == "steering", \
            "Replay must not advance the lesson"
        checks.append("tutorial.replay re-issues the cue without progress")

        # The steering lesson completes only after enough simulated
        # boundaries of real held steering input.
        client.applied("input.set", inputs(steering=0.8, duration=150))
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state.get("driving")
                    and client.state["driving"].get("tutorial")
                    and client.state["driving"]["tutorial"]["lessonId"]
                    == "brake", timeout=60)
        checks.append("steering lesson completes on genuine held steering")

        # The brake lesson is next in the authored order and completes
        # only on a real held brake demand through input.set.
        client.applied("input.set", inputs(brake=0.8, duration=60))
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state.get("driving")
                    and client.state["driving"].get("tutorial")
                    and client.state["driving"]["tutorial"]["lessonId"]
                    == "drift", timeout=60)
        checks.append("brake lesson completes on genuine held brake")

        exited, _ = client.applied("tutorial.exit")
        state = client.action_state(exited["commandId"])
        assert state["screen"] == "practice" and state["driving"] is None
        checks.append("tutorial.exit abandons the onboarding session")

        # --- driving slice -------------------------------------------------
        client.applied("ui.navigate", {"screen": "practice"})
        started, _ = client.applied(
            "race.start", {"vehicleId": "car-01", "eventId": "intro-01"})
        state = client.action_state(started["commandId"])
        assert state["screen"] == "driving" and state["driving"]
        driving = state["driving"]
        assert driving["vehicleId"] == "car-01" and driving["eventId"] == "intro-01"
        assert driving["phase"] == "countdown" and driving["raceId"]
        checks.append("race.start enters driving through the shared command path")

        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state.get("driving")
                    and client.state["driving"]["phase"] == "running",
                    timeout=30)
        # Auto-acceleration: the car moves without any held input.
        spawn_z = driving["motor"]["z"]
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state.get("driving")
                    and client.state["driving"]["motor"]["z"] > spawn_z + 5.0,
                    timeout=60)
        motor = client.state["driving"]["motor"]
        heading0 = motor["heading"]
        client.applied("input.set", inputs(steering=1, duration=90))
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state.get("driving")
                    and abs(client.state["driving"]["motor"]["heading"] - heading0)
                    > 0.3, timeout=30)
        client.applied("input.clear")
        checks.append("command input moves and steers the physical car")

        paused, _ = client.applied("pause.toggle")
        paused_state = client.action_state(paused["commandId"])
        assert paused_state["paused"]
        sim_tick = paused_state["driving"]["simulationTick"]
        client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                    and client.state["tick"] >= paused_state["tick"] + 30)
        assert client.state["driving"]["simulationTick"] == sim_tick, \
            "Simulation kept running while paused"
        client.applied("pause.toggle")
        checks.append("paused driving freezes simulation, observation advances")

        client.send({"type": "screenshot", "captureId": "drive-1"})
        shot = client.wait(lambda event: event.get("type") == "screenshot"
                           and event.get("captureId") == "drive-1")
        assert shot["status"] == "captured", shot
        shot_path = Path(connection["_directory"]) / "screenshots" / shot["file"]
        assert shot_path.stat().st_size > 0, "Screenshot artifact missing"
        checks.append("bridge screenshot capture is correlated and durable")

        restarted, _ = client.applied("race.restart")
        state = client.action_state(restarted["commandId"])
        assert state["driving"]["raceId"] != driving["raceId"], \
            "Restart must create a new race identity"
        exited, _ = client.applied("race.exit")
        state = client.action_state(exited["commandId"])
        assert state["screen"] == "practice" and state["driving"] is None
        checks.append("race.restart identity and race.exit return")

        unsupported, _ = client.command("warp.drive")
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
        assert connection.get("stateSchemaVersion", 1) in SUPPORTED_STATE_SCHEMAS, \
            "Unsupported advertised state schema"
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
