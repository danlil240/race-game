"""Tests the scenario's evidence-integrity checks, not the game itself."""

import json
import tempfile
import unittest
from pathlib import Path

from AgentScenario import (validate_record, validate_state_schema,
                           validate_state_tick, verify_trace,
                           validate_presentation)


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

    def test_schema_2_states_need_the_driving_slice(self):
        record = self.record()
        record["stateSchemaVersion"] = 2
        record["state"]["driving"] = None
        record["state"]["drivingEvents"] = []
        record["state"]["stateSchemaVersion"] = 2
        validate_state_schema(record, record["state"])
        live = self.record()
        live["stateSchemaVersion"] = 2
        live["state"]["driving"] = {
            "raceId": "r1", "vehicleId": "car-01", "eventId": "intro-01",
            "courseId": "graybox-01", "phase": "running", "simulationTick": 5,
            "motor": {"x": 0.0, "y": 0.0, "z": 3.0, "heading": 0.5,
                      "speed": 6.0, "grounded": True},
        }
        live["state"]["drivingEvents"] = []
        live["state"]["stateSchemaVersion"] = 2
        validate_state_schema(live, live["state"])

    def test_schema_2_states_without_driving_are_rejected(self):
        record = self.record()
        record["stateSchemaVersion"] = 2
        record["state"]["stateSchemaVersion"] = 2
        with self.assertRaises(AssertionError):
            validate_state_schema(record, record["state"])

    def test_unsupported_schemas_are_rejected_before_driving_evidence(self):
        for schema in (0, 6, "2", True):
            record = self.record()
            record["stateSchemaVersion"] = schema
            with self.subTest(schema=schema), self.assertRaises(AssertionError):
                validate_state_schema(record, record["state"])

    def schema5_state(self):
        return {
            "stateSchemaVersion": 5,
            "driving": None,
            "drivingEvents": [],
            "network": {
                "role": "host", "phase": "lobby", "localParticipantId": "p",
                "participants": [], "paused": False, "pauseRequests": [],
                "lastError": "", "pendingOperations": [],
                "discovered": [], "results": [],
            },
            "networkRace": None,
            "prediction": None,
            "remotePresentation": {"racers": []},
            "networkLink": None,
            "presentation": {
                "textDirection": "ltr",
                "renderProfileId": "tablet",
                "renderScale": 0.75,
                "targetFrameRate": 30,
                "renderingAvailable": True,
                "performance": {
                    "profile": "tablet",
                    "captureValid": True,
                    "frames": 120,
                    "racingSamples": 90,
                    "medianFrameMs": 32.0,
                    "p95FrameMs": 38.0,
                    "maxFrameMs": 41.0,
                    "reportStatus": "none",
                    "reportError": "",
                    "gpuTimingStatus": "unavailable",
                    "processMemoryStatus": "external",
                    "processMemoryMetric": "process.totalPss",
                    "processMemorySource": "adb dumpsys meminfo",
                    "processMemoryUnits": "bytes",
                },
            },
        }

    def test_schema_5_boundary_includes_presentation_slice(self):
        record = self.record()
        record["stateSchemaVersion"] = 5
        record["state"] = self.schema5_state()
        validate_state_schema(record, record["state"])
        # A disabled capture publishes the performance key as null.
        record["state"]["presentation"]["performance"] = None
        validate_state_schema(record, record["state"])

    def test_schema_5_rejects_missing_or_forged_presentation(self):
        record = self.record()
        record["stateSchemaVersion"] = 5
        record["state"] = self.schema5_state()
        for mutate in (
            lambda s: s.pop("presentation"),
            lambda s: s["presentation"].pop("renderProfileId"),
            lambda s: s["presentation"].pop("performance"),
            lambda s: s["presentation"].update(renderProfileId="quantum"),
            lambda s: s["presentation"].update(renderingAvailable="yes"),
            lambda s: s["presentation"]["performance"].pop("reportStatus"),
        ):
            state = self.schema5_state()
            mutate(state)
            record["state"] = state
            with self.subTest(mutate=mutate), self.assertRaises(AssertionError):
                validate_state_schema(record, state)

    def test_schema_3_requires_the_network_slices(self):
        record = self.record()
        record["stateSchemaVersion"] = 3
        record["state"]["driving"] = None
        record["state"]["drivingEvents"] = []
        record["state"]["stateSchemaVersion"] = 3
        # No network slice: schema 3 must not silently degrade.
        with self.assertRaises(AssertionError):
            validate_state_schema(record, record["state"])
        state = record["state"]
        state["network"] = {
            "role": "host", "phase": "lobby", "localParticipantId": "p",
            "participants": [], "paused": False, "pauseRequests": [],
            "lastError": "", "pendingOperations": [], "discovered": [],
            "results": [],
        }
        state["networkRace"] = None
        state["prediction"] = None
        state["remotePresentation"] = {"racers": []}
        validate_state_schema(record, state)

    def test_schema_3_rejects_credential_leaks(self):
        record = self.record()
        record["stateSchemaVersion"] = 3
        record["state"].update({
            "driving": None, "drivingEvents": [], "stateSchemaVersion": 3,
            "networkRace": None, "prediction": None,
            "remotePresentation": {"racers": []},
            "network": {
                "role": "client", "phase": "lobby",
                "localParticipantId": "p", "participants": [],
                "paused": False, "pauseRequests": [], "lastError": "",
                "pendingOperations": [], "discovered": [], "results": [],
                "credential": "should-never-appear",
            },
        })
        with self.assertRaises(AssertionError):
            validate_state_schema(record, record["state"])

    def test_schema_1_records_stay_compatible(self):
        record = self.record()
        validate_state_schema(record, record["state"])  # no field: baseline schema
        record["stateSchemaVersion"] = 1
        validate_state_schema(record, record["state"])

    def test_screenshot_is_a_sequenced_record_type(self):
        record = self.record(kind="screenshot")
        record["captureId"] = "drive-1"
        record["file"] = "screenshot-drive-1.png"
        record["status"] = "captured"
        self.assertEqual(self.check(record), (1, 10))
        record = self.record(kind="notAScreenshot")
        with self.assertRaises(AssertionError):
            self.check(record)

    def test_missing_tick_states_cannot_hide_behind_contiguous_sequences(self):
        state = self.record(sequence=2)
        state["tick"] = state["state"]["tick"] = 4
        with self.assertRaises(AssertionError):
            validate_state_tick(state["state"], state["tick"], 1)
        state["tick"] = state["state"]["tick"] = 1
        self.assertEqual(validate_state_tick(state["state"], state["tick"], 1), 1)
        state["tick"] = state["state"]["tick"] = 2
        self.assertEqual(validate_state_tick(state["state"], state["tick"], 1), 2)

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

    def test_trace_bound_comes_from_the_configured_limit(self):
        # A run that configured its own trace ceiling publishes it on
        # connection.json; descriptors without the field stay bound to
        # the shared 256 MiB default.
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            info = {"runId": "test-run", "token": "secret-never-in-stream"}
            snapshot = self.record(1, "snapshot")
            finished = self.record(2, "finished")
            finished["captureValid"] = True
            trace = directory / "state.ndjson"
            status_path = directory / "capture-status.json"
            trace.write_text(json.dumps(snapshot) + "\n" + json.dumps(finished) + "\n",
                             encoding="utf-8")
            status_path.write_text(json.dumps(
                {"captureValid": True, "captureComplete": True}),
                encoding="utf-8")

            # The small trace fits the shared default…
            verify_trace(directory, info)
            # …but a descriptor carrying a smaller configured bound must
            # trip exactly that bound.
            with self.assertRaises(AssertionError):
                verify_trace(directory, {"runId": "test-run",
                                         "token": "secret-never-in-stream",
                                         "traceLimitBytes": 10})


if __name__ == "__main__":
    unittest.main()
