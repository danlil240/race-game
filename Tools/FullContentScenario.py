"""External proof of the complete three-world adventure (M6 Task 9).

Usage (development — the scenario owns the processes):

    FullContentScenario.py --launch <player.exe> --evidence <new-dir>
        --scope catalog|campaign|modes|lan [--players 2-4] [--fixture]

Usage (qualification — attach to independently launched players):

    FullContentScenario.py --connections <conn.json> [<conn.json> ...]
        --evidence <new-dir> --scope <scope>
        [--host-address <lan-ip>] [--fixture]

Scopes:
    catalog   Inventory reconciliation only — no driving. Walks the
              schema-7 progression.worlds rows and selects each
              available world through the real campaign.selectWorld
              command to read its published campaignEvents; every
              expected world/course/class/type cell is compared
              against the fixed production contract.
    campaign  Fresh synthetic profile -> every authored campaign event
              in order (regulars then class finales per world), each
              driven through the real command surface and recorded as
              one per-event evidence record (identities, command/trace
              references, completion + objective-mask facts, reward
              receipt, duration, capture validity, human-only gaps).
    modes     Quick Race and Time Trial over every authored
              course x class combination (6 x 2 = 12 each); time
              trials must seat the normalized starter, write a durable
              record and mint zero campaign credit.
    lan       The shared M5 LAN workload (GameplayLoopScenario
              .run_lan_scope): normalization, shared pause, gadget
              authority, disconnect/AI/rejoin, duplicate/retained
              results, failed-save retry, permanent departure and host
              loss.

Coverage reconciliation: the EXPECTED inventory is synthesized from
the fixed production contract — the same constants
FullContentValidation.cs enforces on the authored manifest (worlds
sunny-island/redrock-canyon/snowy-peaks, courses harbor-hop,
lighthouse-loop, canyon-dash, mesa-skyway, snowpine-circuit,
aurora-pass; classes car/motorcycle; types circuit|boost|stunt|time|
gadget|clean -> runtime clean-driving; 72 regular + 6 finales, 8
vehicles, 6 rivals) — never from the observed rows. A production pass
requires every cell proven: completed under a bound receipt, human
attribution, measured duration, all three objective bits with proofs,
plus the reward-level facts (8 vehicles, 6 distinct rival victories,
>=6 non-victory extra stars per region). Removed or missing evidence
is a named gap; contradictory evidence (duplicates, unexpected rows,
AI-attributed finishes, proofs for unearned bits, earned bits without
proofs, invalid capture) is a defect.

Honesty contract: everything the run drives goes through the normal
command surface (profile.*, campaign.selectWorld, the loop
*.open/*.start commands, input.set, results.*, lan.*). No command
grants coins, stars or traversal. Unavailable content, locked gates
and unproven objectives report notVerified — never fabricated
coverage. --fixture labels every artifact fixture:true; fixture
evidence can never produce a production "passed" verdict.

Slice-A note: solo driving rides the shared graybox pursuit follower
(GameplayLoopScenario.SoloDriver over NetworkScenario's LAN_ROUTE);
authored per-course route/checkpoint observations replace it in
slice B once the production courses exist. Local processes and AI
seats are substitutes where used and are labeled as such.
"""

import argparse
import json
import sys
import traceback
from pathlib import Path

from AgentScenario import SUPPORTED_STATE_SCHEMAS
from RestartScenario import (build_fingerprint, file_hash, write_json)
from NetworkScenario import read_connection, _LAN_DRIVERS
from GameplayLoopScenario import (
    Checks, LoopClient, campaign_rows, drive_loop_race,
    expect_new_receipt, finish_client, launch_generation,
    profile_snapshot, progression_available, record_content_identity,
    run_lan_scope, start_campaign_event, terminate_process,
    verify_run_generations, PRESERVED_KEYS, CONTENT_UNAVAILABLE,
    _SOLO_DRIVERS)


REQUIRED_SCHEMA = 7

# The production content contract — mirrors the fixed inventory in
# Assets/FamilyRacing/Editor/FullContentValidation.cs. These are the
# EXPECTED rows the coverage reconciliation synthesizes; observed rows
# can never shrink or grow this list.
PRODUCTION_CONTENT_IDENTITY = "full-content-1"
PRODUCTION_GAMEPLAY_VERSION = "gameplay-4"
PRODUCTION_LAN_WIRE = 3

WORLD_IDS = ("sunny-island", "redrock-canyon", "snowy-peaks")
WORLD_COURSES = {
    "sunny-island": ("harbor-hop", "lighthouse-loop"),
    "redrock-canyon": ("canyon-dash", "mesa-skyway"),
    "snowy-peaks": ("snowpine-circuit", "aurora-pass"),
}
CLASS_IDS = ("car", "motorcycle")
TYPE_SUFFIXES = ("circuit", "boost", "stunt", "time", "gadget",
                 "clean")
VEHICLE_IDS = ("car-01", "car-02", "car-03", "car-04",
               "motorcycle-01", "motorcycle-02", "motorcycle-03",
               "motorcycle-04")
REGULAR_EVENT_COUNT = 72
FINALE_COUNT = 6

# EventProgress.objectiveMask bits (ObjectiveEvaluator.cs).
COMPLETION_BIT = 0x1
INTRODUCTORY_BIT = 0x2
MASTERY_BIT = 0x4
FULL_MASK = COMPLETION_BIT | INTRODUCTORY_BIT | MASTERY_BIT
BIT_NAMES = ((COMPLETION_BIT, "completion"),
             (INTRODUCTORY_BIT, "introductory"),
             (MASTERY_BIT, "mastery"))
OBJECTIVE_BIT_NAMES = ("introductory", "mastery")

# Plan: "at least six non-victory extra stars per region" — star bits
# beyond the completion bit earned on runs that did not win.
NONVICTORY_STARS_PER_WORLD = 6

# Simulation runs at 30 ticks/second (NetworkSettings).
TICKS_PER_SECOND = 30

HUMAN_ONLY_GAPS = [
    "physical input, rendered visuals and bilingual audio on real "
    "devices remain human-only evidence",
    "child/family playtest pacing (6-10 h target) is a human-only "
    "study this scenario cannot substitute",
]


# ---------------------------------------------------------------------------
# Expected inventory (synthesized from the fixed contract)
# ---------------------------------------------------------------------------

def runtime_type(suffix):
    """Event-id suffix -> runtime eventType ('clean' maps to the
    runtime type 'clean-driving'; every other suffix is its own)."""
    return "clean-driving" if suffix == "clean" else suffix


def regular_event_id(course_id, class_id, suffix):
    return "%s-%s-%s" % (course_id, class_id, suffix)


def finale_id(world_id, class_id):
    return "%s-%s-finale" % (world_id, class_id)


def rival_id(world_id, class_id):
    return "%s-%s-rival" % (world_id, class_id)


def livery_id(world_id, class_id):
    return "livery-" + rival_id(world_id, class_id)


def reward_vehicle_id(world_index, class_id):
    return "%s-0%d" % (class_id, world_index + 2)


def expected_inventory():
    """The fixed production contract: 3 ordered worlds, 6 courses,
    8 vehicles, 72 regular cells, 6 rival finales, 6 rivals/liveries."""
    worlds = []
    regular = []
    finales = []
    for index, world_id in enumerate(WORLD_IDS):
        worlds.append({
            "worldId": world_id, "order": index,
            "courses": list(WORLD_COURSES[world_id]),
            "requiredEvents": len(WORLD_COURSES[world_id])
            * len(CLASS_IDS) * len(TYPE_SUFFIXES),
            "totalFinales": len(CLASS_IDS),
            "rewardVehicleIds": [reward_vehicle_id(index, c)
                                 for c in CLASS_IDS],
            "finaleIds": [finale_id(world_id, c) for c in CLASS_IDS],
            "rivalIds": [rival_id(world_id, c) for c in CLASS_IDS],
        })
        for course_id in WORLD_COURSES[world_id]:
            for class_id in CLASS_IDS:
                for suffix in TYPE_SUFFIXES:
                    regular.append({
                        "eventId": regular_event_id(
                            course_id, class_id, suffix),
                        "worldId": world_id, "courseId": course_id,
                        "classId": class_id, "type": suffix,
                        "runtimeType": runtime_type(suffix),
                        "kind": "regular"})
        for class_id in CLASS_IDS:
            finales.append({
                "eventId": finale_id(world_id, class_id),
                "worldId": world_id, "courseId": "",
                "classId": class_id, "type": "finale",
                "runtimeType": "finale", "kind": "finale",
                "rivalId": rival_id(world_id, class_id),
                "liveryId": livery_id(world_id, class_id),
                "rewardVehicleId": reward_vehicle_id(index, class_id)})
    return {
        "worlds": worlds, "regular": regular, "finales": finales,
        "vehicles": list(VEHICLE_IDS),
        "rivals": [f["rivalId"] for f in finales],
        "liveries": [f["liveryId"] for f in finales],
        "contentIdentity": PRODUCTION_CONTENT_IDENTITY,
        "gameplayVersion": PRODUCTION_GAMEPLAY_VERSION,
    }


# ---------------------------------------------------------------------------
# Per-event evidence records
# ---------------------------------------------------------------------------

def new_event_record(row, fixture=False):
    """One evidence record per expected inventory row.

    Carries the full evidence contract: world/course/class/type, build
    and content identities, the actual commands issued, the bound race
    and result identities, completion/objective facts with per-bit
    proofs, rewards, measured duration, capture validity and the named
    human-only gaps. status is passed|failed|notVerified — a record
    only reaches passed through observed evidence."""
    record = {
        "eventId": row["eventId"], "worldId": row["worldId"],
        "courseId": row.get("courseId") or "",
        "classId": row["classId"], "type": row.get("type") or "",
        "runtimeType": row.get("runtimeType") or "",
        "kind": row["kind"],
        "status": "notVerified", "detail": "not attempted",
        "buildVersion": "", "contentVersion": "",
        "contentIdentity": "",
        "commandIds": [], "inputHoldCount": 0,
        "raceId": "", "resultId": "",
        "completed": False,
        "objectiveMaskBefore": 0, "objectiveMaskAfter": 0,
        "earnedBits": [], "objectiveProofs": {},
        "victory": None,
        "rewards": {"coinsAwarded": None, "receiptResultId": "",
                    "vehicleId": "", "liveryId": ""},
        "durationTicks": None, "durationSeconds": None,
        "captureValid": None, "attribution": "",
        "fixture": bool(fixture),
        "humanGaps": list(HUMAN_ONLY_GAPS),
        "attempts": 0,
    }
    if row["kind"] == "finale":
        record["rivalId"] = row["rivalId"]
        record["liveryId"] = row["liveryId"]
        record["rewardVehicleId"] = row["rewardVehicleId"]
    return record


# ---------------------------------------------------------------------------
# Coverage reconciliation — expected contract vs observed evidence
# ---------------------------------------------------------------------------

def _bit_names(mask):
    return [name for bit, name in BIT_NAMES if mask & bit]


def _evaluate_record(record, row, fixture, capture_valid):
    """One observed record against its expected cell. Returns
    (status, detail) where status is passed|failed|notVerified."""
    event_id = row["eventId"]
    status = record.get("status")
    detail = record.get("detail") or ""
    if status == "failed":
        return "failed", detail or "recorded as failed"
    if fixture or record.get("fixture"):
        return "notVerified", \
            "fixture evidence can never prove a production cell" \
            + (" (%s)" % detail if detail else "")
    if not capture_valid or record.get("captureValid") is False:
        return "failed", \
            "capture invalid — dropped or corrupt stream records " \
            "can never prove the cell"
    if status == "notVerified":
        return "notVerified", detail or "not attempted"
    if status != "passed":
        return "failed", "unknown record status " + repr(status)
    # A passed record must carry the facts it claims.
    problems = []
    if not record.get("completed"):
        problems.append("claims passed without a completed event")
    attribution = record.get("attribution")
    if attribution != "human":
        problems.append("finish attribution %r — AI-attributed "
                        "actions can never prove a human completion"
                        % attribution)
    if not record.get("raceId") or not record.get("resultId"):
        problems.append("missing bound race/result identity")
    receipt_id = (record.get("rewards") or {}).get("receiptResultId")
    if receipt_id != record.get("resultId"):
        problems.append("no receipt bound to this result's identity")
    duration = record.get("durationTicks")
    if not (type(duration) is int and duration > 0):
        problems.append("no measured duration")
    if record.get("captureValid") is not True:
        problems.append("capture validity was never measured on "
                        "this record")
    mask = int(record.get("objectiveMaskAfter") or 0) & FULL_MASK
    proofs = record.get("objectiveProofs") or {}
    for bit, name in BIT_NAMES:
        earned = bool(mask & bit)
        proof = proofs.get(name)
        if earned and not proof:
            problems.append("earned %s bit without a recorded proof"
                            % name)
        if not earned and proof:
            problems.append("proof claims unearned %s bit" % name)
    if problems:
        return "failed", "; ".join(problems)
    if row["kind"] == "regular" and mask != FULL_MASK:
        missing = _bit_names(FULL_MASK & ~mask)
        return "notVerified", \
            "finished (%s) but objectives unproven: %s — finishing " \
            "an event alone does not prove its objectives" % (
                detail or "complete", ", ".join(missing))
    return "passed", detail or "proven"


def observed_capture(client):
    """The capture-health flag the endpoint streams on every boundary —
    the measured fact feeding the reconcile gate, never a literal."""
    return bool((client.state or {}).get("captureValid"))


def reconcile_coverage(records, *, fixture=False, capture_valid=True,
                       world_rows=None, reward_facts=None,
                       label="coverage"):
    """Synthesize expected-vs-observed coverage.

    The expected inventory comes from the fixed 72+6 contract, never
    from `records`. Returns {"status", "checks", "cells", "defects",
    "gaps", "totals", "rewards"}; status is "passed" only when every
    production row is proven on a non-fixture run with a valid
    capture — otherwise "partial" (honest gaps) or "failed"
    (contradictory evidence)."""
    inv = expected_inventory()
    checks, cells, defects, gaps = [], [], [], []

    def fail(name, detail):
        checks.append({"name": name, "status": "failed",
                       "detail": detail})
        defects.append(detail)

    def gap(name, detail):
        checks.append({"name": name, "status": "notVerified",
                       "detail": detail})
        gaps.append(detail)

    def ok(name, detail=""):
        row = {"name": name, "status": "passed"}
        if detail:
            row["detail"] = detail
        checks.append(row)

    # -- world rows: the schema-7 contract publishes EVERY authored
    #    world in catalog order, locked ones included — a missing or
    #    duplicated row is a defect, never an absence.
    expected_world_ids = [w["worldId"] for w in inv["worlds"]]
    observed_world_ids = []
    if world_rows is None:
        for world in inv["worlds"]:
            gap("coverage.world." + world["worldId"],
                "progression.worlds surface not observed")
    else:
        observed_world_ids = [row.get("worldId")
                              for row in world_rows
                              if isinstance(row, dict)]
        for wid in observed_world_ids:
            if wid not in expected_world_ids:
                fail("coverage.world." + str(wid),
                     "unexpected world row " + repr(wid)
                     + " — outside the production contract")
            if observed_world_ids.count(wid) > 1:
                fail("coverage.world." + str(wid),
                     "duplicate world row " + repr(wid))
        filtered = [wid for wid in observed_world_ids
                    if wid in expected_world_ids]
        if filtered != expected_world_ids[:len(filtered)]:
            fail("coverage.worlds",
                 "world rows out of catalog order: %s" % filtered)
        for world in inv["worlds"]:
            name = "coverage.world." + world["worldId"]
            row = next((r for r in world_rows
                        if isinstance(r, dict)
                        and r.get("worldId") == world["worldId"]),
                       None)
            if row is None:
                fail(name, "world '%s' has no progression.worlds row "
                     "— schema 7 publishes every authored world"
                     % world["worldId"])
                continue
            if row.get("totalFinales") != world["totalFinales"]:
                fail(name, "world '%s' reports %d finales, expected %d"
                     % (world["worldId"], row.get("totalFinales"),
                        world["totalFinales"]))
                continue
            rewards = sorted(row.get("rewardVehicleIds") or [])
            if rewards != sorted(world["rewardVehicleIds"]):
                fail(name, "world '%s' rewardVehicleIds %r, expected %r"
                     % (world["worldId"], rewards,
                        sorted(world["rewardVehicleIds"])))
                continue
            if row.get("available"):
                ok(name, "available; %d/%d events, %d stars, %d/%d "
                   "finales" % (row.get("completedEvents", 0),
                                row.get("requiredEvents", 0),
                                row.get("stars", 0),
                                row.get("completedFinales", 0),
                                row.get("totalFinales", 0)))
            else:
                ok(name, "observed locked (%s) — its cells gate the "
                   "verdict" % (row.get("lockedReason")
                                or "worldLocked"))

    # -- per-cell reconciliation
    by_id = {}
    for record in records:
        if not isinstance(record, dict) \
                or not isinstance(record.get("eventId"), str) \
                or not record["eventId"]:
            fail("coverage.malformed",
                 "evidence row missing its event identity: %r"
                 % (record,))
            continue
        by_id.setdefault(record["eventId"], []).append(record)

    nonvictory = {wid: 0 for wid in expected_world_ids}
    completed_count = 0
    proven_count = 0
    for row in inv["regular"] + inv["finales"]:
        event_id = row["eventId"]
        name = "coverage.cell." + event_id
        recs = by_id.pop(event_id, [])
        cell = dict(row)
        if len(recs) > 1:
            fail(name, "duplicate evidence for cell %s (%d rows) — "
                 "one cell can never be proven twice"
                 % (event_id, len(recs)))
            cell["status"] = "failed"
            cells.append(cell)
            continue
        if not recs:
            gap(name, "%s: no evidence — the event was never "
                "observed or driven" % event_id)
            cell["status"] = "notVerified"
            cells.append(cell)
            continue
        record = recs[0]
        status, detail = _evaluate_record(
            record, row, fixture, capture_valid)
        cell["status"] = status
        cell["detail"] = detail
        cells.append(cell)
        if record.get("completed") and not record.get("fixture"):
            completed_count += 1
        if status == "passed":
            ok(name, detail)
            proven_count += 1
        elif status == "failed":
            fail(name, detail)
        else:
            gap(name, detail)
        # Non-victory extra stars: star bits beyond completion earned
        # on a run the human did not win.
        if row["kind"] == "regular" and record.get("victory") is False \
                and status == "passed":
            extra = [b for b in record.get("earnedBits") or []
                     if b != "completion"]
            nonvictory[row["worldId"]] += len(extra)
        # A finale the record claims was won must appear in the
        # rival-victory reward facts — a win with no reward trail is
        # contradictory evidence.
        if row["kind"] == "finale" and record.get("victory") is True:
            victories = (reward_facts or {}).get("rivalVictories") \
                or []
            if not any(v.get("rivalId") == row["rivalId"]
                       for v in victories
                       if isinstance(v, dict)):
                fail("coverage.rewards." + row["rivalId"],
                     "finale %s recorded a victory but no rival "
                     "reward proof exists" % event_id)

    # -- observed rows outside the contract are defects, never extra
    #    coverage.
    for event_id, recs in sorted(by_id.items()):
        fail("coverage.unexpected." + str(event_id),
             "observed event %r outside the production contract "
             "(%d records)" % (event_id, len(recs)))

    # -- reward-level facts
    rewards = reward_facts or {}
    vehicles = rewards.get("unlockedVehicles")
    if vehicles is None:
        gap("coverage.vehicles", "final unlocked-vehicle roster "
            "never observed")
    else:
        missing = [v for v in VEHICLE_IDS if v not in vehicles]
        extra = [v for v in vehicles if v not in VEHICLE_IDS]
        if extra:
            fail("coverage.vehicles",
                 "unexpected vehicles unlocked: %s" % extra)
        elif missing:
            gap("coverage.vehicles", "unproven reward vehicles: %s "
                "(%d/8 observed)" % (missing, 8 - len(missing)))
        else:
            ok("coverage.vehicles",
               "all eight vehicle models unlocked")
    victories = rewards.get("rivalVictories")
    if victories is None:
        gap("coverage.rivals",
            "no rival-victory reward evidence recorded")
    else:
        seen_proofs = {}
        for world in inv["worlds"]:
            for rid in world["rivalIds"]:
                entries = [v for v in victories
                           if isinstance(v, dict)
                           and v.get("rivalId") == rid]
                if len(entries) > 1:
                    fail("coverage.rivals." + rid,
                         "duplicate victory proofs for rival " + rid)
                    continue
                if not entries:
                    gap("coverage.rivals." + rid,
                        "no victory proof — the separate rival "
                        "reward was never observed")
                    continue
                proof = entries[0]
                proof_id = proof.get("resultId")
                if not proof_id:
                    fail("coverage.rivals." + rid,
                         "victory proof carries no result identity")
                    continue
                if proof_id in seen_proofs:
                    fail("coverage.rivals." + rid,
                         "result %r already proves %s — six "
                         "separate rival victories are required"
                         % (proof_id, seen_proofs[proof_id]))
                    continue
                seen_proofs[proof_id] = rid
                if proof.get("liveryId") != livery_id(
                        world["worldId"], rid.rsplit("-", 2)[-2]):
                    fail("coverage.rivals." + rid,
                         "victory proof names livery %r, expected %r"
                         % (proof.get("liveryId"),
                            livery_id(world["worldId"],
                                      rid.rsplit("-", 2)[-2])))
                    continue
                # The livery/vehicle grants must be OBSERVED in the
                # profile (ownedCosmetics / unlockedVehicles) — a
                # victory row alone never proves the reward landed.
                # An explicit False is contradictory evidence; a
                # missing key means the grant was never measured.
                if proof.get("liveryGranted") is False \
                        or proof.get("vehicleGranted") is False:
                    fail("coverage.rivals." + rid,
                         "victory recorded but the reward never "
                         "landed (liveryGranted=%r, vehicleGranted=%r)"
                         % (proof.get("liveryGranted"),
                            proof.get("vehicleGranted")))
                    continue
                if proof.get("liveryGranted") is not True \
                        or proof.get("vehicleGranted") is not True:
                    gap("coverage.rivals." + rid,
                        "reward grant never observed — liveryGranted"
                        "/vehicleGranted facts missing from the proof")
                    continue
                ok("coverage.rivals." + rid,
                   "rival victory and reward grants proven by "
                   "result %s" % proof_id)
        for victory in victories:
            if isinstance(victory, dict) \
                    and victory.get("rivalId") not in inv["rivals"]:
                fail("coverage.rivals." + str(victory.get("rivalId")),
                     "unexpected rival-victory proof")

    for world in inv["worlds"]:
        wid = world["worldId"]
        earned = nonvictory[wid]
        if earned >= NONVICTORY_STARS_PER_WORLD:
            ok("coverage.nonVictory." + wid,
               "%d non-victory extra star bits earned" % earned)
        else:
            gap("coverage.nonVictory." + wid,
                "%d/%d non-victory extra star bits — additional "
                "objective runs required"
                % (earned, NONVICTORY_STARS_PER_WORLD))

    # -- totals
    totals = {
        "expectedRegular": REGULAR_EVENT_COUNT,
        "expectedFinales": FINALE_COUNT,
        "expectedEvents": REGULAR_EVENT_COUNT + FINALE_COUNT,
        "observedRecords": len(records),
        "completedEvents": completed_count,
        "provenCells": proven_count,
        "failedCells": len([c for c in cells
                            if c["status"] == "failed"]),
        "unprovenCells": len([c for c in cells
                              if c["status"] == "notVerified"]),
        "nonVictoryStars": nonvictory,
    }
    if completed_count == REGULAR_EVENT_COUNT + FINALE_COUNT \
            and not fixture:
        ok("coverage.completions",
           "78/78 events completed under bound receipts")
    elif defects:
        fail("coverage.completions",
             "%d/78 events completed; contradictory evidence present"
             % completed_count)
    else:
        gap("coverage.completions", "%d/78 events completed"
            % completed_count)
    if not capture_valid:
        fail("coverage.capture", "capture invalid — dropped or "
             "corrupt stream records")
    else:
        ok("coverage.capture", "capture valid")
    if fixture:
        gap("coverage.fixture",
            "fixture run — synthetic protocol evidence can never "
            "prove production coverage")

    if defects:
        status = "failed"
    elif gaps or fixture:
        status = "partial"
    else:
        status = "passed"
    return {"status": status, "checks": checks, "cells": cells,
            "defects": defects, "gaps": gaps, "totals": totals,
            "rewards": {"vehicles": vehicles,
                        "rivalVictories": victories}}


# ---------------------------------------------------------------------------
# Shared plumbing
# ---------------------------------------------------------------------------

def scenario_report(scope, fixture):
    return {
        "status": "failed", "scope": scope, "fixture": bool(fixture),
        "checks": [], "notes": [], "runs": [], "notVerified": [],
        "traces": {}, "contentIdentity": "", "identities": {},
        "events": [], "coverage": None,
        "humanGaps": list(HUMAN_ONLY_GAPS),
    }


def stream_identities(client):
    """Build/content/device identities from the live stream records —
    every sequenced record carries them, so the latest observed value
    is the honest binding."""
    identities = {"runId": client.info.get("runId", ""),
                  "deviceId": "", "buildVersion": "",
                  "contentVersion": ""}
    for event in reversed(client.events):
        if event.get("buildVersion"):
            identities["buildVersion"] = event["buildVersion"]
            identities["contentVersion"] = \
                event.get("contentVersion", "")
            identities["deviceId"] = event.get("deviceId", "")
            break
    return identities


def stamp_identities(record, identities, content_identity):
    record["buildVersion"] = identities.get("buildVersion", "")
    record["contentVersion"] = identities.get("contentVersion", "")
    record["contentIdentity"] = content_identity or ""


def write_events(directory, records):
    path = Path(directory) / "events.ndjson"
    with path.open("w", encoding="utf-8") as out:
        for record in records:
            out.write(json.dumps(record, ensure_ascii=False) + "\n")


def publish_coverage(report, checks, evidence_root, records,
                     coverage):
    """Attach the reconciliation to the report and the evidence dir."""
    write_events(evidence_root, records)
    write_json(Path(evidence_root) / "coverage.json", coverage)
    report["events"] = records
    report["coverage"] = coverage["totals"]
    for row in coverage["checks"]:
        if row["status"] == "passed":
            checks.passed(row["name"], row.get("detail", ""))
        elif row["status"] == "failed":
            checks.failed(row["name"], row.get("detail", ""))
        else:
            checks.not_verified(row["name"], row.get("detail", ""))


def ensure_profile(client, checks, require_fresh):
    """catalog/campaign/modes all need a selected profile — the loop
    commands reject requiresSelectedProfile otherwise."""
    state = client.state
    if state["activeSlot"] >= 0:
        checks.passed("profile.selected",
                      "endpoint already had a selected profile")
        return
    slots = state["slots"]
    if all(slot["status"] == "Missing" for slot in slots):
        if require_fresh:
            client.applied("profile.create",
                           {"slot": 0, "language": "en"})
            checks.passed("profile.fresh",
                          "fresh synthetic profile on an empty store")
        else:
            client.applied("profile.create",
                           {"slot": 0, "language": "en"})
            checks.passed("profile.fresh",
                          "attached endpoint: profile created on its "
                          "own store")
        return
    if require_fresh:
        raise AssertionError(
            "scope requires a fresh synthetic profile store")
    loaded = next(index for index, slot in enumerate(slots)
                  if slot["status"] == "Loaded")
    client.applied("profile.select", {"slot": loaded})
    checks.passed("profile.selected",
                  "attached endpoint: profile resolved on its own "
                  "store")


def world_row(state, world_id):
    for row in (state.get("progression") or {}).get("worlds") or []:
        if isinstance(row, dict) and row.get("worldId") == world_id:
            return row
    return None


def mark_events_unverified(records, reason):
    for record in records:
        if record["status"] == "notVerified" \
                and record["detail"] == "not attempted":
            record["detail"] = reason


def mark_world_unverified(records, world_id, reason):
    for record in records:
        if record["worldId"] == world_id:
            record["status"] = "notVerified"
            record["detail"] = reason


# ---------------------------------------------------------------------------
# Catalog scope — inventory reconciliation only, no driving
# ---------------------------------------------------------------------------

def run_catalog_scope(client, checks, report, fixture, evidence_root):
    inv = expected_inventory()
    records = [new_event_record(row, fixture=fixture)
               for row in inv["regular"] + inv["finales"]]
    identities = stream_identities(client)
    progression = client.state.get("progression") or {}
    content = progression.get("contentIdentity") or ""
    report["identities"] = identities
    report["contentIdentity"] = content
    for record in records:
        stamp_identities(record, identities, content)
    world_rows = progression.get("worlds")
    available = progression.get("available") is True
    if not available:
        mark_events_unverified(records, CONTENT_UNAVAILABLE)
        coverage = reconcile_coverage(
            records, fixture=fixture,
            capture_valid=observed_capture(client),
            world_rows=world_rows, reward_facts=None)
        publish_coverage(report, checks, evidence_root, records,
                         coverage)
        return
    # The bound catalog must be the production content identity —
    # anything else is honest unavailability, not the full catalog.
    identity = content.split("|")[0]
    if identity != PRODUCTION_CONTENT_IDENTITY:
        checks.failed(
            "catalog.identity",
            "contentIdentity %r is not the production %r contract"
            % (content, PRODUCTION_CONTENT_IDENTITY))
    else:
        checks.passed("catalog.identity",
                      "contentIdentity %r binds the production contract"
                      % content)
    for world in inv["worlds"]:
        wid = world["worldId"]
        row = world_row(client.state, wid)
        if row is None:
            # reconcile names the missing worlds row itself.
            continue
        if not row.get("available"):
            mark_world_unverified(
                records, wid,
                "world locked (%s)" % (row.get("lockedReason")
                                       or "worldLocked"))
            continue
        selected, _ = client.command(
            "campaign.selectWorld", {"worldId": wid})
        if selected["status"] != "applied":
            for record in records:
                if record["worldId"] == wid:
                    record["status"] = "failed"
                    record["detail"] = (
                        "world row claims available but "
                        "campaign.selectWorld rejected: %s"
                        % (selected.get("code")
                           or selected["status"]))
            checks.failed(
                "catalog.selectWorld." + wid,
                "available world refused selection: %r" % selected)
            continue
        observed = [r for r in campaign_rows(client.state)
                    if isinstance(r, dict) and r.get("eventId")]
        seen_ids = set()
        observed_by_id = {}
        duplicates = []
        for obs in observed:
            eid = obs["eventId"]
            if eid in seen_ids:
                duplicates.append(eid)
            seen_ids.add(eid)
            observed_by_id.setdefault(eid, obs)
        world_records = [r for r in records
                         if r["worldId"] == wid]
        for record in world_records:
            event_id = record["eventId"]
            obs = observed_by_id.get(event_id)
            if event_id in duplicates:
                record["status"] = "failed"
                record["detail"] = ("duplicate published "
                                    "campaignEvents rows")
                continue
            if obs is None:
                record["status"] = "failed"
                record["detail"] = ("world is available but the "
                                    "event row was never published")
                continue
            problems = []
            if record["kind"] == "regular":
                if obs.get("courseId") != record["courseId"]:
                    problems.append("courseId %r" % obs.get("courseId"))
                if obs.get("classId") != record["classId"]:
                    problems.append("classId %r" % obs.get("classId"))
                if obs.get("eventType") != record["runtimeType"]:
                    problems.append("eventType %r"
                                    % obs.get("eventType"))
                if obs.get("finale"):
                    problems.append("regular event marked finale")
            else:
                if not obs.get("finale"):
                    problems.append("finale row missing finale flag")
                if obs.get("rivalId") != record["rivalId"]:
                    problems.append("rivalId %r" % obs.get("rivalId"))
                if obs.get("rewardVehicleId") \
                        != record["rewardVehicleId"]:
                    problems.append("rewardVehicleId %r"
                                    % obs.get("rewardVehicleId"))
            if problems:
                record["status"] = "failed"
                record["detail"] = "published row disagrees with " \
                    "the contract: " + "; ".join(problems)
                continue
            # Published and contract-consistent — inventory verified,
            # coverage still unproven (catalog scope never drives).
            record["captureValid"] = \
                (client.state or {}).get("captureValid")
            record["completed"] = bool(obs.get("completed"))
            record["objectiveMaskAfter"] = \
                obs.get("objectiveMask", 0) & FULL_MASK
            record["detail"] = "published; %s" % (
                "unlocked" if not obs.get("locked")
                else "locked (%s)" % (obs.get("lockReason")
                                      or "eventLocked"))
        checks.passed("catalog.world." + wid,
                      "%d published rows reconciled against the "
                      "contract" % len(observed))
    # Re-read the world surface at the end: selectWorld boundaries
    # refreshed it since the scope started.
    world_rows = (client.state.get("progression") or {}).get("worlds")
    coverage = reconcile_coverage(
        records, fixture=fixture,
        capture_valid=observed_capture(client),
        world_rows=world_rows, reward_facts=None)
    publish_coverage(report, checks, evidence_root, records,
                     coverage)


# ---------------------------------------------------------------------------
# Campaign scope — the 78-event ordered traversal
# ---------------------------------------------------------------------------

def drive_campaign_event(client, record, checks, notes):
    """Start, drive and finalize one campaign event through the real
    command surface; fill the evidence record with observed facts."""
    event_id = record["eventId"]
    record["attempts"] += 1
    row = next((r for r in campaign_rows(client.state)
                if r.get("eventId") == event_id), None)
    mask_before = (row or {}).get("objectiveMask", 0) & FULL_MASK
    record["objectiveMaskBefore"] = mask_before
    before = profile_snapshot(client)
    vehicle, started = start_campaign_event(client, event_id, checks)
    record["commandIds"].append(started["commandId"])
    result_obj = drive_loop_race(client, checks,
                                 "campaign " + event_id)
    after = profile_snapshot(client)
    receipt = expect_new_receipt(before, after, result_obj,
                                 "campaign " + event_id)
    record["raceId"] = result_obj.get("raceId") or ""
    record["resultId"] = result_obj.get("resultId") or ""
    record["rewards"] = {
        "coinsAwarded": receipt.get("awardedCoins"),
        "receiptResultId": receipt.get("resultId") or "",
        "vehicleId": "", "liveryId": ""}
    own = next((r for r in result_obj.get("racers") or []
                if r and r.get("participantId")
                == (client.state.get("soloRace") or {})
                .get("participantId")), None) or {}
    record["attribution"] = own.get("attribution") or ""
    record["completed"] = own.get("outcome") == "finished"
    record["victory"] = record["completed"] \
        and own.get("position") == 1
    finish_ticks = own.get("finishTicks")
    if type(finish_ticks) is int and finish_ticks > 0:
        record["durationTicks"] = finish_ticks
        record["durationSeconds"] = finish_ticks / TICKS_PER_SECOND
    else:
        record["durationTicks"] = None
        record["durationSeconds"] = None
    # The observed capture-health flag the endpoint streams on every
    # boundary — the honest measurement, never a default.
    record["captureValid"] = (client.state or {}).get("captureValid")
    row_after = next((r for r in campaign_rows(client.state)
                      if r.get("eventId") == event_id), {})
    mask_after = row_after.get("objectiveMask", 0) & FULL_MASK
    record["objectiveMaskAfter"] = mask_after
    record["completed"] = bool(
        record["completed"] or row_after.get("completed"))
    earned = mask_after & ~mask_before
    record["earnedBits"] = _bit_names(earned)
    proofs = record["objectiveProofs"]
    for bit, name in BIT_NAMES:
        if earned & bit:
            proofs[name] = {
                "resultId": record["resultId"],
                "maskBefore": mask_before, "maskAfter": mask_after}
        elif mask_before & bit and name not in proofs:
            proofs[name] = {"durable": True,
                            "maskBefore": mask_before}
    ended, _ = client.applied("results.continue")
    record["commandIds"].append(ended["commandId"])
    record["detail"] = "finished (%s) — mask %d -> %d, receipt %s" % (
        "victory" if record["victory"] else "non-victory",
        mask_before, mask_after, record["resultId"])
    record["status"] = "passed"


def run_campaign_scope(client, checks, report, fixture,
                       evidence_root):
    inv = expected_inventory()
    records = [new_event_record(row, fixture=fixture)
               for row in inv["regular"] + inv["finales"]]
    identities = stream_identities(client)
    progression = client.state.get("progression") or {}
    content = progression.get("contentIdentity") or ""
    report["identities"] = identities
    report["contentIdentity"] = content
    for record in records:
        stamp_identities(record, identities, content)
    notes = report["notes"]
    notes.append("driving rides the shared graybox pursuit follower "
                 "(SoloDriver over LAN_ROUTE); authored per-course "
                 "route/checkpoint observations are slice-B scope")
    notes.append("local process and AI seats are substitutes — "
                 "labeled fixture/substitute, never physical-device "
                 "proof")
    world_rows = progression.get("worlds")
    if not progression.get("available"):
        mark_events_unverified(records, CONTENT_UNAVAILABLE)
        coverage = reconcile_coverage(
            records, fixture=fixture,
            capture_valid=observed_capture(client),
            world_rows=world_rows, reward_facts=None)
        publish_coverage(report, checks, evidence_root, records,
                         coverage)
        return
    identity = content.split("|")[0]
    if identity != PRODUCTION_CONTENT_IDENTITY:
        checks.failed(
            "catalog.identity",
            "contentIdentity %r is not the production %r contract"
            % (content, PRODUCTION_CONTENT_IDENTITY))
    else:
        checks.passed("catalog.identity",
                      "contentIdentity %r binds the production contract"
                      % content)
    rival_facts = []
    for world in inv["worlds"]:
        wid = world["worldId"]
        row = world_row(client.state, wid)
        if row is None:
            continue   # reconcile names the missing worlds row
        if not row.get("available"):
            mark_world_unverified(
                records, wid,
                "world locked (%s)" % (row.get("lockedReason")
                                       or "worldLocked"))
            continue
        selected, _ = client.command(
            "campaign.selectWorld", {"worldId": wid})
        if selected["status"] != "applied":
            checks.failed(
                "campaign.selectWorld." + wid,
                "available world refused selection: %r" % selected)
            continue
        checks.passed("campaign.world." + wid,
                      "world selected through campaign.selectWorld")
        ordered = [r for r in records
                   if r["worldId"] == wid and r["kind"] == "regular"]
        ordered += [r for r in records
                    if r["worldId"] == wid and r["kind"] == "finale"]
        for record in ordered:
            event_id = record["eventId"]
            observed = next(
                (r for r in campaign_rows(client.state)
                 if r.get("eventId") == event_id), None)
            if observed is None:
                record["status"] = "failed"
                record["detail"] = ("world is available but the "
                                    "event row was never published")
                checks.failed("campaign.event." + event_id,
                              record["detail"])
                continue
            if observed.get("locked"):
                record["status"] = "notVerified"
                record["detail"] = "locked (%s) — the traversal " \
                    "gate kept it out of reach" % (
                        observed.get("lockReason") or "eventLocked")
                checks.not_verified(
                    "campaign.event." + event_id, record["detail"])
                continue
            try:
                drive_campaign_event(client, record, checks, notes)
            except Exception as error:
                record["status"] = "failed"
                record["detail"] = "%s: %s" % (type(error).__name__,
                                               error)
                checks.failed("campaign.event." + event_id,
                              record["detail"])
                # Recover to the campaign map so later events can
                # still be attempted honestly.
                try:
                    client.command("results.continue")
                except Exception:
                    try:
                        client.command("race.exit")
                    except Exception:
                        pass
                continue
            checks.passed(
                ("campaign.finale." if record["kind"] == "finale"
                 else "campaign.event.") + event_id,
                record["detail"])
            if record["kind"] == "finale" \
                    and record.get("victory") is True:
                profile = client.state.get("savedProfile") or {}
                cosmetics = {c.get("cosmeticId") for c in
                             profile.get("ownedCosmetics") or []
                             if isinstance(c, dict)}
                cosmetics |= {c for c in
                              profile.get("ownedCosmetics") or []
                              if isinstance(c, str)}
                unlocked = profile.get("unlockedVehicles") or []
                rival_facts.append({
                    "rivalId": record["rivalId"],
                    "liveryId": record["liveryId"],
                    "resultId": record["resultId"],
                    "rewardVehicleId": record["rewardVehicleId"],
                    "liveryGranted": record["liveryId"] in cosmetics,
                    "vehicleGranted": record["rewardVehicleId"]
                    in unlocked})
    profile = client.state.get("savedProfile") or {}
    reward_facts = {
        "unlockedVehicles": list(profile.get("unlockedVehicles")
                                 or []),
        "rivalVictories": rival_facts,
    }
    world_rows = (client.state.get("progression") or {}).get("worlds")
    coverage = reconcile_coverage(
        records, fixture=fixture,
        capture_valid=observed_capture(client),
        world_rows=world_rows, reward_facts=reward_facts)
    publish_coverage(report, checks, evidence_root, records,
                     coverage)


# ---------------------------------------------------------------------------
# Modes scope — all 12 course x class Quick Race / Time Trial combos
# ---------------------------------------------------------------------------

def run_modes_scope(client, checks, report, fixture, evidence_root):
    inv = expected_inventory()
    combos = [(course, class_id)
              for world in inv["worlds"]
              for course in world["courses"]
              for class_id in CLASS_IDS]
    rows = []
    progression = client.state.get("progression") or {}
    if not progression.get("available"):
        for course_id, class_id in combos:
            for mode in ("quickRace", "timeTrial"):
                rows.append({"combo": "%s-%s" % (course_id, class_id),
                             "mode": mode, "status": "notVerified",
                             "detail": CONTENT_UNAVAILABLE})
                checks.not_verified(
                    "modes.%s.%s-%s" % (mode, course_id, class_id),
                    CONTENT_UNAVAILABLE)
        write_json(Path(evidence_root) / "modes.json", {"rows": rows})
        report["modes"] = rows
        return
    ledger_before = {row["eventId"]: dict(row)
                     for row in campaign_rows(client.state)}
    vehicles = (client.state.get("savedProfile") or {}) \
        .get("unlockedVehicles") or []
    opened, _ = client.command("quickRace.open")
    quick_open = opened["status"] == "applied"
    if not quick_open:
        checks.not_verified("modes.quickRace.open",
                            "quickRace.open rejected " + repr(opened))
    campaign_ledger_stable = True
    for course_id, class_id in combos:
        name = "modes.quickRace.%s-%s" % (course_id, class_id)
        row = {"combo": "%s-%s" % (course_id, class_id),
               "mode": "quickRace", "status": "notVerified",
               "detail": ""}
        if not quick_open:
            row["detail"] = "quickRace.open rejected"
        else:
            applied = None
            for vehicle in vehicles:
                probe, _ = client.command(
                    "quickRace.start",
                    {"courseId": course_id, "classId": class_id,
                     "vehicleId": vehicle, "difficulty": "easy"})
                if probe["status"] == "applied":
                    applied = vehicle
                    break
            if applied is None:
                row["detail"] = ("no authored circuit resolved for "
                                 "the course/class pick")
            else:
                try:
                    before = profile_snapshot(client)
                    result_obj = drive_loop_race(
                        client, checks,
                        "quickRace %s/%s" % (course_id, class_id))
                    expect_new_receipt(
                        before, profile_snapshot(client), result_obj,
                        "quickRace %s/%s" % (course_id, class_id))
                    assert result_obj.get("mode") == "quickRace", \
                        result_obj
                    client.applied("results.continue")
                    row.update(status="passed",
                               detail="resolved and finished through "
                               "quickRace.start (%s)" % applied,
                               resultId=result_obj.get("resultId"))
                except Exception as error:
                    row["status"] = "failed"
                    row["detail"] = "%s: %s" % (
                        type(error).__name__, error)
                    try:
                        client.command("results.continue")
                    except Exception:
                        pass
        rows.append(row)
        if row["status"] == "passed":
            checks.passed(name, row["detail"])
        elif row["status"] == "failed":
            checks.failed(name, row["detail"])
        else:
            checks.not_verified(name, row["detail"])
        # A quick race can never mint campaign credit.
        ledger_now = {r["eventId"]: dict(r)
                      for r in campaign_rows(client.state)}
        if ledger_now != ledger_before:
            campaign_ledger_stable = False
    opened, _ = client.command("timeTrial.open")
    trial_open = opened["status"] == "applied"
    if not trial_open:
        checks.not_verified("modes.timeTrial.open",
                            "timeTrial.open rejected " + repr(opened))
    for course_id, class_id in combos:
        name = "modes.timeTrial.%s-%s" % (course_id, class_id)
        row = {"combo": "%s-%s" % (course_id, class_id),
               "mode": "timeTrial", "status": "notVerified",
               "detail": ""}
        if not trial_open:
            row["detail"] = "timeTrial.open rejected"
        else:
            probe, _ = client.command(
                "timeTrial.start",
                {"courseId": course_id, "classId": class_id})
            if probe["status"] != "applied":
                row["detail"] = ("no authored time event resolved "
                                 "for the pick: %s"
                                 % (probe.get("code")
                                    or probe["status"]))
            else:
                try:
                    solo = client.state.get("soloRace") or {}
                    assert solo.get("tuningSource") == "normalized", \
                        "timeTrial did not report the normalized " \
                        "starter (tuningSource=%r)" \
                        % solo.get("tuningSource")
                    result_obj = drive_loop_race(
                        client, checks,
                        "timeTrial %s/%s" % (course_id, class_id))
                    profile = client.state.get("savedProfile") or {}
                    durable = [r for r in
                               profile.get("timeTrialRecords") or []
                               if r.get("courseId") == course_id
                               and r.get("classId") == class_id]
                    assert durable \
                        and durable[-1].get("bestTicks", 0) > 0, \
                        "finished trial left no durable record"
                    client.applied("results.continue")
                    row.update(status="passed",
                               detail="normalized record %d ticks "
                               "durable"
                               % durable[-1]["bestTicks"],
                               resultId=result_obj.get("resultId"))
                except Exception as error:
                    row["status"] = "failed"
                    row["detail"] = "%s: %s" % (
                        type(error).__name__, error)
                    try:
                        client.command("results.continue")
                    except Exception:
                        pass
        rows.append(row)
        if row["status"] == "passed":
            checks.passed(name, row["detail"])
        elif row["status"] == "failed":
            checks.failed(name, row["detail"])
        else:
            checks.not_verified(name, row["detail"])
        # A time trial can never mint campaign credit either — the
        # attestation below must be fed by BOTH loops, not just the
        # quick-race one.
        ledger_now = {r["eventId"]: dict(r)
                      for r in campaign_rows(client.state)}
        if ledger_now != ledger_before:
            campaign_ledger_stable = False
    if campaign_ledger_stable:
        checks.passed("modes.noCampaignCredit",
                      "Quick Race and Time Trial moved no campaign "
                      "ledger row")
    else:
        checks.failed("modes.noCampaignCredit",
                      "a non-campaign mode moved the campaign ledger")
    write_json(Path(evidence_root) / "modes.json", {"rows": rows})
    report["modes"] = rows


# ---------------------------------------------------------------------------
# Run drivers
# ---------------------------------------------------------------------------

def _finish_status(report, checks):
    if checks.failed_rows:
        report["status"] = "failed"
    elif checks.unverified_rows:
        report["status"] = "partial"
    else:
        report["status"] = "passed"


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
            assert 2 <= len(clients) <= 4, \
                "lan scope needs 2-4 endpoints"
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
                "aiFill": max(0, 4 - len(clients))}
        else:
            assert len(clients) == 1, \
                "catalog/campaign/modes take one connection"
            client = clients[0][1]
            ensure_profile(client, checks, require_fresh=False)
            if scope == "catalog":
                run_catalog_scope(client, checks, report, fixture,
                                  root)
            elif scope == "campaign":
                run_campaign_scope(client, checks, report, fixture,
                                   root)
                checks.not_verified(
                    "restart.progress",
                    "--connections mode owns no process; the restart "
                    "generation cannot be driven from here")
                checks.not_verified(
                    "restart.generations",
                    "--connections mode owns no process")
            elif scope == "modes":
                run_modes_scope(client, checks, report, fixture, root)
            record_content_identity(client, report)
            finish_client(client, client.info["_directory"], report,
                          "solo", "loop")
        if fixture:
            checks.not_verified(
                "fixture.label",
                "every artifact is fixture:true — synthetic protocol "
                "evidence can never prove production coverage")
        _finish_status(report, checks)
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
            for name in names:
                profile_dir = root / (name + "-profiles")
                process, info, directory, ev = launch_generation(
                    player_command, root, name, profile_dir,
                    startup_timeout, report, name, fixture)
                entry = {"name": name, "process": process,
                         "info": info, "directory": directory,
                         "evidence": ev, "profileDir": profile_dir,
                         "client": LoopClient(info, ev), "alive": True,
                         "generation": 1}
                entries.append(entry)
                roster.append(entry)
                profile_dirs[name] = profile_dir
            host = entries[0]["client"]
            lan_clients = [e["client"] for e in entries[1:]]
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
            run_lan_scope(host, lan_clients, checks, notes, fault=fault,
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
                "loopback": players, "physical": 0,
                "aiFill": max(0, 4 - players)}
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
            ensure_profile(client, checks, require_fresh=True)
            if scope == "catalog":
                run_catalog_scope(client, checks, report, fixture,
                                  root)
                record_content_identity(client, report)
                finish_client(client, directory, report, "solo",
                              "loop")
            elif scope == "modes":
                run_modes_scope(client, checks, report, fixture, root)
                record_content_identity(client, report)
                finish_client(client, directory, report, "solo",
                              "loop")
            else:
                # campaign: generation 1 drives the full adventure;
                # generation 2 proves durable progress survived a real
                # process restart.
                run_campaign_scope(client, checks, report, fixture,
                                   root)
                expected = profile_snapshot(client) or {}
                record_content_identity(client, report)
                finish_client(client, directory, report, "solo",
                              "loop")
                try:
                    client.close()
                except Exception:
                    pass
                entry["alive"] = False
                terminate_process(process, shutdown_timeout)
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
                client2.applied("profile.select", {"slot": 0})
                profile = client2.state.get("savedProfile") or {}
                lost = [key for key in PRESERVED_KEYS
                        if profile.get(key)
                        != expected.get(key)]
                assert not lost, \
                    "restart lost durable facts: " + ", ".join(lost)
                assert profile.get("unlockedVehicles") \
                    == expected.get("unlockedVehicles"), \
                    "restart lost unlocked vehicles"
                checks.passed(
                    "restart.progress",
                    "new process generation reloaded coins, receipts, "
                    "event/world progress, upgrades and records "
                    "identically")
                verify_run_generations(report["runs"], "campaign")
                checks.passed(
                    "restart.generations",
                    "restart evidence proves a distinct second "
                    "process generation")
                finish_client(client2, directory2, report,
                              "solo-restart", "loop")
        if fixture:
            checks.not_verified(
                "fixture.label",
                "every artifact is fixture:true — synthetic protocol "
                "evidence can never prove production coverage")
        _finish_status(report, checks)
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
                        choices=("catalog", "campaign", "modes",
                                 "lan"),
                        required=True)
    parser.add_argument("--players", type=int, default=2,
                        help="--launch + --scope lan: 2-4 local "
                             "players")
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
                "catalog/campaign/modes scope takes one connection " \
                "descriptor"
        report = run_attached(args.connections, args.evidence,
                              args.scope, args.fixture,
                              host_address=args.host_address)
    else:
        report = run_launch([str(args.launch)], args.evidence,
                            args.scope, args.players, args.fixture,
                            fault_injection=
                            not args.no_fault_injection,
                            startup_timeout=args.startup_timeout,
                            shutdown_timeout=args.shutdown_timeout)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
