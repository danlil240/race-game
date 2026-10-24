"""Collect and use a real gadget in the circuit campaign; capture its HUD."""
import argparse
import json
import math
import time
from pathlib import Path
from AgentScenario import inputs
from GameplayLoopScenario import LoopClient, solo_session, wait_loop_state
from ClosedCircuitScenario import load_route
from DrivingScenario import screenshot
import NetworkScenario as route


def local_item(state):
    solo = state.get("soloRace") or {}
    world = (solo.get("race") or {}).get("gadgets") or {}
    return next((r for r in world.get("racers", []) if r["participantId"] == solo.get("participantId")), {})


def run(path, evidence):
    evidence.mkdir(parents=True, exist_ok=False)
    connection = json.loads(path.read_text(encoding="utf-8-sig"))
    run_dir = path.resolve().parent
    connection["_directory"] = str(run_dir)
    client = LoopClient(connection, evidence)
    result = {"status": "failed"}
    try:
        load_route("coastal-village")
        client.applied("profile.select", {"slot": 0})
        client.applied("campaign.open")
        client.applied("campaign.start", {"eventId": "trial-sunny-car-circuit", "vehicleId": "car-01", "difficulty": "easy"})
        wait_loop_state(client, lambda s: ((s.get("soloRace") or {}).get("race") or {}).get("phase") == "running", 30, "campaign running")
        # Aim through the left-hand boxes instead of following the lap
        # driver's racing line through sites the leading AI may consume.
        deadline = time.monotonic()+25
        while not local_item(client.state).get("heldItemId"):
            assert time.monotonic()<deadline, "No pickup collected in the left lane"
            motor = solo_session(client.state)["motor"]
            arc, _ = route.LanDriver._projection(motor)
            x,z = route.LanDriver._point_along(arc+9)
            bx,bz = route.LanDriver._point_along(arc+10)
            dx,dz = bx-x,bz-z
            length = math.hypot(dx,dz)
            steer = route.steer_command(motor,x-dz/length*4.2,z+dx/length*4.2,gain=1.5)
            tick = client.state["tick"]
            client.applied("input.set",inputs(steering=round(steer,3),duration=8))
            wait_loop_state(client,lambda s:s["tick"]>=tick+2,5,"drive toward glowing box")
        granted = dict(local_item(client.state))
        client.applied("input.set", inputs(throttle=0, brake=1, duration=150))
        tick = client.state["tick"]
        wait_loop_state(client, lambda s: s["tick"] >= tick+15, 5, "HUD settles")
        screenshot(client, "gadget-ready", str(run_dir))
        client.applied("input.set", inputs(throttle=0, brake=1, gadget=True, duration=1))
        client.applied("input.set", inputs(throttle=0, brake=1, gadget=False, duration=150))
        wait_loop_state(client, lambda s: not local_item(s).get("heldItemId"), 5, "use the held gadget")
        used = [e for e in client.events_of("gadget.used") if e.get("detail") == granted["heldItemId"]]
        assert len(used) == 1, used
        tick = client.state["tick"]
        wait_loop_state(client, lambda s: s["tick"] >= tick+3, 5, "used feedback")
        screenshot(client, "gadget-used", str(run_dir))
        result.update(status="passed", collected=granted, used=used)
        client.applied("race.exit")
    except Exception as error:
        result["error"] = str(error)
        raise
    finally:
        (evidence / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    run(args.connection, args.evidence)
