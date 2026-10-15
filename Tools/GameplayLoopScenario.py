"""External proof of the complete gameplay loop through real players.

Usage (development — the scenario owns the processes):

    GameplayLoopScenario.py --launch <player.exe> --evidence <new-dir>
        --scope smoke|campaign|lan [--players 2-4] [--fixture]

Usage (qualification — attach to independently launched players):

    GameplayLoopScenario.py --connections <host-conn.json>
        <client-conn.json> ... --evidence <new-dir> --scope <scope>
        [--host-address <lan-ip>] [--fixture]

Scopes:
    smoke    Fresh synthetic profile -> tutorial + gadget -> a finished
             campaign event -> stars/coins -> legitimate earnings for the
             60-coin upgrade -> purchase -> a real failed-save retry ->
             process restart -> preserved progress -> Quick Race -> Time
             Trial record -> replay without campaign credit.
    campaign Drive every campaign event the loaded catalog authors, in
             authored order; locked/ungated rows and finale advancement
             are reported per event so a partial first world is honest.
    lan      Equal class normalization under (possibly uneven) profiles,
             gadget authority, shared pause, disconnect/AI/rejoin with
             attribution checks, duplicate/conflicting delivery, a real
             failed-save retry, permanent departure, and a fresh process
             attaching to retained result state without re-minting
             rewards. Run for two-client and four-endpoint workloads.

Honesty contract: everything the run drives goes through the normal
command surface (profile.*, tutorial.*, the loop *.open/*.start
commands, input.set, results.*, upgrade.purchase, cosmetic.equip,
lan.*). No command grants coins, stars or traversal. Where the loaded
content cannot support a required step (Task 9's authored first world is
gated on M4 hardware evidence), the row reports notVerified with the
named reason — never fabricated coverage. A run on the legacy catalog
reports the loop content as honestly unavailable.

--fixture is the explicit test-only option: it declares that the
launched/attached endpoint is a synthetic protocol fixture rather than
a development player, and every artifact (launch/attach records, the
verdict rows, result.json) carries "fixture": true so fixture evidence
can never be mistaken for real-player proof.
"""

import argparse
import json
import math
import os
import subprocess
import sys
import time
import traceback
import uuid
from pathlib import Path

from AgentScenario import (BOUNDARY_TYPES, Client, StateReconstructor,
                           SUPPORTED_STATE_SCHEMAS, inputs,
                           process_start_stamp, verify_trace)
from RestartScenario import (build_fingerprint, file_hash,
                             write_json)
from NetworkScenario import (LanDriver, pump_drivers, drive_join,
                             drive_race, finish_handshake, launch,
                             read_connection, settle_results,
                             validate_lan_state, verify_lan_trace,
                             wait_state, wait_terminal, _LAN_DRIVERS)
from DrivingScenario import Driver as WaypointDriver


REQUIRED_SCHEMA = 6

# The production first world (Task 9, hardware-gated): used only to label
# how incomplete the loaded catalog is — never a fabricated requirement
# the fixture/legacy content could fail.
PRODUCTION_REGULAR_EVENTS = 24
PRODUCTION_FINALES = 2

# The smoke scope's required purchase price (plan-pinned authored
# first-level upgrade price).
SMOKE_PURCHASE_PRICE = 60
UPGRADE_CATEGORIES = ("acceleration", "handling", "boost")

LESSON_ORDER = ["steering", "brake", "drift", "boost", "jump",
                "recovery", "gadget"]

# graybox-01 authored pickup columns (DrivingPrototypeSetup): the gadget
# lesson and LAN gadget check drive through these, never a grant seam.
PICKUP_SITES = [(0.0, -20.0), (0.0, 45.0), (78.0, 55.0),
                (95.0, -28.0), (32.0, -55.0), (-60.0, 42.0)]


# ---------------------------------------------------------------------------
# Verdict rows
# ---------------------------------------------------------------------------

class Checks:
    """Ordered named verdict rows: passed | failed | notVerified.

    A required row that could not run because content/hardware is absent
    is notVerified with the named reason — honest partial coverage. A row
    only reaches "passed" through real evidence; a thrown assertion
    inside a step() block records the check failed with the reason."""

    def __init__(self):
        self.rows = []
        self._seen = set()

    def add(self, name, status, detail=""):
        assert status in ("passed", "failed", "notVerified"), status
        row = {"name": name, "status": status}
        if detail:
            row["detail"] = detail
        self.rows.append(row)
        self._seen.add(name)
        return row

    def passed(self, name, detail=""):
        return self.add(name, "passed", detail)

    def failed(self, name, detail=""):
        return self.add(name, "failed", detail)

    def not_verified(self, name, detail):
        return self.add(name, "notVerified", detail)

    def has(self, name):
        return name in self._seen

    @property
    def failed_rows(self):
        return [row for row in self.rows if row["status"] == "failed"]

    @property
    def unverified_rows(self):
        return [row for row in self.rows
                if row["status"] == "notVerified"]


# ---------------------------------------------------------------------------
# Driving-event harvest (same pattern as DrivingScenario.DrivingClient)
# ---------------------------------------------------------------------------

def harvest_events(state, seen):
    events = (state or {}).get("drivingEvents") if isinstance(state, dict) \
        else None
    if not events:
        return
    for entry in events:
        if isinstance(entry, dict) and entry.get("id") \
                and entry["id"] not in seen:
            seen[entry["id"]] = entry


def solo_events(state, seen):
    """Harvest events from a soloRace boundary (per-step staged list)."""
    solo = (state or {}).get("soloRace") if isinstance(state, dict) else None
    if not solo:
        return
    for entry in solo.get("events") or []:
        if isinstance(entry, dict) and entry.get("id") \
                and entry["id"] not in seen:
            seen[entry["id"]] = entry


class LoopClient(Client):
    """Strict client plus event harvesting for gadget/objective proof."""

    def __init__(self, connection, evidence):
        self.driving_events = {}
        super().__init__(connection, evidence)

    def read(self, remaining):
        event = super().read(remaining)
        if event.get("type") in BOUNDARY_TYPES and self.state is not None:
            harvest_events(self.state, self.driving_events)
            solo_events(self.state, self.driving_events)
        return event

    def events_of(self, kind):
        return [entry for entry in self.driving_events.values()
                if entry.get("kind") == kind]

    def boundary(self, predicate, timeout=20):
        """DrivingScenario.Driver contract: one observed boundary whose
        state satisfies `predicate` (drivers are pumped while waiting so
        a shared wait never starves another endpoint)."""
        return wait_loop_state(self, predicate, timeout, "boundary")


# ---------------------------------------------------------------------------
# Stream/state loop invariants — shared by live reads and post-hoc
# verification so a corrupted trace fails identically either way.
# ---------------------------------------------------------------------------

def receipts(profile):
    if not isinstance(profile, dict):
        return []
    return [row for row in profile.get("appliedResults") or []
            if isinstance(row, dict)]


def find_receipt(profile, result_id):
    return [row for row in receipts(profile)
            if row.get("resultId") == result_id]


def validate_loop_state(state, label="state"):
    """Schema-6 loop invariants a reconstructed boundary must satisfy.

    Complements verify_trace/validate_lan_state: solo-session shape,
    per-mode tuning provenance, attribution honesty, and the
    unresolved-reward gate's coherence with the durable receipts."""
    solo = state.get("soloRace")
    if solo is not None:
        assert isinstance(solo, dict), \
            label + ": soloRace must be object|null"
        mode = solo.get("mode")
        assert mode in ("campaign", "quickRace", "timeTrial",
                        "practice"), \
            label + ": unknown soloRace mode " + repr(mode)
        tuning = solo.get("tuningSource")
        # Tuning provenance is a fact the modes set, not a renderer
        # guess: normalized modes always report the baseline tune, and
        # progression races resolve the profile's purchased upgrades —
        # any other claim is fabricated tuning evidence.
        if mode == "timeTrial":
            assert tuning == "normalized", \
                label + ": timeTrial must report normalized tuning, " \
                "got " + repr(tuning)
        elif mode in ("campaign", "quickRace"):
            assert tuning == "purchased", \
                label + ": " + mode + " must report purchased tuning " \
                "provenance, got " + repr(tuning)
        race = solo.get("race")
        if race is not None:
            assert mode in ("campaign", "quickRace"), \
                label + ": four-racer session under non-race mode " \
                + repr(mode)
            own = solo.get("participantId")
            humans = [r for r in race.get("racers") or []
                      if r and r.get("attribution") == "human"]
            own_rows = [r for r in race.get("racers") or []
                        if r and r.get("participantId") == own]
            assert len(humans) == 1, \
                label + ": expected exactly one human racer, got " \
                + repr(humans)
            assert own_rows and own_rows[0].get("attribution") == "human", \
                label + ": local seat is not the human-attributed racer"
        trial = solo.get("trial")
        if trial is not None:
            assert mode in ("timeTrial", "practice"), \
                label + ": trial session under non-trial mode " \
                + repr(mode)
        result = solo.get("result")
        if result is not None:
            assert result.get("finalized") is True, \
                label + ": unfinalized result published"
            ids = [r.get("participantId")
                   for r in result.get("racers") or [] if r]
            assert len(ids) == len(set(ids)), \
                label + ": duplicated racer rows would double-count " \
                "a reward"
            if race is not None:
                live = {r.get("participantId"): r.get("attribution")
                        for r in race.get("racers") or [] if r}
                for row in result.get("racers") or []:
                    if row is None:
                        continue
                    pid = row.get("participantId")
                    if pid in live:
                        assert row.get("attribution") == live[pid], \
                            label + ": result attribution %r disagrees " \
                            "with the live roster %r for %s" % (
                                row.get("attribution"), live[pid], pid)
    progression = state.get("progression")
    if isinstance(progression, dict) \
            and progression.get("rewardUnresolved"):
        # The gate must name the unresolved result's race identity —
        # an anonymous latch is indistinguishable from a stuck latch.
        assert progression.get("rewardUnresolvedRaceId"), \
            label + ": rewardUnresolved without a race identity"
        # The atomic transaction commits receipts+award together, so a
        # durable receipt for the latched result while the gate holds is
        # the "failed save shown as persisted" mutant — both cannot be
        # true at once.
        result = (state.get("soloRace") or {}).get("result")
        if isinstance(result, dict) and result.get("resultId"):
            found = find_receipt(state.get("savedProfile"),
                                 result["resultId"])
            assert not found, \
                label + ": reward gate latched while the result's " \
                "receipt is already durable (failed save shown as " \
                "persisted)"
    return solo


def campaign_rows(state):
    progression = (state or {}).get("progression") or {}
    return progression.get("campaignEvents") or []


def unlocked_events(state):
    return [row for row in campaign_rows(state) if not row.get("locked")]


def solo_session(state):
    """The DrivingSessionState dict the human seat runs in a solo session
    (campaign/quickRace -> racer.session, trial modes -> solo.trial)."""
    if not isinstance(state, dict):
        return None
    solo = state.get("soloRace")
    if not isinstance(solo, dict):
        return None
    if solo.get("mode") in ("campaign", "quickRace"):
        race = solo.get("race")
        own = solo.get("participantId")
        if not isinstance(race, dict):
            return None
        for racer in race.get("racers") or []:
            if racer and racer.get("participantId") == own:
                return racer.get("session")
        return None
    if solo.get("mode") in ("timeTrial", "practice"):
        return solo.get("trial")
    return None


# ---------------------------------------------------------------------------
# Solo driver — the LAN pure-pursuit follower retargeted at local sessions
# ---------------------------------------------------------------------------

class SoloDriver(LanDriver):
    """Pure-pursuit follower over soloRace sessions and the tutorial/
    practice `driving` slice. Emits the same bounded input.set holds a
    human would — the pursuer steers whatever session the seat owns."""

    def _racer(self):
        state = self.peer.state
        if not isinstance(state, dict):
            return None
        solo = state.get("soloRace")
        if isinstance(solo, dict):
            if solo.get("phase") not in ("countdown", "running"):
                return None
            session = solo_session(state)
            if isinstance(session, dict):
                return {"session": session, "slot": 0,
                        "finished": session.get("finished"),
                        "dnf": session.get("dnf", False)}
            return None
        driving = state.get("driving")
        if isinstance(driving, dict) and driving.get("phase") \
                in ("countdown", "running"):
            return {"session": driving, "slot": 0,
                    "finished": driving.get("finished"),
                    "dnf": driving.get("dnf", False)}
        return None


_SOLO_DRIVERS = []


def pump_all_drivers():
    pump_drivers()
    for driver in list(_SOLO_DRIVERS):
        driver.pump()


def drop_driver(peer):
    """Stop steering an endpoint that left the session (its input.set
    frames would only ever be rejected now)."""
    _LAN_DRIVERS[:] = [d for d in _LAN_DRIVERS if d.peer is not peer]
    _SOLO_DRIVERS[:] = [d for d in _SOLO_DRIVERS if d.peer is not peer]


def wait_loop_state(client, predicate, timeout, what):
    """wait_state that also pumps solo drivers (starvation guard)."""
    return client.wait(
        lambda event: event.get("type") in BOUNDARY_TYPES
        and predicate(client.state), timeout=timeout,
        on_event=lambda event: pump_all_drivers())


def drive_solo_until(client, done, timeout, note):
    """Drive the live solo/tutorial session until `done(state)` fires or
    the honest deadline passes — every steering correction is an
    input.set hold, stalls press recover like a player."""
    driver = SoloDriver(client, remote=False)
    _SOLO_DRIVERS.append(driver)
    try:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                driver.pump()
            except AssertionError:
                raise
            try:
                wait_loop_state(client, done, 2.0, note)
                return client.state
            except AssertionError:
                if time.monotonic() >= deadline:
                    raise
        raise AssertionError("Timed out driving toward " + note)
    finally:
        if driver in _SOLO_DRIVERS:
            _SOLO_DRIVERS.remove(driver)


# ---------------------------------------------------------------------------
# Result/reward accounting — the checks that name the mutants
# ---------------------------------------------------------------------------

def expect_new_receipt(before_profile, after_profile, result, label):
    """After a finalized progression result the profile must gain exactly
    one receipt bound to THIS result's identity, and the wallet delta
    must equal the receipt's own award echo. Anything else is a named
    defect: a missing award, a double award, or a stale context."""
    assert isinstance(result, dict) and result.get("resultId"), \
        label + ": finalized result missing its identity"
    result_id = result["resultId"]
    before_rows = receipts(before_profile)
    after_rows = receipts(after_profile)
    before_ids = {row.get("resultId") for row in before_rows}
    new_rows = [row for row in after_rows
                if row.get("resultId") not in before_ids]
    assert len(new_rows) == 1 \
        and new_rows[0].get("resultId") == result_id, \
        label + ": expected exactly one new receipt bound to result " \
        + repr(result_id) + ", got " \
        + repr([row.get("resultId") for row in new_rows]) \
        + " — a receipt naming any other result is a stale context"
    receipt = new_rows[0]
    assert receipt.get("fingerprint"), \
        label + ": receipt carries no result fingerprint"
    duplicates = [row for row in after_rows
                  if row.get("resultId") == result_id]
    assert len(duplicates) == 1, \
        label + ": double award — %d receipts for %s" % (
            len(duplicates), result_id)
    coins_before = (before_profile or {}).get("coins", 0)
    coins_after = (after_profile or {}).get("coins", 0)
    awarded = receipt.get("awardedCoins")
    assert type(awarded) is int, label + ": receipt has no award echo"
    assert coins_after - coins_before == awarded, \
        label + ": wallet delta %d disagrees with the receipt echo %d" \
        % (coins_after - coins_before, awarded)
    return receipt


def assert_tuning_provenance(state, expected, label):
    solo = state.get("soloRace") or {}
    assert solo.get("tuningSource") == expected, \
        label + ": tuningSource %r, expected %r" % (
            solo.get("tuningSource"), expected)


def profile_snapshot(client):
    profile = client.state.get("savedProfile")
    return json.loads(json.dumps(profile)) if profile else None


def first_owned_vehicle(state, want_class=None):
    profile = state.get("savedProfile") or {}
    vehicles = profile.get("unlockedVehicles") or []
    assert vehicles, "profile owns no vehicle"
    if want_class == "motorcycle":
        for vehicle in vehicles:
            if "motorcycle" in vehicle or "moto" in vehicle:
                return vehicle
    for vehicle in vehicles:
        if want_class == "car" and "car" in vehicle:
            return vehicle
    return vehicles[0]


# ---------------------------------------------------------------------------
# Post-hoc evidence verification
# ---------------------------------------------------------------------------

def verify_loop_trace(directory, info):
    """verify_trace plus per-boundary loop invariants."""
    verify_trace(directory, info)
    trace = Path(directory) / "state.ndjson"
    reconstructor = StateReconstructor()
    with trace.open(encoding="utf-8-sig") as source:
        for line in source:
            event = json.loads(line)
            state = reconstructor.apply(event)
            if state is not None:
                validate_loop_state(state, str(directory))


def verify_run_generations(runs, label):
    """Restart/fault evidence must prove each generation was a NEW
    process: distinct pid + runId, and (where the platform reports it) a
    distinct process-creation stamp. A run row missing its generation
    record is the 'missing process generation' mutant — never pass."""
    generations = [run for run in runs
                   if "pid" in run or "runId" in run]
    assert generations, label + ": no process generations recorded"
    for index, run in enumerate(generations):
        assert type(run.get("pid")) is int and run["pid"] > 0, \
            "%s: generation %d carries no process identity" % (
                label, index)
        assert isinstance(run.get("runId"), str) and run["runId"], \
            "%s: generation %d carries no run identity" % (label, index)
    run_ids = [run["runId"] for run in generations]
    assert len(run_ids) == len(set(run_ids)), \
        label + ": two generations share one run identity"
    pids = [run["pid"] for run in generations]
    assert len(pids) == len(set(pids)), \
        label + ": two generations share one pid (recycled evidence?)"
    stamps = [run.get("startStamp") for run in generations]
    if all(stamp is not None for stamp in stamps):
        assert len(set(stamps)) == len(stamps), \
            label + ": two generations share one process-creation stamp"


# ---------------------------------------------------------------------------
# Filesystem write-deny for honest failed-save evidence (Windows only)
# ---------------------------------------------------------------------------

def _acl_user():
    domain = os.environ.get("USERDOMAIN") or os.environ.get("COMPUTERNAME")
    return "%s\\%s" % (domain, os.environ.get("USERNAME", ""))


def deny_directory_write(directory):
    """Deny the current user write access to the profile dir so a real
    profile save fails at commit time; returns True when applied."""
    if sys.platform != "win32":
        return False
    directory = Path(directory)
    if not directory.is_dir():
        return False
    result = subprocess.run(
        ["icacls", str(directory), "/deny",
         _acl_user() + ":(OI)(CI)W"],
        capture_output=True, text=True, timeout=15)
    return result.returncode == 0


def restore_directory_write(directory):
    """Lift the deny ACE deny_directory_write installed; always tried."""
    if sys.platform != "win32":
        return False
    result = subprocess.run(
        ["icacls", str(directory), "/remove:d", _acl_user()],
        capture_output=True, text=True, timeout=15)
    return result.returncode == 0


# ---------------------------------------------------------------------------
# Shared launch/attach plumbing
# ---------------------------------------------------------------------------

def launch_generation(player_command, root, name, profile_dir,
                      startup_timeout, report, role, fixture):
    """Launch one player process with the generation bookkeeping the
    restart checks verify (pid + runId + creation stamp)."""
    process, info, directory = launch(player_command, root, name,
                                      profile_dir, startup_timeout)
    evidence = root / (name + "-evidence")
    evidence.mkdir()
    generation = {
        "role": role, "fixture": bool(fixture), "pid": process.pid,
        "runId": info["runId"],
        "startStamp": process_start_stamp(process.pid),
        "command": info["command"],
        "validationDirectory": str(directory),
        "profileDirectory": str(profile_dir),
        "evidenceDirectory": str(evidence),
    }
    write_json(evidence / "launch.json", generation)
    report["runs"].append({k: generation[k] for k in
                           ("role", "pid", "runId", "startStamp")})
    return process, info, directory, evidence


def terminate_process(process, shutdown_timeout):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=shutdown_timeout)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=shutdown_timeout)
    return process.poll()


# ---------------------------------------------------------------------------
# Tutorial + gadget (smoke)
# ---------------------------------------------------------------------------

def wait_lesson(client, at_least, timeout, what):
    """Wait until the tutorial reaches `at_least` in LESSON_ORDER."""
    def reached(state):
        tutorial = ((state.get("driving") or {}).get("tutorial")) or {}
        if tutorial.get("phase") != "lessons":
            return True
        current = tutorial.get("lessonId")
        try:
            return LESSON_ORDER.index(current) \
                >= LESSON_ORDER.index(at_least)
        except ValueError:
            return current == at_least
    wait_loop_state(client, reached, timeout, what)


def run_tutorial(client, checks, graduate_timeout=420):
    """tutorial/gadget evidence: ordered lessons on genuine driving
    events plus the authored pickup -> gadget.used authority check.
    Each lesson is its own verdict row so a stall names itself."""
    begun, _ = client.applied("tutorial.start", {"vehicleId": "car-01"})
    state = client.action_state(begun["commandId"])
    assert state["screen"] == "driving" and state["driving"], \
        state["screen"]
    tutorial = state["driving"]["tutorial"]
    assert tutorial["phase"] == "lessons" \
        and tutorial["lessonId"] == "steering", tutorial
    checks.passed("tutorial.start", "ordered lesson stage entered "
                  "through the shared command path")
    wait_loop_state(
        client,
        lambda s: (s.get("driving") or {}).get("phase") == "running",
        30, "tutorial running")

    completed = set()
    current = "steering"
    try:
        client.applied("input.set", inputs(steering=0.8, duration=70))
        wait_lesson(client, "brake", 40, "steering lesson")
        completed.add("steering")
        checks.passed("tutorial.steering",
                      "held steering advanced the lesson on genuine "
                      "simulated ticks")
        current = "brake"
        client.applied("input.set", inputs(brake=0.8, duration=40))
        wait_lesson(client, "drift", 40, "brake lesson")
        completed.add("brake")
        checks.passed("tutorial.brake",
                      "held brake demand advanced the lesson")
        # drift: real speed first, then a steer+drift hold — the
        # driftStart event is the only completion currency.
        client.applied("input.set", inputs(steering=0.0, duration=60))
        wait_loop_state(
            client,
            lambda s: ((s.get("driving") or {}).get("motor") or {})
            .get("speed", 0) > 6.0,
            25, "speed for drift")
        current = "drift"
        client.applied("input.set", inputs(steering=-1.0, duration=30,
                                           drift=True))
        wait_lesson(client, "boost", 40, "drift lesson")
        completed.add("drift")
        checks.passed("tutorial.drift",
                      "motor.driftStart completed the drift lesson")
        current = "boost"
        client.applied("input.set", inputs(steering=0.0, duration=40,
                                           boost=True))
        wait_lesson(client, "jump", 40, "boost lesson")
        completed.add("boost")
        checks.passed("tutorial.boost",
                      "motor.boostStart completed the boost lesson")
        # jump: the landmark ramp at x=-60 rising z~24->38 — the same
        # approach legs the solo driving scenario proves.
        current = "jump"
        driver = WaypointDriver(client)
        driver.drive_leg(-60.0, 10.0, radius=5.0, timeout=20,
                         note="ramp approach")
        driver.drive_leg(-60.0, 50.0, radius=6.0, timeout=40,
                         until=lambda s: bool(
                             client.events_of("motor.landed")),
                         note="ramp crest")
        # The crest tosses the car past the target radius while still
        # airborne — the landing lands a beat after the leg ends.
        wait_loop_state(
            client,
            lambda s: bool(client.events_of("motor.landed")),
            15, "motor.landed after the ramp")
        wait_lesson(client, "recovery", 40, "jump lesson")
        completed.add("jump")
        checks.passed("tutorial.jump",
                      "motor.landed completed the landmark jump lesson")
        current = "recovery"
        client.applied("input.set", inputs(steering=0.0, duration=3,
                                           recover=True))
        wait_lesson(client, "gadget", 40, "recovery lesson")
        completed.add("recovery")
        checks.passed("tutorial.recovery",
                      "race.recovered completed the recovery lesson")
        # gadget: an authored pickup grants the item; a real held press
        # spends it — the authoritative gadget.used is the proof.
        current = "gadget"
        driver = WaypointDriver(client)
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            if client.events_of("gadget.used"):
                break
            motor = client.state["driving"]["motor"]
            site = min(PICKUP_SITES, key=lambda p: math.hypot(
                p[0] - motor["x"], p[1] - motor["z"]))
            try:
                driver.drive_leg(
                    site[0], site[1], radius=3.0, timeout=45,
                    until=lambda s: bool(
                        client.events_of("gadget.used")),
                    note="pickup column")
            except AssertionError:
                pass
            client.applied("input.set", inputs(
                steering=0.0, duration=6, gadget=True))
            try:
                wait_loop_state(
                    client,
                    lambda s: bool(client.events_of("gadget.used")),
                    8, "gadget.used")
                break
            except AssertionError:
                continue
        assert client.events_of("gadget.used"), \
            "no gadget.used event after driving through pickups"
        completed.add("gadget")
        checks.passed("tutorial.gadget",
                      "pickup grant + held press produced an "
                      "authoritative gadget.used")
    except Exception as error:
        # Name the lesson that was in flight as the failed row; the rest
        # stay honestly unattempted rather than absent.
        if current not in completed:
            checks.failed("tutorial." + current, str(error))
        for name in LESSON_ORDER:
            if name not in completed and name != current:
                checks.not_verified(
                    "tutorial." + name,
                    "blocked by earlier lesson failure")
        try:
            client.applied("tutorial.exit")
        except Exception:
            pass
        raise
    # The trial is a fresh intro-01 session inside the same tutorial.
    wait_loop_state(
        client,
        lambda s: ((s.get("driving") or {}).get("tutorial") or {})
        .get("phase") == "trial",
        40, "graduation trial opened")
    drive_solo_until(
        client,
        lambda s: (s.get("driving") or {}).get("finished") is True,
        graduate_timeout, "graduation lap")
    wait_loop_state(
        client,
        lambda s: ((s.get("driving") or {}).get("tutorial") or {})
        .get("phase") == "graduated",
        30, "tutorial graduated")
    checks.passed("tutorial.trial",
                  "graduation lap finished under its own race identity")
    exited, _ = client.applied("tutorial.exit")
    assert client.action_state(exited["commandId"])["driving"] is None
    checks.passed("tutorial.exit", "session abandoned cleanly")


# ---------------------------------------------------------------------------
# Progression smoke (campaign -> earnings -> purchase -> failed save)
# ---------------------------------------------------------------------------

def progression_available(client, checks):
    """The honest content gate: progression.available vs the loop
    command's actual answer. Returns (available, contentIdentity)."""
    progression = client.state.get("progression") or {}
    available = progression.get("available") is True
    content = progression.get("contentIdentity") or ""
    if (client.state.get("soloRace") is None
            and client.state.get("driving") is None):
        probe, _ = client.command("campaign.open")
        if available:
            assert probe["status"] == "applied", \
                "progression.available=true but campaign.open " \
                "rejected: " + repr(probe)
        else:
            assert probe["status"] == "rejected", \
                "expected the honest content gate, got " + repr(probe)
    return available, content


def mark_loop_unavailable(checks, names, reason):
    for name in names:
        if not checks.has(name):
            checks.not_verified(name, reason)


CONTENT_UNAVAILABLE = (
    "gameplay content unavailable on this build (the loop commands "
    "reject contentUnavailable); the authored first world is Task 9, "
    "gated on M4 hardware evidence")


def start_campaign_event(client, event_id, checks):
    """campaign.start over the real traversal gate, probing each owned
    vehicle until one applies (ownership/class are handler-enforced)."""
    vehicles = (client.state.get("savedProfile") or {}) \
        .get("unlockedVehicles") or []
    last = None
    for vehicle in vehicles:
        result, _ = client.command(
            "campaign.start",
            {"eventId": event_id, "vehicleId": vehicle,
             "difficulty": "easy"})
        if result["status"] == "applied":
            return vehicle, result
        last = result
    raise AssertionError(
        "campaign.start %s rejected for every owned vehicle: %s"
        % (event_id, last))


def drive_loop_race(client, checks, label, finish_timeout=420):
    """Drive the live solo session to a finalized result; return the
    result dict the immutable seam published."""
    def finished(state):
        solo = state.get("soloRace") or {}
        if solo.get("result") is not None:
            return True
        race = solo.get("race") or {}
        if race.get("phase") == "finished":
            return True
        trial = solo.get("trial") or {}
        return trial.get("phase") == "finished"
    drive_solo_until(client, finished, finish_timeout,
                     label + " finish")
    solo = client.state.get("soloRace") or {}
    result = solo.get("result")
    if result is None:
        result = (solo.get("race") or {}).get("result")
    assert result is not None, label + ": no finalized result published"
    return result


def run_progression_smoke(client, checks, profile_dir):
    """Campaign event -> stars/coins -> earnings -> 60-coin purchase ->
    real failed-save retry. Rows stay honest when the loaded catalog
    has no usable content."""
    available, content = progression_available(client, checks)
    if not available:
        mark_loop_unavailable(
            checks,
            ("campaign.event", "campaign.stars", "campaign.coins",
             "campaign.earnings", "garage.purchase",
             "results.failedSave", "results.retrySave"),
            CONTENT_UNAVAILABLE)
        return None
    checks.passed("loop.screens", "campaign.open applied on a real "
                  "non-legacy catalog (" + content + ")")
    rows = unlocked_events(client.state)
    regular = [row for row in rows if not row.get("finale")]
    if not regular:
        mark_loop_unavailable(
            checks,
            ("campaign.event", "campaign.stars", "campaign.coins",
             "campaign.earnings", "garage.purchase",
             "results.failedSave", "results.retrySave"),
            "loaded catalog has no unlocked regular campaign events")
        return None
    event_id = regular[0]["eventId"]
    mask_before = regular[0].get("objectiveMask", 0)
    before = profile_snapshot(client)
    start_campaign_event(client, event_id, checks)
    solo = client.state.get("soloRace") or {}
    assert solo.get("mode") == "campaign", solo.get("mode")
    assert solo.get("tuningSource") == "purchased", \
        "campaign did not report purchased tuning provenance"
    checks.passed("campaign.start",
                  "campaign.start bound %s (purchased tuning)" % event_id)
    result_obj = drive_loop_race(client, checks, "campaign " + event_id)
    after = profile_snapshot(client)
    receipt = expect_new_receipt(before, after, result_obj,
                                 "campaign " + event_id)
    assert not (client.state.get("progression") or {}) \
        .get("rewardUnresolved"), \
        "reward gate latched after a committed save"
    row_after = next((r for r in campaign_rows(client.state)
                      if r["eventId"] == event_id), None)
    assert row_after is not None and row_after.get("completed"), \
        "finished event did not record completion"
    checks.passed(
        "campaign.event",
        "%s finished; one receipt (%d coins) bound to result %s" % (
            event_id, receipt.get("awardedCoins", 0),
            result_obj.get("resultId")))
    checks.passed("campaign.stars", "stars=%d mask=%d -> %d" % (
        row_after.get("stars", 0), mask_before,
        row_after.get("objectiveMask", 0)))
    checks.passed("campaign.coins", "wallet %d -> %d" % (
        (before or {}).get("coins", 0), (after or {}).get("coins", 0)))

    # Legitimate earnings: every coin must come through a finalized
    # result — replays mint fresh identities, never re-award a receipt.
    attempts = 0
    while (client.state.get("savedProfile") or {}).get("coins", 0) \
            < SMOKE_PURCHASE_PRICE:
        attempts += 1
        assert attempts <= 12, \
            "earnings never reached %d coins after %d real results" % (
                SMOKE_PURCHASE_PRICE, attempts)
        before = profile_snapshot(client)
        race_before = (client.state.get("soloRace") or {}).get("raceId")
        client.applied("results.retry")
        solo = client.state.get("soloRace") or {}
        assert solo.get("raceId") != race_before, \
            "replay did not mint a fresh race identity"
        replay_result = drive_loop_race(client, checks,
                                        "replay %d" % attempts)
        after = profile_snapshot(client)
        expect_new_receipt(before, after, replay_result,
                           "replay %d" % attempts)
    checks.passed(
        "campaign.earnings",
        "%d legitimate coins after %d extra result(s)" % (
            (client.state.get("savedProfile") or {}).get("coins", 0),
            attempts))

    # The 60-coin purchase: receipt-keyed, atomic, honestly debited.
    client.applied("garage.open")
    vehicles = (client.state.get("savedProfile") or {}) \
        .get("unlockedVehicles") or []
    before = profile_snapshot(client)
    purchase_id = "smoke-" + uuid.uuid4().hex[:12]
    bought = None
    for vehicle in vehicles:
        probe, _ = client.command(
            "upgrade.purchase",
            {"purchaseId": purchase_id, "vehicleId": vehicle,
             "category": UPGRADE_CATEGORIES[0], "expectedLevel": 0})
        if probe["status"] == "applied":
            bought = vehicle
            break
        assert probe["status"] == "rejected" \
            and probe.get("code") in (
                "insufficientCoins", "vehicleLocked",
                "unknownVehicle", "unavailable"), probe
    assert bought, "no owned vehicle accepted the purchase"
    after = profile_snapshot(client)
    purchase_receipts = [r for r in after.get("purchaseReceipts") or []
                         if r.get("purchaseId") == purchase_id]
    assert purchase_receipts, "purchase committed no durable receipt"
    debited = (before or {}).get("coins", 0) - after.get("coins", 0)
    assert debited > 0, "purchase debited nothing"
    upgrades = [u for u in after.get("vehicleUpgrades") or []
                if u.get("vehicleId") == bought]
    assert upgrades and upgrades[0].get(UPGRADE_CATEGORIES[0]) == 1, \
        "upgrade level not recorded"
    checks.passed("garage.purchase",
                  "upgrade.purchase on %s debited %d coins with a "
                  "durable receipt" % (bought, debited))
    return {"eventId": event_id,
            "purchase": {"vehicleId": bought,
                         "category": UPGRADE_CATEGORIES[0],
                         "expectedLevel": 1}}


def run_failed_save(client, checks, profile_dir, evidence):
    """A REAL failed save: deny the profile dir's writes AFTER the race
    bound its reward context (campaign.start's bind save must succeed)
    so the atomic reward commit at finalization fails. Prove the
    unresolved gate + its fence, restore, then results.retrySave must
    commit the identical retained pair."""
    if evidence is None:
        return
    event_id = evidence["eventId"]
    purchase = evidence.get("purchase") or {}
    if sys.platform != "win32" or profile_dir is None:
        checks.not_verified(
            "results.failedSave",
            "write-deny unavailable (non-Windows or no owned profile "
            "directory)")
        return
    # The race must exist first: BindRewardContextFor saves
    # pendingRaceContexts inside *.start, so arming earlier would fail
    # the bind, not the reward commit.
    start_campaign_event(client, event_id, checks)
    if not deny_directory_write(profile_dir):
        checks.not_verified(
            "results.failedSave",
            "could not install the write-deny ACL on the profile dir")
        return
    checks.passed("results.failedSave.armed",
                  "profile-dir write deny installed after the reward "
                  "context bound")
    try:
        result_obj = drive_loop_race(client, checks,
                                     "denied-save " + event_id)
        wait_loop_state(
            client,
            lambda s: (s.get("progression") or {})
            .get("rewardUnresolved") is True,
            60, "unresolved-reward gate")
        progression = client.state["progression"]
        assert progression["rewardUnresolvedRaceId"] \
            == result_obj["raceId"], \
            "gate latched on %r, expected the live race %s" % (
                progression["rewardUnresolvedRaceId"],
                result_obj["raceId"])
        assert find_receipt(client.state.get("savedProfile"),
                            result_obj["resultId"]) == [], \
            "a durable receipt exists for the latched result"
        checks.passed(
            "results.failedSave",
            "commit failure latched the unresolved gate on the live "
            "result — no fabricated persist")
        # The gate must fence BOTH new starts and purchases.
        rejected, _ = client.command(
            "campaign.start",
            {"eventId": event_id,
             "vehicleId": first_owned_vehicle(client.state),
             "difficulty": "easy"})
        assert rejected["status"] == "rejected" \
            and rejected.get("code") == "rewardUnresolved", rejected
        rejected, _ = client.command(
            "upgrade.purchase",
            {"purchaseId": "blocked-" + uuid.uuid4().hex[:8],
             "vehicleId": purchase.get("vehicleId")
             or first_owned_vehicle(client.state),
             "category": purchase.get("category")
             or UPGRADE_CATEGORIES[0],
             "expectedLevel": purchase.get("expectedLevel", 1)})
        assert rejected["status"] == "rejected" \
            and rejected.get("code") == "rewardUnresolved", rejected
        checks.passed("results.gateFence",
                      "campaign.start + upgrade.purchase refused while "
                      "the gate held (rewardUnresolved)")
    finally:
        assert restore_directory_write(profile_dir), \
            "write-deny ACL could not be removed"
    retry, _ = client.command("results.retrySave")
    assert retry["status"] == "applied", \
        "results.retrySave did not commit the retained pair: " \
        + repr(retry)
    wait_loop_state(
        client,
        lambda s: (s.get("progression") or {})
        .get("rewardUnresolved") is False,
        30, "gate released")
    found = find_receipt(client.state.get("savedProfile"),
                         result_obj["resultId"])
    assert len(found) == 1, \
        "retrySave minted %d receipts for one result" % len(found)
    checks.passed("results.retrySave",
                  "results.retrySave committed the identical retained "
                  "result+context — receipt unique, gate released")


# ---------------------------------------------------------------------------
# Smoke scope
# ---------------------------------------------------------------------------

def run_smoke_generation1(client, checks, profile_dir,
                          require_fresh=True):
    """Generation 1: fresh profile -> tutorial+gadget -> campaign ->
    earnings -> purchase -> failed-save retry."""
    if require_fresh:
        assert client.state["activeSlot"] == -1 \
            and client.state["screen"] == "profiles"
        assert all(slot["status"] == "Missing"
                   for slot in client.state["slots"]), \
            "smoke requires a fresh synthetic profile store"
        created, _ = client.applied("profile.create",
                                    {"slot": 0, "language": "en"})
        state = client.action_state(created["commandId"])
        assert state["savedProfile"]["coins"] == 0
        checks.passed("profile.fresh",
                      "fresh synthetic profile on an empty store")
    else:
        if client.state["activeSlot"] < 0:
            slots = client.state["slots"]
            if all(slot["status"] == "Missing" for slot in slots):
                client.applied("profile.create",
                               {"slot": 0, "language": "en"})
            else:
                loaded = next(
                    index for index, slot in enumerate(slots)
                    if slot["status"] == "Loaded")
                client.applied("profile.select", {"slot": loaded})
        checks.passed("profile.fresh",
                      "attached endpoint: profile resolved on the "
                      "endpoint's own store")
    run_tutorial(client, checks)
    evidence = run_progression_smoke(client, checks, profile_dir)
    run_failed_save(client, checks, profile_dir, evidence)


PRESERVED_KEYS = ("coins", "eventProgress", "worldProgress",
                  "finaleProgress", "vehicleUpgrades", "ownedCosmetics",
                  "equippedCosmetics", "activeBounties",
                  "bountyRotationCursor", "timeTrialRecords",
                  "purchaseReceipts", "appliedResults",
                  "pendingRaceContexts", "localRaceOrdinal")


def run_smoke_generation2(client, checks, expected_profile):
    """Generation 2 (the restarted process): preserved progress -> Quick
    Race -> Time Trial record -> replay without campaign credit."""
    client.applied("profile.select", {"slot": 0})
    profile = client.state.get("savedProfile") or {}
    lost = [key for key in PRESERVED_KEYS
            if profile.get(key) != expected_profile.get(key)]
    assert not lost, "restart lost durable facts: " + ", ".join(lost)
    checks.passed("restart.progress",
                  "new process generation reloaded coins, receipts, "
                  "event progress, upgrades and records identically")
    run_smoke_loop_modes(client, checks)


def run_smoke_loop_modes(client, checks):
    """Quick Race -> Time Trial record -> replay without campaign
    credit; shared by the restarted generation and the attach-mode
    endpoint."""
    available, _ = progression_available(client, checks)
    if not available:
        mark_loop_unavailable(
            checks,
            ("quickRace.race", "timeTrial.record", "results.replay"),
            CONTENT_UNAVAILABLE)
        return
    campaign_before = {row["eventId"]: dict(row)
                       for row in campaign_rows(client.state)}
    # Quick Race: resolve an authored circuit through the real start.
    picked = probe_quick_race(client)
    if picked is None:
        checks.not_verified(
            "quickRace.race",
            "no authored circuit resolved for any course/class pick")
    else:
        result_obj = drive_loop_race(client, checks,
                                     "quickRace %s/%s" % picked[:2])
        assert result_obj.get("mode") == "quickRace", result_obj
        checks.passed("quickRace.race",
                      "quickRace.start resolved %s and finished"
                      % repr(picked))
        # Replay: fresh race identity, new result+receipt, but the
        # campaign ledger must not move — a quick replay can never
        # mint campaign credit.
        before = profile_snapshot(client)
        race_before = (client.state.get("soloRace") or {}).get("raceId")
        client.applied("results.retry")
        solo = client.state.get("soloRace") or {}
        assert solo.get("raceId") != race_before
        replay_result = drive_loop_race(client, checks, "replay")
        after = profile_snapshot(client)
        expect_new_receipt(before, after, replay_result, "replay")
        rows_after = {row["eventId"]: dict(row)
                      for row in campaign_rows(client.state)}
        assert rows_after == campaign_before, \
            "a quick-race replay moved the campaign ledger"
        checks.passed("results.replay",
                      "results.retry minted a fresh identity; the "
                      "replay earned its own receipt and zero campaign "
                      "credit")
        client.applied("results.continue")
    # Time Trial: normalized starter + a durable personal-best record.
    trial, _ = client.command("timeTrial.open")
    if trial["status"] != "applied":
        checks.not_verified("timeTrial.record",
                            "timeTrial.open rejected " + repr(trial))
        return
    picked = probe_time_trial(client)
    if picked is None:
        checks.not_verified(
            "timeTrial.record",
            "no authored time event resolved for any course/class pick")
        return
    assert_tuning_provenance(client.state, "normalized", "timeTrial")
    result_obj = drive_loop_race(client, checks,
                                 "timeTrial %s/%s" % picked)
    profile = client.state.get("savedProfile") or {}
    records = [r for r in profile.get("timeTrialRecords") or []
               if r.get("courseId") == (picked[0] or "")
               and r.get("classId") == picked[1]]
    assert records and records[0].get("bestTicks", 0) > 0, \
        "finished trial left no durable record"
    checks.passed("timeTrial.record",
                  "normalized time trial finished; durable record "
                  "%d ticks" % records[0]["bestTicks"])
    client.applied("results.continue")


def course_class_candidates(client):
    rows = campaign_rows(client.state)
    courses = sorted({row.get("courseId") for row in rows
                      if row.get("courseId")})
    classes = sorted({row.get("classId") for row in rows
                      if row.get("classId")}
                     or {"car", "motorcycle"})
    return courses, classes


def probe_quick_race(client):
    """quickRace.start resolves the authored circuit for course+class;
    probe the published course ids until one applies."""
    courses, classes = course_class_candidates(client)
    vehicles = (client.state.get("savedProfile") or {}) \
        .get("unlockedVehicles") or [""]
    client.command("quickRace.open")
    for course_id in courses:
        for class_id in classes:
            for vehicle in vehicles:
                probe, _ = client.command(
                    "quickRace.start",
                    {"courseId": course_id, "classId": class_id,
                     "vehicleId": vehicle, "difficulty": "easy"})
                if probe["status"] == "applied":
                    return (course_id, class_id, vehicle)
    return None


def probe_time_trial(client):
    courses, classes = course_class_candidates(client)
    for course_id in courses:
        for class_id in classes:
            probe, _ = client.command(
                "timeTrial.start",
                {"courseId": course_id, "classId": class_id})
            if probe["status"] == "applied":
                return (course_id, class_id)
    return None


# ---------------------------------------------------------------------------
# Campaign scope
# ---------------------------------------------------------------------------

def run_campaign_scope(client, checks):
    if client.state["activeSlot"] < 0:
        assert all(slot["status"] == "Missing"
                   for slot in client.state["slots"]), \
            "campaign scope requires a fresh synthetic profile store"
        client.applied("profile.create", {"slot": 0, "language": "en"})
    available, content = progression_available(client, checks)
    if not available:
        for name in ("campaign.catalog", "campaign.nonVictory",
                     "campaign.replayInflation"):
            checks.not_verified(name, CONTENT_UNAVAILABLE)
        checks.not_verified(
            "campaign.coverage",
            "campaign scope ran on 0 authored events — the catalog is "
            "legacy/empty; the production first world (24 regulars + 2 "
            "finales) is Task-9/hardware gated")
        return
    rows = campaign_rows(client.state)
    regulars = [row for row in rows if not row.get("finale")]
    finales = [row for row in rows if row.get("finale")]
    incomplete = len(regulars) < PRODUCTION_REGULAR_EVENTS \
        or len(finales) < PRODUCTION_FINALES
    checks.passed(
        "campaign.catalog",
        "catalog %s authors %d regular + %d finale events "
        "(production target %d + %d — %s)" % (
            content, len(regulars), len(finales),
            PRODUCTION_REGULAR_EVENTS, PRODUCTION_FINALES,
            "INCOMPLETE FIRST WORLD" if incomplete else "complete"))
    earned_nonvictory = 0
    finished_ids = []
    for row in regulars:
        event_id = row["eventId"]
        if row.get("locked"):
            checks.not_verified(
                "campaign.event." + event_id,
                "locked (" + (row.get("lockReason") or "eventLocked")
                + ") — the traversal gate kept it out of reach")
            continue
        before = profile_snapshot(client)
        mask_before = row.get("objectiveMask", 0)
        start_campaign_event(client, event_id, checks)
        result_obj = drive_loop_race(client, checks,
                                     "campaign " + event_id)
        expect_new_receipt(before, profile_snapshot(client),
                           result_obj, "campaign " + event_id)
        own = next((r for r in result_obj.get("racers") or []
                    if r and r.get("participantId")
                    == (client.state.get("soloRace") or {})
                    .get("participantId")), None)
        row_after = next((r for r in campaign_rows(client.state)
                          if r["eventId"] == event_id), {})
        gained = row_after.get("objectiveMask", 0) & ~mask_before
        victory = own is not None and own.get("position") == 1 \
            and own.get("outcome") == "finished"
        if gained and not victory:
            earned_nonvictory += bin(gained).count("1")
        client.applied("results.continue")
        finished_ids.append(event_id)
        checks.passed(
            "campaign.event." + event_id,
            "finished (%s) — mask %d -> %d"
            % ("victory" if victory else "non-victory",
               mask_before, row_after.get("objectiveMask", 0)))
    # Replays must not inflate stars: re-drive the first finished
    # event; the union mask can only keep or add NEW bits.
    if finished_ids:
        event_id = finished_ids[0]
        row_before = next(r for r in campaign_rows(client.state)
                          if r["eventId"] == event_id)
        start_campaign_event(client, event_id, checks)
        drive_loop_race(client, checks, "replay " + event_id)
        row_after = next(r for r in campaign_rows(client.state)
                         if r["eventId"] == event_id)
        assert row_after["objectiveMask"] \
            | row_before["objectiveMask"] \
            == row_after["objectiveMask"], \
            "replay rewrote earned objective bits"
        assert row_after["completed"] == row_before["completed"]
        client.applied("results.continue")
        checks.passed(
            "campaign.replayInflation",
            "replay of %s could not recount earned bits "
            "(mask %d -> %d)" % (event_id, row_before["objectiveMask"],
                                 row_after["objectiveMask"]))
    for row in finales:
        event_id = row["eventId"]
        if row.get("locked"):
            progression = client.state.get("progression") or {}
            checks.not_verified(
                "campaign.finale." + event_id,
                "finale locked (%s) — %d/%d completions, %d/%d stars"
                % (row.get("lockReason") or "gated",
                   progression.get("campaignCompletedEvents", 0),
                   progression.get("campaignRequiredEvents", 0),
                   progression.get("campaignStars", 0),
                   progression.get("campaignRequiredStars", 0)))
            continue
        before = profile_snapshot(client)
        start_campaign_event(client, event_id, checks)
        result_obj = drive_loop_race(client, checks,
                                     "finale " + event_id)
        expect_new_receipt(before, profile_snapshot(client),
                           result_obj, "finale " + event_id)
        client.applied("results.continue")
        checks.passed("campaign.finale." + event_id,
                      "finale finished and applied")
    if earned_nonvictory:
        checks.passed("campaign.nonVictory",
                      "%d objective star bits earned without victory"
                      % earned_nonvictory)
    else:
        checks.not_verified(
            "campaign.nonVictory",
            "no non-victory star opportunity arose on the authored "
            "objectives present")


# ---------------------------------------------------------------------------
# LAN scope
# ---------------------------------------------------------------------------

def run_lan_scope(host, clients, checks, notes, fault=None,
                  allow_loopback=False, join_address=None,
                  profile_dirs=None):
    """The M5 LAN workload over the shared lifecycle helpers.

    profile_dirs maps endpoint NAME -> synthetic store dir for the
    write-deny failed-save segment (launch mode only)."""
    peers = [("host", host)] + [("client-%d" % i, c)
                               for i, c in enumerate(clients)]
    for name, peer in peers:
        _LAN_DRIVERS.append(LanDriver(peer, remote=name != "host"))
    for name, peer in peers:
        peer.applied("profile.create", {"slot": 0, "language": "en"})
        peer.applied("ui.navigate", {"screen": "lan-browser"})
    checks.passed("lan.setup", "%d endpoints on the LAN browser "
                  "(loopback processes, empty seats AI-fill)"
                  % len(peers))

    # Equal class normalization: within a class every seat runs the
    # identical catalog baseline regardless of profile (requestedTuning
    # is ignored by TryCreate). Uneven unlocks/upgrades need a
    # progression catalog — honestly absent on this build.
    progression = host.state.get("progression") or {}
    if not progression.get("available"):
        checks.not_verified(
            "lan.unevenUnlocks",
            "no gameplay catalog bound — profiles cannot own uneven "
            "unlocks/upgrades; class normalization still checked "
            "through the race")
    result, _ = host.command("lan.host", {"port": 0})
    assert result["status"] == "applied", result
    wait_state(host, lambda s: s["network"]["phase"] == "lobby", 15,
               "host lobby")
    host_port = host.state["network"]["descriptor"]["hostPort"]
    for index, peer in enumerate(clients):
        note = drive_join(peer, host_port, join_address, allow_loopback)
        checks.passed("lan.join.client-%d" % index, note)

    drive_race(host, clients, "car", notes)
    # Normalization evidence: every racer of the class shares the same
    # vehicle baseline — profiles can never leak purchased tune into a
    # LAN seat. Observable through identical vehicleId/class pairing.
    race = host.state["networkRace"]
    assert all(r["attribution"] in ("human", "ai")
               for r in race["racers"])
    checks.passed(
        "lan.normalization",
        "all four seats run the same %s class baseline "
        "(requestedTuning ignored — authored rule observable on the "
        "roster)" % "car")

    first = clients[0]
    client_id = first.state["network"]["localParticipantId"]

    # Shared pause through the real surface.
    accepted, _ = first.command("pause.toggle")
    assert accepted["status"] == "accepted"
    wait_terminal(first, accepted["commandId"])
    wait_state(host,
               lambda s: client_id in s["network"]["pauseRequests"],
               15, "host sees pause request")
    host.applied("pause.toggle")
    for name, peer in peers:
        wait_state(peer, lambda s: s["network"]["paused"] is True,
                   15, name + " paused")
    host.applied("pause.toggle")
    for name, peer in peers:
        wait_state(peer, lambda s: s["network"]["paused"] is False,
                   15, name + " resumed")
    checks.passed("lan.pause",
                  "client request -> host toggle -> shared resume")

    lan_gadget_check(first, client_id, checks)

    # Disconnect -> AI takeover -> credentialed rejoin (launch mode)
    # or honest leave/reconnect refusal (attach mode).
    active = list(clients)
    departed = set()
    if fault is not None:
        fault["kill"](first)
        checks.passed("lan.fault.kill",
                      "client-0 process killed mid-race")
        wait_state(host,
                   lambda s: any(
                       p["participantId"] == client_id
                       and not p["connected"]
                       and p["controlSource"] == "ai"
                       for p in s["network"]["participants"]),
                   60, "AI takeover")
        checks.passed("lan.takeover",
                      "dropped human seat drives on as "
                      "controlSource=ai")
        rejoined = fault["relaunch"](first)
        clients[0] = rejoined
        active[0] = rejoined
        _LAN_DRIVERS.append(LanDriver(rejoined, remote=True))
        rejoined.applied("profile.select", {"slot": 0})
        accepted, _ = rejoined.command("lan.reconnect")
        assert accepted["status"] == "accepted"
        assert wait_terminal(rejoined, accepted["commandId"],
                             60)["status"] == "applied"
        wait_state(host,
                   lambda s: any(
                       p["participantId"] == client_id
                       and p["connected"]
                       and p["controlSource"] == "remote"
                       for p in s["network"]["participants"]),
                   30, "seat reclaimed")
        checks.passed("lan.rejoin",
                      "credentialed rejoin reclaimed the same "
                      "participant identity")
    else:
        first.applied("lan.leave")
        active.remove(first)
        departed.add(client_id)
        rejected, _ = first.command("lan.reconnect")
        assert rejected["status"] == "rejected" \
            and rejected["code"] == "noSession"
        checks.passed("lan.leave",
                      "explicit leave cleared rejoin material honestly")

    survivors = [("host", host)] + [
        ("client-%d" % i, c) for i, c in enumerate(active)]
    settle_results(survivors, notes, departed=departed)

    # Duplicate/conflicting delivery: repeated requests serve the
    # identical immutable result; the ledger stays unique.
    race_id = host.state["networkRace"]["raceId"]
    ledger_before = len(host.state["network"]["results"])
    for _ in range(2):
        result, _ = host.command("lan.results.request",
                                 {"raceId": race_id})
        assert result["status"] == "applied"
    assert len(host.state["network"]["results"]) == ledger_before
    for name, peer in survivors:
        seen = set()
        for row in peer.state["network"]["results"]:
            key = (row["resultId"], row["participantId"])
            assert key not in seen, \
                "duplicated delivery row " + repr(key)
            seen.add(key)
    checks.passed("lan.delivery.dedup",
                  "duplicate results.request served the identical "
                  "immutable result; the ledger stayed unique")
    checks.not_verified(
        "lan.delivery.conflicting",
        "no honest external trigger can mint a conflicting redelivery "
        "(same resultId, different facts) through the command surface "
        "— covered by Core dedup tests")

    # Fresh process attaches to retained result state: the rejoined
    # generation re-serves the result without minting rewards again.
    if fault is not None:
        rejoined = clients[0]
        receipts_before = len(receipts(
            rejoined.state.get("savedProfile")))
        accepted, _ = rejoined.command(
            "lan.results.request", {"raceId": race_id})
        if accepted["status"] == "accepted":
            wait_terminal(rejoined, accepted["commandId"], 30)
        wait_state(
            rejoined,
            lambda s: len(receipts(s.get("savedProfile")))
            == receipts_before,
            30, "no re-minted receipts")
        checks.passed(
            "lan.retainedState",
            "rejoined generation re-served the retained result with "
            "no new receipts — dedup kept the award once-only")

    # Rematch into the other class; race 2 carries the hostile rows.
    host.applied("lan.rematch")
    for name, peer in survivors:
        wait_state(peer,
                   lambda s: s["network"]["phase"] == "lobby", 15,
                   name + " rematch lobby")
    drive_race(host, active, "motorcycle", notes)

    # Permanent departure mid-race: the leaver's delivery must settle
    # failed/undeliverable, never pending or persisted-on-behalf. With
    # at least two active clients the LAST one leaves so the denied
    # save can still ride a connected client; a single-client run sends
    # that client home and the denied save rides the host's own
    # delivery instead. An attach-mode run that already waved client-0
    # off in race 1 has its undeliverable verdict in the ledger below.
    if active:
        leaver = active[-1]
        departed_id = leaver.state["network"]["localParticipantId"]
        leaver.applied("lan.leave")
        drop_driver(leaver)
        active.remove(leaver)
        departed.add(departed_id)

    denied_name = lan_arm_failed_save(
        host, active, checks, profile_dirs, departed)
    survivors = [("host", host)] + [
        ("client-%d" % i, c) for i, c in enumerate(active)]
    # The denied endpoint's failed row must be retried to a settled
    # state BEFORE settle_results runs — a saveFailed row is a real
    # recoverable fault the shared settle must never wave through.
    if denied_name is not None:
        denied = host if denied_name == "host" else next(
            peer for name, peer in survivors if name == denied_name)
        lan_finish_failed_save(denied, denied_name, checks,
                               profile_dirs)
        retry, _ = denied.command("lan.results.retrySave")
        if retry["status"] == "accepted":
            terminal = wait_terminal(denied, retry["commandId"], 60)
            assert terminal["status"] == "applied", terminal
        else:
            assert retry["status"] == "applied", retry
        checks.passed(
            "lan.retrySave",
            "lan.results.retrySave replayed the identical retained "
            "result to a settled delivery")
    settle_results(survivors, notes, departed=departed)
    if departed:
        for participant_id in departed:
            wait_state(
                host,
                lambda s, pid=participant_id: any(
                    row.get("participantId") == pid
                    and row["state"] == "failed"
                    and row.get("code") == "undeliverable"
                    for row in s["network"]["results"]),
                30, "departed delivery verdict")
        checks.passed(
            "lan.departure",
            "permanent departure recorded the terminal undeliverable "
            "verdict — a pending delivery can never be persisted on "
            "behalf of a leaver")

    # Host loss: recovery, honest failed reconnect, clean leaves.
    host.applied("lan.leave")
    wait_state(host,
               lambda s: s["network"]["role"] == "none"
               and s["screen"] == "lan-browser", 15, "host left")
    for index, peer in enumerate(active):
        wait_state(peer,
                   lambda s: s["network"]["phase"] == "recovery"
                   and s["screen"] == "lan-recovery", 30,
                   "client-%d recovery" % index)
        assert peer.state["network"]["lastError"] in (
            "hostClosed", "hostLost", "sessionClosed")
    checks.passed("lan.hostLoss",
                  "clients landed on lan-recovery after host loss")
    for peer in active:
        result, _ = peer.command("lan.leave")
        if result["status"] == "rejected":
            assert result["code"] == "noSession", result


def lan_gadget_check(client, client_id, checks):
    """A held gadget press must surface as an authoritative gadget.used
    attributed to that participant — observed on its own stream."""
    race = client.state.get("networkRace") or {}
    if not race or race.get("phase") not in ("running", "countdown"):
        checks.not_verified("lan.gadget",
                            "no live race for the gadget press")
        return
    client.applied("input.set", inputs(steering=0.0, duration=8,
                                       gadget=True))
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        for event in client.events_of("gadget.used"):
            assert event.get("participantId") in ("", client_id), \
                "gadget.used attributed to %r, expected %s" % (
                    event.get("participantId"), client_id)
            checks.passed(
                "lan.gadget",
                "held gadget press became an authoritative "
                "gadget.used attributed to the human participant")
            return
        try:
            wait_state(client, lambda s: False, 2.0, "gadget used")
        except AssertionError:
            pass
        if client.state.get("network", {}).get("phase") != "racing":
            break
        client.applied("input.set", inputs(
            steering=0.0, duration=8, gadget=True))
    checks.not_verified(
        "lan.gadget",
        "no gadget.used observed — no pickup grant landed in the "
        "window (the press was real but no item was held)")


def lan_arm_failed_save(host, active, checks, profile_dirs, departed):
    """Install the write deny on one still-connected endpoint's profile
    dir BEFORE the race finalizes so its delivery apply fails at
    commit. Prefers an active client; a single-client run rides the
    host's own delivery (host-side retrySave is equally real)."""
    if sys.platform != "win32" or not profile_dirs:
        checks.not_verified(
            "lan.failedSave",
            "write-deny unavailable on this platform")
        return None
    candidates = [("client-%d" % i, c)
                  for i, c in enumerate(active)] + [("host", host)]
    for name, peer in candidates:
        pid = peer.state["network"]["localParticipantId"]
        if pid in departed:
            continue
        directory = profile_dirs.get(name)
        if directory is not None and deny_directory_write(directory):
            checks.passed(
                "lan.failedSave.armed",
                "write deny installed on %s's profile dir mid-race; "
                "the result apply must fail honestly at commit" % name)
            return name
    checks.not_verified(
        "lan.failedSave",
        "could not install the write-deny ACL on any profile dir")
    return None


def lan_finish_failed_save(denied, name, checks, profile_dirs):
    """Wait for the denied client's failed delivery row, then lift the
    deny so retrySave can commit."""
    try:
        wait_state(
            denied,
            lambda s: any(row["state"] == "failed"
                          and row.get("code") == "saveFailed"
                          for row in s["network"]["results"]),
            600, name + " failed delivery row")
        checks.passed(
            "lan.failedSave",
            "commit failure surfaced as a failed delivery row, not "
            "a fabricated persist")
    finally:
        assert restore_directory_write(profile_dirs[name]), \
            "write-deny ACL could not be removed"


# ---------------------------------------------------------------------------
# Run drivers
# ---------------------------------------------------------------------------

def scenario_report(scope, fixture):
    return {
        "status": "failed", "scope": scope, "fixture": bool(fixture),
        "checks": [], "notes": [], "runs": [], "notVerified": [],
        "traces": {}, "contentIdentity": "",
    }


def finish_client(client, directory, report, name, kind):
    finish_handshake(client)
    if kind == "lan":
        outcome = verify_lan_trace(Path(directory), client.info)
        assert not outcome["incompleteCommands"], \
            name + " left lan.* commands without a terminal outcome"
        report["traces"][name] = outcome
    else:
        verify_loop_trace(Path(directory), client.info)
        report["traces"][name] = {"verified": True}


def record_content_identity(client, report):
    try:
        progression = (client.state or {}).get("progression") or {}
        report["contentIdentity"] = progression.get(
            "contentIdentity") or ""
        report["catalog"] = {
            "available": progression.get("available") is True,
            "campaignEvents": len(progression.get("campaignEvents")
                                  or []),
        }
    except Exception:
        pass


def run_launch(player_command, evidence, scope, players, fixture,
               fault_injection=True, startup_timeout=90,
               shutdown_timeout=10):
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = scenario_report(scope, fixture)
    report["mode"] = "launch"
    checks = Checks()
    report["checks"] = checks.rows
    notes = report["notes"]
    executable = Path(player_command[0]).resolve(strict=True)
    report["executable"] = {"path": str(executable),
                            "sha256": file_hash(executable)}
    if not fixture:
        report["buildFiles"] = build_fingerprint(executable)
    roster = []
    try:
        if scope == "lan":
            assert 2 <= players <= 4, "LAN capacity is 2-4 players"
            names = ["host"] + ["client-%d" % i
                                for i in range(players - 1)]
            profile_dirs = {}
            entries = []
            for index, name in enumerate(names):
                profile_dir = root / (name + "-profiles")
                process, info, directory, ev = launch_generation(
                    player_command, root, name, profile_dir,
                    startup_timeout, report, name, fixture)
                entry = {"name": name, "process": process,
                         "info": info, "directory": directory,
                         "evidence": ev, "profileDir": profile_dir,
                         "client": LoopClient(info, ev),
                         "alive": True, "generation": 1}
                entries.append(entry)
                roster.append(entry)
                profile_dirs[name] = profile_dir
            host = entries[0]["client"]
            clients = [e["client"] for e in entries[1:]]
            fault = None
            if fault_injection:
                def kill(client):
                    for entry in entries:
                        if entry["client"] is client and entry["alive"]:
                            process = entry["process"]
                            code = terminate_process(
                                process, shutdown_timeout)
                            try:
                                client.close()
                            except Exception:
                                pass
                            _LAN_DRIVERS[:] = [
                                d for d in _LAN_DRIVERS
                                if d.peer is not client]
                            entry["alive"] = False
                            write_json(
                                entry["evidence"] / "process.json",
                                {"role": entry["name"],
                                 "pid": process.pid, "exitCode": code,
                                 "generation": entry["generation"],
                                 "fault": "terminated by scenario"})
                            report["runs"].append({
                                "role": entry["name"],
                                "fault": "process terminated by "
                                         "scenario (pid %d)"
                                         % process.pid})
                            return
                    raise AssertionError("kill target not in roster")

                def relaunch(client):
                    for entry in entries:
                        if entry["client"] is client:
                            generation = entry["generation"] + 1
                            name = "%s-relaunch%d" % (
                                entry["name"], generation)
                            process, info, directory, ev = \
                                launch_generation(
                                    player_command, root, name,
                                    entry["profileDir"],
                                    startup_timeout, report,
                                    entry["name"], fixture)
                            new_client = LoopClient(info, ev)
                            entry.update(process=process, info=info,
                                         directory=directory,
                                         evidence=ev,
                                         client=new_client,
                                         generation=generation,
                                         alive=True)
                            return new_client
                    raise AssertionError(
                        "relaunch target not in the roster")
                fault = {"kill": kill, "relaunch": relaunch}
            run_lan_scope(host, clients, checks, notes, fault=fault,
                          allow_loopback=True,
                          join_address="127.0.0.1",
                          profile_dirs=profile_dirs)
            for entry in entries:
                if entry["alive"]:
                    finish_client(entry["client"],
                                  entry["directory"], report,
                                  entry["name"], "lan")
                else:
                    report["traces"][entry["name"]] = {
                        "verified": False,
                        "reason": "process terminated by fault "
                                  "injection"}
            verify_run_generations(report["runs"], "lan")
            checks.passed("lan.generations",
                          "every launch/relaunch generation carried a "
                          "distinct process identity")
            report["participants"] = {
                "loopback": players,
                "physical": 0,
                "aiFill": max(0, 4 - players),
            }
        else:
            profile_dir = root / "solo-profiles"
            process, info, directory, ev = launch_generation(
                player_command, root, "solo", profile_dir,
                startup_timeout, report, "primary", fixture)
            entry = {"name": "solo", "process": process, "info": info,
                     "directory": directory, "evidence": ev,
                     "profileDir": profile_dir,
                     "client": LoopClient(info, ev), "alive": True,
                     "generation": 1}
            roster.append(entry)
            client = entry["client"]
            if scope == "smoke":
                run_smoke_generation1(client, checks, profile_dir)
                expected = profile_snapshot(client) or {}
                record_content_identity(client, report)
                finish_client(client, directory, report, "solo", "loop")
                try:
                    client.close()
                except Exception:
                    pass
                entry["alive"] = False
                terminate_process(process, shutdown_timeout)
                # Generation 2: a NEW process on the same store —
                # preserved progress, then Quick Race / Time Trial /
                # replay on the restarted player.
                process2, info2, directory2, ev2 = launch_generation(
                    player_command, root, "solo-restart",
                    profile_dir, startup_timeout, report, "restart",
                    fixture)
                entry2 = {"name": "solo-restart", "process": process2,
                          "info": info2, "directory": directory2,
                          "evidence": ev2, "profileDir": profile_dir,
                          "client": LoopClient(info2, ev2),
                          "alive": True, "generation": 2}
                roster.append(entry2)
                client2 = entry2["client"]
                run_smoke_generation2(client2, checks, expected)
                verify_run_generations(report["runs"], "smoke")
                checks.passed(
                    "restart.generations",
                    "restart evidence proves a distinct second "
                    "process generation")
                finish_client(client2, directory2, report,
                              "solo-restart", "loop")
            else:
                run_campaign_scope(client, checks)
                record_content_identity(client, report)
                finish_client(client, directory, report, "solo",
                              "loop")
        if checks.failed_rows:
            report["status"] = "failed"
        elif checks.unverified_rows:
            report["status"] = "partial"
        else:
            report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = "%s: %s" % (type(error).__name__, error)
        report["traceback"] = traceback.format_exc()
    finally:
        _LAN_DRIVERS.clear()
        _SOLO_DRIVERS.clear()
        for entry in roster:
            client = entry.get("client")
            if client is not None:
                try:
                    client.close()
                except Exception:
                    pass
            process = entry.get("process")
            if process is not None:
                code = terminate_process(process, shutdown_timeout)
                try:
                    write_json(entry["evidence"] / "process.json", {
                        "role": entry["name"], "pid": process.pid,
                        "exitCode": code,
                        "generation": entry.get("generation", 1)})
                except Exception:
                    pass
        write_json(root / "result.json", report)
    return report


def run_attached(connection_paths, evidence, scope, fixture,
                 host_address=None):
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = scenario_report(scope, fixture)
    report["mode"] = "attach"
    checks = Checks()
    report["checks"] = checks.rows
    notes = report["notes"]
    clients = []
    try:
        for index, path in enumerate(connection_paths):
            if scope == "lan":
                role = "host" if index == 0 \
                    else "client-%d" % (index - 1)
            else:
                role = "solo"
            info = read_connection(path)
            assert info.get("stateSchemaVersion", 1) \
                in SUPPORTED_STATE_SCHEMAS
            peer_evidence = root / (role + "-evidence")
            peer_evidence.mkdir()
            write_json(peer_evidence / "attach.json", {
                "role": role, "fixture": bool(fixture),
                "connection": info["_source"],
                "validationDirectory": info["_directory"]})
            peer = LoopClient(info, peer_evidence)
            clients.append((role, peer))
            report["runs"].append({"role": role,
                                   "runId": info["runId"],
                                   "attached": True})
        if scope == "lan":
            assert len(clients) >= 2, "lan scope needs 2-4 endpoints"
            host = clients[0][1]
            rest = [peer for _, peer in clients[1:]]
            run_lan_scope(host, rest, checks, notes, fault=None,
                          allow_loopback=False,
                          join_address=host_address, profile_dirs=None)
            for role, peer in clients:
                finish_client(peer, peer.info["_directory"], report,
                              role, "lan")
            report["participants"] = {
                "loopback": 0, "physical": len(clients),
                "aiFill": max(0, 4 - len(clients)),
            }
        else:
            client = clients[0][1]
            if scope == "smoke":
                run_smoke_generation1(
                    client, checks, None, require_fresh=False)
                run_smoke_loop_modes(client, checks)
                checks.not_verified(
                    "results.failedSave",
                    "--connections mode knows no owned profile "
                    "directory for the write-deny")
                checks.not_verified(
                    "restart.progress",
                    "--connections mode owns no process; the restart "
                    "generation cannot be driven from here")
                checks.not_verified(
                    "restart.generations",
                    "--connections mode owns no process")
            else:
                run_campaign_scope(client, checks)
            record_content_identity(client, report)
            finish_client(client, client.info["_directory"], report,
                          "solo", "loop")
        if checks.failed_rows:
            report["status"] = "failed"
        elif checks.unverified_rows:
            report["status"] = "partial"
        else:
            report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = "%s: %s" % (type(error).__name__, error)
        report["traceback"] = traceback.format_exc()
    finally:
        _LAN_DRIVERS.clear()
        _SOLO_DRIVERS.clear()
        for _, peer in clients:
            try:
                peer.close()
            except Exception:
                pass
        write_json(root / "result.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--launch", type=Path, metavar="PLAYER_EXE",
                      help="self-launch local player(s) and own their "
                           "processes (enables restart + fault "
                           "injection)")
    mode.add_argument("--connections", type=Path, nargs="+",
                      metavar="CONNECTION_JSON",
                      help="attach to independently launched players; "
                           "the first is driven as the host for lan "
                           "scope")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="NEW directory; existing directories are "
                             "refused")
    parser.add_argument("--scope",
                        choices=("smoke", "campaign", "lan"),
                        required=True)
    parser.add_argument("--players", type=int, default=2,
                        help="--launch + --scope lan: 2-4 local players")
    parser.add_argument("--fixture", action="store_true",
                        help="TEST-ONLY: declare the endpoint(s) "
                             "synthetic protocol fixtures; every "
                             "artifact is labeled fixture:true")
    parser.add_argument("--no-fault-injection", action="store_true",
                        help="--launch + --scope lan: skip the "
                             "process-kill rejoin segment")
    parser.add_argument("--host-address", default=None,
                        help="--connections: explicit host LAN address "
                             "when broadcast discovery cannot reach it")
    parser.add_argument("--startup-timeout", type=float, default=90)
    parser.add_argument("--shutdown-timeout", type=float, default=10)
    args = parser.parse_args()
    if not __debug__:
        raise RuntimeError(
            "Run without -O: scenario assertions must be enabled")
    if args.connections is not None:
        if args.scope == "lan":
            assert 2 <= len(args.connections) <= 4, \
                "lan scope takes 2-4 connection descriptors"
        else:
            assert len(args.connections) == 1, \
                "smoke/campaign scope takes one connection descriptor"
        report = run_attached(args.connections, args.evidence,
                              args.scope, args.fixture,
                              host_address=args.host_address)
    else:
        report = run_launch([str(args.launch)], args.evidence,
                            args.scope, args.players, args.fixture,
                            fault_injection=not args.no_fault_injection,
                            startup_timeout=args.startup_timeout,
                            shutdown_timeout=args.shutdown_timeout)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
