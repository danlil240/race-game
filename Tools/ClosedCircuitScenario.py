"""Drive all three authored circuits using real input holds and observed results."""
import argparse
import json
import math
from pathlib import Path
import NetworkScenario as route_driver
from GameplayLoopScenario import LoopClient, Checks, drive_loop_race, wait_loop_state
from DrivingScenario import screenshot
from RacerVisualShots import set_camera

IDS = ("coastal-village", "pine-forest", "desert-canyon")


def load_route(course):
    data = json.loads((Path(__file__).resolve().parents[1] / "Assets/FamilyRacing/Generated/AssetTrial/Circuits" / (course+".json")).read_text(encoding="utf-8"))
    route_driver.LAN_ROUTE = [(p["x"], p["z"]) for p in data["aiRoute"]]
    p = route_driver.LAN_ROUTE
    route_driver.LAN_ROUTE_LENGTH = sum(math.dist(p[i-1], p[i]) for i in range(len(p)))
    arc = [0.0]
    for i in range(1,len(p)): arc.append(arc[-1]+math.dist(p[i-1],p[i]))
    route_driver.LAN_ROUTE_ARC = arc
    route_driver.LAN_GATES = [(g["center"]["x"],g["center"]["z"]) for g in data["gates"]]
    route_driver.LAN_GATE_DIRS = [(g["direction"]["x"],g["direction"]["y"]) for g in data["gates"]]
    route_driver.LAN_GATE_APPROACH = 9.0
    assert data["shortcutLanes"] == [], "Shortcut authored on closed circuit"
    return data


def run(connection_path, evidence):
    evidence.mkdir(parents=True,exist_ok=False)
    connection=json.loads(connection_path.read_text(encoding="utf-8-sig"))
    run_dir=connection_path.resolve().parent
    connection["_directory"]=str(run_dir)
    result={"status":"failed", "checks":[]}
    client=LoopClient(connection,evidence)
    checks=Checks()
    try:
        assert client.state["contentVersion"] == "closed-circuits-2"
        client.applied("profile.create",{"slot":0,"language":"en"})
        for course in IDS:
            load_route(course)
            client.applied("quickRace.open")
            client.applied("quickRace.start",{"courseId":course,"classId":"car","vehicleId":"car-01","difficulty":"easy"})
            wait_loop_state(client,lambda s: ((s.get("soloRace") or {}).get("race") or {}).get("phase")=="running",30,course)
            screenshot(client,course+"-chase",str(run_dir))
            set_camera(client,"overhead")
            screenshot(client,course+"-overhead",str(run_dir))
            set_camera(client,"chase")
            race_result=drive_loop_race(client,checks,course,finish_timeout=180)
            assert race_result.get("courseId")==course, race_result
            screenshot(client,course+"-result",str(run_dir))
            checks.passed(course,"Real input traversal finished the closed checkpoint loop")
            result["checks"].append({"course":course,"resultId":race_result["resultId"],"elapsedTicks":race_result.get("finalizedElapsedTicks")})
            client.applied("results.continue")
        result["status"]="passed"
    except Exception as error:
        result["error"]=str(error)
        raise
    finally:
        (evidence/"result.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
        client.close()


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection",type=Path);parser.add_argument("--evidence",type=Path,required=True)
    args=parser.parse_args();run(args.connection,args.evidence)
