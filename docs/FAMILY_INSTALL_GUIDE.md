# Family Racing — Install & Start Guide (for parents)

This guide covers the privately shared family packages. Everything the
game needs is inside the package — **no internet is required after the
files are copied** (LAN races use your home Wi-Fi directly between
devices; there is no account, no login and no store).

## What you received

| File | What it is |
|---|---|
| `FamilyRacing-<version>-Android.apk` | The tablet game, signed with the family release key |
| `FamilyRacing-<version>-Windows-x64.zip` | The complete Windows game — unzip and play |
| `THIRD-PARTY-NOTICES.txt` | Licenses for the bundled engine components |
| `release-manifest.json` | Technical fingerprints proving these exact files are the ones that were built and tested |
| `INSTALL-GUIDE.txt` | This guide |

The manifest sits **outside** the ZIP on purpose: it records each
file's SHA-256 hash and size so the packages can be verified without
the hashes referencing themselves.

## Windows — install & update

1. Pick where the game should live, e.g. `Documents\FamilyRacing`.
2. Right-click `FamilyRacing-<version>-Windows-x64.zip` →
   **Extract All…** → choose that folder. The ZIP contains a
   `FamilyRacing` folder holding the entire player (`FamilyRacing.exe`,
   `FamilyRacing_Data`, `UnityPlayer.dll`, Mono runtime) — extract the
   whole folder, not just the exe.
3. To update: extract the new ZIP over the same folder, replacing
   files. Progress, settings and the four profile slots are **not**
   stored in the game folder — nothing is lost by replacing the files.

### First start on Windows

- Double-click `FamilyRacing.exe`. It works fully offline.
- **Windows SmartScreen may warn** "Windows protected your PC". The
  exe is **not Authenticode-signed** — no paid code-signing
  certificate is part of this private family release, so Windows shows
  "unknown publisher". This is expected: click **More info → Run
  anyway**. If you are ever unsure the file is genuine, compare its
  SHA-256 with `release-manifest.json`.
- If Windows Firewall asks about network access, allow **private
  networks** so LAN races between PC and tablets work. Do not disable
  the firewall.

### Where progress lives (Windows)

Settings and the four player profiles are stored per Windows user at:

```
C:\Users\<you>\AppData\LocalLow\Family Racing\Family Racing\Profiles
```

The game creates this folder itself on first save. Deleting or
updating the game folder never touches it; uninstalling Windows does.

## Android (tablet) — install & update

1. Copy `FamilyRacing-<version>-Android.apk` to the tablet (USB cable
   or a shared folder — any private method is fine).
2. On the tablet, open the APK with the **Files** app. Android asks to
   allow installs "from this source" once — allow it for the file
   manager only.
3. If Play Protect warns it does not recognize the app, that is normal
   for a privately distributed APK — choose **Install anyway**. The
   package is signed with the family release key; its certificate
   fingerprint is recorded in `release-manifest.json`.
4. **To update, install the newer APK directly over the old one — do
   NOT uninstall first.** Because every release is signed with the same
   family key and keeps the same `com.daniel.familyracing` identity,
   Android updates in place and all four profiles are preserved.
   Uninstalling deletes the profiles, so never uninstall as an update
   step.

### First start on Android

- Launch **Family Racing**. Everything is playable immediately with no
  internet.
- The game asks for no account and no special permissions. Local
  network access is only used when joining a race on home Wi-Fi.

## Starting a LAN race (home Wi-Fi)

1. All devices on the same Wi-Fi. No router internet needed — a
   disconnected Wi-Fi hotspot also works.
2. One device **hosts** the race; others choose **join** and pick the
   host from the list (or type its IP if discovery is blocked).
3. Up to four racers: any mix of tablets and the PC. If Wi-Fi is
   congested the game still runs — it degrades gracefully rather than
   dropping players.

## If something goes wrong

| Symptom | What to do |
|---|---|
| Windows blocks the exe | More info → Run anyway (unsigned publisher is expected) |
| Cannot see the host in LAN list | Check all devices are on the same Wi-Fi; try manual IP join; allow private-network firewall access on the PC |
| Game folder accidentally deleted | Re-extract the ZIP — profiles in LocalLow are untouched |
| Tablet says "app not installed" on update | The APK is signed with a different key than the installed one — keep the old app installed and ask for the correctly signed package |
| Profile looks damaged after a crash | The game keeps an automatic `.bak` copy of each profile and restores it on next start |

For backup/restore of profiles on the same installation (the same PC
user or the same tablet — the tools refuse cross-device restores), see
`FAMILY_BACKUP_GUIDE.md` (when delivered). For the technical release
pipeline, see `FAMILY_RELEASE_RUNBOOK.md`.
