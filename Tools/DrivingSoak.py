"""Real-time graybox driving soak against a validation build.

Usage: DrivingSoak.py <player.exe> --evidence <new-dir> [--minutes 30]

Launches a fresh Windows validation player, attaches the strict schema-2
client, and drives the authored intro-01 course for a sustained wall-clock
session — both vehicle classes, both cameras, and repeated race.restart
identities — while continuously sampling process memory, trace growth,
sim-step cadence at the socket, outgoing-queue health, backlog and
step-overload telemetry. Everything is measured, nothing projected: the
result reports actual duration, per-vehicle laps, restart and incident
counts, the boundary inter-arrival distribution (p50/p95/p99/max) as the
frame-pacing proxy a socket client can honestly observe, and process
memory counters taken through the OS.

Failures inside a lap (a stall the bounded recovery could not clear, a
missed waypoint) are recorded as incidents and the race is restarted —
one bad lap must not fake a whole soak, and the incident count is part
of the honest result.
"""

import argparse
import ctypes
from ctypes import wintypes
import json
import math
import subprocess
import sys
import threading
import time
from pathlib import Path

from AgentScenario import BOUNDARY_TYPES, inputs, verify_trace
from DrivingScenario import DrivingClient, Driver, wait_phase, overloads, \
    IDENTITY_CAPACITY, OUTGOING_CAPACITY, TRACE_CAPACITY_BYTES, \
    MAX_STEPS_PER_FRAME
from RestartScenario import wait_connection, file_hash


SAMPLE_SECONDS = 5.0
CAMERA_TOGGLE_EVERY_LAP = True


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


def process_memory(pid):
    """Working-set and private-byte counters for `pid`, or None."""
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        counters = PROCESS_MEMORY_COUNTERS_EX()
        counters.cb = ctypes.sizeof(counters)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(
                handle, ctypes.byref(counters), counters.cb):
            return None
        return {"workingSetBytes": counters.WorkingSetSize,
                "privateBytes": counters.PrivateUsage,
                "peakWorkingSetBytes": counters.PeakWorkingSetSize}
    finally:
        kernel32.CloseHandle(handle)


class SoakClient(DrivingClient):
    """DrivingClient plus boundary wall-clock arrival times."""

    def __init__(self, connection, evidence):
        self.boundary_times = []   # (monotonic, sequence, observed tick)
        super().__init__(connection, evidence)

    def read(self, remaining):
        event = super().read(remaining)
        if event.get("type") in BOUNDARY_TYPES and self.state is not None:
            self.boundary_times.append(
                (time.monotonic(), event.get("sequence"),
                 self.state.get("tick")))
        return event


def percentile(sorted_values, fraction):
    if not sorted_values:
        return None
    index = min(len(sorted_values) - 1,
                int(math.ceil(fraction * len(sorted_values))) - 1)
    return sorted_values[index]


def cadence_stats(boundary_times):
    """Inter-arrival stats of tick-advancing boundaries (sim-step cadence).

    EmitTick fires once per simulated step inside the frame step loop, so a
    healthy 30 Hz stream shows ~33 ms gaps; catch-up batches arrive nearly
    back-to-back and stalls show as long gaps. This is a socket-observed
    pacing proxy, not render frame times.
    """
    gaps = []
    bursts = []
    last_tick_time = None
    last_tick = None
    batch = 0
    for stamp, _sequence, tick in boundary_times:
        if tick is None or tick == last_tick:
            continue  # command boundary at an unchanged tick
        if last_tick_time is not None:
            gap_ms = (stamp - last_tick_time) * 1000.0
            if gap_ms < 4.0:      # same frame burst (catch-up batch)
                batch += 1
            else:
                if batch:
                    bursts.append(batch)
                    batch = 0
                gaps.append(gap_ms)
        last_tick_time = stamp
        last_tick = tick
    ordered = sorted(gaps)
    return {
        "tickBoundaries": len(gaps),
        "gapMsP50": percentile(ordered, 0.50),
        "gapMsP95": percentile(ordered, 0.95),
        "gapMsP99": percentile(ordered, 0.99),
        "gapMsMax": ordered[-1] if ordered else None,
        "gapsOver100ms": sum(1 for gap in ordered if gap > 100.0),
        "catchUpBatches": len(bursts),
        "largestCatchUpBatch": max(bursts) if bursts else 0,
    }


def drive_soak_leg(client, driver, incidents, tag, *args, **kwargs):
    """drive_leg that records a failed leg and presses recover instead of
    aborting the soak."""
    try:
        driver.drive_leg(*args, **kwargs)
        return True
    except (AssertionError, KeyError, TypeError) as error:
        incidents.append({"note": tag, "error": str(error)[:200],
                          "atTick": client.state.get("tick")})
        try:
            client.applied("input.set", inputs(
                steering=0.0, duration=5, recover=True))
            client.boundary(
                lambda state: state.get("driving")
                and state["driving"]["motor"].get("recovering") is True,
                timeout=10)
        except Exception:
            pass
        return False


def drive_soak_lap(client, driver, incidents, label):
    """One authored intro-01 lap: same real geometry as the qualification
    route — boost straight, drift corner, landmark ramp, island lanes,
    latched-recovery-free finish — with soft failure handling."""
    wait_phase(client, "running", timeout=30)

    driver.refresh_input(driver.motor(), 0.0, 30.0, boost=True)
    drive_soak_leg(client, driver, incidents, label + "boost leg",
                   0.0, 30.0, radius=4.0, timeout=20, boost=True,
                   until=lambda s: s["driving"]["nextCheckpoint"] == 2,
                   note="gate-1")
    drive_soak_leg(client, driver, incidents, label + "drift corner",
                   -25.0, 25.0, radius=6.0, timeout=20, drift=True,
                   note="drift corner")
    drive_soak_leg(client, driver, incidents, label + "west lane",
                   -50.0, 0.0, radius=6.0, timeout=20,
                   note="west lane below BarrierB")
    drive_soak_leg(client, driver, incidents, label + "ramp approach",
                   -60.0, 10.0, radius=5.0, timeout=20, note="ramp approach")
    drive_soak_leg(client, driver, incidents, label + "ramp crest",
                   -60.0, 50.0, radius=6.0, timeout=40,
                   until=lambda s: bool(client.events_of("motor.landed")),
                   note="ramp crest")
    drive_soak_leg(client, driver, incidents, label + "north apron",
                   -30.0, 58.0, radius=7.0, timeout=25, note="north apron east")
    drive_soak_leg(client, driver, incidents, label + "north lane",
                   20.0, 60.0, radius=8.0, timeout=25, note="north lane")
    drive_soak_leg(client, driver, incidents, label + "gate-2",
                   63.0, 55.0, radius=5.0, timeout=25,
                   until=lambda s: s["driving"]["nextCheckpoint"] == 3,
                   note="gate-2")
    drive_soak_leg(client, driver, incidents, label + "east lane",
                   90.0, 40.0, radius=7.0, timeout=25, note="east lane entry")
    drive_soak_leg(client, driver, incidents, label + "gate-3",
                   95.0, -8.0, radius=5.0, timeout=25,
                   until=lambda s: s["driving"]["nextCheckpoint"] == 4,
                   note="gate-3")
    drive_soak_leg(client, driver, incidents, label + "south lane",
                   95.0, -45.0, radius=7.0, timeout=25, note="south lane")
    drive_soak_leg(client, driver, incidents, label + "south lane west",
                   62.0, -55.0, radius=6.0, timeout=25, note="south lane west")
    drive_soak_leg(client, driver, incidents, label + "gate-4",
                   40.0, -55.0, radius=6.0, timeout=25,
                   until=lambda s: s["driving"]["nextCheckpoint"] == 0,
                   note="gate-4")
    drive_soak_leg(client, driver, incidents, label + "west lane",
                   -15.0, -52.0, radius=7.0, timeout=25, note="west lane")
    drive_soak_leg(client, driver, incidents, label + "below start",
                   -10.0, -68.0, radius=6.0, timeout=25, note="below start")
    drive_soak_leg(client, driver, incidents, label + "finish",
                   0.0, -45.0, radius=8.0, timeout=25,
                   until=lambda s: s["driving"]["finished"],
                   note="finish line")


def soak_race(client, vehicle, laps, incidents, camera_toggles):
    """One race: `laps` authored laps, a camera toggle per lap."""
    started, _ = client.applied(
        "race.start", {"vehicleId": vehicle, "eventId": "intro-01"})
    state = client.action_state(started["commandId"])
    driving = state["driving"]
    assert driving["vehicleId"] == vehicle and driving["phase"] == "countdown"
    wait_phase(client, "running", timeout=30)
    driver = Driver(client)
    completed = 0
    for lap in range(laps):
        label = "%s lap %d: " % (vehicle, lap + 1)
        before = client.state["driving"]["finished"] if client.state.get(
            "driving") else False
        try:
            drive_soak_lap(client, driver, incidents, label)
        except (AssertionError, KeyError, TypeError) as error:
            incidents.append({"note": label + "lap aborted",
                              "error": str(error)[:200],
                              "atTick": client.state.get("tick")})
        if client.state.get("driving") and client.state["driving"].get(
                "finished") and not before:
            completed += 1
        if CAMERA_TOGGLE_EVERY_LAP:
            try:
                mode = client.state["driving"]["camera"]["mode"]
                client.applied("camera.toggle")
                client.boundary(
                    lambda s: s["driving"]["camera"]["mode"] != mode,
                    timeout=10)
                camera_toggles.append(
                    {"lap": lap + 1, "from": mode,
                     "to": client.state["driving"]["camera"]["mode"]})
            except Exception as error:
                incidents.append({"note": label + "camera toggle",
                                  "error": str(error)[:200],
                                  "atTick": client.state.get("tick")})
        if lap + 1 < laps:
            try:
                restarted, _ = client.applied("race.restart")
                assert client.action_state(
                    restarted["commandId"])["driving"]["raceId"] != driving[
                        "raceId"]
                wait_phase(client, "running", timeout=30)
            except (AssertionError, KeyError, TypeError) as error:
                incidents.append({"note": label + "restart",
                                  "error": str(error)[:200],
                                  "atTick": client.state.get("tick")})
                break
    exited, _ = client.applied("race.exit")
    assert client.action_state(exited["commandId"])["driving"] is None
    return completed


def run(player_command, evidence, minutes):
    root = Path(evidence).absolute()
    run_dir = root / "run"
    client_evidence = root / "client"
    client_evidence.mkdir(parents=True)
    result = {
        "status": "failed",
        "scope": "sustained graybox driving soak (both vehicles, both "
                 "cameras, repeated restarts) with measured memory/cadence",
        "minutesRequested": minutes,
        "executable": str(player_command),
        "runDirectory": str(run_dir),
    }
    process = None
    client = None
    stop_sampling = threading.Event()
    samples = []
    try:
        executable = Path(player_command[0]).resolve(strict=True)
        result["executableSha256"] = file_hash(executable)
        command = [str(executable), "--validation-dir", str(run_dir),
                   "-logFile", str(root / "player.log")]
        with (root / "process.log").open("wb") as output:
            process = subprocess.Popen(
                command, stdout=output, stderr=subprocess.STDOUT)
        result["pid"] = process.pid
        connection = wait_connection(process, run_dir, timeout=60)
        result["runId"] = connection["runId"]
        connection["_directory"] = str(run_dir)
        client = SoakClient(connection, client_evidence)
        start_wall = time.monotonic()
        deadline = start_wall + minutes * 60.0
        trace_path = run_dir / "state.ndjson"

        def sampler():
            while not stop_sampling.is_set():
                memory = process_memory(process.pid)
                sample = {
                    "elapsedSeconds": round(time.monotonic() - start_wall, 1),
                    "tick": client.state.get("tick") if client.state else None,
                    "traceBytes": trace_path.stat().st_size
                    if trace_path.is_file() else 0,
                    "boundaries": client.boundaries,
                }
                if memory:
                    sample.update(memory)
                presentation = (client.state or {}).get("presentation") or {}
                sample["backlogSteps"] = presentation.get("backlogSteps") or 0
                sample["stepOverloads"] = (
                    presentation.get("stepOverloads") or 0)
                samples.append(sample)
                stop_sampling.wait(SAMPLE_SECONDS)

        thread = threading.Thread(target=sampler, daemon=True)
        thread.start()

        assert client.state["activeSlot"] == -1
        client.applied("profile.create", {"slot": 0, "language": "en"})
        client.applied("ui.navigate", {"screen": "practice"})

        incidents = []
        camera_toggles = []
        vehicle_laps = {"car-01": 0, "motorcycle-01": 0}
        restarts = 0
        vehicles = ["car-01", "motorcycle-01"]
        index = 0
        while time.monotonic() < deadline:
            assert process.poll() is None, \
                "Player exited mid-soak (code %s)" % process.poll()
            remaining = deadline - time.monotonic()
            laps = 2 if remaining > 300 else 1
            vehicle = vehicles[index % 2]
            index += 1
            try:
                done = soak_race(client, vehicle, laps, incidents,
                                 camera_toggles)
                vehicle_laps[vehicle] += done
                restarts += max(0, laps - 1)
            except (AssertionError, KeyError, TypeError) as error:
                incidents.append({"note": vehicle + " race aborted",
                                  "error": str(error)[:200],
                                  "atTick": client.state.get("tick")})
                # An unrecoverable race exits honestly before the next one.
                try:
                    if client.state.get("driving"):
                        client.applied("race.exit")
                except Exception:
                    pass

        stop_sampling.set()
        thread.join(timeout=10)
        result["wallSeconds"] = round(time.monotonic() - start_wall, 1)

        client.send({"type": "health"})
        health = client.wait(lambda event: event.get("type") == "health")
        result["health"] = {
            "outgoingRecords": health["outgoingRecords"],
            "captureValid": health["captureValid"],
        }
        # The drivingEvents slice is a bounded 128-event observation
        # window: a 30-minute soak legitimately evicts older events. The
        # contract is marked accounting — `droppedDrivingEvents` must be
        # present and monotonic — not zero evictions; boundary history
        # and the capture stay complete either way.
        result["droppedDrivingEvents"] = client.state.get(
            "droppedDrivingEvents", 0)
        result["drivingEventsObserved"] = len(client.driving_events)
        result["drivingEventAccounting"] = {
            "window": "bounded 128-event observation slice",
            "droppedMarked": result["droppedDrivingEvents"],
            "disclosure": "evictions are counted on the durable state "
                          "marker; no event leaves state unaccounted",
        }
        assert "droppedDrivingEvents" in client.state, \
            "Driving-event eviction is not marked in state"
        result["maxBacklogSteps"] = client.max_backlog_steps
        result["presentation"] = {
            "stepOverloads": overloads(client.state),
            "inputSource": (client.state.get("presentation") or {}).get(
                "inputSource"),
            "screen": "%sx%s" % ((client.state.get("presentation") or {}).get(
                "screenWidth"), (client.state.get("presentation") or {}).get(
                    "screenHeight")),
        }
        result["cadence"] = cadence_stats(client.boundary_times)
        result["vehicleLaps"] = vehicle_laps
        result["lapsCompleted"] = sum(vehicle_laps.values())
        result["restarts"] = restarts
        result["cameraToggles"] = len(camera_toggles)
        result["cameraModesSeen"] = sorted(
            {entry["to"] for entry in camera_toggles})
        result["incidents"] = incidents
        result["incidentCount"] = len(incidents)
        result["commandIdentities"] = sum(
            1 for line in (client_evidence / "commands.ndjson").read_text(
                encoding="utf-8").splitlines()
            if json.loads(line).get("type") == "command")
        result["traceBytes"] = trace_path.stat().st_size
        result["boundaries"] = client.boundaries
        memory_samples = [s for s in samples if "workingSetBytes" in s]
        if memory_samples:
            result["memory"] = {
                "samples": len(memory_samples),
                "workingSetPeakBytes": max(
                    s["workingSetBytes"] for s in memory_samples),
                "privatePeakBytes": max(
                    s["privateBytes"] for s in memory_samples),
                "workingSetFirstBytes": memory_samples[0]["workingSetBytes"],
                "workingSetLastBytes": memory_samples[-1]["workingSetBytes"],
            }
        overload_growth = (samples[-1].get("stepOverloads", 0)
                           - samples[0].get("stepOverloads", 0)) \
            if len(samples) > 1 else 0
        result["stepOverloadsDuringSoak"] = overload_growth
        # Split the startup transient (first 60 s: JIT/content warm-up on
        # a development player) from steady state so a warm-up spike is
        # not read as sustained overload, and vice versa.
        startup = [s for s in samples if s["elapsedSeconds"] <= 60]
        steady = [s for s in samples if s["elapsedSeconds"] > 60]
        if steady:
            base = max((s.get("stepOverloads") or 0) for s in startup) \
                if startup else 0
            result["stepOverloadsSteadyState"] = max(
                0, (steady[-1].get("stepOverloads") or 0) - base)
            result["steadyStateMaxBacklog"] = max(
                (s.get("backlogSteps") or 0) for s in steady)

        client.send({"type": "finish"})
        finished = client.wait(lambda event: event.get("type") == "finished")
        assert finished["captureValid"], finished
        verify_trace(run_dir, connection)
        result["captureVerified"] = True

        # Hard gates: every eviction is marked (asserted above), the queue
        # never saturated, backlog always drained, the capture is intact,
        # and both vehicles/cameras actually ran.
        assert result["health"]["outgoingRecords"] < OUTGOING_CAPACITY
        # The all-time max includes the startup warm-up spike (a dev
        # player ~2.5 s behind during first content load, disclosed
        # above); the sustained-soak contract is steady-state: overloads
        # must not grow and sampled backlog must stay drained after the
        # warm-up window.
        assert result.get("stepOverloadsSteadyState", 0) == 0, \
            "Step overloads kept growing after the startup window"
        assert result.get("steadyStateMaxBacklog", 0) < MAX_STEPS_PER_FRAME, \
            "Backlog stayed saturated after the startup window"
        assert result["commandIdentities"] < IDENTITY_CAPACITY
        assert result["traceBytes"] < TRACE_CAPACITY_BYTES
        assert all(laps > 0 for laps in vehicle_laps.values()), \
            "A vehicle class never finished a lap: %s" % vehicle_laps
        assert set(result["cameraModesSeen"]) == {"chase", "overhead"}
        result["status"] = "passed"
    except Exception as error:
        result["error"] = type(error).__name__ + ": " + str(error)
    finally:
        stop_sampling.set()
        (root / "samples.json").write_text(
            json.dumps(samples, indent=1), encoding="utf-8")
        result["notVerified"] = [
            "render frame times — cadence is the socket-observed sim-step "
            "pacing proxy; GPU/render timing needs on-device tooling",
            "Android/A7 Lite device behavior — Windows development player only",
            "ordinary (non-development) build performance — structurally "
            "impossible for this harness: the soak is command-driven over "
            "--validation-dir, which is compiled OUT of ordinary builds "
            "(FoundationApp.cs validation arg parsing is UNITY_EDITOR/"
            "DEVELOPMENT_BUILD-only), so no agent can attach; comparing "
            "ordinary vs instrumented builds would need in-build scripted "
            "driving or manual play under the Unity profiler — this "
            "measures the instrumented development player only",
            "physical input, visual/Hebrew/audio quality, four-racer/tablet-host",
        ]
        try:
            if client is not None:
                client.close()
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
            if process is not None:
                result["playerExitCode"] = process.poll()
        (root / "result.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=2),
            encoding="utf-8")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("player", type=Path,
                        help="Windows development player executable")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="NEW directory; existing directories are refused")
    parser.add_argument("--minutes", type=float, default=30.0,
                        help="Sustained soak duration in minutes (default 30)")
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=False)
    if not __debug__:
        raise RuntimeError("Run without -O: scenario assertions must be enabled")
    result = run([str(args.player)], args.evidence, args.minutes)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
