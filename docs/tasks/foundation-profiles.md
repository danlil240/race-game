# Foundation task: local profiles and persistence

Owner: profile implementer. Workspace: C:\Users\daniel\race-game.

Implement only the profile foundation from GAME_PLAN.md, with tests first. Do not implement racing, progression rewards, purchases, UI, Unity editor bootstrap, networking, or generated content. Do not install tools or spawn agents. Do not commit until root integration review. Root is installing .NET SDK 10 and Unity 6000.3.26f1.

## Files owned

- Assets/FamilyRacing/Core/Profiles/*.cs and FamilyRacing.Core.asmdef in Assets/FamilyRacing/Core.
- Tools/CoreTests/* (a .NET 10 NUnit test project linking the production profile source files).
- Assets/FamilyRacing/Tests/EditMode/ProfileTests.cs and FamilyRacing.Tests.EditMode.asmdef (share appropriate behavior tests with the .NET harness).
- docs/tasks/foundation-profiles-report.md.

## Contracts (namespace FamilyRacing.Core.Profiles)

- Serializable ProfileSave public fields: int schemaVersion=1; string id; int slot; string avatar; int colorIndex; string language; string camera; bool reducedCameraShake; float musicVolume/effectsVolume/voiceVolume; int coins; List<string> unlockedVehicles. Starter vehicle IDs: car-01 and motorcycle-01. Four slots indexed 0..3; avatars fox/rabbit/panda/raccoon; language en/he; camera chase/overhead; volume [0,1]. New profiles have 0 coins, chase camera, music .65, effects .85, voice 1.0, and reducedCameraShake false. Default language is supplied to Create, never inferred by core.
- ProfileFactory.Create(int slot, string language): ProfileSave. Stable randomly generated id retained across saves. Validate arguments. Assign default avatar and colorIndex from slot.
- IProfileSerializer: string Serialize(ProfileSave profile); ProfileSave Deserialize(string json). Unity adapter will use JsonUtility; test adapter uses System.Text.Json with IncludeFields=true.
- ProfileStore(string directory, IProfileSerializer serializer); Load(int slot): ProfileLoadResult; Save(ProfileSave profile): void.
- ProfileLoadResult: status enum Missing/Loaded/RecoveredFromBackup/Blocked, profile or null, and a safe diagnostic string. Never expose raw serialized save text or OS paths in user messages.
- Exactly profile-0.json through profile-3.json, sibling .bak and temporary files inside the supplied directory. Validate slots before path construction.
- Validate schema, profile id, slot, known avatars/languages/cameras, coins>=0, volume bounds and finite values, non-null collection and valid nonempty vehicle IDs. Unsupported newer schemas are Blocked, not reset or migrated by guessing. Missing files are Missing. Invalid primary plus valid backup can be RecoveredFromBackup. Both invalid are Blocked. Do not fall back over a newer primary schema: preserve it and block.
- Save atomically using a temporary file, flushed content, replacement plus last-known-good backup. Preserve invalid/newer files rather than silently overwrite. Save over an existing invalid/newer primary must fail explicitly; restoration needs a separate deliberate recovery operation if you choose to expose it (document contract to root). No partial writes after serialization or validation errors.
- No UnityEngine dependency in Core. No network APIs. C# 9 or lower and .NET Standard 2.1-compatible APIs. Unity assembly must not reference .NET10 libraries.

## Tests and evidence

Write failing tests before production behavior. Wait for dotnet availability if installing; do not fake executed tests. Test all 4 profile slots, setting round-trips across a fresh store instance, invariant validation, no slot traversal, preserved ids, backup recovery, missing data, corrupt data, future schemas (including a valid older backup), refused destructive overwrite, and failed save preserving the old data. Tests must exercise actual disk files in isolated temp directories.

The .NET test harness uses NUnit 3.14.0, NUnit3TestAdapter 4.6.0 and Microsoft.NET.Test.Sdk 17.12.0 or compatible pinned versions if restore rejects them; record changes. It validates shared pure C# behavior, not Unity rendering/device compatibility.

Unity EditMode tests use Unity's NUnit references. Use separate assembly definitions for Core and tests. Avoid writing .meta files manually; Unity will generate them during import.

Report exact RED/GREEN commands, counts, files changed, and concerns. Never claim the whole milestone or game is complete.
