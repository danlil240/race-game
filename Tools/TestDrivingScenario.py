"""Unit tests for the schema-2 delta reconstructor and DrivingScenario helpers.

These test the independent consumer and scenario tooling, not the game: the
mutation fixtures prove missing/duplicate/reordered/corrupt records cannot
pass, resnapshots restore observation without repairing missing history, and
old full-state fixtures keep working.
"""

import json
import tempfile
import unittest
from pathlib import Path

import DrivingScenario
from AgentScenario import StateReconstructor, verify_trace


RUN = {"runId": "drive-test", "token": "never-leak-this"}


def base_state(tick):
    """A schema-2 full state as emitted inside snapshot/state records."""
    return {
        "stateSchemaVersion": 2, "contentVersion": "driving-0.1",
        "screen": "driving", "language": "en", "activeSlot": 0, "paused": False,
        "tick": tick,
        "input": {"steering": 0.0, "drift": False, "boost": False,
                  "gadget": False, "recover": False, "changeCamera": False,
                  "pause": False},
        "slots": [{"slot": i, "status": "Missing", "profile": None} for i in range(4)],
        "savedProfile": {"id": "p"}, "draftProfile": {"id": "p"},
        "pendingSettings": False, "persistenceStatus": "",
        "captureValid": True, "captureError": "",
        "featureCoverage": {}, "presentation": {"backlogSteps": 0,
                                                "stepOverloads": 0},
        "driving": {
            "raceId": "race-1", "vehicleId": "car-01", "eventId": "intro-01",
            "courseId": "graybox-01", "phase": "running",
            "simulationTick": tick, "elapsedTicks": tick,
            "lap": 1, "lapsRequired": 1, "nextCheckpoint": 1,
            "checkpoints": [], "finished": False, "finishTicks": 0,
            "motor": {"x": 0.0, "y": 0.0, "z": float(tick), "heading": 0.0,
                      "speed": 10.0, "grounded": True, "drifting": False,
                      "boostActive": False, "boostCharge": 1.0,
                      "recovering": False},
            "input": {"steering": 0.0, "drift": False, "boost": False,
                      "gadget": False, "recover": False, "changeCamera": False,
                      "pause": False},
            "pendingActions": [], "camera": {"mode": "chase"},
            "tutorial": None, "controls": [],
        },
        "drivingEvents": [],
        "droppedDrivingEvents": 0,
    }


def record(sequence, kind, tick, **payload):
    event = {
        "type": kind, "protocolVersion": 1, "runId": RUN["runId"],
        "deviceId": "dev", "participantId": "local",
        "buildVersion": "foundation-0.1", "contentVersion": "driving-0.1",
        "sequence": sequence, "monotonicMs": sequence * 10, "tick": tick,
    }
    event.update(payload)
    return event


def snapshot_record(sequence, tick):
    return record(sequence, "snapshot", tick,
                  state=base_state(tick), stateSchemaVersion=2)


def delta_record(sequence, base, tick, changed=None, removed=None, **extra):
    return record(sequence, "delta", tick, stateSchemaVersion=2,
                  base=base, changed=changed or {}, removed=removed or [],
                  **extra)


def finished_record(sequence, tick):
    return record(sequence, "finished", tick, captureValid=True)


def write_trace(directory, events):
    path = Path(directory) / "state.ndjson"
    path.write_text("".join(json.dumps(event) + "\n" for event in events),
                    encoding="utf-8")
    (Path(directory) / "capture-status.json").write_text(json.dumps(
        {"captureValid": True, "captureComplete": True}), encoding="utf-8")
    return path


class ReconstructorTests(unittest.TestCase):
    def test_delta_merges_changed_fields_and_removes_paths(self):
        recon = StateReconstructor()
        recon.apply(snapshot_record(1, 4))
        state = recon.apply(delta_record(
            2, 1, 5, changed={"tick": 5,
                              "driving": {"motor": {"z": 44.5, "speed": 12.0},
                                          "simulationTick": 5},
                              "screen": "driving"},
            removed=["persistenceStatus"]))
        self.assertEqual(state["tick"], 5)
        self.assertEqual(state["driving"]["motor"]["z"], 44.5)
        self.assertEqual(state["driving"]["motor"]["speed"], 12.0)
        self.assertEqual(state["driving"]["simulationTick"], 5)
        # Untouched fields survive the merge.
        self.assertEqual(state["driving"]["vehicleId"], "car-01")
        self.assertEqual(state["driving"]["motor"]["heading"], 0.0)
        self.assertNotIn("persistenceStatus", state)

    def test_delta_null_assignment_and_array_replacement(self):
        recon = StateReconstructor()
        recon.apply(snapshot_record(1, 2))
        events = [{"id": "r1-e1", "tick": 2, "kind": "race.ended",
                   "commandId": "c", "detail": "restarted"},
                  {"id": "r2-e1", "tick": 2, "kind": "race.started",
                   "commandId": "c", "detail": "intro-01"}]
        state = recon.apply(delta_record(
            2, 1, 2, changed={"driving": None, "drivingEvents": events}))
        self.assertIsNone(state["driving"])
        self.assertEqual([e["kind"] for e in state["drivingEvents"]],
                         ["race.ended", "race.started"],
                         "same-tick transient events must stay ordered")

    def test_delta_without_baseline_or_wrong_base_fails(self):
        with self.assertRaises(AssertionError):
            StateReconstructor().apply(delta_record(1, 0, 1))
        recon = StateReconstructor()
        recon.apply(snapshot_record(1, 1))
        for base in (2, 0, "1", None):
            with self.subTest(base=base), self.assertRaises(AssertionError):
                recon.apply(delta_record(2, base, 2))

    def test_malformed_delta_payloads_fail(self):
        recon = StateReconstructor()
        recon.apply(snapshot_record(1, 1))
        bad = delta_record(2, 1, 2)
        bad["changed"] = [1, 2]
        with self.assertRaises(AssertionError):
            recon.apply(bad)
        bad = delta_record(2, 1, 2, removed="tick")
        with self.assertRaises(AssertionError):
            recon.apply(bad)
        bad = delta_record(2, 1, 2, removed=["driving.motor.warp"])
        with self.assertRaises(AssertionError):
            recon.apply(bad)

    def test_resnapshot_restores_observation(self):
        recon = StateReconstructor()
        recon.apply(snapshot_record(1, 1))
        recon.apply(delta_record(2, 1, 2, changed={"tick": 2}))
        fresh = recon.apply(snapshot_record(9, 2))
        self.assertEqual(recon.base_sequence, 9)
        state = recon.apply(delta_record(10, 9, 3, changed={"tick": 3}))
        self.assertEqual(state["tick"], 3)
        self.assertEqual(state["driving"]["vehicleId"], "car-01")

    def test_full_state_records_remain_valid_baselines(self):
        recon = StateReconstructor()
        recon.apply(snapshot_record(1, 1))
        state = recon.apply(record(2, "state", 2, state=base_state(2),
                                   stateSchemaVersion=2, commandId="cmd"))
        self.assertEqual(state["tick"], 2)
        self.assertEqual(recon.base_sequence, 2)
        state = recon.apply(delta_record(3, 2, 3, changed={"tick": 3}))
        self.assertEqual(state["driving"]["raceId"], "race-1")

    def test_returned_states_are_independent_copies(self):
        recon = StateReconstructor()
        first = recon.apply(snapshot_record(1, 1))
        first["driving"]["vehicleId"] = "corrupted"
        first["slots"].clear()
        state = recon.apply(delta_record(2, 1, 2, changed={"tick": 2}))
        self.assertEqual(state["driving"]["vehicleId"], "car-01",
                         "Caller mutation of a returned state corrupts the baseline")
        self.assertEqual(len(state["slots"]), 4)
        second = recon.apply(delta_record(3, 2, 3, changed={"tick": 3}))
        self.assertEqual(state["tick"], 2,
                         "Later deltas must not rewrite earlier returned states")


class TraceFixtureTests(unittest.TestCase):
    def good_trace(self):
        return [
            snapshot_record(1, 1),
            delta_record(2, 1, 2, changed={
                "tick": 2, "driving": {"simulationTick": 2,
                                       "motor": {"z": 9.0}}}),
            record(3, "result", 2, status="applied", code="", action="input.set",
                 duplicate=False, result={"status": "applied"},
                 commandId="cmd-1"),
            delta_record(4, 2, 3, changed={
                "tick": 3, "driving": {"simulationTick": 3},
                "input": {"steering": 0.5}}),
            snapshot_record(5, 3),   # explicit resnapshot restores observation
            delta_record(6, 5, 4, changed={
                "tick": 4, "driving": {"simulationTick": 4}}),
            record(7, "screenshot", 4, captureId="drive-1",
                   file="screenshot-drive-1.png", status="captured"),
            finished_record(8, 4),
        ]

    def test_delta_capture_verifies_end_to_end(self):
        with tempfile.TemporaryDirectory() as temp:
            write_trace(temp, self.good_trace())
            verify_trace(Path(temp), RUN)

    def test_old_full_state_fixtures_still_pass(self):
        with tempfile.TemporaryDirectory() as temp:
            events = [snapshot_record(1, 1)]
            for sequence in range(2, 6):
                events.append(record(
                    sequence, "state", sequence - 1,
                    state=base_state(sequence - 1), stateSchemaVersion=2))
            events.append(finished_record(6, 5))
            write_trace(temp, events)
            verify_trace(Path(temp), RUN)

    def test_missing_duplicate_reordered_and_corrupt_records_fail(self):
        good = self.good_trace()
        cases = {
            "missing delta": good[:2] + good[3:],
            "duplicate record": good[:2] + [good[1]] + [
                dict(event, sequence=i + 3)
                for i, event in enumerate(good[2:])],
            "reordered records": [good[0], good[2], good[1]] + [
                dict(event, sequence=i + 4)
                for i, event in enumerate(good[3:])],
            "corrupt base": [good[0],
                             delta_record(2, 7, 2, changed={"tick": 2})] + [
                dict(event, sequence=i + 3)
                for i, event in enumerate(good[2:])],
            "truncated json": None,
        }
        with tempfile.TemporaryDirectory() as temp:
            for name, events in cases.items():
                with self.subTest(name=name):
                    if events is None:
                        path = write_trace(temp, good)
                        path.write_text(
                            path.read_text().rstrip("\n")[:-40],
                            encoding="utf-8")
                    else:
                        write_trace(temp, events)
                    with self.assertRaises(AssertionError):
                        verify_trace(Path(temp), RUN)

    def test_resnapshot_cannot_repair_missing_history(self):
        good = self.good_trace()
        # Drop the second delta; renumber the resnapshot so sequences stay
        # contiguous-looking is impossible — every honest attempt fails on
        # either the sequence gap or the base linkage.
        broken = [good[0], good[2], good[3], good[4], good[5], good[6], good[7]]
        with tempfile.TemporaryDirectory() as temp:
            write_trace(temp, broken)
            with self.assertRaises(AssertionError):
                verify_trace(Path(temp), RUN)
        # Even renumbering to hide the gap leaves a delta whose base names a
        # boundary that never supplied its state.
        renumbered = []
        for index, event in enumerate(broken, start=1):
            copy = dict(event)
            copy["sequence"] = index
            renumbered.append(copy)
        with tempfile.TemporaryDirectory() as temp:
            write_trace(temp, renumbered)
            with self.assertRaises(AssertionError):
                verify_trace(Path(temp), RUN)


class ScenarioHelperTests(unittest.TestCase):
    def test_steer_command_tracks_targets_and_wraps(self):
        motor = {"x": 0.0, "z": 0.0, "heading": 0.0}
        # Target due north: no correction needed.
        self.assertAlmostEqual(DrivingScenario.steer_command(motor, 0.0, 10.0), 0.0)
        # Target east: turn right (positive steering toward +x).
        self.assertGreater(DrivingScenario.steer_command(motor, 10.0, 0.0), 0.5)
        # Target west: turn left.
        self.assertLess(DrivingScenario.steer_command(motor, -10.0, 0.0), -0.5)
        # Behind: saturates at full lock, never exceeds 1.
        self.assertEqual(DrivingScenario.steer_command(motor, 0.0, -10.0), 1.0)
        # Heading wrap: facing west (-pi/2), target north is a right turn.
        motor["heading"] = -1.5708
        self.assertGreater(DrivingScenario.steer_command(motor, 0.0, 10.0), 0.0)

    def test_capacity_projection_scales_measured_load(self):
        stats = {"wallSeconds": 120.0, "traceBytes": 8 * 1024 * 1024,
                 "commands": 240, "boundaryRecords": 3600}
        projected = DrivingScenario.project_capacity(stats, 1800.0)
        self.assertAlmostEqual(projected["traceBytes"], 120 * 1024 * 1024)
        self.assertAlmostEqual(projected["commandIdentities"], 3600.0)
        self.assertAlmostEqual(projected["boundaryRecords"], 54000.0)
        # A schedule that would exceed the trace cap must be detectable.
        self.assertGreater(projected["traceBytes"], 0)
        over = DrivingScenario.project_capacity(
            {"wallSeconds": 1.0, "traceBytes": 1024 * 1024,
             "commands": 1, "boundaryRecords": 30}, 1800.0)
        self.assertGreater(over["traceBytes"], 256 * 1024 * 1024)

    def test_event_harvest_collects_stable_ids(self):
        seen = {}
        DrivingScenario.harvest_events({"drivingEvents": [
            {"id": "r1-e1", "kind": "race.started"},
            {"id": "r1-e2", "kind": "race.running"}]}, seen)
        DrivingScenario.harvest_events({"drivingEvents": [
            {"id": "r1-e2", "kind": "race.running"},
            {"id": "r1-e3", "kind": "checkpoint.crossed"}]}, seen)
        self.assertEqual([e["id"] for e in seen.values()],
                         ["r1-e1", "r1-e2", "r1-e3"])
        DrivingScenario.harvest_events({"drivingEvents": None}, seen)
        DrivingScenario.harvest_events({}, seen)
        self.assertEqual(len(seen), 3)


if __name__ == "__main__":
    unittest.main()
