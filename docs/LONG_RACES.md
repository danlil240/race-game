# Race Planet · Grand Tour

Windows player: `Builds/Windows-LongRaces-Ordinary/FamilyRacing.exe`.
The matching developer player is `Builds/Windows-LongRaces/FamilyRacing.exe`.
Select a racer, then **Quick Race**, **Campaign**, or **Time Trial**.

The coastal road is a large irregular 7 km circuit with two-lap races (14 km
total). Pine Lake is a 14 km journey; Canyon Expedition is a 15 km journey.
Journeys finish at a separate destination after all checkpoints and never send
racers back to the start. Their HUD shows journey percentage. Course cards show
the route type, distance and approximate 5–8 minute duration.

The car reaches 135 km/h, or 185 km/h with held boost. The motorcycle reaches
145 km/h, or 195 km/h with boost. Acceleration, grip and steering are tuned for
the larger roads. Keyboard controls remain automatic acceleration, left/right
steering, S/down brake, Space drift, Shift boost, E gadget, R recovery, C camera,
and Escape pause. Release and recharge boost between activations.

Visual changes include wider marked roads, steel guardrails, gentle road
elevation, rolling terrain, more woodland, lake clearings, coastal buildings and
boats, distant wooded hills, layered canyon formations, warmer lighting, soft
shadows, four-sample PC antialiasing and a new procedural sky. Existing licensed
art and original geometry are reused; no assets were purchased or downloaded.
Trees, rocks and roadside detail share instanced meshes, while unsupported water
shaders retain normal renderers. Distant decorative chunks are culled; collision
stays active along the entire selected route. Other courses are deactivated.

Profiles, coins, purchased upgrades and existing campaign event identities are
preserved. New course IDs isolate long-route time records from the old short
circuits. Long events give remaining racers four minutes after the first finish;
older content retains its one-minute grace period. Both old circuit build folders
remain available. The new content identity is `grand-tour-1`.

## Reproduce

Unity 6000.3.26f1: **Family Racing / Grand Tour / Create and Build Both**.
Batch entry point: `FamilyRacing.EditorTools.LongRaceAuthoring.CreateAndBuildBoth`.
It authors the separate scene `Assets/FamilyRacing/Generated/LongRaces/GrandTour.unity`,
validates its catalog and roads, simulates complete car/motorcycle traversals,
and builds development and ordinary Windows players.

```
dotnet test Tools/CoreTests/FamilyRacing.CoreTests.csproj --no-restore
python -m unittest discover -s Tools -p TestLongRaceScenario.py
python Tools/LongRaceScenario.py --course coastal-grand-tour-v1 --evidence Logs/new-coast-run
python Tools/LongRaceScenario.py --course pine-lake-journey-v1 --vehicle motorcycle-01 --language he --mode timeTrial --evidence Logs/new-forest-run
python Tools/LongRaceScenario.py --course canyon-expedition-v1 --evidence Logs/new-canyon-run
python Tools/LongRaceLanScenario.py --evidence Logs/new-lan-run
```

Unity PlayMode filter: `FamilyRacing.Tests.LongRaceWorldPlayTests`.
The external scenarios use normal input holds and observed gate crossings; no
teleports, checkpoint grants or forced finishes. They raise the bounded developer
trace limit to 2 GB because the default short-test recording limit cannot retain
an entire long race. `--lifecycle --smoke-seconds 15` additionally checks pause,
resume, camera switching and restart without another full traversal.

## Verification

- Core: 856 tests passed. Unity: 9 long-world tests passed, covering continuous
  road/grid/recovery, nested-model batching, water rendering, course activation
  performance classification for all local race modes, Time Trial rider framing
  and the 32 KB LAN snapshot budget. Actual initial packets measure 23,897–28,271
  bytes. Sparse 320 m checkpoints complement continuous barriers along the road.
- Complete deterministic simulation over actual Unity road collision: all six
  course × vehicle combinations finish with zero recoveries. Recorded times:
  coastal car 6:14 / motorcycle 5:48; forest 6:11 / 5:45; canyon 6:37 / 6:10.
  These are simulated driving times, not live-player or hardware performance evidence.
- Full input-driven Windows runs: coastal car 5:31, canyon car 5:49, forest
  motorcycle 5:05. Measured boost peaks: 185.04 and 195.12 km/h. No recovery was
  needed in those traversals. A later screenshot review found and corrected a
  Time Trial camera routing bug; the final build's Hebrew rider-camera check
  also verifies pause, resume, manual recovery, both cameras and restart.
  The final build additionally reduces checkpoint density to fit the existing
  LAN packet budget; all six simulated traversals remain unchanged in duration.
  Full evidence is recorded separately in the QA report. Faster drivers may
  finish sooner; pauses do not advance race time.
- PC targets 60 FPS, tablet targets 30 FPS. Physical tablet performance and actual
  Wi-Fi device behavior remain unverified. Captured PC medians are about 16.7 ms;
  p95 ranges from 16.76 to 29.76 ms during concurrent test/build workloads, with
  occasional stalls. These captures do not establish isolated 60 FPS qualification.
  Loopback LAN tests run real networking
  endpoints on this PC. The ordinary build does not expose developer automation.

Evidence: `docs/qa/long-races-2026-10-24/`; developer build log:
`Logs/grand-tour-qualified-build.log`.
