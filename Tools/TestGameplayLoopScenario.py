"""Harness-integrity tests for GameplayLoopScenario.

A --fake-player subprocess speaks the real validation protocol (hello +
token, sequenced snapshot/state/delta boundaries, capture-status.json,
result frames) and simulates enough of the schema-6 loop — tutorial
lessons, a one-lap campaign race on the authored graybox route, reward
receipts, purchases, the unresolved-save gate — for the scenario to
drive its whole smoke chain through --connections. Every run passes
--fixture: the evidence is fixture evidence, labeled fixture:true, and
never counts as real-player qualification.

Each corruption mutant must make the scenario fail with its NAMED
reason — a harness that waves a corrupted trace through is worse than
no harness. Mutants cover the required matrix: missing delta, silent
observer gap, wrong attribution, double award, stale result context,
fake normalized tuning, failed-save shown as persisted, missing process
generation, and timeout.
"""

import argparse
import importlib
import json
import math
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from NetworkScenario import LAN_ROUTE, LAN_GATES, LAN_GATE_DIRS  # noqa: E402


LESSONS = ["steering", "brake", "drift", "boost", "jump",
           "recovery", "gadget"]
COURSE_ID = "harbor-hop"
CLASS_ID = "car"
EVENT_ID = "fixture-event-01"
AWARD_COINS = 45
PURCHASE_PRICE = 60
LAP_TICK_LIMIT = 4000
START_POSE = (0.0, -30.0, math.pi / 2.0)   # x, z, heading — east straight


def fresh_profile(slot, language="en"):
    return {
        "schemaVersion": 3, "id": "fixture-profile-" + uuid.uuid4().hex[:8],
        "slot": slot, "avatar": "comet", "colorIndex": slot,
        "language": language, "camera": "chase",
        "reducedCameraShake": False,
        "musicVolume": 1.0, "effectsVolume": 1.0, "voiceVolume": 1.0,
        "coins": 0, "unlockedVehicles": ["car-01", "motorcycle-01"],
        "appliedResults": [], "purchaseReceipts": [],
        "vehicleUpgrades": [], "eventProgress": [], "worldProgress": [],
        "finaleProgress": [], "ownedCosmetics": [],
        "equippedCosmetics": [], "activeBounties": [],
        "bountyRotationCursor": 0, "timeTrialRecords": [],
        "pendingRaceContexts": [], "localRaceOrdinal": 0,
    }


def base_state():
    return {
        "stateSchemaVersion": 6,
        "screen": "profiles", "language": "en", "activeSlot": -1,
        "paused": False, "tick": 0,
        "input": {"steering": 0.0, "drift": False, "boost": False,
                  "gadget": False, "recover": False,
                  "changeCamera": False, "pause": False},
        "slots": [{"slot": i, "status": "Missing", "profile": None}
                  for i in range(4)],
        "savedProfile": None, "draftProfile": None,
        "pendingSettings": False,
        "captureValid": True, "captureError": "",
        "featureCoverage": {},
        "driving": None, "drivingEvents": [],
        "soloRace": None,
        "progression": {
            "available": True,
            "contentIdentity": "fixture-catalog-1|graybox",
            "campaignWorldId": "world-1",
            "campaignCompletedEvents": 0, "campaignStars": 0,
            "campaignRequiredEvents": 1, "campaignRequiredStars": 0,
            "campaignFinalesOpen": False, "campaignWorldComplete": False,
            "campaignEvents": [
                {"eventId": EVENT_ID, "courseId": COURSE_ID,
                 "classId": CLASS_ID, "eventType": "race",
                 "finale": False, "rivalId": "",
                 "rewardVehicleId": "", "completed": False,
                 "objectiveMask": 0, "stars": 0, "locked": False,
                 "lockReason": ""},
                {"eventId": "fixture-finale-01", "courseId": COURSE_ID,
                 "classId": CLASS_ID, "eventType": "finale",
                 "finale": True, "rivalId": "",
                 "rewardVehicleId": "", "completed": False,
                 "objectiveMask": 0, "stars": 0, "locked": True,
                 "lockReason": "stars"},
            ],
            "bounties": [],
            "rewardUnresolved": False,
            "rewardUnresolvedRaceId": "",
        },
        "network": {
            "role": "none", "phase": "idle",
            "localParticipantId": "", "currentRaceId": "",
            "descriptor": None, "participants": [], "paused": False,
            "pauseRequests": [], "lastError": "",
            "pendingOperations": [], "discovered": [], "results": [],
        },
        "networkRace": None, "prediction": None,
        "remotePresentation": None, "networkLink": None,
        "presentation": {
            "renderProfileId": "none", "renderingAvailable": False,
            "hebrewFontAvailable": True, "fontWarning": "",
            "textDirection": "ltr", "racers": [], "performance": None,
        },
        "persistenceStatus": "saved",
        "tutorial": None,
    }


class FixtureSim:
    """One tick of honest-enough fixture physics: steering turns the
    heading, speed builds, the pose advances along heading — enough for
    the pursuit followers to converge on the authored route. Everything
    the scenario checks is produced by the same fields a real player
    would publish; shortcuts the fixture cannot take (gate crossings,
    lesson events, receipts) are simulated, never reported without the
    scenario's command having caused them."""

    def __init__(self):
        self.held = {"steering": 0.0, "drift": False, "boost": False,
                     "gadget": False, "recover": False}
        self.hold_ticks = 0
        self.prev_arc = None
        self.prev_along = {}
        self.recovering_ticks = 0
        self.lesson_ticks = 0
        self.race_counter = 0

    @staticmethod
    def motor_for(x=START_POSE[0], z=START_POSE[1],
                  heading=START_POSE[2]):
        return {"x": x, "z": z, "heading": heading, "speed": 0.0,
                "tick": 0, "grounded": True, "recovering": False,
                "boostCharge": 1.0, "boostActive": False,
                "airborne": False}

    def session(self, race_id):
        return {"raceId": race_id, "eventId": EVENT_ID,
                "vehicleId": "car-01", "phase": "running",
                "simulationTick": 0, "nextCheckpoint": 0, "lap": 0,
                "finished": False, "dnf": False,
                "checkpoints": [{"index": i, "crossed": False}
                                for i in range(len(LAN_GATES))],
                "motor": self.motor_for()}

    @staticmethod
    def _project(x, z):
        best = None
        arc = 0.0
        for index in range(len(LAN_ROUTE)):
            ax, az = LAN_ROUTE[index - 1]
            bx, bz = LAN_ROUTE[index]
            ex, ez = bx - ax, bz - az
            seg2 = ex * ex + ez * ez
            t = 0.0 if seg2 == 0 else max(0.0, min(1.0,
                ((x - ax) * ex + (z - az) * ez) / seg2))
            px, pz = ax + ex * t, az + ez * t
            d2 = (px - x) ** 2 + (pz - z) ** 2
            if best is None or d2 < best[0]:
                best = (d2, arc + t * math.sqrt(seg2))
            arc += math.sqrt(seg2)
        return best[1]

    NEUTRAL = {"steering": 0.0, "drift": False, "boost": False,
               "gadget": False, "recover": False}

    def step(self, driving, held=None):
        """Advance one simulated tick; returns emitted event dicts.

        `held` None means the HUMAN seat — it consumes the shared held
        sample and its lease; explicit samples drive AI seats on rails
        without eating the human's holds."""
        events = []
        motor = driving["motor"]
        motor["tick"] += 1
        driving["simulationTick"] = driving.get("simulationTick", 0) + 1
        if self.recovering_ticks > 0:
            self.recovering_ticks -= 1
            if self.recovering_ticks == 0:
                motor["recovering"] = False
                # The respawn lands ON the route at the current arc.
                arc = self._project(motor["x"], motor["z"])
                px, pz = self._point_along(arc)
                motor.update(x=px, z=pz, speed=0.0, heading=0.0)
        if held is None:
            if self.hold_ticks > 0:
                self.hold_ticks -= 1
                held = dict(self.held)
            else:
                held = dict(self.NEUTRAL)
        if held.get("recover"):
            motor["recovering"] = True
            self.recovering_ticks = 5
            events.append(self._event(driving, "race.recovered"))
        if held.get("gadget"):
            events.append(self._event(driving, "gadget.used"))
        motor["heading"] += float(held.get("steering") or 0.0) * 0.06
        motor["heading"] = (motor["heading"] + math.pi) % (2 * math.pi) \
            - math.pi
        motor["speed"] = min(14.0, motor["speed"] + 0.25)
        step = 1.3
        motor["x"] += math.sin(motor["heading"]) * step
        motor["z"] += math.cos(motor["heading"]) * step
        # Landmark ramp zone: any honest pass over the crest emits
        # airborne + landed, the same currency the jump lesson spends.
        if motor["x"] < -50 and 20 < motor["z"] < 42:
            events.append(self._event(driving, "motor.becameAirborne"))
            events.append(self._event(driving, "motor.landed"))
        self._gate_progress(driving)
        return events

    def _point_along(self, arclen):
        total = sum(math.hypot(
            LAN_ROUTE[i][0] - LAN_ROUTE[i - 1][0],
            LAN_ROUTE[i][1] - LAN_ROUTE[i - 1][1])
            for i in range(len(LAN_ROUTE)))
        arclen %= total
        arc = 0.0
        for index in range(len(LAN_ROUTE)):
            ax, az = LAN_ROUTE[index - 1]
            bx, bz = LAN_ROUTE[index]
            seg = math.hypot(bx - ax, bz - az)
            if arclen <= arc + seg and seg > 0:
                t = (arclen - arc) / seg
                return ax + (bx - ax) * t, az + (bz - az) * t
            arc += seg
        return LAN_ROUTE[-1]

    def _gate_progress(self, driving):
        gate = driving.get("nextCheckpoint", 0)
        if not isinstance(gate, int) or gate >= len(LAN_GATES):
            return
        gx, gz = LAN_GATES[gate]
        dx, dz = LAN_GATE_DIRS[gate]
        motor = driving["motor"]
        along = (motor["x"] - gx) * dx + (motor["z"] - gz) * dz
        key = id(driving)
        prev = self.prev_along.get(key, {})
        if gate in prev and prev[gate] <= 0.5 < along \
                and abs((motor["x"] - gx) * (-dz)
                        + (motor["z"] - gz) * dx) < 20:
            driving["checkpoints"][gate]["crossed"] = True
            driving["nextCheckpoint"] = (gate + 1) % len(LAN_GATES)
            if driving["nextCheckpoint"] == 0:
                driving["lap"] = driving.get("lap", 0) + 1
        prev[gate] = along
        self.prev_along[key] = prev

    _event_counter = [0]

    def _event(self, driving, kind):
        FixtureSim._event_counter[0] += 1
        return {"id": "ev-%d" % FixtureSim._event_counter[0],
                "tick": driving["simulationTick"], "kind": kind,
                "commandId": "", "detail": "",
                "participantId": "local", "attribution": "human"}


def fixture_server(validation_dir, profile_dir, mutant=None,
                   mute_commands=False):
    """Serve one validation connection inside this process (tests keep
    the fixture in-thread: --fake-player wraps it for subprocess use).
    Returns (connection.json path, stop callback)."""
    validation_dir = Path(validation_dir)
    validation_dir.mkdir(parents=True, exist_ok=True)
    profile_dir = Path(profile_dir)
    profile_dir.mkdir(parents=True, exist_ok=True)
    state = base_state()
    sim = FixtureSim()
    run_id = uuid.uuid4().hex
    token = uuid.uuid4().hex
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    port = server.getsockname()[1]
    connection = {"protocolVersion": 1, "host": "127.0.0.1",
                  "port": port, "runId": run_id, "token": token,
                  "participantId": "local", "stateSchemaVersion": 6}
    sequence = [0]
    lock = threading.Lock()
    stream = (validation_dir / "state.ndjson").open(
        "w", encoding="utf-8")
    conn_sock = [None]
    done = [False]

    def write_status(valid, complete, error=""):
        (validation_dir / "capture-status.json").write_text(json.dumps({
            "captureValid": valid, "captureComplete": complete,
            "captureError": error}), encoding="utf-8")

    def emit(kind, **payload):
        sequence[0] += 1
        record = {"type": kind, "protocolVersion": 1, "runId": run_id,
                  "deviceId": "fixture-device", "participantId": "local",
                  "buildVersion": "fixture-1.0",
                  "contentVersion": "fixture-catalog-1",
                  "sequence": sequence[0],
                  "monotonicMs": sequence[0] * 4,
                  "tick": state["tick"], **payload}
        line = json.dumps(record) + "\n"
        stream.write(line)
        stream.flush()
        if conn_sock[0] is not None:
            try:
                conn_sock[0].sendall(line.encode("utf-8"))
            except OSError:
                pass

    def boundary(command_id=None):
        payload = {"stateSchemaVersion": 6,
                   "state": json.loads(json.dumps(state))}
        if command_id is not None:
            payload["commandId"] = command_id
        emit("state", **payload)

    def result(command_id, action, status="applied", code=""):
        emit("result", commandId=command_id, status=status, code=code,
             action=action, duplicate=False,
             result={"status": status})

    # -- profile persistence: the write-deny mutant only works if the
    #    reward commit performs a REAL filesystem write.
    def save_profile():
        slot = state["activeSlot"]
        path = profile_dir / ("profile-%d.json" % slot)
        tmp = profile_dir / ("profile-%d.json.tmp" % slot)
        tmp.write_text(json.dumps(state["savedProfile"]),
                       encoding="utf-8")
        os.replace(str(tmp), str(path))

    def load_slots():
        for index in range(4):
            path = profile_dir / ("profile-%d.json" % index)
            if path.exists():
                profile = json.loads(path.read_text(encoding="utf-8"))
                state["slots"][index] = {
                    "slot": index, "status": "Loaded",
                    "profile": profile}

    def make_result(race_id, mode, participant="local"):
        racers = [{"position": i + 1, "slot": i,
                   "participantId": participant if i == 0
                   else "ai-%d" % i,
                   "vehicleId": "car-01",
                   "attribution": ("ai" if mutant == "wrong-attribution"
                                   else "human") if i == 0 else "ai",
                   "outcome": "finished",
                   "finishTicks": 4500 + i * 120,
                   "lapsCompleted": 1, "coinsAwarded": AWARD_COINS,
                   "difficultyId": "easy"}
                  for i in range(4)]
        return {"formatVersion": 1,
                "resultId": "result-" + uuid.uuid4().hex[:10],
                "sessionId": race_id, "raceId": race_id,
                "eventId": EVENT_ID,
                "contentVersion": "fixture-catalog-1",
                "mode": mode,
                "rulesFingerprint": "fixture-rules-01",
                "courseId": COURSE_ID, "classId": CLASS_ID,
                "finalized": True, "racers": racers,
                "deliveries": []}

    def apply_reward(result_obj):
        """The atomic reward seam: receipt + wallet + progress + the
        profile file write — one commit, honestly failable."""
        receipt_id = result_obj["resultId"]
        if mutant == "stale-context":
            receipt_id = "stale-" + result_obj["resultId"]
        receipt = {
            "resultId": receipt_id,
            "participantId": result_obj["racers"][0]["participantId"],
            "awardedCoins": AWARD_COINS,
            "fingerprint": "fp-" + uuid.uuid4().hex[:8],
            "rulesFingerprint": "fixture-rules-01",
            "mode": result_obj["mode"],
            "completionCoins": AWARD_COINS, "placementCoins": 0,
            "challengeCoins": 0, "bountyCoins": 0, "rivalCoins": 0,
            "completedBountyIds": [],
            "recordImproved": False, "recordBestTicks": 0,
            "updatedVehicleCount": 0, "updatedTimeTrialRecordCount": 0,
        }
        profile = state["savedProfile"]
        profile["appliedResults"].append(receipt)
        if mutant == "double-award":
            profile["appliedResults"].append(dict(receipt))
        profile["coins"] += AWARD_COINS
        profile["localRaceOrdinal"] += 1
        if result_obj["mode"] == "campaign":
            row = next((r for r in
                        state["progression"]["campaignEvents"]
                        if r["eventId"] == EVENT_ID), None)
            if row is not None:
                row["completed"] = True
                row["stars"] = max(row["stars"], 1)
                row["objectiveMask"] |= 1
            state["progression"]["campaignCompletedEvents"] += 1
            state["progression"]["campaignStars"] = sum(
                r["stars"] for r in
                state["progression"]["campaignEvents"])
        if result_obj["mode"] == "timeTrial":
            profile["timeTrialRecords"].append({
                "courseId": COURSE_ID, "classId": CLASS_ID,
                "bestTicks": 4500, "bestResultId":
                result_obj["resultId"]})
        profile["pendingRaceContexts"] = [
            c for c in profile.get("pendingRaceContexts") or []
            if c.get("raceId") != result_obj["raceId"]]
        try:
            save_profile()
            state["persistenceStatus"] = "saved"
            if mutant == "failed-save-as-persisted":
                # THE MUTANT: gate latched while the receipt is already
                # durable — a failed save "shown as persisted"; both
                # cannot honestly be true at once.
                state["progression"]["rewardUnresolved"] = True
                state["progression"]["rewardUnresolvedRaceId"] = \
                    result_obj["raceId"]
                return False
            return True
        except OSError:
            state["persistenceStatus"] = "saveFailed"
            # Honest fixture: a failed commit rolls the award back out
            # of memory AND latches the unresolved gate.
            profile["appliedResults"] = [
                r for r in profile["appliedResults"]
                if r["resultId"] != receipt["resultId"]]
            profile["coins"] -= AWARD_COINS
            state["progression"]["rewardUnresolved"] = True
            state["progression"]["rewardUnresolvedRaceId"] = \
                result_obj["raceId"]
            return False

    def start_race(mode, event_id="", course_id="", class_id="",
                   vehicle_id="car-01", difficulty="easy"):
        sim.race_counter += 1
        race_id = "fixture-race-%d" % sim.race_counter
        session = sim.session(race_id)
        session.update(eventId=event_id or EVENT_ID,
                       vehicleId=vehicle_id)
        racers = [session]
        roster = [{"participantId": "local", "slot": 0,
                   "vehicleId": vehicle_id, "attribution": "human",
                   "session": session, "finished": False,
                   "dnf": False}]
        for index in range(1, 4):
            ai = sim.session(race_id)
            ai["motor"].update(x=START_POSE[0],
                               z=START_POSE[1] - index * 2.0)
            roster.append({"participantId": "ai-%d" % index,
                           "slot": index, "vehicleId": vehicle_id,
                           "attribution": "ai", "session": ai,
                           "finished": False, "dnf": False})
        race = {"raceId": race_id, "eventId": event_id or EVENT_ID,
                "classId": class_id or CLASS_ID,
                "courseId": course_id or COURSE_ID,
                "phase": "countdown", "countdown": 3,
                "racers": roster, "result": None,
                "laps": 1, "gadgets": {"pickups": [], "patches": []},
                "events": []}
        tuning = "normalized" if (mode == "timeTrial"
                                  or mutant == "fake-normalized") \
            else "purchased"
        state["soloRace"] = {
            "mode": mode, "sessionId": race_id, "raceId": race_id,
            "participantId": "local",
            "phase": "countdown", "paused": False,
            "difficultyId": difficulty, "tuningSource": tuning,
            "race": race if mode in ("campaign", "quickRace") else None,
            "trial": session if mode in ("timeTrial", "practice")
            else None,
            "result": None, "metrics": [], "events": [],
            "abortReason": ""}
        state["screen"] = "driving"
        return race_id

    def finish_race():
        solo = state["soloRace"]
        if solo is None or solo.get("phase") == "finished":
            return
        result_obj = make_result(solo["raceId"], solo["mode"])
        solo["result"] = result_obj
        solo["phase"] = "finished"
        if solo.get("race") is not None:
            solo["race"]["phase"] = "finished"
            solo["race"]["result"] = result_obj
            for racer in solo["race"]["racers"]:
                racer["finished"] = True
                racer["session"]["finished"] = True
        if solo.get("trial") is not None:
            solo["trial"]["finished"] = True
        apply_reward(result_obj)

    def tick_solo():
        solo = state["soloRace"]
        if solo is None:
            return
        session = solo.get("trial")
        if solo.get("race") is not None:
            own = next((r for r in solo["race"]["racers"]
                        if r["participantId"] == "local"), None)
            session = own["session"] if own else None
            for racer in solo["race"]["racers"]:
                if racer["participantId"] != "local":
                    for event in sim.step(racer["session"],
                                          held=sim.NEUTRAL):
                        solo["events"].append(event)
        if session is None or session.get("finished"):
            return
        if solo["phase"] == "countdown":
            if session["simulationTick"] > 10:
                solo["phase"] = "running"
                if solo.get("race") is not None:
                    solo["race"]["phase"] = "running"
            session["simulationTick"] += 1
            session["motor"]["tick"] += 1
            return
        for event in sim.step(session):
            solo["events"].append(event)
            state["drivingEvents"].append(event)
        # The real stream's event windows are bounded — so is the
        # fixture's, or a long run would masquerade an unbounded slice.
        solo["events"] = solo["events"][-64:]
        state["drivingEvents"] = state["drivingEvents"][-64:]
        if session.get("lap", 0) >= 1 \
                or session["simulationTick"] > LAP_TICK_LIMIT:
            finish_race()

    def tick_tutorial():
        driving = state["driving"]
        if driving is None:
            return
        tutorial = driving["tutorial"]
        for event in sim.step(driving):
            state["drivingEvents"].append(event)
            if event["kind"] == "gadget.used":
                sim.lesson_ticks = 99
            if event["kind"] in ("motor.landed", "race.recovered"):
                sim.lesson_ticks += 2
        state["drivingEvents"] = state["drivingEvents"][-64:]
        if tutorial["phase"] == "lessons":
            sim.lesson_ticks += 1
            if sim.lesson_ticks >= 3:
                sim.lesson_ticks = 0
                index = LESSONS.index(tutorial["lessonId"]) + 1
                if index >= len(LESSONS):
                    tutorial["phase"] = "trial"
                    driving["raceId"] = "tutorial-trial"
                    driving["lap"] = 0
                    driving["nextCheckpoint"] = 0
                else:
                    tutorial["lessonId"] = LESSONS[index]
        elif tutorial["phase"] == "trial":
            if driving.get("lap", 0) >= 1 \
                    or driving["simulationTick"] > LAP_TICK_LIMIT:
                driving["finished"] = True
                tutorial["phase"] = "graduated"

    def handle_command(frame):
        action = frame["action"]
        params = frame.get("parameters") or {}
        cid = frame["commandId"]
        profile = state.get("savedProfile")
        applied_actions = {
            "campaign.open": "campaign", "quickRace.open": "quickRace",
            "timeTrial.open": "timeTrial", "garage.open": "garage",
            "bounties.open": "bounties", "input.clear": None,
            "settings.save": None,
        }
        if action in ("profile.create", "profile.select"):
            index = int(params["slot"])
            if action == "profile.create":
                profile_new = fresh_profile(index,
                                            params.get("language", "en"))
                state["slots"][index].update(status="Loaded",
                                             profile=profile_new)
                save_into = profile_dir / ("profile-%d.json" % index)
                save_into.write_text(json.dumps(profile_new),
                                     encoding="utf-8")
            row = state["slots"][index]
            if row["status"] != "Loaded":
                result(cid, action, "rejected", "unavailable")
                return
            state.update(activeSlot=index,
                         savedProfile=dict(row["profile"]),
                         draftProfile=dict(row["profile"]),
                         screen="settings",
                         language=row["profile"]["language"],
                         pendingSettings=False)
        elif action == "settings.update":
            state["pendingSettings"] = True
            state["language"] = params.get("language", "en")
        elif action == "tutorial.start":
            driving = sim.session("tutorial-free")
            driving["tutorial"] = {"phase": "lessons",
                                   "lessonId": "steering"}
            driving.update(eventId="tutorial-01", vehicleId="car-01")
            state["driving"] = driving
            state["screen"] = "driving"
        elif action == "tutorial.exit":
            state["driving"] = None
            state["screen"] = "settings"
        elif action == "input.set":
            sim.held = {
                "steering": float(params.get("steering", "0")),
                "drift": params.get("drift") == "true",
                "boost": params.get("boost") == "true",
                "gadget": params.get("gadget") == "true",
                "recover": params.get("recover") == "true"}
            sim.hold_ticks = int(params.get("durationTicks", "10"))
        elif action in ("campaign.start", "quickRace.start",
                        "timeTrial.start", "practice.start",
                        "results.retry"):
            if state["progression"]["rewardUnresolved"]:
                result(cid, action, "rejected", "rewardUnresolved")
                return
            if profile is None:
                result(cid, action, "rejected",
                       "requiresSelectedProfile")
                return
            if action == "results.retry":
                solo = state.get("soloRace")
                if solo is None or solo.get("result") is None:
                    result(cid, action, "rejected", "unavailable")
                    return
                mode = solo["mode"]
                stored = {"mode": mode,
                          "eventId": EVENT_ID,
                          "courseId": COURSE_ID,
                          "classId": CLASS_ID}
            else:
                mode = {"campaign.start": "campaign",
                        "quickRace.start": "quickRace",
                        "timeTrial.start": "timeTrial",
                        "practice.start": "practice"}[action]
                stored = params
            race_id = start_race(
                mode, event_id=stored.get("eventId", EVENT_ID),
                course_id=stored.get("courseId", COURSE_ID),
                class_id=stored.get("classId", CLASS_ID),
                vehicle_id=stored.get("vehicleId", "car-01"),
                difficulty=stored.get("difficulty") or "easy")
            if profile is not None:
                profile.setdefault("pendingRaceContexts", []).append({
                    "raceId": race_id, "mode": mode,
                    "fingerprint": "ctx-" + uuid.uuid4().hex[:8]})
                try:
                    save_profile()
                except OSError:
                    result(cid, action, "rejected", "saveFailed")
                    return
        elif action in ("results.continue", "results.retrySave",
                        "race.exit", "tutorial.replay"):
            if action == "results.retrySave":
                if not state["progression"]["rewardUnresolved"]:
                    result(cid, action, "rejected", "noPendingResult")
                    return
                solo = state["soloRace"] or {}
                result_obj = solo.get("result")
                if result_obj is None:
                    result(cid, action, "rejected", "noPendingResult")
                    return
                # The retained pair replays identically — the second
                # write must succeed now that the deny lifted.
                state["progression"]["rewardUnresolved"] = False
                state["progression"]["rewardUnresolvedRaceId"] = ""
                profile = state["savedProfile"]
                if not any(r["resultId"] == result_obj["resultId"]
                           for r in profile["appliedResults"]):
                    profile["appliedResults"].append({
                        "resultId": result_obj["resultId"],
                        "participantId": "local",
                        "awardedCoins": AWARD_COINS,
                        "fingerprint": "fp-retry",
                        "rulesFingerprint": "fixture-rules-01",
                        "mode": result_obj["mode"],
                        "completionCoins": AWARD_COINS,
                        "placementCoins": 0, "challengeCoins": 0,
                        "bountyCoins": 0, "rivalCoins": 0,
                        "completedBountyIds": [],
                        "recordImproved": False, "recordBestTicks": 0,
                        "updatedVehicleCount": 0,
                        "updatedTimeTrialRecordCount": 0})
                    profile["coins"] += AWARD_COINS
                save_profile()
            elif action in ("results.continue", "race.exit"):
                state["soloRace"] = None
                state["driving"] = None
                state["screen"] = "campaign" \
                    if action == "results.continue" else "settings"
            elif action == "tutorial.replay":
                if not state.get("driving"):
                    result(cid, action, "rejected", "unavailable")
                    return
        elif action == "upgrade.purchase":
            if state["progression"]["rewardUnresolved"]:
                result(cid, action, "rejected", "rewardUnresolved")
                return
            if profile is None:
                result(cid, action, "rejected",
                       "requiresSelectedProfile")
                return
            if profile["coins"] < PURCHASE_PRICE:
                result(cid, action, "rejected", "insufficientCoins")
                return
            profile["coins"] -= PURCHASE_PRICE
            profile["purchaseReceipts"].append({
                "purchaseId": params["purchaseId"],
                "vehicleId": params["vehicleId"],
                "category": params["category"],
                "level": int(params["expectedLevel"]) + 1,
                "pricePaid": PURCHASE_PRICE})
            upgrade = next(
                (u for u in profile["vehicleUpgrades"]
                 if u.get("vehicleId") == params["vehicleId"]), None)
            if upgrade is None:
                upgrade = {"vehicleId": params["vehicleId"],
                           "acceleration": 0, "handling": 0,
                           "boost": 0}
                profile["vehicleUpgrades"].append(upgrade)
            upgrade[params["category"]] = \
                int(params["expectedLevel"]) + 1
            try:
                save_profile()
            except OSError:
                result(cid, action, "rejected", "saveFailed")
                return
        elif action in applied_actions or action == "ui.navigate":
            if action in applied_actions and applied_actions[action]:
                state["screen"] = applied_actions[action]
            elif action == "ui.navigate":
                state["screen"] = params.get("screen", state["screen"])
        elif action == "pause.toggle":
            state["paused"] = not state["paused"]
        else:
            result(cid, action, "rejected", "unknownAction")
            return
        if mutant == "observer-gap" and cid \
                and sequence[0] % 7 == 0:
            sequence[0] += 2   # silent gap in the emitted stream
        if mutant == "missing-delta":
            emit("delta", base=sequence[0] + 50, changed={},
                 removed=[], commandId=cid,
                 stateSchemaVersion=6)
        boundary(command_id=cid)
        result(cid, action)

    def sim_loop():
        while not done[0]:
            with lock:
                state["tick"] += 1
                tick_tutorial()
                tick_solo()
                delay = 0.01 if (state.get("driving")
                                 or state.get("soloRace")) else 0.1
                if not done[0]:
                    boundary()
            time.sleep(delay)

    write_status(True, False)
    emit("snapshot", stateSchemaVersion=6,
         state=json.loads(json.dumps(state)))
    load_slots()
    (validation_dir / "connection.json").write_text(
        json.dumps(connection), encoding="utf-8")
    threading.Thread(target=sim_loop, daemon=True).start()

    def serve():
        sock, _ = server.accept()
        conn_sock[0] = sock
        sock.settimeout(30)
        reader = sock.makefile("rb")
        try:
            hello = json.loads(reader.readline())
            if hello.get("token") != token:
                return
            sock.sendall((json.dumps(
                {"type": "hello", "protocolVersion": 1, "runId": run_id,
                 "stateSchemaVersion": 6}) + "\n").encode())
            with lock:
                emit("snapshot", stateSchemaVersion=6,
                     state=json.loads(json.dumps(state)))
            while True:
                try:
                    raw = reader.readline()
                except (OSError, ValueError):
                    return
                if not raw:
                    return
                frame = json.loads(raw)
                kind = frame["type"]
                if kind == "health":
                    with lock:
                        emit("health", captureValid=True,
                             captureError="", pendingCommands=0,
                             outgoingRecords=sequence[0],
                             controllerConnected=True)
                elif kind == "finish":
                    write_status(True, True)
                    with lock:
                        emit("finished", captureValid=True,
                             captureError="")
                        # The stream's last record is the terminal one:
                        # nothing may be emitted after it.
                        done[0] = True
                elif kind == "command":
                    if mute_commands:
                        continue   # the timeout mutant
                    with lock:
                        handle_command(frame)
        finally:
            reader.close()

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()

    def stop():
        done[0] = True
        try:
            server.close()
        except OSError:
            pass
        if conn_sock[0] is not None:
            try:
                conn_sock[0].close()
            except OSError:
                pass
        stream.close()

    return validation_dir / "connection.json", stop


def fake_player_main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--fake-player", action="store_true")
    parser.add_argument("--validation-dir", required=True)
    parser.add_argument("--validation-profile-dir", required=True)
    parser.add_argument("-logFile", default=None)
    parser.add_argument("--mutant", default=None)
    parser.add_argument("--mute-commands", action="store_true")
    args = parser.parse_args(argv)
    conn_path, _stop = fixture_server(
        args.validation_dir, args.validation_profile_dir,
        mutant=args.mutant, mute_commands=args.mute_commands)
    # The launched process must stay alive until the scenario kills it.
    while True:
        time.sleep(1)


# ---------------------------------------------------------------------------

def scenario():
    return importlib.import_module("GameplayLoopScenario")


class VerifierUnitTests(unittest.TestCase):
    """The named mutants, at the level the shared invariants own them."""

    def _state(self, **overrides):
        state = base_state()
        state.update(overrides)
        return state

    def test_clean_solo_state_passes(self):
        scenario().validate_loop_state(self._state())

    def test_fake_normalized_tuning_is_named(self):
        state = self._state(soloRace={
            "mode": "campaign", "raceId": "r1", "phase": "running",
            "participantId": "local", "tuningSource": "normalized",
            "race": None, "trial": None, "result": None})
        with self.assertRaises(AssertionError) as caught:
            scenario().validate_loop_state(state)
        self.assertIn("normalized", str(caught.exception))

    def test_wrong_attribution_is_named(self):
        race = {"racers": [
            {"participantId": "local", "attribution": "ai",
             "session": {}},
            {"participantId": "ai-1", "attribution": "ai",
             "session": {}}]}
        state = self._state(soloRace={
            "mode": "campaign", "raceId": "r1", "phase": "running",
            "participantId": "local", "tuningSource": "purchased",
            "race": race, "trial": None, "result": None})
        with self.assertRaises(AssertionError) as caught:
            scenario().validate_loop_state(state)
        self.assertIn("human", str(caught.exception))

    def test_result_attribution_disagreeing_with_roster_fails(self):
        race = {"racers": [
            {"participantId": "local", "attribution": "human",
             "session": {}}]}
        result = {"resultId": "res-1", "finalized": True, "racers": [
            {"participantId": "local", "attribution": "ai"}]}
        state = self._state(soloRace={
            "mode": "campaign", "raceId": "r1", "phase": "finished",
            "participantId": "local", "tuningSource": "purchased",
            "race": race, "trial": None, "result": result})
        with self.assertRaises(AssertionError) as caught:
            scenario().validate_loop_state(state)
        self.assertIn("attribution", str(caught.exception))

    def test_failed_save_shown_as_persisted_is_named(self):
        profile = fresh_profile(0)
        profile["appliedResults"].append(
            {"resultId": "res-9", "awardedCoins": 45,
             "fingerprint": "fp"})
        progression = base_state()["progression"]
        progression["rewardUnresolved"] = True
        progression["rewardUnresolvedRaceId"] = "race-9"
        state = self._state(
            savedProfile=profile,
            progression=progression,
            soloRace={"mode": "campaign", "raceId": "race-9",
                      "phase": "finished", "participantId": "local",
                      "tuningSource": "purchased", "race": None,
                      "trial": None,
                      "result": {"resultId": "res-9",
                                 "finalized": True, "racers": []}})
        with self.assertRaises(AssertionError) as caught:
            scenario().validate_loop_state(state)
        self.assertIn("persisted", str(caught.exception))

    def test_reward_gate_without_identity_fails(self):
        progression = base_state()["progression"]
        progression["rewardUnresolved"] = True
        progression["rewardUnresolvedRaceId"] = ""
        with self.assertRaises(AssertionError):
            scenario().validate_loop_state(
                self._state(progression=progression))

    def test_double_award_is_named(self):
        scenario_module = scenario()
        before = fresh_profile(0)
        after = fresh_profile(0)
        after["coins"] = 90
        receipt = {"resultId": "res-1", "awardedCoins": 45,
                   "fingerprint": "fp"}
        after["appliedResults"] = [dict(receipt), dict(receipt)]
        with self.assertRaises(AssertionError) as caught:
            scenario_module.expect_new_receipt(
                before, after,
                {"resultId": "res-1", "raceId": "r1"}, "test")
        self.assertIn("one new receipt", str(caught.exception))

    def test_stale_result_context_is_named(self):
        scenario_module = scenario()
        before = fresh_profile(0)
        after = fresh_profile(0)
        after["coins"] = 45
        after["appliedResults"] = [
            {"resultId": "res-OLD", "awardedCoins": 45,
             "fingerprint": "fp"}]
        with self.assertRaises(AssertionError) as caught:
            scenario_module.expect_new_receipt(
                before, after,
                {"resultId": "res-NEW", "raceId": "r1"}, "test")
        self.assertIn("stale", str(caught.exception))

    def test_wallet_delta_disagreeing_with_receipt_fails(self):
        scenario_module = scenario()
        before = fresh_profile(0)
        after = fresh_profile(0)
        after["coins"] = 200
        after["appliedResults"] = [
            {"resultId": "res-1", "awardedCoins": 45,
             "fingerprint": "fp"}]
        with self.assertRaises(AssertionError) as caught:
            scenario_module.expect_new_receipt(
                before, after,
                {"resultId": "res-1", "raceId": "r1"}, "test")
        self.assertIn("wallet delta", str(caught.exception))

    def test_missing_process_generation_is_named(self):
        scenario_module = scenario()
        with self.assertRaises(AssertionError) as caught:
            scenario_module.verify_run_generations([], "runs")
        self.assertIn("process generations", str(caught.exception))
        with self.assertRaises(AssertionError) as caught:
            scenario_module.verify_run_generations(
                [{"role": "solo", "runId": "r1"}], "runs")
        self.assertIn("process identity", str(caught.exception))
        with self.assertRaises(AssertionError) as caught:
            scenario_module.verify_run_generations(
                [{"role": "a", "pid": 1, "runId": "r"},
                 {"role": "b", "pid": 1, "runId": "s"}], "runs")
        self.assertIn("pid", str(caught.exception))
        scenario_module.verify_run_generations(
            [{"role": "a", "pid": 1, "runId": "r1",
              "startStamp": "t1"},
             {"role": "b", "pid": 2, "runId": "r2",
              "startStamp": "t2"}], "runs")


class FixtureSmokeTests(unittest.TestCase):
    """End-to-end runs of the attach-mode smoke scope against the
    fixture player — every artifact labeled fixture:true."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.stops = []

    def tearDown(self):
        for stop in self.stops:
            stop()

    def run_fixture(self, name, mutant=None, mute_commands=False,
                    profiles=None):
        run_dir = self.root / (name + "-run")
        profile_dir = profiles or (self.root / (name + "-profiles"))
        conn_path, stop = fixture_server(
            run_dir, profile_dir, mutant=mutant,
            mute_commands=mute_commands)
        self.stops.append(stop)
        deadline = time.time() + 10
        while not conn_path.exists() and time.time() < deadline:
            time.sleep(0.05)
        assert conn_path.exists(), "fixture wrote no connection.json"
        evidence = self.root / (name + "-evidence")
        report = scenario().run_attached(
            [conn_path], evidence, "smoke", fixture=True)
        return report, evidence

    def test_clean_fixture_smoke_completes(self):
        report, evidence = self.run_fixture("clean")
        self.assertTrue(report["fixture"])
        self.assertEqual(report["status"], "partial",
                         json.dumps(report.get("checks"), indent=2)
                         + "\n" + report.get("error", ""))
        self.assertFalse([r for r in report["checks"]
                          if r["status"] == "failed"])
        # Attach mode honestly cannot restart a process it does not own
        # — the restart rows must be the only notVerified rows.
        unverified = {r["name"] for r in report["checks"]
                      if r["status"] == "notVerified"}
        self.assertEqual(unverified,
                         {"results.failedSave", "restart.progress",
                          "restart.generations"}, unverified)
        result = json.loads(
            (evidence / "result.json").read_text(encoding="utf-8"))
        self.assertTrue(result["fixture"])
        attach = json.loads(
            (evidence / "solo-evidence" / "attach.json")
            .read_text(encoding="utf-8"))
        self.assertTrue(attach["fixture"])

    def test_missing_delta_fails(self):
        report, _ = self.run_fixture("delta", mutant="missing-delta")
        self.assertEqual(report["status"], "failed")
        self.assertIn("delta", report.get("error", "").lower()
                      + json.dumps(report["checks"]).lower())

    def test_observer_gap_fails(self):
        report, _ = self.run_fixture("gap", mutant="observer-gap")
        self.assertEqual(report["status"], "failed")
        self.assertTrue(report.get("error"), report)

    def test_wrong_attribution_fails(self):
        report, _ = self.run_fixture(
            "attrib", mutant="wrong-attribution")
        self.assertEqual(report["status"], "failed")

    def test_double_award_fails(self):
        report, _ = self.run_fixture("double", mutant="double-award")
        self.assertEqual(report["status"], "failed")

    def test_stale_context_fails(self):
        report, _ = self.run_fixture("stale", mutant="stale-context")
        self.assertEqual(report["status"], "failed")

    def test_fake_normalized_fails(self):
        report, _ = self.run_fixture(
            "normalized", mutant="fake-normalized")
        self.assertEqual(report["status"], "failed")

    def test_failed_save_as_persisted_fails(self):
        report, _ = self.run_fixture(
            "incoherent", mutant="failed-save-as-persisted")
        self.assertEqual(report["status"], "failed",
                         json.dumps(report.get("checks"), indent=2))
        evidence_text = report.get("error", "") \
            + json.dumps(report["checks"])
        self.assertIn("gate", evidence_text)

    def test_timeout_fails(self):
        report, _ = self.run_fixture("timeout", mute_commands=True)
        self.assertEqual(report["status"], "failed")
        self.assertTrue(report.get("error"), report)


def main():
    if "--fake-player" in sys.argv:
        fake_player_main([a for a in sys.argv[1:]
                          if a != "--fake-player"])
        return
    unittest.main()


if __name__ == "__main__":
    main()
