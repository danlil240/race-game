"""Contract tests for PhysicsSoak.py: the LAN physical observer's
host/client split, per-race epoch bookkeeping, the compact-wire
exclusion, and the physics-aware trace replay — all on synthetic state,
no live endpoints."""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from NetworkSoak import soak_inputs
from PhysicsScenario import PhysicsContractError
from PhysicsSoak import (HISTORY_BASELINE_MARGIN, MAX_HOLD_SECONDS,
                         MAX_HOLD_TICKS, LanPhysicsObserver,
                         PHYSICS_CLASS_EVENTS, run_soak,
                         verify_physics_lan_trace,
                         verify_rematch_baselines)
from TestPhysicsScenario import (diagnostics, physics, substep_path,
                                 wheel)


def racer(pid, backend="physicalAssisted", motor_physics=None,
          fingerprint="a" * 64, session_overrides=None,
          simulation=None, path=None, wire_present=None):
    session = {"eventId": "physics-01",
               "simulationBackend": backend,
               "contentFingerprint": fingerprint,
               "lastRecoveryReason": "",
               "phase": "running", "simulationTick": 20,
               "motor": {"x": 0.0, "y": 0.1, "z": 0.0, "heading": 0.0,
                         "speed": 0.0, "grounded": True,
                         "recovering": False, "tick": 20,
                         "physics": motor_physics}}
    if session_overrides:
        session.update(session_overrides)
    if simulation is not None:
        session["simulation"] = simulation
    if path is not None:
        session["substepPath"] = path
    entry = {"participantId": pid, "slot": 0,
             "vehicleId": "car-physics-01", "attribution": "human",
             "position": 1, "finished": False, "dnf": False,
             "finishTicks": 0, "session": session}
    if wire_present is not None:
        # The compact DTO the wire fold leaves on the decoded racer:
        # `present` is the only authority that may attach motor.physics
        # on a client endpoint.
        entry["physics"] = {"present": wire_present}
    return entry


def lan_state(racers, phase="racing", race_id="lan-race-1",
              events=(), tick=5, prediction=None):
    # The published wire shape: NetworkRaceBridge clears
    # networkRace.events before emission, so race events ride the
    # top-level drivingEvents window — the fixture mirrors that.
    return {"stateSchemaVersion": 4, "tick": tick,
            "captureValid": True, "driving": None,
            "drivingEvents": list(events), "droppedDrivingEvents": 0,
            "network": {"role": "client", "phase": phase,
                        "localParticipantId": "local",
                        "participants": [], "paused": False,
                        "pauseRequests": [], "lastError": "",
                        "pendingOperations": [], "discovered": [],
                        "results": [],
                        "descriptor": {"sessionId": "sess-1"}},
            "networkRace": {"sessionId": "sess-1", "raceId": race_id,
                            "phase": "running", "racers": racers},
            "prediction": prediction, "remotePresentation": None,
            "networkLink": None}


def lan_record(seq, kind, tick, state=None, **fields):
    event = {"type": kind, "protocolVersion": 1, "runId": "run-x",
             "deviceId": "dev-1", "participantId": "local",
             "buildVersion": "1.0", "contentVersion": "physics-1",
             "sequence": seq, "monotonicMs": float(seq * 33),
             "tick": tick, "stateSchemaVersion": 4}
    if state is not None:
        event["state"] = state
    event.update(fields)
    return event


LAN_INFO = {"runId": "run-x", "token": "tok-secret-1"}


def write_lan_trace(directory, records):
    trace = Path(directory) / "state.ndjson"
    with trace.open("w", encoding="utf-8") as out:
        for entry in records:
            out.write(json.dumps(entry) + "\n")
    (Path(directory) / "capture-status.json").write_text(
        json.dumps({"captureValid": True, "captureComplete": True}),
        encoding="utf-8")
    return trace


class LanPhysicsObserverTests(unittest.TestCase):
    def test_host_accepts_full_diagnostics(self):
        observer = LanPhysicsObserver(is_host=True)
        applied = observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics(),
                  simulation=diagnostics(),
                  path=substep_path())]))
        assert applied and observer.diagnostics_seen == 1
        assert observer.fingerprints == {"a" * 64}

    def test_client_accepts_wire_projection_without_diagnostics(self):
        observer = LanPhysicsObserver(is_host=False)
        applied = observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics(), wire_present=True)]))
        assert applied and observer.diagnostics_seen == 0

    def test_client_rejects_physics_without_present_marker(self):
        # Regression: the stripped inline artifact materialized by decode
        # ({attitude:(0,0,0,1), wheels:[]}) survived fold unchecked until
        # the default-instance detector was fixed — a non-null
        # motor.physics whose racer.physics.present is false can never
        # come from the wire on this backend.
        observer = LanPhysicsObserver(is_host=False)
        shell = physics()
        shell["wheels"] = []
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=shell, wire_present=False)]))

    def test_client_rejects_leaked_simulation(self):
        observer = LanPhysicsObserver(is_host=False)
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=physics(),
                      simulation=diagnostics())]))

    def test_client_rejects_leaked_substep_path(self):
        observer = LanPhysicsObserver(is_host=False)
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=physics(),
                      path=substep_path())]))

    def test_fingerprint_disagreement_fails(self):
        observer = LanPhysicsObserver(is_host=True)
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=physics()),
                racer("p1", motor_physics=physics(),
                      fingerprint="b" * 64)]))

    def test_epoch_regression_fails_within_race(self):
        observer = LanPhysicsObserver(is_host=True)
        observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics(recoveryEpoch=2))]))
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=physics(recoveryEpoch=1))]))

    def test_nonphysical_racer_cannot_carry_physics(self):
        observer = LanPhysicsObserver(is_host=True)
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", backend="arcade",
                      motor_physics=physics())]))

    def test_recovery_events_harvested_with_epochs(self):
        # Race events ride the top-level drivingEvents window — the
        # bridge clears networkRace.events before publishing, so only
        # this source can ever produce a nonzero recovery ledger.
        observer = LanPhysicsObserver(is_host=True)
        observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics())],
            events=[{"id": "e1", "kind": "race.recovered",
                     "detail": "stuck#3", "participantId": "p0",
                     "tick": 5}]))
        assert observer.recoveries == [("p0", "stuck", 3)]

    def test_recovery_event_without_epoch_is_a_contract_error(self):
        observer = LanPhysicsObserver(is_host=True)
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=physics())],
                events=[{"id": "e1", "kind": "race.recovered",
                         "detail": "manual", "participantId": "p0",
                         "tick": 5}]))

    def test_duplicate_event_never_double_counts_recovery(self):
        # The bounded window re-publishes surviving events on later
        # boundaries — dedupe by id so the ledger counts once.
        observer = LanPhysicsObserver(is_host=True)
        state = lan_state(racers=[
            racer("p0", motor_physics=physics())],
            events=[{"id": "e1", "kind": "race.recovered",
                     "detail": "stuck#3", "participantId": "p0",
                     "tick": 5}])
        observer.apply(state)
        observer.apply(state)
        assert observer.recoveries == [("p0", "stuck", 3)]

    def test_host_records_body_and_tick_baselines(self):
        observer = LanPhysicsObserver(is_host=True)
        observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics(),
                  simulation=diagnostics(ticks=10, bodies=4))]))
        observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics(),
                  simulation=diagnostics(ticks=20, bodies=4))]))
        metrics = observer.metrics()
        assert metrics["bodiesFirst"] == 4
        assert metrics["bodiesTail"] == 4
        assert metrics["ticksFirst"] == 10
        assert metrics["ticksTail"] == 20

    def test_client_records_prediction_history_baseline(self):
        observer = LanPhysicsObserver(is_host=False)
        observer.apply(lan_state(
            racers=[racer("p0", motor_physics=physics(),
                          wire_present=True)],
            prediction={"historyCount": 2}))
        observer.apply(lan_state(
            racers=[racer("p0", motor_physics=physics(),
                          wire_present=True)],
            prediction={"historyCount": 17}))
        metrics = observer.metrics()
        assert metrics["historyFirst"] == 2
        assert metrics["historyTail"] == 17
        assert metrics["historyPeak"] == 17

    def test_reset_rebaselines_epochs_per_race(self):
        observer = LanPhysicsObserver(is_host=True)
        observer.apply(lan_state(racers=[
            racer("p0", motor_physics=physics(recoveryEpoch=4))]))
        observer.reset()
        # A fresh race identity legally restarts epochs at 0.
        observer.apply(lan_state(race_id="lan-race-2", racers=[
            racer("p0", motor_physics=physics(recoveryEpoch=0))]))
        assert observer.epochs == {"p0": 0}

    def test_plain_validator_errors_become_contract_errors(self):
        observer = LanPhysicsObserver(is_host=True)
        broken = physics()
        broken["wheels"] = []
        with self.assertRaises(PhysicsContractError):
            observer.apply(lan_state(racers=[
                racer("p0", motor_physics=broken)]))


class VerifyPhysicsLanTraceTests(unittest.TestCase):
    def _trace(self, racers, is_host):
        """A minimal legal LAN boundary history ending in a physical
        racing boundary: idle -> lobby -> countdown -> racing."""
        idle = lan_state(racers=[], phase="idle", tick=0)
        idle["networkRace"] = None
        idle["network"]["descriptor"] = None
        records = [
            lan_record(1, "snapshot", 0, idle),
            lan_record(2, "delta", 1,
                       changed={"tick": 1,
                                "network": {"phase": "lobby"}},
                       base=1, removed=[]),
            lan_record(3, "delta", 2,
                       changed={"tick": 2,
                                "network": {"phase": "countdown"}},
                       base=2, removed=[]),
            lan_record(4, "delta", 3,
                       changed={"tick": 3,
                                "network": {"phase": "racing"},
                                "networkRace": lan_state(
                                    racers=racers, tick=3)
                                ["networkRace"]},
                       base=3, removed=[]),
            lan_record(5, "finished", 4,
                       captureValid=True),
        ]
        return records

    def test_host_trace_replays_physics_race(self):
        with tempfile.TemporaryDirectory() as directory:
            write_lan_trace(directory, self._trace(racers=[
                racer("p0", motor_physics=physics(),
                      simulation=diagnostics(),
                      path=substep_path())], is_host=True))
            outcome = verify_physics_lan_trace(directory, LAN_INFO,
                                               is_host=True)
            assert outcome["physicsRaces"]
            assert outcome["physicsRaces"][0]["diagnosticsSeen"] >= 1
            assert outcome["physicsRaces"][0]["fingerprints"] \
                == ["a" * 64]

    def test_client_trace_replays_wire_projection(self):
        with tempfile.TemporaryDirectory() as directory:
            write_lan_trace(directory, self._trace(racers=[
                racer("p0", motor_physics=physics(),
                      wire_present=True)],
                is_host=False))
            outcome = verify_physics_lan_trace(directory, LAN_INFO,
                                               is_host=False)
            assert outcome["physicsRaces"][0]["diagnosticsSeen"] == 0

    def test_client_trace_rejects_simulation_leak(self):
        with tempfile.TemporaryDirectory() as directory:
            write_lan_trace(directory, self._trace(racers=[
                racer("p0", motor_physics=physics(),
                      wire_present=True,
                      simulation=diagnostics())], is_host=False))
            with self.assertRaises(PhysicsContractError):
                verify_physics_lan_trace(directory, LAN_INFO,
                                         is_host=False)

    def test_trace_rejects_epoch_regression(self):
        with tempfile.TemporaryDirectory() as directory:
            records = self._trace(racers=[
                racer("p0", motor_physics=physics(recoveryEpoch=2))],
                is_host=True)
            records.insert(4, lan_record(
                5, "delta", 4,
                changed={"tick": 4,
                         "networkRace": {"racers": [
                             racer("p0", motor_physics=physics(
                                 recoveryEpoch=1))]}},
                base=4, removed=[]))
            records[5]["sequence"] = 6
            records[5]["tick"] = 5
            records[5]["monotonicMs"] = float(6 * 33)
            write_lan_trace(directory, records)
            with self.assertRaises(PhysicsContractError):
                verify_physics_lan_trace(directory, LAN_INFO,
                                         is_host=True)


class RematchBaselineTests(unittest.TestCase):
    """World/body/history counters must rebase on every rematch — a
    persistent world or accumulating prediction history is a leak, not
    a warm cache."""

    def _report(self, host_races, client_races):
        return {"host": {"races": host_races},
                "client-0": {"races": client_races}}

    def test_clean_rematches_pass(self):
        violations = verify_rematch_baselines(self._report(
            host_races=[
                {"bodiesFirst": 4, "bodiesTail": 4,
                 "ticksFirst": 1, "ticksTail": 9000},
                {"bodiesFirst": 4, "bodiesTail": 4,
                 "ticksFirst": 3, "ticksTail": 8000},
            ],
            client_races=[
                {"historyFirst": 2, "historyTail": 500,
                 "historyPeak": 600},
                {"historyFirst": 4, "historyTail": 480,
                 "historyPeak": 590},
            ]))
        assert violations == []

    def test_body_growth_across_rematches_fails(self):
        violations = verify_rematch_baselines(self._report(
            host_races=[
                {"bodiesFirst": 4, "bodiesTail": 4,
                 "ticksFirst": 1, "ticksTail": 9000},
                {"bodiesFirst": 5, "bodiesTail": 5,
                 "ticksFirst": 2, "ticksTail": 8000},
            ],
            client_races=[]))
        assert any("roster grew" in v for v in violations)

    def test_world_ticks_not_restarting_fails(self):
        violations = verify_rematch_baselines(self._report(
            host_races=[
                {"bodiesFirst": 4, "bodiesTail": 4,
                 "ticksFirst": 1, "ticksTail": 9000},
                {"bodiesFirst": 4, "bodiesTail": 4,
                 "ticksFirst": 9001, "ticksTail": 12000},
            ],
            client_races=[]))
        assert any("did not restart" in v for v in violations)

    def test_history_accumulating_across_rematches_fails(self):
        violations = verify_rematch_baselines(self._report(
            host_races=[],
            client_races=[
                {"historyFirst": 2, "historyTail": 500,
                 "historyPeak": 600},
                {"historyFirst": 500 + HISTORY_BASELINE_MARGIN,
                 "historyTail": 900, "historyPeak": 900},
            ]))
        assert any("prediction history" in v for v in violations)


class CliContractTests(unittest.TestCase):
    def test_physical_class_events_cover_both_classes(self):
        assert PHYSICS_CLASS_EVENTS["car"] == (
            "physics-01", "car-physics-01")
        assert PHYSICS_CLASS_EVENTS["motorcycle"] == (
            "physics-01", "motorcycle-physics-01")

    def test_hold_bound_keeps_soak_inputs_legal(self):
        # FoundationService.MaxDurationTicks = 150: an 8 s refresh
        # interval maps to 240 ticks and every input.set lands as an
        # invalidParameter rejection — the soak must clamp the hold, so
        # the bound is pinned alongside the shared helper's output.
        assert MAX_HOLD_TICKS == 150
        for role_index in range(4):
            for refresh in range(3):
                args = soak_inputs(role_index, refresh,
                                   min(8.0, MAX_HOLD_SECONDS))
                duration = int(float(args["durationTicks"]))
                assert 1 <= duration <= MAX_HOLD_TICKS

    def test_single_connection_fails_honestly(self):
        with tempfile.TemporaryDirectory() as work:
            run_dir = Path(work) / "run"
            run_dir.mkdir()
            (run_dir / "connection.json").write_text(json.dumps({
                "protocolVersion": 1, "runId": "run-x",
                "token": "tok", "port": 9,
                "stateSchemaVersion": 4}), encoding="utf-8")
            evidence = Path(work) / "evidence"
            report = run_soak([run_dir / "connection.json"],
                              evidence, minutes=1.0)
            assert report["status"] == "failed"
            assert "2-4" in report["error"]
            assert (evidence / "result.json").is_file()


if __name__ == "__main__":
    unittest.main()
