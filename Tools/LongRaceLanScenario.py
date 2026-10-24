"""Two real Windows endpoints complete the long forest journey over loopback LAN."""
import argparse
import json
import subprocess
import time
from pathlib import Path

import NetworkScenario as network
import GameplayLoopScenario as loop
from LongRaceScenario import ROOT, TourDriver
from DrivingScenario import screenshot


def validate_results(host_race, guest_race, event_id, course):
    a, b = host_race["result"], guest_race["result"]
    assert host_race["phase"] == guest_race["phase"] == "finished"
    assert a["resultId"] == b["resultId"], "Endpoints received different result identities"
    assert a["eventId"] == b["eventId"] == event_id
    # LAN uses the original format-1 result contract, which does not populate
    # the local format-2 courseId field. The authoritative sessions identify
    # the actual driven course on both endpoints.
    for race in (host_race, guest_race):
        assert race["racers"] and all(r["session"]["courseId"] == course for r in race["racers"])
    humans = [r for r in a["racers"] if r["attribution"] == "human"]
    assert len(humans) == 2 and all(r["outcome"] == "finished" for r in humans), humans
    assert [(r["slot"], r["finishTicks"], r["outcome"]) for r in a["racers"]] == [
        (r["slot"], r["finishTicks"], r["outcome"]) for r in b["racers"]]
    return a, humans


def saved_rewards(root, result_id):
    rewards = []
    for name, slot in (("host", 0), ("guest", 1)):
        profile = json.loads((root / (name + "-profiles") / ("profile-%d.json" % slot)).read_text(encoding="utf-8"))
        receipts = [r for r in profile["appliedResults"] if r["resultId"] == result_id]
        assert len(receipts) == 1 and receipts[0]["awardedCoins"] > 0
        rewards.append({"endpoint": name, "coins": profile["coins"], "receipt": receipts[0]})
    return rewards


def run(args):
    root=args.evidence.resolve();root.mkdir(parents=True,exist_ok=False)
    entries=[];drivers=[];report={"status":"failed","transport":"Unity Transport loopback","checks":[]}
    course="pine-lake-journey-v1"
    try:
        for name,language in (("host","en"),("guest","he")):
            evidence=root/(name+"-evidence");evidence.mkdir()
            process,info,run_dir=network.launch([str(args.player.resolve()),"-force-d3d11","-screen-fullscreen","0","-screen-width","1024","-screen-height","768","--validation-trace-mb","2048"],root,name,root/(name+"-profiles"),120)
            client=loop.LoopClient(info,evidence);entries.append((name,process,client,run_dir))
            client.applied("profile.create",{"slot":0 if name=="host" else 1,"language":language})
        host=entries[0][2];guest=entries[1][2]
        host.applied("lan.host",{"port":0})
        network.wait_state(host,lambda s:s["network"]["phase"]=="lobby",20,"host lobby")
        port=host.state["network"]["descriptor"]["hostPort"]
        network.drive_join(guest,port,"127.0.0.1",True)
        event_id="trial-pine-forest-car-circuit"
        host.applied("lan.configure",{"eventId":event_id,"classId":"car"})
        host.applied("lan.vehicle",{"vehicleId":"car-01"})
        network.wait_state(guest,lambda s:((s["network"].get("descriptor") or {}).get("eventId"))==event_id,30,"guest course selection")
        accepted,_=guest.command("lan.vehicle",{"vehicleId":"car-01"})
        assert accepted["status"]=="accepted";assert network.wait_terminal(guest,accepted["commandId"],30)["status"]=="applied"
        host.applied("lan.ready",{"ready":True})
        accepted,_=guest.command("lan.ready",{"ready":True})
        assert accepted["status"]=="accepted";assert network.wait_terminal(guest,accepted["commandId"],30)["status"]=="applied"
        host.applied("lan.start")
        for name,_,client,_ in entries:
            network.wait_state(client,lambda s:s["network"]["phase"]=="racing" and s.get("networkRace") is not None,45,name+" first authoritative race snapshot")
        report["checks"].append("Both endpoints received the long-course race snapshot")
        for name,_,client,run_dir in entries:
            driver=TourDriver(client,course,run_dir,remote=name=="guest",lan=True)
            drivers.append(driver);network._LAN_DRIVERS.append(driver)
        started=time.monotonic()
        network.wait_state(host,lambda s:s.get("networkRace",{}).get("elapsedTicks",0)>=1800,120,"first minute")
        host.applied("pause.toggle")
        for name,_,client,_ in entries:
            network.wait_state(client,lambda s:s["network"]["paused"],20,name+" shared pause")
        paused=host.state["networkRace"]["elapsedTicks"];old_tick=host.last_state_tick
        host.wait(lambda e:e.get("type") in loop.BOUNDARY_TYPES and host.last_state_tick>=old_tick+30,timeout=15,on_event=lambda e:network.pump_drivers())
        assert host.state["networkRace"]["elapsedTicks"]==paused
        screenshot(host,"shared-pause",str(entries[0][3]));host.applied("pause.toggle")
        report["checks"].append("Shared pause freezes the authoritative race clock and resumes")
        for name,_,client,run_dir in entries:
            network.wait_state(client,lambda s:s["network"]["phase"]=="results" and (s.get("networkRace") or {}).get("result") is not None,850,name+" journey result")
            screenshot(client,name+"-journey-result",str(run_dir))
        a,humans=validate_results(host.state["networkRace"],guest.state["networkRace"],event_id,course)
        report["checks"].append("Both endpoints received the same finished race and human results on the selected course")
        rewards=saved_rewards(root,a["resultId"])
        report["checks"].append("Both profiles saved one reward receipt for this result")
        report.update(status="passed",result=a,humanSeconds=[r["finishTicks"]/30 for r in humans],
                      rewards=rewards,maxSpeedKmh=[d.max_speed for d in drivers],recoveries=[d.recoveries for d in drivers],wallSeconds=time.monotonic()-started)
    except Exception as error:
        report["error"]=repr(error);raise
    finally:
        (root/"result.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
        network._LAN_DRIVERS[:]=[d for d in network._LAN_DRIVERS if d not in drivers]
        for _,process,client,_ in entries:
            client.close()
            if process.poll() is None:
                subprocess.run(["taskkill","/pid",str(process.pid)],capture_output=True)
                try:process.wait(timeout=15)
                except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=15)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player",type=Path,default=ROOT/"Builds/Windows-LongRaces/FamilyRacing.exe")
    parser.add_argument("--evidence",type=Path,required=True)
    run(parser.parse_args())
