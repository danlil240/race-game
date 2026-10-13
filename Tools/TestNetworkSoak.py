"""Integrity checks for the LAN soak tool's own plumbing — not the game.

These tests prove the soak can catch lies without any player: rematches
that reuse race/result identities, rewards delivered twice or never
settled, results claimed before the results phase, duplicated racers in
an immutable result, malformed per-device health samples, and the
mandated CLI contract (--connections/--minutes/--evidence)."""

import argparse
import tempfile
import unittest
from pathlib import Path

from NetworkSoak import (RaceLedger, build_parser, cycle_class,
                         deliveries_settled, health_sample,
                         should_start_cycle, soak_inputs, summarize_race)


def results_state(result_id="res-1", race_id="race-1",
                  session_id="session-1", phase="results",
                  delivery_states=("persisted",)):
    """A minimal well-formed post-race observation for one endpoint."""
    racers = [
        {"position": index + 1, "slot": index,
         "participantId": "p-%d" % index, "vehicleId": "car-01",
         "attribution": "human", "outcome": "finished",
         "finishTicks": 900 + index, "lapsCompleted": 1,
         "coinsAwarded": 20}
        for index in range(4)]
    return {
        "network": {
            "phase": phase,
            "results": [
                {"resultId": result_id, "raceId": race_id,
                 "participantId": "p-%d" % index,
                 "state": delivery_states[index % len(delivery_states)]}
                for index in range(4)],
        },
        "networkRace": {
            "sessionId": session_id, "raceId": race_id,
            "phase": "finished",
            "result": {"resultId": result_id, "sessionId": session_id,
                       "raceId": race_id, "eventId": "lan-car-01",
                       "finalized": True, "racers": racers},
        },
    }


class CyclePlanningTests(unittest.TestCase):
    def test_cycle_class_alternates_both_catalogs(self):
        self.assertEqual([cycle_class(i) for i in range(4)],
                         ["car", "motorcycle", "car", "motorcycle"])

    def test_cycle_budget_gate(self):
        self.assertTrue(should_start_cycle(150.0))
        self.assertTrue(should_start_cycle(600.0))
        self.assertFalse(should_start_cycle(149.9))
        self.assertFalse(should_start_cycle(0.0))
        self.assertTrue(should_start_cycle(30.0, minimum=30.0))
        self.assertFalse(should_start_cycle(29.9, minimum=30.0))

    def test_soak_inputs_stay_bounded_and_legal(self):
        frame = soak_inputs(0, 0, 8.0)
        self.assertEqual(frame["steering"], 1)
        self.assertTrue(frame["boost"])
        self.assertGreater(frame["durationTicks"], 0)
        self.assertFalse(frame["recover"])
        later = soak_inputs(0, 1, 8.0)
        self.assertEqual(later["steering"], -1)
        other_seat = soak_inputs(1, 0, 8.0)
        self.assertEqual(other_seat["steering"], -1)
        for role in range(4):
            for refresh in range(6):
                sample = soak_inputs(role, refresh, 5.0)
                self.assertIn(sample["steering"], (-1, 1))
                self.assertGreaterEqual(sample["durationTicks"], 30)


class RaceSummaryTests(unittest.TestCase):
    def test_wellformed_result_summarizes(self):
        summary = summarize_race(results_state(), "t")
        self.assertEqual(summary["resultId"], "res-1")
        self.assertEqual(summary["raceId"], "race-1")
        self.assertEqual(summary["racers"], 4)
        self.assertEqual(summary["finishers"], 4)
        self.assertEqual(len(summary["deliveries"]), 4)

    def test_wrong_phase_is_rejected(self):
        for phase in ("lobby", "racing", "countdown", "recovery"):
            with self.subTest(phase=phase):
                with self.assertRaises(AssertionError):
                    summarize_race(results_state(phase=phase), "t")

    def test_unfinished_race_is_rejected(self):
        state = results_state()
        state["networkRace"]["phase"] = "running"
        with self.assertRaises(AssertionError):
            summarize_race(state, "t")
        state["networkRace"] = None
        with self.assertRaises(AssertionError):
            summarize_race(state, "t")

    def test_missing_result_is_rejected(self):
        state = results_state()
        state["networkRace"]["result"] = None
        with self.assertRaises(AssertionError):
            summarize_race(state, "t")

    def test_duplicated_result_racer_is_rejected(self):
        state = results_state()
        state["networkRace"]["result"]["racers"][1] = dict(
            state["networkRace"]["result"]["racers"][0])
        with self.assertRaises(AssertionError):
            summarize_race(state, "t")

    def test_missing_delivery_rows_are_rejected(self):
        state = results_state()
        state["network"]["results"] = []
        with self.assertRaises(AssertionError):
            summarize_race(state, "t")


class RaceLedgerTests(unittest.TestCase):
    def test_distinct_races_accumulate(self):
        ledger = RaceLedger()
        for index in range(3):
            ledger.record(
                summarize_race(
                    results_state(result_id="res-%d" % index,
                                  race_id="race-%d" % index), "t"),
                "cycle-%d" % index)
        self.assertEqual(len(ledger.races), 3)
        self.assertEqual(len(ledger.deliveries), 12)

    def test_reused_race_identity_is_rejected(self):
        ledger = RaceLedger()
        ledger.record(summarize_race(results_state(), "t"), "cycle-0")
        with self.assertRaises(AssertionError):
            ledger.record(
                summarize_race(
                    results_state(result_id="res-2", race_id="race-1"),
                    "t"), "cycle-1")

    def test_duplicated_result_identity_is_rejected(self):
        ledger = RaceLedger()
        ledger.record(summarize_race(results_state(), "t"), "cycle-0")
        with self.assertRaises(AssertionError):
            ledger.record(
                summarize_race(
                    results_state(result_id="res-1", race_id="race-9"),
                    "t"), "cycle-1")

    def test_double_reward_delivery_is_rejected(self):
        ledger = RaceLedger()
        ledger.record(summarize_race(results_state(), "t"), "cycle-0")
        # A second report of the same result (duplicate delivery retry
        # elsewhere) must not add a second reward row.
        state = results_state(result_id="res-1", race_id="race-2")
        with self.assertRaises(AssertionError):
            ledger.record(summarize_race(state, "t"), "cycle-1")

    def test_unsettled_delivery_is_rejected(self):
        ledger = RaceLedger()
        state = results_state(delivery_states=("pending",))
        with self.assertRaises(AssertionError):
            ledger.record(summarize_race(state, "t"), "cycle-0")

    def test_failed_delivery_is_recorded_not_rejected(self):
        # A failed persistence is honest evidence: the ledger accepts it
        # (the soak flags it as an incident) but still counts once-only.
        ledger = RaceLedger()
        state = results_state(delivery_states=("persisted", "failed"))
        ledger.record(summarize_race(state, "t"), "cycle-0")
        self.assertEqual(len(ledger.deliveries), 4)

    def test_deliveries_settled_predicate(self):
        self.assertTrue(deliveries_settled(
            results_state(delivery_states=("persisted", "failed"))))
        self.assertFalse(deliveries_settled(
            results_state(delivery_states=("pending",))))
        self.assertFalse(deliveries_settled(
            results_state(delivery_states=("persisted", "pending"))))
        self.assertFalse(deliveries_settled({"network": {"results": []}}))
        self.assertFalse(deliveries_settled({}))


class HealthSampleTests(unittest.TestCase):
    class StubPeer:
        def __init__(self, state, boundaries=7, backlog=0):
            self.state = state
            self.boundaries = boundaries
            self.max_backlog_steps = backlog

    def test_sample_carries_required_health_fields(self):
        peer = self.StubPeer(
            {"tick": 42, "network": {"phase": "racing"}},
            boundaries=11, backlog=2)
        sample = health_sample(
            "host", peer,
            {"captureValid": True, "outgoingRecords": 12},
            elapsed=61.234, directory=None)
        self.assertEqual(sample["role"], "host")
        self.assertEqual(sample["tick"], 42)
        self.assertEqual(sample["phase"], "racing")
        self.assertTrue(sample["captureValid"])
        self.assertEqual(sample["outgoingRecords"], 12)
        self.assertEqual(sample["boundaries"], 11)
        self.assertEqual(sample["maxBacklogSteps"], 2)
        self.assertEqual(sample["elapsedSeconds"], 61.2)
        self.assertNotIn("traceBytes", sample)

    def test_sample_tolerates_missing_state_and_dir(self):
        peer = self.StubPeer(None)
        sample = health_sample("client-0", peer, {"captureValid": True},
                               elapsed=0.0, directory="/no/such/dir")
        self.assertIsNone(sample["tick"])
        self.assertIsNone(sample["phase"])
        self.assertIsNone(sample["traceBytes"])

    def test_sample_reads_local_trace_size(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "state.ndjson"
            trace.write_text("{}\n{}\n", encoding="utf-8")
            peer = self.StubPeer({"tick": 1, "network": {"phase": "lobby"}})
            sample = health_sample("host", peer, {"captureValid": True},
                                   elapsed=1.0, directory=directory)
            self.assertEqual(sample["traceBytes"], trace.stat().st_size)
            self.assertGreater(sample["traceBytes"], 0)


class CliContractTests(unittest.TestCase):
    def test_mandated_cli_parses(self):
        args = build_parser().parse_args([
            "--connections", "host.json", "a.json", "b.json",
            "--minutes", "30", "--evidence", "ev"])
        self.assertEqual(len(args.connections), 3)
        self.assertEqual(args.minutes, 30.0)
        self.assertEqual(str(args.evidence), "ev")
        self.assertIsNone(args.host_address)

    def test_defaults_match_the_mandated_interface(self):
        args = build_parser().parse_args([
            "--connections", "h.json", "c.json", "--evidence", "ev"])
        self.assertEqual(args.minutes, 30.0)
        self.assertGreater(args.results_timeout, 0)
        self.assertGreater(args.input_interval, 0)
        self.assertGreater(args.sample_seconds, 0)

    def test_missing_connections_or_evidence_fails(self):
        with self.assertRaises(SystemExit):
            build_parser().parse_args(["--evidence", "ev"])
        with self.assertRaises(SystemExit):
            build_parser().parse_args(["--connections", "h.json", "c.json"])


if __name__ == "__main__":
    unittest.main()
