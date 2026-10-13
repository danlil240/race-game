"""External process-memory sampler for the M4 visual benchmark.

Process memory is measured OUTSIDE the player once per second, never
from Unity heap counters: a Windows player reports its working set
through psapi, an Android player reports the app's total PSS through
`adb dumpsys meminfo`. Every record in `memory.ndjson` names the
metric, source and units; a failed sample is an explicit
`status: "unavailable"` record, never a fabricated zero. The two
metrics are differently-defined quantities — each device compares only
against its own baseline, never against the other platform.

Windows player:
    python Tools\\PerfSampler.py --pid <player-pid> --evidence <new-dir>
Android player (device attached over adb):
    python Tools\\PerfSampler.py --adb-serial <serial> \
        --package com.daniel.familyracing --evidence <new-dir>

`--seconds` bounds the sample window (otherwise Ctrl+C ends it);
`--interval` overrides the 1 Hz cadence only for tooling tests —
benchmark evidence uses the default once-per-second interval.
"""

import argparse
import json
import sys
import time
from pathlib import Path

from AgentScenario import MemorySampler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--pid", type=int,
                        help="Windows player process id (working set).")
    target.add_argument("--adb-serial",
                        help="Android device serial (app total PSS).")
    parser.add_argument("--package", default="com.daniel.familyracing",
                        help="Android package for adb dumpsys meminfo.")
    parser.add_argument("--evidence", type=Path, required=True,
                        help="New evidence directory for memory.ndjson "
                             "and result.json.")
    parser.add_argument("--interval", type=float, default=1.0,
                        help="Sample interval in seconds (default 1.0).")
    parser.add_argument("--seconds", type=float, default=0,
                        help="Bounded sample window in seconds; 0 runs "
                             "until interrupted.")
    args = parser.parse_args()
    if args.interval <= 0:
        parser.error("--interval must be positive")
    args.evidence.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed",
              "scope": "external process-memory sampling",
              "metric": ("process.workingSet" if args.pid is not None
                         else "process.totalPss"),
              "source": ("psapi GetProcessMemoryInfo"
                         if args.pid is not None
                         else "adb dumpsys meminfo"),
              "units": "bytes",
              "intervalSeconds": args.interval}
    sampler = MemorySampler(args.evidence, pid=args.pid,
                            adb_serial=args.adb_serial,
                            package=args.package, interval=args.interval)
    try:
        sampler.start()
        if args.seconds > 0:
            time.sleep(args.seconds)
        else:
            try:
                while True:
                    time.sleep(1)
            except KeyboardInterrupt:
                pass
        sampler.stop()
        path = args.evidence / "memory.ndjson"
        records = [json.loads(line)
                   for line in path.read_text(encoding="utf-8")
                   .splitlines() if line.strip()]
        result["samples"] = len(records)
        result["okSamples"] = sum(
            1 for record in records if record.get("status") == "ok")
        result["unavailableSamples"] = sum(
            1 for record in records
            if record.get("status") == "unavailable")
        result["file"] = str(path)
        result["status"] = "passed"
        if not records:
            result["status"] = "failed"
            result["error"] = "no samples recorded"
    except Exception as error:
        result["error"] = str(error)
        sampler.stop()
    (args.evidence / "result.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
