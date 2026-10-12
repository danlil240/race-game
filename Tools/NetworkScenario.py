"""Multi-player LAN qualification scenario over the real transport.

Usage (physical qualification — the mandated interface):

    NetworkScenario.py --connections <host-connection.json>
        <client-connection.json> [more connection.json files]
        [--host-address <lan-ip>] --evidence <new-directory>

Attaches to 2-4 independently launched players (each player's run
directory carries its own connection.json — on Android these are
per-device ADB-forwarded loopback descriptors). The first listed
connection is driven as the host; the rest join as clients. Joining uses
LAN discovery first; --host-address is the explicit fallback for
subnets where broadcast discovery cannot reach.

Usage (local development — the scenario owns the processes):

    NetworkScenario.py --launch <player.exe> [--players 2-4]
        [--no-fault-injection] --evidence <new-directory>

Self-launches N loopback players with per-player profile directories.
Fault injection (a labeled separate mode) kills and relaunches the first
client mid-race to exercise AI takeover and a real credentialed rejoin,
then kills the host at the end to land survivors on the recovery screen.

Both modes drive the complete family lifecycle through the same command
surface the UI widgets call: profiles, lan-browser navigation, host on an
ephemeral port, join, lobby configuration, vehicle/ready, duplicate
command dedupe, bounded input and normalization, the deduplicated recover
action, both cameras, the shared pause request/toggle, AI takeover of a
dropped seat, immutable results + duplicate result retrieval, rematch
with the second vehicle class, host loss, recovery and leave.

Every endpoint's NDJSON trace is verified independently: contiguous
sequencing, schema-3 boundary validity, reconstructable snapshot/delta
boundaries, async command/terminal-outcome correlation, honest lifecycle
phases, predicted-vs-authoritative separation, unique reward rows, and no
credential or transport-secret leakage. State predicates with bounded
timeouts drive every step; there is no teleporting or forced finishing.
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

from AgentScenario import (BOUNDARY_TYPES, Client, StateReconstructor,
                           SUPPORTED_STATE_SCHEMAS, verify_trace)
from RestartScenario import file_hash, wait_connection, write_json


# The schema-3 session phases in their legal neighbourhood graph. A phase
# may repeat or move only along an edge the session policy permits; any
# other hop in a reconstructed boundary history means the emitted state
# disagrees with the lifecycle contract.
LEGAL_PHASE_TRANSITIONS = {
    "idle": {"idle", "discovering", "connecting", "lobby"},
    "discovering": {"discovering", "idle", "connecting"},
    "connecting": {"connecting", "lobby", "idle", "recovery"},
    "lobby": {"lobby", "countdown", "idle", "recovery", "results"},
    "countdown": {"countdown", "racing", "lobby", "idle", "recovery",
                  "reconnecting"},
    "racing": {"racing", "results", "recovery", "reconnecting", "idle"},
    "results": {"results", "lobby", "countdown", "idle", "recovery",
                "reconnecting"},
    "reconnecting": {"reconnecting", "lobby", "racing", "results",
                     "recovery", "idle"},
    "recovery": {"recovery", "reconnecting", "idle"},
}

TERMINAL_STATUSES = {"applied", "rejected", "failed"}

# Field names that must never appear inside observable state — credential
# or transport-secret material leaking would compromise rejoin safety.
FORBIDDEN_STATE_KEYS = {"credential", "cred", "token", "password",
                        "storedCredential", "joinSecret", "secret"}


def assert_no_secrets(node, path="state"):
    """Reject observable state carrying credential-shaped fields."""
    if isinstance(node, dict):
        for name, value in node.items():
            assert name not in FORBIDDEN_STATE_KEYS, \
                "Credential material leaked into " + path + "." + name
            assert_no_secrets(value, path + "." + name)
    elif isinstance(node, list):
        for index, item in enumerate(node):
            assert_no_secrets(item, "%s[%d]" % (path, index))


def validate_lan_state(state):
    """Schema-3 network-slice shape a reconstructed boundary must carry."""
    assert state.get("stateSchemaVersion") == 3, \
        "LAN evidence requires schema 3"
    session = state.get("network")
    assert isinstance(session, dict), "network slice must be an object"
    for key in ("role", "phase", "localParticipantId", "participants",
                "paused", "pauseRequests", "lastError", "pendingOperations",
                "discovered", "results"):
        assert key in session, "network slice missing " + key
    assert session["role"] in ("none", "host", "client")
    assert session["phase"] in LEGAL_PHASE_TRANSITIONS, \
        "Unknown phase " + repr(session["phase"])
    assert isinstance(session["participants"], list)
    assert len(session["participants"]) == 4, \
        "The roster must always expose all four slots"
    for participant in session["participants"]:
        for key in ("participantId", "slot", "occupied", "connected",
                    "ready", "controlSource", "vehicleId",
                    "ownershipEpoch"):
            assert key in participant, "participant missing " + key
    assert "networkRace" in state and "prediction" in state \
        and "remotePresentation" in state
    assert_no_secrets(session, "state.network")

    # Predicted-vs-authoritative separation: while a LAN race exists the
    # solo `driving` slice stays null — predicted/host-confirmed state may
    # never pose as local authoritative solo state. Prediction itself is
    # a client-only slice; the host's `prediction` is always null.
    if state.get("networkRace") is not None:
        assert state.get("driving") is None, \
            "LAN race state must not masquerade as authoritative driving"
    if state.get("prediction") is not None:
        assert session["role"] == "client", \
            "prediction exists only on a client; the host is authoritative"
        assert state.get("networkRace") is not None, \
            "prediction without a live LAN race"

    # The result/reward ledger is unique per (resultId, participantId):
    # a duplicated row is a duplicated reward claim. The immutable result
    # itself can never name the same participant twice.
    for row in session["results"]:
        for key in ("resultId", "raceId", "participantId", "state"):
            assert key in row, "result ledger row missing " + key
        assert row["state"] in ("pending", "failed", "persisted"), \
            "Unknown delivery state " + repr(row["state"])
    seen_deliveries = set()
    for row in session["results"]:
        key = (row["resultId"], row["participantId"])
        assert key not in seen_deliveries, \
            "Duplicated reward/delivery row: " + repr(key)
        seen_deliveries.add(key)

    if state.get("networkRace") is not None:
        race = state["networkRace"]
        descriptor = session.get("descriptor") or {}
        assert race.get("sessionId") == descriptor.get("sessionId"), \
            "Race/session identity mismatch"
        result = race.get("result")
        if result is not None:
            racers = [racer.get("participantId")
                      for racer in result.get("racers", []) if racer]
            assert len(racers) == len(set(racers)), \
                "Duplicated racer rows would double-count a reward"
    return session


def phase_history(events):
    """Ordered (sequence, phase, role) observed at every boundary."""
    history = []
    for event in events:
        if event.get("type") not in BOUNDARY_TYPES:
            continue
        state = event.get("_state")
        if state is None:
            continue
        network = state.get("network") or {}
        history.append((event["sequence"], network.get("phase"),
                        network.get("role")))
    return history


def validate_phase_history(history, label):
    """Every consecutive observed phase pair must be a legal transition;
    observation may skip boundaries only where the sequence validator
    already caught the gap."""
    previous = None
    for sequence, phase, role in history:
        assert phase in LEGAL_PHASE_TRANSITIONS, \
            label + ": unknown phase " + repr(phase)
        if previous is not None:
            prior_sequence, prior_phase, _ = previous
            assert phase in LEGAL_PHASE_TRANSITIONS[prior_phase], \
                "%s: illegal phase transition %s -> %s at sequence %d" % (
                    label, prior_phase, phase, sequence)
        previous = (sequence, phase, role)
    return history


def collect_command_results(events):
    """commandId -> ordered (status, tick, action, duplicate) results."""
    results = {}
    for event in events:
        if event.get("type") != "result":
            continue
        command_id = event.get("commandId")
        if not command_id:
            continue
        results.setdefault(command_id, []).append(
            (event.get("status"), event.get("tick"), event.get("action"),
             event.get("duplicate") is True))
    return results


def validate_async_outcomes(results, label):
    """Every lan.* command that was accepted must complete exactly once,
    and no accepted command may claim a terminal result before the
    acceptance record. A result marked duplicate is an identity-cache
    replay — it re-states the recorded outcome, it does not complete the
    command a second time."""
    incomplete = []
    for command_id, statuses in results.items():
        actions = {entry[2] for entry in statuses}
        if not any(action and action.startswith("lan.")
                   for action in actions):
            continue
        accepted = [i for i, entry in enumerate(statuses)
                    if entry[0] == "accepted" and not entry[3]]
        terminal = [entry[0] for entry in statuses
                    if entry[0] in TERMINAL_STATUSES and not entry[3]]
        if accepted and not terminal:
            incomplete.append(command_id)
        assert not (accepted and len(terminal) > 1), \
            "%s: %s completed more than once" % (label, command_id)
    return incomplete


def verify_lan_trace(directory, info):
    """verify_trace plus: lan.* accepted commands each carry a terminal
    outcome exactly once, and no phase transition is impossible."""
    verify_trace(directory, info)
    trace = directory / "state.ndjson"
    events = []
    reconstructor = StateReconstructor()
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            event = json.loads(line)
            state = reconstructor.apply(event)
            if state is not None:
                event["_state"] = state
            events.append(event)
    history = validate_phase_history(phase_history(events),
                                     str(directory))
    results = collect_command_results(events)
    incomplete = validate_async_outcomes(results, str(directory))
    return {"events": len(events), "phases": [h[1] for h in history],
            "incompleteCommands": incomplete}


def wait_state(client, predicate, timeout, what):
    """Wait until the client's reconstructed boundary state matches."""
    return client.wait(
        lambda event: event.get("type") in BOUNDARY_TYPES
        and predicate(client.state), timeout=timeout)


def wait_terminal(client, command_id, timeout=30):
    """The accepted result arrives first; the terminal outcome follows."""
    return client.wait(
        lambda event: event.get("type") == "result"
        and event.get("commandId") == command_id
        and event.get("status") in TERMINAL_STATUSES,
        timeout=timeout)


def launch(player_command, root, name, profile_dir, startup_timeout):
    """Start one validation player with a stable per-player profile dir
    (the reconnect credential store survives a relaunch); return
    (process, info, directory)."""
    directory = root / (name + "-run")
    log_path = root / (name + "-player.log")
    command = [*player_command, "--validation-dir", str(directory),
               "--validation-profile-dir", str(profile_dir),
               "-logFile", str(log_path)]
    with log_path.open("wb") as output:
        process = subprocess.Popen(command, stdout=output,
                                   stderr=subprocess.STDOUT)
    info = wait_connection(process, directory, startup_timeout)
    info["command"] = command
    return process, info, directory


def lan_inputs(steering=0, duration=10, drift=False, boost=False,
               recover=False, change_camera=False):
    return {
        "steering": steering, "drift": drift, "boost": boost,
        "gadget": False, "recover": recover, "changeCamera": change_camera,
        "pause": False, "durationTicks": duration,
    }


# The LAN catalog authors one event per class: the scenario drives both
# so a class-specific defect can never hide behind a single-pass check.
CLASS_EVENTS = {
    "car": ("lan-car-01", "car-01"),
    "motorcycle": ("lan-motorcycle-01", "motorcycle-01"),
}


def occupied(state):
    return [p for p in state["network"]["participants"] if p["occupied"]]


def send_duplicate_command(client, action, parameters, label):
    """Replay one identical command frame; the second result must carry
    the duplicate marker with the same status — never a re-execution."""
    command_id = "dup-" + label
    first, frame = client.command(action, parameters,
                                  command_id=command_id)
    client.send(frame)
    second = client.wait(
        lambda event: event.get("type") == "result"
        and event.get("commandId") == command_id,
        timeout=20)
    assert second.get("duplicate") is True, \
        "Replayed " + action + " did not report duplicate: " + repr(second)
    assert second["status"] == first["status"], \
        "Duplicate " + action + " changed status: " + repr(second)
    return first


def drive_join(client, host_port, join_address, allow_loopback,
               discovery_timeout=8):
    """Discovery-first join with the honest fallbacks; returns the
    evidence note describing which join path was exercised."""
    join_target = None
    client.command("lan.discover")
    deadline = time.monotonic() + discovery_timeout
    while time.monotonic() < deadline:
        try:
            client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                        and any(entry["port"] == host_port
                                for entry in
                                client.state["network"]["discovered"]),
                        timeout=1)
            discovered = [entry for entry in
                          client.state["network"]["discovered"]
                          if entry["port"] == host_port]
            if discovered:
                join_target = {"address": discovered[0]["address"],
                               "port": host_port}
                break
        except AssertionError:
            pass
    if join_target is not None:
        note = "discovery located the host advertisement"
    elif join_address is not None:
        client.command("lan.discovery.stop")
        join_target = {"address": join_address, "port": host_port}
        note = "direct-address join (--host-address fallback)"
    elif allow_loopback:
        client.command("lan.discovery.stop")
        join_target = {"address": "127.0.0.1", "port": host_port}
        note = "direct-address join (loopback discovery fallback)"
    else:
        raise AssertionError(
            "Discovery found no host and no --host-address was given; "
            "on a real LAN either broadcasts must reach or the host's "
            "address must be supplied.")

    accepted, _ = client.command("lan.join", join_target)
    assert accepted["status"] == "accepted", accepted
    terminal = wait_terminal(client, accepted["commandId"])
    assert terminal["status"] == "applied", terminal
    wait_state(client,
               lambda state: state["network"]["phase"] == "lobby"
               and state["screen"] == "lan-lobby", 15, "client lobby")
    return note


def drive_race(host, clients, class_id, checks):
    """Lobby -> racing for one vehicle class: configure, per-peer vehicle
    + ready (clients round-trip asynchronously), host start, then the
    schema-3 race assertions both endpoints must satisfy."""
    event_id, vehicle_id = CLASS_EVENTS[class_id]
    host.applied("lan.configure",
                 {"eventId": event_id, "classId": class_id})
    host.applied("lan.vehicle", {"vehicleId": vehicle_id})
    for index, peer in enumerate(clients):
        accepted, _ = peer.command("lan.vehicle", {"vehicleId": vehicle_id})
        assert accepted["status"] == "accepted"
        assert wait_terminal(peer, accepted["commandId"],
                             20)["status"] == "applied", \
            "client %d vehicle select" % index
    host.applied("lan.ready", {"ready": True})
    for index, peer in enumerate(clients):
        accepted, _ = peer.command("lan.ready", {"ready": True})
        assert accepted["status"] == "accepted"
        assert wait_terminal(peer, accepted["commandId"],
                             20)["status"] == "applied", \
            "client %d ready" % index
    host.applied("lan.start")
    peers = [("host", host)] + [("client-%d" % i, c)
                               for i, c in enumerate(clients)]
    for name, peer in peers:
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "racing",
                   30, name + " racing")
        state = peer.state
        assert state["screen"] == "lan-race", name
        validate_lan_state(state)
        race = state["networkRace"]
        assert race is not None \
            and race["phase"] in ("countdown", "running")
        assert len(race["racers"]) == 4, "AI fill seats empty slots"
        for racer in race["racers"]:
            assert racer["attribution"] in ("human", "ai"), racer
        # Predicted-vs-authoritative separation, observed live: the
        # solo driving slice stays empty under a LAN race.
        assert state["driving"] is None, \
            name + ": LAN race leaked into the solo driving slice"
    assert host.state["prediction"] is None, \
        "the host never predicts its own racer"
    for index, peer in enumerate(clients):
        wait_state(peer,
                   lambda state: isinstance(state["prediction"], dict),
                   30, "client-%d prediction" % index)
        lanes = peer.state["remotePresentation"]["racers"]
        assert isinstance(lanes, list), "client interpolation lanes"
    checks.append("lan.start ran the shared countdown into racing "
                  "(%s; predicted-vs-authoritative separation proven)"
                  % class_id)


def settle_results(peers, checks):
    """Every surviving endpoint reaches results with the immutable
    result, then deliveries reach their persisted terminal state."""
    for name, peer in peers:
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "results",
                   600, name + " results")
        state = peer.state
        assert state["screen"] == "lan-results", name
        race = state["networkRace"]
        assert race is not None and race["phase"] == "finished"
        assert race.get("result") is not None, "immutable result missing"
        assert state["network"]["results"], \
            "delivery ledger missing on " + name

    def deliveries_settled(state):
        states = [row["state"] for row in state["network"]["results"]]
        return all(value in ("persisted", "failed") for value in states)
    for name, peer in peers:
        wait_state(peer, deliveries_settled, 60, name + " delivery settle")
        states = {row["state"] for row in peer.state["network"]["results"]}
        assert "failed" not in states, \
            name + " persistence failed honestly? " + repr(states)
    checks.append("results phase: immutable result, delivery ledger "
                  "settled persisted on every survivor")


def run_lifecycle(host, clients, checks, join_address=None,
                  allow_loopback=False, fault=None):
    """Drive the complete LAN lifecycle across all attached players.

    `fault` (launch mode only) supplies {"kill": client->None,
    "relaunch": client->Client} hooks for the labeled fault-injection
    segment: process death exercises real AI takeover and credentialed
    rejoin instead of simulated disconnects."""
    peers = [("host", host)] + [("client-%d" % i, c)
                               for i, c in enumerate(clients)]
    for name, peer in peers:
        assert peer.state["activeSlot"] == -1 \
            and peer.state["screen"] == "profiles", name
        assert peer.hello.get("stateSchemaVersion") == 3, \
            name + " did not advertise schema 3"
        validate_lan_state(peer.state)
        peer.applied("profile.create", {"slot": 0, "language": "en"})
        peer.applied("ui.navigate", {"screen": "lan-browser"})
        assert peer.state["screen"] == "lan-browser", name
    checks.append("%d instances on the LAN browser with schema-3 slices"
                  % len(peers))

    # Host on an ephemeral port so two concurrent runs never collide;
    # the bound port is observable in the descriptor.
    result, _ = host.command("lan.host", {"port": 0})
    assert result["status"] == "applied", result
    wait_state(host,
               lambda state: state["network"]["phase"] == "lobby",
               10, "host lobby")
    host_state = host.state
    assert host_state["screen"] == "lan-lobby"
    descriptor = host_state["network"]["descriptor"]
    host_port = descriptor["hostPort"]
    assert type(host_port) is int and host_port > 0, descriptor
    checks.append("lan.host applied; lobby screen; bound port observable")

    # Duplicate command dedupe before session traffic grows: replaying
    # an identical frame yields the duplicate marker, same status, no
    # re-execution.
    send_duplicate_command(host, "lan.vehicle", {"vehicleId": "car-01"},
                           "host-vehicle")
    checks.append("duplicate command replayed with the duplicate marker")

    # Every client joins — the first tries discovery, later clients use
    # whatever join path the first found (or the explicit fallback).
    for index, peer in enumerate(clients):
        note = drive_join(peer, host_port, join_address,
                          allow_loopback)
        checks.append("client-%d %s" % (index, note))
    wait_state(host,
               lambda state: len(occupied(state)) == len(peers),
               15, "host sees the full roster")
    checks.append("async lan.join accepted then completed applied once "
                  "for %d clients" % len(clients))

    # --- race 1: cars --------------------------------------------------
    drive_race(host, clients, "car", checks)

    first_client = clients[0]
    client_id = first_client.state["network"]["localParticipantId"]
    assert client_id == first_client.state["savedProfile"]["id"]

    # Input bounds and normalization: an out-of-range sample is refused
    # wholesale, and a held input auto-expires after its duration — the
    # observable steering returns to neutral without another command.
    rejected, _ = first_client.command(
        "input.set", lan_inputs(steering=2, duration=5))
    assert rejected["status"] == "rejected" \
        and rejected["code"] == "invalidParameter", rejected
    first_client.applied("input.set", lan_inputs(steering=1, duration=10))
    assert first_client.state["input"]["steering"] == 1
    wait_state(first_client,
               lambda state: state["input"]["steering"] == 0,
               15, "held input normalized back to neutral")
    checks.append("bounded input: out-of-range refused, hold normalized "
                  "to neutral after durationTicks")

    # Both cameras: the toggle flips the draft preference and the applied
    # presentation camera facts follow on the next rendered frame.
    assert first_client.state["draftProfile"]["camera"] == "chase"
    first_client.applied("camera.toggle")
    wait_state(first_client,
               lambda state: state["presentation"]["camera"]["mode"]
               == "overhead", 15, "overhead camera applied")
    first_client.applied("camera.toggle")
    wait_state(first_client,
               lambda state: state["presentation"]["camera"]["mode"]
               == "chase", 15, "chase camera applied")
    checks.append("both cameras applied through observable camera facts")

    # Bounded input and the deduplicated one-shot action on the client:
    # the recover press rides the action path, so action.* events must
    # acknowledge it on the client's observable event stream.
    recover, _ = first_client.command(
        "input.set", lan_inputs(steering=0, duration=5, recover=True))
    assert recover["status"] == "applied", recover

    def action_acked(state):
        return any(event["kind"].startswith("action.")
                   and event.get("participantId") == client_id
                   for event in state.get("drivingEvents") or [])
    wait_state(first_client, action_acked, 30,
               "client action acknowledgment")
    checks.append("recover action acknowledged via action.* events")

    # Shared pause: the client's request appears on the host's roster of
    # pause requests; the host's toggle pauses and resumes everyone.
    accepted, _ = first_client.command("pause.toggle")
    assert accepted["status"] == "accepted"
    terminal = wait_terminal(first_client, accepted["commandId"])
    assert terminal["status"] == "applied", terminal
    wait_state(host,
               lambda state: client_id in state["network"]["pauseRequests"],
               10, "host sees the pause request")
    host.applied("pause.toggle")
    for name, peer in peers:
        wait_state(peer,
                   lambda state: state["network"]["paused"] is True,
                   10, name + " paused")
    host.applied("pause.toggle")
    for name, peer in peers:
        wait_state(peer,
                   lambda state: state["network"]["paused"] is False,
                   10, name + " resumed")
    checks.append("shared pause: client request, host toggle, shared resume")

    # AI takeover of a dropped seat. Launch mode kills the process (real
    # transport loss); attach mode uses an explicit mid-race leave — both
    # produce the same host-observed outcome: connected=false, AI driving.
    # `active` tracks endpoints still inside the session so nothing waits
    # on a player that honestly left.
    active = list(clients)
    if fault is not None:
        fault["kill"](first_client)
        checks.append("FAULT-INJECTION: client-0 process killed mid-race")
    else:
        first_client.applied("lan.leave")
        active.remove(first_client)
    dropped_id = client_id
    wait_state(host,
               lambda state: any(
                   p["participantId"] == dropped_id
                   and not p["connected"]
                   and p["controlSource"] == "ai"
                   for p in state["network"]["participants"]),
               60, "host marks the dropped seat AI-controlled")
    checks.append("AI takeover: dropped seat drives on as controlSource=ai")

    # Rejoin. Fault injection relaunches the process so the private
    # credential store drives a real credentialed reclaim; the leave path
    # honestly reports noSession because explicit leave discards material.
    if fault is not None:
        rejoined = fault["relaunch"](first_client)
        clients[0] = rejoined
        active[0] = rejoined
        rejoined.applied("profile.select", {"slot": 0})
        accepted, _ = rejoined.command("lan.reconnect")
        assert accepted["status"] == "accepted", accepted
        terminal = wait_terminal(rejoined, accepted["commandId"], 60)
        assert terminal["status"] == "applied", terminal
        wait_state(rejoined,
                   lambda state: state["network"]["phase"] == "racing",
                   30, "rejoined client racing")
        wait_state(host,
                   lambda state: any(
                       p["participantId"] == dropped_id
                       and p["connected"]
                       and p["controlSource"] == "remote"
                       for p in state["network"]["participants"]),
                   30, "host reclaims the seat for the rejoin")
        checks.append("FAULT-INJECTION: credentialed lan.reconnect "
                      "reclaimed the reserved seat")
    else:
        rejected, _ = first_client.command("lan.reconnect")
        assert rejected["status"] == "rejected" \
            and rejected["code"] == "noSession", rejected
        checks.append("explicit leave cleared rejoin material: "
                      "lan.reconnect rejected noSession")

    survivors = [("host", host)] + [
        ("client-%d" % i, c) for i, c in enumerate(active)]
    settle_results(survivors, checks)

    # Duplicate result retrieval: the immutable result serves repeated
    # requests identically and the ledger never duplicates a row.
    race_id = host.state["networkRace"]["raceId"]
    ledger_before = len(host.state["network"]["results"])
    for _ in range(2):
        result, _ = host.command("lan.results.request",
                                 {"raceId": race_id})
        assert result["status"] == "applied", result
    assert len(host.state["network"]["results"]) == ledger_before
    checks.append("duplicate lan.results.request served the identical "
                  "immutable result; ledger unchanged")

    # Rematch everyone still in session; the second race runs the other
    # vehicle class so both classes complete a race on the wire.
    host.applied("lan.rematch")
    for name, peer in survivors:
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "lobby",
                   15, name + " rematch lobby")
    checks.append("lan.rematch returned survivors to the lobby")
    drive_race(host, active, "motorcycle", checks)
    settle_results(survivors, checks)
    checks.append("both classes completed a full race lifecycle")

    # Host loss: an explicit host leave broadcasts session.closed and
    # every client still in the session lands on the recovery surface
    # with the honest reason. A reconnect attempt then fails honestly —
    # the endpoint is gone.
    host.applied("lan.leave")
    wait_state(host,
               lambda state: state["network"]["role"] == "none"
               and state["screen"] == "lan-browser",
               15, "host left")
    for index, peer in enumerate(active):
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "recovery"
                   and state["screen"] == "lan-recovery",
                   30, "client-%d recovery" % index)
        assert peer.state["network"]["lastError"] in \
            ("hostClosed", "hostLost", "sessionClosed"), \
            peer.state["network"]["lastError"]
        accepted, _ = peer.command("lan.reconnect")
        if accepted["status"] == "accepted":
            terminal = wait_terminal(peer, accepted["commandId"], 60)
            assert terminal["status"] == "failed", terminal
    checks.append("host loss: clients landed on lan-recovery; reconnect "
                  "attempts terminated failed honestly")
    for peer in active:
        peer.applied("lan.leave")
        wait_state(peer,
                   lambda state: state["network"]["role"] == "none"
                   and state["screen"] == "lan-browser",
                   15, "client left")
    checks.append("lan.leave returned every endpoint to the browser")
    return checks


def read_connection(path):
    """One launched player's connection descriptor (same contract as
    AgentScenario/DrivingScenario): a per-device ADB-forwarded loopback
    endpoint on Android, a local port on desktop."""
    info = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    assert type(info.get("protocolVersion")) is int \
        and info["protocolVersion"] == 1, "Bad connection descriptor"
    assert info.get("stateSchemaVersion", 1) in SUPPORTED_STATE_SCHEMAS, \
        "Player advertises an unsupported state schema"
    info["_directory"] = str(Path(path).resolve().parent)
    info["_source"] = str(Path(path).resolve())
    return info


def finish_capture(name, peer, report):
    """Health + finish handshake, then independent trace verification."""
    peer.send({"type": "health"})
    health = peer.wait(lambda event: event.get("type") == "health")
    assert health["captureValid"], health
    peer.send({"type": "finish"})
    finished = peer.wait(lambda event: event.get("type") == "finished")
    assert finished["captureValid"], finished
    outcome = verify_lan_trace(Path(peer.info["_directory"]), peer.info)
    assert not outcome["incompleteCommands"], \
        name + " left lan.* commands without a terminal outcome"
    report.setdefault("traces", {})[name] = outcome


def run_attached(connection_paths, evidence, host_address=None):
    """Attach to 2-4 independently launched players via their
    connection.json descriptors; the scenario owns no processes."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "attached %d-player LAN lifecycle over the real "
                 "transport" % len(connection_paths),
        "players": [], "checks": [],
        "notVerified": [
            "credentialed rejoin after process death (needs "
            "fault injection — use --launch)",
            "physical input, visuals, Hebrew rendering on devices",
            "audio quality",
        ],
    }
    clients = []
    try:
        for index, path in enumerate(connection_paths):
            role = "host" if index == 0 else "client-%d" % (index - 1)
            info = read_connection(path)
            peer_evidence = root / (role + "-evidence")
            peer_evidence.mkdir()
            write_json(peer_evidence / "attach.json", {
                "role": role, "connection": info["_source"],
                "validationDirectory": info["_directory"],
            })
            peer = Client(info, peer_evidence)
            clients.append((role, peer))
            report["players"].append({
                "role": role, "runId": info["runId"],
                "connection": info["_source"],
            })

        host = clients[0][1]
        rest = [peer for _, peer in clients[1:]]
        report["checks"] = run_lifecycle(
            host, rest, [],
            join_address=host_address,
            allow_loopback=False)
        for role, peer in clients:
            finish_capture(role, peer, report)
        report["checks"].append(
            "all captures finished; traces reconstruct; phases legal; "
            "accepted commands completed exactly once")
        report["status"] = "passed"
    except Exception as error:
        report["error"] = str(error)
    finally:
        for _, peer in clients:
            try:
                peer.close()
            except Exception:
                pass
        write_json(root / "result.json", report)
    return report


def run_scenario(player_command, evidence, players=2,
                 fault_injection=True, startup_timeout=90,
                 shutdown_timeout=10):
    """Self-launch N loopback players; the scenario owns the processes so
    the labeled fault-injection segment can kill and relaunch them."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "self-launched %d-instance LAN lifecycle over the real "
                 "UDP transport%s" % (players,
                 " + process-kill fault injection" if fault_injection
                 else ""),
        "runs": [], "checks": [],
        "notVerified": [
            "cross-device (physical LAN) reachability",
            "physical keyboard/touch input on devices",
            "visual/Hebrew/audio quality",
            "Android device behavior",
        ],
    }
    # name -> {process, info, directory, evidence, client, generation}:
    # the roster survives a kill/relaunch so the dead generation's
    # unfinished capture is honestly recorded, not silently dropped.
    roster = {}
    names = []
    try:
        assert startup_timeout > 0 and shutdown_timeout > 0
        assert 2 <= players <= 4, "LAN capacity is 2-4 players"
        executable = Path(player_command[0]).resolve(strict=True)
        report["executable"] = {"path": str(executable),
                                "sha256": file_hash(executable)}

        names = ["host"] + ["client-%d" % i for i in range(players - 1)]
        for name in names:
            profile_dir = root / (name + "-profiles")
            process, info, directory = launch(
                player_command, root, name, profile_dir, startup_timeout)
            evidence_dir = root / (name + "-evidence")
            evidence_dir.mkdir()
            write_json(evidence_dir / "launch.json", {
                "role": name, "command": info["command"],
                "validationDirectory": str(directory),
                "profileDirectory": str(profile_dir),
                "pid": process.pid,
            })
            report["runs"].append({"role": name, "pid": process.pid,
                                   "runId": info["runId"]})
            roster[name] = {
                "process": process, "info": info, "directory": directory,
                "evidence": evidence_dir, "profileDir": profile_dir,
                "client": Client(info, evidence_dir),
                "generation": 1, "alive": True,
            }

        host = roster["host"]["client"]
        clients = [roster[name]["client"] for name in names[1:]]

        fault = None
        if fault_injection:
            def kill(client):
                for name in names:
                    entry = roster[name]
                    if entry["client"] is client and entry["alive"]:
                        process = entry["process"]
                        process.terminate()
                        try:
                            process.wait(timeout=shutdown_timeout)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=shutdown_timeout)
                        try:
                            client.close()
                        except Exception:
                            pass
                        entry["alive"] = False
                        report["runs"].append({
                            "role": name, "pid": process.pid,
                            "fault": "process terminated by scenario",
                        })
                        return
                raise AssertionError("kill target not in the roster")

            def relaunch(client):
                for name in names:
                    entry = roster[name]
                    if entry["client"] is client:
                        generation = entry["generation"] + 1
                        run_name = "%s-relaunch%d" % (name, generation)
                        # Same profile dir: lan-reconnect.json survives.
                        process, info, directory = launch(
                            player_command, root, run_name,
                            entry["profileDir"], startup_timeout)
                        evidence_dir = root / (run_name + "-evidence")
                        evidence_dir.mkdir()
                        write_json(evidence_dir / "launch.json", {
                            "role": name, "generation": generation,
                            "command": info["command"],
                            "validationDirectory": str(directory),
                            "profileDirectory": str(entry["profileDir"]),
                            "pid": process.pid,
                        })
                        report["runs"].append({
                            "role": name, "generation": generation,
                            "pid": process.pid, "runId": info["runId"],
                            "fault": "relaunched for credentialed rejoin",
                        })
                        new_client = Client(info, evidence_dir)
                        entry.update(process=process, info=info,
                                     directory=directory,
                                     evidence=evidence_dir,
                                     client=new_client,
                                     generation=generation, alive=True)
                        return new_client
                raise AssertionError("relaunch target not in the roster")

            fault = {"kill": kill, "relaunch": relaunch}

        report["checks"] = run_lifecycle(
            host, clients, [],
            join_address="127.0.0.1", allow_loopback=True,
            fault=fault)

        # Only living generations can durably finish a capture; a killed
        # process's truncated trace is recorded as a fault-injection
        # casualty instead of being forced through verification.
        for name in names:
            entry = roster[name]
            if entry["alive"]:
                finish_capture(name, entry["client"], report)
            else:
                report.setdefault("traces", {})[name] = {
                    "verified": False,
                    "reason": "process terminated by fault injection",
                }
        report["checks"].append(
            "live captures finished; traces reconstruct; phases legal; "
            "accepted commands completed exactly once")
        report["status"] = "passed"
    except Exception as error:
        report["error"] = str(error)
    finally:
        for name in names:
            entry = roster.get(name)
            if entry is None:
                continue
            try:
                entry["client"].close()
            except Exception:
                pass
            process = entry["process"]
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=shutdown_timeout)
                except subprocess.TimeoutExpired:
                    process.kill()
                    try:
                        process.wait(timeout=shutdown_timeout)
                    except subprocess.TimeoutExpired:
                        pass
            write_json(entry["evidence"] / "process.json", {
                "role": name, "generation": entry["generation"],
                "pid": process.pid, "exitCode": process.poll(),
            })
        write_json(root / "result.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument(
        "--connections", type=Path, nargs="+", metavar="CONNECTION_JSON",
        help="2-4 connection.json descriptors of independently launched "
             "players; the first is driven as the host. Android players "
             "use per-device ADB-forwarded loopback descriptors.")
    mode.add_argument(
        "--launch", type=Path, metavar="PLAYER_EXE",
        help="Dev convenience: self-launch N local players and own their "
             "processes (enables process-kill fault injection).")
    parser.add_argument("--players", type=int, default=2,
                        help="--launch mode only: 2-4 local players")
    parser.add_argument("--no-fault-injection", action="store_true",
                        help="--launch mode only: skip the labeled "
                             "process-kill rejoin/host-loss segment")
    parser.add_argument("--host-address", default=None,
                        help="--connections mode only: explicit host LAN "
                             "address when broadcast discovery cannot "
                             "reach it")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--startup-timeout", type=float, default=90)
    parser.add_argument("--shutdown-timeout", type=float, default=10)
    args = parser.parse_args()
    if not __debug__:
        raise RuntimeError("Run without -O: scenario assertions must be enabled")
    if args.connections is not None:
        assert 2 <= len(args.connections) <= 4, \
            "--connections takes 2-4 connection descriptors"
        report = run_attached(args.connections, args.evidence,
                              host_address=args.host_address)
    else:
        report = run_scenario([str(args.launch)], args.evidence,
                              players=args.players,
                              fault_injection=not args.no_fault_injection,
                              startup_timeout=args.startup_timeout,
                              shutdown_timeout=args.shutdown_timeout)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
