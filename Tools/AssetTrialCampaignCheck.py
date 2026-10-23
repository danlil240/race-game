"""Real-player regression: Campaign opens, starts, finishes and saves progress."""
import argparse
import json
from pathlib import Path

from DrivingScenario import screenshot
from GameplayLoopScenario import (LoopClient, Checks, campaign_rows,
    drive_loop_race, expect_new_receipt, profile_snapshot, wait_loop_state)


def run(connection_path, evidence, finish=False, reload=False):
    evidence.mkdir(parents=True, exist_ok=False)
    connection = json.loads(connection_path.read_text(encoding="utf-8-sig"))
    run_dir = connection_path.resolve().parent
    connection["_directory"] = str(run_dir)
    result = {"status": "failed", "scope": "art trial campaign regression"}
    client = LoopClient(connection, evidence)
    checks = Checks()
    try:
        if reload:
            client.applied("profile.select", {"slot": 0})
        else:
            client.applied("profile.create", {"slot": 0, "language": "en"})
        assert (client.state.get("progression") or {}).get("available"), \
            "Campaign unavailable: build has no valid gameplay catalog"
        client.applied("campaign.open")
        rows = campaign_rows(client.state)
        unlocked = [r for r in rows if not r.get("locked") and not r.get("finale")]
        assert unlocked, "No playable campaign event"
        screenshot(client, "campaign-map", str(run_dir))
        event_id = unlocked[0]["eventId"]
        profile = profile_snapshot(client)
        if reload:
            assert profile["coins"] > 0, "Campaign wallet did not persist"
            assert profile.get("eventProgress"), "Campaign stars did not persist"
            result.update(status="passed", checks=["campaign progress persists across process restart"],
                          coins=profile["coins"], events=rows)
            return
        for row in rows:
            if row.get("locked"):
                rejected, _ = client.command("campaign.start", {"eventId": row["eventId"], "vehicleId": "car-01", "difficulty": "easy"})
                assert rejected["status"] == "rejected", "Locked event accepted"
                break
        client.applied("campaign.start", {"eventId": event_id, "vehicleId": "car-01", "difficulty": "easy"})
        wait_loop_state(client, lambda s: ((s.get("soloRace") or {}).get("race") or {}).get("phase") == "running", 30, "campaign race running")
        assert client.state["soloRace"]["mode"] == "campaign"
        screenshot(client, "campaign-race", str(run_dir))
        checks.passed("campaign.start", "Campaign menu and first race start on a fresh profile")
        if finish:
            race_result = drive_loop_race(client, checks, "first campaign race", finish_timeout=240)
            after = profile_snapshot(client)
            receipt = expect_new_receipt(profile, after, race_result, "campaign finish")
            assert after["coins"] > profile["coins"], "No campaign earnings"
            assert receipt.get("completionCoins") == 20, "Missing campaign completion reward"
            assert after.get("eventProgress"), "No campaign event progress"
            screenshot(client, "campaign-result", str(run_dir))
            checks.passed("campaign.reward", "Finalized result awards coins and saved stars once")
            result.update(awardedCoins=receipt["awardedCoins"], coins=after["coins"])
        client.applied("results.continue" if finish else "race.exit")
        if finish:
            assert client.state["screen"] == "campaign", "Continue did not return to campaign"
            screenshot(client, "campaign-progress", str(run_dir))
            for row in unlocked[1:]:
                vehicle = "motorcycle-01" if row["classId"] == "motorcycle" else "car-01"
                client.applied("campaign.start", {"eventId": row["eventId"], "vehicleId": vehicle, "difficulty": "easy"})
                wait_loop_state(client, lambda s: ((s.get("soloRace") or {}).get("race") or {}).get("phase") == "running", 30, row["eventId"])
                assert client.state["soloRace"]["mode"] == "campaign"
                checks.passed("start." + row["eventId"], vehicle + " entered its authored event")
                client.applied("race.exit")
                client.applied("campaign.open")
        result.update(status="passed", checks=checks.rows, events=rows)
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
    parser.add_argument("--finish", action="store_true")
    parser.add_argument("--reload", action="store_true")
    args = parser.parse_args()
    run(args.connection, args.evidence, args.finish, args.reload)
