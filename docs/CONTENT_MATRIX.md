# Full Content Matrix — reviewable inventory

M6 Task 4 slice A. This document is the human-reviewable content
inventory for the full-content milestone. It is **evidence** — not a
second runtime catalog; `Assets/FamilyRacing/Generated/FullContent`
and `FullContentValidation` are the machine contract.

Every row carries:

- `status`: `passed` | `failed` | `notVerified` — a row is only
  `passed` when the linked evidence shows it ran against the real
  production inventory on the qualified path.
- `evidence`: link to the run/artifact proving the row
  (verification log, capture, device record). Empty = unproven.

**Current state (this commit):** no production content is authored
under `Generated/FullContent` yet — every row is honestly
`notVerified` and the production gate fails the incomplete
inventory by design. Rows flip only through Tasks 5–9 evidence,
never by editing this file alone.

## Worlds

| # | worldId | nameEn | nameHe | courses | status | evidence |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | `sunny-island` | Sunny Island | *(hebrew)* | `harbor-hop`, `lighthouse-loop` | notVerified | |
| 1 | `redrock-canyon` | Redrock Canyon | *(hebrew)* | `canyon-dash`, `mesa-skyway` | notVerified | |
| 2 | `snowy-peaks` | Snowy Peaks | *(hebrew)* | `snowpine-circuit`, `aurora-pass` | notVerified | |

## Courses

Each course requires: 4 separated spawn slots, validated recovery
poses, gadget pickup sites, a landmark jump, and **two distinct
legal optional shortcuts** (rejoin before the next required gate,
never overlapping a mandatory gate plane).

| worldId | courseId | spawn slots | recovery | pickups | shortcut lanes | status | evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `sunny-island` | `harbor-hop` | 4 | authored | authored | 2 | notVerified | |
| `sunny-island` | `lighthouse-loop` | 4 | authored | authored | 2 | notVerified | |
| `redrock-canyon` | `canyon-dash` | 4 | authored | authored | 2 | notVerified | |
| `redrock-canyon` | `mesa-skyway` | 4 | authored | authored | 2 | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | 4 | authored | authored | 2 | notVerified | |
| `snowy-peaks` | `aurora-pass` | 4 | authored | authored | 2 | notVerified | |

## Vehicles

| classId | models | status | evidence |
| --- | --- | --- | --- |
| `car` | `car-01` `car-02` `car-03` `car-04` | notVerified | |
| `motorcycle` | `motorcycle-01` `motorcycle-02` `motorcycle-03` `motorcycle-04` | notVerified | |

## Regular events (72 = 6 courses x 2 classes x 6 types)

ID convention: `<courseId>-<classId>-<type>`; `clean` maps to the
runtime type `clean-driving`. Each cell must occur exactly once.

| worldId | courseId | classId | type | eventId | status | evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `sunny-island` | `harbor-hop` | `car` | `circuit` | `harbor-hop-car-circuit` | notVerified | |
| `sunny-island` | `harbor-hop` | `car` | `boost` | `harbor-hop-car-boost` | notVerified | |
| `sunny-island` | `harbor-hop` | `car` | `stunt` | `harbor-hop-car-stunt` | notVerified | |
| `sunny-island` | `harbor-hop` | `car` | `time` | `harbor-hop-car-time` | notVerified | |
| `sunny-island` | `harbor-hop` | `car` | `gadget` | `harbor-hop-car-gadget` | notVerified | |
| `sunny-island` | `harbor-hop` | `car` | `clean` | `harbor-hop-car-clean` | notVerified | |
| `sunny-island` | `harbor-hop` | `motorcycle` | `circuit` | `harbor-hop-motorcycle-circuit` | notVerified | |
| `sunny-island` | `harbor-hop` | `motorcycle` | `boost` | `harbor-hop-motorcycle-boost` | notVerified | |
| `sunny-island` | `harbor-hop` | `motorcycle` | `stunt` | `harbor-hop-motorcycle-stunt` | notVerified | |
| `sunny-island` | `harbor-hop` | `motorcycle` | `time` | `harbor-hop-motorcycle-time` | notVerified | |
| `sunny-island` | `harbor-hop` | `motorcycle` | `gadget` | `harbor-hop-motorcycle-gadget` | notVerified | |
| `sunny-island` | `harbor-hop` | `motorcycle` | `clean` | `harbor-hop-motorcycle-clean` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `car` | `circuit` | `lighthouse-loop-car-circuit` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `car` | `boost` | `lighthouse-loop-car-boost` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `car` | `stunt` | `lighthouse-loop-car-stunt` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `car` | `time` | `lighthouse-loop-car-time` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `car` | `gadget` | `lighthouse-loop-car-gadget` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `car` | `clean` | `lighthouse-loop-car-clean` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `motorcycle` | `circuit` | `lighthouse-loop-motorcycle-circuit` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `motorcycle` | `boost` | `lighthouse-loop-motorcycle-boost` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `motorcycle` | `stunt` | `lighthouse-loop-motorcycle-stunt` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `motorcycle` | `time` | `lighthouse-loop-motorcycle-time` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `motorcycle` | `gadget` | `lighthouse-loop-motorcycle-gadget` | notVerified | |
| `sunny-island` | `lighthouse-loop` | `motorcycle` | `clean` | `lighthouse-loop-motorcycle-clean` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `car` | `circuit` | `canyon-dash-car-circuit` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `car` | `boost` | `canyon-dash-car-boost` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `car` | `stunt` | `canyon-dash-car-stunt` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `car` | `time` | `canyon-dash-car-time` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `car` | `gadget` | `canyon-dash-car-gadget` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `car` | `clean` | `canyon-dash-car-clean` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `motorcycle` | `circuit` | `canyon-dash-motorcycle-circuit` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `motorcycle` | `boost` | `canyon-dash-motorcycle-boost` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `motorcycle` | `stunt` | `canyon-dash-motorcycle-stunt` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `motorcycle` | `time` | `canyon-dash-motorcycle-time` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `motorcycle` | `gadget` | `canyon-dash-motorcycle-gadget` | notVerified | |
| `redrock-canyon` | `canyon-dash` | `motorcycle` | `clean` | `canyon-dash-motorcycle-clean` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `car` | `circuit` | `mesa-skyway-car-circuit` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `car` | `boost` | `mesa-skyway-car-boost` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `car` | `stunt` | `mesa-skyway-car-stunt` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `car` | `time` | `mesa-skyway-car-time` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `car` | `gadget` | `mesa-skyway-car-gadget` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `car` | `clean` | `mesa-skyway-car-clean` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `motorcycle` | `circuit` | `mesa-skyway-motorcycle-circuit` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `motorcycle` | `boost` | `mesa-skyway-motorcycle-boost` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `motorcycle` | `stunt` | `mesa-skyway-motorcycle-stunt` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `motorcycle` | `time` | `mesa-skyway-motorcycle-time` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `motorcycle` | `gadget` | `mesa-skyway-motorcycle-gadget` | notVerified | |
| `redrock-canyon` | `mesa-skyway` | `motorcycle` | `clean` | `mesa-skyway-motorcycle-clean` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `car` | `circuit` | `snowpine-circuit-car-circuit` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `car` | `boost` | `snowpine-circuit-car-boost` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `car` | `stunt` | `snowpine-circuit-car-stunt` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `car` | `time` | `snowpine-circuit-car-time` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `car` | `gadget` | `snowpine-circuit-car-gadget` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `car` | `clean` | `snowpine-circuit-car-clean` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `motorcycle` | `circuit` | `snowpine-circuit-motorcycle-circuit` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `motorcycle` | `boost` | `snowpine-circuit-motorcycle-boost` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `motorcycle` | `stunt` | `snowpine-circuit-motorcycle-stunt` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `motorcycle` | `time` | `snowpine-circuit-motorcycle-time` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `motorcycle` | `gadget` | `snowpine-circuit-motorcycle-gadget` | notVerified | |
| `snowy-peaks` | `snowpine-circuit` | `motorcycle` | `clean` | `snowpine-circuit-motorcycle-clean` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `car` | `circuit` | `aurora-pass-car-circuit` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `car` | `boost` | `aurora-pass-car-boost` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `car` | `stunt` | `aurora-pass-car-stunt` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `car` | `time` | `aurora-pass-car-time` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `car` | `gadget` | `aurora-pass-car-gadget` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `car` | `clean` | `aurora-pass-car-clean` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `motorcycle` | `circuit` | `aurora-pass-motorcycle-circuit` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `motorcycle` | `boost` | `aurora-pass-motorcycle-boost` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `motorcycle` | `stunt` | `aurora-pass-motorcycle-stunt` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `motorcycle` | `time` | `aurora-pass-motorcycle-time` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `motorcycle` | `gadget` | `aurora-pass-motorcycle-gadget` | notVerified | |
| `snowy-peaks` | `aurora-pass` | `motorcycle` | `clean` | `aurora-pass-motorcycle-clean` | notVerified | |

## Rival finales (6)

ID convention: `<worldId>-<classId>-finale`; rival
`<worldId>-<classId>-rival`; cosmetic `livery-<rivalId>`;
world-completion rewards sunny->*-02, redrock->*-03, snowy->*-04.

| worldId | classId | finale eventId | rivalId | livery | reward vehicle | status | evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `sunny-island` | `car` | `sunny-island-car-finale` | `sunny-island-car-rival` | `livery-sunny-island-car-rival` | `car-02` | notVerified | |
| `sunny-island` | `motorcycle` | `sunny-island-motorcycle-finale` | `sunny-island-motorcycle-rival` | `livery-sunny-island-motorcycle-rival` | `motorcycle-02` | notVerified | |
| `redrock-canyon` | `car` | `redrock-canyon-car-finale` | `redrock-canyon-car-rival` | `livery-redrock-canyon-car-rival` | `car-03` | notVerified | |
| `redrock-canyon` | `motorcycle` | `redrock-canyon-motorcycle-finale` | `redrock-canyon-motorcycle-rival` | `livery-redrock-canyon-motorcycle-rival` | `motorcycle-03` | notVerified | |
| `snowy-peaks` | `car` | `snowy-peaks-car-finale` | `snowy-peaks-car-rival` | `livery-snowy-peaks-car-rival` | `car-04` | notVerified | |
| `snowy-peaks` | `motorcycle` | `snowy-peaks-motorcycle-finale` | `snowy-peaks-motorcycle-rival` | `livery-snowy-peaks-motorcycle-rival` | `motorcycle-04` | notVerified | |

## Voice prompts (14 cues x 2 locales)

Final licensed English+Hebrew clips are required for production
validation; missing clips fail the final-content gate.

| cueId | en | he | status | evidence |
| --- | --- | --- | --- | --- |
| `world.intro.sunny-island` | required | required | notVerified | |
| `world.intro.redrock-canyon` | required | required | notVerified | |
| `world.intro.snowy-peaks` | required | required | notVerified | |
| `rival.intro.sunny-island-car-rival` | required | required | notVerified | |
| `rival.intro.sunny-island-motorcycle-rival` | required | required | notVerified | |
| `rival.intro.redrock-canyon-car-rival` | required | required | notVerified | |
| `rival.intro.redrock-canyon-motorcycle-rival` | required | required | notVerified | |
| `rival.intro.snowy-peaks-car-rival` | required | required | notVerified | |
| `rival.intro.snowy-peaks-motorcycle-rival` | required | required | notVerified | |
| `objective.completion` | required | required | notVerified | |
| `objective.introductory` | required | required | notVerified | |
| `objective.mastery` | required | required | notVerified | |
| `world.unlocked` | required | required | notVerified | |
| `adventure.complete` | required | required | notVerified | |
