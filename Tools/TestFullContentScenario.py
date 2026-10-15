"""Harness-integrity tests for FullContentScenario (M6 Task 9, slice A).

Two layers keep every dishonesty mutant named:

- ReconcileTests drive ``reconcile_coverage`` over synthetic per-event
  evidence records. The expected inventory is synthesized from the
  fixed production contract (3 worlds / 6 courses / 2 classes / 6
  types = 72 regular + 6 finales, 8 vehicles, 6 rivals) — never from
  the observed rows — so removing a world or event, duplicating a
  cell, substituting fixture records, invalidating the capture,
  misattributing AI actions, omitting an objective proof, or
  reporting only 77 completions each prevent a production pass.

- Fixture*Tests run the real attach-mode scenario against an in-process
  schema-7 fixture server (same protocol as the player: hello+token,
  sequenced snapshot/state/delta boundaries, capture-status.json).
  Every run passes --fixture semantics: evidence is fixture:true and a
  fixture can never reach a production "passed" verdict — only
  "partial". Stream-corruption mutants must turn the run "failed",
  never silent.
"""

import argparse
import copy
import importlib
import json
import os
import socket
import sys
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

# The shared driving sim the sibling harness uses: pure kinematics over
# the graybox LAN route — enough for the pursuit follower to finish a
# lap. Production per-course routes are slice-B scope.
from TestGameplayLoopScenario import (  # noqa: E402
    FixtureSim, fresh_profile)


def scenario():
    return importlib.import_module("FullContentScenario")


# ---------------------------------------------------------------------------
# Synthetic evidence helpers (the reconcile-level contract)
# ---------------------------------------------------------------------------

def expected_event_rows():
    inv = scenario().expected_inventory()
    return inv["regular"] + inv["finales"]


def complete_record(row, fixture=False, mask=0x7, victory=True):
    """One honest, complete evidence record for an expected row — the
    shape a real production run produces (result identity, receipt,
    human attribution, measured duration, per-bit objective proofs)."""
    record = scenario().new_event_record(row, fixture=fixture)
    record.update(
        status="passed", detail="finished under its own race identity",
        buildVersion="dev-1.0", contentVersion="full-content-1",
        contentIdentity="full-content-1|fixture",
        commandIds=["cmd-start-1", "cmd-continue-1"],
        raceId="race-" + row["eventId"],
        resultId="result-" + row["eventId"],
        completed=True,
        objectiveMaskAfter=mask if row["kind"] == "regular" else 0,
        victory=victory,
        attribution="human",
        durationTicks=4500, durationSeconds=150.0,
        captureValid=True, attempts=1,
        inputHoldCount=40)
    record["earnedBits"] = [
        bit for bit, flag in (("completion", 0x1),
                              ("introductory", 0x2),
                              ("mastery", 0x4))
        if row["kind"] == "regular" and mask & flag]
    record["rewards"] = {"coinsAwarded": 45,
                         "receiptResultId": record["resultId"],
                         "vehicleId": "", "liveryId": ""}
    if row["kind"] == "regular":
        record["objectiveProofs"] = {
            bit: {"resultId": record["resultId"]}
            for bit, flag in (("completion", 0x1),
                              ("introductory", 0x2),
                              ("mastery", 0x4))
            if mask & flag}
    return record


def complete_records(fixture=False, mask=0x7, victory=None):
    """Full 78-record evidence set. victory=None mixes in the plan's
    non-victory proof: the first six regular events of each world are
    recorded as non-victory runs that still earned their star bits
    (>= 6 extra non-victory stars per region)."""
    records = []
    per_world = {}
    for row in expected_event_rows():
        if victory is None:
            index = per_world.get(row["worldId"], 0)
            per_world[row["worldId"]] = index + 1
            won = not (row["kind"] == "regular" and index < 6)
        else:
            won = victory
        records.append(complete_record(row, fixture=fixture,
                                       mask=mask, victory=won))
    return records


def complete_world_rows():
    """The schema-7 progression.worlds surface after a full adventure."""
    inv = scenario().expected_inventory()
    rows = []
    for world in inv["worlds"]:
        rows.append({
            "worldId": world["worldId"], "nameEn": "w",
            "nameHe": "w", "selected": world["order"] == 2,
            "available": True, "lockedReason": "",
            "completedEvents": 24, "requiredEvents": 24,
            "stars": 72, "requiredStars": 30,
            "finalesOpen": True, "completedFinales": 2,
            "totalFinales": 2, "worldComplete": True,
            "rewardVehicleIds": world["rewardVehicleIds"]})
    return rows


def complete_reward_facts(records):
    """Reward-level facts the finished adventure must show: all eight
    vehicles owned and six DISTINCT rival victories (one per finale)."""
    inv = scenario().expected_inventory()
    victories = []
    for finale in inv["finales"]:
        victories.append({
            "rivalId": finale["rivalId"], "liveryId": finale["liveryId"],
            "resultId": "result-" + finale["eventId"],
            "rewardVehicleId": finale["rewardVehicleId"]})
    return {"unlockedVehicles": list(inv["vehicles"]),
            "rivalVictories": victories}


def reconcile(records, **kwargs):
    return scenario().reconcile_coverage(records, **kwargs)


# ---------------------------------------------------------------------------
# Unit-level contract + reconciliation tests
# ---------------------------------------------------------------------------

class ContractTests(unittest.TestCase):
    """The synthesized inventory must mirror FullContentValidation.cs
    exactly — a drift between the authoring gate and the coverage gate
    would let one pass while the other fails."""

    def test_expected_inventory_shape(self):
        inv = scenario().expected_inventory()
        self.assertEqual(len(inv["worlds"]), 3)
        self.assertEqual([w["worldId"] for w in inv["worlds"]],
                         ["sunny-island", "redrock-canyon",
                          "snowy-peaks"])
        self.assertEqual(len(inv["regular"]), 72)
        self.assertEqual(len(inv["finales"]), 6)
        self.assertEqual(len(inv["vehicles"]), 8)
        self.assertEqual(len(inv["rivals"]), 6)
        # Every class x course x type cell exactly once.
        cells = {(r["courseId"], r["classId"], r["type"])
                 for r in inv["regular"]}
        self.assertEqual(len(cells), 72)
        ids = [r["eventId"] for r in inv["regular"]]
        self.assertEqual(len(set(ids)), 72)

    def test_id_conventions(self):
        mod = scenario()
        self.assertEqual(mod.regular_event_id("harbor-hop", "car",
                                              "clean"),
                         "harbor-hop-car-clean")
        self.assertEqual(mod.runtime_type("clean"), "clean-driving")
        self.assertEqual(mod.runtime_type("circuit"), "circuit")
        self.assertEqual(mod.finale_id("sunny-island", "motorcycle"),
                         "sunny-island-motorcycle-finale")
        self.assertEqual(mod.rival_id("snowy-peaks", "car"),
                         "snowy-peaks-car-rival")
        self.assertEqual(mod.livery_id("snowy-peaks", "car"),
                         "livery-snowy-peaks-car-rival")
        self.assertEqual(mod.reward_vehicle_id(0, "car"), "car-02")
        self.assertEqual(mod.reward_vehicle_id(2, "motorcycle"),
                         "motorcycle-04")

    def test_event_record_carries_the_required_fields(self):
        row = scenario().expected_inventory()["regular"][0]
        record = scenario().new_event_record(row, fixture=False)
        for key in ("eventId", "worldId", "courseId", "classId", "type",
                    "runtimeType", "kind", "status", "detail",
                    "buildVersion", "contentVersion", "contentIdentity",
                    "commandIds", "raceId", "resultId", "completed",
                    "objectiveMaskBefore", "objectiveMaskAfter",
                    "earnedBits", "objectiveProofs", "victory",
                    "rewards", "durationTicks", "durationSeconds",
                    "captureValid", "attribution", "fixture",
                    "humanGaps", "attempts"):
            self.assertIn(key, record, key)
        self.assertEqual(record["status"], "notVerified")


class ReconcileTests(unittest.TestCase):
    """Every required mutant must prevent a production pass."""

    def test_complete_production_evidence_passes(self):
        records = complete_records()
        outcome = reconcile(
            records, fixture=False, capture_valid=True,
            world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "passed",
                         json.dumps(outcome["defects"], indent=2)
                         + json.dumps(outcome["gaps"], indent=2))
        self.assertFalse(outcome["defects"])
        self.assertFalse(outcome["gaps"])
        self.assertEqual(outcome["totals"]["completedEvents"], 78)
        failed = [c for c in outcome["checks"]
                  if c["status"] != "passed"]
        self.assertFalse(failed, json.dumps(failed, indent=2))

    def test_removed_world_never_passes(self):
        records = [r for r in complete_records()
                   if r["worldId"] != "redrock-canyon"]
        worlds = [w for w in complete_world_rows()
                  if w["worldId"] != "redrock-canyon"]
        outcome = reconcile(
            records, world_rows=worlds,
            reward_facts=complete_reward_facts(records))
        self.assertNotEqual(outcome["status"], "passed")
        # A missing worlds row is contradictory evidence — the schema-7
        # contract publishes EVERY authored world, locked or not.
        self.assertTrue(any("redrock-canyon" in d
                            for d in outcome["defects"]),
                        json.dumps(outcome, indent=2))
        self.assertEqual(outcome["status"], "failed")

    def test_removed_event_is_a_named_gap(self):
        records = [r for r in complete_records()
                   if r["eventId"] != "harbor-hop-car-boost"]
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertNotEqual(outcome["status"], "passed")
        self.assertTrue(any("harbor-hop-car-boost" in g
                            for g in outcome["gaps"]))
        self.assertEqual(outcome["status"], "partial")

    def test_duplicate_cell_is_a_defect(self):
        records = complete_records()
        records.append(copy.deepcopy(records[0]))
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")
        self.assertTrue(any("duplicate" in d.lower()
                            for d in outcome["defects"]))

    def test_unexpected_event_is_a_defect(self):
        records = complete_records()
        extra = complete_record(
            {"eventId": "harbor-hop-car-bonus", "worldId": "sunny-island",
             "courseId": "harbor-hop", "classId": "car",
             "type": "bonus", "runtimeType": "bonus", "kind": "regular"})
        records.append(extra)
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")
        self.assertTrue(any("unexpected" in d.lower()
                            or "outside" in d.lower()
                            for d in outcome["defects"]))

    def test_fixture_records_never_prove_production(self):
        # Substituted fixtures: identical evidence shape but flagged —
        # a production pass must be unreachable.
        records = complete_records(fixture=True)
        outcome = reconcile(
            records, fixture=True,
            world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "partial")
        self.assertTrue(any("fixture" in g.lower()
                            for g in outcome["gaps"]))

    def test_run_level_fixture_flag_overrides_unflagged_records(self):
        records = complete_records(fixture=False)
        outcome = reconcile(
            records, fixture=True,
            world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "partial")

    def test_dropped_stream_records_fail_the_capture(self):
        outcome = reconcile(
            complete_records(), capture_valid=False,
            world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(complete_records()))
        self.assertEqual(outcome["status"], "failed")
        self.assertTrue(any("capture" in d.lower()
                            for d in outcome["defects"]))

    def test_ai_attribution_is_a_defect(self):
        records = complete_records()
        records[0]["attribution"] = "ai"
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")
        self.assertTrue(any("attribution" in d.lower()
                            or "ai" in d.lower()
                            for d in outcome["defects"]))

    def test_omitted_objective_proof_is_a_defect(self):
        records = complete_records()
        del records[0]["objectiveProofs"]["mastery"]
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")
        self.assertTrue(any("proof" in d.lower()
                            for d in outcome["defects"]))

    def test_forged_proof_for_unearned_bit_is_a_defect(self):
        records = complete_records(mask=0x3)
        records[0]["objectiveProofs"]["mastery"] = {
            "resultId": "forged"}
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")

    def test_unproven_objectives_stay_partial(self):
        # Finishing alone never proves objectives: completion-bit-only
        # evidence is honest partial coverage, not a defect.
        records = complete_records(mask=0x1)
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "partial")
        self.assertTrue(any("objective" in g.lower()
                            for g in outcome["gaps"]))
        self.assertFalse(outcome["defects"])

    def test_77_completions_never_pass(self):
        records = complete_records()[:77]
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertNotEqual(outcome["status"], "passed")
        self.assertEqual(outcome["totals"]["completedEvents"], 77)

    def test_not_verified_rows_are_preserved_not_success(self):
        records = complete_records()
        records[0].update(status="notVerified",
                          detail="locked (eventLocked)",
                          completed=False)
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "partial")
        row = next(c for c in outcome["checks"]
                   if c["name"].endswith(records[0]["eventId"]))
        self.assertEqual(row["status"], "notVerified")

    def test_failed_record_is_a_defect(self):
        records = complete_records()
        records[0].update(status="failed", detail="race aborted")
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")

    def test_missing_reward_vehicle_is_a_gap(self):
        records = complete_records()
        rewards = complete_reward_facts(records)
        rewards["unlockedVehicles"] = rewards["unlockedVehicles"][:-1]
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=rewards)
        self.assertNotEqual(outcome["status"], "passed")
        self.assertTrue(any("motorcycle-04" in g
                            for g in outcome["gaps"]))

    def test_one_rival_reward_short_never_passes(self):
        records = complete_records()
        rewards = complete_reward_facts(records)
        rewards["rivalVictories"] = rewards["rivalVictories"][:-1]
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=rewards)
        self.assertNotEqual(outcome["status"], "passed")
        named = outcome["gaps"] + outcome["defects"]
        self.assertTrue(any("rival" in d.lower() for d in named))

    def test_shared_rival_proof_is_a_defect(self):
        # Six "separate" rival victories cannot share one result id.
        records = complete_records()
        rewards = complete_reward_facts(records)
        rewards["rivalVictories"][1]["resultId"] = \
            rewards["rivalVictories"][0]["resultId"]
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=rewards)
        self.assertEqual(outcome["status"], "failed")

    def test_missing_duration_is_a_defect(self):
        records = complete_records()
        records[0]["durationTicks"] = None
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertEqual(outcome["status"], "failed")

    def test_nonvictory_star_deficit_is_a_gap(self):
        # All-victory evidence earns zero non-victory extra stars per
        # region — the >=6-per-region goal stays honestly open.
        records = complete_records(victory=True)
        for record in records:
            record["earnedBits"] = []
        outcome = reconcile(
            records, world_rows=complete_world_rows(),
            reward_facts=complete_reward_facts(records))
        self.assertNotEqual(outcome["status"], "passed")
        self.assertTrue(any("non-victory" in g.lower()
                            or "nonvictory" in g.lower()
                            for g in outcome["gaps"]))


# ---------------------------------------------------------------------------
# Schema-7 fixture server (attach-mode end-to-end harness tests)
# ---------------------------------------------------------------------------

FC_WORLD_IDS = ["sunny-island", "redrock-canyon", "snowy-peaks"]
FC_COURSES = {"sunny-island": ["harbor-hop", "lighthouse-loop"],
              "redrock-canyon": ["canyon-dash", "mesa-skyway"],
              "snowy-peaks": ["snowpine-circuit", "aurora-pass"]}
FC_CLASSES = ["car", "motorcycle"]
FC_TYPES = ["circuit", "boost", "stunt", "time", "gadget", "clean"]


def fixture_event_ids(world_id):
    ids = []
    for course_id in FC_COURSES[world_id]:
        for class_id in FC_CLASSES:
            for suffix in FC_TYPES:
                ids.append("%s-%s-%s" % (course_id, class_id, suffix))
    return ids


def fc_state(open_worlds, unlocked, finales_open, mutant):
    """A schema-7 boundary state authoring the full 78-event inventory
    (minus/with whatever the mutant asks). Every world row exists —
    locked ones carry their reason — matching the stream contract."""
    state = {
        "stateSchemaVersion": 7,
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
            "contentIdentity": "full-content-1|fixture",
            "campaignWorldId": open_worlds[0] if open_worlds else "",
            "campaignCompletedEvents": 0, "campaignStars": 0,
            "campaignRequiredEvents": 24, "campaignRequiredStars": 24,
            "campaignFinalesOpen": False, "campaignWorldComplete": False,
            "campaignEvents": [],
            "worlds": [], "adventureComplete": False,
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
    worlds = [w for w in FC_WORLD_IDS if mutant != "drop-world"
              or w != "redrock-canyon"]
    for index, world_id in enumerate(worlds):
        available = world_id in open_worlds
        state["progression"]["worlds"].append({
            "worldId": world_id,
            "nameEn": "World " + world_id,
            "nameHe": "עולם", "selected": index == 0 and available,
            "available": available,
            "lockedReason": "" if available else "worldLocked",
            "completedEvents": 0, "requiredEvents": 24,
            "stars": 0, "requiredStars": 24,
            "finalesOpen": False, "completedFinales": 0,
            "totalFinales": 2, "worldComplete": False,
            "rewardVehicleIds": [
                "car-0%d" % (index + 2), "motorcycle-0%d" % (index + 2)]})
    return state


def fc_campaign_rows(world_id, unlocked, finales_open, mutant):
    """campaignEvents rows for one selected world."""
    rows = []
    for event_id in fixture_event_ids(world_id):
        if mutant == "drop-event" \
                and event_id == "%s-car-boost" % \
                FC_COURSES[world_id][0]:
            continue
        parts = event_id.split("-")
        course_id = "-".join(parts[:-2])
        class_id = "motorcycle" if "motorcycle" in parts else "car"
        suffix = parts[-1]
        runtime = "clean-driving" if suffix == "clean" else suffix
        rows.append({
            "eventId": event_id, "courseId": course_id,
            "classId": class_id, "eventType": runtime,
            "finale": False, "rivalId": "", "rewardVehicleId": "",
            "completed": False, "objectiveMask": 0, "stars": 0,
            "locked": event_id not in unlocked,
            "lockReason": "" if event_id in unlocked
            else "eventLocked"})
    for class_id in FC_CLASSES:
        finale_id = "%s-%s-finale" % (world_id, class_id)
        open_now = finale_id in finales_open
        rows.append({
            "eventId": finale_id, "courseId": FC_COURSES[world_id][0],
            "classId": class_id, "eventType": "finale",
            "finale": True, "rivalId": "%s-%s-rival" % (world_id, class_id),
            "rewardVehicleId": "%s-0%d" % (
                class_id, FC_WORLD_IDS.index(world_id) + 2),
            "completed": False, "objectiveMask": 0, "stars": 0,
            "locked": not open_now,
            "lockReason": "" if open_now else "eventLocked"})
    if mutant == "duplicate-cell":
        rows.append(dict(rows[0]))
    return rows


def fc_fixture_server(validation_dir, profile_dir, mutant=None,
                      open_worlds=("sunny-island",),
                      unlocked=frozenset(), finales_open=frozenset(),
                      victory_events=frozenset(), grant_mask=None,
                      mode_combos=frozenset(), mute_commands=False):
    """One schema-7 protocol endpoint inside this process.

    The fixture authors the real 72+6 inventory (mutants remove/dup
    rows); `unlocked`/`finales_open` gate starts exactly like the
    traversal rules; finishing a campaign event marks its row and pays
    a receipt — a finish the scenario did not drive is never emitted.
    """
    validation_dir = Path(validation_dir)
    validation_dir.mkdir(parents=True, exist_ok=True)
    profile_dir = Path(profile_dir)
    profile_dir.mkdir(parents=True, exist_ok=True)
    state = fc_state(open_worlds, unlocked, finales_open, mutant)
    sim = FixtureSim()
    run_id = uuid.uuid4().hex
    token = uuid.uuid4().hex
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    port = server.getsockname()[1]
    connection = {"protocolVersion": 1, "host": "127.0.0.1",
                  "port": port, "runId": run_id, "token": token,
                  "participantId": "local", "stateSchemaVersion": 7}
    sequence = [0]
    lock = threading.Lock()
    stream = (validation_dir / "state.ndjson").open(
        "w", encoding="utf-8")
    conn_sock = [None]
    done = [False]
    selected = [open_worlds[0] if open_worlds else ""]
    race_row = [None]   # the campaign row the live race binds — never
                        # part of the emitted state
    grant_mask = grant_mask or {}

    def world_row(world_id):
        return next((w for w in state["progression"]["worlds"]
                     if w["worldId"] == world_id), None)

    def event_row(event_id):
        for row in state["progression"]["campaignEvents"]:
            if row["eventId"] == event_id:
                return row
        return None

    def refresh_world_rows():
        progression = state["progression"]
        for world in progression["worlds"]:
            wid = world["worldId"]
            world_events = fixture_event_ids(wid)
            regular_done = 0
            stars = 0
            finales_done = 0
            profile = state.get("savedProfile") or {}
            for progress in profile.get("eventProgress") or []:
                if progress.get("eventId") in world_events:
                    regular_done += 1
                    stars += bin(progress.get("objectiveMask", 0)
                                 & 0x7).count("1")
                for class_id in FC_CLASSES:
                    if progress.get("eventId") == \
                            "%s-%s-finale" % (wid, class_id):
                        finales_done += 1
            world["completedEvents"] = regular_done
            world["stars"] = stars
            world["finalesOpen"] = regular_done >= 24 \
                and stars >= world["requiredStars"]
            world["completedFinales"] = finales_done
            world["worldComplete"] = finales_done >= 2
        progression["adventureComplete"] = all(
            w["worldComplete"] for w in progression["worlds"]) \
            and len(progression["worlds"]) == 3
        # Unlock the next world once its predecessor completes.
        for index, world_id in enumerate(FC_WORLD_IDS[:-1]):
            row = world_row(world_id)
            nxt = world_row(FC_WORLD_IDS[index + 1])
            if row and nxt and row["worldComplete"] and not nxt["available"]:
                nxt["available"] = True
                nxt["lockedReason"] = ""
        selected_world = world_row(selected[0])
        progression["campaignWorldId"] = selected[0]
        progression["campaignCompletedEvents"] = \
            selected_world["completedEvents"] if selected_world else 0
        progression["campaignStars"] = \
            selected_world["stars"] if selected_world else 0
        progression["campaignFinalesOpen"] = \
            selected_world["finalesOpen"] if selected_world else False
        progression["campaignWorldComplete"] = \
            selected_world["worldComplete"] if selected_world else False
        progression["campaignEvents"] = fc_campaign_rows(
            selected[0], unlocked, finales_open, mutant) \
            if selected_world else []
        # Durable per-event progress mirrors into the published rows.
        profile = state.get("savedProfile") or {}
        for row in progression["campaignEvents"]:
            progress = next(
                (p for p in profile.get("eventProgress") or []
                 if p.get("eventId") == row["eventId"]), None)
            if progress is not None:
                row["completed"] = progress.get("completed", False)
                row["objectiveMask"] = progress.get("objectiveMask", 0)
                row["stars"] = bin(row["objectiveMask"] & 0x7).count("1")

    def write_status(valid, complete, error=""):
        (validation_dir / "capture-status.json").write_text(json.dumps({
            "captureValid": valid, "captureComplete": complete,
            "captureError": error}), encoding="utf-8")

    def emit(kind, **payload):
        sequence[0] += 1
        record = {"type": kind, "protocolVersion": 1, "runId": run_id,
                  "deviceId": "fixture-device", "participantId": "local",
                  "buildVersion": "fixture-1.0",
                  "contentVersion": "full-content-1",
                  "sequence": sequence[0],
                  "monotonicMs": sequence[0] * 4,
                  "tick": state["tick"], **payload}
        line = json.dumps(record) + "\n"
        if mutant != "drop-records" or sequence[0] % 11 != 0:
            stream.write(line)
            stream.flush()
        if conn_sock[0] is not None:
            try:
                conn_sock[0].sendall(line.encode("utf-8"))
            except OSError:
                pass

    def boundary(command_id=None):
        payload = {"stateSchemaVersion": 7,
                   "state": json.loads(json.dumps(state))}
        if command_id is not None:
            payload["commandId"] = command_id
        emit("state", **payload)

    def result(command_id, action, status="applied", code=""):
        emit("result", commandId=command_id, status=status, code=code,
             action=action, duplicate=False,
             result={"status": status})

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

    def make_result(race_id, mode, row, victory):
        event_id = row["eventId"]
        position = 1 if victory else 2
        # The human seat finishes P1 only on a genuine victory row;
        # otherwise an AI seat takes the win (positions stay unique).
        racers = []
        for i in range(4):
            own = (i == 0) if victory else (i == 1)
            if victory:
                position = i + 1
            else:
                position = 1 if i == 0 else i + 1
            racers.append({
                "position": position,
                "slot": i,
                "participantId": "local" if own else "ai-%d" % i,
                "vehicleId": "car-01",
                "attribution": (
                    "ai" if mutant == "misattribute-ai" else "human")
                if own else "ai",
                "outcome": "finished",
                "finishTicks": 4500 + i * 120,
                "lapsCompleted": 1, "coinsAwarded": 45,
                "difficultyId": "easy",
                "metrics": {"position": position,
                            "boostActivations": 0,
                            "cleanLandings": 0, "recoveries": 0,
                            "gadgetUses": 0, "distinctGadgetTypes": 0,
                            "humanDistinctShortcuts": 0,
                            "longestCleanStreakTicks": 0}})
        return {"formatVersion": 1,
                "resultId": "result-" + uuid.uuid4().hex[:10],
                "sessionId": race_id, "raceId": race_id,
                "eventId": event_id,
                "contentVersion": "full-content-1",
                "mode": mode,
                "rulesFingerprint": "fixture-rules-01",
                "courseId": row.get("courseId", ""),
                "classId": row.get("classId", ""),
                "finalized": True, "racers": racers,
                "deliveries": []}

    def apply_reward(result_obj, row):
        receipt = {
            "resultId": result_obj["resultId"],
            "participantId": "local",
            "awardedCoins": 45,
            "fingerprint": "fp-" + uuid.uuid4().hex[:8],
            "rulesFingerprint": "fixture-rules-01",
            "mode": result_obj["mode"],
            "completionCoins": 45, "placementCoins": 0,
            "challengeCoins": 0, "bountyCoins": 0, "rivalCoins": 0,
            "completedBountyIds": [],
            "recordImproved": False, "recordBestTicks": 0,
            "updatedVehicleCount": 0,
            "updatedTimeTrialRecordCount": 0}
        profile = state["savedProfile"]
        profile["appliedResults"].append(receipt)
        profile["coins"] += 45
        profile["localRaceOrdinal"] += 1
        if result_obj["mode"] == "campaign":
            earned = grant_mask.get(result_obj["eventId"], 0x1)
            progress = next(
                (p for p in profile["eventProgress"]
                 if p.get("eventId") == result_obj["eventId"]), None)
            if progress is None:
                progress = {"eventId": result_obj["eventId"],
                            "completed": False, "objectiveMask": 0}
                profile["eventProgress"].append(progress)
            progress["completed"] = True
            progress["objectiveMask"] |= earned
            victory = row and row["finale"] \
                and result_obj["eventId"] in victory_events
            if victory:
                livery = "livery-" + row["rivalId"]
                if livery not in profile["ownedCosmetics"]:
                    profile["ownedCosmetics"].append(
                        {"cosmeticId": livery, "slot": "livery"})
                reward = row.get("rewardVehicleId") or ""
                if reward and reward not in \
                        profile["unlockedVehicles"]:
                    profile["unlockedVehicles"].append(reward)
        if result_obj["mode"] == "timeTrial":
            profile["timeTrialRecords"].append({
                "courseId": result_obj["courseId"],
                "classId": result_obj["classId"],
                "bestTicks": 4500,
                "bestResultId": result_obj["resultId"]})
        profile["pendingRaceContexts"] = [
            c for c in profile.get("pendingRaceContexts") or []
            if c.get("raceId") != result_obj["raceId"]]
        try:
            save_profile()
            state["persistenceStatus"] = "saved"
            return True
        except OSError:
            state["persistenceStatus"] = "saveFailed"
            profile["appliedResults"] = [
                r for r in profile["appliedResults"]
                if r["resultId"] != receipt["resultId"]]
            profile["coins"] -= 45
            state["progression"]["rewardUnresolved"] = True
            state["progression"]["rewardUnresolvedRaceId"] = \
                result_obj["raceId"]
            return False

    def start_race(mode, row, vehicle_id="car-01", difficulty="easy"):
        sim.race_counter += 1
        race_id = "fixture-race-%d" % sim.race_counter
        session = sim.session(race_id)
        session.update(eventId=row["eventId"], vehicleId=vehicle_id)
        racers = [session]
        roster = [{"participantId": "local", "slot": 0,
                   "vehicleId": vehicle_id, "attribution": "human",
                   "session": session, "finished": False,
                   "dnf": False}]
        start_x, start_z = 0.0, -30.0
        for index in range(1, 4):
            ai = sim.session(race_id)
            ai["motor"].update(x=start_x, z=start_z - index * 2.0)
            roster.append({"participantId": "ai-%d" % index,
                           "slot": index, "vehicleId": vehicle_id,
                           "attribution": "ai", "session": ai,
                           "finished": False, "dnf": False})
        race = {"raceId": race_id, "eventId": row["eventId"],
                "classId": row.get("classId") or "car",
                "courseId": row.get("courseId") or "",
                "phase": "countdown", "countdown": 3,
                "racers": roster, "result": None,
                "laps": 1, "gadgets": {"pickups": [], "patches": []},
                "events": []}
        tuning = "normalized" if mode == "timeTrial" else "purchased"
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
        race_row[0] = row
        return race_id

    def finish_race():
        solo = state["soloRace"]
        if solo is None or solo.get("phase") == "finished":
            return
        row = race_row[0] or {"eventId": "", "finale": False}
        victory = row["eventId"] in victory_events
        result_obj = make_result(solo["raceId"], solo["mode"], row,
                                 victory)
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
        apply_reward(result_obj, row)
        refresh_world_rows()

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
        solo["events"] = solo["events"][-64:]
        state["drivingEvents"] = state["drivingEvents"][-64:]
        if session.get("lap", 0) >= 1 \
                or session["simulationTick"] > 4000:
            finish_race()

    def handle_command(frame):
        action = frame["action"]
        params = frame.get("parameters") or {}
        cid = frame["commandId"]
        profile = state.get("savedProfile")
        if action in ("profile.create", "profile.select"):
            index = int(params["slot"])
            if action == "profile.create":
                profile_new = fresh_profile(index,
                                            params.get("language", "en"))
                state["slots"][index].update(status="Loaded",
                                             profile=profile_new)
                (profile_dir / ("profile-%d.json" % index)).write_text(
                    json.dumps(profile_new), encoding="utf-8")
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
            refresh_world_rows()
        elif action == "input.set":
            sim.held = {
                "steering": float(params.get("steering", "0")),
                "drift": params.get("drift") == "true",
                "boost": params.get("boost") == "true",
                "gadget": params.get("gadget") == "true",
                "recover": params.get("recover") == "true"}
            sim.hold_ticks = int(params.get("durationTicks", "10"))
        elif action == "campaign.open":
            if not state["progression"]["available"]:
                result(cid, action, "rejected", "contentUnavailable")
                return
            if state["activeSlot"] < 0:
                result(cid, action, "rejected",
                       "requiresSelectedProfile")
                return
            state["screen"] = "campaign"
        elif action == "campaign.selectWorld":
            world_id = params.get("worldId", "")
            world = world_row(world_id)
            if world is None:
                result(cid, action, "rejected", "unknownWorld")
                return
            if not world["available"]:
                result(cid, action, "rejected", "worldLocked")
                return
            selected[0] = world_id
            for w in state["progression"]["worlds"]:
                w["selected"] = w["worldId"] == world_id
            refresh_world_rows()
        elif action == "campaign.start":
            if state["progression"]["rewardUnresolved"]:
                result(cid, action, "rejected", "rewardUnresolved")
                return
            if profile is None:
                result(cid, action, "rejected",
                       "requiresSelectedProfile")
                return
            if state.get("soloRace") is not None:
                result(cid, action, "rejected", "raceActive")
                return
            event_id = params.get("eventId", "")
            world_id = next(
                (w for w in FC_WORLD_IDS
                 if event_id in fixture_event_ids(w)
                 or event_id.startswith(w + "-")), None)
            world = world_row(world_id) if world_id else None
            if world is None:
                result(cid, action, "rejected", "unknownEvent")
                return
            if not world["available"]:
                result(cid, action, "rejected", "worldLocked")
                return
            candidate = None
            for row in fc_campaign_rows(world_id, unlocked,
                                        finales_open, mutant):
                if row["eventId"] == event_id:
                    candidate = row
                    break
            if candidate is None or candidate["locked"]:
                result(cid, action, "rejected", "eventLocked")
                return
            vehicle = params.get("vehicleId", "car-01")
            if vehicle not in (profile.get("unlockedVehicles") or []):
                result(cid, action, "rejected", "vehicleLocked")
                return
            start_race("campaign", candidate, vehicle_id=vehicle,
                       difficulty=params.get("difficulty") or "easy")
            profile.setdefault("pendingRaceContexts", []).append({
                "raceId": state["soloRace"]["raceId"], "mode": "campaign",
                "fingerprint": "ctx-" + uuid.uuid4().hex[:8]})
            try:
                save_profile()
            except OSError:
                result(cid, action, "rejected", "saveFailed")
                return
        elif action in ("quickRace.start", "timeTrial.start"):
            if profile is None:
                result(cid, action, "rejected",
                       "requiresSelectedProfile")
                return
            combo = (params.get("courseId", ""),
                     params.get("classId", ""))
            if combo not in mode_combos:
                result(cid, action, "rejected", "unknownEvent")
                return
            row = {"eventId": "%s-%s-circuit" % combo,
                   "courseId": combo[0], "classId": combo[1],
                   "finale": False}
            start_race("quickRace" if action == "quickRace.start"
                       else "timeTrial", row)
        elif action == "quickRace.open":
            state["screen"] = "quickRace"
        elif action == "timeTrial.open":
            state["screen"] = "timeTrial"
        elif action in ("results.continue", "results.retrySave",
                        "race.exit"):
            if action == "results.retrySave":
                if not state["progression"]["rewardUnresolved"]:
                    result(cid, action, "rejected", "noPendingResult")
                    return
                state["progression"]["rewardUnresolved"] = False
                state["progression"]["rewardUnresolvedRaceId"] = ""
            else:
                state["soloRace"] = None
                race_row[0] = None
                state["screen"] = "campaign"
        elif action == "results.retry":
            solo = state.get("soloRace")
            if solo is None or solo.get("result") is None:
                result(cid, action, "rejected", "unavailable")
                return
            start_race(solo["mode"], race_row[0]
                       or {"eventId": "", "finale": False})
        elif action in ("garage.open", "bounties.open",
                        "input.clear", "settings.save",
                        "pause.toggle", "ui.navigate"):
            if action == "ui.navigate":
                state["screen"] = params.get("screen", state["screen"])
            elif action == "pause.toggle":
                state["paused"] = not state["paused"]
        else:
            result(cid, action, "rejected", "unknownAction")
            return
        boundary(command_id=cid)
        result(cid, action)

    def sim_loop():
        while not done[0]:
            with lock:
                state["tick"] += 1
                tick_solo()
                delay = 0.01 if state.get("soloRace") else 0.1
                if not done[0]:
                    boundary()
            time.sleep(delay)

    refresh_world_rows()
    write_status(True, False)
    emit("snapshot", stateSchemaVersion=7,
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
                 "stateSchemaVersion": 7}) + "\n").encode())
            with lock:
                emit("snapshot", stateSchemaVersion=7,
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
                        done[0] = True
                elif kind == "command":
                    if mute_commands:
                        continue
                    with lock:
                        handle_command(frame)
        finally:
            reader.close()

    threading.Thread(target=serve, daemon=True).start()

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


def os_replace(src, dst):
    import os
    os.replace(src, dst)


def fake_player_main(argv):  # pragma: no cover - manual smoke path
    parser = argparse.ArgumentParser()
    parser.add_argument("--fake-player", action="store_true")
    parser.add_argument("--validation-dir", required=True)
    parser.add_argument("--validation-profile-dir", required=True)
    parser.add_argument("-logFile", default=None)
    args = parser.parse_args(argv)
    fc_fixture_server(args.validation_dir, args.validation_profile_dir)
    while True:
        time.sleep(1)


# ---------------------------------------------------------------------------
# End-to-end attach-mode tests against the schema-7 fixture
# ---------------------------------------------------------------------------

class FixtureHarness:
    def __init__(self, test):
        self.test = test
        self.temp = tempfile.TemporaryDirectory()
        test.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.stops = []
        test.addCleanup(self._stop_all)

    def _stop_all(self):
        for stop in self.stops:
            stop()

    def run(self, name, scope, **kwargs):
        run_dir = self.root / (name + "-run")
        profile_dir = self.root / (name + "-profiles")
        conn_path, stop = fc_fixture_server(run_dir, profile_dir,
                                            **kwargs)
        self.stops.append(stop)
        deadline = time.time() + 10
        while not conn_path.exists() and time.time() < deadline:
            time.sleep(0.05)
        assert conn_path.exists(), "fixture wrote no connection.json"
        evidence = self.root / (name + "-evidence")
        report = scenario().run_attached(
            [conn_path], evidence, scope, fixture=True)
        return report, evidence


class FixtureCatalogTests(unittest.TestCase):
    """Catalog scope: inventory reconciliation against the published
    worlds/campaignEvents surfaces — no driving required."""

    def setUp(self):
        self.fx = FixtureHarness(self)

    def test_full_inventory_fixture_is_partial_never_passed(self):
        report, evidence = self.fx.run(
            "catalog", "catalog",
            open_worlds=tuple(FC_WORLD_IDS),
            unlocked=set(e for w in FC_WORLD_IDS
                         for e in fixture_event_ids(w)),
            finales_open=set(
                "%s-%s-finale" % (w, c)
                for w in FC_WORLD_IDS for c in FC_CLASSES))
        self.assertTrue(report["fixture"])
        # Every cell is observable but nothing was driven — honest
        # partial coverage, never a production pass.
        self.assertIn(report["status"], ("partial", "failed"),
                      report.get("error", ""))
        self.assertNotEqual(report["status"], "passed")
        names = {row["name"] for row in report["checks"]}
        self.assertIn("coverage.cell.harbor-hop-car-circuit", names)
        self.assertIn("coverage.cell.aurora-pass-motorcycle-clean",
                      names)
        coverage = json.loads(
            (evidence / "coverage.json").read_text(encoding="utf-8"))
        self.assertEqual(coverage["totals"]["expectedRegular"], 72)
        result = json.loads(
            (evidence / "result.json").read_text(encoding="utf-8"))
        self.assertTrue(result["fixture"])

    def test_dropped_world_fails_catalog(self):
        report, _ = self.fx.run(
            "drop-world", "catalog", mutant="drop-world",
            open_worlds=tuple(FC_WORLD_IDS))
        self.assertEqual(report["status"], "failed",
                         report.get("error", "")
                         + json.dumps(report["checks"]))
        self.assertTrue(any("redrock-canyon" in row.get("detail", "")
                            or "redrock-canyon" in row["name"]
                            for row in report["checks"]))

    def test_dropped_event_row_fails_catalog(self):
        report, _ = self.fx.run(
            "drop-event", "catalog", mutant="drop-event",
            open_worlds=("sunny-island",))
        self.assertNotEqual(report["status"], "passed")
        self.assertTrue(any("harbor-hop-car-boost"
                            in row.get("detail", "") + row["name"]
                            for row in report["checks"]),
                        json.dumps(report["checks"], indent=2))

    def test_duplicate_cell_fails_catalog(self):
        report, _ = self.fx.run(
            "dup", "catalog", mutant="duplicate-cell",
            open_worlds=("sunny-island",))
        self.assertEqual(report["status"], "failed",
                         report.get("error", "")
                         + json.dumps(report["checks"]))

    def test_locked_world_is_honestly_unverified(self):
        report, _ = self.fx.run(
            "locked", "catalog", open_worlds=("sunny-island",),
            unlocked=set(fixture_event_ids("sunny-island")))
        self.assertIn(report["status"], ("partial", "failed"))
        self.assertNotEqual(report["status"], "passed")
        locked_rows = [row for row in report["checks"]
                       if row["status"] == "notVerified"
                       and "locked" in row.get("detail", "").lower()]
        self.assertTrue(locked_rows)


class FixtureCampaignTests(unittest.TestCase):
    """Campaign scope drives unlocked events end-to-end on the fixture;
    every artifact stays fixture:true."""

    def setUp(self):
        self.fx = FixtureHarness(self)

    def test_fixture_campaign_partial_with_named_gaps(self):
        unlocked = {"harbor-hop-car-circuit",
                    "harbor-hop-car-boost"}
        report, evidence = self.fx.run(
            "campaign", "campaign",
            unlocked=unlocked,
            grant_mask={"harbor-hop-car-circuit": 0x7})
        self.assertTrue(report["fixture"])
        self.assertIn(report["status"], ("partial", "failed"),
                      report.get("error", "")
                      + json.dumps(report["checks"]))
        self.assertNotEqual(report["status"], "passed")
        events = {e["eventId"]: e for e in (
            json.loads(line) for line in
            (evidence / "events.ndjson").read_text(encoding="utf-8")
            .splitlines() if line.strip())}
        driven = events["harbor-hop-car-circuit"]
        self.assertEqual(driven["status"], "passed")
        self.assertTrue(driven["fixture"])
        self.assertEqual(driven["worldId"], "sunny-island")
        self.assertEqual(driven["courseId"], "harbor-hop")
        self.assertEqual(driven["classId"], "car")
        self.assertEqual(driven["type"], "circuit")
        self.assertTrue(driven["resultId"])
        self.assertTrue(driven["captureValid"])
        # Locked cells stayed honest — never silently "driven".
        self.assertEqual(events["snowy-peaks-car-finale"]["status"],
                         "notVerified")

    def test_dropped_stream_records_fail(self):
        report, _ = self.fx.run(
            "droprec", "campaign", mutant="drop-records",
            unlocked={"harbor-hop-car-circuit"})
        self.assertEqual(report["status"], "failed")
        self.assertTrue(report.get("error"),
                        json.dumps(report, indent=2))

    def test_misattributed_human_row_fails(self):
        report, _ = self.fx.run(
            "aiattr", "campaign", mutant="misattribute-ai",
            unlocked={"harbor-hop-car-circuit"})
        self.assertEqual(report["status"], "failed")

    def test_timeout_fails(self):
        report, _ = self.fx.run(
            "timeout", "campaign", mute_commands=True)
        self.assertEqual(report["status"], "failed")
        self.assertTrue(report.get("error"))


def main():
    if "--fake-player" in sys.argv:
        fake_player_main([a for a in sys.argv[1:]
                          if a != "--fake-player"])
        return
    unittest.main()


if __name__ == "__main__":
    main()
