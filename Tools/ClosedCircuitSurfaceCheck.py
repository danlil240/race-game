"""Measure rough-ground drag and barrier containment using real player inputs."""
import argparse
import json
import math
import time
from pathlib import Path
import NetworkScenario as follower
from AgentScenario import inputs
from GameplayLoopScenario import LoopClient, wait_loop_state
from DrivingScenario import screenshot
from ClosedCircuitScenario import load_route


def lane_drive(client, offset, predicate, timeout=15, boost=False):
    deadline=time.monotonic()+timeout
    samples=[]
    while time.monotonic()<deadline:
        state=client.state; motor=state["driving"]["motor"]
        arc,_=follower.LanDriver._projection(motor)
        x,z=follower.LanDriver._point_along(arc+9)
        bx,bz=follower.LanDriver._point_along(arc+10)
        dx,dz=bx-x,bz-z; length=math.hypot(dx,dz)
        # Right side of the forward racing line, still inside the 11 m rail.
        tx,tz=x+dz/length*offset,z-dx/length*offset
        steer=follower.steer_command(motor,tx,tz,gain=1.5)
        tick=state["tick"]
        client.applied("input.set",inputs(steering=round(steer,3),throttle=1,brake=0,boost=boost,duration=10))
        wait_loop_state(client,lambda s:s["tick"]>=tick+3,5,"lane control")
        motor=client.state["driving"]["motor"]
        arc,_=follower.LanDriver._projection(motor)
        cx,cz=follower.LanDriver._point_along(arc)
        distance=math.hypot(motor["x"]-cx,motor["z"]-cz)
        samples.append({"tick":client.state["tick"],"speed":motor["speed"],"roadDistance":distance,"x":motor["x"],"z":motor["z"]})
        if predicate(samples): return samples
    raise AssertionError("Lane target timed out: "+repr(samples[-5:]))


def run(path,evidence):
    evidence.mkdir(parents=True,exist_ok=False)
    connection=json.loads(path.read_text(encoding="utf-8-sig")); run_dir=path.resolve().parent
    connection["_directory"]=str(run_dir);client=LoopClient(connection,evidence)
    result={"status":"failed"}
    try:
        load_route("coastal-village")
        client.applied("profile.select",{"slot":0})
        client.applied("ui.navigate",{"screen":"practice"})
        client.applied("race.start",{"vehicleId":"car-01","eventId":"intro-01"})
        wait_loop_state(client,lambda s:(s.get("driving") or {}).get("phase")=="running",30,"practice running")
        paved=lane_drive(client,0,lambda s: s[-1]["speed"]>=24 and s[-1]["roadDistance"]<4)
        rough=lane_drive(client,8.7,lambda s:len(s)>8 and all(7.3<x["roadDistance"]<10.8 and x["speed"]<9 for x in s[-8:]))
        screenshot(client,"rough-ground-slow",str(run_dir))
        boosted=lane_drive(client,8.7,lambda s:len(s)>12 and all(x["speed"]<9 and x["roadDistance"]>7.2 for x in s[-10:]),boost=True)
        rejoin=lane_drive(client,0,lambda s:s[-1]["speed"]>24 and s[-1]["roadDistance"]<3)
        screenshot(client,"road-speed-restored",str(run_dir))
        # The containment claim is about traveled position. A grounded car
        # can still spin its driven wheels against a barrier.
        blocked=lane_drive(client,22,lambda s:len(s)>35 and math.hypot(s[-1]["x"]-s[-12]["x"],s[-1]["z"]-s[-12]["z"])<.15,timeout=18)
        assert max(s["roadDistance"] for s in blocked)<11, "Car escaped the continuous barrier"
        screenshot(client,"barrier-stops-cut",str(run_dir))
        client.applied("race.exit")
        result.update(status="passed", paved=paved[-1],rough=rough[-1],boostedRough=boosted[-1],rejoin=rejoin[-1],barrier=blocked[-1],
            samples={"paved":paved,"rough":rough,"boosted":boosted,"rejoin":rejoin,"barrier":blocked})
    except Exception as error: result["error"]=str(error);raise
    finally:
        (evidence/"result.json").write_text(json.dumps(result,indent=2),encoding="utf-8");client.close()


if __name__=="__main__":
    p=argparse.ArgumentParser(description=__doc__);p.add_argument("connection",type=Path);p.add_argument("--evidence",type=Path,required=True)
    a=p.parse_args();run(a.connection,a.evidence)
