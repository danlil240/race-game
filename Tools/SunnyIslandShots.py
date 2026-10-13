"""Sunny Island visual-benchmark screenshot capture.

Usage: SunnyIslandShots.py <run-dir>/connection.json --evidence <new-dir>

Attaches to a live development player's validation run and drives the
dressed sunny-island-benchmark-01 course through the shared command path,
capturing chase and overhead screenshots at every readability-critical
landmark: the start line, the west turn lanes, the north-east shortcut
lane, the boardwalk jump (airborne frame), gate-3, the authored recovery
respawn, the south-east shortcut lane and gate-4. Runs once per vehicle
class so the review set covers car-01 and motorcycle-01 alike. Every
capture is requested against observed motor state — never a guess —
and lands under <run-dir>/screenshots/ via the ValidationBridge.
"""

import argparse
import json
import sys
import time
from pathlib import Path

from AgentScenario import Client, inputs, verify_trace, SUPPORTED_STATE_SCHEMAS
from DrivingScenario import (DrivingClient, Driver, screenshot,
                             wait_phase)


def camera_mode(client):
    cam = (client.state.get("driving") or {}).get("camera") or {}
    return cam.get("mode", "none")


def set_camera(client, mode):
    """Toggle the real presentation camera until it reports `mode`."""
    deadline = time.monotonic() + 15
    while camera_mode(client) != mode:
        assert time.monotonic() < deadline, \
            "camera never reached " + mode + " (mode=" \
            + camera_mode(client) + ")"
        before = camera_mode(client)
        client.applied("camera.toggle")
        client.boundary(
            lambda state: ((state.get("driving") or {}).get("camera")
                           or {}).get("mode") != before, timeout=10)


def shot_pair(client, run_directory, name):
    """One landmark, both required views."""
    shot = screenshot(client, name + "-chase", run_directory)
    assert shot["status"] == "captured"
    set_camera(client, "overhead")
    shot = screenshot(client, name + "-overhead", run_directory)
    assert shot["status"] == "captured"
    set_camera(client, "chase")


def run_class(client, driver, run_directory, label):
    wait_phase(client, "running", timeout=30)
    set_camera(client, "chase")

    # Start line / south straight.
    shot_pair(client, run_directory, label + "start")

    # North up the south straight, west-turn lanes through gate-1.
    driver.drive_leg(0.0, 33.0, radius=5.0, timeout=25, note="gate-1")
    shot_pair(client, run_directory, label + "gate1-turn")

    # The north-east shortcut lane (inside/south of IslandNorthEast).
    driver.drive_leg(48.0, 30.0, radius=5.0, timeout=25,
                     note="NE shortcut mouth")
    driver.drive_leg(50.0, 42.0, radius=6.0, timeout=25,
                     note="NE shortcut lane")
    shot_pair(client, run_directory, label + "shortcut-ne")

    # Rejoin above the island and cross gate-2, then run the east leg
    # through gate-3.
    driver.drive_leg(58.0, 52.0, radius=5.0, timeout=25,
                     note="NE rejoin")
    driver.drive_leg(63.0, 55.0, radius=6.0, timeout=20, note="gate-2")
    driver.drive_leg(95.0, -8.0, radius=5.0, timeout=30, note="gate-3")
    shot_pair(client, run_directory, label + "gate3")

    # Authored recovery: press R like a player, respawn on the gate-3
    # safe pose (95,3) heading south, then photograph it.
    client.applied("input.set", inputs(steering=0.0, duration=5,
                                     recover=True))
    client.boundary(
        lambda state: state["driving"]["motor"].get("recovering") is True,
        timeout=15)
    shot_pair(client, run_directory, label + "recovery")
    driver.held = None

    # The south-east shortcut lane (inside/west of IslandSouthEast).
    driver.drive_leg(55.0, -18.0, radius=6.0, timeout=25,
                     note="SE shortcut mouth")
    driver.drive_leg(56.0, -40.0, radius=7.0, timeout=25,
                     note="SE shortcut lane")
    shot_pair(client, run_directory, label + "shortcut-se")

    # Gate-4 and the south return — the last readability stretch.
    driver.drive_leg(42.0, -55.0, radius=6.0, timeout=25, note="gate-4")
    shot_pair(client, run_directory, label + "gate4-return")

    # The boardwalk jump: approach from the west leg, screenshot the
    # observed airborne frame (never a guessed sleep), then the landing.
    driver.drive_leg(-30.0, 10.0, radius=8.0, timeout=30,
                     note="ramp approach")
    driver.drive_leg(-60.0, 14.0, radius=7.0, timeout=30,
                     note="ramp line-up")
    airborne = {"got": False}

    def shoot_when_air(state):
        motor = state["driving"]["motor"]
        if not airborne["got"] and not motor.get("grounded", True):
            airborne["got"] = True
            shot = screenshot(client, label + "jump-airborne",
                              run_directory)
            assert shot["status"] == "captured"
            return True
        return airborne["got"]

    driver.drive_leg(-60.0, 62.0, radius=7.0, timeout=35,
                     until=shoot_when_air, note="ramp flight")
    if not airborne["got"]:
        # The flight finished before the poll could fire — honest
        # fallback: photograph the landing zone instead of faking air.
        shot = screenshot(client, label + "jump-landing", run_directory)
        assert shot["status"] == "captured"
    else:
        client.boundary(
            lambda state: state["driving"]["motor"].get("grounded")
            is True, timeout=15)
        shot = screenshot(client, label + "jump-landing", run_directory)
        assert shot["status"] == "captured"

    exited, _ = client.applied("race.exit")
    assert client.action_state(exited["commandId"])["driving"] is None


def run(connection, evidence):
    client = DrivingClient(connection, evidence)
    run_directory = connection["_directory"]
    checks = []
    try:
        client.send({"type": "discover"})
        client.wait(lambda event: event.get("type") == "discovery")
        created, _ = client.applied("profile.create",
                                    {"slot": 0, "language": "en"})
        assert client.action_state(created["commandId"])["activeSlot"] == 0
        client.applied("ui.navigate", {"screen": "practice"})

        # The published course-visual identity must be the dressed root.
        def state_cvid(state):
            return (state.get("presentation") or {}).get("courseVisualId")
        client.boundary(
            lambda state: state_cvid(state) == "sunny-island-benchmark-01",
            timeout=15)

        for label, vehicle in (("car-", "car-01"),
                               ("moto-", "motorcycle-01")):
            started, _ = client.applied(
                "race.start", {"vehicleId": vehicle,
                               "eventId": "intro-01"})
            driving = client.action_state(started["commandId"])["driving"]
            assert driving["vehicleId"] == vehicle
            run_class(client, Driver(client), run_directory, label)
            checks.append(label + "landmark captures complete")

        client.send({"type": "finish"})
        finished = client.wait(lambda event: event.get("type") == "finished")
        assert finished["captureValid"], finished
        return {"checks": checks}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection_file", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed",
              "scope": "sunny island landmark screenshot capture"}
    try:
        if not __debug__:
            raise RuntimeError(
                "Run without -O: scenario assertions must be enabled")
        connection = json.loads(
            args.connection_file.read_text(encoding="utf-8-sig"))
        assert connection["protocolVersion"] == 1
        assert connection.get("stateSchemaVersion", 1) in \
            SUPPORTED_STATE_SCHEMAS
        connection["_directory"] = str(
            args.connection_file.resolve().parent)
        result.update(run(connection, args.evidence))
        result["status"] = "passed"
    except Exception as error:  # noqa: BLE001 - scenario reports detail
        result["error"] = repr(error)
        raise
    finally:
        (args.evidence / "result.json").write_text(
            json.dumps(result, indent=2, sort_keys=True),
            encoding="utf-8")


if __name__ == "__main__":
    main()
