"""M4 visual-benchmark qualification scenario through the shared command path.

Usage (physical qualification — the mandated interface):

    VisualBenchmarkScenario.py --connections <host-connection.json>
        <client-connection.json> [more connection.json files]
        --evidence <new-directory> [--minutes 30]
        [--host-address <lan-ip>] [--allow-loopback]
        [--perf-report-dir <dir>]

Attaches to 2-4 independently launched players via their run-directory
connection.json descriptors — the same contract as NetworkScenario.py;
on Android these are per-device ADB-forwarded loopback endpoints. The
first listed connection is driven as the host; empty seats fill through
the normal AI fill path up to four racers.

Usage (local development — the scenario owns the processes):

    VisualBenchmarkScenario.py --launch <player.exe> [--players 2-4]
        [--no-fault-injection] [--render-profile pc|tablet]
        [--minutes 30] --evidence <new-directory>

The run is three labeled segments, all through the documented command
surface — no new gameplay bypass, no teleport, no sleeps-as-proof:

  A. Solo tutorial phase per endpoint (before any hosting): profile
     creation, locale command boundaries (he + en), the three volume
     controls through settings.update/settings.save, tutorial.start,
     tutorial.replay (voice replay re-issues the cue), honest clip-
     identity reporting, then tutorial.exit.
  B. The LAN lifecycle through real commands: lobby/ready/race/
     results/rematch on both vehicle classes, both cameras, drift,
     boost, the landmark ramp and a deduplicated recovery, the shared
     pause, a mid-race drop with AI takeover and the reconnect path,
     avatar/color continuity, immutable results with the once-only
     delivery ledger, bounded effect/audio pools and quiet presentation
     facts after session exit. A labeled screenshot set is captured at
     course landmarks while four racers are presented; a screenshot
     timeout or missing rendered frame is a failure, never a shot.
  C. A separate performance segment: fresh lobby, then real-time LAN
     race cycles until every endpoint accumulates the requested active-
     racing minutes (--minutes, default 30). The qualification floors
     are fixed — at least 1,800 observed active-racing seconds after
     the 60-second warm-up, with every class/camera combination
     represented for at least five minutes per endpoint. Warm-up,
     paused, lobby, countdown and results time are reported as
     excluded time — slow transitions can never hide in a good
     aggregate.

Every endpoint's NDJSON trace is independently verified (contiguous
sequencing, schema-5 boundaries, legal phase transitions, async
outcomes, no credential material). Loopback attachments and AI-filled
seats are labeled honestly — they are desktop automation evidence, not
cross-device or four-human acceptance.
"""

import argparse
import json
import math
import subprocess
import sys
import time
import traceback
from pathlib import Path

from AgentScenario import (BOUNDARY_TYPES, MemorySampler,
                           SUPPORTED_STATE_SCHEMAS,
                           validate_presentation)
from DrivingScenario import DrivingClient, MAX_STEPS_PER_FRAME, \
    OUTGOING_CAPACITY
from NetworkScenario import (LanDriver, _LAN_DRIVERS, drive_join,
                             drive_race, finish_handshake, LAN_ROUTE,
                             LAN_ROUTE_LENGTH, lan_inputs, occupied,
                             pump_drivers, read_connection,
                             REQUIRED_LAN_SCHEMA, settle_results,
                             verify_lan_trace, wait_state,
                             wait_terminal)
from NetworkSoak import (deliveries_settled, RaceLedger,
                         request_health, summarize_race)
from RacePresentationShots import (audio_facts, CATALOG, effects_facts)
from RestartScenario import file_hash, wait_connection, write_json


COURSE_VISUAL_ID = "sunny-island-benchmark-01"

# --- the fixed qualification contract -------------------------------------
# At least 30 active-racing minutes per endpoint after the 60-second
# capture warm-up, and every class/camera combination represented for at
# least five minutes. These floors do not scale with --minutes: a shorter
# request produces a shorter measurement and an honest failing verdict.
PERF_RACING_FLOOR_SECONDS = 1800.0
PERF_COMBO_FLOOR_SECONDS = 300.0
PERF_WARMUP_MS = 60_000
BENCH_CLASSES = ("car", "motorcycle")
BENCH_CAMERAS = ("chase", "overhead")

# Course landmarks from the authored graybox-01 gate/route geometry —
# the same coordinates the driving scenarios navigate by.
LANDMARKS = {
    "start": (0.0, -60.0), "gate1": (0.0, 25.0), "ramp": (-60.0, 32.0),
    "gate2": (55.0, 55.0), "gate3": (95.0, 0.0), "gate4": (50.0, -55.0),
}
LANDMARK_RADIUS = 12.0
LANDMARK_WINDOW_SECONDS = 120.0
HEALTH_SAMPLE_SECONDS = 60.0
# The perf drive loop's wall-clock cap scales with the requested segment
# so a stalled session fails instead of hanging forever.
WALL_LIMIT_FACTOR = 2.5
WALL_LIMIT_MIN_SECONDS = 600.0


def vehicle_class(vehicle_id):
    """The benchmark class a vehicle id belongs to; unknown ids stay
    verbatim so a surprise vehicle can never alias onto a class."""
    if isinstance(vehicle_id, str):
        if vehicle_id.startswith("car"):
            return "car"
        if vehicle_id.startswith("motorcycle"):
            return "motorcycle"
    return vehicle_id or ""


def is_loopback_host(host):
    return host in ("127.0.0.1", "localhost", "::1", "0.0.0.0", "")


def transport_label(endpoints):
    """The honest transport label for the attached descriptor set."""
    loopback = sum(1 for entry in endpoints
                   if is_loopback_host(entry.get("host")))
    if loopback == len(endpoints):
        return "loopback"
    return "mixed" if loopback else "lan"


def local_racer(state):
    """This endpoint's own racer inside a live LAN race, else None."""
    race = state.get("networkRace")
    own = (state.get("network") or {}).get("localParticipantId")
    if race is None or not own:
        return None
    for racer in race.get("racers") or []:
        if racer.get("participantId") == own:
            return racer
    return None


def camera_mode_of(state):
    cam = ((state.get("presentation") or {}).get("camera") or {})
    if cam.get("mode"):
        return cam["mode"]
    return ((state.get("driving") or {}).get("camera") or {}) \
        .get("mode") or "none"


# --- active-racing measurement over the observed stream -------------------

class PerfAccumulator:
    """Per-endpoint active-racing clock measured purely from the
    reconstructed boundary stream.

    Each sequenced record's monotonicMs interval is credited to the
    classification of the state that preceded it: warm-up (first 60 s of
    the player's capture clock), paused (solo or shared), racing (solo
    running or LAN racing — split by the endpoint's own vehicle class and
    applied camera), or the named non-racing phase. Paused and transition
    time land in `excluded`, never in the racing total the verdict reads.
    """

    def __init__(self):
        self.racing = {}      # (vehicleClass, cameraMode) -> seconds
        self.excluded = {}    # reason -> seconds
        self.boundaries = 0
        self._pending_ms = None

    def observe(self, record, state):
        ms = record.get("monotonicMs")
        if type(ms) not in (int, float) or not isinstance(state, dict):
            return
        self.boundaries += 1
        classification = self._classify(ms, state)
        # End-state attribution: the interval since the previous
        # sequenced record is credited to the state observed at its end
        # — when in doubt the boundary lands in an excluded bucket, so
        # paused/transition time can never inflate the racing total.
        if self._pending_ms is not None:
            prev = self._pending_ms
            # An interval straddling the capture warm-up edge splits:
            # everything before the edge is always warm-up, whatever
            # state the later record observed.
            if prev < PERF_WARMUP_MS < ms:
                self._credit((PERF_WARMUP_MS - prev) / 1000.0,
                             ("excluded", "warmup"))
                prev = PERF_WARMUP_MS
            delta = (ms - prev) / 1000.0
            if delta > 0:
                self._credit(delta, classification)
        self._pending_ms = ms

    def _credit(self, delta, classification):
        if classification[0] == "racing":
            key = (classification[1], classification[2])
            self.racing[key] = self.racing.get(key, 0.0) + delta
        else:
            reason = classification[1]
            self.excluded[reason] = self.excluded.get(reason, 0.0) + delta

    def _classify(self, ms, state):
        if ms < PERF_WARMUP_MS:
            return ("excluded", "warmup")
        if state.get("paused") \
                or (state.get("network") or {}).get("paused"):
            return ("excluded", "paused")
        driving = state.get("driving") or {}
        if driving.get("phase") == "running":
            return ("racing", vehicle_class(driving.get("vehicleId")),
                    camera_mode_of(state))
        net = state.get("network") or {}
        if net.get("phase") == "racing" \
                and state.get("networkRace") is not None:
            racer = local_racer(state)
            return ("racing",
                    vehicle_class((racer or {}).get("vehicleId")),
                    camera_mode_of(state))
        phase = net.get("phase") or driving.get("phase") or "menu"
        if phase == "finished":
            phase = "results"
        return ("excluded", phase)

    def total(self):
        return sum(self.racing.values())

    def combos(self):
        """JSON-friendly combo table: "<class>/<camera>" -> seconds."""
        return {"%s/%s" % key: round(value, 3)
                for key, value in sorted(self.racing.items())}


# --- pure evidence validators (the tests corrupt one dimension each) -------

def check_build_manifest(endpoints):
    """Every attached endpoint must run the same build/content/schema —
    a mixed manifest can never pass as one measured benchmark."""
    failures = []
    for field in ("buildVersion", "contentVersion",
                  "protocolVersion", "stateSchemaVersion"):
        values = {entry.get("role"): entry.get(field)
                  for entry in endpoints}
        if any(value in (None, "") for value in values.values()):
            failures.append("manifest field %s missing: %s"
                            % (field, values))
        elif len(set(values.values())) > 1:
            failures.append("endpoints disagree on %s: %s"
                            % (field, values))
    return failures


def check_streams(streams):
    """Every surviving endpoint's capture must be verified complete —
    a truncated or unfinished stream is not qualification evidence."""
    failures = []
    if not streams:
        failures.append("no endpoint streams were verified")
    for name, outcome in (streams or {}).items():
        if not outcome.get("verified"):
            failures.append("%s: stream not verified (%s)"
                            % (name, outcome.get("reason", "unknown")))
        elif outcome.get("captureComplete") is False:
            failures.append("%s: capture did not complete" % name)
    return failures


def check_rendered_samples(per_endpoint):
    """Rendered-player evidence: a headless capture or zero steady-state
    racing samples can never pass — missing rendered frames fail."""
    failures = []
    if not per_endpoint:
        failures.append("no per-endpoint rendered-frame evidence")
        return failures
    for name, entry in per_endpoint.items():
        if entry.get("renderingAvailable") is not True:
            failures.append(
                "%s: not rendered-player evidence "
                "(renderingAvailable=%r)"
                % (name, entry.get("renderingAvailable")))
        if (entry.get("racingSamples") or 0) <= 0:
            failures.append(
                "%s: zero rendered racing samples" % name)
        if entry.get("captureValid") is False:
            failures.append(
                "%s: endpoint performance capture invalidated (%s) — "
                "a truncated capture can never pass as complete "
                "evidence" % (name, entry.get("captureInvalidReason")
                              or "unspecified"))
    return failures


def check_perf_reports(perf_reports):
    """A written quit-time perf report whose own capture is invalid is
    honest bounded-pool evidence — and a verdict failure (the mandated
    capture must fit inside the recorder's bounded store). A report not
    yet written (`pendingPlayerQuit`) or never requested stays an honest
    noted gap, not a fabricated pass."""
    failures = []
    for name, entry in (perf_reports or {}).items():
        if not isinstance(entry, dict):
            continue
        if entry.get("status") == "invalid":
            failures.append(
                "%s: quit-time perf report invalid (%s)"
                % (name, entry.get("reason") or "unknown"))
        elif entry.get("status") == "validated" \
                and entry.get("captureValid") is False:
            failures.append(
                "%s: quit-time perf report capture invalid (%s)"
                % (name, entry.get("captureInvalidReason")
                   or "unspecified"))
    return failures


def check_perf_evidence(perf):
    """The fixed floors: >=1,800 observed active-racing seconds per
    endpoint plus >=300 s for every class/camera combination."""
    failures = []
    floor = (perf or {}).get("floorSeconds", PERF_RACING_FLOOR_SECONDS)
    combo_floor = (perf or {}).get("comboFloorSeconds",
                                  PERF_COMBO_FLOOR_SECONDS)
    per_endpoint = (perf or {}).get("perEndpoint") or {}
    if not per_endpoint:
        failures.append("no per-endpoint performance measurements")
        return failures
    for name, entry in per_endpoint.items():
        total = entry.get("racingSeconds") or 0.0
        if total < floor:
            failures.append(
                "%s: %.1f observed active-racing s < required %.0f s"
                % (name, total, floor))
        combos = entry.get("combos") or {}
        for cls in BENCH_CLASSES:
            for camera in BENCH_CAMERAS:
                key = "%s/%s" % (cls, camera)
                got = combos.get(key) or 0.0
                if got < combo_floor:
                    failures.append(
                        "%s: %s coverage %.1f s < required %.0f s"
                        % (name, key, got, combo_floor))
    return failures


def check_shots(shots):
    """Every recorded capture request must have landed a durable
    rendered frame — a timeout, a failed status or a missing/empty
    artifact is a failure, never a successful capture. A landmark a
    racer never approached is an honest miss, not a fake capture, but
    every endpoint must land at least one real landmark shot."""
    failures = []
    if not shots:
        return ["no landmark screenshots were recorded"]
    captured_peers = set()
    for shot in shots:
        label = "%s/%s" % (shot.get("peer"), shot.get("captureId")
                           or shot.get("landmark"))
        status = shot.get("status")
        if status == "captured":
            captured_peers.add(shot.get("peer"))
            if not (shot.get("fileBytes") or 0):
                failures.append(
                    "%s: rendered frame artifact missing/empty" % label)
        elif status == "missed":
            continue  # honest coverage gap, recorded in the manifest
        else:
            failures.append(
                "%s: screenshot %r is a failure, not a capture" % (
                    label, status))
    if not captured_peers:
        failures.append("no landmark screenshot was captured")
    for peer in {shot.get("peer") for shot in shots} - captured_peers:
        failures.append("%s: no captured landmark shot" % peer)
    return failures


def check_labels(labels, endpoints):
    """Honesty labels must match the attached descriptor set: loopback
    runs never pose as LAN evidence, and AI-filled seats are named —
    never silently counted as humans."""
    failures = []
    if not isinstance(labels, dict):
        return ["honesty labels missing"]
    loopback = sum(1 for entry in endpoints
                   if is_loopback_host(entry.get("host")))
    expected = transport_label(endpoints)
    if labels.get("transport") != expected:
        failures.append("transport labelled %r but descriptors are %r"
                        % (labels.get("transport"), expected))
    if labels.get("loopbackEndpoints") != loopback:
        failures.append("loopbackEndpoints %r != %d observed"
                        % (labels.get("loopbackEndpoints"), loopback))
    humans = len(endpoints)
    if labels.get("humanRacers") != humans:
        failures.append("humanRacers %r but %d endpoints attached"
                        % (labels.get("humanRacers"), humans))
    ai_seats = labels.get("aiFilledSeats")
    if ai_seats != max(0, 4 - humans):
        failures.append(
            "aiFilledSeats %r inconsistent with %d endpoints "
            "and four total racers" % (ai_seats, humans))
    if ai_seats and not labels.get("aiFillNoted"):
        failures.append("AI-filled seats not labelled honestly")
    if expected != "lan" and not labels.get("loopbackNoted"):
        failures.append("loopback evidence not labelled honestly")
    return failures


def evaluate_verdict(parts):
    """The assembled qualification verdict: every required evidence
    dimension must hold. Any failure string fails the run — a good
    aggregate can never hide a missing dimension."""
    failures = []
    endpoints = (parts or {}).get("endpoints") or []
    failures += check_build_manifest(endpoints)
    failures += check_streams((parts or {}).get("streams") or {})
    perf = (parts or {}).get("perf") or {}
    failures += check_rendered_samples(perf.get("perEndpoint") or {})
    failures += check_perf_evidence(perf)
    failures += check_shots((parts or {}).get("shots") or [])
    failures += check_labels((parts or {}).get("labels"), endpoints)
    failures += check_perf_reports((parts or {}).get("perfReports"))
    return failures


# --- live client -----------------------------------------------------------

class VBClient(DrivingClient):
    """Strict driving client plus the armed perf accumulator and the
    pending-landmark-shot bookkeeping for this endpoint."""

    def __init__(self, connection, evidence):
        self.perf = PerfAccumulator()
        self.perf_armed = False
        self.perf_summary = None
        # Screenshot records are correlated evidence — the bounded
        # events deque can evict them during a long capture window, so
        # they live in their own table keyed by captureId.
        self.shot_records = {}
        super().__init__(connection, evidence)

    def read(self, remaining):
        event = super().read(remaining)
        if event.get("type") == "screenshot":
            self.shot_records[event.get("captureId")] = event
        elif event.get("type") in BOUNDARY_TYPES \
                and self.state is not None and self.perf_armed:
            self.perf.observe(event, self.state)
        return event


# --- evidence helpers ------------------------------------------------------

def racer_facts(state):
    """participantId/slot -> presented cosmetic identity, for the
    avatar/color continuity check across rematches."""
    facts = {}
    for entry in (state.get("presentation") or {}).get("racers") or []:
        key = entry.get("participantId") or "slot-%s" % entry.get("slot")
        facts[key] = {
            "vehicleVisualId": entry.get("vehicleVisualId"),
            "characterId": entry.get("characterId"),
            "colorIndex": entry.get("colorIndex"),
            "poseSource": entry.get("poseSource"),
        }
    return facts


def wait_screenshot_record(peer, capture_id, timeout=15):
    """The screenshot record may already have flowed past during another
    wait — check the retained table before waiting for a fresh one."""
    record = peer.shot_records.get(capture_id)
    if record is not None:
        return record
    return peer.wait(
        lambda event: event.get("type") == "screenshot"
        and event.get("captureId") == capture_id, timeout=timeout)


def request_landmark_shots(peers, report):
    """One capture window: issue a labeled screenshot request whenever a
    peer's own racer comes within LANDMARK_RADIUS of an authored
    landmark while four racers are presented. Completions are collected
    against the stream afterwards — a timeout or a failed capture is a
    failure, never a shot. Landmarks never approached are recorded as
    honest misses."""
    pending = {name: dict(LANDMARKS) for name, _ in peers}
    issued = []
    timeout = time.monotonic() + LANDMARK_WINDOW_SECONDS
    while any(pending.values()) and time.monotonic() < timeout:
        for name, peer in peers:
            if not pending.get(name):
                continue
            try:
                peer.wait(lambda event: event["type"] in BOUNDARY_TYPES,
                          timeout=0.75,
                          on_event=lambda event: pump_drivers())
            except AssertionError:
                pass
            pump_drivers()
            state = peer.state or {}
            race = state.get("networkRace")
            if (state.get("network") or {}).get("phase") != "racing" \
                    or race is None:
                continue
            racer = local_racer(state)
            motor = ((racer or {}).get("session") or {}) \
                .get("motor") or {}
            x, z = motor.get("x"), motor.get("z")
            if type(x) not in (int, float) or type(z) not in (int, float):
                continue
            camera = camera_mode_of(state)
            for landmark, (lx, lz) in list(pending[name].items()):
                if math.hypot(x - lx, z - lz) <= LANDMARK_RADIUS:
                    capture_id = "vb-%s-%s" % (name, landmark)
                    peer.send({"type": "screenshot",
                               "captureId": capture_id})
                    issued.append({
                        "peer": name, "captureId": capture_id,
                        "landmark": landmark,
                        "requestTick": state.get("tick"),
                        "camera": camera,
                        "raceId": race.get("raceId"),
                        "vehicleId": racer.get("vehicleId"),
                        "racersPresented": len(race.get("racers") or []),
                    })
                    del pending[name][landmark]
    for (name, peer) in peers:
        for landmark in pending.get(name, {}):
            report["shots"].append({
                "peer": name, "landmark": landmark, "status": "missed",
                "reason": "racer never entered the %.0f m capture radius "
                          "inside the %.0f s window" % (
                              LANDMARK_RADIUS, LANDMARK_WINDOW_SECONDS)})
    for shot in issued:
        peer = dict(peers)[shot["peer"]]
        record = wait_screenshot_record(peer, shot["captureId"])
        shot["status"] = record.get("status")
        shot["file"] = record.get("file")
        shot["sequence"] = record.get("sequence")
        shot["monotonicMs"] = record.get("monotonicMs")
        shot["tick"] = record.get("tick")
        if record.get("status") == "captured":
            path = Path(peer.info["_directory"]) / "screenshots" \
                / (record.get("file") or "")
            shot["fileBytes"] = path.stat().st_size \
                if path.is_file() else 0
        report["shots"].append(shot)
    return issued


def check_presentation_facts(peer, name, checks):
    """The schema-5 presentation contract on this endpoint while racing:
    dressed course, applied render profile, bound catalog, four racer
    facts, bounded pools and zero missing assets."""
    presentation = peer.state.get("presentation") or {}
    assert presentation.get("courseVisualId") == COURSE_VISUAL_ID, \
        name + ": courseVisualId %r is not the dressed benchmark" \
        % presentation.get("courseVisualId")
    profile = presentation.get("renderProfileId")
    assert profile in ("pc", "tablet"), \
        name + ": no real render profile applied: %r" % profile
    racers = presentation.get("racers") or []
    assert len(racers) == 4, \
        name + ": expected four racer facts, got %d" % len(racers)
    for entry in racers:
        assert entry.get("vehicleVisualId"), \
            name + ": racer fact missing vehicleVisualId"
        assert entry.get("characterId"), \
            name + ": racer fact missing characterId"
    # The presenter publishes audio/effects facts a frame or two after
    # the racing boundary — the catalog binding has to be awaited, not
    # snapshotted (the same class of race as the shared-pause fact).
    wait_state(peer,
               lambda state: (audio_facts(state) or {})
                             .get("catalogId") == CATALOG
                             and (effects_facts(state) or {})
                             .get("catalogId") == CATALOG,
               15, name + " race presentation catalog")
    audio = audio_facts(peer.state)
    effects = effects_facts(peer.state)
    assert not audio.get("missingCues"), \
        name + ": missing audio cues %s" % audio.get("missingCues")
    assert not effects.get("missingEffects"), \
        name + ": missing effect prefabs %s" % effects.get("missingEffects")
    for entry in effects.get("pools") or []:
        assert entry.get("active", 0) <= entry.get("capacity", 0), \
            name + ": effect pool %s over capacity" % entry.get("effectId")
        assert entry.get("capacity", 0) > 0, \
            name + ": effect pool %s reports zero capacity" \
            % entry.get("effectId")
    assert len(effects.get("missingEffects") or []) <= 16, \
        name + ": missingEffects list unbounded"
    return {"profile": profile, "racerFacts": racer_facts(peer.state)}


def ensure_camera(peer, mode, timeout=20):
    """Toggle the real presentation camera until the applied fact
    reports `mode` — both cameras exercised through the same command a
    player keypress rides."""
    deadline = time.monotonic() + timeout
    while camera_mode_of(peer.state) != mode:
        before = camera_mode_of(peer.state)
        assert time.monotonic() < deadline, \
            "camera never reached " + mode
        peer.applied("camera.toggle")
        wait_state(peer,
                   lambda state: camera_mode_of(state) != before, 10,
                   "camera switch to " + mode)


def driver_of(peer):
    return next(driver for driver in _LAN_DRIVERS if driver.peer is peer)


class VBDriver(LanDriver):
    """The shared route follower with two benchmark-run hardenings.

    1. The recovery budget demotes from a scenario abort to a named
       incident: when a follower cannot rescue a beached racer inside
       LAN_MAX_RECOVERIES, the honest product outcome is a real DNF
       (delivery reports ineligible:dnf) — not a crashed harness.
       Suspending the follower lets the race settle truthfully while
       the incident stays on the record. A new race re-arms it.
    2. Projection continuity: inside the course infield the closest
       point can jump between route legs — a sliding projection both
       teleports the aim point across the course and ratchets fake
       "progress" so the stall detector never fires (the bring-up run
       watched a remote racer orbit midfield for a full DNF). The
       follower therefore anchors its projection to the corridor near
       the previous arc; a genuine respawn far along the route falls
       back to the global projection.
    """

    # How far along the route (metres) the projection may jump between
    # consecutive pumps before the aim is treated as a new anchoring.
    PROJECTION_WINDOW = 45.0

    # The 30-minute perf segment drives long enough that the stock
    # resend/moved cadence burns tens of thousands of input.set
    # identities — every command, rejections included, permanently
    # consumes one of FoundationService.IdentityCapacity = 10000
    # results, after which everything is rejected commandCapacity
    # (qual-2 evidence). The benchmark driver therefore opts into the
    # shared minimum-send-gap pacing (~3 sends/s, ~7,000 identities
    # over a full run, hold_ticks raised to outlive the resend gap).
    # The stock LanDriver stays unthrottled: throttling its short
    # races measurably beaches the follower (post-change live pass
    # needed >LAN_MAX_RECOVERIES recoveries in one race).
    MIN_SEND_TICKS = 10

    def __init__(self, peer, report, remote=False):
        self._report = report
        self._tripped = False
        self._seen_race = None
        super().__init__(peer, remote=remote)

    def _projection(self, motor):
        prev = self.prev_arc
        if prev is None:
            return LanDriver._projection(motor)
        best = None
        arc = 0.0
        half = LAN_ROUTE_LENGTH * 0.5
        for index in range(len(LAN_ROUTE)):
            ax, az = LAN_ROUTE[index - 1]
            bx, bz = LAN_ROUTE[index]
            ex, ez = bx - ax, bz - az
            seg2 = ex * ex + ez * ez
            t = 0.0 if seg2 == 0 else max(0.0, min(1.0,
                ((motor["x"] - ax) * ex + (motor["z"] - az) * ez) / seg2))
            px, pz = ax + ex * t, az + ez * t
            seg_len = math.sqrt(seg2)
            seg_arc = arc + t * seg_len
            da = abs(seg_arc - prev)
            if da > half:
                da = LAN_ROUTE_LENGTH - da
            if da <= self.PROJECTION_WINDOW:
                d2 = (px - motor["x"]) ** 2 + (pz - motor["z"]) ** 2
                if best is None or d2 < best[0]:
                    best = (d2, seg_arc, index)
            arc += seg_len
        if best is None:
            # Teleported far along the route (a recovery respawn) —
            # re-anchor globally.
            return LanDriver._projection(motor)
        return best[1], best[2]

    def pump(self):
        # A new race re-arms the follower — the suspension is per race,
        # matching the shared driver's per-race recovery budget.
        racer = self._racer()
        race_id = (racer.get("session") or {}).get("raceId") \
            if racer else None
        if race_id is not None and race_id != self._seen_race:
            self._seen_race = race_id
            self._tripped = False
            self.suspended = False
        try:
            super().pump()
        except AssertionError as error:
            if self._tripped:
                raise
            self._tripped = True
            self.suspended = True
            self._report["incidents"].append({
                "kind": "driverRecoveryBudgetExceeded",
                "detail": str(error)[:200],
                "note": "route follower gave up; the racer's DNF/"
                        "finish outcome is the honest product evidence"})


def register_drivers(peers, report):
    for name, peer in peers:
        _LAN_DRIVERS.append(VBDriver(peer, report, remote=name != "host"))


def exercise_features(host, peers, checks):
    """In-race command-path exercises for race 1: both cameras, a held
    drift, boost and the landmark ramp, a deduplicated recover, and the
    shared pause — all observed on the stream, never asserted from
    sleeps."""
    # Both cameras on every endpoint.
    for name, peer in peers:
        ensure_camera(peer, "overhead")
        ensure_camera(peer, "chase")
    checks.append("both cameras applied on every endpoint "
                  "(presentation.camera.mode chase + overhead)")

    # Held drift on the host — suspended driver so the drift hold under
    # test is not overwritten by pursuit resends.
    driver = driver_of(host)
    driver.suspended = True
    try:
        local_id = host.state["network"]["localParticipantId"]
        host.applied("input.set",
                     lan_inputs(steering=-0.6, duration=30, drift=True))
        wait_state(host,
                   lambda state: any(
                       event.get("kind") == "motor.driftStart"
                       and event.get("participantId") == local_id
                       for event in (state.get("networkRace") or {})
                       .get("events") or [])
                   or (((local_racer(state) or {}).get("session") or {})
                       .get("motor") or {}).get("drifting") is True,
                   20, "host drift")
    finally:
        driver.suspended = False
    checks.append("held drift engaged through input.set "
                  "(motor.driftStart observed)")

    # Boost and the landmark ramp: the route follower fires boost when
    # charged and crosses the ramp every lap. Accept either the recorded
    # motor events or the live motor flags — both are host-confirmed
    # stream evidence, never sleeps.
    for name, peer in peers:
        local_id = peer.state["network"]["localParticipantId"]
        deadline = time.monotonic() + 180
        boosted = False
        airborne = False
        while not (boosted and airborne) \
                and time.monotonic() < deadline:
            events = ((peer.state.get("networkRace") or {})
                      .get("events") or [])
            for event in events:
                if event.get("participantId") != local_id:
                    continue
                if event.get("kind") == "motor.boostStart":
                    boosted = True
                if event.get("kind") in ("motor.becameAirborne",
                                         "motor.landed"):
                    airborne = True
            motor = (((local_racer(peer.state) or {})
                      .get("session") or {}).get("motor") or {})
            boosted = boosted or motor.get("boostActive") is True
            airborne = airborne or motor.get("grounded") is False
            if boosted and airborne:
                break
            try:
                peer.wait(lambda event: event["type"] in BOUNDARY_TYPES,
                          timeout=1.0,
                          on_event=lambda event: pump_drivers())
            except AssertionError:
                pass
            pump_drivers()
        assert boosted, name + ": boost never observed within 180 s"
        assert airborne, \
            name + ": landmark ramp never crossed within 180 s"
    checks.append("boost and the landmark ramp observed "
                  "(boostStart/airborne evidence per racer)")

    # Deduplicated recovery: the recover press rides the action path.
    driver.suspended = True
    try:
        recover, _ = host.command(
            "input.set", lan_inputs(steering=0, duration=5, recover=True))
        assert recover["status"] == "applied", recover
        local_id = host.state["network"]["localParticipantId"]
        wait_state(host,
                   lambda state: any(
                       (event.get("kind") or "").startswith("action.")
                       and event.get("participantId") == local_id
                       for event in
                       ((state.get("networkRace") or {})
                        .get("events") or [])
                       + (state.get("drivingEvents") or [])),
                   30, "recover action acknowledgment")
    finally:
        driver.suspended = False
    checks.append("deduplicated recover acknowledged via action.* events")

    # Shared pause: the client's request lands on the host roster; the
    # host toggle pauses everyone and suspends loops/music honestly.
    first_client = peers[1][1]
    client_id = first_client.state["network"]["localParticipantId"]
    accepted, _ = first_client.command("pause.toggle")
    assert accepted["status"] == "accepted"
    terminal = wait_terminal(first_client, accepted["commandId"])
    assert terminal["status"] == "applied", terminal
    wait_state(host,
               lambda state: client_id
               in state["network"]["pauseRequests"],
               10, "host sees the pause request")
    host.applied("pause.toggle")
    for name, peer in peers:
        wait_state(peer,
                   lambda state: state["network"]["paused"] is True,
                   10, name + " paused")
    for name, peer in peers:
        # Paused voices stay live under `loops` — suspension is a held
        # voice, not a destroyed one; the honest fact is the paused flag
        # plus the bound cue surviving as "the actual cue suspended"
        # (RaceAudioPresenter.UpdateFacts). The audio fact lands a frame
        # or two after the network.paused boundary — wait for it rather
        # than snapshotting whatever the last published facts were.
        wait_state(peer,
                   lambda state: (audio_facts(state) or {})
                                 .get("paused") is True,
                   15, name + " presenter paused")
    host.applied("pause.toggle")
    for name, peer in peers:
        wait_state(peer,
                   lambda state: state["network"]["paused"] is False,
                   10, name + " resumed")
    checks.append("shared pause: client request, host toggle, shared "
                  "resume; loops/music suspended while paused")


def tutorial_phase(name, peer, checks, phases):
    """Segment A on one endpoint — before any hosting: profile, locale
    boundaries, the three volume controls, tutorial start/replay/exit
    through normal commands only."""
    assert peer.state["activeSlot"] == -1 \
        and peer.state["screen"] == "profiles", name

    # ui.language only exists on the profiles screen (activeSlot < 0) —
    # the pre-profile locale boundary, exactly where AgentScenario
    # exercises it.
    for language in ("he", "en"):
        localized, _ = peer.applied("ui.language",
                                    {"language": language})
        validate_presentation(
            peer.action_state(localized["commandId"]), language)

    created, _ = peer.applied("profile.create",
                              {"slot": 0, "language": "en"})
    assert peer.action_state(created["commandId"])["activeSlot"] == 0

    # settings.update requires the full parameter set — the three volume
    # controls under test ride the same complete frame a user save sends.
    draft, _ = peer.applied("settings.update", {
        "language": "en", "camera": "chase", "reducedCameraShake": False,
        "musicVolume": 0.4, "effectsVolume": 0.6, "voiceVolume": 0.8})
    state = peer.action_state(draft["commandId"])
    assert state["pendingSettings"] is True
    assert state["draftProfile"]["voiceVolume"] == 0.8
    saved, _ = peer.applied("settings.save")
    state = peer.action_state(saved["commandId"])
    assert state["pendingSettings"] is False
    assert state["savedProfile"]["voiceVolume"] == 0.8
    assert state["savedProfile"]["musicVolume"] == 0.4
    assert state["savedProfile"]["effectsVolume"] == 0.6

    peer.applied("ui.navigate", {"screen": "practice"})
    begun, _ = peer.applied("tutorial.start", {"vehicleId": "car-01"})
    state = peer.action_state(begun["commandId"])
    tutorial = (state.get("driving") or {}).get("tutorial") or {}
    assert tutorial.get("phase") == "lessons" \
        and tutorial.get("lessonId") == "steering", tutorial
    peer.boundary(
        lambda state: (state.get("driving") or {}).get("phase")
        == "running", timeout=30)
    replayed, _ = peer.applied("tutorial.replay")
    state = peer.action_state(replayed["commandId"])
    refreshed = (state.get("driving") or {}).get("tutorial") or {}
    assert refreshed.get("cueSequence") \
        == tutorial.get("cueSequence") + 1, refreshed
    assert refreshed.get("lessonId") == "steering", \
        "voice replay must not advance the lesson"
    clips = {"audioEn": refreshed.get("audioEn"),
             "audioHe": refreshed.get("audioHe")}

    exited, _ = peer.applied("tutorial.exit")
    state = peer.action_state(exited["commandId"])
    assert state["screen"] == "practice" and state["driving"] is None

    peer.applied("ui.navigate", {"screen": "lan-browser"})
    assert peer.state["screen"] == "lan-browser", name
    checks.append(
        name + ": profile, locale boundaries (he+en), three volume "
        "controls, tutorial start/replay/exit through normal commands")
    phases.setdefault("tutorial", {})[name] = {
        "completed": True, "voiceClipIds": clips,
        "voiceClipsPending": not any(clips.values()),
    }


def perf_segment(peers, host, clients, report, minutes, wall_seconds):
    """Segment C — the separated performance segment.

    A fresh session, then real-time LAN race cycles until every
    surviving endpoint's stream-measured active-racing time reaches the
    requested target (and, for a qualifying run, every class/camera
    combination reaches its five-minute floor). Each cycle assigns every
    endpoint the camera least covered for the race's class, accumulates
    racing/excluded time off the boundary stream, samples health against
    the outgoing-queue bound, and settles the immutable result through
    the once-only ledger."""
    checks = report["checks"]
    perf = report["perf"]
    result, _ = host.command("lan.host", {"port": 0})
    assert result["status"] == "applied", result
    wait_state(host,
               lambda state: state["network"]["phase"] == "lobby",
               10, "perf-segment host lobby")
    host_port = host.state["network"]["descriptor"]["hostPort"]
    perf["hostPort"] = host_port
    for index, peer in enumerate(clients):
        note = drive_join(peer, host_port, report["_joinAddress"],
                          report["_allowLoopback"])
        checks.append("perf segment: client-%d %s" % (index, note))
    wait_state(host,
               lambda state: len(occupied(state)) == len(peers),
               15, "perf-segment full roster")
    checks.append("perf segment: fresh session, %d endpoints, AI fill "
                  "to four racers" % len(peers))

    for name, peer in peers:
        peer.perf = PerfAccumulator()
        peer.perf_armed = True
    target = minutes * 60.0
    start = time.monotonic()
    deadline = start + wall_seconds
    perf["targetRacingSeconds"] = target
    perf["wallLimitSeconds"] = wall_seconds
    perf["cycles"] = []
    perf["healthSamples"] = {}
    ledger = RaceLedger()
    cycle = 0

    def targets_met():
        totals = [peer.perf.total() for _, peer in peers]
        if not totals or min(totals) < target:
            return False
        # A shorter bring-up request only gates the drive loop — the
        # verdict still demands the fixed 1,800 s/300 s floors.
        if target < PERF_RACING_FLOOR_SECONDS:
            return True
        for _, peer in peers:
            for cls in BENCH_CLASSES:
                for camera in BENCH_CAMERAS:
                    if peer.perf.racing.get((cls, camera), 0.0) \
                            < PERF_COMBO_FLOOR_SECONDS:
                        return False
        return True

    while not targets_met() and time.monotonic() < deadline:
        class_id = "car" if cycle % 2 == 0 else "motorcycle"
        try:
            drive_race(host, clients, class_id, checks)
        except (AssertionError, KeyError) as error:
            report["incidents"].append({
                "kind": "raceStartFailed", "cycle": cycle,
                "class": class_id, "detail": str(error)[:300]})
            break
        cls = class_id
        for name, peer in peers:
            chase = peer.perf.racing.get((cls, "chase"), 0.0)
            overhead = peer.perf.racing.get((cls, "overhead"), 0.0)
            ensure_camera(peer, "chase" if chase <= overhead
                          else "overhead")

        # Real-time race window: consume boundaries on every peer so the
        # accumulator sees each phase, keep the human cars driven, and
        # sample health against the outgoing-queue bound.
        pending = {name: peer for name, peer in peers}
        next_health = {name: 0.0 for name in pending}
        lost = {}
        while pending and time.monotonic() < deadline:
            for name, peer in list(pending.items()):
                try:
                    peer.wait(
                        lambda event: event["type"] in BOUNDARY_TYPES,
                        timeout=0.75,
                        on_event=lambda event: pump_drivers())
                except AssertionError as error:
                    # The wait-deadline AssertionError just means no
                    # boundary matched yet — keep polling. Anything else
                    # (EOF, a captureValid guard, a sequencing fault) is a
                    # real stream failure and a named incident.
                    if "Timed out" not in str(error):
                        report["incidents"].append({
                            "kind": "streamLost", "role": name,
                            "cycle": cycle, "detail": str(error)[:200]})
                        lost[name] = pending.pop(name)
                        continue
                now = time.monotonic()
                if now >= next_health[name]:
                    try:
                        health = request_health(peer)
                        perf["healthSamples"].setdefault(name, []).append(
                            {"elapsedSeconds": round(now - start, 1),
                             "outgoingRecords":
                                 health.get("outgoingRecords"),
                             "captureValid": health.get("captureValid")})
                        if health.get("captureValid") is not True:
                            report["incidents"].append({
                                "kind": "captureInvalid", "role": name,
                                "cycle": cycle})
                        if (health.get("outgoingRecords") or 0) \
                                >= OUTGOING_CAPACITY:
                            report["incidents"].append({
                                "kind": "outgoingQueueFull",
                                "role": name, "cycle": cycle,
                                "detail": repr(
                                    health.get("outgoingRecords"))})
                    except (AssertionError, KeyError, OSError) as error:
                        report["incidents"].append({
                            "kind": "streamLost", "role": name,
                            "cycle": cycle, "detail": str(error)[:200]})
                        lost[name] = pending.pop(name)
                        continue
                    next_health[name] = now + HEALTH_SAMPLE_SECONDS
                if ((peer.state or {}).get("network") or {}) \
                        .get("phase") == "results" \
                        and ((peer.state or {}).get("networkRace") or {}) \
                        .get("phase") == "finished":
                    del pending[name]
            pump_drivers()
        if lost:
            report["perf"]["lostPeers"] = sorted(lost)
            break
        if pending:
            report["incidents"].append({
                "kind": "resultsTimeout", "cycle": cycle,
                "detail": "peers never reached results inside the "
                          "wall budget"})
            break

        # Settle and ledger-record the finished race on every endpoint.
        try:
            ledger.begin_cycle()
            summaries = {}
            for name, peer in peers:
                wait_state(peer, deliveries_settled, 90,
                           name + " delivery settle")
                summary = summarize_race(peer.state, name)
                ledger.record(summary, "perf-%d %s" % (cycle, name))
                summaries[name] = summary
            perf["cycles"].append({
                "cycle": cycle, "class": class_id,
                "elapsedSeconds": round(time.monotonic() - start, 1),
                "raceId": ledger.races[-1]["raceId"],
                "resultId": ledger.races[-1]["resultId"],
            })
        except (AssertionError, KeyError) as error:
            report["incidents"].append({
                "kind": "resultLedger", "cycle": cycle,
                "detail": str(error)[:300]})
            break
        if targets_met() or time.monotonic() >= deadline:
            break
        try:
            host.applied("lan.rematch")
            for name, peer in peers:
                wait_state(
                    peer,
                    lambda state: state["network"]["phase"] == "lobby",
                    30, name + " rematch lobby")
        except (AssertionError, KeyError) as error:
            report["incidents"].append({
                "kind": "rematchFailed", "cycle": cycle,
                "detail": str(error)[:200]})
            break
        cycle += 1

    for name, peer in peers:
        peer.perf_armed = False
        summary = ((peer.state or {}).get("presentation") or {}) \
            .get("performance") or {}
        peer.perf_summary = summary
        perf["perEndpoint"][name] = {
            "racingSeconds": round(peer.perf.total(), 3),
            "combos": peer.perf.combos(),
            "excluded": {key: round(value, 3)
                         for key, value in sorted(
                             peer.perf.excluded.items())},
            "boundaries": peer.perf.boundaries,
            "racingSamples": summary.get("racingSamples"),
            "renderingAvailable":
                summary.get("renderingAvailable"),
            "medianFrameMs": summary.get("medianFrameMs"),
            "p95FrameMs": summary.get("p95FrameMs"),
            "maxFrameMs": summary.get("maxFrameMs"),
            "stallCount": summary.get("stallCount"),
            "pausedSeconds": summary.get("pausedSeconds"),
            "warmupSeconds": summary.get("warmupSeconds"),
            "captureValid": summary.get("captureValid"),
        }
    perf["wallSeconds"] = round(time.monotonic() - start, 1)
    perf["targetMet"] = targets_met()
    checks.append("perf segment: %d race cycles, %.0f s wall, "
                  "target %.0f s racing %s" % (
                      len(perf["cycles"]), perf["wallSeconds"], target,
                      "met" if perf["targetMet"] else "NOT met"))


def run_benchmark(peers, report, minutes, wall_seconds,
                  join_address=None, allow_loopback=False, fault=None):
    """The complete benchmark run over attached peers: tutorial, LAN
    lifecycle, landmark shots, then the separated performance segment."""
    checks = report["checks"]
    phases = report["phases"]
    host = peers[0][1]
    clients = [peer for _, peer in peers[1:]]

    # The shared wait path drives every attached human racer; a follower
    # that exhausts its recovery budget suspends as a named incident
    # rather than aborting the whole run.
    register_drivers(peers, report)

    # --- Segment A: solo tutorial phase (before any hosting) ---------
    for name, peer in peers:
        tutorial_phase(name, peer, checks, phases)

    # --- Segment B: the LAN lifecycle through real commands ----------
    result, _ = host.command("lan.host", {"port": 0})
    assert result["status"] == "applied", result
    wait_state(host,
               lambda state: state["network"]["phase"] == "lobby",
               10, "host lobby")
    descriptor = host.state["network"]["descriptor"]
    host_port = descriptor["hostPort"]
    assert type(host_port) is int and host_port > 0
    for index, peer in enumerate(clients):
        note = drive_join(peer, host_port, join_address, allow_loopback)
        checks.append("client-%d %s" % (index, note))
    wait_state(host,
               lambda state: len(occupied(state)) == len(peers),
               15, "host sees the full roster")
    checks.append("lobby assembled: %d endpoints, AI fill to four "
                  "racers" % len(peers))

    # Race 1 — cars: presentation facts, avatar baseline, landmark
    # shots, both cameras, drift/boost/jump/recovery, shared pause.
    drive_race(host, clients, "car", checks)
    baselines = {}
    manifest_by_role = {
        entry["role"]: entry
        for entry in report["buildManifest"]["endpoints"]}
    for name, peer in peers:
        facts = check_presentation_facts(peer, name, checks)
        baselines[name] = facts["racerFacts"]
        manifest_by_role[name]["renderProfileId"] = facts["profile"]
    checks.append("dressed course + render profile + bound catalog + "
                  "four racer facts on every endpoint")
    request_landmark_shots(peers, report)
    checks.append("landmark screenshot set issued and collected "
                  "(%d captured)" % sum(
                      1 for shot in report["shots"]
                      if shot.get("status") == "captured"))
    exercise_features(host, peers, checks)
    survivors = list(peers)
    settle_results(survivors, checks)
    phases["lifecycle"] = {"races": []}
    race_ledger = RaceLedger()
    race_ledger.begin_cycle()
    for name, peer in survivors:
        race_ledger.record(
            summarize_race(peer.state, name), "race-1 " + name)
    phases["lifecycle"]["races"].append({
        "class": "car", "raceId": race_ledger.races[-1]["raceId"],
        "resultId": race_ledger.races[-1]["resultId"]})
    checks.append("race 1 (car): immutable result + settled deliveries")

    # Rematch into race 2 — motorcycles.
    host.applied("lan.rematch")
    for name, peer in survivors:
        wait_state(peer,
                   lambda state: state["network"]["phase"] == "lobby",
                   30, name + " rematch lobby")
    drive_race(host, clients, "motorcycle", checks)

    # Avatar/color continuity: the same presented participant keeps its
    # cosmetic identity across the rematch; the vehicle visual honestly
    # follows the new class.
    for name, peer in peers:
        current = racer_facts(peer.state)
        for key, baseline in baselines[name].items():
            if key not in current:
                continue
            assert current[key]["characterId"] \
                == baseline["characterId"], \
                name + ": %s character changed across rematch" % key
            assert current[key]["colorIndex"] \
                == baseline["colorIndex"], \
                name + ": %s color changed across rematch" % key
    checks.append("avatar/color continuity: cosmetic identities stable "
                  "across the rematch")

    # Per-race counters must re-baseline — a rematch never reports the
    # previous race's presentation totals.
    for name, peer in peers:
        audio = audio_facts(peer.state)
        effects = effects_facts(peer.state)
        if audio:
            assert not audio.get("missingCues"), \
                name + ": missing cues leaked across rematch"
        if effects:
            assert not effects.get("missingEffects"), \
                name + ": missing effects leaked across rematch"
    checks.append("rematch re-baselined per-race presentation counters")

    # Mid-race drop: explicit leave exercises the same host-observed
    # outcome as a process kill (AI takeover) and the reconnect path —
    # an explicit leave discards rejoin material, so the honest verdict
    # is a noSession rejection. Launch mode replaces this with a real
    # process kill and credentialed rejoin.
    first_client = clients[0]
    dropped_id = first_client.state["network"]["localParticipantId"]
    # `live` tracks the current generation of every endpoint: a fault
    # casualty's frozen mid-race state is evidence, not a live peer.
    live = list(peers)
    if fault is not None:
        fault["kill"](first_client)
        report["faultInjection"].append(
            "client-0 process killed mid-race")
        survivors = [entry for entry in survivors
                     if entry[1] is not first_client]
    else:
        first_client.applied("lan.leave")
        survivors = [entry for entry in survivors
                     if entry[1] is not first_client]
    wait_state(host,
               lambda state: any(
                   p["participantId"] == dropped_id
                   and not p["connected"]
                   and p["controlSource"] == "ai"
                   for p in state["network"]["participants"]),
               60, "host marks the dropped seat AI-controlled")
    checks.append("mid-race drop: seat drives on as controlSource=ai")
    if fault is not None:
        rejoined = fault["relaunch"](first_client)
        clients[0] = rejoined
        survivors.append(("client-0", rejoined))
        live = [(name, rejoined if peer is first_client else peer)
                for name, peer in live]
        _LAN_DRIVERS.append(VBDriver(rejoined, report, remote=True))
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
                   30, "host reclaims the rejoined seat")
        checks.append("FAULT-INJECTION: credentialed lan.reconnect "
                      "reclaimed the reserved seat")
        report["faultInjection"].append(
            "client-0 relaunched; credentialed rejoin applied")
    else:
        rejected, _ = first_client.command("lan.reconnect")
        assert rejected["status"] == "rejected" \
            and rejected["code"] == "noSession", rejected
        checks.append("reconnect path exercised: explicit leave "
                      "discards material; lan.reconnect rejected "
                      "noSession honestly")

    settle_results(survivors, checks)
    race_ledger.begin_cycle()
    for name, peer in survivors:
        race_ledger.record(
            summarize_race(peer.state, name), "race-2 " + name)
    phases["lifecycle"]["races"].append({
        "class": "motorcycle",
        "raceId": race_ledger.races[-1]["raceId"],
        "resultId": race_ledger.races[-1]["resultId"]})
    checks.append("race 2 (motorcycle): immutable result + once-only "
                  "deliveries across rematches")

    # Duplicate result retrieval serves the identical immutable result.
    race_id = host.state["networkRace"]["raceId"]
    ledger_before = len(host.state["network"]["results"])
    for _ in range(2):
        result, _ = host.command("lan.results.request",
                                 {"raceId": race_id})
        assert result["status"] == "applied", result
    assert len(host.state["network"]["results"]) == ledger_before
    checks.append("duplicate results.request served identically; "
                  "ledger unchanged")

    # Session teardown: host leave broadcasts the close; every client
    # lands on recovery and leaves through the normal command.
    host.applied("lan.leave")
    wait_state(host,
               lambda state: state["network"]["role"] == "none"
               and state["screen"] == "lan-browser",
               15, "host left")
    for index, peer in enumerate(
            peer for _, peer in survivors if peer is not host):
        wait_state(peer,
                   lambda state: state["network"]["phase"]
                   in ("recovery", "idle"),
                   30, "client recovery")
        result, _ = peer.command("lan.leave")
        if result["status"] == "rejected":
            assert result["code"] == "noSession", result
        else:
            assert result["status"] == "applied", result
        wait_state(peer,
                   lambda state: state["network"]["role"] == "none",
                   15, "client left")
    # Cleanup after race exit: presentation facts go quiet — no live
    # loops, no music, no active effects survive session teardown.
    # Only the live generation is checked; a fault casualty's frozen
    # mid-race state is honest evidence, not a leak. The quiet facts
    # publish a frame or two after the role boundary — wait for them.
    def quiet(state):
        audio = audio_facts(state)
        if audio is not None \
                and (audio.get("loops")
                     or audio.get("musicCue") not in (None, "none")
                     or audio.get("paused") is True):
            return False
        effects = effects_facts(state)
        if effects is not None and effects.get("activeEffects", 0) != 0:
            return False
        return True

    for name, peer in live:
        wait_state(peer, quiet, 15,
                   name + ": presentation facts still live after "
                          "session exit")
    checks.append("session teardown: every endpoint on lan-browser; "
                  "presentation facts quiet (no loops/music/effects)")

    # --- Segment C: the separated performance segment -----------------
    # Rebuild the live roster — a fault-injected client may have been
    # replaced by a relaunched generation, and a voluntarily-left client
    # rejoins the fresh perf session through lan.join.
    report["_joinAddress"] = join_address
    report["_allowLoopback"] = allow_loopback
    perf_segment(live, host, clients, report, minutes, wall_seconds)


def endpoint_manifest(name, peer, info):
    hello = peer.hello or {}
    snapshot = next((event for event in peer.events
                     if event.get("type") == "snapshot"), {})
    return {
        "role": name, "runId": info["runId"],
        "host": info.get("host", "127.0.0.1"), "port": info["port"],
        "deviceId": snapshot.get("deviceId"),
        "buildVersion": snapshot.get("buildVersion")
        or hello.get("buildVersion"),
        "contentVersion": snapshot.get("contentVersion")
        or hello.get("contentVersion"),
        "protocolVersion": 1,
        "stateSchemaVersion": hello.get("stateSchemaVersion", 1),
    }


def attach_peers(connection_paths, root, report):
    peers = []
    for index, path in enumerate(connection_paths):
        role = "host" if index == 0 else "client-%d" % (index - 1)
        info = read_connection(path)
        peer_evidence = root / (role + "-evidence")
        peer_evidence.mkdir()
        write_json(peer_evidence / "attach.json", {
            "role": role, "connection": str(Path(path).resolve()),
            "validationDirectory": info["_directory"],
        })
        peer = VBClient(info, peer_evidence)
        peers.append((role, peer))
        report["players"].append({
            "role": role, "runId": info["runId"],
            "connection": str(Path(path).resolve()),
            "validationDirectory": info["_directory"],
        })
        report["buildManifest"]["endpoints"].append(
            endpoint_manifest(role, peer, info))
    return peers


def finish_and_verify(peers, report):
    """Health+finish handshakes first (waits drain peer sockets), then
    per-endpoint trace verification."""
    for name, peer in peers:
        finish_handshake(peer)
    for name, peer in peers:
        outcome = verify_lan_trace(Path(peer.info["_directory"]),
                                   peer.info)
        assert not outcome["incompleteCommands"], \
            name + " left lan.* commands without a terminal outcome"
        report["traces"][name] = {
            "verified": True, "events": outcome["events"],
            "phases": outcome["phases"],
            # verify_lan_trace asserts capture-status.json's
            # captureComplete before returning verified.
            "captureComplete": True,
        }
        # Stepper health is reported per endpoint: a transient peak
        # (a GC spike or a screenshot encode) drops a handful of sim
        # ticks and is honest measured evidence — only a still-
        # saturated stepper at finish means the player never caught
        # up, which invalidates the wall-clock measurement.
        presentation = (peer.state or {}).get("presentation") or {}
        report["streamHealth"][name] = {
            "boundaries": peer.boundaries,
            "maxBacklogSteps": peer.max_backlog_steps,
            "stepOverloads":
                presentation.get("stepOverloads"),
            "finalBacklogSteps":
                presentation.get("backlogSteps"),
        }
        assert (presentation.get("backlogSteps") or 0) \
            < MAX_STEPS_PER_FRAME, \
            name + " stepper still saturated at finish (%s backlog)" \
            % presentation.get("backlogSteps")


def collect_perf_reports(run_ids, report, report_dir, timeout=15.0):
    """The compact report written at player quit: validate it where the
    launch/operator placed it; its absence stays a named gap, never a
    fabricated measurement."""
    if report_dir is None:
        for name in run_ids:
            report["perfReports"][name] = {
                "status": "pendingPlayerQuit",
                "note": "no --perf-report-dir supplied; the report is "
                        "written by the player at quit",
            }
        return
    deadline = time.monotonic() + timeout
    for name, run_id in run_ids.items():
        path = Path(report_dir) / ("perf-report-%s.json" % run_id)
        while not path.is_file() and time.monotonic() < deadline:
            time.sleep(0.25)
        if not path.is_file():
            report["perfReports"][name] = {
                "status": "pendingPlayerQuit",
                "note": "perf-report-%s.json not found under %s — "
                        "written at player quit" % (run_id, report_dir),
            }
            continue
        entry = validate_perf_report_file(path)
        report["perfReports"][name] = entry


def validate_perf_report_file(path):
    """Validate one on-disk perf report: reportVersion 1 wrapper, the
    capture-validity verdict (recorded — an invalid capture is honest
    bounded-pool evidence and fails the run verdict, never a crash),
    rendered-player evidence with real racing samples, and the
    excluded-time accounting preserved."""
    path = Path(path)
    entry = {"file": path.name, "sha256": file_hash(path)}
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        entry.update({"status": "invalid", "reason": str(error)[:200]})
        return entry
    state = payload.get("state") or {}
    entry["captureValid"] = state.get("captureValid")
    entry["captureInvalidReason"] = state.get("captureInvalidReason")
    problems = []
    if payload.get("reportVersion") != 1:
        problems.append("reportVersion %r"
                        % payload.get("reportVersion"))
    if state.get("captureValid") is False:
        problems.append("capture invalid: %s"
                        % state.get("captureInvalidReason"))
    if state.get("renderingAvailable") is not True:
        problems.append("headless capture cannot be rendered evidence")
    if state.get("evidenceStatus") != "collected":
        problems.append("evidenceStatus %r"
                        % state.get("evidenceStatus"))
    if (state.get("racingSamples") or 0) <= 0:
        problems.append("report carries no racing samples")
    segments = state.get("segments") or []
    racing_seconds = sum(
        seg.get("seconds") or 0.0 for seg in segments
        if seg.get("phase") == "racing"
        and seg.get("warmup") is not True)
    excluded = {}
    for seg in segments:
        if seg.get("warmup") is True:
            key = "warmup"
        elif seg.get("phase") == "racing":
            continue
        else:
            key = seg.get("phase") or "unknown"
        excluded[key] = excluded.get(key, 0.0) \
            + (seg.get("seconds") or 0.0)
    if problems:
        entry.update({"status": "invalid", "reason": "; ".join(problems)})
    else:
        entry["status"] = "validated"
    entry.update({
        "profile": state.get("profile"),
        "buildIdentity": state.get("buildIdentity"),
        "contentVersion": state.get("contentVersion"),
        "racingSamples": state.get("racingSamples"),
        "medianFrameMs": state.get("medianFrameMs"),
        "p95FrameMs": state.get("p95FrameMs"),
        "maxFrameMs": state.get("maxFrameMs"),
        "stallCount": state.get("stallCount"),
        "droppedFrames": state.get("droppedFrames"),
        "observedSeconds": state.get("observedSeconds"),
        "racingSecondsExcludingWarmup": round(racing_seconds, 3),
        "excludedSeconds": {key: round(value, 3)
                            for key, value in sorted(excluded.items())},
        "segments": len(segments),
    })
    return entry


def assemble_verdict(peers, report):
    """Fold every evidence dimension into the final verdict."""
    parts = {
        "endpoints": report["buildManifest"]["endpoints"],
        # Fault-injection casualties are expected evidence, not live
        # streams — they are recorded in the report but never asked to
        # verify.
        "streams": {name: entry
                    for name, entry in report["traces"].items()
                    if not entry.get("faultCasualty")},
        "perf": report["perf"],
        "shots": report["shots"],
        "labels": report["labels"],
        "perfReports": report.get("perfReports"),
    }
    report["failures"] = evaluate_verdict(parts)
    if report.get("incidents"):
        report["failures"] += [
            "incident %s%s" % (
                incident["kind"],
                " (%s)" % incident.get("role", incident.get("cycle", "")))
            for incident in report["incidents"]
            if incident["kind"] in (
                "captureInvalid", "outgoingQueueFull", "raceStartFailed",
                "resultLedger", "resultsTimeout", "rematchFailed",
                "streamLost")]
    report["status"] = "passed" if not report["failures"] else "failed"
    return report["status"]


def build_labels(peers, report):
    endpoints = report["buildManifest"]["endpoints"]
    loopback = sum(1 for entry in endpoints
                   if is_loopback_host(entry.get("host")))
    transport = transport_label(endpoints)
    ai_seats = max(0, 4 - len(peers))
    labels = {
        "transport": transport,
        "loopbackEndpoints": loopback,
        # ADB-forwarded descriptors present as loopback endpoints; the
        # operator's join notes name them. There is no way for the
        # scenario to detect adb forward — the honest label is loopback.
        "adbForwarded": False,
        "humanRacers": len(peers),
        "aiFilledSeats": ai_seats,
        "aiFillNoted": True,
        "loopbackNoted": transport != "lan",
    }
    report["labels"] = labels
    if transport != "lan":
        report["notVerified"].append(
            "cross-device LAN reachability — %s attach is same-machine "
            "desktop automation evidence" % transport)
    if ai_seats:
        report["notVerified"].append(
            "%d AI-filled seat(s): four-racer load exercised but not "
            "four-human acceptance" % ai_seats)
    return labels


def run_attached(connection_paths, evidence, minutes, host_address=None,
                 allow_loopback=False, perf_report_dir=None,
                 wall_seconds=None):
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "attached %d-endpoint visual-benchmark qualification "
                 "through the shared command path" % len(connection_paths),
        "minutesRequested": minutes,
        "players": [], "checks": [], "shots": [],
        "buildManifest": {"endpoints": []},
        "phases": {}, "incidents": [], "faultInjection": [],
        "traces": {}, "summaries": {}, "perfReports": {},
        "streamHealth": {},
        "perf": {"floorSeconds": PERF_RACING_FLOOR_SECONDS,
                 "comboFloorSeconds": PERF_COMBO_FLOOR_SECONDS,
                 "perEndpoint": {}},
        "labels": {}, "failures": [],
        "notVerified": [
            "physical keyboard/touch input and on-device UI parity",
            "visual/Hebrew/audio quality on devices (desktop automation "
            "evidence only)",
            "ordinary-build performance — validation endpoints are "
            "compiled out of ordinary builds, so no agent can attach; "
            "see the documented ordinary-build procedure",
            "credentialed rejoin after process death — attach mode "
            "exercises the honest noSession outcome (use --launch for "
            "fault injection)",
        ],
    }
    peers = []
    wall_seconds = wall_seconds or max(
        minutes * 60.0 * WALL_LIMIT_FACTOR, WALL_LIMIT_MIN_SECONDS)
    try:
        assert 2 <= len(connection_paths) <= 4, \
            "--connections takes 2-4 connection descriptors"
        peers = attach_peers(connection_paths, root, report)
        for name, peer in peers:
            assert peer.hello.get("stateSchemaVersion") \
                == REQUIRED_LAN_SCHEMA, \
                name + " did not advertise schema %d" \
                % REQUIRED_LAN_SCHEMA
        build_labels(peers, report)
        report["_joinAddress"] = host_address
        report["_allowLoopback"] = allow_loopback
        run_benchmark(peers, report, minutes, wall_seconds,
                      join_address=host_address,
                      allow_loopback=allow_loopback)
        finish_and_verify(peers, report)
        for name, peer in peers:
            report["summaries"][name] = peer.perf_summary or {}
        collect_perf_reports(
            {name: peer.info["runId"] for name, peer in peers},
            report, perf_report_dir)
        assemble_verdict(peers, report)
    except Exception as error:
        report["error"] = "%s: %s" % (type(error).__name__, error)
        report["traceback"] = traceback.format_exc()
    finally:
        _LAN_DRIVERS.clear()
        for name, peer in peers:
            try:
                peer.close()
            except Exception:
                pass
        for key in ("_joinAddress", "_allowLoopback"):
            report.pop(key, None)
        write_json(root / "result.json", report)
    return report


def launch_player(player_command, root, name, profile_dir, perf_dir,
                  trace_mb, render_profile, startup_timeout):
    """One benchmark player with a stable profile dir, the perf-report
    destination and a raised-but-bounded trace ceiling for the long
    segment."""
    directory = root / (name + "-run")
    log_path = root / (name + "-player.log")
    perf_dir.mkdir(parents=True, exist_ok=True)
    command = [*player_command,
               "--validation-dir", str(directory),
               "--validation-profile-dir", str(profile_dir),
               "--perf-report-dir", str(perf_dir),
               "--validation-trace-mb", str(trace_mb)]
    if render_profile:
        command += ["--render-profile", render_profile]
    command += ["-logFile", str(log_path)]
    with log_path.open("wb") as output:
        process = subprocess.Popen(command, stdout=output,
                                   stderr=subprocess.STDOUT)
    info = wait_connection(process, directory, startup_timeout)
    info["command"] = command
    return process, info, directory


def run_scenario(player_command, evidence, minutes, players=2,
                 fault_injection=True, startup_timeout=90,
                 shutdown_timeout=10, trace_mb=1024,
                 render_profile=None, wall_seconds=None):
    """Self-launch N loopback benchmark players; the scenario owns the
    processes so the labeled fault-injection segment can kill and
    relaunch one, and so perf reports land under evidence at quit."""
    root = Path(evidence).absolute()
    root.mkdir(parents=True, exist_ok=False)
    report = {
        "status": "failed",
        "scope": "self-launched %d-instance visual-benchmark "
                 "qualification%s" % (
                     players,
                     " + process-kill fault injection"
                     if fault_injection else ""),
        "minutesRequested": minutes,
        "players": [], "checks": [], "shots": [],
        "buildManifest": {"endpoints": []},
        "phases": {}, "incidents": [], "faultInjection": [],
        "traces": {}, "summaries": {}, "perfReports": {},
        "streamHealth": {},
        "perf": {"floorSeconds": PERF_RACING_FLOOR_SECONDS,
                 "comboFloorSeconds": PERF_COMBO_FLOOR_SECONDS,
                 "perEndpoint": {}},
        "labels": {}, "failures": [],
        "notVerified": [
            "cross-device (physical LAN) reachability — loopback "
            "self-launch is same-machine desktop automation evidence",
            "physical keyboard/touch input and on-device UI parity",
            "ordinary-build performance — validation endpoints are "
            "compiled out; see the documented ordinary-build procedure",
        ],
    }
    roster = {}
    names = []
    samplers = []
    wall_seconds = wall_seconds or max(
        minutes * 60.0 * WALL_LIMIT_FACTOR, WALL_LIMIT_MIN_SECONDS)
    try:
        assert 2 <= players <= 4, "LAN capacity is 2-4 players"
        executable = Path(player_command[0]).resolve(strict=True)
        report["executable"] = {"path": str(executable),
                                "sha256": file_hash(executable)}
        names = ["host"] + ["client-%d" % i
                            for i in range(players - 1)]
        for name in names:
            profile_dir = root / (name + "-profiles")
            perf_dir = root / (name + "-perf")
            process, info, directory = launch_player(
                player_command, root, name, profile_dir, perf_dir,
                trace_mb, render_profile, startup_timeout)
            evidence_dir = root / (name + "-evidence")
            evidence_dir.mkdir()
            write_json(evidence_dir / "launch.json", {
                "role": name, "command": info["command"],
                "validationDirectory": str(directory),
                "profileDirectory": str(profile_dir),
                "perfReportDirectory": str(perf_dir),
                "pid": process.pid,
            })
            report["players"].append({
                "role": name, "pid": process.pid,
                "runId": info["runId"]})
            client = VBClient(info, evidence_dir)
            roster[name] = {
                "process": process, "info": info,
                "directory": directory, "evidence": evidence_dir,
                "profileDir": profile_dir, "perfDir": perf_dir,
                "client": client, "generation": 1, "alive": True,
            }
            report["buildManifest"]["endpoints"].append(
                endpoint_manifest(name, client, info))

        peers = [(name, roster[name]["client"]) for name in names]
        host = peers[0][1]

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
                        report["faultInjection"].append(
                            "%s gen-%d terminated by scenario"
                            % (name, entry["generation"]))
                        report["traces"][
                            "%s-gen%d" % (name, entry["generation"])] = {
                            "verified": False,
                            "faultCasualty": True,
                            "reason": "process terminated by fault "
                                      "injection (intentional)"}
                        return
                raise AssertionError("kill target not in the roster")

            def relaunch(client):
                for name in names:
                    entry = roster[name]
                    if entry["client"] is client:
                        generation = entry["generation"] + 1
                        run_name = "%s-relaunch%d" % (name, generation)
                        process, info, directory = launch_player(
                            player_command, root, run_name,
                            entry["profileDir"],
                            entry["perfDir"], trace_mb, render_profile,
                            startup_timeout)
                        evidence_dir = root / (run_name + "-evidence")
                        evidence_dir.mkdir()
                        write_json(evidence_dir / "launch.json", {
                            "role": name, "generation": generation,
                            "command": info["command"],
                            "validationDirectory": str(directory),
                            "pid": process.pid,
                        })
                        new_client = VBClient(info, evidence_dir)
                        entry.update(process=process, info=info,
                                     directory=directory,
                                     evidence=evidence_dir,
                                     client=new_client,
                                     generation=generation, alive=True)
                        report["players"].append({
                            "role": name, "generation": generation,
                            "pid": process.pid, "runId": info["runId"]})
                        # The new process runs the same build/content —
                        # it must agree with the recorded manifest.
                        new_manifest = endpoint_manifest(
                            name, new_client, info)
                        for field in ("buildVersion", "contentVersion",
                                      "stateSchemaVersion"):
                            existing = [
                                e[field] for e in
                                report["buildManifest"]["endpoints"]
                                if e["role"] == name]
                            assert all(
                                value == new_manifest[field]
                                for value in existing), \
                                "relaunch changed %s" % field
                        return new_client
                raise AssertionError("relaunch target not in the roster")

            fault = {"kill": kill, "relaunch": relaunch}

        build_labels(peers, report)
        # External process-memory sampling — measured outside the
        # player once per second for the whole run.
        for name in names:
            sampler = MemorySampler(
                root / (name + "-perf"),
                pid=roster[name]["process"].pid)
            sampler.start()
            samplers.append(sampler)

        run_benchmark(peers, report, minutes, wall_seconds,
                      join_address="127.0.0.1", allow_loopback=True,
                      fault=fault)

        live_peers = [(name, roster[name]["client"])
                      for name in names if roster[name]["alive"]]
        finish_and_verify(live_peers, report)
        for name, peer in live_peers:
            report["summaries"][name] = peer.perf_summary or {}
        # Graceful termination first — a hard kill skips
        # OnApplicationQuit and the quit-time perf report never writes.
        # WM_CLOSE (taskkill without /F) runs the quit path; a player
        # that ignores it falls back to terminate.
        for name in names:
            entry = roster[name]
            process = entry["process"]
            if process.poll() is not None:
                continue
            closed = False
            if sys.platform == "win32":
                completed = subprocess.run(
                    ["taskkill", "/pid", str(process.pid)],
                    capture_output=True)
                if completed.returncode == 0:
                    try:
                        process.wait(timeout=shutdown_timeout + 20)
                        closed = True
                    except subprocess.TimeoutExpired:
                        pass
            if not closed:
                process.terminate()
                try:
                    process.wait(timeout=shutdown_timeout + 20)
                except subprocess.TimeoutExpired:
                    process.kill()
        for name in names:
            entry = roster[name]
            if not entry["alive"]:
                continue
            collect_perf_reports(
                {name: entry["info"]["runId"]},
                report, entry["perfDir"])
        assemble_verdict(live_peers, report)
    except Exception as error:
        report["error"] = "%s: %s" % (type(error).__name__, error)
        report["traceback"] = traceback.format_exc()
    finally:
        _LAN_DRIVERS.clear()
        for sampler in samplers:
            try:
                sampler.stop()
            except Exception:
                pass
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
        for key in ("_joinAddress", "_allowLoopback"):
            report.pop(key, None)
        write_json(root / "result.json", report)
    return report


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument(
        "--connections", type=Path, nargs="+",
        metavar="CONNECTION_JSON",
        help="2-4 connection.json descriptors of independently launched "
             "players; the first is driven as the host. Android players "
             "use per-device ADB-forwarded loopback descriptors.")
    mode.add_argument(
        "--launch", type=Path, metavar="PLAYER_EXE",
        help="Dev convenience: self-launch N local benchmark players "
             "and own their processes (enables process-kill fault "
             "injection and quit-time perf-report collection).")
    parser.add_argument("--players", type=int, default=2,
                        help="--launch mode only: 2-4 local players")
    parser.add_argument("--no-fault-injection", action="store_true",
                        help="--launch mode only: skip the labeled "
                             "process-kill rejoin segment")
    parser.add_argument("--minutes", type=float, default=30.0,
                        help="active-racing minutes the performance "
                             "segment must accumulate per endpoint "
                             "(default 30; the qualification floor is a "
                             "fixed 30)")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="NEW directory; existing directories are "
                             "refused")
    parser.add_argument("--host-address", default=None,
                        help="--connections mode only: explicit host LAN "
                             "address when broadcast discovery cannot "
                             "reach it")
    parser.add_argument("--allow-loopback", action="store_true",
                        help="--connections mode only: permit the "
                             "127.0.0.1 direct-join fallback "
                             "(same-machine evidence, labeled)")
    parser.add_argument("--perf-report-dir", type=Path, default=None,
                        help="--connections mode only: directory where "
                             "the launched players write "
                             "perf-report-<runId>.json at quit")
    parser.add_argument("--render-profile", default=None,
                        choices=("pc", "tablet"),
                        help="--launch mode only: force a render profile "
                             "on every launched player")
    parser.add_argument("--trace-mb", type=int, default=1024,
                        help="--launch mode only: per-player trace "
                             "ceiling in MiB (default 1024)")
    parser.add_argument("--wall-limit", type=float, default=None,
                        help="performance-segment wall-clock cap in "
                             "seconds (default max(150%% of requested "
                             "racing time, 600))")
    parser.add_argument("--startup-timeout", type=float, default=90)
    parser.add_argument("--shutdown-timeout", type=float, default=10)
    return parser


def main():
    args = build_parser().parse_args()
    if not __debug__:
        raise RuntimeError(
            "Run without -O: scenario assertions must be enabled")
    if args.connections is not None:
        assert 2 <= len(args.connections) <= 4, \
            "--connections takes 2-4 connection descriptors"
        report = run_attached(
            args.connections, args.evidence, args.minutes,
            host_address=args.host_address,
            allow_loopback=args.allow_loopback,
            perf_report_dir=args.perf_report_dir,
            wall_seconds=args.wall_limit)
    else:
        report = run_scenario(
            [str(args.launch)], args.evidence, args.minutes,
            players=args.players,
            fault_injection=not args.no_fault_injection,
            startup_timeout=args.startup_timeout,
            shutdown_timeout=args.shutdown_timeout,
            trace_mb=args.trace_mb,
            render_profile=args.render_profile,
            wall_seconds=args.wall_limit)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
