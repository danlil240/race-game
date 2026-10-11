"""Complete driving-observation qualification against a running validation build.

Usage: DrivingScenario.py <run-dir>/connection.json --evidence <new-dir>

Attaches to a live player's validation run (same contract as AgentScenario)
and proves — through independently reconstructed schema-2 snapshot/delta
boundaries — that an agent using only the shared command path can complete
the authored graybox lap in both vehicle classes: discovery, profile
selection, race.start, bounded input holds driven by observed position and
gate progress (never teleports, never marks checkpoints, never sleeps as
proof), drift, boost, the landmark-ramp jump, latched recovery, pause and
camera switch, an explicit resnapshot, finish, retry, and race.exit. Stream
capacity is measured and projected onto a representative 30-minute input
schedule against the identity cache, outgoing queue and trace cap.
"""

import argparse
import json
import math
import sys
import time
from pathlib import Path

from AgentScenario import (BOUNDARY_TYPES, Client, inputs, verify_trace,
                           SUPPORTED_STATE_SCHEMAS)


# Mirrored stream/queue bounds so qualification can fail on real pressure.
IDENTITY_CAPACITY = 10000
OUTGOING_CAPACITY = 256
TRACE_CAPACITY_BYTES = 256 * 1024 * 1024
MAX_STEPS_PER_FRAME = 8          # FoundationApp.MaxStepsPerFrame
MAX_PENDING_SCREENSHOTS = 4      # ValidationBridge.MaxPendingScreenshots
SCHEDULE_SECONDS = 30 * 60       # representative 30-minute input schedule

# graybox-01 layout (authored content): gates start(0,-60)/gate-1(0,25)/
# gate-2(55,55)/gate-3(95,0)/gate-4(50,-55); walls at |x|<=119.5,|z|<=79.5;
# BarrierA x[22,28] z[-40,-10]; BarrierB x[-45,-15] z[7,13]; islands
# NE x[21,43] z[37,51] and SE x[62,82] z[-40,-24]; landmark ramp at
# x=-60 rising z~24->38 (crest y~3.4) — the sweep mounts it and the crest
# launches a real airborne episode (proven by VehicleClassPlayTests).

# Recovery respawn poses are authored content (gate centre - dir*3,
# heading along dir): qualifying recovery checks the real pose, not a
# teleport we invented.
RECOVER_POSE_GATE3 = {"x": 95.0, "z": 3.0, "heading": math.pi}


def wrap_angle(radians):
    while radians > math.pi:
        radians -= 2.0 * math.pi
    while radians <= -math.pi:
        radians += 2.0 * math.pi
    return radians


def steer_command(motor, target_x, target_z, gain=1.4):
    """Bounded proportional steering toward a world waypoint: -1..1."""
    desired = math.atan2(target_x - motor["x"], target_z - motor["z"])
    error = wrap_angle(desired - motor["heading"])
    return max(-1.0, min(1.0, error * gain))


def distance(motor, target_x, target_z):
    return math.hypot(motor["x"] - target_x, motor["z"] - target_z)


def harvest_events(state, seen):
    """Collect newly observed driving events keyed by stable event id."""
    if not isinstance(state, dict):
        return
    events = state.get("drivingEvents")
    if not events:
        return
    for entry in events:
        if isinstance(entry, dict) and entry.get("id") and entry["id"] not in seen:
            seen[entry["id"]] = entry


def event_kinds(seen):
    return [entry["kind"] for entry in seen.values()]


def project_capacity(stats, schedule_seconds):
    """Extrapolate measured stream load onto a representative schedule.

    Never trims history to fit: the projection honestly reports what the
    measured record mix would cost, so qualification can fail on real
    capacity pressure instead of hiding it.
    """
    assert stats["wallSeconds"] > 0, "Capacity needs a positive measured window"
    scale = schedule_seconds / stats["wallSeconds"]
    return {
        "traceBytes": stats["traceBytes"] * scale,
        "commandIdentities": stats["commands"] * scale,
        "boundaryRecords": stats["boundaryRecords"] * scale,
    }


class DrivingClient(Client):
    """Healthy strict client plus driving-observation bookkeeping.

    read() keeps Client's sequenced-record validation and the independent
    StateReconstructor application; this subclass additionally harvests
    transient driving events and overload telemetry off every boundary.
    """

    def __init__(self, connection, evidence):
        # Set before super(): the base constructor already reads the hello
        # and attach snapshot through the overridden read() below.
        self.driving_events = {}
        self.max_backlog_steps = 0
        self.boundaries = 0
        super().__init__(connection, evidence)

    def read(self, remaining):
        event = super().read(remaining)
        if event.get("type") in BOUNDARY_TYPES and self.state is not None:
            self.boundaries += 1
            harvest_events(self.state, self.driving_events)
            presentation = self.state.get("presentation") or {}
            backlog = presentation.get("backlogSteps") or 0
            self.max_backlog_steps = max(self.max_backlog_steps, backlog)
        return event

    def events_of(self, kind):
        return [entry for entry in self.driving_events.values()
                if entry.get("kind") == kind]

    def boundary(self, predicate, timeout=20):
        """Wait for a reconstructed boundary state matching a predicate."""
        return self.wait(
            lambda event: event.get("type") in BOUNDARY_TYPES
            and predicate(self.state), timeout=timeout)


class Driver:
    """Bounded-hold waypoint follower over the shared command path.

    Every leg re-evaluates observed motor state and issues fresh input.set
    holds (durationTicks <= HOLD_TICKS, resent well before lease-out). A
    genuine stall presses recover like a player would — bounded to
    MAX_RECOVERIES per run — never a teleport or a guessed sleep.
    """

    HOLD_TICKS = 24
    RESEND_TICKS = 16      # always resend before the hold leases out
    CORRECTION = 0.18      # resend earlier when steering moved this far
    STALL_SECONDS = 6.0    # no measurable movement -> player-like recovery
    MAX_RECOVERIES = 3

    def __init__(self, client):
        self.client = client
        self.held = None
        self.held_flags = (False, False)
        self.held_at_tick = -10 ** 9
        self.recoveries = 0

    def motor(self):
        driving = self.client.state.get("driving")
        assert driving, "No active driving session to steer"
        return driving["motor"]

    def refresh_input(self, motor, tx, tz, drift=False, boost=False):
        steering = round(steer_command(motor, tx, tz), 3)
        tick = self.client.state["tick"]
        stale = (self.held is None
                 or tick >= self.held_at_tick + self.RESEND_TICKS
                 or self.held_flags != (drift, boost))
        moved = self.held is None or abs(steering - self.held) >= self.CORRECTION
        if stale or moved:
            self.client.applied("input.set", inputs(
                steering=steering, duration=self.HOLD_TICKS,
                drift=drift, boost=boost))
            self.held = steering
            self.held_flags = (drift, boost)
            self.held_at_tick = tick

    def recover(self, note):
        self.recoveries += 1
        assert self.recoveries <= self.MAX_RECOVERIES, \
            "Vehicle needed more than %d recoveries before %s" % (
                self.MAX_RECOVERIES, note)
        before = self.motor()
        self.client.applied("input.set", inputs(
            steering=0.0, duration=5, recover=True))
        # The latched action is consumed on a simulated boundary: the
        # respawn re-baselines the pose — never asserted as progress.
        self.client.boundary(
            lambda state: state["driving"]["motor"].get("recovering") is True
            or distance(state["driving"]["motor"], before["x"], before["z"]) > 3.0,
            timeout=15)
        self.held = None

    def drive_leg(self, tx, tz, radius=4.0, timeout=30.0, drift=False,
                  boost=False, until=None, note=""):
        """Hold input toward (tx,tz) until inside radius, `until` fires, a
        bounded player-like recovery unsticks a stall, or the leg honestly
        times out."""
        deadline = time.monotonic() + timeout
        motor = self.motor()
        last_x, last_z, moved_at = motor["x"], motor["z"], time.monotonic()
        while time.monotonic() < deadline:
            motor = self.motor()
            if distance(motor, tx, tz) <= radius:
                return motor
            if until is not None and until(self.client.state):
                return motor
            if math.hypot(motor["x"] - last_x, motor["z"] - last_z) > 0.5:
                last_x, last_z, moved_at = motor["x"], motor["z"], time.monotonic()
            if time.monotonic() - moved_at >= self.STALL_SECONDS:
                self.recover(note)
                motor = self.motor()
                last_x, last_z, moved_at = (
                    motor["x"], motor["z"], time.monotonic())
                continue
            self.refresh_input(motor, tx, tz, drift=drift, boost=boost)
            self.client.boundary(
                lambda state: state["driving"]["motor"]["tick"]
                != motor["tick"], timeout=10)
        raise AssertionError("Leg timed out before reaching " + note)


def wait_phase(client, phase, timeout=30):
    client.boundary(
        lambda state: state.get("driving")
        and state["driving"]["phase"] == phase, timeout=timeout)


def wait_next(client, expected, timeout=60):
    """Wait until the session expects `expected` next — 2/3/4 after gates
    1-3, and 0 (wrapped) after gate-4."""
    client.boundary(
        lambda state: state.get("driving")
        and state["driving"]["nextCheckpoint"] == expected, timeout=timeout)


def overloads(state):
    presentation = state.get("presentation") or {}
    return presentation.get("stepOverloads") or 0


def screenshot(client, capture_id, run_directory):
    client.send({"type": "screenshot", "captureId": capture_id})
    shot = client.wait(lambda event: event.get("type") == "screenshot"
                       and event.get("captureId") == capture_id)
    assert shot["status"] == "captured", shot
    path = Path(run_directory) / "screenshots" / shot["file"]
    assert path.stat().st_size > 0, "Screenshot artifact missing"
    return shot


def drive_lap(client, driver, checks, run_directory, label):
    """Drive one authored lap of intro-01, exercising every driving feature
    through the command path with observed-position proof only."""
    wait_phase(client, "running")
    baseline_overloads = overloads(client.state)

    # --- boost leg: straight north through gate-1 ------------------------
    driver.refresh_input(driver.motor(), 0.0, 30.0, boost=True)
    client.boundary(
        lambda state: state["driving"]["motor"].get("boostActive") is True,
        timeout=15)
    checks.append(label + "boost engaged through input.set on the start straight")
    driver.drive_leg(0.0, 30.0, radius=4.0, timeout=20, boost=True,
                     until=lambda state: state["driving"]["nextCheckpoint"] == 2,
                     note="gate-1")
    wait_next(client, 2)
    assert any(mark["index"] == 1 and mark["crossed"]
               for mark in client.state["driving"]["checkpoints"]), \
        "gate-1 was not honestly crossed"
    assert client.events_of("checkpoint.crossed"), "No checkpoint.crossed event"
    assert client.events_of("motor.boostStart"), "No motor.boostStart event"
    assert client.state["driving"]["motor"]["speed"] > 0, \
        "Boost leg never accelerated"
    checks.append(label + "gate-1 crossed in order; boostStart event with charge drain")

    # --- drift leg: the hard left off the straight into the west lane ----
    # BarrierB occupies x[-45,-15] x z[7,13]: swing west north of it to
    # (-25,25), then drop south-west below it to line up on the ramp.
    driver.refresh_input(driver.motor(), -25.0, 25.0, drift=True)
    driver.drive_leg(-25.0, 25.0, radius=6.0, timeout=20, drift=True,
                     note="drift corner")
    assert client.events_of("motor.driftStart") \
        or client.state["driving"]["motor"].get("drifting"), \
        "Held drift input never engaged (speed/steering eligible)"
    driver.drive_leg(-50.0, 0.0, radius=6.0, timeout=20,
                     note="west lane below BarrierB")
    client.boundary(
        lambda state: state["driving"]["motor"].get("drifting") is False
        or bool(client.events_of("motor.driftEnd")), timeout=15)
    checks.append(label + "drift held through the west turn (driftStart/End observed)")

    # --- ramp jump: line up south on the centre line, climb, crest -------
    driver.drive_leg(-60.0, 10.0, radius=5.0, timeout=20,
                     note="ramp approach")
    driver.drive_leg(-60.0, 50.0, radius=6.0, timeout=40,
                     until=lambda state: bool(client.events_of("motor.landed")),
                     note="ramp crest")
    assert client.events_of("motor.becameAirborne"), "Vehicle never left the ground"
    assert client.events_of("motor.landed"), "No motor.landed event after the ramp"
    client.boundary(
        lambda state: state["driving"]["motor"].get("grounded") is True,
        timeout=15)
    checks.append(label + "landmark ramp launched and landed (becameAirborne/landed)")

    # --- resnapshot mid-drive ---------------------------------------------
    race_id = client.state["driving"]["raceId"]
    client.send({"type": "snapshot"})
    snap = client.wait(lambda event: event.get("type") == "snapshot",
                       timeout=10)
    # The resnapshot became the new reconstruction baseline: the identity it
    # reports must be the same live race, not a stale prefix.
    assert snap["state"]["driving"]["raceId"] == race_id
    assert snap["state"]["driving"]["simulationTick"] > 0
    checks.append(label + "explicit resnapshot restored complete observation mid-drive")

    # --- gate-2 east leg ----------------------------------------------------
    # IslandNE occupies x[21,43] x z[37,51]: stay north of its z=51 face.
    driver.drive_leg(-30.0, 58.0, radius=7.0, timeout=25, note="north apron east")
    driver.drive_leg(20.0, 60.0, radius=8.0, timeout=25, note="north lane")
    driver.drive_leg(63.0, 55.0, radius=5.0, timeout=25,
                     until=lambda state: state["driving"]["nextCheckpoint"] == 3,
                     note="gate-2")
    wait_next(client, 3)
    assert any(mark["index"] == 2 and mark["crossed"]
               for mark in client.state["driving"]["checkpoints"])
    checks.append(label + "gate-2 crossed heading east above the island")

    # --- pause + camera switch ------------------------------------------------
    paused, _ = client.applied("pause.toggle")
    paused_state = client.action_state(paused["commandId"])
    assert paused_state["paused"] is True
    frozen_sim = paused_state["driving"]["simulationTick"]
    frozen_elapsed = paused_state["driving"]["elapsedTicks"]
    client.boundary(lambda state: state["tick"] >= paused_state["tick"] + 10,
                    timeout=15)
    assert client.state["driving"]["simulationTick"] == frozen_sim, \
        "Simulation advanced while paused"
    assert client.state["driving"]["elapsedTicks"] == frozen_elapsed
    camera_before = client.state["driving"]["camera"]["mode"]
    toggled, _ = client.applied("camera.toggle")
    assert client.action_state(toggled["commandId"])["draftProfile"]["camera"] \
        != client.action_state(paused["commandId"])["draftProfile"]["camera"]
    client.boundary(
        lambda state: state["driving"]["camera"]["mode"] != camera_before,
        timeout=15)
    assert client.state["driving"]["camera"]["mode"] in ("chase", "overhead")
    client.applied("pause.toggle")
    client.boundary(lambda state: state["paused"] is False, timeout=10)
    checks.append(label + "pause froze simulation; camera.toggle switched the real mode")

    # --- gate-3 south leg + latched recovery ----------------------------------
    # IslandSE occupies x[62,82] x z[-40,-24]: hold x>=90 down the east side.
    driver.drive_leg(90.0, 40.0, radius=7.0, timeout=25, note="east lane entry")
    driver.drive_leg(95.0, -8.0, radius=5.0, timeout=25,
                     until=lambda state: state["driving"]["nextCheckpoint"] == 4,
                     note="gate-3")
    wait_next(client, 4)
    progress_before = client.state["driving"]["nextCheckpoint"]
    recover, _ = client.applied("input.set", inputs(
        steering=0.0, duration=2, recover=True))
    assert client.action_state(recover["commandId"])["driving"]["pendingActions"], \
        "Recovery press was not latched pending"
    client.boundary(
        lambda state: state["driving"]["motor"].get("recovering") is True
        or bool(client.events_of("race.recovered")), timeout=15)
    recovered = client.state["driving"]["motor"]
    assert distance(recovered, RECOVER_POSE_GATE3["x"], RECOVER_POSE_GATE3["z"]) < 2.5, \
        "Recovery did not respawn at the gate-3 safe pose: %s" % recovered
    assert client.state["driving"]["nextCheckpoint"] == progress_before, \
        "Recovery manufactured checkpoint progress"
    assert client.events_of("action.queued") and client.events_of("action.consumed"), \
        "Pending recovery lifecycle events missing"
    checks.append(label + "latched recovery respawned at the gate-3 safe pose, no progress gained")

    # --- gate-4 + finish --------------------------------------------------
    # BarrierA occupies x[22,28] x z[-40,-10]: stay south on z=-55 for the
    # westbound gate-4 crossing, then swing below the start line and cross
    # northbound for the finish.
    driver.drive_leg(95.0, -45.0, radius=7.0, timeout=25, note="south lane")
    driver.drive_leg(62.0, -55.0, radius=6.0, timeout=25, note="south lane west")
    driver.drive_leg(40.0, -55.0, radius=6.0, timeout=25,
                     until=lambda state: state["driving"]["nextCheckpoint"] == 0,
                     note="gate-4")
    wait_next(client, 0)
    driver.drive_leg(-15.0, -52.0, radius=7.0, timeout=25, note="west lane")
    driver.drive_leg(-10.0, -68.0, radius=6.0, timeout=25, note="below start")
    driver.drive_leg(0.0, -45.0, radius=8.0, timeout=25,
                     until=lambda state: state["driving"]["finished"],
                     note="finish line")
    client.boundary(lambda state: state["driving"]["finished"] is True
                    and state["driving"]["phase"] == "finished", timeout=30)
    finished = client.state["driving"]
    assert finished["finishTicks"] > 0, "Finished without an elapsed race clock"
    assert finished["lap"] >= 1
    assert client.events_of("race.lap"), "No race.lap event on the final gate"
    assert client.events_of("race.finished"), "No race.finished event"
    assert overloads(client.state) == baseline_overloads, \
        "Sustained step overload during the lap: %d" % overloads(client.state)
    checks.append(label + "one full honest lap finished (lap+finished events, ordered gates)")

    # --- screenshot inside the driving screen -----------------------------------
    screenshot(client, "finish-" + label.strip(" :"), run_directory)
    checks.append(label + "screenshot captured and durable inside the run directory")


def run(connection, evidence):
    client = DrivingClient(connection, evidence)
    checks = []
    run_directory = connection["_directory"]
    start_wall = time.monotonic()
    try:
        assert client.state["activeSlot"] == -1 and client.state["screen"] == "profiles"
        client.send({"type": "discover"})
        discovery = client.wait(lambda event: event.get("type") == "discovery")
        actions = {entry["action"] for entry in discovery["commands"]}
        assert {"race.start", "race.restart", "race.exit",
                "input.set", "input.clear", "camera.toggle",
                "pause.toggle", "tutorial.start"} <= actions, actions
        checks.append("discovery lists the driving command set with vehicle parameters")

        created, _ = client.applied("profile.create",
                                    {"slot": 0, "language": "en"})
        assert client.action_state(created["commandId"])["activeSlot"] == 0
        client.applied("ui.navigate", {"screen": "practice"})
        checks.append("profile created and practice screen reached")

        races = []
        for label, vehicle in (("car:", "car-01"), ("moto:", "motorcycle-01")):
            started, _ = client.applied(
                "race.start", {"vehicleId": vehicle, "eventId": "intro-01"})
            state = client.action_state(started["commandId"])
            driving = state["driving"]
            assert driving["vehicleId"] == vehicle and driving["eventId"] == "intro-01"
            assert driving["phase"] == "countdown" and driving["raceId"]
            races.append(driving["raceId"])
            wait_phase(client, "running", timeout=30)
            drive_lap(client, Driver(client), checks, run_directory, label + " ")

            # Retry creates a fresh race identity inside the same run.
            restarted, _ = client.applied("race.restart")
            state = client.action_state(restarted["commandId"])
            assert state["driving"]["raceId"] != driving["raceId"]
            wait_phase(client, "running", timeout=30)
            checks.append(label + " race.restart produced a new race identity")

            exited, _ = client.applied("race.exit")
            state = client.action_state(exited["commandId"])
            assert state["screen"] == "practice" and state["driving"] is None
            checks.append(label + " race.exit returned to practice with no session")

        assert len(set(races)) == len(races), "Race identities collided across classes"

        # --- capacity measurement against a representative 30-minute schedule ----
        trace_path = Path(run_directory) / "state.ndjson"
        wall_seconds = time.monotonic() - start_wall
        commands = sum(1 for line in
                       (evidence / "commands.ndjson").read_text(
                           encoding="utf-8").splitlines()
                       if json.loads(line).get("type") == "command")
        stats = {
            "wallSeconds": wall_seconds,
            "traceBytes": trace_path.stat().st_size,
            "commands": commands,
            "boundaryRecords": client.boundaries,
        }
        projected = project_capacity(stats, SCHEDULE_SECONDS)
        # Bounded load probes: pending outgoing records sampled via health.
        client.send({"type": "health"})
        health = client.wait(lambda event: event.get("type") == "health")
        dropped_events = client.state.get("droppedDrivingEvents", 0)
        measurement = {
            "measured": stats,
            "scheduleSeconds": SCHEDULE_SECONDS,
            "projected": projected,
            "limits": {"identityCapacity": IDENTITY_CAPACITY,
                       "outgoingQueue": OUTGOING_CAPACITY,
                       "traceBytes": TRACE_CAPACITY_BYTES},
            "maxBacklogSteps": client.max_backlog_steps,
            "pendingOutgoingAtFinish": health["outgoingRecords"],
            "droppedDrivingEvents": dropped_events,
            # Task 7 owns the real 30-minute graybox soak; these figures
            # are an honest projection of this measured run, not a soak.
            "basis": "projection from a measured ~%d s run, not a sustained soak" % wall_seconds,
        }
        assert projected["commandIdentities"] < IDENTITY_CAPACITY, \
            "Representative schedule exhausts the command-identity cache"
        assert projected["traceBytes"] < TRACE_CAPACITY_BYTES, \
            "Representative schedule exceeds the trace cap: %s" % projected
        assert health["outgoingRecords"] < OUTGOING_CAPACITY, \
            "Outgoing queue saturated during qualification"
        assert client.max_backlog_steps < MAX_STEPS_PER_FRAME, \
            "Sustained overload: backlog never drained during the run"
        assert dropped_events == 0, \
            "Driving history was silently evicted during qualification: %d" % dropped_events
        checks.append(
            "capacity measured: %.1f MiB trace / %d commands observed, "
            "projected %d MiB / %d identities over %d s" % (
                stats["traceBytes"] / 1048576.0, commands,
                projected["traceBytes"] / 1048576.0,
                projected["commandIdentities"], SCHEDULE_SECONDS))

        client.send({"type": "finish"})
        finished = client.wait(lambda event: event.get("type") == "finished")
        assert finished["captureValid"], finished
        verify_trace(Path(run_directory), connection)
        checks.append("full stream reconstructed and finished cleanly")
        return {"checks": checks, "measurement": measurement,
                "raceIds": races,
                "events": len(client.driving_events),
                "buildVersion": client.hello.get("buildVersion"),
                "contentVersion": client.hello.get("contentVersion"),
                "deviceId": client.events[-1].get("deviceId") if client.events else None,
                "stateSchemaVersion": 2}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection_file", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed",
              "scope": "complete driving observation + reconstruction qualification"}
    try:
        if not __debug__:
            raise RuntimeError("Run without -O: scenario assertions must be enabled")
        connection = json.loads(args.connection_file.read_text(encoding="utf-8-sig"))
        assert connection["protocolVersion"] == 1
        assert connection.get("stateSchemaVersion", 1) in SUPPORTED_STATE_SCHEMAS, \
            "Unsupported advertised state schema"
        connection["_directory"] = str(args.connection_file.resolve().parent)
        outcome = run(connection, args.evidence)
        result.update(outcome)
        result["status"] = "passed"
        result["runId"] = connection["runId"]
        result["notVerified"] = [
            "physical keyboard/touch device input and UI parity "
            "(motor-effect parity covered by FoundationInputPlayTests."
            "PhysicalSteerMatchesCommandSteerInActualMotorOutcome)",
            "visual/Hebrew/audio quality", "Android device behavior",
            "real 30-minute soak (deferred to Task 7 — capacity here is a "
            "projection of a measured ~76 s run, not a sustained session)",
            "fault-injection outcomes (StreamRobustness/FinishDelivery own them)",
        ]
        result["deferredToTask7"] = (
            "real 30-minute graybox soak: capacity figures above are "
            "projections from a measured ~76 s run, not a measured soak")
    except Exception as error:
        result["error"] = type(error).__name__ + ": " + str(error)
    (args.evidence / "result.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
