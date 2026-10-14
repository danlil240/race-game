"""Task-4 racer-visual screenshot capture.

Usage: RacerVisualShots.py <run-dir>/connection.json --evidence <new-dir>

Attaches to a live development player's validation run and photographs
every animal racer in both vehicle classes under both cameras: all four
profiles (slots 0..3 carry fox/rabbit/panda/raccoon and the matching
color slot) each drive a short solo leg on intro-01 in car-01 and
motorcycle-01, captured chase + overhead — 16 screenshots under
<run-dir>/screenshots/. Every capture also asserts the schema-5 racer
facts stream the resolved character/color/visual identities, so the
evidence binds the pixels to the published identities.
"""

import argparse
import json
import sys
import time
from pathlib import Path

from AgentScenario import Client, SUPPORTED_STATE_SCHEMAS
from DrivingScenario import (DrivingClient, Driver, screenshot,
                             wait_phase)

# Slot -> (avatar, colorIndex) per ProfileFactory/ProfileValidation.
SLOTS = ((0, "fox"), (1, "rabbit"), (2, "panda"), (3, "raccoon"))
VEHICLES = (("car", "car-01", "rally-car-01"),
            ("moto", "motorcycle-01", "moto-01"))


def camera_mode(client):
    cam = (client.state.get("driving") or {}).get("camera") or {}
    if not cam:
        cam = (client.state.get("presentation") or {}).get("camera") or {}
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
            lambda state: (((state.get("driving") or {}).get("camera")
                            or {}).get("mode")
                           or ((state.get("presentation") or {})
                               .get("camera") or {}).get("mode"))
            != before, timeout=10)


def racer_facts(client):
    return ((client.state.get("presentation") or {}).get("racers")
            or [])


def assert_racer_fact(client, avatar, color_index, vehicle_id,
                      visual_id):
    """The solo racer's streamed fact names the resolved identity —
    the authored visual id, never a placeholder."""
    facts = racer_facts(client)
    assert len(facts) == 1, \
        "solo presentation must stream exactly one racer fact: " \
        + repr(facts)
    fact = facts[0]
    assert fact.get("characterId") == avatar, repr(fact)
    assert fact.get("colorIndex") == color_index, repr(fact)
    assert fact.get("vehicleId") == vehicle_id, repr(fact)
    assert fact.get("vehicleVisualId") == visual_id, repr(fact)
    assert fact.get("poseSource") == "authoritative", repr(fact)
    return fact


def shot_pair(client, run_directory, name):
    shot = screenshot(client, name + "-chase", run_directory)
    assert shot["status"] == "captured"
    set_camera(client, "overhead")
    shot = screenshot(client, name + "-overhead", run_directory)
    assert shot["status"] == "captured"
    set_camera(client, "chase")


def run(connection, evidence):
    client = DrivingClient(connection, evidence)
    run_directory = connection["_directory"]
    checks = []
    captures = []
    try:
        client.send({"type": "discover"})
        client.wait(lambda event: event.get("type") == "discovery")

        # One profile per avatar slot — the roster avatars the shared
        # rig catalog maps to (fox/rabbit/panda/raccoon).
        for slot, avatar in SLOTS:
            client.applied("profile.create",
                           {"slot": slot, "language": "en"})

        for slot, avatar in SLOTS:
            client.applied("profile.select", {"slot": slot})
            client.applied("ui.navigate", {"screen": "practice"})
            for vlabel, vehicle, visual_id in VEHICLES:
                started, _ = client.applied(
                    "race.start", {"vehicleId": vehicle,
                                   "eventId": "intro-01"})
                assert (client.action_state(started["commandId"])
                        ["driving"]["vehicleId"] == vehicle)
                wait_phase(client, "running", timeout=30)
                driver = Driver(client)
                # A short leg up the start straight: enough motion that
                # wheels/spin/rider are visibly alive in the captures.
                driver.drive_leg(0.0, 18.0, radius=4.0, timeout=25,
                                 note="start straight")
                # The published facts must bind this animal + visual.
                fact = assert_racer_fact(
                    client, avatar, slot, vehicle, visual_id)
                assert fact.get("participantId"), repr(fact)
                name = "%s-%s-" % (avatar, vlabel)
                shot_pair(client, run_directory, name)
                captures.extend((name + "chase", name + "overhead"))
                exited, _ = client.applied("race.exit")
                assert (client.action_state(exited["commandId"])
                        ["driving"] is None)
                checks.append("%s %s raced as %s in %s (%s)"
                              % (avatar, vlabel, avatar, vehicle,
                                 visual_id))

        client.send({"type": "finish"})
        finished = client.wait(
            lambda event: event.get("type") == "finished")
        assert finished["captureValid"], finished
        return {"checks": checks, "captures": captures}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection_file", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed",
              "scope": "racer visual screenshot matrix"}
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
