"""Integrity checks for the LAN soak tool's own plumbing — not the game.

These tests prove the soak can catch lies without any player: rematches
that reuse race/result identities, rewards delivered twice or never
settled, results claimed before the results phase, duplicated racers in
an immutable result, failed deliveries that are not permanent
ineligible verdicts, malformed per-device health samples, and the
mandated CLI contract (--connections/--minutes/--evidence)."""

import argparse
import tempfile
import unittest
from pathlib import Path

from NetworkSoak import (HARD_INCIDENTS, RaceLedger, build_parser,
                         cycle_class, deliveries_settled,
                         delivery_verdicts, hard_incident_kinds,
                         health_sample, record_delivery_verdicts,
                         should_start_cycle, soak_inputs, soak_status,
                         summarize_race)


def results_state(result_id="res-1", race_id="race-1",
                  session_id="session-1", phase="results",
                  delivery_states=("persisted",),
                  delivery_codes=None, outcomes=None):
    """A minimal well-formed post-race observation for one endpoint."""
    codes = delivery_codes or ("",) * 4
    racer_outcomes = outcomes or ("finished",) * 4
    racers = [
        {"position": index + 1, "slot": index,
         "participantId": "p-%d" % index, "vehicleId": "car-01",
         "attribution": "human",
         "outcome": racer_outcomes[index % len(racer_outcomes)],
         "finishTicks": 900 + index, "lapsCompleted": 1,
         "coinsAwarded": 20}
        for index in range(4)]
    return {
        "network": {
            "phase": phase,
            "results": [
                {"resultId": result_id, "raceId": race_id,
                 "participantId": "p-%d" % index,
                 "state": delivery_states[index % len(delivery_states)],
                 "code": codes[index % len(codes)]}
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
            ledger.begin_cycle()
            ledger.record(
                summarize_race(
                    results_state(result_id="res-%d" % index,
                                  race_id="race-%d" % index), "t"),
                "cycle-%d" % index)
        self.assertEqual(len(ledger.races), 3)
        self.assertEqual(len(ledger.deliveries), 12)

    def test_peer_views_of_one_race_agree(self):
        # Every surviving endpoint summarizes the SAME race within a
        # cycle: the second record is agreement, not a rematch — the
        # failure this fix removes from the live soak path.
        ledger = RaceLedger()
        ledger.begin_cycle()
        ledger.record(
            summarize_race(results_state(), "host"), "cycle-0 host")
        ledger.record(
            summarize_race(results_state(), "client-0"),
            "cycle-0 client-0")
        self.assertEqual(len(ledger.races), 2)
        self.assertEqual(len(ledger.deliveries), 4)

    def test_peer_identity_disagreement_is_rejected(self):
        # Two different identities inside ONE cycle means the endpoints
        # observed different races — an honest violation, not agreement.
        ledger = RaceLedger()
        ledger.begin_cycle()
        ledger.record(
            summarize_race(results_state(), "host"), "cycle-0 host")
        with self.assertRaises(AssertionError):
            ledger.record(
                summarize_race(
                    results_state(result_id="res-9", race_id="race-9"),
                    "client-0"), "cycle-0 client-0")

    def test_reused_race_identity_is_rejected(self):
        ledger = RaceLedger()
        ledger.begin_cycle()
        ledger.record(summarize_race(results_state(), "t"), "cycle-0")
        ledger.begin_cycle()
        with self.assertRaises(AssertionError):
            ledger.record(
                summarize_race(
                    results_state(result_id="res-2", race_id="race-1"),
                    "t"), "cycle-1")

    def test_duplicated_result_identity_is_rejected(self):
        ledger = RaceLedger()
        ledger.begin_cycle()
        ledger.record(summarize_race(results_state(), "t"), "cycle-0")
        ledger.begin_cycle()
        with self.assertRaises(AssertionError):
            ledger.record(
                summarize_race(
                    results_state(result_id="res-1", race_id="race-9"),
                    "t"), "cycle-1")

    def test_double_reward_delivery_is_rejected(self):
        ledger = RaceLedger()
        ledger.begin_cycle()
        ledger.record(summarize_race(results_state(), "t"), "cycle-0")
        ledger.begin_cycle()
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

    def test_summary_surfaces_delivery_codes_and_outcomes(self):
        # The verdict check needs the row's code (ResultDeliveryState.code)
        # and the immutable result's per-racer outcome side by side.
        summary = summarize_race(
            results_state(delivery_states=("persisted", "failed"),
                          delivery_codes=("", "ineligible:dnf"),
                          outcomes=("finished", "dnf")),
            "t")
        self.assertEqual(summary["deliveries"]["p-1"], "failed")
        self.assertEqual(summary["deliveryCodes"]["p-1"],
                         "ineligible:dnf")
        self.assertEqual(summary["deliveryCodes"]["p-0"], "")
        self.assertEqual(summary["outcomes"]["p-1"], "dnf")

    def test_deliveries_settled_predicate(self):
        self.assertTrue(deliveries_settled(
            results_state(delivery_states=("persisted", "failed"))))
        self.assertFalse(deliveries_settled(
            results_state(delivery_states=("pending",))))
        self.assertFalse(deliveries_settled(
            results_state(delivery_states=("persisted", "pending"))))
        self.assertFalse(deliveries_settled({"network": {"results": []}}))
        self.assertFalse(deliveries_settled({}))


class DeliveryVerdictTests(unittest.TestCase):
    """Failed delivery rows are judged by the NetworkScenario
    settle_results standard: `failed` is a legal outcome only when the
    code is exactly the permanent `ineligible:<outcome>` verdict the
    immutable result itself records. `saveFailed`, an absent code, or a
    verdict contradicting the result must fail the soak run — a
    systematic persistence regression can never yield a passing soak
    with incidents."""

    def test_save_failed_delivery_fails_the_run(self):
        report = {"incidents": []}
        state = results_state(delivery_states=("failed",),
                              delivery_codes=("saveFailed",))
        verdicts = delivery_verdicts(
            {"host": summarize_race(state, "host")})
        self.assertEqual(len(verdicts), 4)
        self.assertTrue(all(not verdict["honest"]
                            for verdict in verdicts))
        dishonest = record_delivery_verdicts(report, verdicts, 0)
        self.assertTrue(dishonest)
        self.assertEqual(len(report["incidents"]), 4)
        for incident in report["incidents"]:
            self.assertEqual(incident["kind"], "deliveryFailed")
            self.assertEqual(incident["code"], "saveFailed")
            self.assertFalse(incident["honest"])
        status, error = soak_status({}, dishonest)
        self.assertEqual(status, "failed")
        self.assertIsNotNone(error)

    def test_ineligible_verdict_passes_with_the_incident_logged(self):
        report = {"incidents": []}
        state = results_state(
            delivery_states=("persisted", "failed"),
            delivery_codes=("", "ineligible:dnf"),
            outcomes=("finished", "dnf"))
        verdicts = delivery_verdicts(
            {"host": summarize_race(state, "host")})
        self.assertEqual(len(verdicts), 2)
        self.assertTrue(all(verdict["honest"] for verdict in verdicts))
        # An honest ineligible verdict is still recorded as an incident —
        # legal outcome, visible evidence — but it must not fail the run.
        dishonest = record_delivery_verdicts(report, verdicts, 0)
        self.assertFalse(dishonest)
        self.assertEqual(len(report["incidents"]), 2)
        for incident in report["incidents"]:
            self.assertEqual(incident["kind"], "deliveryFailed")
            self.assertEqual(incident["code"], "ineligible:dnf")
            self.assertTrue(incident["honest"])
        status, error = soak_status({}, dishonest)
        self.assertEqual(status, "passed")
        self.assertIsNone(error)

    def test_absent_or_mismatched_code_fails_the_run(self):
        # No code at all, and an ineligible verdict the immutable result
        # contradicts (racer finished, not dnf), are both dishonest.
        for codes, outcomes in (
                (("",), ("finished",) * 4),
                (("ineligible:dnf",), ("finished",) * 4)):
            with self.subTest(code=codes[0]):
                state = results_state(delivery_states=("failed",),
                                      delivery_codes=codes,
                                      outcomes=outcomes)
                verdicts = delivery_verdicts(
                    {"host": summarize_race(state, "host")})
                self.assertTrue(all(not verdict["honest"]
                                    for verdict in verdicts))
                status, _ = soak_status(
                    {}, any(not verdict["honest"]
                            for verdict in verdicts))
                self.assertEqual(status, "failed")

    def test_retained_ledger_failures_obey_the_prefix_rule(self):
        # Failed rows retained from an earlier race outlive their result
        # slice — the outcome cross-check no longer applies, only the
        # ineligible-prefix requirement, matching settle_results.
        state = results_state()
        state["network"]["results"].append(
            {"resultId": "res-old", "raceId": "race-old",
             "participantId": "p-0", "state": "failed",
             "code": "saveFailed"})
        verdicts = delivery_verdicts(
            {"host": summarize_race(state, "host")})
        self.assertEqual(len(verdicts), 1)
        self.assertFalse(verdicts[0]["honest"])
        state["network"]["results"][-1]["code"] = "ineligible:dnf"
        verdicts = delivery_verdicts(
            {"host": summarize_race(state, "host")})
        self.assertTrue(verdicts[0]["honest"])

    def test_lost_peers_and_clean_deliveries(self):
        status, error = soak_status({"client-0": object()}, False)
        self.assertEqual(status, "failed")
        self.assertIn("client-0", error)
        status, error = soak_status({}, False)
        self.assertEqual(status, "passed")
        self.assertIsNone(error)


class SoakStatusTests(unittest.TestCase):
    """The top-level verdict fails on ANY hard incident — a cycle
    abort or invariant violation can never leave `status: "passed"`
    behind. Informational records (an honest `ineligible:*`
    deliveryFailed verdict) remain legal outcomes that do not fail
    the run."""

    def test_result_ledger_incident_fails_the_run(self):
        # e.g. a delivery row stuck `pending` past the settle window:
        # the cycle breaks on the incident and the run must fail.
        incidents = [{
            "kind": "resultLedger", "cycle": 0,
            "detail": "cycle-0 client-0: delivery for p-1 never "
                      "settled (pending)"}]
        self.assertEqual(hard_incident_kinds(incidents),
                         {"resultLedger"})
        status, error = soak_status({}, False, incidents)
        self.assertEqual(status, "failed")
        self.assertIsNotNone(error)

    def test_results_timeout_incident_fails_the_run(self):
        incidents = [{
            "kind": "resultsTimeout", "role": "client-0", "cycle": 0,
            "detail": "no results phase within the race window"}]
        status, error = soak_status({}, False, incidents)
        self.assertEqual(status, "failed")
        self.assertIsNotNone(error)

    def test_every_hard_kind_fails_the_run(self):
        for kind in sorted(HARD_INCIDENTS):
            with self.subTest(kind=kind):
                status, error = soak_status(
                    {}, False, [{"kind": kind, "cycle": 0}])
                self.assertEqual(status, "failed")
                self.assertIn(kind, error)

    def test_honest_ineligible_delivery_still_passes(self):
        # An informational-only run: failed rows carrying the exact
        # permanent ineligible verdict are logged as incidents but the
        # verdict stays passed while no peer is lost.
        report = {"incidents": []}
        state = results_state(
            delivery_states=("persisted", "failed"),
            delivery_codes=("", "ineligible:dnf"),
            outcomes=("finished", "dnf"))
        verdicts = delivery_verdicts(
            {"host": summarize_race(state, "host")})
        dishonest = record_delivery_verdicts(report, verdicts, 0)
        self.assertFalse(dishonest)
        self.assertEqual(len(report["incidents"]), 2)
        self.assertEqual(hard_incident_kinds(report["incidents"]),
                         set())
        status, error = soak_status({}, dishonest, report["incidents"])
        self.assertEqual(status, "passed")
        self.assertIsNone(error)

    def test_dishonest_delivery_incident_alone_fails(self):
        # A dishonest deliveryFailed incident is hard even if the
        # caller's flag was missed — the incident list is authoritative.
        incidents = [{"kind": "deliveryFailed", "role": "host",
                      "participant": "p-0", "cycle": 0,
                      "code": "saveFailed", "honest": False}]
        status, _ = soak_status({}, False, incidents)
        self.assertEqual(status, "failed")


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
