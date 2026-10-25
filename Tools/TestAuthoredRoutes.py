"""Authored route selection and normal-input steering integrity tests."""
import copy
import importlib
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import GameplayLoopScenario as loop
import NetworkScenario as network


def course(course_id, offset=1000):
    return {"courseId": course_id,
            "aiRoute": [{"x": offset, "z": 0}, {"x": offset, "z": 100},
                        {"x": offset + 100, "z": 100}, {"x": offset + 100, "z": 0}],
            "gates": [{"center": {"x": offset, "z": 50},
                       "direction": {"x": 0, "y": 2}}]}


def manifest():
    full = importlib.import_module("FullContentScenario")
    return {"schemaVersion": 1, "contentIdentity": "full-content-1",
            "courses": [course(cid, 1000 + i * 500) for i, cid in enumerate(
                cid for ids in full.WORLD_COURSES.values() for cid in ids)]}


class Peer:
    def __init__(self, course_id="harbor-hop"):
        self.info = {"runId": "unit-run"}
        self.last_state_tick = 1
        self.frames = []
        self.state = {"soloRace": {"mode": "timeTrial", "phase": "running",
                     "trial": {"courseId": course_id, "raceId": "race-1",
                               "simulationTick": 1, "lap": 0, "nextCheckpoint": 0,
                               "motor": {"x": 1000, "z": 35, "heading": 0,
                                         "grounded": True, "boostCharge": 0}}}}

    def overdue_commands(self):
        return {}

    def send(self, frame):
        self.frames.append(frame)


class AuthoredRouteTests(unittest.TestCase):
    def load(self, data):
        full = importlib.import_module("FullContentScenario")
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "courses.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            return full.load_course_routes(path)

    def test_authored_gate_direction_drives_input_without_mutating_observations(self):
        routes = self.load(manifest())
        peer = Peer()
        peer.solo_routes = routes
        before = copy.deepcopy(peer.state)
        driver = loop.SoloDriver(peer)
        driver.pump()
        self.assertEqual(len(peer.frames), 1)
        frame = peer.frames[0]
        self.assertEqual(frame["action"], "input.set")
        self.assertAlmostEqual(float(frame["parameters"]["steering"]), 0, places=3)
        self.assertEqual(peer.state, before)
        self.assertEqual(network.LAN_ROUTE[0], (0, -30))

    def test_course_switch_uses_its_own_geometry(self):
        routes = self.load(manifest())
        peer = Peer("lighthouse-loop")
        peer.solo_routes = routes
        peer.state["soloRace"]["trial"]["motor"]["x"] = 1500
        driver = loop.SoloDriver(peer)
        driver.pump()
        self.assertAlmostEqual(float(peer.frames[0]["parameters"]["steering"]), 0, places=3)

    def test_two_drivers_keep_independent_course_geometry(self):
        routes = self.load(manifest())
        a, b = Peer(), Peer("lighthouse-loop")
        a.solo_routes = b.solo_routes = routes
        b.state["soloRace"]["trial"]["motor"]["x"] = 1500
        first, second = loop.SoloDriver(a), loop.SoloDriver(b)
        first.pump(); second.pump()
        a.last_state_tick = 5
        first.pump()
        self.assertAlmostEqual(float(a.frames[-1]["parameters"]["steering"]), 0, places=3)
        self.assertEqual(first._point_along(145), (1000, 45))
        self.assertEqual(second._point_along(145), (1500, 45))

    def test_geometry_export_is_preserved_and_bound_to_live_content(self):
        full = importlib.import_module("FullContentScenario")
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "source.json"
            raw = json.dumps(manifest()).encode("utf-8")
            path.write_bytes(raw)
            peer = Peer()
            peer.state["progression"] = {"contentIdentity": "full-content-1|build-hash"}
            report = {}
            full.prepare_course_routes(peer, report, False, tmp, path)
            self.assertEqual((Path(tmp) / "course-routes.json").read_bytes(), raw)
            self.assertEqual(report["drivingRoutes"]["sha256"], full.file_hash(path))
            peer.state["progression"]["contentIdentity"] = "other-content"
            with self.assertRaisesRegex(ValueError, "identity"):
                full.prepare_course_routes(peer, {}, False, tmp, path)

    def test_unavailable_content_reports_gaps_without_requiring_route_geometry(self):
        full = importlib.import_module("FullContentScenario")
        for scope in ("campaign", "modes"):
            with self.subTest(scope=scope), tempfile.TemporaryDirectory() as tmp:
                peer = Peer()
                peer.events = []
                peer.state["captureValid"] = True
                peer.state["progression"] = {"available": False}
                report = full.scenario_report(scope, False)
                getattr(full, "run_" + scope + "_scope")(
                    peer, loop.Checks(), report, False, tmp,
                    routes_path=Path(tmp) / "absent.json")
                self.assertNotIn("drivingRoutes", report)
                self.assertTrue((Path(tmp) / ("events.ndjson" if scope == "campaign"
                                             else "modes.json")).exists())

    def test_unknown_live_course_never_falls_back_to_graybox(self):
        peer = Peer("unknown")
        peer.solo_routes = self.load(manifest())
        with self.assertRaisesRegex(AssertionError, "unknown"):
            loop.SoloDriver(peer).pump()
        self.assertEqual(peer.frames, [])

    def test_missing_duplicate_nonfinite_and_degenerate_geometry_are_rejected(self):
        cases = []
        missing = manifest(); missing["courses"].pop(); cases.append(missing)
        duplicate = manifest(); duplicate["courses"][-1] = duplicate["courses"][0]; cases.append(duplicate)
        bad = manifest(); bad["courses"][0]["aiRoute"][0]["x"] = math.nan; cases.append(bad)
        bad = manifest(); bad["courses"][0]["gates"][0]["direction"] = {"x": 0, "y": 0}; cases.append(bad)
        bad = manifest(); bad["courses"][0]["aiRoute"] = [{"x": 0, "z": 0}] * 4; cases.append(bad)
        for data in cases:
            with self.subTest(data=data), self.assertRaises(ValueError):
                self.load(data)

    def test_route_point_and_progress_use_same_closed_loop_origin(self):
        routes = self.load(manifest())
        peer = Peer(); peer.solo_routes = routes
        driver = loop.SoloDriver(peer)
        driver.pump()
        arc, segment = driver._projection({"x": 1000, "z": 35})
        self.assertEqual(arc, 135)
        self.assertEqual(driver._point_along(arc + 10), (1000, 45))
        self.assertEqual(driver._lookahead(195, 1), 5.5)


if __name__ == "__main__":
    unittest.main()
