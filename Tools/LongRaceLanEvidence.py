"""Audit final LAN recordings and persisted rewards without rerunning a race."""
import argparse
import json
from pathlib import Path

from LongRaceLanScenario import saved_rewards, validate_results


def final_state(path):
    # Finalization occurs near EOF. Read a bounded suffix instead of loading
    # hundreds of MB of input and checkpoint observations into memory.
    with path.open("rb") as stream:
        stream.seek(max(0, path.stat().st_size - 64 * 1024 * 1024))
        lines = stream.read().splitlines()
    race = None
    phase = None
    for line in reversed(lines[1:]):
        try:
            event = json.loads(line)
        except (ValueError, UnicodeDecodeError):
            continue
        changed = event.get("changed") or {}
        network = changed.get("network") or {}
        if phase is None and "phase" in network:
            phase = network["phase"]
        candidate = changed.get("networkRace") or {}
        if race is None and candidate.get("phase") == "finished" and candidate.get("result"):
            race = candidate
        if race is not None and phase is not None:
            break
    assert phase == "results" and race is not None, "Missing final session/race observation"
    return race


def audit(root):
    races = [final_state(root / (name + "-run") / "state.ndjson") for name in ("host", "guest")]
    result, humans = validate_results(*races, "trial-pine-forest-car-circuit", "pine-lake-journey-v1")
    rewards = saved_rewards(root, result["resultId"])
    live = json.loads((root / "result.json").read_text(encoding="utf-8"))
    assert "Shared pause freezes the authoritative race clock and resumes" in live["checks"]
    for name in ("host", "guest"):
        assert (root / (name + "-run") / "screenshots" / ("screenshot-" + name + "-journey-result.png")).is_file()
    report = {
        "status": "passed",
        "method": "Audit of actual Windows endpoint recordings and persisted profiles",
        "liveHarnessStatus": live["status"],
        "liveHarnessCorrection": "Format-1 LAN results leave courseId empty; validate each authoritative racer session's courseId instead.",
        "checks": live["checks"] + [
            "Both endpoints reached results and received matching finished race identity, event and standings",
            "Every racer session used pine-lake-journey-v1",
            "Both human drivers finished and saved exactly one reward receipt",
            "Both result screenshots exist",
        ],
        "result": result,
        "humanSeconds": [r["finishTicks"] / 30 for r in humans],
        "lastRecoveryReasons": [[r["session"].get("lastRecoveryReason", "") for r in race["racers"] if r["attribution"] == "human"] for race in races],
        "rewards": rewards,
    }
    (root / "verified-result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    report = audit(parser.parse_args().evidence.resolve())
    print(json.dumps({key: report[key] for key in ("status", "humanSeconds", "lastRecoveryReasons")}))
