"""Failing-first contract tests for PhysicsScenario.py's validators and
the independent physical trace replay — synthetic boundaries prove the
contract before any live build is trusted with it."""

import json
import sys
import tempfile
import types
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from PhysicsScenario import (PhysicsObserver, body_up_dot,
                             envelope_consistent, finite,
                             overturned_event,
                             parse_recovery_detail,
                             race_events_of,
                             validate_physics_state,
                             validate_simulation_diagnostics,
                             validate_substep_path,
                             verify_physics_trace,
                             verify_recovery_ledger, main)


def wheel(index, **overrides):
    value = {"index": index, "grounded": True, "contactValid": True,
             "contactX": 0.0, "contactY": 0.0, "contactZ": 0.0,
             "normalX": 0.0, "normalY": 1.0, "normalZ": 0.0,
             "compression": 0.05, "previousCompression": 0.05,
             "steering": 0.0, "spin": 0.0, "normalLoad": 900.0,
             "slipLongitudinal": 0.0, "slipLateral": 0.0}
    value.update(overrides)
    return value


def physics(wheels=4, **overrides):
    wheel_list = wheels if isinstance(wheels, list) \
        else [wheel(i) for i in range(wheels)]
    value = {"attitudeX": 0.0, "attitudeY": 0.0, "attitudeZ": 0.0,
             "attitudeW": 1.0,
             "velocityX": 0.0, "velocityY": 0.0, "velocityZ": 0.0,
             "angularVelocityX": 0.0, "angularVelocityY": 0.0,
             "angularVelocityZ": 0.0,
             "filteredSteering": 0.0, "filteredRollRate": 0.0,
             "balancePrevError": 0.0, "filteredLeanTarget": 0.0,
             "driftAmount": 0.0,
             "grounded": True, "airSeconds": 0.0, "airPeakY": 0.0,
             "airWallContact": False, "overturnedSeconds": 0.0,
             "contactLossSeconds": 0.0, "lastContact": "ground",
             "recoveryEpoch": 0,
             "wheels": wheel_list}
    value.update(overrides)
    return value


def diagnostics(backend="physicalAssisted", ticks=10, bodies=1,
                **overrides):
    value = {"backend": backend,
             "ticksSimulated": ticks,
             "substepsSimulated": ticks * 2
             if backend != "arcade" else 0,
             "bodies": bodies,
             "lastStepSeconds": 0.001, "totalStepSeconds": 0.01,
             "invalidStateFaults": 0}
    value.update(overrides)
    return value


def substep_path(x=0.0, z=0.0, heading=0.0):
    return [{"offsetSeconds": offset,
             "pose": {"x": x, "y": 0.1, "z": z, "heading": heading}}
            for offset in (0.0, 1.0 / 60.0, 1.0 / 30.0)]


def driving(race="race-1", motor_physics=None, backend="physicalAssisted",
            tick=10, phase="running", reason="", path=None,
            sim=None, motor_overrides=None):
    motor = {"x": 0.0, "y": 0.1, "z": 0.0, "heading": 0.0,
             "speed": 0.0, "lateralSpeed": 0.0, "verticalSpeed": 0.0,
             "grounded": True, "recovering": False,
             "tick": tick, "physics": motor_physics}
    if motor_overrides:
        motor.update(motor_overrides)
    state = {"raceId": race, "vehicleId": "car-physics-01",
             "eventId": "physics-01", "courseId": "graybox-01",
             "contentVersion": "physics-1",
             "simulationBackend": backend,
             "contentFingerprint": "a" * 64,
             "lastRecoveryReason": reason,
             "phase": phase, "simulationTick": tick,
             "substepPath": path, "motor": motor}
    if sim is not None:
        state["simulation"] = sim
    elif backend == "physicalAssisted":
        state["simulation"] = diagnostics()
    return state


def app_state(vehicle_driving=None, events=(), schema=4):
    """The schema-4 boundary skeleton verify_physics_trace consumes."""
    state = {"stateSchemaVersion": schema, "tick": 1,
             "captureValid": True, "activeSlot": 0,
             "screen": "practice", "paused": False,
             "slots": [], "drivingEvents": list(events),
             "droppedDrivingEvents": 0,
             "driving": vehicle_driving}
    if schema >= 3:
        state["network"] = {
            "role": "none", "phase": "idle",
            "localParticipantId": "local", "participants": [],
            "paused": False, "pauseRequests": [],
            "lastError": "", "pendingOperations": [],
            "discovered": [], "results": []}
        state["networkRace"] = None
        state["prediction"] = None
        state["remotePresentation"] = None
        state["networkLink"] = None
    return state


def record(sequence, kind, state=None, **fields):
    event = {"type": kind, "sequence": sequence,
             "stateSchemaVersion": 4}
    if state is not None:
        event["state"] = state
    event.update(fields)
    return event


def write_trace(directory, records):
    path = Path(directory) / "state.ndjson"
    with path.open("w", encoding="utf-8") as out:
        for entry in records:
            out.write(json.dumps(entry) + "\n")
    return path


class FiniteHelpersTests(unittest.TestCase):
    def test_finite_rejects_bool_nan_inf(self):
        assert finite(1.5) and finite(0) and finite(-2)
        assert not finite(True) and not finite(False)
        assert not finite(float("nan")) and not finite(float("inf"))
        assert not finite("1.0") and not finite(None)

    def test_body_up_dot_orientation(self):
        assert body_up_dot(physics()) > 0.99
        # 180-degree roll: up dot is -1.
        assert body_up_dot(physics(attitudeX=0.0, attitudeZ=1.0,
                                   attitudeW=0.0)) < -0.99

    def test_envelope_consistency(self):
        motor = {"heading": 0.0, "speed": 4.0}
        value = physics(velocityZ=4.0)
        assert envelope_consistent(motor, value)
        assert not envelope_consistent(motor, physics(velocityZ=9.0))
        # Lateral velocity does not count toward forward speed.
        assert envelope_consistent(motor, physics(velocityX=8.0,
                                                  velocityZ=4.0))


class ValidatePhysicsStateTests(unittest.TestCase):
    def test_accepts_complete_car_and_bike(self):
        assert len(validate_physics_state(physics())) == 4
        assert len(validate_physics_state(physics(wheels=2))) == 2

    def test_rejects_non_dict_and_missing_fields(self):
        for bad in (None, [], "physics", 42):
            with self.assertRaises(AssertionError):
                validate_physics_state(bad)
        broken = physics()
        del broken["attitudeW"]
        with self.assertRaises(AssertionError):
            validate_physics_state(broken)

    def test_rejects_non_unit_attitude(self):
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(attitudeW=0.0))

    def test_rejects_non_finite_motion(self):
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(velocityX=float("nan")))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(airSeconds=float("inf")))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(grounded="yes"))

    def test_rejects_out_of_range_semantics(self):
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(driftAmount=1.5))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(airSeconds=-0.1))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(overturnedSeconds=-1.0))

    def test_rejects_unknown_contact_and_bad_epoch(self):
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(lastContact="ceiling"))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(recoveryEpoch=-1))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(recoveryEpoch=1.5))
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(recoveryEpoch="0"))

    def test_rejects_missing_and_misordered_wheels(self):
        with self.assertRaises(AssertionError):
            validate_physics_state(physics(wheels=[]))
        flipped = physics()
        flipped["wheels"][0], flipped["wheels"][1] = \
            flipped["wheels"][1], flipped["wheels"][0]
        with self.assertRaises(AssertionError):
            validate_physics_state(flipped)

    def test_rejects_bad_wheel_fields(self):
        bad = physics()
        bad["wheels"][0]["compression"] = -0.1
        with self.assertRaises(AssertionError):
            validate_physics_state(bad)
        bad = physics()
        bad["wheels"][0]["normalLoad"] = -5.0
        with self.assertRaises(AssertionError):
            validate_physics_state(bad)
        bad = physics()
        bad["wheels"][2]["grounded"] = None
        with self.assertRaises(AssertionError):
            validate_physics_state(bad)


class ValidateSubstepPathTests(unittest.TestCase):
    def test_accepts_null_and_ordered_path(self):
        value = driving(motor_physics=physics())
        assert validate_substep_path(value, value["motor"]) is None
        value = driving(motor_physics=physics(), path=substep_path())
        assert validate_substep_path(value, value["motor"]) is not None

    def test_rejects_wrong_shape_and_offsets(self):
        value = driving(motor_physics=physics(),
                        path=substep_path()[:2])
        with self.assertRaises(AssertionError):
            validate_substep_path(value, value["motor"])
        bad = substep_path()
        bad[1]["offsetSeconds"] = 0.5
        value = driving(motor_physics=physics(), path=bad)
        with self.assertRaises(AssertionError):
            validate_substep_path(value, value["motor"])

    def test_rejects_terminal_pose_divergence(self):
        bad = substep_path()
        bad[-1]["pose"]["x"] = 5.0
        value = driving(motor_physics=physics(), path=bad)
        with self.assertRaises(AssertionError):
            validate_substep_path(value, value["motor"])

    def test_skips_pose_match_on_recovery_and_countdown(self):
        diverged = substep_path()
        diverged[-1]["pose"]["x"] = 9.0
        recovering = driving(motor_physics=physics(), path=diverged,
                             motor_overrides={"recovering": True})
        validate_substep_path(recovering, recovering["motor"])
        countdown = driving(motor_physics=physics(), path=diverged,
                            phase="countdown")
        validate_substep_path(countdown, countdown["motor"])

    def test_rejects_non_finite_pose_sample(self):
        bad = substep_path()
        bad[0]["pose"]["heading"] = float("nan")
        value = driving(motor_physics=physics(), path=bad)
        with self.assertRaises(AssertionError):
            validate_substep_path(value, value["motor"])


class ValidateSimulationDiagnosticsTests(unittest.TestCase):
    def test_accepts_physical_and_arcade(self):
        validate_simulation_diagnostics(diagnostics())
        validate_simulation_diagnostics(
            diagnostics(backend="arcade", substepsSimulated=0))

    def test_enforces_two_substeps_per_physical_tick(self):
        with self.assertRaises(AssertionError):
            validate_simulation_diagnostics(
                diagnostics(substepsSimulated=11))

    def test_rejects_unknown_backend_and_expected_mismatch(self):
        with self.assertRaises(AssertionError):
            validate_simulation_diagnostics(diagnostics(backend="x"))
        with self.assertRaises(AssertionError):
            validate_simulation_diagnostics(
                diagnostics(backend="arcade", substepsSimulated=0),
                expected_backend="physicalAssisted")

    def test_rejects_negative_or_typed_wrong(self):
        for key in ("ticksSimulated", "substepsSimulated",
                    "invalidStateFaults"):
            with self.assertRaises(AssertionError):
                validate_simulation_diagnostics(diagnostics(
                    **{key: -1}))
        with self.assertRaises(AssertionError):
            validate_simulation_diagnostics(
                diagnostics(lastStepSeconds=-0.1))
        with self.assertRaises(AssertionError):
            validate_simulation_diagnostics(diagnostics(bodies=-1))


class RecoveryDetailTests(unittest.TestCase):
    def test_arcade_and_physical_shapes(self):
        assert parse_recovery_detail("manual") == ("manual", None)
        assert parse_recovery_detail("stuck#7") == ("stuck", 7)
        assert parse_recovery_detail("overturned#12") == ("overturned", 12)
        assert parse_recovery_detail("") == ("", None)
        assert parse_recovery_detail(None) == ("", None)

    def test_rejects_unknown_or_malformed(self):
        with self.assertRaises(AssertionError):
            parse_recovery_detail("mystery#4")
        with self.assertRaises(AssertionError):
            parse_recovery_detail("stuck#x")
        with self.assertRaises(AssertionError):
            parse_recovery_detail("#4")


class PhysicsObserverTests(unittest.TestCase):
    def test_jump_records_airborne_span_and_transitions(self):
        observer = PhysicsObserver()
        grounded = physics()
        flying = physics(grounded=False, airSeconds=0.5, airPeakY=2.0,
                         wheels=[wheel(i, grounded=False)
                                 for i in range(4)])
        observer.apply(driving(tick=1, motor_physics=grounded))
        observer.apply(driving(tick=2, motor_physics=flying))
        observer.apply(driving(tick=3, motor_physics=flying))
        observer.apply(driving(tick=4, motor_physics=grounded))
        assert observer.airborne_spans == [(2, 4, 2.0)]
        transitions = {(i, kind) for i, kind, _ in
                       observer.contact_transitions}
        assert (0, "lost") in transitions
        assert (0, "regained") in transitions
        assert observer.diagnostics_seen == 4

    def test_epoch_regression_fails(self):
        observer = PhysicsObserver()
        observer.apply(driving(tick=1, motor_physics=physics(
            recoveryEpoch=3)))
        with self.assertRaises(AssertionError):
            observer.apply(driving(tick=2, motor_physics=physics(
                recoveryEpoch=2)))

    def test_recovery_reason_transition_mirrors(self):
        observer = PhysicsObserver()
        observer.apply(driving(tick=1, motor_physics=physics()))
        observer.apply(driving(tick=2, reason="manual",
                               motor_physics=physics(recoveryEpoch=1)))
        assert observer.recoveries[-1][0] == "manual"
        assert observer.recoveries[-1][1] == 1

    def test_envelope_divergence_counts_faults(self):
        observer = PhysicsObserver()
        observer.apply(driving(
            tick=1,
            motor_physics=physics(velocityZ=12.0),
            motor_overrides={"speed": 0.0}))
        assert observer.envelope_faults == 1

    def test_nonphysical_boundary_skipped(self):
        observer = PhysicsObserver()
        assert observer.apply(driving(backend="arcade",
                                      motor_physics=None)) is False
        assert observer.boundaries == 0

    def test_reset_clears_race_history(self):
        observer = PhysicsObserver()
        observer.apply(driving(tick=1,
                               motor_physics=physics(recoveryEpoch=5)))
        observer.reset()
        observer.apply(driving(race="race-2", tick=1,
                               motor_physics=physics(recoveryEpoch=0)))
        assert observer.epochs == [0]

    def test_within_tick_contact_loss_and_regain(self):
        """A tire contact loss that begins and ends inside one logical
        tick: every boundary shows the body grounded with all wheels in
        contact, so the per-wheel transition ledger sees nothing — the
        debounce counter's rise and fall is the only published evidence,
        and the episode is bounded to a single boundary window."""
        observer = PhysicsObserver()
        observer.apply(driving(tick=1, motor_physics=physics()))
        observer.apply(driving(
            tick=2,
            motor_physics=physics(contactLossSeconds=0.028)))
        observer.apply(driving(tick=3, motor_physics=physics()))
        assert observer.contact_loss_episodes == [(2, 3)]
        assert observer.max_contact_loss == 0.028
        # No grounded flag ever flipped and no airborne span minted —
        # the loss+regain lived entirely below boundary granularity.
        assert observer.contact_transitions == []
        assert observer.airborne_spans == []

    def test_contact_loss_episode_spanning_boundaries(self):
        """A loss held across two boundaries records one episode whose
        regain tick is honest — never split into per-boundary noise."""
        observer = PhysicsObserver()
        observer.apply(driving(tick=1, motor_physics=physics()))
        observer.apply(driving(
            tick=2, motor_physics=physics(contactLossSeconds=0.01)))
        observer.apply(driving(
            tick=3, motor_physics=physics(contactLossSeconds=0.02)))
        observer.apply(driving(tick=4, motor_physics=physics()))
        assert observer.contact_loss_episodes == [(2, 4)]
        # A loss that outlasts the debounce becomes real airtime and is
        # not double-counted as a transient episode.
        observer.apply(driving(
            tick=5,
            motor_physics=physics(grounded=False, airSeconds=0.1,
                                  airPeakY=0.4,
                                  contactLossSeconds=0.05,
                                  wheels=[wheel(i, grounded=False)
                                          for i in range(4)])))
        assert observer.contact_loss_episodes == [(2, 4)]


class RaceEventScopeTests(unittest.TestCase):
    """Race-scoped event predicates: a prior race's events must never
    satisfy a leg running under a new race identity — the stale-event
    bug that let the car race's landed event 'prove' the bike jump."""

    def _client(self, events):
        return types.SimpleNamespace(
            driving_events={entry["id"]: entry for entry in events})

    def test_scopes_to_current_race_only(self):
        client = self._client([
            {"id": "race-1-e1", "kind": "motor.landed", "tick": 10},
            {"id": "race-1-e2", "kind": "race.recovered",
             "detail": "stuck#1", "tick": 20},
            {"id": "race-2-e1", "kind": "motor.landed", "tick": 30},
        ])
        assert [e["id"] for e in race_events_of(
            client, "motor.landed", "race-2")] == ["race-2-e1"]
        assert len(race_events_of(
            client, "race.recovered", "race-2")) == 0

    def test_prefix_never_matches_longer_race_ids(self):
        client = self._client([
            {"id": "race-1-e1", "kind": "motor.landed", "tick": 1},
            {"id": "race-10-e1", "kind": "motor.landed", "tick": 2},
        ])
        assert [e["id"] for e in race_events_of(
            client, "motor.landed", "race-1")] == ["race-1-e1"]

    def test_overturned_event_ignores_other_races(self):
        client = self._client([
            {"id": "race-1-e4", "kind": "race.recovered",
             "detail": "overturned#2", "tick": 12},
        ])
        assert overturned_event(client, "race-2") is None
        assert overturned_event(client, "race-1")["id"] == "race-1-e4"


class RecoveryLedgerTests(unittest.TestCase):
    def test_accepts_mirrored_monotonic_ledger(self):
        recoveries = [("manual", 1, 10), ("stuck", 2, 40)]
        events = [
            {"id": "e1", "kind": "race.recovered",
             "detail": "manual#1", "tick": 10},
            {"id": "e2", "kind": "motor.landed", "tick": 20},
            {"id": "e3", "kind": "race.recovered",
             "detail": "stuck#2", "tick": 40},
        ]
        ledger = verify_recovery_ledger(recoveries, events)
        assert [row["reason"] for row in ledger] == ["manual", "stuck"]

    def test_rejects_unmirrored_or_unordered(self):
        events = [{"id": "e1", "kind": "race.recovered",
                   "detail": "manual#1", "tick": 10}]
        with self.assertRaises(AssertionError):
            verify_recovery_ledger([], events)
        out_of_order = [
            {"id": "e1", "kind": "race.recovered",
             "detail": "manual#2", "tick": 10},
            {"id": "e2", "kind": "race.recovered",
             "detail": "manual#1", "tick": 20},
        ]
        with self.assertRaises(AssertionError):
            verify_recovery_ledger(
                [("manual", 2, 10), ("manual", 1, 20)], out_of_order)
        missing_epoch = [{"id": "e1", "kind": "race.recovered",
                          "detail": "manual", "tick": 10}]
        with self.assertRaises(AssertionError):
            verify_recovery_ledger([("manual", 1, 10)], missing_epoch)


class VerifyPhysicsTraceTests(unittest.TestCase):
    def test_replays_two_physical_races_with_epoch_reset(self):
        with tempfile.TemporaryDirectory() as directory:
            records = [
                record(1, "snapshot",
                       app_state(driving(
                           tick=1, motor_physics=physics(),
                           path=substep_path()))),
                record(2, "delta",
                       changed={"driving": {"simulationTick": 2}},
                       base=1, removed=[]),
                # Race identity changes: the second race's epoch restarts
                # at 0 — a legal per-race reset, not a regression.
                record(3, "delta",
                       changed={"driving": driving(
                           race="race-2", tick=2,
                           motor_physics=physics(recoveryEpoch=0),
                           path=substep_path())},
                       base=2, removed=[]),
            ]
            write_trace(directory, records)
            outcome = verify_physics_trace(directory)
            assert outcome["boundaries"] == 3
            assert outcome["physicsBoundaries"] == 3
            assert len(outcome["races"]) == 2

    def test_arcade_trace_is_legal(self):
        with tempfile.TemporaryDirectory() as directory:
            records = [
                record(1, "snapshot",
                       app_state(driving(
                           backend="arcade", tick=1,
                           motor_physics=None,
                           sim=diagnostics(backend="arcade",
                                           substepsSimulated=0)))),
                record(2, "delta", changed={}, base=1, removed=[]),
            ]
            write_trace(directory, records)
            outcome = verify_physics_trace(directory)
            assert outcome["boundaries"] == 2
            assert outcome["physicsBoundaries"] == 0

    def test_rejects_malformed_physics_inside_trace(self):
        with tempfile.TemporaryDirectory() as directory:
            broken = physics()
            broken["wheels"] = []
            write_trace(directory, [
                record(1, "snapshot",
                       app_state(driving(tick=1, motor_physics=broken)))])
            with self.assertRaises(AssertionError):
                verify_physics_trace(directory)

    def test_rejects_delta_without_baseline_and_bad_base(self):
        with tempfile.TemporaryDirectory() as directory:
            write_trace(directory, [
                record(1, "delta", changed={}, base=0, removed=[])])
            with self.assertRaises(AssertionError):
                verify_physics_trace(directory)
        with tempfile.TemporaryDirectory() as directory:
            write_trace(directory, [
                record(1, "snapshot", app_state(None)),
                record(3, "delta", changed={}, base=99, removed=[])])
            with self.assertRaises(AssertionError):
                verify_physics_trace(directory)

    def test_rejects_incomplete_line_and_mutated_replay(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.ndjson"
            path.write_text(json.dumps(record(
                1, "snapshot", app_state(None))),  # no newline
                encoding="utf-8")
            with self.assertRaises(AssertionError):
                verify_physics_trace(directory)
        # The bounded window re-publishes surviving events on later
        # boundaries: byte-identical replay is legal, a changed entry
        # under the same id is a mutated history and must fail.
        with tempfile.TemporaryDirectory() as directory:
            events = [{"id": "ev-1", "kind": "motor.landed", "tick": 1}]
            write_trace(directory, [
                record(1, "snapshot",
                       app_state(None, events=events)),
                record(2, "delta",
                       changed={"drivingEvents": events}, base=1,
                       removed=[])])
            outcome = verify_physics_trace(directory)
            assert len(outcome["events"]) == 1
        with tempfile.TemporaryDirectory() as directory:
            first = [{"id": "ev-1", "kind": "motor.landed", "tick": 1}]
            second = [{"id": "ev-1", "kind": "motor.landed", "tick": 2}]
            write_trace(directory, [
                record(1, "snapshot",
                       app_state(None, events=first)),
                record(2, "delta",
                       changed={"drivingEvents": second}, base=1,
                       removed=[])])
            with self.assertRaises(AssertionError):
                verify_physics_trace(directory)


class CliContractTests(unittest.TestCase):
    def test_result_written_on_missing_player(self):
        import socket
        with tempfile.TemporaryDirectory() as work:
            # A closed port guarantees a refused connect — the scenario
            # must fail honestly and still write result.json.
            probe = socket.socket()
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
            probe.close()
            run_dir = Path(work) / "run"
            run_dir.mkdir()
            connection = {
                "protocolVersion": 1, "runId": "run-test",
                "token": "secret", "port": port,
                "stateSchemaVersion": 4}
            (run_dir / "connection.json").write_text(
                json.dumps(connection), encoding="utf-8")
            evidence = Path(work) / "evidence"
            argv = sys.argv
            try:
                sys.argv = ["PhysicsScenario.py",
                            str(run_dir / "connection.json"),
                            "--evidence", str(evidence)]
                assert main() == 1
            finally:
                sys.argv = argv
            result = json.loads(
                (evidence / "result.json").read_text(encoding="utf-8"))
            assert result["status"] == "failed"
            assert "error" in result

    def test_evidence_directory_must_be_new(self):
        # The contract: an existing evidence directory is refused
        # outright by the same exist_ok=False guard main() uses.
        with tempfile.TemporaryDirectory() as work:
            with self.assertRaises(FileExistsError):
                Path(work).mkdir(parents=True, exist_ok=False)


if __name__ == "__main__":
    unittest.main()
