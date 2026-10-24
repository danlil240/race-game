"""Capture the opt-in art trial through the actual development player's controls.

python Tools/AssetTrialShots.py <run-dir>/connection.json --evidence <new-dir>
"""
import argparse
import json
from pathlib import Path

from AgentScenario import inputs
from DrivingScenario import DrivingClient, Driver, screenshot, wait_phase, steer_command
from RacerVisualShots import set_camera


class ArtDriver(Driver):
    """Drive slowly enough to stop near the scenery being photographed."""
    def refresh_input(self, motor, tx, tz, drift=False, boost=False):
        steering = round(steer_command(motor, tx, tz), 3)
        tick = self.client.state["tick"]
        if (self.held is None or tick >= self.held_at_tick + 8
                or abs(steering - self.held) >= self.CORRECTION):
            self.client.applied("input.set", inputs(
                steering=steering, duration=self.HOLD_TICKS,
                throttle=.3, brake=.4 if abs(motor["speed"]) > 6 else 0))
            self.held = steering
            self.held_at_tick = tick


def run(connection_path, evidence, language="en", vehicle="car-01", coastal_tour=False):
    connection = json.loads(connection_path.read_text(encoding="utf-8"))
    run_dir = connection_path.resolve().parent
    connection["_directory"] = str(run_dir)
    evidence.mkdir(parents=True, exist_ok=False)
    client = DrivingClient(connection, evidence)
    captures = []
    try:
        assert client.state["contentVersion"] == "asset-trial-2", "Wrong build/content"
        client.applied("profile.create", {"slot": 0, "language": language})
        client.applied("ui.navigate", {"screen": "practice"})
        client.applied("race.start", {"vehicleId": vehicle, "eventId": "intro-01"})
        wait_phase(client, "running")
        driver = ArtDriver(client)
        stops = [
            ("opening", [(0, 18)]),
            ("garden", [(0, 33), (20, 30), (40, 30)]),
        ]
        if coastal_tour:
            stops += [
                ("harbor", [(50, 30), (50, 44), (63, 55), (85, 55), (95, 10), (108, 10)]),
                ("south-village", [(95, -8), (56, -18), (56, -40)]),
            ]
        for label, waypoints in stops:
            for x, z in waypoints:
                driver.drive_leg(x, z, radius=3, timeout=30, note=label)
            client.applied("input.set", inputs(duration=120, throttle=0, brake=1))
            client.boundary(lambda s: abs(s["driving"]["motor"].get("speed", 0)) < .2)
            for mode in ("chase", "overhead"):
                set_camera(client, mode)
                tick = client.state["driving"]["motor"]["tick"]
                client.boundary(lambda s: s["driving"]["motor"]["tick"] > tick + 12)
                fact = client.state["presentation"]["racers"][0]
                expected_visual = "kenney-racer-trial" if vehicle == "car-01" else "moto-01"
                assert fact["vehicleVisualId"] == expected_visual, fact
                assert fact["characterId"] == "fox", fact
                shot = screenshot(client, "asset-trial-" + label + "-" + mode, str(run_dir))
                captures.append({"label": label, "camera": mode, "capture": shot, "racer": fact})
                (evidence / (label + "-" + mode + "-state.json")).write_text(
                    json.dumps(client.state, indent=2), encoding="utf-8")
            set_camera(client, "chase")
            if label == "opening":
                client.applied("pause.toggle")
                assert client.state["paused"]
                captures.append({"label": "pause", "capture": screenshot(client, "asset-trial-pause", str(run_dir))})
                client.applied("pause.toggle")
                assert not client.state["paused"]
        old_race = client.state["driving"]["raceId"]
        client.applied("race.restart")
        wait_phase(client, "running")
        assert client.state["driving"]["raceId"] != old_race
        client.applied("input.set", inputs(duration=120, throttle=0, brake=1))
        captures.append({"label": "restart", "capture": screenshot(client, "asset-trial-restart", str(run_dir))})
        client.applied("race.exit")
        assert client.state["screen"] == "practice"
        (evidence / "result.json").write_text(json.dumps({
            "status": "passed", "language": language, "vehicle": vehicle, "captures": captures,
            "coastalTour": coastal_tour,
            "checks": ["authored vehicle and fox bound", "chase and overhead cameras", "pause and resume", "fresh restart identity", "exit to practice"],
            "scope": "PC art smoke test; not tablet qualification",
        }, indent=2), encoding="utf-8")
        print(json.dumps({"status": "passed", "screenshots": len(captures), "run": str(run_dir)}))
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("connection", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--language", choices=("en", "he"), default="en")
    parser.add_argument("--vehicle", choices=("car-01", "motorcycle-01"), default="car-01")
    parser.add_argument("--coastal-tour", action="store_true")
    args = parser.parse_args()
    run(args.connection, args.evidence, args.language, args.vehicle, args.coastal_tour)
