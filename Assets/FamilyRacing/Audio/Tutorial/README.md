# Tutorial prompt audio

The tutorial presenter (`Runtime/Presentation/TutorialPresenter.cs`)
announces each lesson cue through the `AudioClip` slots on
`Assets/FamilyRacing/Generated/Driving/tutorial-def-01.asset` (the
`TutorialDefinitionAsset`; `tutorial-01.asset` is the free-drive event).
Bundle one clip
per cue per locale into this folder and wire it into the asset:

- `cue-<lesson>-en.wav` -> `TutorialDefinitionAsset.Cue.english`
- `cue-<lesson>-he.wav` -> `TutorialDefinitionAsset.Cue.hebrew`

Cues today: `steering`, `drift`, `boost`, `jump`, `recovery`, `trial`,
`done` (cue IDs match `TutorialLesson.lessonId`; `trial`/`done` are the
trial-unlock and graduation cues).

Rules:

- Only licensed, locally produced recordings may be committed here. No
  TTS services, no credential use, no downloaded voice assets.
- Keep the recordings short (one sentence), normalized, and WAV/OGG so
  `AudioSource` plays them directly.
- When no clip is wired for the active locale the presenter falls back
  to the on-screen text cue and logs a warning; it reports the miss via
  `LastMissingCue` instead of pretending audio played.

## Acceptance gap (as of task 5)

No licensed EN/HE recordings exist yet, so the voice slots on
`tutorial-def-01.asset` are intentionally empty. Text and icon cues are
complete; spoken prompts remain an open acceptance item tracked in the
task-5 report until a licensed voice production lands.
