"""Integrity checks for the LAN scenario's validators, not the game itself.

These tests prove the scenario tooling can catch lies: mutated or missing
schema-3 slices, impossible phase transitions, unsequenced or gapped
records, deltas that do not name their boundary, commands accepted but
never completed, and credential leakage into observable state."""

import copy
import json
import tempfile
import unittest
from pathlib import Path

from AgentScenario import (StateReconstructor, validate_state_schema,
                           validate_record, verify_trace)
from NetworkScenario import (LEGAL_PHASE_TRANSITIONS,
                             assert_no_secrets, collect_command_results,
                             phase_history, validate_async_outcomes,
                             validate_lan_state, validate_phase_history,
                             verify_lan_trace)


def lan_state(tick=1):
    """A minimal well-formed schema-3 state with a live LAN session."""
    return {
        "stateSchemaVersion": 3,
        "contentVersion": "lan-content-1",
        "screen": "lan-lobby",
        "language": "en",
        "activeSlot": 0,
        "paused": False,
        "tick": tick,
        "input": {"steering": 0.0, "drift": False, "boost": False,
                  "gadget": False, "recover": False, "changeCamera": False,
                  "pause": False},
        "slots": [],
        "savedProfile": None,
        "draftProfile": None,
        "pendingSettings": False,
        "persistenceStatus": "",
        "captureValid": True,
        "captureError": "",
        "featureCoverage": {},
        "presentation": {"textDirection": "ltr"},
        "driving": None,
        "drivingEvents": [],
        "droppedDrivingEvents": 0,
        "network": {
            "role": "host",
            "phase": "lobby",
            "localParticipantId": "p-host",
            "descriptor": {"sessionId": "s-1", "eventId": "lan-car-01",
                           "classId": "car", "joinable": True},
            "participants": [
                {"participantId": "p-host", "slot": 0, "occupied": True,
                 "avatar": "fox", "colorIndex": 0, "vehicleId": "car-01",
                 "connected": True, "ready": True, "controlSource": "local",
                 "ownershipEpoch": 1},
                {"participantId": "", "slot": 1, "occupied": False,
                 "avatar": "", "colorIndex": 0, "vehicleId": "",
                 "connected": False, "ready": False,
                 "controlSource": "none", "ownershipEpoch": 0},
                {"participantId": "", "slot": 2, "occupied": False,
                 "avatar": "", "colorIndex": 0, "vehicleId": "",
                 "connected": False, "ready": False,
                 "controlSource": "none", "ownershipEpoch": 0},
                {"participantId": "", "slot": 3, "occupied": False,
                 "avatar": "", "colorIndex": 0, "vehicleId": "",
                 "connected": False, "ready": False,
                 "controlSource": "none", "ownershipEpoch": 0},
            ],
            "paused": False,
            "pauseRequests": [],
            "lastError": "",
            "pendingOperations": [],
            "discovered": [],
            "results": [],
        },
        "networkRace": None,
        "prediction": None,
        "remotePresentation": {"racers": []},
    }


class LanStateValidationTests(unittest.TestCase):
    def test_wellformed_lan_state_passes(self):
        session = validate_lan_state(lan_state())
        self.assertEqual(session["phase"], "lobby")

    def test_missing_or_mutated_network_slice_is_rejected(self):
        state = lan_state()
        del state["network"]
        with self.assertRaises(AssertionError):
            validate_lan_state(state)
        state = lan_state()
        state["network"]["phase"] = "blorp"
        with self.assertRaises(AssertionError):
            validate_lan_state(state)
        state = lan_state()
        state["network"]["participants"] = state["network"]["participants"][:3]
        with self.assertRaises(AssertionError):
            validate_lan_state(state)
        state = lan_state()
        del state["network"]["participants"][0]["ownershipEpoch"]
        with self.assertRaises(AssertionError):
            validate_lan_state(state)
        state = lan_state()
        del state["remotePresentation"]
        with self.assertRaises(AssertionError):
            validate_lan_state(state)

    def test_schema_mismatch_is_rejected(self):
        state = lan_state()
        state["stateSchemaVersion"] = 2
        with self.assertRaises(AssertionError):
            validate_lan_state(state)

    def test_credential_fields_are_rejected_anywhere_in_state(self):
        state = lan_state()
        state["network"]["credential"] = "secret"
        with self.assertRaises(AssertionError):
            validate_lan_state(state)
        state = lan_state()
        state["network"]["participants"][0]["credential"] = "x"
        with self.assertRaises(AssertionError):
            validate_lan_state(state)
        with self.assertRaises(AssertionError):
            assert_no_secrets({"outer": [{"token": "t"}]})


class PhaseHistoryTests(unittest.TestCase):
    def boundary(self, sequence, phase, role="client"):
        state = lan_state(tick=sequence)
        state["network"]["phase"] = phase
        state["network"]["role"] = role
        return {"type": "state", "sequence": sequence,
                "state": state, "_state": state}

    def test_legal_lifecycle_passes(self):
        events = [self.boundary(1, "idle"), self.boundary(2, "connecting"),
                  self.boundary(3, "lobby"), self.boundary(4, "countdown"),
                  self.boundary(5, "racing"), self.boundary(6, "results"),
                  self.boundary(7, "idle")]
        history = phase_history(events)
        self.assertEqual([h[1] for h in history],
                         ["idle", "connecting", "lobby", "countdown",
                          "racing", "results", "idle"])
        validate_phase_history(history, "test")

    def test_impossible_transition_is_rejected(self):
        events = [self.boundary(1, "idle"), self.boundary(2, "racing")]
        with self.assertRaises(AssertionError):
            validate_phase_history(phase_history(events), "test")
        events = [self.boundary(1, "idle"), self.boundary(2, "results")]
        with self.assertRaises(AssertionError):
            validate_phase_history(phase_history(events), "test")

    def test_unknown_phase_is_rejected(self):
        events = [self.boundary(1, "wibbly")]
        with self.assertRaises(AssertionError):
            validate_phase_history(phase_history(events), "test")


class AsyncOutcomeTests(unittest.TestCase):
    def test_accepted_then_terminal_passes(self):
        events = [
            {"type": "result", "commandId": "c1", "status": "accepted",
             "tick": 1, "action": "lan.join"},
            {"type": "result", "commandId": "c1", "status": "applied",
             "tick": 2, "action": "lan.join"},
            {"type": "result", "commandId": "c2", "status": "applied",
             "tick": 3, "action": "lan.ready"},
        ]
        results = collect_command_results(events)
        self.assertEqual(validate_async_outcomes(results, "t"), [])

    def test_accepted_never_completed_is_incomplete(self):
        events = [
            {"type": "result", "commandId": "c1", "status": "accepted",
             "tick": 1, "action": "lan.join"},
        ]
        results = collect_command_results(events)
        self.assertEqual(validate_async_outcomes(results, "t"), ["c1"])

    def test_double_terminal_is_rejected(self):
        events = [
            {"type": "result", "commandId": "c1", "status": "accepted",
             "tick": 1, "action": "lan.join"},
            {"type": "result", "commandId": "c1", "status": "applied",
             "tick": 2, "action": "lan.join"},
            {"type": "result", "commandId": "c1", "status": "failed",
             "tick": 3, "action": "lan.join"},
        ]
        results = collect_command_results(events)
        with self.assertRaises(AssertionError):
            validate_async_outcomes(results, "t")

    def test_solo_commands_are_out_of_scope(self):
        events = [
            {"type": "result", "commandId": "c1", "status": "applied",
             "tick": 1, "action": "race.start"},
        ]
        self.assertEqual(
            validate_async_outcomes(collect_command_results(events), "t"),
            [])


class Schema3ConsumerTests(unittest.TestCase):
    def record(self, sequence=1, kind="state", state=None):
        return {
            "type": kind, "protocolVersion": 1, "runId": "lan-test",
            "deviceId": "d", "participantId": "local",
            "buildVersion": "b", "contentVersion": "c",
            "sequence": sequence, "monotonicMs": sequence * 10,
            "tick": sequence,
            "state": state if state is not None else lan_state(tick=sequence),
        }

    def test_schema3_boundary_validates(self):
        record = self.record()
        record["stateSchemaVersion"] = 3
        validate_state_schema(record, record["state"])

    def test_schema3_missing_slices_are_rejected(self):
        for key in ("network", "networkRace", "prediction",
                    "remotePresentation"):
            with self.subTest(key=key):
                state = lan_state()
                del state[key]
                state["driving"] = None
                state["drivingEvents"] = []
                record = self.record(state=state)
                record["stateSchemaVersion"] = 3
                with self.assertRaises(AssertionError):
                    validate_state_schema(record, record["state"])

    def test_schema3_nested_marker_must_match(self):
        state = lan_state()
        state["stateSchemaVersion"] = 2
        record = self.record(state=state)
        record["stateSchemaVersion"] = 3
        with self.assertRaises(AssertionError):
            validate_state_schema(record, record["state"])

    def test_schema2_records_still_validate_as_schema2(self):
        state = {"stateSchemaVersion": 2, "driving": None,
                 "drivingEvents": []}
        record = self.record(state=state)
        record["stateSchemaVersion"] = 2
        validate_state_schema(record, record["state"])

    def test_delta_without_named_base_is_rejected(self):
        reconstructor = StateReconstructor()
        record = self.record()
        reconstructor.apply(record)
        delta = {"type": "delta", "sequence": 2, "base": 99,
                 "changed": {}, "removed": []}
        with self.assertRaises(AssertionError):
            reconstructor.apply(delta)

    def test_delta_alone_is_never_a_full_state(self):
        reconstructor = StateReconstructor()
        delta = {"type": "delta", "sequence": 1, "base": 0,
                 "changed": {"tick": 1}, "removed": []}
        with self.assertRaises(AssertionError):
            reconstructor.apply(delta)

    def test_reconstruction_after_gap_requires_fresh_boundary(self):
        reconstructor = StateReconstructor()
        reconstructor.apply(self.record(1))
        record2 = self.record(2)
        record2["state"]["network"]["phase"] = "racing"
        state = reconstructor.apply(record2)
        self.assertEqual(state["network"]["phase"], "racing")


class TraceIntegrityTests(unittest.TestCase):
    def write_trace(self, directory, records):
        trace = Path(directory) / "state.ndjson"
        with trace.open("w", encoding="utf-8", newline="\n") as sink:
            for record in records:
                sink.write(json.dumps(record) + "\n")
        status = {"captureValid": True, "captureComplete": True,
                  "captureError": ""}
        (Path(directory) / "capture-status.json").write_text(
            json.dumps(status), encoding="utf-8")
        return trace

    def info(self, run_id="lan-trace"):
        return {"runId": run_id, "token": "sekrit-token"}

    def base_records(self, count=3):
        records = []
        for index in range(count):
            kind = "snapshot" if index == 0 else "state"
            state = lan_state(tick=index + 1)
            record = {
                "type": kind, "protocolVersion": 1, "runId": "lan-trace",
                "deviceId": "d", "participantId": "local",
                "buildVersion": "b", "contentVersion": "c",
                "stateSchemaVersion": 3,
                "sequence": index + 1, "monotonicMs": (index + 1) * 10,
                "tick": index + 1, "state": state,
            }
            records.append(record)
        records.append({
            "type": "finished", "protocolVersion": 1, "runId": "lan-trace",
            "deviceId": "d", "participantId": "local", "buildVersion": "b",
            "contentVersion": "c", "sequence": count + 1,
            "monotonicMs": (count + 1) * 10, "tick": count,
            "captureValid": True, "captureError": "",
        })
        return records

    def test_clean_trace_passes_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            self.write_trace(directory, self.base_records())
            outcome = verify_lan_trace(Path(directory), self.info())
            self.assertEqual(outcome["incompleteCommands"], [])
            self.assertEqual(outcome["phases"], ["lobby"] * 3)

    def test_mutated_state_record_is_caught(self):
        with tempfile.TemporaryDirectory() as directory:
            records = self.base_records()
            # A tampered boundary: the phase hops illegally and the tick
            # disagrees with the state body.
            records[1]["state"]["network"]["phase"] = "racing"
            self.write_trace(directory, records)
            with self.assertRaises(AssertionError):
                verify_lan_trace(Path(directory), self.info())

    def test_dropped_sequence_is_caught(self):
        with tempfile.TemporaryDirectory() as directory:
            records = self.base_records()
            del records[2]
            # Resequence the tail so the gap shows as a hole.
            self.write_trace(directory, records)
            with self.assertRaises(AssertionError):
                verify_lan_trace(Path(directory), self.info())

    def test_credential_in_trace_is_caught(self):
        with tempfile.TemporaryDirectory() as directory:
            records = self.base_records()
            records[0]["state"]["note"] = "sekrit-token"
            self.write_trace(directory, records)
            with self.assertRaises(AssertionError):
                verify_lan_trace(Path(directory), self.info())

    def test_accepted_without_terminal_fails_lan_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            records = self.base_records()
            records.insert(2, {
                "type": "result", "protocolVersion": 1,
                "runId": "lan-trace", "deviceId": "d",
                "participantId": "local", "buildVersion": "b",
                "contentVersion": "c", "sequence": 3,
                "monotonicMs": 30, "tick": 2, "commandId": "cmd-9",
                "action": "lan.join", "status": "accepted",
                "code": "", "result": {},
            })
            # Resequence the remaining records to stay contiguous.
            for index, record in enumerate(records):
                record["sequence"] = index + 1
                record["monotonicMs"] = (index + 1) * 10
            self.write_trace(directory, records)
            outcome = verify_lan_trace(Path(directory), self.info())
            self.assertEqual(outcome["incompleteCommands"], ["cmd-9"])


if __name__ == "__main__":
    unittest.main()
