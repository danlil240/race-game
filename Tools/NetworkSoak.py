"""Sustained multi-device LAN soak over the real transport.

Usage (physical qualification — the mandated interface):

    NetworkSoak.py --connections <host-connection.json>
        <client-connection.json> [more connection.json files]
        --minutes 30 --evidence <new-directory>
        [--host-address <lan-ip>]

Attaches to 2-4 independently launched players via their run-directory
connection.json descriptors — the same contract as NetworkScenario.py
(on Android these are per-device ADB-forwarded loopback endpoints). The
first listed connection is driven as the host. The soak runs the
complete lobby -> race -> results -> rematch cycle repeatedly for the
requested wall-clock minutes: every race runs in REAL time on the live
UDP transport. The soak never steps the deterministic clock — it waits
out real countdowns, real AI-fill racing, and the real finish deadline,
so a 30-minute soak is honest evidence of sustained family play, not an
accelerated simulation.

While each race runs, every participant receives periodic bounded
input.set holds (light family traffic — never teleports, never
sleeps-as-proof) and a periodic health sample. Per-device boundary
cadence is recorded at the socket; the immutable-result/delivery ledger
is checked once-only across every rematch (fresh raceId/resultId per
race, each (resultId, participantId) reward delivered exactly once) —
a `failed` delivery row is a legal outcome only when its `code` is the
permanent `ineligible:<outcome>` verdict the immutable result itself
implies (the NetworkScenario settle standard); any other failure code
fails the run; and
every endpoint's NDJSON trace is verified independently at teardown with
the full NetworkScenario LAN verification — contiguous sequencing,
schema-3 boundary validity, legal phase transitions, async command
completion exactly once, predicted-vs-authoritative separation, and no
credential leakage.

A peer whose stream stalls or closes mid-soak is recorded as a lost
participant (a real finding, not an error to hide) and the run fails
honestly; it is never substituted by synthetic traffic.
"""

import argparse
import json
import sys
import time
from pathlib import Path

from AgentScenario import BOUNDARY_TYPES
from DrivingScenario import DrivingClient, MAX_STEPS_PER_FRAME, \
    OUTGOING_CAPACITY
from DrivingSoak import cadence_stats
from NetworkScenario import drive_join, drive_race, finish_capture, \
    lan_inputs, read_connection, validate_lan_state, wait_state
from RestartScenario import write_json


# A new real-time race is only started while enough of the requested
# window remains to finish it honestly — a race truncated by the
# deadline is recorded as an incident, never silently dropped.
MIN_CYCLE_SECONDS = 150.0
# A peer that yields no usable stream activity for this long is lost:
# healthy LAN phases emit boundaries continuously, so a stall this long
# is a finding, not a scheduling artifact.
STALL_SECONDS = 45.0
DEFAULT_RESULTS_TIMEOUT = 600.0
DEFAULT_INPUT_INTERVAL = 8.0
DEFAULT_SAMPLE_SECONDS = 15.0
# After the first human/AI finisher the finish deadline bounds how long
# a race can linger; settle plus rematch needs headroom on top.
DELIVERY_SETTLE_SECONDS = 90.0


def cycle_class(index):
    """Race cycles alternate classes so a soak exercises both vehicle
    catalogs on the wire."""
    return "car" if index % 2 == 0 else "motorcycle"


def should_start_cycle(remaining_seconds, minimum=MIN_CYCLE_SECONDS):
    """Only begin a new real-time race while the remaining soak budget
    can plausibly contain one — shorter remainders drain instead."""
    return remaining_seconds >= minimum


def soak_inputs(role_index, refresh_index, hold_seconds):
    """One bounded held input sample: alternating steering direction per
    refresh and periodic boost — legal LAN input through the shared
    command path, nothing a child could not produce."""
    steering = 1 if (role_index + refresh_index) % 2 == 0 else -1
    return lan_inputs(steering=steering,
                      duration=max(1, int(hold_seconds * 30)),
                      boost=refresh_index % 3 == 0)


def request_health(peer, timeout=15):
    """One on-demand health record from a live endpoint."""
    peer.send({"type": "health"})
    return peer.wait(lambda event: event.get("type") == "health",
                     timeout=timeout)


def health_sample(role, peer, health, elapsed, directory=None):
    """One per-device health observation. `traceBytes` is only recorded
    when the player's validation directory is readable from here — for
    on-device runs it stays null rather than guessing."""
    state = peer.state or {}
    sample = {
        "role": role,
        "elapsedSeconds": round(elapsed, 1),
        "tick": state.get("tick"),
        "phase": (state.get("network") or {}).get("phase"),
        "captureValid": health.get("captureValid"),
        "outgoingRecords": health.get("outgoingRecords"),
        "boundaries": peer.boundaries,
        "maxBacklogSteps": peer.max_backlog_steps,
    }
    if directory is not None:
        trace = Path(directory) / "state.ndjson"
        try:
            sample["traceBytes"] = trace.stat().st_size \
                if trace.is_file() else None
        except OSError:
            sample["traceBytes"] = None
    return sample


def summarize_race(state, label):
    """The immutable-result facts one endpoint observed for a completed
    race. Asserts the endpoint reached results with the finalized result
    — a peer that never finished is evidence of a different kind."""
    network = state.get("network") or {}
    assert network.get("phase") == "results", \
        label + ": expected results phase, observed " + \
        repr(network.get("phase"))
    race = state.get("networkRace")
    assert race is not None and race.get("phase") == "finished", \
        label + ": race is not finished"
    result = race.get("result")
    assert result and result.get("resultId"), \
        label + ": immutable result missing"
    participants = [racer.get("participantId")
                    for racer in result.get("racers", [])]
    assert len(participants) == len(set(participants)), \
        label + ": duplicated racer in the immutable result"
    deliveries = {}
    delivery_codes = {}
    retained_failures = []
    for row in network.get("results") or []:
        if row.get("resultId") == result["resultId"]:
            deliveries[row["participantId"]] = row["state"]
            delivery_codes[row["participantId"]] = row.get("code") or ""
        elif row.get("state") == "failed":
            # Failed rows retained from earlier races outlive their
            # result slice: the outcome cross-check no longer applies,
            # only the ineligible-prefix requirement (the settle_results
            # standard).
            retained_failures.append({
                "resultId": row["resultId"],
                "participantId": row["participantId"],
                "code": row.get("code") or "",
            })
    assert deliveries, label + ": no delivery rows for the finalized result"
    return {
        "sessionId": race.get("sessionId"),
        "raceId": race.get("raceId"),
        "resultId": result["resultId"],
        "eventId": result.get("eventId"),
        "racers": len(result["racers"]),
        "finishers": sum(1 for racer in result["racers"]
                         if racer.get("outcome") == "finished"),
        "deliveries": deliveries,
        "deliveryCodes": delivery_codes,
        "outcomes": {racer.get("participantId"): racer.get("outcome")
                     for racer in result.get("racers", [])},
        "retainedFailures": retained_failures,
    }


class RaceLedger:
    """Cross-cycle once-only bookkeeping: every rematch must mint fresh
    race/result identities, and a participant may receive a given
    result's reward delivery exactly once."""

    def __init__(self):
        self.race_ids = set()
        self.result_ids = set()
        self.deliveries = set()
        self.races = []

    def record(self, summary, label):
        assert summary["raceId"] not in self.race_ids, \
            label + ": rematch reused raceId " + repr(summary["raceId"])
        assert summary["resultId"] not in self.result_ids, \
            label + ": duplicated resultId " + repr(summary["resultId"])
        for participant, state in summary["deliveries"].items():
            assert state in ("persisted", "failed"), \
                "%s: delivery for %s never settled (%s)" % (
                    label, participant, state)
            key = (summary["resultId"], participant)
            assert key not in self.deliveries, \
                label + ": reward delivered twice for " + repr(key)
            self.deliveries.add(key)
        self.race_ids.add(summary["raceId"])
        self.result_ids.add(summary["resultId"])
        self.races.append(summary)


class LanSoakClient(DrivingClient):
    """Strict client plus per-endpoint boundary wall-clock arrival times
    — the socket-observed cadence evidence for each device."""

    def __init__(self, connection, evidence):
        self.boundary_times = []
        super().__init__(connection, evidence)

    def read(self, remaining):
        event = super().read(remaining)
        if event.get("type") in BOUNDARY_TYPES and self.state is not None:
            self.boundary_times.append(
                (time.monotonic(), event.get("sequence"),
                 self.state.get("tick")))
        return event


def await_results(peers, deadline, start_monotonic, samples, incidents,
                  input_interval, sample_seconds, trace_dirs):
    """Drive the real-time race window: bounded input per participant and
    periodic per-device health samples until every peer reports results
    or the shared deadline expires. Peers that stall or disconnect are
    moved to `lost` (returned) — honest findings, never retried into a
    poisoned socket."""
    pending = {role: peer for role, peer in peers}
    role_index = {role: index for index, (role, _) in enumerate(peers)}
    lost = {}
    next_input = {role: time.monotonic() for role in pending}
    next_sample = {role: time.monotonic() for role in pending}
    refreshes = {role: 0 for role in pending}

    while pending and time.monotonic() < deadline:
        acted = False
        for role in list(pending):
            peer = pending[role]
            phase = ((peer.state or {}).get("network") or {}).get("phase")
            if phase == "results":
                del pending[role]
                acted = True
                continue
            now = time.monotonic()
            if now >= next_sample[role]:
                try:
                    health = request_health(peer)
                    samples[role].append(health_sample(
                        role, peer, health, now - start_monotonic,
                        trace_dirs.get(role)))
                    if health.get("captureValid") is not True:
                        incidents.append({
                            "kind": "captureInvalid", "role": role,
                            "elapsedSeconds": round(
                                now - start_monotonic, 1),
                            "detail": repr(health)[:200]})
                    if (health.get("outgoingRecords") or 0) \
                            >= OUTGOING_CAPACITY:
                        incidents.append({
                            "kind": "outgoingQueueFull", "role": role,
                            "elapsedSeconds": round(
                                now - start_monotonic, 1),
                            "detail": repr(health.get(
                                "outgoingRecords"))})
                except (AssertionError, KeyError, OSError) as error:
                    incidents.append({
                        "kind": "streamLost", "role": role,
                        "elapsedSeconds": round(now - start_monotonic, 1),
                        "detail": str(error)[:200]})
                    lost[role] = pending.pop(role)
                    acted = True
                    continue
                next_sample[role] = now + sample_seconds
                acted = True
            if now >= next_input[role] and phase == "racing":
                try:
                    peer.applied("input.set", soak_inputs(
                        role_index[role], refreshes[role],
                        input_interval))
                    refreshes[role] += 1
                except (AssertionError, KeyError) as error:
                    current = ((peer.state or {}).get("network") or {}) \
                        .get("phase")
                    incidents.append({
                        "kind": "inputRejected", "role": role,
                        "elapsedSeconds": round(now - start_monotonic, 1),
                        "phase": current, "detail": str(error)[:200]})
                except OSError as error:
                    incidents.append({
                        "kind": "streamLost", "role": role,
                        "elapsedSeconds": round(now - start_monotonic, 1),
                        "detail": str(error)[:200]})
                    lost[role] = pending.pop(role)
                    acted = True
                    continue
                next_input[role] = now + input_interval
                acted = True
            if ((peer.state or {}).get("network") or {}).get("phase") \
                    == "results":
                del pending[role]
                acted = True
        if not acted and pending:
            # Nothing was due: block on the first pending peer long
            # enough to consume boundaries — the stream is continuous in
            # a healthy race, so this returns quickly and never busy-spins.
            role, peer = next(iter(pending.items()))
            wait_for = min(next_input[role], next_sample[role]) \
                - time.monotonic()
            wait_for = max(0.25, min(wait_for, STALL_SECONDS))
            try:
                peer.wait(
                    lambda event: event.get("type") in BOUNDARY_TYPES
                    and ((peer.state or {}).get("network") or {})
                    .get("phase") == "results",
                    timeout=wait_for)
            except AssertionError as error:
                if "closed" in str(error):
                    incidents.append({
                        "kind": "streamLost", "role": role,
                        "elapsedSeconds": round(
                            time.monotonic() - start_monotonic, 1),
                        "detail": str(error)[:200]})
                    lost[role] = pending.pop(role)
                # A plain predicate timeout just re-evaluates due work.
            except (OSError, TimeoutError) as error:
                incidents.append({
                    "kind": "streamLost", "role": role,
                    "elapsedSeconds": round(
                        time.monotonic() - start_monotonic, 1),
                    "detail": str(error)[:200]})
                lost[role] = pending.pop(role)
    return pending, lost


def deliveries_settled(state):
    rows = (state.get("network") or {}).get("results") or []
    return bool(rows) and all(
        row.get("state") in ("persisted", "failed") for row in rows)


def delivery_verdicts(summaries):
    """One verdict per `failed` delivery row across the summarized
    endpoints, judged by the same standard NetworkScenario's
    settle_results asserts: a `failed` row is honest only when its
    `code` is exactly the permanent ineligible verdict the immutable
    result's own outcome implies (`ineligible:<outcome>`). `saveFailed`,
    an absent code, or an ineligible verdict contradicting the recorded
    outcome is a dishonest failure that must fail the soak — incident
    evidence, never a passing footnote."""
    verdicts = []
    for role in sorted(summaries):
        summary = summaries[role]
        for participant, state in sorted(summary["deliveries"].items()):
            if state != "failed":
                continue
            code = summary["deliveryCodes"].get(participant) or ""
            expected = "ineligible:" + str(
                summary["outcomes"].get(participant))
            verdicts.append({
                "role": role, "participant": participant,
                "code": code, "expected": expected,
                "honest": code == expected})
        for row in summary["retainedFailures"]:
            code = row["code"]
            verdicts.append({
                "role": role, "participant": row["participantId"],
                "resultId": row["resultId"], "code": code,
                "expected": "ineligible:*",
                "honest": code.startswith("ineligible:")})
    return verdicts


def record_delivery_verdicts(report, verdicts, cycle):
    """Append one deliveryFailed incident per failed row — the observed
    code and the honesty verdict recorded — and return True when any
    failure was dishonest (the run-level failure trigger)."""
    dishonest = False
    for verdict in verdicts:
        report["incidents"].append({
            "kind": "deliveryFailed", "role": verdict["role"],
            "participant": verdict["participant"], "cycle": cycle,
            "code": verdict["code"], "honest": verdict["honest"]})
        if not verdict["honest"]:
            dishonest = True
    return dishonest


def soak_status(lost, dishonest_delivery):
    """The honest top-level verdict: a lost participant or a failed
    delivery that is not a permanent ineligible verdict fails the run
    no matter how many races completed cleanly."""
    if lost:
        return "failed", "lost participant(s): " + \
            ", ".join(sorted(lost))
    if dishonest_delivery:
        return "failed", "result delivery failed for a recoverable " \
            "or result-mismatched reason (see deliveryFailed incidents)"
    return "passed", None


def enter_lobby(peers, host, clients, checks, host_address,
                discovery_timeout=8):
    """Profiles -> lan-browser -> host on an ephemeral port -> every
    client joined. Mirrors the NetworkScenario entry so the soak starts
    from the same proven lobby state."""
    for role, peer in peers:
        assert peer.state["activeSlot"] == -1 \
            and peer.state["screen"] == "profiles", role
        assert peer.hello.get("stateSchemaVersion") == 3, \
            role + " did not advertise schema 3"
        validate_lan_state(peer.state)
        peer.applied("profile.create", {"slot": 0, "language": "en"})
        peer.applied("ui.navigate", {"screen": "lan-browser"})
        assert peer.state["screen"] == "lan-browser", role
    checks.append("%d endpoints attached on the LAN browser "
                  "(schema-3 slices)" % len(peers))

    result, _ = host.command("lan.host", {"port": 0})
    assert result["status"] == "applied", result
    wait_state(host,
               lambda state: state["network"]["phase"] == "lobby",
               10, "host lobby")
    descriptor = host.state["network"]["descriptor"]
    host_port = descriptor["hostPort"]
    assert type(host_port) is int and host_port > 0, descriptor
    checks.append("lan.host applied; lobby open on bound port %d"
                  % host_port)

    for index, peer in enumerate(clients):
        note = drive_join(peer, host_port, host_address,
                          allow_loopback=False,
                          discovery_timeout=discovery_timeout)
        checks.append("client-%d %s" % (index, note))
    return host_port


def run_soak(connection_paths, evidence, minutes, host_address=None,
             results_timeout=DEFAULT_RESULTS_TIMEOUT,
             input_interval=DEFAULT_INPUT_INTERVAL,
             sample_seconds=DEFAULT_SAMPLE_SECONDS):
    """Attach to 2-4 launched players and soak the LAN lifecycle for the
    requested wall-clock minutes. Owns no processes."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "attached %d-player real-time LAN soak "
                 "(%.1f min requested)" % (len(connection_paths), minutes),
        "minutesRequested": minutes,
        "players": [], "checks": [], "cycles": [], "incidents": [],
        "notVerified": [
            "render frame times — cadence is the socket-observed "
            "boundary pacing proxy, not GPU timing",
            "cross-device reachability when descriptors share one host "
            "(loopback attach is same-machine evidence)",
            "credentialed rejoin after process death — the soak drives "
            "no fault injection (NetworkScenario --launch covers it)",
            "physical input, visual/Hebrew/audio quality on devices",
            "ordinary-build performance — validation endpoints are "
            "compiled out of ordinary builds, so no agent can attach",
        ],
    }
    peers = []
    samples = {}
    trace_dirs = {}
    ledger = RaceLedger()
    lost = {}
    dishonest_delivery = False
    try:
        assert 2 <= len(connection_paths) <= 4, \
            "--connections takes 2-4 connection descriptors"
        assert minutes > 0, "--minutes must be positive"
        for index, path in enumerate(connection_paths):
            role = "host" if index == 0 else "client-%d" % (index - 1)
            info = read_connection(path)
            peer_evidence = root / (role + "-evidence")
            peer_evidence.mkdir()
            write_json(peer_evidence / "attach.json", {
                "role": role, "connection": info["_source"],
                "validationDirectory": info["_directory"],
            })
            peer = LanSoakClient(info, peer_evidence)
            peers.append((role, peer))
            samples[role] = []
            trace_dirs[role] = info["_directory"]
            report["players"].append({
                "role": role, "runId": info["runId"],
                "connection": info["_source"],
                "validationDirectory": info["_directory"],
            })
        host = peers[0][1]
        clients = [peer for _, peer in peers[1:]]

        checks = enter_lobby(peers, host, clients, [],
                             host_address)
        report["checks"] = checks

        # The soak window starts once the session is assembled — lobby
        # setup time is not soak evidence.
        start = time.monotonic()
        deadline = start + minutes * 60.0
        cycle = 0
        while True:
            remaining = deadline - time.monotonic()
            if not should_start_cycle(remaining):
                break
            class_id = cycle_class(cycle)
            label = "cycle-%d" % cycle
            try:
                drive_race(host, clients, class_id, checks)
            except (AssertionError, KeyError) as error:
                report["incidents"].append({
                    "kind": "raceStartFailed", "cycle": cycle,
                    "class": class_id, "detail": str(error)[:300]})
                break
            race_deadline = min(deadline,
                                time.monotonic() + results_timeout)
            pending, newly_lost = await_results(
                [(role, peer) for role, peer in peers
                 if role not in lost],
                race_deadline, start, samples, report["incidents"],
                input_interval, sample_seconds, trace_dirs)
            lost.update(newly_lost)
            if pending:
                for role in pending:
                    report["incidents"].append({
                        "kind": "resultsTimeout", "role": role,
                        "cycle": cycle,
                        "detail": "no results phase within the "
                                  "race window"})
                break
            survivors = [(role, peer) for role, peer in peers
                         if role not in lost]
            summaries = {}
            try:
                for role, peer in survivors:
                    wait_state(peer, deliveries_settled,
                               DELIVERY_SETTLE_SECONDS,
                               role + " delivery settle")
                    summary = summarize_race(peer.state, role)
                    summary["role"] = role
                    ledger.record(summary, "%s %s" % (label, role))
                    summaries[role] = summary
            except (AssertionError, KeyError) as error:
                report["incidents"].append({
                    "kind": "resultLedger", "cycle": cycle,
                    "detail": str(error)[:300]})
                break
            verdicts = delivery_verdicts(summaries)
            report["cycles"].append({
                "cycle": cycle, "class": class_id,
                "elapsedSeconds": round(time.monotonic() - start, 1),
                "raceId": ledger.races[-1]["raceId"],
                "resultId": ledger.races[-1]["resultId"],
                "deliveries": ledger.races[-1]["deliveries"],
                "failedDeliveries": [
                    {"role": verdict["role"],
                     "participant": verdict["participant"],
                     "resultId": verdict.get("resultId"),
                     "code": verdict["code"],
                     "honest": verdict["honest"]}
                    for verdict in verdicts],
            })
            if record_delivery_verdicts(report, verdicts, cycle):
                dishonest_delivery = True
            checks.append("cycle %d: %s race completed with immutable "
                          "result %s" % (cycle, class_id,
                                         ledger.races[-1]["resultId"]))
            cycle += 1
            remaining = deadline - time.monotonic()
            if not should_start_cycle(remaining):
                break
            try:
                host.applied("lan.rematch")
                for role, peer in survivors:
                    wait_state(
                        peer,
                        lambda state: state["network"]["phase"] == "lobby",
                        30, role + " rematch lobby")
            except (AssertionError, KeyError) as error:
                report["incidents"].append({
                    "kind": "rematchFailed", "cycle": cycle,
                    "detail": str(error)[:200]})
                break

        report["wallSeconds"] = round(time.monotonic() - start, 1)
        report["racesCompleted"] = len(report["cycles"])

        # Teardown: host leave -> survivors land on recovery -> each
        # leaves honestly. Lost peers are skipped, not forced. A
        # `noSession` rejection is the NetworkScenario tolerance: a late
        # session close may already have expired the endpoint back to
        # the browser, so there is honestly nothing left to leave — an
        # already-clean exit, not a failure.
        survivors = [(role, peer) for role, peer in peers
                     if role not in lost]
        if survivors:
            try:
                result, _ = host.command("lan.leave")
                if result["status"] == "rejected":
                    assert result["code"] == "noSession", result
                else:
                    assert result["status"] == "applied", result
                wait_state(host,
                           lambda state: state["network"]["role"] == "none"
                           and state["screen"] == "lan-browser",
                           15, "host left")
                for role, peer in survivors:
                    if peer is host:
                        continue
                    wait_state(
                        peer,
                        lambda state: state["network"]["phase"]
                        in ("recovery", "idle"),
                        30, role + " recovery")
                    result, _ = peer.command("lan.leave")
                    if result["status"] == "rejected":
                        assert result["code"] == "noSession", result
                    else:
                        assert result["status"] == "applied", result
                    wait_state(
                        peer,
                        lambda state: state["network"]["role"] == "none",
                        15, role + " left")
                checks.append("teardown: host leave, client recovery, "
                              "clean leave (or honest noSession) on "
                              "every surviving endpoint")
            except (AssertionError, KeyError) as error:
                report["incidents"].append({
                    "kind": "teardownFailed",
                    "detail": str(error)[:200]})

        for role, peer in peers:
            if role in lost:
                report.setdefault("traces", {})[role] = {
                    "verified": False,
                    "reason": "peer stream lost mid-soak",
                }
                continue
            finish_capture(role, peer, report)
        checks.append("surviving captures finished; traces reconstruct; "
                      "phases legal; async commands completed once")

        report["devices"] = {}
        for role, peer in peers:
            device = {
                "healthSamples": len(samples[role]),
                "boundaries": peer.boundaries,
                "maxBacklogSteps": peer.max_backlog_steps,
                "cadence": cadence_stats(peer.boundary_times),
            }
            if samples[role]:
                device["lastHealth"] = samples[role][-1]
                device["outgoingRecordsPeak"] = max(
                    s.get("outgoingRecords") or 0
                    for s in samples[role])
            report["devices"][role] = device
            if role not in lost:
                assert peer.boundaries > 0, \
                    role + " produced no stream boundaries"
                assert peer.max_backlog_steps < MAX_STEPS_PER_FRAME, \
                    role + " backlog stayed saturated"

        status, error = soak_status(lost, dishonest_delivery)
        report["status"] = status
        if error is not None:
            report["error"] = error
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)
    finally:
        for role, peer in peers:
            try:
                peer.close()
            except Exception:
                pass
            write_json(root / (role + "-evidence") / "samples.json",
                       samples.get(role, []))
        if lost:
            report["lostPeers"] = sorted(lost)
        write_json(root / "result.json", report)
    return report


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--connections", type=Path, nargs="+", required=True,
        metavar="CONNECTION_JSON",
        help="2-4 connection.json descriptors of independently launched "
             "players; the first is driven as the host. Android players "
             "use per-device ADB-forwarded loopback descriptors.")
    parser.add_argument("--minutes", type=float, default=30.0,
                        help="sustained soak duration in minutes "
                             "(default 30)")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="NEW directory; existing directories are "
                             "refused")
    parser.add_argument("--host-address", default=None,
                        help="explicit host LAN address when broadcast "
                             "discovery cannot reach it")
    parser.add_argument("--results-timeout", type=float,
                        default=DEFAULT_RESULTS_TIMEOUT,
                        help="per-race results wait bound in seconds "
                             "(default 600)")
    parser.add_argument("--input-interval", type=float,
                        default=DEFAULT_INPUT_INTERVAL,
                        help="seconds between per-peer bounded input "
                             "refreshes (default 8)")
    parser.add_argument("--sample-seconds", type=float,
                        default=DEFAULT_SAMPLE_SECONDS,
                        help="seconds between per-peer health samples "
                             "(default 15)")
    return parser


def main():
    args = build_parser().parse_args()
    if not __debug__:
        raise RuntimeError(
            "Run without -O: scenario assertions must be enabled")
    report = run_soak(args.connections, args.evidence, args.minutes,
                      host_address=args.host_address,
                      results_timeout=args.results_timeout,
                      input_interval=args.input_interval,
                      sample_seconds=args.sample_seconds)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
