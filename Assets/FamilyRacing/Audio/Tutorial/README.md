# Tutorial prompt audio

The tutorial presenter (`Runtime/Presentation/TutorialPresenter.cs`)
announces each lesson cue through the `AudioClip` slots on
`Assets/FamilyRacing/Generated/Driving/tutorial-def-01.asset` (the
`TutorialDefinitionAsset`; `tutorial-01.asset` is the free-drive event).
Bundle one clip
per cue per locale into this folder and wire it into the asset:

- `cue-<id>-en.wav` -> `TutorialDefinitionAsset.Cue.english`
- `cue-<id>-he.wav` -> `TutorialDefinitionAsset.Cue.hebrew`

Cues today: `steering`, `drift`, `boost`, `jump`, `recovery`, `trial`,
`finished` (cue IDs match `TutorialLesson.lessonId`; `trial`/`finished`
are the trial-unlock and graduation cues resolved by `FindCue`).

## How the clips are produced

Spoken prompts are synthesized **offline at authoring time** with Azure
Speech (GAME_PLAN §3 — AI-generated voice allowed within the asset
budget; free tier F0 is sufficient, $0 spend) and committed as plain WAV
assets. Nothing network-related enters the player: no runtime service,
no credentials, no downloads.

- Spoken lines and voices live in `Tools/tutorial-cues.json` — that
  manifest is the single source for cue IDs, EN/HE lines, and voices.
- `Tools/Generate-TutorialCues.py` (standard library only) POSTs SSML to
  the Azure Speech REST endpoint and writes `cue-<id>-<locale>.wav`
  here.
- The editor setup pass `DrivingPrototypeSetup.WireTutorialCueClips`
  assigns each clip onto `tutorial-def-01.asset`, so a regenerated
  project stays wired.

### Regeneration runbook

```powershell
$env:AZURE_SPEECH_KEY    = "<speech resource key>"    # free tier F0
$env:AZURE_SPEECH_REGION = "<region, e.g. eastus>"
python Tools\Generate-TutorialCues.py
Tools\Verify.ps1 -Mode Setup   # re-wires clips onto the tutorial asset
```

Edit lines/voices in `Tools/tutorial-cues.json`, re-run the generator,
then run setup. Commit the WAVs and the updated asset. Credentials are
never written to the repository or the shipped game.

Rules:

- Only clips produced by this pipeline (or other licensed recordings)
  may be committed here.
- Keep the recordings short (one sentence), normalized, and WAV/OGG so
  `AudioSource` plays them directly.
- When no clip is wired for the active locale the presenter falls back
  to the on-screen text cue and logs a warning; it reports the miss via
  `LastMissingCue` instead of pretending audio played.

## Acceptance gap

Spoken prompts require the owner's Azure Speech credentials at
generation time (see the runbook above). Until clips are generated and
committed, the voice slots on `tutorial-def-01.asset` remain
intentionally empty and `EveryCueHasWiredClipForBothLocales` stays
explicitly Ignored as the tracking gate. Text and icon cues are
complete.
