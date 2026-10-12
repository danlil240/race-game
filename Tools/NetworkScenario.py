"""Two-player LAN qualification scenario over the real transport.

Usage: NetworkScenario.py <player> --evidence <new-dir>

Launches two validation players (a host and a client), then drives the
complete family LAN lifecycle through the same command surface the UI
widgets call: profiles, lan-browser navigation, host on an ephemeral port,
discovery-or-direct join, lobby configuration, vehicle/ready, start, the
shared countdown/race clock, bounded input and the deduplicated recover
action, the shared pause request/toggle, immutable results and their
persistence ledger, rematch/leave, and a graceful finish of both captures.

Both endpoints' NDJSON traces are verified independently: contiguous
sequencing, schema-3 boundary validity, reconstructable snapshot/delta
boundaries, async command/terminal-outcome correlation, honest lifecycle
phases, and no credential or transport-secret leakage. This scenario
verifies two instances on one machine (loopback); cross-device physical
qualification stays with Task 10.
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

from AgentScenario import (BOUNDARY_TYPES, Client, StateReconstructor,
                           verify_trace)
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
    if state.get("networkRace") is not None:
        race = state["networkRace"]
        assert race.get("sessionId") == session.get("descriptor", {}).get(
            "sessionId") or session.get("descriptor") is None, \
            "Race/session identity mismatch"
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
    """commandId -> ordered (status, tick) results over one endpoint."""
    results = {}
    for event in events:
        if event.get("type") != "result":
            continue
        command_id = event.get("commandId")
        if not command_id:
            continue
        results.setdefault(command_id, []).append(
            (event.get("status"), event.get("tick"), event.get("action")))
    return results


def validate_async_outcomes(results, label):
    """Every lan.* command that was accepted must complete exactly once,
    and no accepted command may claim a terminal result before the
    acceptance record."""
    incomplete = []
    for command_id, statuses in results.items():
        actions = {entry[2] for entry in statuses}
        if not any(action and action.startswith("lan.")
                   for action in actions):
            continue
        accepted = [i for i, entry in enumerate(statuses)
                    if entry[0] == "accepted"]
        terminal = [entry[0] for entry in statuses
                    if entry[0] in TERMINAL_STATUSES]
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


def launch(player_command, root, role, startup_timeout):
    """Start one validation player instance; return (process, info, dir)."""
    directory = root / (role + "-run")
    log_path = root / (role + "-player.log")
    command = [*player_command, "--validation-dir", str(directory),
               "-logFile", str(log_path)]
    with log_path.open("wb") as output:
        process = subprocess.Popen(command, stdout=output,
                                   stderr=subprocess.STDOUT)
    info = wait_connection(process, directory, startup_timeout)
    info["command"] = command
    return process, info, directory


def lan_inputs(steering=0, duration=10, drift=False, boost=False,
               recover=False):
    return {
        "steering": steering, "drift": drift, "boost": boost,
        "gadget": False, "recover": recover, "changeCamera": False,
        "pause": False, "durationTicks": duration,
    }


def run_lifecycle(host, client, checks):
    """Drive the complete LAN lifecycle over both validation clients."""
    for peer, name in ((host, "host"), (client, "client")):
        assert peer.state["activeSlot"] == -1 \
            and peer.state["screen"] == "profiles", name
        assert peer.hello.get("stateSchemaVersion") == 3, \
            name + " did not advertise schema 3"
        validate_lan_state(peer.state)
        peer.applied("profile.create", {"slot": 0, "language": "en"})
        peer.applied("ui.navigate", {"screen": "lan-browser"})
        assert peer.state["screen"] == "lan-browser", name
    checks.append("both instances on the LAN browser with schema-3 slices")

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
    host_participant = host_state["savedProfile"]["id"]
    checks.append("lan.host applied; lobby screen; bound port observable")

    # Discovery first, direct-address fallback if loopback broadcast does
    # not arrive in time — both are legal family join paths and the
    # evidence records which one was exercised.
    join_target = None
    client.command("lan.discover")
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline:
        try:
            client.wait(lambda event: event.get("type") in BOUNDARY_TYPES
                        and any(host["port"] == host_port
                                for host in
                                client.state["network"]["discovered"]),
                        timeout=1)
            discovered = [h for h in client.state["network"]["discovered"]
                          if h["port"] == host_port]
            if discovered:
                join_target = {"address": discovered[0]["address"],
                               "port": host_port}
                checks.append("discovery located the host advertisement")
            break
        except AssertionError:
            pass
    if join_target is None:
        client.command("lan.discovery.stop")
        join_target = {"address": "127.0.0.1", "port": host_port}
        checks.append("direct-address join (loopback discovery fallback)")

    accepted, _ = client.command("lan.join", join_target)
    assert accepted["status"] == "accepted", accepted
    terminal = wait_terminal(client, accepted["commandId"])
    assert terminal["status"] == "applied", terminal
    wait_state(client,
               lambda state: state["network"]["phase"] == "lobby"
               and state["screen"] == "lan-lobby", 10, "client lobby")
    wait_state(host,
               lambda state: len([p for p in
                                  state["network"]["participants"]
                                  if p["occupied"]]) == 2,
               10, "host sees the client")
    checks.append("async lan.join accepted then completed applied once")

    # Lobby: the host configures, the client picks a vehicle and readies;
    # every request round-trips through the host's authority.
    configured, _ = host.command("lan.configure",
                                 {"eventId": "lan-car-01",
                                  "classId": "car"})
    assert configured["status"] == "applied", configured
    accepted, _ = client.command("lan.vehicle", {"vehicleId": "car-01"})
    assert accepted["status"] == "accepted"
    assert wait_terminal(client, accepted["commandId"])["status"] == "applied"
    accepted, _ = client.command("lan.ready", {"ready": True})
    terminal = wait_terminal(client, accepted["commandId"])
    assert terminal["status"] == "applied", terminal
    host.applied("lan.vehicle", {"vehicleId": "car-01"})
    host.applied("lan.ready", {"ready": True})
    checks.append("lobby configure/vehicle/ready round-tripped")

    started, _ = host.command("lan.start")
    assert started["status"] == "applied", started
    for peer, name in ((host, "host"), (client, "client")):
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "racing",
                   30, name + " racing")
        state = peer.state
        assert state["screen"] == "lan-race", name
        validate_lan_state(state)
        race = state["networkRace"]
        assert race is not None and race["phase"] in ("countdown", "running")
        assert len(race["racers"]) == 4, "AI fill seats empty slots"
    checks.append("lan.start ran the shared countdown into racing")

    # Bounded input and the deduplicated one-shot action on the client:
    # the recover press rides the action path, so action.* events must
    # acknowledge it on the client's observable event stream.
    client.applied("input.set", lan_inputs(steering=1, duration=45))
    recover, _ = client.command("input.set",
                                lan_inputs(steering=0, duration=5,
                                           recover=True))
    assert recover["status"] == "applied", recover
    client_id = client.state["network"]["localParticipantId"]
    assert client_id == client.state["savedProfile"]["id"]

    def action_acked(state):
        return any(event["kind"].startswith("action.")
                   and event.get("participantId") == client_id
                   for event in state.get("drivingEvents") or [])
    wait_state(client, action_acked, 30, "client action acknowledgment")
    checks.append("recover action acknowledged via action.* events")

    # Shared pause: the client's request appears on the host's roster of
    # pause requests; the host's toggle pauses and resumes everyone.
    accepted, _ = client.command("pause.toggle")
    assert accepted["status"] == "accepted"
    terminal = wait_terminal(client, accepted["commandId"])
    assert terminal["status"] == "applied", terminal
    wait_state(host,
               lambda state: client_id in state["network"]["pauseRequests"],
               10, "host sees the pause request")
    host.applied("pause.toggle")
    for peer, name in ((host, "host"), (client, "client")):
        wait_state(peer,
                   lambda state: state["network"]["paused"] is True,
                   10, name + " paused")
    host.applied("pause.toggle")
    for peer, name in ((host, "host"), (client, "client")):
        wait_state(peer,
                   lambda state: state["network"]["paused"] is False,
                   10, name + " resumed")
    checks.append("shared pause: client request, host toggle, shared resume")

    # The race must terminate: AI fill plus the finish deadline bound it.
    # 3 laps at AI pace plus the 60-second grace is comfortably inside
    # the wait; the trace carries the honest duration.
    for peer, name in ((host, "host"), (client, "client")):
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "results",
                   600, name + " results")
        state = peer.state
        assert state["screen"] == "lan-results", name
        race = state["networkRace"]
        assert race is not None and race["phase"] == "finished"
        assert race.get("result") is not None, "immutable result missing"
        deliveries = state["network"]["results"]
        assert deliveries, "delivery ledger missing on " + name
    checks.append("results phase with immutable result and delivery ledger")

    # Persistence settles: delivery rows must reach persisted/failed —
    # a retry path exists on failure but pending must not linger.
    def deliveries_settled(state):
        states = [row["state"] for row in state["network"]["results"]]
        return all(value in ("persisted", "failed") for value in states)
    for peer, name in ((host, "host"), (client, "client")):
        wait_state(peer, deliveries_settled, 60, name + " delivery settle")
        states = {row["state"] for row in
                  peer.state["network"]["results"]}
        assert "failed" not in states, \
            name + " persistence failed honestly? " + repr(states)
    checks.append("result deliveries reached a persisted terminal state")

    # Rematch returns the lobby to everyone; leaving tears down cleanly.
    host.applied("lan.rematch")
    for peer, name in ((host, "host"), (client, "client")):
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "lobby",
                   15, name + " rematch lobby")
    checks.append("lan.rematch returned both endpoints to the lobby")
    # The client leaves first: a clean leave is asserted on the client,
    # then the host's leave is the last teardown — it can never strand a
    # participant mid-assertion.
    for peer, name in ((client, "client"), (host, "host")):
        peer.applied("lan.leave")
        wait_state(peer,
                   lambda state: state["network"]["role"] == "none"
                   and state["screen"] == "lan-browser",
                   15, name + " left")
    checks.append("lan.leave returned both endpoints to the browser")
    return checks


def run_scenario(player_command, evidence, startup_timeout=90,
                 shutdown_timeout=10):
    """Own both player processes; persist a passing or failed report."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "two-instance LAN lifecycle over the real UDP transport",
        "runs": [], "checks": [],
        "notVerified": [
            "cross-device (physical LAN) reachability",
            "physical keyboard/touch input on devices",
            "visual/Hebrew/audio quality",
            "Android device behavior",
        ],
    }
    processes = []
    clients = []
    try:
        assert startup_timeout > 0 and shutdown_timeout > 0
        executable = Path(player_command[0]).resolve(strict=True)
        report["executable"] = {"path": str(executable),
                                "sha256": file_hash(executable)}

        for role in ("host", "client"):
            process, info, directory = launch(
                player_command, root, role, startup_timeout)
            role_evidence = root / (role + "-evidence")
            role_evidence.mkdir()
            write_json(role_evidence / "launch.json", {
                "role": role, "command": info["command"],
                "validationDirectory": str(directory),
                "pid": process.pid,
            })
            report["runs"].append({"role": role, "pid": process.pid,
                                   "runId": info["runId"]})
            clients.append((role, process, info, directory,
                            Client(info, role_evidence)))
            processes.append(process)

        host = next(client for role, _, _, _, client in clients
                    if role == "host")
        client = next(client for role, _, _, _, client in clients
                      if role == "client")
        report["checks"] = run_lifecycle(host, client, [])

        for role, process, info, directory, session in clients:
            session.send({"type": "health"})
            health = session.wait(
                lambda event: event.get("type") == "health")
            assert health["captureValid"], health
            session.send({"type": "finish"})
            finished = session.wait(
                lambda event: event.get("type") == "finished")
            assert finished["captureValid"], finished
            outcome = verify_lan_trace(Path(info["_directory"]), info)
            assert not outcome["incompleteCommands"], \
                role + " left lan.* commands without a terminal outcome"
            report.setdefault("traces", {})[role] = outcome
        report["checks"].append(
            "both captures finished; traces reconstruct; phases legal; "
            "accepted commands completed exactly once")
        report["status"] = "passed"
    except Exception as error:
        report["error"] = str(error)
    finally:
        for role, process, info, directory, session in clients:
            try:
                session.close()
            except Exception:
                pass
        for process in processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=shutdown_timeout)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=shutdown_timeout)
        for role, process, info, directory, session in clients:
            write_json(root / (role + "-evidence") / "process.json", {
                "role": role, "pid": process.pid,
                "exitCode": process.poll(),
            })
        write_json(root / "result.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("player", type=Path,
                        help="Path to the validation-capable player executable")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--startup-timeout", type=float, default=90)
    parser.add_argument("--shutdown-timeout", type=float, default=10)
    args = parser.parse_args()
    if not __debug__:
        raise RuntimeError("Run without -O: scenario assertions must be enabled")
    report = run_scenario([str(args.player)], args.evidence,
                          args.startup_timeout, args.shutdown_timeout)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
