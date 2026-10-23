"""Drive a Grand Tour route with normal input holds and save real-player evidence."""
import argparse
import bisect
import json
import math
import subprocess
import time
from pathlib import Path

import NetworkScenario as network
import GameplayLoopScenario as loop
from AgentScenario import BOUNDARY_TYPES
from DrivingScenario import screenshot

ROOT = Path(__file__).resolve().parents[1]
COURSES = ("coastal-grand-tour-v1", "pine-lake-journey-v1", "canyon-expedition-v1")


class TourDriver(loop.SoloDriver):
    """The established input driver with bounded projection for open roads."""
    MIN_SEND_TICKS = 4
    HOLD_TICKS = 12

    def __init__(self, peer, course, run_dir, remote=False, lan=False):
        data = json.loads((ROOT / "Assets/FamilyRacing/Generated/LongRaces/Courses" / (course + ".json")).read_text(encoding="utf-8"))
        self.points = [(p["x"], p["z"]) for p in data["aiRoute"]]
        self.open = data["isOpenRoute"]
        self.arc = [0.0]
        for a, b in zip(self.points, self.points[1:]):
            self.arc.append(self.arc[-1] + math.dist(a, b))
        self.total = self.arc[-1] + (0 if self.open else math.dist(self.points[-1], self.points[0]))
        network.LAN_ROUTE = self.points
        network.LAN_ROUTE_LENGTH = self.total
        network.LAN_ROUTE_ARC = self.arc
        network.LAN_GATES = [(g["center"]["x"], g["center"]["z"]) for g in data["gates"]]
        network.LAN_GATE_DIRS = [(g["direction"]["x"], g["direction"]["y"]) for g in data["gates"]]
        network.LAN_GATE_APPROACH = 12.0
        super().__init__(peer, remote=remote)
        self.lookahead = 24.0 if remote else 20.0
        self.gain = 1.6 if remote else 2.0
        self.run_dir = run_dir
        self.is_lan = lan
        self.max_speed = 0
        self.samples = []
        self.last_sample = -999
        self.shots = []
        self.progress_shots = set()

    def _racer(self):
        return network.LanDriver._racer(self) if self.is_lan else super()._racer()

    def _projection(self, motor):
        best = None
        segments = list(range(1, len(self.points)))
        if not self.open:
            segments.append(0)
        for index in segments:
            ax, az = self.points[index - 1]
            bx, bz = self.points[index]
            dx, dz = bx - ax, bz - az
            length2 = dx * dx + dz * dz
            if length2 < .001:
                continue
            t = max(0, min(1, ((motor["x"] - ax) * dx + (motor["z"] - az) * dz) / length2))
            d2 = (motor["x"] - ax - dx * t) ** 2 + (motor["z"] - az - dz * t) ** 2
            arc = (self.arc[index - 1] if index else self.arc[-1]) + t * math.sqrt(length2)
            if best is None or d2 < best[0]:
                best = (d2, arc, index)
        return best[1], best[2]

    def _point_along(self, arc):
        arc = max(0, min(self.total, arc)) if self.open else arc % self.total
        i = bisect.bisect_right(self.arc, arc)
        if i == len(self.points):
            if self.open:
                # Continue through the finish plane rather than steering back.
                a, b = self.points[-2:]
                dx, dz = b[0] - a[0], b[1] - a[1]
                length = math.hypot(dx, dz)
                return b[0] + dx / length * 12, b[1] + dz / length * 12
            a, b = self.points[-1], self.points[0]
            t = (arc - self.arc[-1]) / (self.total - self.arc[-1])
        else:
            a, b = self.points[i - 1], self.points[i]
            t = (arc - self.arc[i - 1]) / (self.arc[i] - self.arc[i - 1])
        return a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t

    def _corner_deflection(self, vertex_index):
        if self.open and vertex_index in (0, len(self.points) - 1):
            return 0
        return super()._corner_deflection(vertex_index)

    def _send(self, steering, boost=False, recover=False):
        racer=self._racer()
        motor=((racer or {}).get("session") or {}).get("motor") or {}
        # Once engaged, hold boost for its authored duration rather than
        # releasing immediately when charge dips below the activation threshold.
        sustain=motor.get("boostActive") and abs(steering)<=network.LAN_BOOST_MAX_STEER
        super()._send(steering,boost=bool(boost or sustain),recover=recover)

    def pump(self):
        racer = self._racer()
        session = (racer or {}).get("session") or {}
        motor = session.get("motor") or {}
        self.max_speed = max(self.max_speed, (motor.get("speed") or 0) * 3.6)
        tick = session.get("elapsedTicks") or 0
        if motor and tick - self.last_sample >= 150:
            if session.get("phase")=="running" and tick>=30:
                camera=(self.peer.state.get("presentation") or {}).get("camera") or {}
                assert camera.get("mode") in ("chase","overhead"), "Running race has no active follow camera"
                assert abs(camera.get("px",0)-motor["x"])<150, "Camera follows another course"
            self.last_sample = tick
            arc, _ = self._projection(motor)
            if not self.open and not session.get("lap") and session.get("nextCheckpoint")==1 and arc>self.total*.75:
                arc=0
            progress = session.get("routeProgress", 0) if self.open else ((session.get("lap") or 0) + arc / self.total) / session.get("lapsRequired", 1)
            row = {"seconds": round(tick / 30, 2), "progress": round(progress, 4),
                   "speedKmh": round((motor.get("speed") or 0) * 3.6, 2),
                   "nextCheckpoint": session.get("nextCheckpoint"), "x": motor.get("x"), "y": motor.get("y"), "z": motor.get("z")}
            self.samples.append(row)
            (self.peer.evidence / "progress.json").write_text(json.dumps(row), encoding="utf-8")
            for threshold in (.01, .25, .5, .75, .95):
                if session.get("phase")=="running" and progress >= threshold and threshold not in self.progress_shots:
                    self.progress_shots.add(threshold)
                    self.peer.send({"type": "screenshot", "captureId": "route-%02d" % round(threshold * 100)})
        super().pump()


def run(args):
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    profile_dir = evidence / "profiles"
    process, connection, run_dir = network.launch(
        [str(args.player.resolve()), "-force-d3d11", "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720", "--render-profile", "pc",
         "--perf-report-dir", str(evidence / "perf"), "--validation-trace-mb", "2048"], evidence, "primary", profile_dir, 120)
    client = None
    report = {"status": "failed", "course": args.course, "vehicle": args.vehicle, "language": args.language, "mode": args.mode}
    try:
        client = loop.LoopClient(connection, evidence)
        assert client.state["contentVersion"] == "grand-tour-1", client.state["contentVersion"]
        client.applied("profile.create", {"slot": 0, "language": args.language})
        client.applied("quickRace.open" if args.mode == "quickRace" else "timeTrial.open")
        screenshot(client, "route-selection", str(run_dir))
        cls = "motorcycle" if args.vehicle.startswith("motorcycle") else "car"
        parameters={"courseId":args.course,"classId":cls}
        if args.mode=="quickRace":parameters.update(vehicleId=args.vehicle,difficulty="easy")
        client.applied(args.mode + ".start", parameters)
        driver = TourDriver(client, args.course, run_dir)
        loop._SOLO_DRIVERS.append(driver)
        if args.lifecycle:
            loop.wait_loop_state(client,lambda s:(loop.solo_session(s) or {}).get("elapsedTicks",0)>=180,30,"lifecycle drive")
            driver.suspended=True;client.applied("pause.toggle")
            loop.wait_loop_state(client,lambda s:s.get("paused"),20,"paused")
            frozen=loop.solo_session(client.state)["simulationTick"];boundary=client.last_state_tick
            client.wait(lambda e:e.get("type") in BOUNDARY_TYPES and client.last_state_tick>=boundary+30,timeout=15)
            assert loop.solo_session(client.state)["simulationTick"]==frozen
            screenshot(client,"paused",str(run_dir));client.applied("pause.toggle")
            recovered=len(client.events_of("race.recovered"))
            client.applied("input.set",network.lan_inputs(recover=True,duration=1))
            loop.wait_loop_state(client,lambda s:len(client.events_of("race.recovered"))>recovered,20,"manual recovery")
            client.applied("camera.toggle");screenshot(client,"overhead",str(run_dir));client.applied("camera.toggle")
            before_id=loop.solo_session(client.state)["raceId"]
            client.applied("race.restart")
            loop.wait_loop_state(client,lambda s:(loop.solo_session(s) or {}).get("raceId")!=before_id,30,"restart creates a new race")
            driver.suspended=False;driver.last_sample=-999;driver.progress_shots.clear()
            report["lifecycle"]="pause clock freeze, resume, recovery, both cameras, restart passed"
        started = time.monotonic()

        def observe(event):
            driver.pump()
            if event.get("type") == "screenshot":
                assert event.get("status") == "captured", event
                driver.shots.append(event)

        def done(event):
            if event.get("type") not in BOUNDARY_TYPES:
                return False
            solo = client.state.get("soloRace") or {}
            if args.smoke_seconds and time.monotonic() - started > args.smoke_seconds:
                return True
            return solo.get("result") is not None or ((solo.get("trial") or {}).get("finished") is True)

        client.wait(done, timeout=1000, on_event=observe)
        loop._SOLO_DRIVERS.remove(driver)
        solo = client.state.get("soloRace") or {}
        result = solo.get("result") or (solo.get("race") or {}).get("result")
        report.update(maxSpeedKmh=driver.max_speed, recoveries=driver.recoveries, samples=driver.samples, screenshots=driver.shots,
                      wallSeconds=round(time.monotonic() - started, 2), result=result)
        session = loop.solo_session(client.state)
        if not args.smoke_seconds:
            if result:
                own = next(r for r in result["racers"] if r.get("participantId") == solo["participantId"])
                assert own.get("outcome") == "finished", own
                report["raceSeconds"] = own["finishTicks"] / 30
            else:
                assert session and session.get("finished"), session
                report["raceSeconds"] = session["finishTicks"] / 30
            assert 300 <= report["raceSeconds"] <= 480, "Race duration outside 5–8 minute target: " + str(report["raceSeconds"])
        screenshot(client, "finished" if not args.smoke_seconds else "smoke", str(run_dir))
        report["status"] = "passed"
    except Exception as error:
        report["error"] = repr(error)
        raise
    finally:
        if client:
            report["performance"] = (client.state.get("presentation") or {}).get("performance")
        (evidence / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        if client:
            loop._SOLO_DRIVERS[:] = [d for d in loop._SOLO_DRIVERS if d.peer is not client]
            client.close()
        if process.poll() is None:
            subprocess.run(["taskkill", "/pid", str(process.pid)], capture_output=True)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=15)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=Path, default=ROOT / "Builds/Windows-LongRaces/FamilyRacing.exe")
    parser.add_argument("--course", choices=COURSES, required=True)
    parser.add_argument("--vehicle", default="car-01")
    parser.add_argument("--language", choices=("en", "he"), default="en")
    parser.add_argument("--mode", choices=("quickRace", "timeTrial"), default="quickRace")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--smoke-seconds", type=int, default=0)
    parser.add_argument("--lifecycle", action="store_true")
    run(parser.parse_args())
