"""Sustained multi-device LAN soak on the opt-in physical backend.

Usage (physical qualification — the mandated interface):

    PhysicsSoak.py --connections <host-connection.json>
        <client-connection.json> [more connection.json files]
        --minutes 30 --evidence <new-directory>
        [--host-address <lan-ip>]

Attaches to 2-4 independently launched players via their run-directory
connection.json descriptors — the same contract as NetworkSoak.py — and
runs the complete lobby -> race -> results -> rematch cycle on the
authored physics-01 content for the requested wall-clock minutes, every
cycle on a REAL transport in REAL time (never stepped clocks, never
synthetic traffic, never a substituted peer).

Where this differs from the arcade soak — the physical observability
contract every surviving endpoint is held to on EVERY reconstructed
boundary while a physical race runs:

- Every racer session reports simulationBackend "physicalAssisted" and
  the same 64-hex canonical tuning fingerprint on every endpoint —
  a racer-level content-identity disagreement fails the run.
- The HOST's stream carries the full diagnostic MotorState.physics per
  racer (wheel arrays in authored index order, loads, slips, contact
  points) plus the shared world VehicleSimulationDiagnostics — exactly
  2 substeps per logical tick; a CLIENT's stream carries only the
  compact wire projection folded back onto the motor, and
  session.simulation + session.substepPath must be ABSENT there (the
  local-diagnostics exclusion the wire encoder enforces).
- Per-racer recoveryEpoch never regresses within a race identity and a
  race.recovered detail is always "reason#epoch" on this backend.
- Prediction exists client-side only; the host never predicts.

The immutable-result/delivery ledger, once-only once-per-rematch checks,
cadence and health sampling, lost-peer and hard-incident verdicts reuse
the arcade soak's standard unchanged: a lost participant or any hard
incident fails the run no matter how many races completed cleanly.

Player launches for this harness should raise the validation trace
bound: full physical diagnostics stream ~500 KB/s of boundary data, so
the default 256 MiB capture ceiling invalidates a >~9-minute run.
Launch each player with `--validation-trace-mb 1024` (or larger) for a
30-minute window — the configured bound is published on connection.json
as `traceLimitBytes` and `verify_lan_trace` measures against it.
"""

import argparse
import json
import sys
import time
from pathlib import Path

from AgentScenario import BOUNDARY_TYPES, StateReconstructor
from DrivingScenario import MAX_STEPS_PER_FRAME, OUTGOING_CAPACITY
from DrivingSoak import cadence_stats
from NetworkScenario import (finish_handshake, read_connection,
                             validate_lan_state, verify_lan_trace,
                             wait_state, wait_terminal)
from NetworkSoak import (RaceLedger, deliveries_settled,
                         delivery_verdicts, enter_lobby, health_sample,
                         record_delivery_verdicts, request_health,
                         should_start_cycle, soak_inputs, soak_status,
                         summarize_race, LanSoakClient,
                         DEFAULT_INPUT_INTERVAL, DEFAULT_RESULTS_TIMEOUT,
                         DEFAULT_SAMPLE_SECONDS, DELIVERY_SETTLE_SECONDS,
                         STALL_SECONDS)
from PhysicsScenario import (PhysicsContractError,
                             parse_recovery_detail,
                             validate_physics_state,
                             validate_simulation_diagnostics,
                             validate_substep_path)
from RestartScenario import write_json


# The physical event seats both classes; class selection stays on the
# content convention (car-physics-01 -> "car"), so the soak alternates
# classes per cycle exactly like the arcade soak.
PHYSICS_CLASS_EVENTS = {
    "car": ("physics-01", "car-physics-01"),
    "motorcycle": ("physics-01", "motorcycle-physics-01"),
}

# input.set's held-sample bound (FoundationService.MaxDurationTicks): a
# hold beyond 150 ticks is honestly rejected invalidParameter. The
# shared soak_inputs() now clamps the produced duration itself; the
# hold seconds are still clamped here so the *requested* interval never
# pretends a longer hold than the wire can carry.
MAX_HOLD_TICKS = 150
MAX_HOLD_SECONDS = MAX_HOLD_TICKS / 30.0

# Wire-rebuilt physics (client side) is contract-valid under the FULL
# validator — decode zeroes the diagnostic-only fields (contact points,
# normals, loads, slips) which remain finite and in-range. The real
# exclusion proof is the absence of session.simulation/substepPath.
# One shape is still malformed on a client: motor.physics non-null while
# racer.physics.present is false. The DTO's `present` flag is the only
# authority that may attach physical state on the wire — a surviving
# inline payload without it is the stripped-artifact shell leaking, not
# real content (an absent state decodes to motor.physics == null).


class LanPhysicsObserver:
    """Per-endpoint, per-race physical observability for LAN slices.

    apply() validates one reconstructed boundary's networkRace slice:
    racer backend identity, shared tuning fingerprint, per-participant
    recovery-epoch monotonicity, full-vs-wire physics by endpoint role,
    and the local-diagnostics exclusion on clients. reset() re-baselines
    on every new race identity — epoch history is per-race."""
    def __init__(self, is_host, seen_ids=None):
        self.is_host = is_host
        # Event ids are globally unique per session, so the seen set
        # survives reset() — and a trace replay shares one set across
        # per-race observers: a straggler event re-published on the
        # boundary where the race identity flips can never double-count
        # into the next race's recovery ledger.
        self._seen_event_ids = seen_ids if seen_ids is not None \
            else set()
        self.reset()

    def reset(self):
        self.boundaries = 0
        self.fingerprints = set()
        self.vehicle_ids = set()
        self.epochs = {}             # participantId -> last epoch
        self.overturned_peaks = {}   # participantId -> peak seconds
        self.airborne_peaks = {}     # participantId -> peak airPeakY
        self.recoveries = []         # (participantId, reason, epoch)
        self.prev_reasons = {}       # participantId -> last published reason
        self.events = {}             # event id -> entry (this race)
        self.diagnostics_seen = 0
        self.physics_racers = 0
        # Counters the rematch baseline check reads: host world roster
        # first/last, client prediction-history first/peak/last.
        self.bodies_first = None
        self.bodies_tail = None
        self.ticks_first = None
        self.ticks_tail = None
        self.history_first = None
        self.history_tail = None
        self.history_peak = 0

    def apply(self, state, label="networkRace"):
        # Every contract failure — whatever validator surfaced it — is a
        # PhysicsContractError so the soak's streamLost handling can
        # never mislabel a physics defect as a transport loss.
        try:
            return self._apply(state, label)
        except PhysicsContractError:
            raise
        except AssertionError as error:
            raise PhysicsContractError(str(error))

    def _apply(self, state, label="networkRace"):
        race = state.get("networkRace")
        if not isinstance(race, dict):
            return False
        # Race events ride the top-level drivingEvents window —
        # NetworkRaceBridge clears the internal race.events list before
        # publishing, so harvesting networkRace.events would record
        # nothing (the reason recoveryEvents read 0 on passing runs).
        for entry in state.get("drivingEvents") or []:
            if not isinstance(entry, dict):
                continue
            event_id = entry.get("id")
            if not event_id or event_id in self._seen_event_ids:
                continue
            self._seen_event_ids.add(event_id)
            self.events[event_id] = entry
            if entry.get("kind") == "race.recovered":
                reason, epoch = parse_recovery_detail(
                    entry.get("detail"))
                PhysicsContractError._raise_if(
                    epoch is None,
                    label + ": physical race.recovered without the "
                    "reason#epoch detail on " + repr(event_id))
                self.recoveries.append(
                    (entry.get("participantId"), reason, epoch))
        self.boundaries += 1
        for racer in race.get("racers") or []:
            session = racer.get("session") or {}
            motor = session.get("motor") or {}
            physics = motor.get("physics")
            backend = session.get("simulationBackend")
            pid = racer.get("participantId")
            if backend != "physicalAssisted":
                PhysicsContractError._raise_if(
                    physics is not None,
                    label + ": non-physical racer carried a physics "
                    "payload on " + repr(pid))
                continue
            self.physics_racers += 1
            fingerprint = session.get("contentFingerprint")
            PhysicsContractError._raise_if(
                not (type(fingerprint) is str
                     and len(fingerprint) == 64),
                label + ": physical racer missing the canonical "
                "fingerprint on " + repr(pid))
            self.fingerprints.add(fingerprint)
            PhysicsContractError._raise_if(
                len(self.fingerprints) > 1,
                label + ": racers disagree on the tuning fingerprint")
            self.vehicle_ids.add(racer.get("vehicleId"))
            if self.is_host:
                diagnostics = session.get("simulation")
                if diagnostics is not None:
                    validate_simulation_diagnostics(
                        diagnostics, label + ".simulation",
                        "physicalAssisted")
                    self.diagnostics_seen += 1
                    # World/body counters for the rematch baseline: a
                    # fresh world on each race must show the same body
                    # roster and restarted tick accounting — a leaking
                    # world would grow bodies or keep counting ticks.
                    if self.bodies_first is None:
                        self.bodies_first = diagnostics["bodies"]
                        self.ticks_first = \
                            diagnostics["ticksSimulated"]
                    self.bodies_tail = diagnostics["bodies"]
                    self.ticks_tail = diagnostics["ticksSimulated"]
                if physics is not None:
                    validate_physics_state(
                        physics, label + ".motor.physics")
                    validate_substep_path(session, motor,
                                          label + ".session")
            else:
                # The compact-wire exclusion, observed on every boundary:
                # local diagnostics (world counters, substep paths) must
                # never appear on a client endpoint.
                PhysicsContractError._raise_if(
                    session.get("simulation") is not None,
                    label + ": client observed session.simulation — "
                    "local diagnostics leaked to the wire")
                PhysicsContractError._raise_if(
                    bool(session.get("substepPath")),
                    label + ": client observed session.substepPath — "
                    "local path evidence leaked to the wire")
                if physics is not None:
                    # The compact DTO is the only wire path that may
                    # attach physical state: a non-null motor.physics
                    # without racer.physics.present means the stripped
                    # inline artifact survived decode instead of
                    # normalizing to null — no legitimate sender can
                    # produce that shape on this backend.
                    dto = racer.get("physics")
                    PhysicsContractError._raise_if(
                        not (isinstance(dto, dict)
                             and dto.get("present") is True),
                        label + ": client observed motor.physics on "
                        + repr(pid) + " without racer.physics.present "
                        "— stripped artifact leaked through the fold")
                    validate_physics_state(
                        physics, label + ".motor.physics")
            if physics is not None:
                epoch = physics["recoveryEpoch"]
                previous = self.epochs.get(pid)
                PhysicsContractError._raise_if(
                    previous is not None and epoch < previous,
                    "%s: %s recoveryEpoch regressed %r -> %r" % (
                        label, pid, previous, epoch))
                self.epochs[pid] = epoch
                self.overturned_peaks[pid] = max(
                    self.overturned_peaks.get(pid, 0.0),
                    physics["overturnedSeconds"])
                self.airborne_peaks[pid] = max(
                    self.airborne_peaks.get(pid, 0.0),
                    physics["airPeakY"])
            reason = session.get("lastRecoveryReason") or ""
            if pid and reason != self.prev_reasons.get(pid, ""):
                self.prev_reasons[pid] = reason
        if not self.is_host:
            # Client-side replay history is the rematch-baseline mirror
            # of the host's body counter: a fresh prediction per race
            # must start near zero rather than accumulating stale
            # snapshots across rematches.
            prediction = state.get("prediction")
            if isinstance(prediction, dict) \
                    and type(prediction.get("historyCount")) is int:
                history = prediction["historyCount"]
                if self.history_first is None:
                    self.history_first = history
                self.history_tail = history
                self.history_peak = max(self.history_peak, history)
        return True

    def metrics(self):
        return {
            "boundaries": self.boundaries,
            "fingerprints": sorted(self.fingerprints),
            "vehicleIds": sorted(self.vehicle_ids),
            "epochs": self.epochs,
            "overturnedPeaks": self.overturned_peaks,
            "airbornePeaks": self.airborne_peaks,
            "recoveryEvents": len(self.recoveries),
            "recoveries": [
                {"participantId": pid, "reason": reason, "epoch": epoch}
                for pid, reason, epoch in self.recoveries],
            "diagnosticsSeen": self.diagnostics_seen,
            "physicsRacerSamples": self.physics_racers,
            "bodiesFirst": self.bodies_first,
            "bodiesTail": self.bodies_tail,
            "ticksFirst": self.ticks_first,
            "ticksTail": self.ticks_tail,
            "historyFirst": self.history_first,
            "historyTail": self.history_tail,
            "historyPeak": self.history_peak,
        }


class PhysicsSoakClient(LanSoakClient):
    """Cadence-recording LAN client plus per-boundary physical
    validation. A contract violation raises PhysicsContractError (an
    AssertionError subclass) so the soak's streamLost handling can never
    mislabel a physics defect as a transport loss."""
    def __init__(self, connection, evidence, is_host):
        self.observer = LanPhysicsObserver(is_host)
        self.active_race = None
        self.race_metrics = []
        super().__init__(connection, evidence)

    def read(self, remaining):
        event = super().read(remaining)
        if event.get("type") in BOUNDARY_TYPES and self.state is not None:
            race = self.state.get("networkRace")
            race_id = race.get("raceId") if isinstance(race, dict) \
                else None
            if race_id != self.active_race:
                if self.observer.boundaries:
                    self.race_metrics.append(self.observer.metrics())
                self.active_race = race_id
                self.observer.reset()
            if isinstance(race, dict):
                self.observer.apply(self.state)
        return event

    def all_race_metrics(self):
        metrics = list(self.race_metrics)
        if self.observer.boundaries:
            metrics.append(self.observer.metrics())
        return metrics


# First-observed prediction history can lag a few seconds of snapshot
# backlog on a rematch boundary; a leaking accumulator at race N is
# hundreds of entries by then, so the baseline margin stays tight.
HISTORY_BASELINE_MARGIN = 60


def verify_rematch_baselines(physics_report):
    """World/body/history counters must return to baseline across
    rematches. Host: the body roster is stable inside each race and
    never grows past the first race's, and each new world's tick
    counter restarts below the previous world's tail (a persistent
    world would keep counting). Client: prediction history restarts
    near zero — accumulating snapshots across rematches is a leak.
    Returns violation strings; empty means every counter rebased."""
    violations = []
    for role, section in physics_report.items():
        races = section.get("races") or []
        if role == "host":
            roster = None
            for index, race in enumerate(races):
                first = race.get("bodiesFirst")
                tail = race.get("bodiesTail")
                if first is None or tail is None:
                    continue
                if first != tail:
                    violations.append(
                        "%s race %d: body roster drifted inside the "
                        "race (%s -> %s)" % (role, index, first, tail))
                if roster is None:
                    roster = tail
                elif tail > roster:
                    violations.append(
                        "%s race %d: body roster grew past the "
                        "baseline %s -> %s" % (role, index, roster,
                                               tail))
            for index in range(1, len(races)):
                prev_tail = races[index - 1].get("ticksTail")
                first = races[index].get("ticksFirst")
                if prev_tail is not None and first is not None \
                        and first > prev_tail:
                    violations.append(
                        "%s race %d: world tick counter did not "
                        "restart on rematch (first %s after a %s-tick "
                        "world)" % (role, index, first, prev_tail))
        else:
            baseline = None
            for index, race in enumerate(races):
                first = race.get("historyFirst")
                if first is None:
                    continue
                if baseline is None:
                    baseline = first
                elif first > baseline + HISTORY_BASELINE_MARGIN:
                    violations.append(
                        "%s race %d: prediction history did not "
                        "restart near baseline (first %s vs %s+%d)"
                        % (role, index, first, baseline,
                           HISTORY_BASELINE_MARGIN))
    return violations


def drive_physics_race(host, clients, class_id, checks):
    """Lobby -> racing for one physical class: configure physics-01,
    per-peer vehicle + ready, host start, then the physical
    observability assertions both endpoint roles must satisfy."""
    event_id, vehicle_id = PHYSICS_CLASS_EVENTS[class_id]
    host.applied("lan.configure",
                 {"eventId": event_id, "classId": class_id})
    host.applied("lan.vehicle", {"vehicleId": vehicle_id})
    for index, peer in enumerate(clients):
        # The host's configure applies on its own boundary; the client's
        # descriptor only reflects it once the session snapshot crosses
        # — a lan.vehicle sent earlier is honestly rejected
        # "notConfigured". Wait for the OBSERVED descriptor, never a
        # guessed delay.
        # classId too: alternating cycles keep eventId=physics-01 while
        # the class flips — a stale descriptor would still reject.
        wait_state(peer,
                   lambda state: (
                       lambda d: d.get("eventId") == event_id
                       and d.get("classId") == class_id)(
                           (state.get("network") or {})
                           .get("descriptor") or {}),
                   15, "client-%d configure propagation" % index)
        accepted, _ = peer.command("lan.vehicle",
                                   {"vehicleId": vehicle_id})
        assert accepted["status"] == "accepted", \
            "client %d lan.vehicle was not accepted: %r" % (
                index, accepted)
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
    peers = [("host", host, True)] \
        + [("client-%d" % i, c, False) for i, c in enumerate(clients)]
    for name, peer, is_host in peers:
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
            session = racer.get("session") or {}
            assert session.get("eventId") == event_id, \
                "%s: racer session is not on %s" % (name, event_id)
            assert session.get("simulationBackend") \
                == "physicalAssisted", \
                "%s: racer %s not on the physical backend" % (
                    name, racer.get("participantId"))
            fingerprint = session.get("contentFingerprint")
            assert type(fingerprint) is str \
                and len(fingerprint) == 64, \
                name + ": racer missing canonical fingerprint"
        if is_host:
            # The shared world diagnostics ride the host's stream.
            for racer in race["racers"]:
                diagnostics = (racer.get("session") or {}) \
                    .get("simulation")
                if diagnostics is not None:
                    validate_simulation_diagnostics(
                        diagnostics, name + ".simulation",
                        "physicalAssisted")
        else:
            # Clients carry only the compact projection: the local
            # diagnostics must be absent, and the racer physics the
            # wire restores stays contract-valid.
            for racer in race["racers"]:
                session = racer.get("session") or {}
                assert session.get("simulation") is None, \
                    name + ": session.simulation reached a client"
                physics = (session.get("motor") or {}).get("physics")
                if physics is not None:
                    validate_physics_state(
                        physics, name + ".motor.physics")
    assert host.state["prediction"] is None, \
        "the host never predicts its own racer"
    for index, peer in enumerate(clients):
        wait_state(peer,
                   lambda state: isinstance(state["prediction"], dict),
                   30, "client-%d prediction" % index)
        assert isinstance(
            peer.state["remotePresentation"]["racers"], list)
    checks.append("lan.start ran physics-01 into racing on %s; host "
                  "diagnostics live, client wire-exclusion proven"
                  % class_id)


def rejection_code(error):
    """The wire rejection code carried by an applied() AssertionError.

    applied() asserts ``result["status"] == "applied"`` with
    ``(action, result)`` as the assertion payload, so args[0][1] is the
    rejected result dict when the failure is a command rejection at
    all. Any other shape (a KeyError, an assertion from inside the
    transport pump) yields None and stays a hard incident."""
    args = getattr(error, "args", ())
    if args and isinstance(args[0], tuple) and len(args[0]) == 2 \
            and isinstance(args[0][1], dict):
        return args[0][1].get("code")
    return None


def await_physics_results(peers, deadline, start_monotonic, samples,
                          incidents, input_interval, sample_seconds,
                          trace_dirs):
    """await_results adapted for the physical soak: identical pacing and
    lost-peer semantics, but a PhysicsContractError from a client's
    per-boundary validation is a hard defect that must abort the run —
    never be mislabeled a transport loss."""
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
            phase = ((peer.state or {}).get("network") or {}) \
                .get("phase")
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
                except PhysicsContractError:
                    raise
                except (AssertionError, KeyError, OSError) as error:
                    incidents.append({
                        "kind": "streamLost", "role": role,
                        "elapsedSeconds": round(
                            now - start_monotonic, 1),
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
                        min(input_interval, MAX_HOLD_SECONDS)))
                    refreshes[role] += 1
                except PhysicsContractError:
                    raise
                except (AssertionError, KeyError) as error:
                    # The phase snapshot above is stale by up to one
                    # stream boundary: an endpoint that has already
                    # left the driving screen honestly refuses
                    # input.set "unavailable" (SetInput rejects before
                    # touching input state). That wire answer is the
                    # fresher truth, so it is recorded as an
                    # informational refusal — only a refusal while the
                    # endpoint still accepts driving input
                    # (invalidParameter, malformed durations, …) is a
                    # hard inputRejected incident.
                    kind = "inputRefused" \
                        if rejection_code(error) == "unavailable" \
                        else "inputRejected"
                    incidents.append({
                        "kind": kind, "role": role,
                        "elapsedSeconds": round(
                            now - start_monotonic, 1),
                        "phase": phase, "detail": str(error)[:200]})
                except OSError as error:
                    incidents.append({
                        "kind": "streamLost", "role": role,
                        "elapsedSeconds": round(
                            now - start_monotonic, 1),
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
            except PhysicsContractError:
                raise
            except AssertionError as error:
                if "closed" in str(error):
                    incidents.append({
                        "kind": "streamLost", "role": role,
                        "elapsedSeconds": round(
                            time.monotonic() - start_monotonic, 1),
                        "detail": str(error)[:200]})
                    lost[role] = pending.pop(role)
            except (OSError, TimeoutError) as error:
                incidents.append({
                    "kind": "streamLost", "role": role,
                    "elapsedSeconds": round(
                        time.monotonic() - start_monotonic, 1),
                    "detail": str(error)[:200]})
                lost[role] = pending.pop(role)
    return pending, lost


def verify_physics_lan_trace(directory, info, is_host):
    """verify_lan_trace plus a per-race physical replay: every LAN
    boundary the endpoint recorded is re-validated by role (full
    diagnostics on the host, wire projection on a client), and per-race
    observer metrics are returned for the report."""
    directory = Path(directory)
    outcome = verify_lan_trace(directory, info)
    reconstructor = StateReconstructor()
    observers = []
    seen_ids = set()
    observer = LanPhysicsObserver(is_host, seen_ids)
    active_race = None
    trace = directory / "state.ndjson"
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            assert line.endswith("\n"), "Incomplete trace record"
            state = reconstructor.apply(json.loads(line))
            if state is None:
                continue
            race = state.get("networkRace")
            race_id = race.get("raceId") if isinstance(race, dict) \
                else None
            if race_id != active_race:
                if observer.boundaries:
                    observers.append(observer)
                observer = LanPhysicsObserver(is_host, seen_ids)
                active_race = race_id
            if isinstance(race, dict):
                observer.apply(state, str(directory))
    if observer.boundaries:
        observers.append(observer)
    outcome["physicsRaces"] = [o.metrics() for o in observers]
    return outcome


def finish_physics_capture(name, peer, report, is_host):
    """Health + finish handshake, then the physical trace verification."""
    finish_handshake(peer)
    outcome = verify_physics_lan_trace(
        Path(peer.info["_directory"]), peer.info, is_host)
    assert not outcome["incompleteCommands"], \
        name + " left lan.* commands without a terminal outcome"
    report.setdefault("traces", {})[name] = outcome


def run_soak(connection_paths, evidence, minutes, host_address=None,
             results_timeout=DEFAULT_RESULTS_TIMEOUT,
             input_interval=DEFAULT_INPUT_INTERVAL,
             sample_seconds=DEFAULT_SAMPLE_SECONDS):
    """Attach to 2-4 launched players and soak the physical LAN lifecycle
    for the requested wall-clock minutes. Owns no processes."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "attached %d-player physical-backend LAN soak "
                 "(%.1f min requested)" % (len(connection_paths),
                                          minutes),
        "minutesRequested": minutes,
        "effectiveSettings": {
            "resultsTimeoutSeconds": results_timeout,
            "inputIntervalSeconds": input_interval,
            "sampleSeconds": sample_seconds,
        },
        "players": [], "checks": [], "cycles": [], "incidents": [],
        "notVerified": [
            "render frame times — cadence is the socket-observed "
            "boundary pacing proxy, not GPU timing",
            "cross-device reachability when descriptors share one host "
            "(loopback attach is same-machine evidence)",
            "credentialed rejoin after process death — the soak drives "
            "no fault injection (NetworkScenario --launch covers it)",
            "physical input, visual/Hebrew/audio quality on devices",
            "ordinary-build behavior — validation endpoints are "
            "compiled out of ordinary builds, so no agent can attach",
            "sustained physical cost headroom beyond the sampled "
            "window — diagnostics are observed, not load-tested",
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
            peer = PhysicsSoakClient(info, peer_evidence, index == 0)
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

        checks = []
        # enter_lobby appends to the list it is given and returns the
        # bound port — the checks list itself stays the evidence (the
        # arcade soak's `checks = enter_lobby(...)` binding is a latent
        # bug: it replaces the list with an int).
        host_port = enter_lobby(peers, host, clients, checks,
                                host_address)
        report["hostPort"] = host_port
        report["checks"] = checks

        # The soak window starts once the session is assembled — lobby
        # setup time is not soak evidence.
        start = time.monotonic()
        deadline = start + minutes * 60.0
        cycle = 0
        # A cycle may only start when the remaining window can plausibly
        # hold a complete physical race: the full results_timeout the
        # race is legally allowed to take, plus delivery settle and the
        # rematch transition. The shared 150 s floor fits arcade race
        # durations; physical races run ~4-8 minutes, so starting one
        # with less room guarantees a resultsTimeout hard incident on a
        # cycle that was doomed from its first tick.
        cycle_minimum = results_timeout + DELIVERY_SETTLE_SECONDS + 30.0
        while True:
            remaining = deadline - time.monotonic()
            if not should_start_cycle(remaining, cycle_minimum):
                break
            class_id = "car" if cycle % 2 == 0 else "motorcycle"
            label = "cycle-%d" % cycle
            try:
                drive_physics_race(host, clients, class_id, checks)
            except (AssertionError, KeyError) as error:
                report["incidents"].append({
                    "kind": "raceStartFailed", "cycle": cycle,
                    "class": class_id, "detail": str(error)[:300]})
                break
            race_deadline = min(deadline,
                                time.monotonic() + results_timeout)
            pending, newly_lost = await_physics_results(
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
                # Once-only bookkeeping is per cycle: every surviving
                # endpoint records its view of the same race next —
                # peer agreement on identity, fresh ids across cycles.
                ledger.begin_cycle()
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
            checks.append("cycle %d: physics %s race completed with "
                          "immutable result %s" % (
                              cycle, class_id,
                              ledger.races[-1]["resultId"]))
            cycle += 1
            remaining = deadline - time.monotonic()
            if not should_start_cycle(remaining, cycle_minimum):
                break
            try:
                host.applied("lan.rematch")
                for role, peer in survivors:
                    wait_state(
                        peer,
                        lambda state: state["network"]["phase"]
                        == "lobby",
                        30, role + " rematch lobby")
            except (AssertionError, KeyError) as error:
                report["incidents"].append({
                    "kind": "rematchFailed", "cycle": cycle,
                    "detail": str(error)[:200]})
                break

        report["wallSeconds"] = round(time.monotonic() - start, 1)
        report["racesCompleted"] = len(report["cycles"])

        # Teardown mirrors the arcade soak: host leave -> survivors land
        # on recovery -> each leaves honestly (or reports a noSession
        # that is an already-clean exit), lost peers never forced.
        survivors = [(role, peer) for role, peer in peers
                     if role not in lost]
        if survivors:
            try:
                if "host" not in lost:
                    result, _ = host.command("lan.leave")
                    if result["status"] == "rejected":
                        assert result["code"] == "noSession", result
                    else:
                        assert result["status"] == "applied", result
                    wait_state(
                        host,
                        lambda state: state["network"]["role"]
                        == "none"
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
                        lambda state: state["network"]["role"]
                        == "none",
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
            finish_physics_capture(role, peer, report, role == "host")
        checks.append("surviving captures finished; traces reconstruct; "
                      "physics contract held on every endpoint")

        report["devices"] = {}
        report["physics"] = {}
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
            report["physics"][role] = {
                "races": peer.all_race_metrics(),
            }
            if role not in lost:
                assert peer.boundaries > 0, \
                    role + " produced no stream boundaries"
                assert peer.max_backlog_steps < MAX_STEPS_PER_FRAME, \
                    role + " backlog stayed saturated"

        # Rematch baseline: bodies, world ticks and prediction history
        # must all rebase on each new race identity — growth is a leak.
        baseline_violations = verify_rematch_baselines(
            report["physics"])
        for violation in baseline_violations:
            report["incidents"].append({
                "kind": "rematchBaseline", "detail": violation})
        if not baseline_violations:
            total_races = sum(
                len((report["physics"][role] or {}).get("races") or [])
                for role in report["physics"])
            checks.append("rematch baseline: bodies/ticks/history "
                          "counters rebased across %d observed race "
                          "identities" % total_races)

        status, error = soak_status(lost, dishonest_delivery,
                                    report["incidents"])
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
             "physical-backend players; the first is driven as the "
             "host.")
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
