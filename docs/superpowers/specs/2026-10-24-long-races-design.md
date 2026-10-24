# Larger, faster races and richer worlds

Status: approved by the user ("implement") and implemented on 2026-10-24.
Measured-distance adjustment: the initial distance estimates below produced
3–4 minute races at the higher speeds. The delivered coastal circuit is 7 km
with two laps (14 km total); forest and canyon journeys are 14 and 15 km.
This adjustment preserves the agreed 5–8 minute experience. Source and live
verification details are in `docs/LONG_RACES.md` and the QA evidence folder.

## Intent and agreed direction

The user wants substantially larger races, higher speeds, enough time to enjoy
driving, varied routes rather than every race being a circle, and better graphics.
They selected a mix of large circuits and start-to-finish journeys, with ordinary
races lasting approximately 5–8 minutes.

Current generated coastal, forest and canyon loops measure approximately 501,
540 and 607 metres. Regular campaign circuits use one lap. The trial car tops
out at 25.2 m/s, with 34 m/s boost. Current authoring shares a flat ribbon road,
short-loop checkpoints and looping AI; landscapes use flat ground and sparse
scenery. Merely raising lap counts does not address the requested experience.

## Approaches

1. Enlarge existing loops and add laps: least code, but every race still circles
   the same compact setting. Does not deliver sufficient route variety.
2. Mix a genuinely large circuit with two long journeys: selected by the user.
   Extends existing racing rules for open routes and reuses the Unity/URP stack,
   vehicles, campaign and licensed art. More route and scenery work, but all
   three requested improvements arrive together.
3. Convert every course to a journey: viable, but removes circuit variety.

## Proposed playable content

These particular settings and numerical tuning targets are the builder's
proposal, rather than separately agreed user requirements.

- Coastal Village: an irregular 3–4 km circuit, normally two laps, with harbor,
  village, headland and beach sections. Long straights alternate with sweeping
  corners and a few tighter turns. No oval stretched to fill space.
- Pine Forest: a 7–10 km journey from a woodland settlement through dense forest,
  lakeside clearings and rocky passes to a separate finish.
- Desert Canyon: a 7–10 km journey through broad canyon straights, winding rock
  corridors and an oasis finish. Keep the route legible from the chase camera.
- Ordinary Quick Race and regular campaign races target 5–8 minutes of clean
  representative driving, excluding pause/countdown. Distance is adjusted after
  real driving measurements; duration is not an enforced timer. Faster drivers
  can finish sooner and beginners can take longer.
- Journeys finish once at their final gate. They never require returning to the
  start. Circuit laps remain explicit. Journey HUDs show route progress instead
  of misleading lap counts. Race setup identifies the route type and approximate
  expected duration. Time Trial keeps its single-attempt behavior with route
  length clearly shown; campaign time goals are retuned to the enlarged routes.
- Initial speed targets: car about 135 km/h normally and 185 km/h boosted;
  motorcycle about 145/195 km/h. Tune acceleration, steering, AI cornering and
  camera framing together. Targets may move to retain approachable handling.
- Pickups and recovery poses are distributed throughout each full route, with
  repeated opportunities for gadgets. Roadside signs communicate turns early.

## Visual direction

Keep the playful animal racers and stylized art, with a substantial environment
upgrade: rolling surrounding land, layered distant silhouettes, fuller vegetation,
harbor buildings and boats, varied canyon formations, dressed road verges, better
surface textures, lane markings, softer shadows and coherent warm lighting.
Use the existing licensed model packs and original procedural meshes/materials.
No paid asset purchases or downloads are needed for this scope.

Shape the scenery around each route so it changes as players travel. Avoid huge
empty ground planes, identical scatter everywhere, scenery on the road, and abrupt
landscape edges. Use gentle driving elevation only where the existing motor and
recovery rules can support it reliably; no stacked bridges or tunnels are required.
Roadside models must not obscure upcoming turns. Chase and overhead views remain
usable; respect reduced-camera-motion preferences.

## Technical scope and compatibility

Keep Unity 6000.3.26f1, the arcade motor, authoritative simulation, bilingual UI,
current vehicle/animal rigs and existing campaign progression. Extend course data
with an explicit closed/open route contract that defaults to closed for old data.
Generate road, barriers, gates, AI and recovery from the same route source.

Open-route support must cover checkpoint completion, AI endpoint behavior,
placement/progress calculations, recovery, results and host/client content
fingerprinting. Test these together rather than faking an open course as a closed
loop. Retune event pars and objective thresholds for measured race duration.
All race entry paths, including practice and LAN, must bind valid new courses.

Preserve profiles, coins, purchases and campaign completion. Use new versioned
course identities for replacement layouts and update campaign/selection references,
so old 500-metre time-trial records cannot compete with long-route records.
Keep existing event identities where progress depends on them. Bump content
identity/fingerprints to prevent incompatible LAN players from joining.

Large worlds must not render all three locations at once. Use course activation,
chunked scenery, distance culling/LOD and material batching as appropriate.
Keep the existing PC/tablet render profiles. PC target remains 60 FPS; tablet
target remains 30 FPS. Physical tablet performance cannot be claimed from PC
checks. Preserve the current working tree and unrelated existing changes.

## Verification and delivery

Add focused core tests for open-route completion, gate order, reverse/skip
rejection, AI finish behavior and unchanged circuit laps. Unity checks cover
continuous road/collision, road-aligned grid/recovery, course activation and
both vehicle classes. Exercise full ordinary-length runs on all three routes
using actual inputs, record duration and speeds, and verify results/rewards.
Check pause, retry, recovery, and English/Hebrew HUDs. Verify LAN open-route
completion with two endpoints and run existing relevant race regressions.

Build a separate Windows player for this upgrade, with development evidence and
an ordinary player for use. Inspect actual gameplay captures at several sections
of each route, and measure sustained PC rendering during full races. Report
observed timings and any performance limitations honestly. Implementation details,
exact road shapes, scenery placements and final tuning belong to the builder.

Success means a usable Windows build containing three substantially larger,
visibly richer routes, materially faster driving, and representative 5–8 minute
regular races with both circuit and separate-start/finish behavior.
