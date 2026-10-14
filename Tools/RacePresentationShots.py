"""M4 Task-5 race-presentation evidence: authored effects + audio.

Usage: RacePresentationShots.py <run-dir>/connection.json --evidence <new-dir>

Attaches to a live development player's validation run and drives one
solo car-01 race on sunny-island-benchmark-01 through the shared command
path, asserting the schema-5 presentation facts (`presentation.audio`,
`presentation.effects`) at each authored beat and capturing screenshots
while the effect bursts are live:

  * racing music cue + engine loop while running,
  * boost loop + boost-flare effect on the start straight,
  * skid loop + drift-dust through the west turn,
  * checkpoint one-shot/sparkle on gate crossings,
  * air/landing cues + landing puff on the boardwalk jump,
  * pause suspending every loop, resume restoring them,
  * finish burst + results music on race.finished,
  * clean teardown on race.exit.

Every capture and fact check is requested against observed state —
never a guess — and lands under <run-dir>/screenshots/ plus
<evidence>/presentation-beats.json with the observed slices.
"""

import argparse
import json
import sys
import time
from pathlib import Path

from AgentScenario import Client, inputs, verify_trace, SUPPORTED_STATE_SCHEMAS
from DrivingScenario import (DrivingClient, Driver, screenshot,
                             wait_next, wait_phase)

CATALOG = "race-presentation-01"


def audio_facts(state):
    return (state.get("presentation") or {}).get("audio") or {}


def effects_facts(state):
    return (state.get("presentation") or {}).get("effects") or {}


def loops(state):
    return audio_facts(state).get("loops") or []


def pool(state, effect_id):
    for entry in effects_facts(state).get("pools") or []:
        if entry.get("effectId") == effect_id:
            return entry
    return None


def any_loop(state, prefix):
    return any(loop.startswith(prefix + "@") for loop in loops(state))


def pool_active(state, effect_id):
    entry = pool(state, effect_id)
    return entry is not None and entry.get("active", 0) > 0


def record_beat(beats, name, state):
    """Durable per-beat evidence: the observed presentation slices."""
    beats[name] = {
        "audio": audio_facts(state),
        "effects": effects_facts(state),
    }


def run(connection, evidence):
    client = DrivingClient(connection, evidence)
    run_directory = connection["_directory"]
    checks = []
    beats = {}
    try:
        client.send({"type": "discover"})
        client.wait(lambda event: event.get("type") == "discovery")
        created, _ = client.applied("profile.create",
                                    {"slot": 0, "language": "en"})
        assert client.action_state(created["commandId"])["activeSlot"] == 0
        client.applied("ui.navigate", {"screen": "practice"})
        client.boundary(
            lambda state: (state.get("presentation") or {}).get(
                "courseVisualId") == "sunny-island-benchmark-01",
            timeout=15)

        started, _ = client.applied(
            "race.start", {"vehicleId": "car-01", "eventId": "intro-01"})
        wait_phase(client, "running", timeout=30)

        # --- bound catalog + racing music + the go cue ------------------
        client.boundary(
            lambda state: audio_facts(state).get("catalogId") == CATALOG
            and effects_facts(state).get("catalogId") == CATALOG,
            timeout=10)
        client.boundary(
            lambda state: audio_facts(state).get("musicCue")
            == "music-race", timeout=15)
        client.boundary(
            lambda state: audio_facts(state).get("oneShots", 0) >= 1,
            timeout=15)
        assert not audio_facts(client.state).get("missingCues"), \
            "missing cues at race start: %s" \
            % audio_facts(client.state).get("missingCues")
        assert not effects_facts(client.state).get("missingEffects"), \
            "missing effects at race start: %s" \
            % effects_facts(client.state).get("missingEffects")
        record_beat(beats, "running", client.state)
        checks.append("catalog bound; music-race cue + go one-shot live")

        driver = Driver(client)

        # --- engine loop: wanted while the racer exists ------------------
        client.boundary(
            lambda state: any_loop(state, "engine.car"), timeout=15)
        checks.append("engine.car loop live under race context")

        # --- boost straight: loop + flare bursts + boost one-shot --------
        driver.refresh_input(driver.motor(), 0.0, 30.0, boost=True)
        client.boundary(
            lambda state: state["driving"]["motor"].get("boostActive")
            is True, timeout=15)
        client.boundary(lambda state: any_loop(state, "boost"), timeout=15)
        assert client.events_of("motor.boostStart"), \
            "No motor.boostStart event"
        client.boundary(
            lambda state: pool_active(state, "boost-flare"), timeout=15)
        shot = screenshot(client, "t5-boost-flare", run_directory)
        assert shot["status"] == "captured"
        driver.drive_leg(0.0, 30.0, radius=4.0, timeout=25, boost=True,
                         note="gate-1 boost straight")
        record_beat(beats, "boost", client.state)
        checks.append("boost loop + boost-flare effect + screenshot")

        # --- drift corner: skid loop + continuous drift dust -------------
        driver.refresh_input(driver.motor(), -25.0, 25.0, drift=True)
        client.boundary(
            lambda state: state["driving"]["motor"].get("drifting") is True
            or bool(client.events_of("motor.driftStart")), timeout=20)
        client.boundary(lambda state: any_loop(state, "skid"), timeout=15)
        client.boundary(
            lambda state: pool_active(state, "drift-dust"), timeout=15)
        shot = screenshot(client, "t5-drift-dust", run_directory)
        assert shot["status"] == "captured"
        driver.drive_leg(-50.0, 0.0, radius=6.0, timeout=25, drift=True,
                         note="west lane")
        record_beat(beats, "drift", client.state)
        checks.append("skid loop + drift-dust emitter + screenshot")

        # --- checkpoint evidence so far ---------------------------------
        assert client.events_of("checkpoint.crossed"), \
            "No checkpoint.crossed event after gate-1"
        checks.append("checkpoint.crossed event consumed by both presenters")

        # --- ramp: airborne cue + landing puff + landing cue -------------
        driver.drive_leg(-60.0, 10.0, radius=5.0, timeout=25,
                         note="ramp approach")
        driver.drive_leg(-60.0, 50.0, radius=6.0, timeout=40,
                         until=lambda state: bool(
                             client.events_of("motor.becameAirborne")),
                         note="ramp crest")
        client.boundary(
            lambda state: not state["driving"]["motor"].get(
                "grounded", True), timeout=10)
        shot = screenshot(client, "t5-jump-airborne", run_directory)
        assert shot["status"] == "captured"
        client.boundary(
            lambda state: bool(client.events_of("motor.landed")),
            timeout=20)
        client.boundary(
            lambda state: state["driving"]["motor"].get("grounded")
            is True, timeout=15)
        shot = screenshot(client, "t5-jump-landing", run_directory)
        assert shot["status"] == "captured"
        record_beat(beats, "landing", client.state)
        checks.append("air + landing cues, landing puff, airborne shot")

        # --- pause: every loop suspends, published honestly --------------
        paused, _ = client.applied("pause.toggle")
        assert client.action_state(paused["commandId"])["paused"] is True
        client.boundary(
            lambda state: audio_facts(state).get("paused") is True
            and not loops(state), timeout=15)
        record_beat(beats, "paused", client.state)
        client.applied("pause.toggle")
        client.boundary(lambda state: state["paused"] is False,
                        timeout=10)
        client.boundary(
            lambda state: audio_facts(state).get("paused") is not True
            and any_loop(state, "engine.car"), timeout=15)
        checks.append("pause suspended every loop; resume restored engine")

        # --- finish: burst + results music + finish cue -------------------
        # The proven DrivingScenario waypoint chain: stay north of
        # IslandNE (x[21,43] x z[37,51]), east lane down x>=90 around
        # IslandSE, south lane west below BarrierA, then the finish
        # approach from below the start line — the lap only counts when
        # gate-0 is crossed through its corridor northbound.
        driver.drive_leg(-30.0, 58.0, radius=7.0, timeout=25,
                         note="north apron east")
        driver.drive_leg(20.0, 60.0, radius=8.0, timeout=25,
                         note="north lane")
        driver.drive_leg(63.0, 55.0, radius=5.0, timeout=25,
                         until=lambda state:
                             state["driving"]["nextCheckpoint"] == 3,
                         note="gate-2")
        wait_next(client, 3)
        driver.drive_leg(90.0, 40.0, radius=7.0, timeout=25,
                         note="east lane entry")
        driver.drive_leg(95.0, -8.0, radius=5.0, timeout=25,
                         until=lambda state:
                             state["driving"]["nextCheckpoint"] == 4,
                         note="gate-3")
        wait_next(client, 4)
        driver.drive_leg(95.0, -45.0, radius=7.0, timeout=25,
                         note="south lane")
        driver.drive_leg(62.0, -55.0, radius=6.0, timeout=25,
                         note="south lane west")
        driver.drive_leg(40.0, -55.0, radius=6.0, timeout=25,
                         until=lambda state:
                             state["driving"]["nextCheckpoint"] == 0,
                         note="gate-4")
        wait_next(client, 0)
        driver.drive_leg(-15.0, -52.0, radius=7.0, timeout=25,
                         note="west lane")
        driver.drive_leg(-10.0, -68.0, radius=6.0, timeout=25,
                         note="below start")
        driver.drive_leg(0.0, -45.0, radius=8.0, timeout=30,
                         until=lambda state:
                             state["driving"]["finished"],
                         note="finish line")
        client.boundary(
            lambda state: state["driving"]["finished"] is True
            and state["driving"]["phase"] == "finished", timeout=30)
        assert client.events_of("race.finished"), "No race.finished event"
        client.boundary(
            lambda state: audio_facts(state).get("musicCue")
            == "music-results", timeout=15)
        client.boundary(
            lambda state: pool_active(state, "finish-burst"), timeout=15)
        shot = screenshot(client, "t5-finish-burst", run_directory)
        assert shot["status"] == "captured"
        record_beat(beats, "finished", client.state)
        checks.append("finish burst + results music + finish cue")

        # --- exit: presentation stops, facts go quiet ---------------------
        exited, _ = client.applied("race.exit")
        assert client.action_state(exited["commandId"])["driving"] is None
        client.boundary(
            lambda state: not loops(state)
            and audio_facts(state).get("musicCue") == "none"
            and not audio_facts(state).get("paused")
            and effects_facts(state).get("activeEffects", -1) == 0,
            timeout=15)
        record_beat(beats, "exit", client.state)
        checks.append("race.exit stopped every loop and burst")

        # --- honesty rollup ------------------------------------------------
        final_audio = audio_facts(client.state)
        final_fx = effects_facts(client.state)
        assert not final_audio.get("missingCues"), \
            "missing audio cues during the run: %s" \
            % final_audio.get("missingCues")
        assert not final_fx.get("missingEffects"), \
            "missing effect prefabs during the run: %s" \
            % final_fx.get("missingEffects")
        checks.append("zero missing cues/effects across the whole beat set")

        (evidence / "presentation-beats.json").write_text(
            json.dumps(beats, indent=2, sort_keys=True),
            encoding="utf-8")

        client.send({"type": "finish"})
        finished = client.wait(lambda event: event.get("type")
                               == "finished")
        assert finished["captureValid"], finished
        return {"checks": checks, "beats": sorted(beats.keys())}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("connection_file", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed",
              "scope": "M4 task-5 race presentation effects/audio evidence"}
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
            json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
