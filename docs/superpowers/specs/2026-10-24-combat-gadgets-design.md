# Homing missiles, mines, and EMP

## Intent

Make races more varied by adding forward offense, rear defense, and a close-range attack to the existing random pickup pool. The user requested new gadgets such as forward homing missiles and mines, then selected the larger set including an EMP pulse.

## Approach

Extend the existing pure, host-authoritative gadget rules, authored catalog, snapshot state, and pooled Unity presenter. This is the recommended implementation choice because solo, AI-filled, and LAN races already share these systems. Independent Unity projectile physics would split authority and make LAN outcomes harder to reproduce; instant missile hits would remove the visible chase and counterplay.

Keep the existing one-item inventory, seeded pickup bag, press-edge activation, and gadget controls. Existing turbo, shield, and foam gadgets remain available. No new input bindings or equipment menus.

## Behavior

Numbers below are proposed starting values chosen by the implementer and may be tuned after verification. The simulation runs at 30 ticks per second.

### Forward homing missile

- Launch from the front of the vehicle along its committed heading.
- At activation, select the nearest eligible rival in a forward cone (roughly 60 degrees on either side), within 70 meters and a compatible road-height band. Break ties by stable racer slot order.
- A visible missile travels at 45 meters per second, steering toward that target with a bounded turn rate for up to 3 seconds. It never retargets after launch. Without a target, fly straight until expiration.
- Resolve swept collision with eligible rival paths to avoid passing through fast-moving racers. The first rival struck consumes the missile. The owner is immune.
- Hit applies a short strong slowdown (0.4 multiplier for 0.9 seconds) through the existing motor modifier seam. It does not add damage or permanently remove control.
- If the target finishes or recovers, lose the lock and continue straight. Different-height roads do not interact.

### Mine

- Drop a clearly visible mine about 3 meters behind the vehicle, at road height.
- Arm after 0.6 seconds; remain for up to 12 seconds, using a 2.5-meter trigger radius.
- The first eligible rival crossing its trigger consumes it, including a shield-blocked crossing. The owner is immune.
- Hit applies a 0.35 slowdown multiplier for 1.2 seconds. Use swept detection and the existing recovery/height protections.
- Show arming and armed states distinctly with color and a pulsing indicator.

### EMP pulse

- Activation emits an immediately resolving pulse in a 12-meter radius around the racer, within the same road-height band.
- Affect every eligible nearby rival once, excluding owner, finished racers, and recovering racers.
- Hit applies a 0.6 slowdown multiplier for 1.5 seconds. A short expanding blue ring makes its radius and activation visible.
- Do not invert steering, clear inventory, or remove existing turbo effects. Compose the temporary slowdown with turbo through the current motor seam.

### Shared counterplay

- Bubble shields block one incoming missile, mine, foam, or EMP hit per charge.
- Reuse the current slowdown and post-hit immunity rules for all offensive gadgets to prevent repeated stun chains. A shield absorbs a hit without applying a slowdown.
- Weapon events carry the firing owner's identity and the affected rival identity as needed; they must not be miscounted as foam hits or duplicate rewards. Existing foam event behavior stays compatible.
- Finished racers cannot activate gadgets. Pausing freezes every authoritative lifetime, arming timer, and projectile movement.

## Data and integration

- Add recognized gadget kinds and validated authored parameters for projectile speed, targeting range, turn rate, arming time, pulse radius, lifetimes, and hit effects. Carry them through ScriptableObject conversion and content fingerprints.
- Update the checked-in gameplay catalog and its authoring path so regeneration preserves all six gadgets. Discover all catalogs referencing the existing gadget asset before editing.
- Include live missile/mine/pulse state in deep-copied, bounded gadget snapshots. Reuse patch storage for mines only if its meaning remains explicit; missiles require their own moving state. Validate finite geometry, known kinds, positive lifetimes, owner/target identifiers, and shared capacity limits before applying network snapshots.
- Pass committed vehicle heading into gadget inputs in both standalone driving and coordinated races.
- Use explicit new hit/drop/launch/pulse events. Audit metric consumers, presentation audio, and any event allowlists. Advance protocol/schema identity if the serialization contract requires it; content fingerprints must distinguish the new authored behavior.
- AI uses missiles when a rival is ahead, mines when pursued, and EMP when a rival is nearby. Preserve existing willingness, patience, and inherited press-latch behavior. Shields should recognize incoming new threats where existing context can support this.
- Add concise English and Hebrew names and hints, distinct HUD icons, pooled projectile/mine/pulse visuals, and feedback for hits/blocks using the existing presentation infrastructure.
- Views retire when the authoritative effect disappears, on rematch, and on leaving the race; pools stay bounded.

## Verification

Add meaningful rule tests covering forward targeting, no-target behavior, missile travel/turning/swept hits, mine arming and one-shot consumption, EMP multiple victims and radius, owner/height/recovery/finish exclusions, shields and immunity, pause/lifetime behavior, deep copies, and capacity bounds. Test authored catalog conversion and fingerprint sensitivity for new parameters. Cover snapshot encode/decode and malformed new-state rejection, AI decisions, and presentation lifecycle where supported by the existing test harness.

Run the offline Core suite and relevant Unity EditMode/PlayMode checks; compile the runtime and run a development-player smoke check if the local Unity environment permits. Record actual results and any unverified visual/device behavior. Do not claim tablet, physical LAN, or human feel qualification from automated tests.

## Scope and builder decisions

This is a focused gameplay extension across the existing Core, runtime, content, localization, and tests. No new art dependency, monetization, progression unlocks, vehicle damage system, course changes, or multiplayer transport redesign. The implementer may choose internal state layout, pool organization, exact primitive visual construction, minor numeric tuning, and test organization while preserving the behaviors above.

## Approval

Approved by the user on 2026-10-24. The user also requested nice icons for every gadget; create a consistent original icon set for all six and wire it into the HUD.
