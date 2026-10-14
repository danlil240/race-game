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
sequencing, schema-4 boundary validity, reconstructable snapshot/delta
boundaries, async command/terminal-outcome correlation, honest lifecycle
phases, predicted-vs-authoritative separation, unique reward rows, and no
credential or transport-secret leakage. State predicates with bounded
timeouts drive every step; there is no teleporting or forced finishing.
"""

import argparse
import json
import math
import subprocess
import sys
import time
import traceback
import uuid
from pathlib import Path

from AgentScenario import (BOUNDARY_TYPES, Client, StateReconstructor,
                           SUPPORTED_STATE_SCHEMAS,
                           validate_schema5_presentation, verify_trace)
from RestartScenario import file_hash, wait_connection, write_json


# The observation schema this scenario requires — mirrors
# NetworkSettings.StateSchemaVersion in the pinned build. Schema 5 (M4)
# adds the additive render-profile/performance presentation slice on top
# of the schema-4 protocol-2 epoch (physical LAN replication, content
# fingerprints on descriptors/discovery, P1 gameplay envelopes).
# Historical schema-3/4 evidence is not valid proof of a current build.
REQUIRED_LAN_SCHEMA = 5

# The schema-3+ session phases in their legal neighbourhood graph. A phase
# may repeat or move only along an edge the session policy permits; any
# other hop in a reconstructed boundary history means the emitted state
# disagrees with the lifecycle contract.
LEGAL_PHASE_TRANSITIONS = {
    # idle -> reconnecting is real: a restarted process whose credential
    # store still holds session material issues lan.reconnect straight
    # from the browser — the fault-injection relaunch does exactly this.
    "idle": {"idle", "discovering", "connecting", "lobby",
             "reconnecting"},
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
    """Schema-4 network-slice shape a reconstructed boundary must carry."""
    assert state.get("stateSchemaVersion") == REQUIRED_LAN_SCHEMA, \
        "LAN evidence requires schema %d" % REQUIRED_LAN_SCHEMA
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
        and "remotePresentation" in state \
        and "networkLink" in state
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

    # Schema 5: LAN evidence at this schema must also carry the additive
    # presentation slice — the render profile this endpoint actually
    # applied and its bounded performance summary contract.
    validate_schema5_presentation(state)

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
        and predicate(client.state), timeout=timeout,
        on_event=lambda event: pump_drivers())


def wait_terminal(client, command_id, timeout=30):
    """The accepted result arrives first; the terminal outcome follows."""
    return client.wait(
        lambda event: event.get("type") == "result"
        and event.get("commandId") == command_id
        and event.get("status") in TERMINAL_STATUSES,
        timeout=timeout,
        on_event=lambda event: pump_drivers())


# --- human-racer driving ---------------------------------------------------
#
# LAN racers auto-accelerate but never steer themselves: a connected human
# seat that receives no input drives straight into a wall and DNFs, and a
# DNF participant's reward is honestly ineligible — the delivery ledger can
# never settle "persisted" unless someone actually drives the car. The
# scenario therefore drives every attached human racer through the ordinary
# bounded input.set hold path: a proportional-steering follower over the
# authored graybox-01 racing line, plus the same recover one-shot a player
# presses when beached. No teleports, no forced finishes, no privileged
# input — every sample rides the validated command surface.
#
# The route below is the AI racing line authored in
# Assets/FamilyRacing/Editor/NetworkPrototypeSetup.cs (LanRoutePoints):
# north to gate-1, west over the landmark ramp, around IslandNorthEast to
# gate-2, down the east edge to gate-3, under IslandSouthEast to gate-4,
# then south of the line back across start/finish. A course layout change
# must move this table with it.
LAN_ROUTE = [
    (0.0, -30.0), (0.0, 10.0), (0.0, 30.0), (-30.0, 24.0),
    (-54.0, 14.0), (-60.0, 30.0), (-60.0, 46.0), (-40.0, 58.0),
    (-10.0, 62.0), (20.0, 62.0), (48.0, 60.0), (62.0, 55.0),
    (82.0, 42.0), (95.0, 18.0), (95.0, -14.0), (99.0, -34.0),
    (92.0, -48.0), (68.0, -60.0), (38.0, -56.0), (10.0, -62.0),
    (0.0, -66.0), (0.0, -48.0),
]
# Authored graybox-01 gates (graybox-01.asset), in checkpoint order:
# centre plus the crossing direction. A route waypoint only passes near
# a gate's plane, so close approaches aim THROUGH the plane — a point
# past the centre in the travel direction — because the crossing only
# counts inside the half-width and in the authored direction.
LAN_GATES = [
    (0.0, -60.0), (0.0, 25.0), (55.0, 55.0), (95.0, 0.0), (50.0, -55.0),
]
LAN_GATE_DIRS = [
    (0.0, 1.0), (0.0, 1.0), (1.0, 0.0), (0.0, -1.0), (-1.0, 0.0),
]
LAN_GATE_APPROACH = 22.0   # inside this radius, aim at the gate centre
LAN_LOOKAHEAD = 11.0       # pure-pursuit aim distance along the route
LAN_ROUTE_LENGTH = sum(
    math.hypot(LAN_ROUTE[i][0] - LAN_ROUTE[i - 1][0],
               LAN_ROUTE[i][1] - LAN_ROUTE[i - 1][1])
    for i in range(len(LAN_ROUTE)))
# Cumulative arclen of each route vertex (LAN_ROUTE_ARC[i] is the arclen
# of LAN_ROUTE[i]); the loop closes back to 0 at LAN_ROUTE_LENGTH.
LAN_ROUTE_ARC = [0.0]
for _i in range(1, len(LAN_ROUTE)):
    LAN_ROUTE_ARC.append(
        LAN_ROUTE_ARC[-1]
        + math.hypot(LAN_ROUTE[_i][0] - LAN_ROUTE[_i - 1][0],
                     LAN_ROUTE[_i][1] - LAN_ROUTE[_i - 1][1]))
# Follower constants mirror the host's AiDriver (NetworkSettings.cs):
# the scenario steers with the same reach/passed/gain the fill-in AI
# proves is enough to complete laps on this course.
LAN_ROUTE_REACH = 5.0        # AiWaypointReachMeters
LAN_ROUTE_PASSED = 3.0       # AiWaypointPassedFactor
# Gain is deliberately lower than the AI's 3.0: the AI re-evaluates every
# sim tick, while a remote driver's hold spans multiple ticks — full-lock
# holds at high gain bang-bang into orbits that never converge. The solo
# driving scenario's proven 1.4 sits in the stable range.
LAN_STEER_GAIN = 1.4
LAN_BOOST_MAX_STEER = 0.2    # AiBoostMaxSteer
LAN_BOOST_MIN_CHARGE = 0.9   # AiBoostMinCharge
LAN_STALL_TICKS = 90         # ~3s of sim time without progress -> recover
LAN_MAX_RECOVERIES = 12      # per race; more is an honest driving defect


def wrap_angle(radians):
    while radians > math.pi:
        radians -= 2.0 * math.pi
    while radians <= -math.pi:
        radians += 2.0 * math.pi
    return radians


def steer_command(motor, target_x, target_z, gain=LAN_STEER_GAIN):
    """Bounded proportional steering toward a world waypoint: -1..1."""
    desired = math.atan2(target_x - motor["x"], target_z - motor["z"])
    error = wrap_angle(desired - motor["heading"])
    return max(-1.0, min(1.0, error * gain))


class LanDriver:
    """One racer's pure-pursuit follower over the shared input.set
    surface.

    Reads only the state every participant can see (its own racer's
    replicated session: motor pose, simulation tick, recovering flag) and
    emits held steering samples exactly like DrivingScenario's solo
    driver. The aim point is a lookahead along the authored route's
    closest point — projection+lookahead is the stable pursuit under
    remote-input lag where waypoint chasing bang-bangs into orbits. A
    genuine stall presses recover like a human would — bounded by
    LAN_MAX_RECOVERIES per race — never a guessed sleep or a teleport.
    """

    HOLD_TICKS = 8
    RESEND_TICKS = 3    # boundary ticks; always resend before lease-out
    CORRECTION = 0.08   # resend earlier when steering moved this far
    # Command identities are finite (FoundationService.IdentityCapacity
    # = 10000, one per result — rejections included): a thirty-minute
    # driven benchmark at the old cadence burns tens of thousands of
    # input.set identities and every subsequent command is rejected
    # commandCapacity. The follower therefore enforces a minimum
    # sim-tick gap between sends and holds each lease long enough to
    # cover it — steering corrections land within ~1/3 s, the same
    # order as a human's reaction on a laggy link.
    MIN_SEND_TICKS = 10

    def __init__(self, peer, remote=False):
        self.peer = peer
        # A remote endpoint steers on state the wire already aged a few
        # sim ticks (15 Hz snapshots plus input transit), and every held
        # input keeps rotating the car for its whole duration — a strong
        # correction held through that lag overshoots into a full-lock
        # flip-flop that orbits the aim point. Remote drivers therefore
        # steer with much softer gain, a longer lookahead, and a shorter
        # hold so no single stale sample can spin the car.
        self.lookahead = LAN_LOOKAHEAD * 1.4 if remote else LAN_LOOKAHEAD
        self.gain = LAN_STEER_GAIN * (0.43 if remote else 1.0)
        self.hold_ticks = max(4 if remote else self.HOLD_TICKS,
                              self.MIN_SEND_TICKS + 4)
        self.resend_ticks = 2 if remote else self.RESEND_TICKS
        self.min_send_ticks = self.MIN_SEND_TICKS
        # Pose extrapolation from observed heading+speed compensates the
        # same wire age: aim at where the car IS now, not where the
        # snapshot left it — purely the replicated motor's own fields.
        self.lead_meters = 2.4 if remote else 0.0
        self.race_id = None
        self.held = None
        self.held_boost = False
        self.held_at = -10 ** 9
        self.recoveries = 0
        self.best_progress = None
        self.progress_at = 0
        self.catchup_credit = 0.0
        self.unwrapped = None
        self.prev_arc = None
        self.send_ok = True
        self.calm_until = 0
        # While True the driver observes the state stream but sends no
        # input: the scenario suspends a peer's driver around the
        # observable-input assertions (bounds rejection, held-input
        # normalization, recover ack) so driver resends cannot overwrite
        # the value under test before the assertion window closes.
        self.suspended = False

    def _racer(self):
        """This endpoint's own racer while a LAN race is live, else None."""
        state = self.peer.state
        if not isinstance(state, dict):
            return None
        network = state.get("network") or {}
        race = state.get("networkRace")
        if race is None or network.get("phase") != "racing":
            return None
        own = network.get("localParticipantId")
        for racer in race.get("racers") or []:
            if racer.get("participantId") == own:
                return racer
        return None

    @staticmethod
    def _projection(motor):
        """Closest point on the route loop: (arclen, seg_index, t)."""
        best = None
        count = len(LAN_ROUTE)
        arc = 0.0
        for index in range(count):
            ax, az = LAN_ROUTE[index - 1]
            bx, bz = LAN_ROUTE[index]
            ex, ez = bx - ax, bz - az
            seg2 = ex * ex + ez * ez
            t = 0.0 if seg2 == 0 else max(0.0, min(1.0,
                ((motor["x"] - ax) * ex + (motor["z"] - az) * ez) / seg2))
            px, pz = ax + ex * t, az + ez * t
            d2 = (px - motor["x"]) ** 2 + (pz - motor["z"]) ** 2
            if best is None or d2 < best[0]:
                best = (d2, arc + t * math.sqrt(seg2), index)
            arc += math.sqrt(seg2)
        return best[1], best[2]

    @staticmethod
    def _point_along(arclen):
        """The route point `arclen` metres along the loop."""
        total = LAN_ROUTE_LENGTH
        arclen = arclen % total
        count = len(LAN_ROUTE)
        arc = 0.0
        for index in range(count):
            ax, az = LAN_ROUTE[index - 1]
            bx, bz = LAN_ROUTE[index]
            seglen = math.hypot(bx - ax, bz - az)
            if arclen <= arc + seglen and seglen > 0:
                t = (arclen - arc) / seglen
                return ax + (bx - ax) * t, az + (bz - az) * t
            arc += seglen
        return LAN_ROUTE[-1]

    @staticmethod
    def _corner_deflection(vertex_index):
        """Heading change (radians) at route vertex `vertex_index`."""
        count = len(LAN_ROUTE)
        px, pz = LAN_ROUTE[vertex_index - 1]
        vx, vz = LAN_ROUTE[vertex_index]
        nx, nz = LAN_ROUTE[(vertex_index + 1) % count]
        return abs(wrap_angle(
            math.atan2(nx - vx, nz - vz)
            - math.atan2(vx - px, vz - pz)))

    def _lookahead(self, arc, seg_index):
        """Effective lookahead from the projected route position.

        A fixed lookahead cuts every corner — the aim point sits on the
        far leg, the car travels straight toward it, and the inside of
        the corner (the landmark ramp's west edge, the island corners)
        becomes a wall it beaches on. When a sharp vertex lies inside
        the lookahead window, clamp the aim to just past that vertex so
        the car genuinely reaches the corner before steering beyond it;
        on shallow corners keep the full pursuit distance."""
        vertex_arc = LAN_ROUTE_ARC[seg_index]
        gap = vertex_arc - arc            # metres of route to the corner
        if 0 <= gap < self.lookahead \
                and self._corner_deflection(seg_index) > 0.7:
            # Aim essentially AT the corner: a clamp beyond the vertex
            # (gap + margin > lookahead) silently lengthens the pursuit
            # distance right where it must shorten, and the chord again
            # cuts across the inside wall of the hairpin.
            return max(3.0, min(self.lookahead, gap + 0.5))
        return self.lookahead

    def _send(self, steering, boost=False, recover=False):
        frame = {
            "type": "command", "protocolVersion": 1,
            "runId": self.peer.info["runId"],
            "commandId": str(uuid.uuid4()),
            "participantId": "local", "action": "input.set",
            "parameters": {
                key: (str(value).lower() if isinstance(value, bool)
                      else str(value))
                for key, value in lan_inputs(
                    steering=steering, duration=self.hold_ticks,
                    boost=boost, recover=recover).items()},
        }
        try:
            self.peer.send(frame)
        except OSError:
            self.send_ok = False

    def pump(self):
        """Re-evaluate observed state and issue at most one fresh hold."""
        if not self.send_ok or self.suspended:
            return
        racer = self._racer()
        if (racer is None or racer.get("finished") or racer.get("dnf")):
            return
        session = racer.get("session") or {}
        motor = session.get("motor")
        if not isinstance(motor, dict):
            return
        if self.lead_meters:
            # Steer against the extrapolated pose: the wire-aged motor
            # under-reads where a moving car already is.
            heading = motor.get("heading") or 0.0
            motor = dict(motor)
            motor["x"] = motor["x"] + math.sin(heading) * self.lead_meters
            motor["z"] = motor["z"] + math.cos(heading) * self.lead_meters
        sim_tick = session.get("simulationTick") or 0
        if session.get("raceId") != self.race_id:
            self.race_id = session.get("raceId")
            self.recoveries = 0
            self.best_progress = None
            self.progress_at = sim_tick
            self.catchup_credit = 0.0
            self.unwrapped = None
            self.prev_arc = None
            self.held = None
            self.calm_until = 0
        recovering = motor.get("recovering") is True
        # Progress = laps plus forward-unwrapped arclen. The spawn slots
        # sit beside the route's final segment, so a raw projected arclen
        # starts near the route END and the car's first-lap arc can never
        # beat that false frontier — every honest lap would read as one
        # giant stall. Unwrapping the per-sample arc delta measures only
        # route distance actually driven: it survives the wrap, the long
        # detours lap/gate metrics cannot express, and the loops
        # position-churn hides.
        arc, seg_index = self._projection(motor)
        if self.prev_arc is None:
            self.unwrapped = 0.0
        else:
            da = arc - self.prev_arc
            half = LAN_ROUTE_LENGTH * 0.5
            while da > half:
                da -= LAN_ROUTE_LENGTH
            while da <= -half:
                da += LAN_ROUTE_LENGTH
            self.unwrapped += da
        self.prev_arc = arc
        progress = (session.get("lap") or 0) * LAN_ROUTE_LENGTH \
            + self.unwrapped
        if self.best_progress is None or progress > self.best_progress:
            self.best_progress = progress
            self.progress_at = sim_tick
            self.catchup_credit = 0.0
        else:
            # A respawn far behind the best mark earns catch-up time in
            # the same metres. The budget ratchets UP to the worst gap
            # since the frontier — never down: the old formula re-read
            # the live gap every pump, so the credit decayed exactly as
            # the car closed on its frontier and the driver recovered a
            # moving car metres short of the mark, respawned it, and
            # repeated the cycle deterministically until
            # LAN_MAX_RECOVERIES ran out.
            gap_now = self.best_progress - progress
            if gap_now * 3.0 > self.catchup_credit:
                self.catchup_credit = gap_now * 3.0
        # A gate plane the crossing rule keeps rejecting costs a
        # turn-around cycle, so the expected gate's approach radius buys
        # a little more.
        allowed = LAN_STALL_TICKS + self.catchup_credit
        gate_index = session.get("nextCheckpoint")
        if isinstance(gate_index, int) and 0 <= gate_index < len(LAN_GATES):
            gx, gz = LAN_GATES[gate_index]
            if math.hypot(gx - motor["x"], gz - motor["z"]) \
                    <= LAN_GATE_APPROACH:
                allowed += 60
        # Simulation ticks freeze during a shared pause, so a paused race
        # can never read as stalled.
        if (motor.get("grounded") and not recovering
                and sim_tick - self.progress_at >= allowed):
            self.recoveries += 1
            assert self.recoveries <= LAN_MAX_RECOVERIES, \
                "LAN racer needed more than %d recoveries in one race" \
                % LAN_MAX_RECOVERIES
            self.progress_at = sim_tick
            # Roll out of the respawn straight for ~1.5 s before
            # resuming pursuit — the safe pose needs distance to
            # straighten, and an immediate full-lock correction is how
            # a recovered car re-beaches inside the same corner.
            self.calm_until = sim_tick + 45
            self._send(0.0, recover=True)
            self.held = None
            return
        # Post-recovery rollout: the respawn just landed — hold neutral
        # steering so the car travels straight out of the safe pose
        # instead of re-entering the same wall under full pursuit.
        if sim_tick < self.calm_until:
            steering, boost = 0.0, False
            tick = self.peer.last_state_tick or 0
            stale = (self.held is None
                     or tick >= self.held_at + self.resend_ticks)
            if stale and tick >= self.held_at + self.min_send_ticks:
                self._send(steering)
                self.held = steering
                self.held_boost = boost
                self.held_at = tick
            return
        # Pure pursuit: aim at a lookahead point along the route, not at
        # a waypoint — held inputs over remote lag stay stable. Sharp
        # corners shorten the lookahead so the aim cannot cut across the
        # inside of the turn.
        eff_lookahead = self._lookahead(arc, seg_index)
        tx, tz = self._point_along(arc + eff_lookahead)
        # Lane offset perpendicular to the route direction at the car's
        # projection, exactly the AI's LaneFor(slot) fan-out, so four
        # followers do not shove each other off the line. Through a
        # clamped sharp corner the offset lane is narrower than the
        # route corridor — a fan aimed beside the route is what beaches
        # on the inside barrier, so tight corners drive the centreline.
        ax, az = LAN_ROUTE[seg_index - 1]
        bx, bz = LAN_ROUTE[seg_index]
        sx, sz = bx - ax, bz - az
        length = math.hypot(sx, sz)
        lane = ((racer.get("slot") or 0) - 1.5) * 2.0
        if eff_lookahead < self.lookahead:
            lane = 0.0
        if length > 0.001 and lane != 0.0:
            tx -= sz / length * lane
            tz += sx / length * lane
        # Final approach: the plane only counts a forward crossing in
        # the travel direction inside its half-width, so near the
        # expected gate aim THROUGH it — a point past the centre — never
        # at the centre itself, which would park the car parallel on the
        # line. If a crossing missed (the car ended up on the far side
        # uncrossed), aim back through to the approach side and retry.
        # The flip boundary sits ON the plane: a margin away from it
        # turns into a wall the car orbits without ever crossing —
        # flipping exactly past the plane makes every yo-yo a real
        # crossing attempt instead.
        if isinstance(gate_index, int) and 0 <= gate_index < len(LAN_GATES):
            gx, gz = LAN_GATES[gate_index]
            rx, rz = motor["x"] - gx, motor["z"] - gz
            if math.hypot(rx, rz) <= LAN_GATE_APPROACH:
                dx, dz = LAN_GATE_DIRS[gate_index]
                along = rx * dx + rz * dz
                if along <= 0.5:
                    tx, tz = gx + dx * 10.0, gz + dz * 10.0
                else:
                    tx, tz = gx - dx * 10.0, gz - dz * 10.0
        steering = round(steer_command(motor, tx, tz, gain=self.gain), 3)
        boost = (abs(steering) <= LAN_BOOST_MAX_STEER
                 and (motor.get("boostCharge") or 0.0)
                 >= LAN_BOOST_MIN_CHARGE)
        tick = self.peer.last_state_tick or 0
        stale = (self.held is None or self.held_boost != boost
                 or tick >= self.held_at + self.resend_ticks)
        moved = self.held is None or abs(steering - self.held) >= self.CORRECTION
        if (stale or moved) \
                and tick >= self.held_at + self.min_send_ticks:
            self._send(steering, boost=boost)
            self.held = steering
            self.held_boost = boost
            self.held_at = tick


_LAN_DRIVERS = []


def pump_drivers():
    """Give every registered human racer one steering re-evaluation.

    Called from the shared wait path so long waits on one endpoint keep
    the other humans' cars honestly driven instead of wall-pinned."""
    for driver in list(_LAN_DRIVERS):
        driver.pump()


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
    schema-4 race assertions both endpoints must satisfy."""
    event_id, vehicle_id = CLASS_EVENTS[class_id]
    host.applied("lan.configure",
                 {"eventId": event_id, "classId": class_id})
    host.applied("lan.vehicle", {"vehicleId": vehicle_id})
    for index, peer in enumerate(clients):
        # The client's mirrored descriptor lags the host's configure by
        # a session broadcast; a vehicle pick processed while the lobby
        # still shows the previous event's class is rejected
        # invalidVehicle — exactly what a human sees too. Wait for the
        # client to observe the new class before it selects.
        wait_state(peer,
                   lambda state, cid=class_id:
                       (((state.get("network") or {}).get("descriptor")
                         or {}).get("classId") == cid),
                   20, "client-%d lobby class" % index)
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
        # The session phase and the finished-race snapshot are separate
        # wire facts: a client can see "results" a boundary before the
        # final authoritative race slice lands, so the wait must cover
        # both rather than asserting a snapshot that is still arriving.
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "results"
                   and state["networkRace"] is not None
                   and state["networkRace"]["phase"] == "finished",
                   600, name + " results")
        state = peer.state
        assert state["screen"] == "lan-results", name
        race = state["networkRace"]
        assert race.get("result") is not None, "immutable result missing"
        assert state["network"]["results"], \
            "delivery ledger missing on " + name

    def deliveries_settled(state):
        states = [row["state"] for row in state["network"]["results"]]
        return all(value in ("persisted", "failed") for value in states)
    for name, peer in peers:
        wait_state(peer, deliveries_settled, 60, name + " delivery settle")
        rows = peer.state["network"]["results"]
        # A failed row is honest only when its code is a permanent
        # ineligible verdict (e.g. ineligible:dnf — the delivered result
        # was applied but the racer earned no reward by rule). Any other
        # failure code is a retryable persistence fault the scenario
        # must not wave through.
        bad = [row for row in rows
               if row["state"] == "failed"
               and not (row.get("code") or "").startswith("ineligible:")]
        assert not bad, \
            name + " delivery failed for a recoverable reason: " \
            + repr(bad)
        for row in rows:
            if row["state"] != "failed":
                continue
            # Cross-check the verdict against the immutable result when
            # the row belongs to the live race: an ineligible:dnf row
            # must belong to a racer the result itself records as dnf —
            # a code that contradicts the result is a ledger bug, not
            # an honest outcome. Rows retained from earlier races no
            # longer have their result in the live race slice, so only
            # the ineligible-prefix requirement above covers them.
            race = peer.state["networkRace"] or {}
            if row.get("raceId") != race.get("raceId"):
                continue
            outcome = next(
                (r.get("outcome") for r in
                 ((race.get("result") or {}).get("racers") or [])
                 if r.get("participantId") == row["participantId"]),
                None)
            assert row.get("code") == "ineligible:" + str(outcome), \
                name + " failed delivery code %r does not match the " \
                "result outcome %r" % (row.get("code"), outcome)
    checks.append("results phase: immutable result, delivery ledger "
                  "settled persisted (or honest ineligible verdicts) "
                  "on every survivor")


def run_lifecycle(host, clients, checks, join_address=None,
                  allow_loopback=False, fault=None):
    """Drive the complete LAN lifecycle across all attached players.

    `fault` (launch mode only) supplies {"kill": client->None,
    "relaunch": client->Client} hooks for the labeled fault-injection
    segment: process death exercises real AI takeover and credentialed
    rejoin instead of simulated disconnects."""
    peers = [("host", host)] + [("client-%d" % i, c)
                               for i, c in enumerate(clients)]
    # Every attached endpoint owns a waypoint follower: its samples ride
    # the same bounded input.set hold surface a human uses, so idle cars
    # cannot wall-pin into an honest DNF while waits run elsewhere.
    for name, peer in peers:
        _LAN_DRIVERS.append(LanDriver(peer, remote=name != "host"))
    for name, peer in peers:
        assert peer.state["activeSlot"] == -1 \
            and peer.state["screen"] == "profiles", name
        assert peer.hello.get("stateSchemaVersion") == REQUIRED_LAN_SCHEMA, \
            name + " did not advertise schema %d" % REQUIRED_LAN_SCHEMA
        validate_lan_state(peer.state)
        peer.applied("profile.create", {"slot": 0, "language": "en"})
        peer.applied("ui.navigate", {"screen": "lan-browser"})
        assert peer.state["screen"] == "lan-browser", name
    checks.append("%d instances on the LAN browser with schema-%d slices"
                  % (len(peers), REQUIRED_LAN_SCHEMA))

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

    # Observable-input assertions read the endpoint's own input slice,
    # which every input.set rewrites — including the driver's resends.
    # Suspend the client driver's sends for exactly this block so the
    # held-input window under test cannot be overwritten mid-wait. The
    # driver resumes before the shared-pause segment: pause.toggle rides
    # its own action path and the driver's neutral pause field is inert.
    first_driver = next(d for d in _LAN_DRIVERS if d.peer is first_client)
    first_driver.suspended = True
    try:
        # Input bounds and normalization: an out-of-range sample is
        # refused wholesale, and a held input auto-expires after its
        # duration — the observable steering returns to neutral without
        # another command.
        rejected, _ = first_client.command(
            "input.set", lan_inputs(steering=2, duration=5))
        assert rejected["status"] == "rejected" \
            and rejected["code"] == "invalidParameter", rejected
        first_client.applied("input.set",
                             lan_inputs(steering=1, duration=10))
        assert first_client.state["input"]["steering"] == 1
        wait_state(first_client,
                   lambda state: state["input"]["steering"] == 0,
                   15, "held input normalized back to neutral")
        checks.append("bounded input: out-of-range refused, hold "
                      "normalized to neutral after durationTicks")

        # Both cameras: the toggle flips the draft preference and the
        # applied presentation camera facts follow on the next rendered
        # frame.
        assert first_client.state["draftProfile"]["camera"] == "chase"
        first_client.applied("camera.toggle")
        wait_state(first_client,
                   lambda state: state["presentation"]["camera"]["mode"]
                   == "overhead", 15, "overhead camera applied")
        first_client.applied("camera.toggle")
        wait_state(first_client,
                   lambda state: state["presentation"]["camera"]["mode"]
                   == "chase", 15, "chase camera applied")
        checks.append(
            "both cameras applied through observable camera facts")

        # Bounded input and the deduplicated one-shot action on the
        # client: the recover press rides the action path, so action.*
        # events must acknowledge it on the client's observable event
        # stream.
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
    finally:
        first_driver.suspended = False

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
        _LAN_DRIVERS.append(LanDriver(rejoined, remote=True))
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
        # The failed reconnect may already have expired the client back
        # to the browser (join timeout drops a dead session to idle), in
        # which case lan.leave honestly reports there is nothing left to
        # leave; otherwise the explicit leave performs the exit. Either
        # way the endpoint must finish on the browser with no session.
        result, _ = peer.command("lan.leave")
        if result["status"] == "rejected":
            assert result["code"] == "noSession", result
        else:
            assert result["status"] == "applied", result
        wait_state(peer,
                   lambda state: state["network"]["role"] == "none"
                   and state["screen"] == "lan-browser",
                   15, "client left")
    checks.append("lan.leave (or honest noSession after the failed "
                  "reconnect) returned every endpoint to the browser")
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
    finish_handshake(peer)
    verify_trace_into(name, peer, report)


def finish_handshake(peer):
    """Ask one endpoint for its health/finish records; waits drain the
    peer sockets, so every peer stays fed while this one is awaited."""
    peer.send({"type": "health"})
    health = peer.wait(lambda event: event.get("type") == "health")
    assert health["captureValid"], health
    peer.send({"type": "finish"})
    finished = peer.wait(lambda event: event.get("type") == "finished")
    assert finished["captureValid"], finished


def verify_trace_into(name, peer, report):
    """Replay the endpoint's captured stream into the shared traces map."""
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
        # The report's own checks list is handed in so a mid-lifecycle
        # failure still records every checkpoint that did pass — an
        # empty list would misreport a late failure as "nothing ran".
        run_lifecycle(
            host, rest, report["checks"],
            join_address=host_address,
            allow_loopback=False)
        # Same starvation guard as launch mode: handshakes first (waits
        # drain peers), file verification last.
        for role, peer in clients:
            finish_handshake(peer)
        for role, peer in clients:
            verify_trace_into(role, peer, report)
        report["checks"].append(
            "all captures finished; traces reconstruct; phases legal; "
            "accepted commands completed exactly once")
        report["status"] = "passed"
    except Exception as error:
        report["error"] = "%s: %s" % (type(error).__name__, error)
        report["traceback"] = traceback.format_exc()
    finally:
        _LAN_DRIVERS.clear()
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

        run_lifecycle(
            host, clients, report["checks"],
            join_address="127.0.0.1", allow_loopback=True,
            fault=fault)

        # Only living generations can durably finish a capture; a killed
        # process's truncated trace is recorded as a fault-injection
        # casualty instead of being forced through verification. The
        # handshakes all run first: every wait drains peer sockets, so no
        # endpoint's validation stream backs up while another finishes —
        # a starved stream times out its server-side write and the
        # player disposes the session before its own health check.
        # Trace verification is file work only, so it runs last.
        for name in names:
            entry = roster[name]
            if entry["alive"]:
                finish_handshake(entry["client"])
        for name in names:
            entry = roster[name]
            if entry["alive"]:
                verify_trace_into(name, entry["client"], report)
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
        report["error"] = "%s: %s" % (type(error).__name__, error)
        report["traceback"] = traceback.format_exc()
    finally:
        _LAN_DRIVERS.clear()
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
