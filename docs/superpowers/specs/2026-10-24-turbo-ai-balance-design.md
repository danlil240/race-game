# Turbo and AI balance

Approved in chat on 2026-10-24 (user: "yes").

Make the pickup turbo useful alongside the rechargeable car turbo, and repair
AI decisions that make Hard unnecessarily slow. Preserve the shared vehicle
rules, upgrades, passive recharge, controls and network state format.

The selected approach repairs AI boost continuation and the reversed braking
margin, then measures complete races before considering further tuning. Merely
raising AI speed would bypass the underlying bugs; nerfing human recharge would
change an unrelated driving mechanic.

An AI may start a boost at its difficulty's charge threshold, then sustain it on
a safe straight until the ordinary motor ends it. Braking and significant
steering demand still release boost. Larger brakeMarginScale values must mean
more conservative cornering on both simulation backends. Default/LAN behavior
retains its existing margin.

The turbo pickup lasts 150 simulation ticks (5 seconds), multiplies acceleration
by 1.75 and the speed cap by 1.50. It continues to combine with the ordinary car
turbo through existing modifiers and does not consume its meter. Update both the
shipping asset and its authoring source. Existing refresh, pause, recovery and
foam rules continue to apply.

Validate with regression tests, complete route simulations across difficulties,
Unity checks and rebuilt Grand Tour Windows players. Any additional balance
change must be supported by those measurements.
