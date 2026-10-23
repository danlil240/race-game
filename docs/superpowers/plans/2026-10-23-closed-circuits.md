# Closed circuits implementation plan

**Goal:** Three distinct closed racing locations, no shortcuts, rough-ground slowdown.

**Architecture:** Keep the existing Unity arcade driving/race/progression systems. Author
three separate loop definitions and one combined scenery prefab with locations spaced
apart. Extend DrivingWorld's optional course inventory without changing legacy scenes.
Use explicit collider surface markers for rough ground, and continuous inner/outer
barriers plus ordered checkpoints to prevent cross-country cuts.

**Choices:** Coastal village, pine forest and desert canyon. Roads are 14 m wide,
with 4 m rough run-off on each side before the barriers. Existing free assets and
original procedural geometry; no purchases. New trial content identity and separate
Windows-Circuits build. Preserve existing profile files and original benchmark assets.

- [x] Red/green motor tests: rough-ground drag, boost cap and returning to road.
- [x] Author three loop meshes, colliders, continuous rails, gates and AI routes.
- [x] Bind course inventory and practice events; one championship keeps every place available immediately.
- [x] Validate closed topology, empty shortcuts and matching road/gate geometry.
- [x] Build; drive every course through its actual gates, capture gameplay and scenery.
- [x] Verify off-road slowdown in the player, run Core tests and archive evidence.

This continues the user's approved playable art trial. The course replacement is
intentional, so the earlier requirement to preserve the graybox collider signatures
is replaced by checks against the new authored loop contract.
