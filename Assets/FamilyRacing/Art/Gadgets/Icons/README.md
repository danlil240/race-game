# Gadget icon artwork

Six original icons created for this game on 2026-10-24. No downloaded artwork or third-party licenses are required.

Editable vector masters live here; runtime 256×256 PNGs with transparent corners are under `Resources/GadgetIcons`. The HUD loads them by gadget kind. Keep the PNGs in Resources so every build flavor includes them.

To regenerate, run `node Tools/Create-GadgetIcons.cjs` with the `sharp` package on Node's module search path. The script also creates the QA contact sheet under `docs/qa/combat-gadgets-2026-10-24`.
