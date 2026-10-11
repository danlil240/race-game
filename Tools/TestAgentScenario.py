"""Tests the scenario's evidence-integrity checks, not the game itself."""

import json
import tempfile
import unittest
from pathlib import Path

from AgentScenario import validate_record, validate_state_tick, verify_trace, validate_presentation


class IntegrityTests(unittest.TestCase):
    def test_bilingual_evidence_rejects_missing_font_and_wrong_direction(self):
        state = {"language": "he", "presentation": {
            "hebrewFontAvailable": True, "fontWarning": "", "textDirection": "rtl",
        }}
        validate_presentation(state, "he")
        for key, value in (("hebrewFontAvailable", False), ("fontWarning", "missing"),
                           ("textDirection", "ltr")):
            invalid = {"language": "he", "presentation": dict(state["presentation"])}
            invalid["presentation"][key] = value
            with self.subTest(key=key), self.assertRaises(AssertionError):
                validate_presentation(invalid, "he")
        with self.assertRaises(AssertionError):
            validate_presentation(state, "en")

    def record(self, sequence=1, kind="state"):
        return {
            "type": kind, "protocolVersion": 1, "runId": "test-run",
            "deviceId": "test-device", "participantId": "local",
            "buildVersion": "foundation-0.1", "contentVersion": "foundation-0.1",
            "sequence": sequence, "monotonicMs": sequence * 10, "tick": sequence,
            "state": {"captureValid": True, "captureError": "", "tick": sequence},
        }

    def check(self, record, previous=None, time=None):
        return validate_record(record, {"runId": "test-run"}, previous, time)

    def test_complete_record_advances_sequence(self):
        self.assertEqual(self.check(self.record(2), 1, 10), (2, 20))

    def test_missing_sequence_is_not_a_passing_record(self):
        record = self.record()
        del record["sequence"]
        with self.assertRaises(AssertionError):
            self.check(record)

    def test_skipped_and_reordered_records_fail(self):
        for sequence in (1, 3):
            with self.subTest(sequence=sequence), self.assertRaises(AssertionError):
                self.check(self.record(sequence), 1, 10)

    def test_unknown_records_cannot_hide_in_a_trace(self):
        with self.assertRaises(AssertionError):
            self.check(self.record(kind="unknown"))

    def test_missing_tick_states_cannot_hide_behind_contiguous_sequences(self):
        state = self.record(sequence=2)
        state["tick"] = state["state"]["tick"] = 4
        with self.assertRaises(AssertionError):
            validate_state_tick(state, 1)
        state["tick"] = state["state"]["tick"] = 1
        self.assertEqual(validate_state_tick(state, 1), 1)
        state["tick"] = state["state"]["tick"] = 2
        self.assertEqual(validate_state_tick(state, 1), 2)

    def test_invalid_time_and_wrong_run_fail(self):
        for key, value in (("monotonicMs", float("nan")), ("monotonicMs", -1),
                           ("runId", "different"), ("protocolVersion", True),
                           ("deviceId", ""), ("tick", None)):
            record = self.record()
            record[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(AssertionError):
                self.check(record)

    def test_trace_requires_complete_history_and_durable_finish(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            info = {"runId": "test-run", "token": "secret-never-in-stream"}
            snapshot = self.record(1, "snapshot")
            finished = self.record(2, "finished")
            finished["captureValid"] = True
            status = {"captureValid": True, "captureComplete": True}
            trace = directory / "state.ndjson"
            status_path = directory / "capture-status.json"
            trace.write_text(json.dumps(snapshot) + "\n" + json.dumps(finished) + "\n",
                             encoding="utf-8")
            status_path.write_text(json.dumps(status), encoding="utf-8")
            verify_trace(directory, info)
            status["captureComplete"] = False
            status_path.write_text(json.dumps(status), encoding="utf-8")
            with self.assertRaises(AssertionError):
                verify_trace(directory, info)
            status["captureComplete"] = True
            status_path.write_text(json.dumps(status), encoding="utf-8")
            finished["sequence"] = 3
            trace.write_text(json.dumps(snapshot) + "\n" + json.dumps(finished) + "\n",
                             encoding="utf-8")
            with self.assertRaises(AssertionError):
                verify_trace(directory, info)


if __name__ == "__main__":
    unittest.main()
