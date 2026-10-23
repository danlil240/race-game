# Race Planet: closed circuits

Current player: `Builds/Windows-Circuits/FamilyRacing.exe` (Unity 6000.3.26f1).
Launch with `-force-d3d11`, select a profile, then choose **Quick Race** or
**Campaign**. Coastal Village, Pine Forest and Desert Canyon are all available
from the beginning. Each location has a different closed road layout.

- 14 m paved road, visible curbs, 4 m rough run-off on either side.
- Continuous 1.5 m inner and outer barriers; no shortcut lanes or alternate roads.
- Ordered checkpoints and the AI racing line come from the same sampled loop as
  the road mesh and barrier colliders. A lap must pass every gate in order.
- Rough-ground drag reduces the current arcade car to 35% of normal top speed
  (8.82 m/s for the starter car). Boost cannot bypass this cap. Steering remains
  available and normal acceleration returns when the car rejoins the road.
- The car now reaches 25.2 m/s (91 km/h), or 34 m/s (122 km/h) under boost,
  with 9.5 m/s² normal acceleration. The starting grid follows the actual curved
  road; each car's full body is checked against the paved ribbon.
- Three pickup stations per course each span three lanes. The large gadget
  card names the collected item and explains its effect. Press **E** or click
  the named gadget button to use it. Short taps survive until the next simulation
  step; pause, focus loss and explicit input clearing cancel pending presses.
- One championship spans all three locations: 12 regular events and two class
  finales. Existing trial profile files, earned coins and earlier coastal event
  identities are retained. Practice events use the coastal circuit.

The old open-map landmark tutorial is not offered in this circuit build because
its drills refer to geometry that was intentionally replaced. Original benchmark
assets and the older `Windows-AssetTrial` player remain separate.

Authoring: **Family Racing / Asset Trial / Create Sunny Island Trial**. The
generated scene now uses content identity `closed-circuits-2`. Its optional
three-track inventory extends DrivingWorld while older single-track scenes keep
their original binding. All scenery is from the previously licensed free packs
or original procedural geometry. No purchases or additional downloads.

Verification commands:

```
dotnet test Tools/CoreTests/FamilyRacing.CoreTests.csproj
python Tools/ClosedCircuitScenario.py <run>/connection.json --evidence <new-dir>
python Tools/ClosedCircuitSurfaceCheck.py <run>/connection.json --evidence <new-dir>
python Tools/ClosedCircuitGadgetCheck.py <run>/connection.json --evidence <new-dir>
```

`ClosedCircuitScenario` loads the generated route manifests and uses real input
holds to finish each loop. It never teleports or marks checkpoints. The surface
scenario measures paved/rough/rejoined speed and drives toward a barrier while
checking traveled position stays inside the circuit. Engine overviews and actual
gameplay captures live in `docs/qa/asset-trial-2026-10-23/closed-circuits`.

The speed/grid/gadget bugfix evidence is in
`docs/qa/asset-trial-2026-10-23/circuit-fixes`. Unity regression tests cover every
grid seat, shoulder-to-road probes at all 208 route points, and a gadget press
released before the simulation can sample it.

Physical A7 Lite performance is not verified by these PC checks.
