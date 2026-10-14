"""Integrity checks for the M4 visual-benchmark scenario's own plumbing.

These tests prove the qualification verdict can catch lies without any
player: corrupted evidence dimensions — missing rendered samples, an
incomplete stream, paused time counted as racing, an unrepresented
class/camera combination, dishonest loopback/AI-fill labelling and
mismatched build manifests — each force a failing verdict. A valid
fixture passes so the checks discriminate.
"""

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from VisualBenchmarkScenario import (PERF_COMBO_FLOOR_SECONDS,
                                     PERF_RACING_FLOOR_SECONDS,
                                     PERF_WARMUP_MS, GenerationSamplers,
                                     PerfAccumulator,
                                     build_parser, check_build_manifest,
                                     check_labels,
                                     check_memory_generations,
                                     check_perf_evidence,
                                     check_rendered_samples, check_shots,
                                     check_streams, evaluate_verdict,
                                     transport_label,
                                     validate_perf_report_file,
                                     vehicle_class)


def _racing_state(vehicle="car-01", camera="chase", participant="p-0"):
    """A reconstructed boundary while this endpoint races a LAN event."""
    return {
        "paused": False,
        "network": {"phase": "racing", "paused": False,
                    "localParticipantId": participant},
        "networkRace": {"phase": "running",
                        "racers": [{"participantId": participant,
                                    "vehicleId": vehicle,
                                    "session": {}}]},
        "driving": None,
        "presentation": {"camera": {"mode": camera}},
    }


def _paused_state(vehicle="car-01", camera="chase", participant="p-0"):
    state = _racing_state(vehicle, camera, participant)
    state["network"]["paused"] = True
    return state


def _lobby_state():
    return {"paused": False,
            "network": {"phase": "lobby", "paused": False,
                        "localParticipantId": "p-0"},
            "networkRace": None, "driving": None,
            "presentation": {"camera": {"mode": "none"}}}


def _good_endpoint_perf(seconds=None, samples=54000, rendering=True):
    """One endpoint's measured performance evidence: every class/camera
    combination covered for at least the five-minute floor."""
    combos = {"car/chase": 450.0, "car/overhead": 450.0,
              "motorcycle/chase": 450.0, "motorcycle/overhead": 450.0}
    if seconds:
        combos = {key: seconds / 4.0 for key in combos}
    return {
        "racingSeconds": sum(combos.values()),
        "combos": combos,
        "excluded": {"warmup": 60.0, "lobby": 30.0, "results": 20.0,
                     "paused": 5.0, "countdown": 12.0},
        "racingSamples": samples,
        "renderingAvailable": rendering,
    }


def _good_parts():
    """A minimal verdict fixture in which every dimension is honest."""
    endpoints = [
        {"role": "host", "host": "127.0.0.1", "port": 5101,
         "buildVersion": "0.1.0", "contentVersion": "visual-1",
         "protocolVersion": 1, "stateSchemaVersion": 5},
        {"role": "client-0", "host": "127.0.0.1", "port": 5102,
         "buildVersion": "0.1.0", "contentVersion": "visual-1",
         "protocolVersion": 1, "stateSchemaVersion": 5},
    ]
    return {
        "endpoints": endpoints,
        "streams": {
            "host": {"verified": True, "captureComplete": True},
            "client-0": {"verified": True, "captureComplete": True},
        },
        "perf": {
            "floorSeconds": PERF_RACING_FLOOR_SECONDS,
            "comboFloorSeconds": PERF_COMBO_FLOOR_SECONDS,
            "perEndpoint": {
                "host": _good_endpoint_perf(),
                "client-0": _good_endpoint_perf(),
            },
            # Per-process-generation memory accounting: the live
            # generations each hold valid samples; the dead generation
            # honestly ends all-unavailable.
            "memoryGenerations": [
                {"role": "host", "generation": 1, "pid": 111,
                 "alive": True, "okSamples": 4,
                 "unavailableSamples": 1},
                {"role": "client-0", "generation": 1, "pid": 222,
                 "alive": False, "okSamples": 3,
                 "unavailableSamples": 2},
                {"role": "client-0", "generation": 2, "pid": 333,
                 "alive": True, "okSamples": 5,
                 "unavailableSamples": 0},
            ],
        },
        "shots": [
            {"peer": "host", "captureId": "vb-host-start",
             "status": "captured", "file": "screenshot-vb-host-start.png",
             "fileBytes": 51234},
            {"peer": "client-0", "captureId": "vb-client-0-start",
             "status": "captured",
             "file": "screenshot-vb-client-0-start.png",
             "fileBytes": 49876},
        ],
        "labels": {
            "transport": "loopback", "loopbackEndpoints": 2,
            "adbForwarded": False, "humanRacers": 2, "aiFilledSeats": 2,
            "aiFillNoted": True, "loopbackNoted": True,
        },
    }


class PerfAccumulatorTests(unittest.TestCase):
    """The stream-observed active-racing clock: only real rendered racing
    seconds count; warm-up, pause, lobby, countdown and results time are
    excluded and reported, never blended into the good aggregate."""

    def _observe(self, acc, start_ms, end_ms, state):
        acc.observe({"monotonicMs": start_ms}, state)
        acc.observe({"monotonicMs": end_ms}, state)

    def test_racing_seconds_accumulate_per_combo(self):
        acc = PerfAccumulator()
        acc.observe({"monotonicMs": PERF_WARMUP_MS + 1000},
                    _racing_state("car-01", "chase"))
        acc.observe({"monotonicMs": PERF_WARMUP_MS + 11000},
                    _racing_state("car-01", "chase"))
        self.assertAlmostEqual(
            acc.racing.get(("car", "chase"), 0.0), 10.0)
        acc.observe({"monotonicMs": PERF_WARMUP_MS + 15000},
                    _racing_state("car-01", "overhead"))
        self.assertAlmostEqual(
            acc.racing.get(("car", "overhead"), 0.0), 4.0)

    def test_DoesNotCountPausedTime(self):
        """Paused boundaries credit the excluded-paused bucket — never
        the racing total the qualification verdict reads."""
        acc = PerfAccumulator()
        base = PERF_WARMUP_MS + 1000
        acc.observe({"monotonicMs": base}, _racing_state())
        # End-state attribution: each interval lands in the bucket of
        # the state observed at its end, so the pause boundary claims
        # its whole interval — paused time never blends into racing.
        acc.observe({"monotonicMs": base + 10000},
                    _racing_state())            # 10 s racing
        acc.observe({"monotonicMs": base + 20000},
                    _paused_state())            # 10 s paused
        acc.observe({"monotonicMs": base + 25000},
                    _racing_state())            # 5 s racing again
        self.assertAlmostEqual(sum(acc.racing.values()), 15.0)
        self.assertAlmostEqual(acc.excluded.get("paused", 0.0), 10.0)

    def test_warmup_and_transitions_are_excluded(self):
        acc = PerfAccumulator()
        acc.observe({"monotonicMs": 1000}, _racing_state())
        acc.observe({"monotonicMs": 11000}, _racing_state())
        self.assertEqual(sum(acc.racing.values()), 0.0)
        self.assertAlmostEqual(acc.excluded.get("warmup", 0.0), 10.0)
        # The interval straddling the 60 s warm-up edge splits: 49 s of
        # warm-up plus 1 s of the lobby state observed at its end.
        acc.observe({"monotonicMs": PERF_WARMUP_MS + 1000}, _lobby_state())
        self.assertAlmostEqual(acc.excluded.get("warmup", 0.0), 59.0)
        acc.observe({"monotonicMs": PERF_WARMUP_MS + 8000}, _lobby_state())
        self.assertAlmostEqual(acc.excluded.get("lobby", 0.0), 8.0)

    def test_vehicle_class_normalisation(self):
        self.assertEqual(vehicle_class("car-01"), "car")
        self.assertEqual(vehicle_class("motorcycle-01"), "motorcycle")
        self.assertEqual(vehicle_class("practice-02"), "practice-02")
        self.assertEqual(vehicle_class(""), "")


class VerdictTests(unittest.TestCase):
    """Corrupt exactly one required evidence dimension per fixture and
    require a failing verdict — a good aggregate can never hide a
    missing dimension."""

    def test_valid_fixture_passes(self):
        self.assertEqual(evaluate_verdict(_good_parts()), [])

    def test_RejectsMissingRenderedSamples(self):
        for corrupt in (
                lambda ep: ep.update(renderingAvailable=False),
                lambda ep: ep.update(racingSamples=0)):
            parts = _good_parts()
            corrupt(parts["perf"]["perEndpoint"]["host"])
            failures = evaluate_verdict(parts)
            self.assertTrue(failures, "missing rendered samples passed")
            self.assertTrue(
                any("rendered" in failure or "racingSamples" in failure
                    for failure in failures), failures)

    def test_RejectsIncompleteStream(self):
        parts = _good_parts()
        parts["streams"]["client-0"] = {
            "verified": False, "reason": "trace ended without finished"}
        failures = evaluate_verdict(parts)
        self.assertTrue(any("client-0" in failure
                            for failure in failures), failures)

    def test_RejectsInvalidatedCapture(self):
        # The live endpoint capture declaring itself invalid (e.g. the
        # recorder's bounded sample store overflowed) fails the verdict —
        # a truncated capture can never pass as complete evidence.
        parts = _good_parts()
        parts["perf"]["perEndpoint"]["host"]["captureValid"] = False
        parts["perf"]["perEndpoint"]["host"]["captureInvalidReason"] = \
            "sampleLimitExceeded"
        failures = evaluate_verdict(parts)
        self.assertTrue(any("host" in failure and "capture" in failure
                            for failure in failures), failures)
        # The quit-time report saying the same thing fails the verdict
        # the same way — honest bounded-pool evidence, still a failure.
        parts = _good_parts()
        parts["perfReports"] = {
            "host": {"status": "invalid",
                     "reason": "capture invalid: sampleLimitExceeded"},
            "client-0": {"status": "validated", "captureValid": True},
        }
        failures = evaluate_verdict(parts)
        self.assertTrue(any("perf report" in failure
                            for failure in failures), failures)
        # A validated report whose capture is invalid fails too.
        parts = _good_parts()
        parts["perfReports"] = {
            "host": {"status": "validated", "captureValid": False,
                     "captureInvalidReason": "sampleLimitExceeded"},
        }
        self.assertTrue(evaluate_verdict(parts))
        # Pending and validated-true reports never fabricate a failure.
        parts = _good_parts()
        parts["perfReports"] = {
            "host": {"status": "pendingPlayerQuit"},
            "client-0": {"status": "validated", "captureValid": True},
        }
        self.assertEqual(evaluate_verdict(parts), [])

    def test_RequiresBothClassesAndCameras(self):
        for missing in ("car/overhead", "motorcycle/chase",
                        "motorcycle/overhead"):
            with self.subTest(missing=missing):
                parts = _good_parts()
                combos = parts["perf"]["perEndpoint"]["client-0"]["combos"]
                combos[missing] = 120.0   # under the five-minute floor
                failures = evaluate_verdict(parts)
                self.assertTrue(
                    any(missing in failure or
                        missing.replace("/", "/") in failure
                        for failure in failures), failures)
        # Raising every other combo cannot hide the missing one.
        parts = _good_parts()
        combos = parts["perf"]["perEndpoint"]["host"]["combos"]
        for key in combos:
            combos[key] = 3000.0
        combos["car/chase"] = 10.0
        parts["perf"]["perEndpoint"]["host"]["racingSeconds"] = \
            sum(combos.values())
        failures = evaluate_verdict(parts)
        self.assertTrue(any("car/chase" in failure
                            for failure in failures), failures)

    def test_LabelsLoopbackAndAiFillHonestly(self):
        # A loopback run labelled as LAN evidence must fail.
        parts = _good_parts()
        parts["labels"]["transport"] = "lan"
        self.assertTrue(evaluate_verdict(parts))
        # AI fill hidden (four racers claimed as human).
        parts = _good_parts()
        parts["labels"]["humanRacers"] = 4
        parts["labels"]["aiFilledSeats"] = 0
        parts["labels"]["aiFillNoted"] = False
        failures = evaluate_verdict(parts)
        self.assertTrue(any("AI" in failure or "ai" in failure
                            for failure in failures), failures)
        # The AI-fill note dropped while seats are AI-filled.
        parts = _good_parts()
        parts["labels"]["aiFillNoted"] = False
        self.assertTrue(evaluate_verdict(parts))
        # An all-human four-endpoint run honestly labels no fill.
        parts = _good_parts()
        parts["endpoints"] += [
            {"role": "client-%d" % i, "host": "192.168.1.%d" % (10 + i),
             "port": 6000 + i, "buildVersion": "0.1.0",
             "contentVersion": "visual-1", "protocolVersion": 1,
             "stateSchemaVersion": 5}
            for i in (1, 2)]
        parts["streams"]["client-1"] = {
            "verified": True, "captureComplete": True}
        parts["streams"]["client-2"] = {
            "verified": True, "captureComplete": True}
        for name in ("client-1", "client-2"):
            parts["perf"]["perEndpoint"][name] = _good_endpoint_perf()
        parts["labels"] = {
            "transport": "mixed", "loopbackEndpoints": 2,
            "adbForwarded": False, "humanRacers": 4,
            "aiFilledSeats": 0, "aiFillNoted": True,
            "loopbackNoted": True}
        self.assertEqual(evaluate_verdict(parts), [])

    def test_RejectsMismatchedBuildManifest(self):
        for field, value in (("buildVersion", "0.1.0-other"),
                             ("contentVersion", "driving-0.1"),
                             ("stateSchemaVersion", 4)):
            with self.subTest(field=field):
                parts = _good_parts()
                parts["endpoints"][1][field] = value
                failures = evaluate_verdict(parts)
                self.assertTrue(any(field in failure
                                    for failure in failures), failures)

    def test_failed_or_missing_screenshots_are_failures(self):
        for corrupt in (
                lambda shot: shot.update(status="timedOut"),
                lambda shot: shot.update(fileBytes=0),
                lambda shot: shot.update(status="failed")):
            parts = _good_parts()
            corrupt(parts["shots"][0])
            failures = evaluate_verdict(parts)
            self.assertTrue(failures, "a failed screenshot passed")
        parts = _good_parts()
        parts["shots"] = []
        self.assertTrue(evaluate_verdict(parts),
                        "an empty image set passed as landmark evidence")


class CheckHelperTests(unittest.TestCase):
    def test_check_streams_and_rendered_samples_discriminate(self):
        parts = _good_parts()
        self.assertEqual(check_streams(parts["streams"]), [])
        self.assertEqual(
            check_rendered_samples(parts["perf"]["perEndpoint"]), [])
        parts["perf"]["perEndpoint"]["client-0"]["racingSamples"] = 0
        self.assertTrue(
            check_rendered_samples(parts["perf"]["perEndpoint"]))

    def test_check_perf_evidence_floor(self):
        parts = _good_parts()
        self.assertEqual(check_perf_evidence(parts["perf"]), [])
        low = _good_parts()
        ep = low["perf"]["perEndpoint"]["host"]
        for key in ep["combos"]:
            ep["combos"][key] = 300.0
        ep["racingSeconds"] = 1200.0     # every combo met, floor not met
        failures = check_perf_evidence(low["perf"])
        self.assertTrue(any("1800" in failure or "active" in failure
                            for failure in failures), failures)

    def test_transport_and_build_helpers(self):
        parts = _good_parts()
        self.assertEqual(transport_label(parts["endpoints"]), "loopback")
        parts["endpoints"][1]["host"] = "192.168.1.20"
        self.assertEqual(transport_label(parts["endpoints"]), "mixed")
        for entry in parts["endpoints"]:
            entry["host"] = "192.168.1.20"
        self.assertEqual(transport_label(parts["endpoints"]), "lan")
        self.assertEqual(
            check_build_manifest(parts["endpoints"]), [])
        self.assertEqual(
            check_labels(parts["labels"], _good_parts()["endpoints"]), [])


class PerfReportFileTests(unittest.TestCase):
    """The quit-time report validator: a well-formed report yields a
    clean validated entry — file identity, capture verdict and the
    excluded accounting — never a corrupted/merged shape; an invalid
    capture is recorded, not crashed on."""

    def _write_report(self, directory, state):
        path = Path(directory) / "perf-report-test.json"
        path.write_text(json.dumps(
            {"reportVersion": 1, "state": state}), encoding="utf-8")
        return path

    def _state(self, **overrides):
        state = {
            "captureValid": True,
            "renderingAvailable": True,
            "evidenceStatus": "collected",
            "racingSamples": 120000,
            "profile": "pc", "buildIdentity": "0.1.0-development",
            "contentVersion": "visual-1",
            "medianFrameMs": 16.7, "p95FrameMs": 16.7,
            "maxFrameMs": 33.0, "stallCount": 0, "droppedFrames": 0,
            "observedSeconds": 2400.0,
            "segments": [
                {"windowIndex": 0, "warmup": True, "phase": "menu",
                 "samples": 60, "seconds": 60.0},
                {"windowIndex": 3, "warmup": False, "phase": "racing",
                 "cameraMode": "chase", "vehicleClass": "car-01",
                 "samples": 90000, "seconds": 1500.0},
                {"windowIndex": 8, "warmup": False, "phase": "results",
                 "cameraMode": "overhead", "vehicleClass": "car-01",
                 "samples": 15000, "seconds": 250.0},
            ],
        }
        state.update(overrides)
        return state

    def test_valid_report_entry_shape(self):
        with tempfile.TemporaryDirectory() as tmp:
            entry = validate_perf_report_file(
                self._write_report(tmp, self._state()))
        self.assertEqual(entry["status"], "validated")
        self.assertEqual(entry["captureValid"], True)
        self.assertEqual(entry["file"], "perf-report-test.json")
        self.assertTrue(entry["sha256"])
        self.assertEqual(entry["racingSamples"], 120000)
        self.assertEqual(entry["racingSecondsExcludingWarmup"], 1500.0)
        # The excluded accounting survives — and no segment fields
        # leak into the summary entry.
        self.assertEqual(entry["excludedSeconds"]["warmup"], 60.0)
        self.assertEqual(entry["excludedSeconds"]["results"], 250.0)
        for leaked in ("windowIndex", "samples", "seconds",
                       "cameraMode", "vehicleClass"):
            self.assertNotIn(leaked, entry)

    def test_invalid_capture_recorded_not_raised(self):
        with tempfile.TemporaryDirectory() as tmp:
            entry = validate_perf_report_file(
                self._write_report(
                    tmp, self._state(
                        captureValid=False,
                        captureInvalidReason="sampleLimitExceeded",
                        droppedFrames=40000)))
        self.assertEqual(entry["status"], "invalid")
        self.assertIn("sampleLimitExceeded", entry["reason"])
        self.assertEqual(entry["captureValid"], False)


class _FakeSampler:
    """Scripted MemorySampler stand-in: writes one memory.ndjson line
    per scripted status so GenerationSamplers.account() reads real
    per-generation evidence files."""

    def __init__(self, evidence_dir, statuses, pid=None, interval=1.0):
        self.path = Path(evidence_dir) / "memory.ndjson"
        self.statuses = list(statuses)
        self.pid = pid
        self.interval = interval
        self.started = False

    def start(self):
        self.started = True
        with self.path.open("x", encoding="utf-8") as sink:
            for sequence, status in enumerate(self.statuses, 1):
                sink.write(json.dumps(
                    {"type": "memory", "sequence": sequence,
                     "status": status, "units": "bytes"}) + "\n")
        return self

    def stop(self, timeout=5.0):
        pass


class GenerationSamplersTests(unittest.TestCase):
    """Every process generation is its own measurement target: its own
    pid, process-start identity, evidence interval and sampler. A
    relaunch must create a new sampler through start(); a recycled pid
    is rejected; a dead generation's file honestly reads all
    unavailable; and a live generation with zero valid samples fails
    the verdict."""

    def test_each_generation_gets_own_sampler_and_interval(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            made = []

            def factory(evidence_dir, pid=None, interval=1.0):
                sampler = _FakeSampler(evidence_dir, ["ok"],
                                       pid=pid, interval=interval)
                made.append(sampler)
                return sampler

            samplers = GenerationSamplers(
                sampler_factory=factory, stamp_of=lambda pid: pid * 10)
            first = samplers.start("client-0", 1, 111, root / "g1")
            second = samplers.start("client-0", 2, 222, root / "g2")

            self.assertEqual(len(made), 2)
            self.assertNotEqual(first["memory"], second["memory"])
            self.assertEqual(first["processStartStamp"], 1110)
            self.assertEqual(second["processStartStamp"], 2220)
            self.assertEqual(first["pid"], 111)
            self.assertEqual(second["pid"], 222)
            self.assertTrue(all(made_i.started for made_i in made))

    def test_pid_reuse_across_generations_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            samplers = GenerationSamplers(
                sampler_factory=lambda d, pid=None, interval=1.0:
                    _FakeSampler(d, ["ok"], pid=pid),
                stamp_of=lambda pid: pid)
            samplers.start("client-0", 1, 111, root / "g1")
            with self.assertRaises(AssertionError):
                # Same pid number, different start stamp: recycled.
                samplers.start("client-0", 2, 111, root / "g2")
            # The start stamp does not rescue it: the same pid on a new
            # generation means the old process never died, which is
            # itself a fault-injection defect.
            samplers2 = GenerationSamplers(
                sampler_factory=lambda d, pid=None, interval=1.0:
                    _FakeSampler(d, ["ok"], pid=pid),
                stamp_of=lambda pid: pid)
            samplers2.start("client-0", 1, 111, root / "g3")
            with self.assertRaises(AssertionError):
                samplers2.start("client-0", 2, 111, root / "g4")

    def test_dead_generation_unavailable_live_generation_samples(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            statuses = [["unavailable", "unavailable"], ["ok", "ok"]]
            made = []

            def factory(evidence_dir, pid=None, interval=1.0):
                sampler = _FakeSampler(
                    evidence_dir, statuses[len(made)],
                    pid=pid, interval=interval)
                made.append(sampler)
                return sampler

            samplers = GenerationSamplers(
                sampler_factory=factory, stamp_of=lambda pid: pid * 10)
            samplers.start("client-0", 1, 111, root / "g1")
            samplers.end_generation("client-0", 1)
            samplers.start("client-0", 2, 222, root / "g2")
            samplers.stop_all()

            account = samplers.account()
            self.assertEqual(len(account), 2)
            dead = account[0]
            live = account[1]
            self.assertFalse(dead["alive"])
            self.assertEqual(dead["okSamples"], 0)
            self.assertEqual(dead["unavailableSamples"], 2)
            self.assertTrue(live["alive"])
            self.assertEqual(live["okSamples"], 2)
            self.assertEqual(check_memory_generations(account), [])

    def test_zero_valid_samples_on_live_generation_cannot_pass(self):
        account = [
            {"role": "host", "generation": 1, "pid": 1,
             "alive": True, "okSamples": 3, "unavailableSamples": 0},
            {"role": "client-0", "generation": 1, "pid": 2,
             "alive": False, "okSamples": 0, "unavailableSamples": 4},
            {"role": "client-0", "generation": 2, "pid": 3,
             "alive": True, "okSamples": 0, "unavailableSamples": 3},
        ]
        failures = check_memory_generations(account)
        self.assertTrue(any("no valid memory samples" in failure
                            for failure in failures), failures)
        parts = _good_parts()
        parts["perf"]["memoryGenerations"] = account
        self.assertTrue(evaluate_verdict(parts))

    def test_verdict_accepts_all_unavailable_dead_generation(self):
        parts = _good_parts()
        parts["perf"]["memoryGenerations"][1]["okSamples"] = 0
        parts["perf"]["memoryGenerations"][1]["unavailableSamples"] = 5
        self.assertEqual(evaluate_verdict(parts), [])

    def test_generation_with_no_records_at_all_fails(self):
        account = [
            {"role": "host", "generation": 1, "pid": 1,
             "alive": True, "okSamples": 2, "unavailableSamples": 0},
            {"role": "client-0", "generation": 1, "pid": 2,
             "alive": True, "okSamples": 0, "unavailableSamples": 0},
        ]
        failures = check_memory_generations(account)
        self.assertTrue(any("no samples at all" in failure
                            for failure in failures), failures)


class CliContractTests(unittest.TestCase):
    def test_mandated_cli_parses(self):
        args = build_parser().parse_args([
            "--connections", "host.json", "a.json",
            "--minutes", "30", "--evidence", "ev"])
        self.assertEqual(len(args.connections), 2)
        self.assertEqual(args.minutes, 30.0)
        self.assertEqual(str(args.evidence), "ev")

    def test_connections_and_evidence_are_required(self):
        with self.assertRaises(SystemExit):
            build_parser().parse_args(["--evidence", "ev"])
        with self.assertRaises(SystemExit):
            build_parser().parse_args(["--connections", "a.json", "b.json"])


if __name__ == "__main__":
    unittest.main()
