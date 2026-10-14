# Sunny Island Art Guide (M4 benchmark)

This guide defines the visual language for the `sunny-island-benchmark-01`
course treatment — the M4 visual benchmark built over the proven
`graybox-01` gameplay layout. It is the authority the dressing was authored
against and the checklist reviewers measure readability against. Spec
context: `GAME_PLAN.md` §2 (Sunny Island: turquoise water, bright
villages, boardwalks, coastal jumps) and §3 (graphics budget).

## Identity

A working harbor island on a bright day: a pale sand-and-stone deck
ringed by whitewashed sea walls, turquoise water on every horizon, a
small terracotta village on the route islands, one lighthouse landmark,
wooden boardwalks marking the shortcut lanes and a boardwalk ramp for
the coastal jump. Everything reads at a glance from a child's chase
camera and from the overhead camera at 0.75 render scale.

## Palette

Warm base + turquoise accent, flat matte colors (no textures in this
treatment — flat-color shared materials read cleaner at low internal
resolution and batch better).

| Role | Color (sRGB) | Use |
|---|---|---|
| Deck sand | `#E6D3A3` | the island's base surface (drivable anywhere) |
| Dust road | `#93775C` | the main racing line ribbons — darker than deck |
| Boardwalk wood | `#A8814F` | shortcut lanes, ramp skin, rail caps — *faster-looking alternate path* |
| Whitewash | `#F2EFE5` | sea-wall parapet, lighthouse bands |
| Harbor teal | `#1F949E` | wall cap rails, shortcut signs, window trim — the "alternate route" accent |
| Terracotta | `#CC613D` | village roofs |
| Cream walls | `#F5E8CC` | village house bodies |
| Pastel blue/yellow/pink | `#8CBDD1` / `#F2D173` / `#E6999E` | village houses, cargo stacks — one strong pastel per building |
| Water | `#29A8B8` | opaque shallow turquoise |
| Deep water | `#146680` | outer ring, darker and deeper |
| Foam | `#E6F8FF` | waterline ring around the island |
| Foliage | `#4D8F47` | palm canopies, island gardens |
| Palm trunk | `#805C38` | trunks, posts |
| Stone gray | `#999488` | island retaining walls, corner boulders |
| Sun yellow | `#FAC72E` | gate stripes, gate pennants, direction chevrons — *mandatory-route* accent |
| Flag red | `#DB4338` | the jump — the only place red is used |
| Check black / white | `#1F1C1F` / `#F0F0EB` | start/finish checker line |
| Hull blue | `#4D809E` | fishing boats in the harbor |
| Drift orange | `#D98C40` | the drift pad paint |

Route language rule: **yellow = mandatory route, teal = optional
shortcut, red = the jump.** A player who has never seen the course can
follow yellow; teal says "this way is legal but optional"; red marks
the one place the ground deliberately ends.

## Proportions and shapes

Rounded, chunky, toy-like. House walls are slightly wider than tall;
roofs overhang by a full slab; vehicles (Task 4) and props sit in the
same scale language. No thin realistic detail — smallest authored
feature is ~0.4 m so it survives 0.75 render scale.

- Houses: 5–7 m bodies, gable roofs (45° diamond prisms), teal door and
  window blocks. One lighthouse (~9 m) on the north-east island — the
  course's only tall landmark.
- Palms: ~3.2 m trunk, sphere-cluster canopy; used on the islands and
  on two water islets only — never on the drivable deck.
- Boats: 6 m hulls, low cabins, one mast; anchored outside the sea
  walls, decoration only.
- Cargo stacks: the chicane barriers are dressed as painted cargo rows
  slightly *larger* than their colliders — contiguous along the full
  collider length (never smaller, never gapped: a driver may brush
  paint, never clip through a visual gap or aim at a bare collider
  face).

## Animal silhouettes (binding for Task 4 racers)

Racers are stylized cartoon animals driving chunky open vehicles. The
silhouette rules keep them readable at 0.75 render scale on the tablet:

- **Silhouette-first design.** Every animal must be identifiable by
  outline alone against both cameras: one signature ear/head/tail shape
  per species (round ears, tall ears, long snout, curled tail — pick one
  dominant feature and exaggerate it; never stack small details).
- **Proportion language.** Oversized head (roughly 40–50% of body
  height), compact torso, stubby limbs — the same rounded, toy-like
  proportion language as the environment (5–7 m houses, chunky palms).
  Realistic anatomy and thin extremities are off-language: limbs and
  tails must be thick enough to survive the smallest on-screen size.
- **Reading distance.** The racer silhouette must stay legible inside
  its vehicle at chase-camera distance and against the dark road ribbon
  from the overhead camera — light-to-mid body tones against the darker
  road, no pure-white bodies that clip into wall whitewash, no water
  turquoise or road-dust browns as the dominant body color.
- **Palette.** Racers use saturated accent colors the course reserves
  for characters — coral, mango, leaf green, sky blue, berry purple —
  never mandatory-route yellow, shortcut teal or jump red as the
  dominant body color (route language must never appear to sit on a
  racer).
- **Materials.** Same flat-matte Simple Lit treatment as the course —
  no fur shaders, no translucency, no emission. A darker belly/muzzle
  block plus one contrasting accent (scarf, cap or stripe) per animal
  carries personality without texture.
- **Scale band.** Drivers sit 1.4–1.8 m tall in the seat; the vehicle
  stays under ~2.2 m tall overall so nothing occludes the road behind
  it from the overhead camera.

## Materials

URP **Simple Lit**, opaque, one shared flat-color material per palette
role (~25 materials total — the "atlas" is the palette). Smoothness is
kept low (0.05–0.15) except water (0.55) and hull paint (0.20). No
textures, no emission, no transparency anywhere on the course — alpha
costs fill-rate the tablet cannot spare and transparent route markings
would be unreadable at 0.75 scale.

## Route and shortcut readability

- The deck is light sand; the main line is a darker dust-road ribbon,
  so the route reads as "the darker path on the light island" from both
  cameras.
- Both shortcut lanes are boardwalk planks — visibly a different
  surface but still obviously ground, never confused with water.
- Gates are a yellow road stripe plus two yellow pennant posts at the
  gate ends (outside the 24 m gate corridor). The start/finish gate
  adds a black/white checker line.
- The landmark jump is a boardwalk ramp with red pennants and painted
  approach chevrons — red flags exist nowhere else.
- The drift pad is painted drift-orange inside the north-east apron.
- Water is always turquoise, flat and below the deck edge; the sea wall
  parapet + teal cap rail marks the real collision boundary exactly.
  Nothing about the water reads as drivable.

## Occlusion and collision rules (binding)

- **The nested `GrayboxCourse` prefab instance is the only gameplay
  geometry.** Dressing contributes zero colliders that gameplay can see
  — motor probes, wheel raycasts and the physical backend's collision
  clone all query `Physics.DefaultRaycastLayers`, which excludes layer
  2, so the shipped collision set is the only physical shape.
- Occluder-class dressing (anything tall enough to sit between a camera
  and the road — houses, the lighthouse, palms, boats, masts, sign and
  pennant posts, cargo stacks, parapet rails, ramp rails) is authored on
  the built-in **Ignore Raycast layer (2)** and carries a dedicated
  non-trigger **camera-occluder BoxCollider** on the same layer:
  invisible to `Physics.DefaultRaycastLayers` but hit by the driving
  camera's explicit `~0` occlusion raycast, so the chase camera pulls in
  *before* the lens can clip inside a visual. The rule is hard: a
  camera-only collider lives on layer 2, carries no renderer, is never
  a trigger — and **never** sits on Default or inside the gameplay
  subtree.
- Nothing tall is placed on the drivable deck; islands/water/walls host
  every tall element. Decorative barrier visuals always *cover* their
  collider — never recessed behind the real boundary, and contiguous
  along the collider's full length so no gap reads as an opening.
- Overhead readability: the only overhead crossings are the checker
  line and gate stripes — flat paint, never geometry over the road.

## Lighting

One warm directional sun (~55° elevation, warm straw color, soft
shadows within the profile shadow distance) plus an authored ambient
trilight (warm sky / straw equator / sea-teal ground bounce) — no
real-time reflections, no fog, no post. The camera clears to solid
turquoise sky. Static flags are set on all dressing (batching + GI
contribution + occluder/occludee) so a lightmap bake can land without
re-authoring; if the editor cannot bake on this machine the authored
trilight stands as the documented ambient.

## Effects restraint

The course treatment itself carries no particles, trails or animated
materials — readability is proven on static dressing alone. Task 5 adds
a catalog-driven presentation layer on top (`race-presentation-01`):
flat-colored quad bursts and emitters in the same palette language —
drift dust in deck sand, landing/wall puffs in whitewash, boost flare
in warm orange, checkpoint sparkle and the finish burst in the
mandatory-route sun-yellow accent family. Effects are presentation-only
decoration (pooled, bounded, despawned quads) — they carry zero
colliders, mutate no simulation state, and gameplay readability never
depends on them: audio cues and camera feedback carry the required
signal.

## Performance budget (tablet-first)

- ≤ ~400 dressing renderers, all URP Simple Lit, all statically
  batched. Flat colors only — no textures means near-zero texture
  memory.
- LOD groups on every occluder-class cluster (houses, lighthouse,
  palms, boats): full detail near, single-silhouette far.
- The PC profile (Task 2) may add shadow distance; collision and
  gameplay visibility stay identical between profiles by construction.

## Provenance and budget

All meshes are Unity primitives composed in-editor by
`SunnyIslandCourseAuthoring` — original work, US$0. No third-party
assets are used. Every referenced asset is inventoried in
`docs/ASSET_INVENTORY.md` with GUID, hash, license, source, cost and
role; `AllReferencedAssetsHaveInventoryEntries` enforces it.
