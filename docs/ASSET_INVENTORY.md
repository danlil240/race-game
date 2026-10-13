# Asset Inventory

Every art/audio/content asset the visual benchmark references, with
source, license evidence, recorded cost and role. Budget authority:
`GAME_PLAN.md` §5 — US$100 planning cap, **not** authorization to
purchase; recorded spending to date is **US$0**. The
`AllReferencedAssetsHaveInventoryEntries` test parses the GUID table
below and fails when a referenced asset is missing an entry or an entry
lacks license/cost/role — keep the table machine-readable (one row per
asset GUID).

## Recorded spending

| Date | Spend | Notes |
|---|---|---|
| 2026-10-14 | US$0 of US$100 | All benchmark environment art is original, authored procedurally in-editor. No purchases made or needed. |

## Referenced assets (machine-readable)

Columns: `guid` | `path` | `source` | `license` | `cost` | `role`.

<!-- INVENTORY-TABLE-BEGIN -->
| guid | path | source | license | cost | role |
|---|---|---|---|---|---|
| 0000000000000000f000000000000000 | unity.builtin-extra (cube/sphere/cylinder/plane primitives) | Unity built-in primitives, composed in-editor | Unity Editor EULA — bundled resource, no attribution required | US$0 | all dressing meshes |
| 933532a4fcc9baf4fa0491de14d08ed7 | Packages/com.unity.render-pipelines.universal/Runtime/Materials/Lit (URP shaders) | Unity URP package 17.3.0 | Unity Package License — bundled with the pinned editor | US$0 | material shading (Simple Lit/Lit variants) |
| 2c139f00b744d2a48ad00a85b6112475 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/sunny-island-benchmark-01.prefab | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:703ef1452aeeed03) | Project-owned original work — no third-party license, no attribution required | US$0 | course visual root (dressing + nested graybox collision) |
| 19274855f36805e4b9b920641f22eadc | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Boardwalk.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:3c1791038fc2a79f) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 6e9b9a59f7d71314eb0a89d4c49427e6 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_CheckBlack.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:7faac8c242720137) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 436c3becc9950e94f979e467e9fdf732 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_CheckWhite.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:63ee5cad9749c426) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| c8fec8a767e0664469e5330109b0aa2f | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Cream.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:46b14a0ef02e9e65) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 00f215d85f525ac44b57d21682ee24ca | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_DeckSand.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:6826d8e0eb93a479) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 9b86ab8b1fe0868449519d2a0864f79d | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_DeepWater.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:c95ae9d4360d0f53) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 3dc30cb085ae5ec4c9dbe6cacfe86362 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_DriftPaint.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:4924e46747f2785d) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 0f418d0e0cbf99440ad3ea4c5cba4808 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_FlagRed.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:0602aadd580cbfa9) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| c2645e098c4ac654c9fa6d9afa6c0c89 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Foam.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:f994bc10e4af6b34) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| ae347c79495ee634e8245c9c5b2e8447 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Foliage.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:89d190e29b1d6a05) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 51dbdf43689062541b74ec61e810b6c1 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_GateYellow.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:84fbac7ec47e8a39) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| bd2931e8390ec1c4f8654f956f7e6466 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Grass.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:aabe08cd2c0abf58) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| b74ad0b313f188541b00569c3c1af873 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_HullBlue.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:cbabdc09435891b1) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| c68c846a5891b164b9d872676a20cca9 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_PastelBlue.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:198ca712eb98a8ec) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| e8055581291293544a11ba5ce3249b0b | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_PastelPink.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:49b90592f6014634) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 6288564fa3f36e741a7daa4d3d2ab0a0 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_PastelYellow.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:839f916e49f6ce86) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 594a20db20b29ea4c8124719005f87dd | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_RoadDust.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:0ea3c55ef944591c) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| e8d36eb1ac35d984095191db6bccf2ee | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_SandDark.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:53b5f677848cf92c) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| eca55c4e36a0d6c4f89c911edf6b7acf | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_StoneGray.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:8fe4575616dedda8) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 3a23718d758a63c4382126bc8b34101b | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_TealTrim.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:2d011828739620d6) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 005b3eb655b47a743ba9c1d26761cff2 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Terracotta.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:e890a71645c75975) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| d2de0dd3d58c750409c0f68d466d2543 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Trunk.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:d08e84563302b05d) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| ea022b971af4d764db3514527b06e362 | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_WallWhite.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:eeed9ed88d7808c5) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| 385b8fb875492e24fb0bff5f740d178d | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Mat_Water.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:d859efa692f31325) | Project-owned original work — no third-party license, no attribution required | US$0 | dressing material |
| d37bed84d2cc3a04b9d38fe97824ee5e | Assets/FamilyRacing/Art/VisualBenchmark/Environment/Materials/Sky_SunnyIsland.mat | Original — procedurally authored by SunnyIslandCourseAuthoring.cs (sha256:0cc2e25f204ba187) | Project-owned original work — no third-party license, no attribution required | US$0 | scene skybox (procedural sky, turquoise tint) |
<!-- INVENTORY-TABLE-END -->

## Authored-in-editor originals

All `Assets/FamilyRacing/Art/VisualBenchmark/` content is generated by
`Assets/FamilyRacing/Editor/SunnyIslandCourseAuthoring.cs` from Unity
primitives — original work owned by this project, no third-party
licenses, no attribution obligations. Their GUIDs and SHA-256 hashes
are recorded in the table above after each authored regeneration (the
hash records the exact serialized asset reviewed; `.meta` files are
committed with the assets).

## Free/third-party assets considered

None selected. The US$0 treatment meets the benchmark brief; if a later
task proposes a purchase, the needed asset, candidate sources, license
and cost must be recorded here **before** purchase — the budget is a
cap, not authorization.

## Open needs

- Vehicle and animal racer visuals (Task 4) — planned as original
  in-editor work; record candidates here if licensed sources are
  preferred.
- Tutorial voice clips (existing open gate from earlier milestones) —
  requires licensed synthetic voices per `GAME_PLAN.md` §3; unchanged
  by this task.
