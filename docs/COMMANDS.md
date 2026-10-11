# Command and validation reference

Exact commands for building, running, and validating the foundation shell.
All Unity invocations use the pinned editor `6000.3.26f1`. The verified
project resolves from this editor's bundled package cache; do not assume a
fresh installation has every dependency cached or authorize downloads implicitly.

## Local verification

```powershell
# Shared Core tests, fully offline (local .NET SDK + bundled Unity NUnit):
Tools\Verify.ps1 -Mode CoreOffline

# Environment check (pinned editor, modules, .NET SDK):
Tools\Verify.ps1 -Mode CheckEnvironment

# Unity project setup (repeatable; writes generated scene/assets):
Tools\Verify.ps1 -Mode Setup

# Unity test platforms (results XML under Logs\):
Tools\Verify.ps1 -Mode EditMode
Tools\Verify.ps1 -Mode PlayMode

# Windows Mono development build -> Builds\Windows\FamilyRacing.exe:
Tools\Verify.ps1 -Mode Windows
```

`Verify.ps1` modes write `Logs\verify-unity-<mode>.log` and propagate the
editor exit code. `dotnet test` (`-Mode Core`) still needs a one-time
`dotnet restore`, which requires explicit network approval.

There is intentionally no `-Mode Android`: the current Gradle path does not
force `--offline`, so an Android build requires explicit network approval.
The APK at `Builds\Android\FamilyRacing.apk` (~46 MB) was produced by such a
non-forced build and is **unqualified** for offline certification.

## Validation run

The loopback endpoint exists only in editor/development builds and only
when opted in:

```powershell
# Run this in a persistent terminal. In an agent terminal, keep this command
# backgrounded with its shell alive; detaching alone may not escape its job.
$exe = (Resolve-Path "Builds\Windows\FamilyRacing.exe").Path
$run = Join-Path (Get-Location) "Logs\validation-<unique-name>"
$player = Start-Process $exe -ArgumentList "--validation-dir",$run -PassThru
$player.WaitForExit()
```

The directory receives `connection.json` (`runId`, `token`, `port`,
`protocolVersion`, `participantId`), `state.ndjson` (sequenced NDJSON
records), `capture-status.json`, and an isolated `profiles/` store. The
lead-owned scenario drives it via:

```powershell
python Tools\AgentScenario.py <dir>\connection.json --evidence <new-evidence-dir>
```

Proven recipe (wave-3 build, 2026-10-11): preview at
`Logs\run-validation-lead` (`connection.json` → port + token), scenario
passed all 9 checks — frozen evidence `Logs\evidence-lead\result.json`.
The player stays alive after `finish` for inspection, while the launch
terminal remains alive. The command/state capture is then closed; start a
new player with a fresh validation directory for another scenario.

## Wire protocol (protocolVersion 1)

Client → server frames, one JSON object per line, max 16 KiB, depth ≤ 16,
no duplicate keys, no trailing content, strict UTF-8:

- `{"type":"hello","protocolVersion":1,"runId":...,"token":...}` — first
  frame within a 5-second total deadline; bad/missing auth is rejected.
- `{"type":"command","protocolVersion":1,"runId":...,"commandId":...,"participantId":"local","action":...,"parameters":{...}}`
- `{"type":"discover"|"snapshot"|"health"|"finish"}`, `{"type":"lookup","commandId":...}`

Actions: `profile.create`, `profile.select`, `ui.navigate`, `ui.language`,
`settings.update`, `settings.save`, `input.set`, `input.clear`,
`camera.toggle`, `pause.toggle`.

Sequenced server records carry `protocolVersion`, `runId`, `deviceId`
(random per run), `participantId`, `buildVersion`, `contentVersion`,
contiguous `sequence`, `monotonicMs`, `tick`: `snapshot`, `state`,
`result`, `discovery`, `lookup`, `health`, `finished`. The trace opens with
a `snapshot` at sequence 1; an authenticated controller's first live frame
is a fresh snapshot at the current sequence. Records emitted while no
controller is attached are trace-only; queued records are never retargeted
to a newer session.

Integrity rules: unintended disconnect, outgoing-queue overflow, trace
limit (256 MiB) or sink failure invalidate the capture and clear agent
input; after `finish` the run is closed (`captureClosed` rejects new
controllers/commands); `capture-status.json` reports `captureComplete`
only after the trace is flushed and the final status stamped.
