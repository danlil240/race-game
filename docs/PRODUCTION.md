# Production releases and update notifications

The game checks the public [GitHub latest release](https://github.com/danlil240/race-game/releases/latest) once at startup on Windows and Android. A newer stable version with a matching platform package produces an English/Hebrew menu notice. “View update on GitHub” opens the release page; “Later” dismisses it for this launch. Download/install remains a player action. Offline startup, timeouts, API limits, no releases, malformed responses and unsupported platforms do not interrupt play. Editor and batch runs skip the request. The request has a six-second timeout, sends no profile data, and uses no credentials.

Version comparisons use the release tag (`vMAJOR.MINOR.PATCH`) and the installed `Application.version`, stamped from `Release/release.json`. Drafts and prereleases never prompt. A Windows update requires `FamilyRacing-<version>-Windows-x64.zip`; Android requires `FamilyRacing-<version>-Android.apk`. The link is constructed for this repository rather than trusted from the API response. Keeping the repository public is required for this unauthenticated check.

## CI

`.github/workflows/ci.yml` runs on pull requests, main pushes and manual dispatch. GitHub-hosted Windows runners execute the .NET 10 Core suite, Python tooling tests and PowerShell fixtures. Pull requests do not execute on the production runner or receive signing secrets.

`.github/workflows/production.yml` runs on `v*` tag pushes, excluding tags containing a hyphen such as `v1.0.0-preview.1`, `v1.0.0-beta.1`, or `v1.0.0-rc.1`. Preview releases are uploaded separately and do not start production builds. Hosted preflight checks tag/config equality, Android version progression, main ancestry, Core and tooling tests. The dedicated runner then checks Unity prerequisites, runs CoreOffline/EditMode/PlayMode, and packages ordinary Windows and signed Android using the existing family release scripts. A separate clean checkout keeps Unity test regeneration out of the candidate source identity. Package validation checks the tagged commit, clean source, content completeness, Android signing identity and all distribution file hashes. The hosted final job revalidates downloaded artifacts and creates a **draft** GitHub release containing the packages, install guide, third-party notices, manifest and SHA256SUMS.txt. Existing drafts/releases are not overwritten.

The package manifest remains `acceptanceStatus=unqualified`. Automated packaging does not close the device/human rows in `Release/acceptance-matrix.json`. Review [the existing release qualification instructions](FAMILY_INSTALL_GUIDE.md) and `Tools/ReleaseQualification.py` before publishing. Publishing the reviewed stable draft makes it visible to players. Do not publish rehearsal or incomplete-content packages.

## One-time GitHub and runner setup

As of 2026-10-25, the existing permanent keystore's public fingerprint was
verified against `Release/release.json` and all four production environment
signing secrets below were configured. No secret values are stored in this
repository. The dedicated Actions runner is still absent; manual local
qualification/builds are possible with the installed Unity toolchain.

1. Merge these source/workflow changes into `main`. The production workflow only accepts tags on main's history.
2. In [repository environments](https://github.com/danlil240/race-game/settings/environments), use `production`, restricted to tag pattern `v*`. Add required reviewers there if the release owner wants a build approval gate.
3. Register a **dedicated Windows x64 Actions runner** for this repository, labeled `family-racing-production`. Use a separate machine/account from a personal checkout; do not run untrusted pull requests on it. Keep the Actions runner current for the pinned actions' Node runtime. Repository runner setup: Settings → Actions → Runners → New self-hosted runner.
4. Under the runner's service account install/activate Unity **6000.3.26f1** at `C:\Program Files\Unity\Hub\Editor`, Windows Build Support (IL2CPP), Android Build Support including SDK/NDK/OpenJDK, Git, .NET 10 SDK and Python 3.12. Run `Tools/Verify.ps1 -Mode CheckEnvironment` and a real EditMode pass as that account. A file prerequisite check does not establish a valid Unity license.
5. Populate the Unity/package and Gradle caches for this checkout/toolchain ahead of time. Android production packaging intentionally uses Gradle `--offline`; missing cached dependencies must be provisioned on the runner before a release.
6. Add these **environment secrets** to `production`, using the existing durable release keystore (do not create a new update signing identity):

   | Secret | Value |
   | --- | --- |
   | `FAMILY_RACING_KEYSTORE_BASE64` | Base64 of the release keystore bytes |
   | `FAMILY_RACING_KEY_ALIAS` | Alias in that keystore |
   | `FAMILY_RACING_KEYSTORE_PASSWORD` | Keystore password |
   | `FAMILY_RACING_KEY_PASSWORD` | Signing key password |

   Its public SHA-256 certificate fingerprint must equal `expectedCertificateSha256` in `Release/release.json`. The workflow writes the key to a unique temporary file and removes it in `finally`; it never uploads it. The build receives signing secrets only for packaging. Release creation uses GitHub's scoped job token; no personal token is needed in the player or workflow.

## Release a version

1. Change `versionName` in `Release/release.json` to the next stable three-part version; set `previousAndroidVersionCode` to the last shipped code and increase `androidVersionCode`. Keep application/company/product/signing identity stable so saves and Android in-place updates continue to work. Existing debug-key APKs cannot be updated in place by a release-key APK.
2. Merge the tested change into main. From a clean main checkout, tag that exact commit, for example `git tag v1.0.1`, then `git push origin v1.0.1` (use the actual configured version).
3. Watch “Production release” in GitHub Actions. A queued build usually means the dedicated labeled runner is absent/offline or the environment awaits approval. No real release run is possible until the runner, license, caches and signing secrets are supplied.
4. Download/test the draft artifacts, check checksums and the existing release acceptance evidence. Complete the required device/human review. Review the draft notes, then publish it as a stable release and mark it latest. Next launch of an older installed version will offer it. A game already running does not poll repeatedly.
5. On Windows, close the game and extract the new complete player folder before launching. On Android, install the same-identity/same-certificate APK with an increasing code. Follow the bundled install/backup guide; the updater never replaces saves.

## Troubleshooting

- No prompt: check installed version, published stable tag, matching exact platform asset name, and public repository visibility. GitHub's unauthenticated API can rate-limit shared IPs; the next launch tries again.
- Unity returns code 198: activate the editor license for the runner's account. `Verify.ps1` explicitly waits for the GUI process and propagates that exit code.
- Build blocked on full content/signing/evidence: use the existing named blocker. CI does not bypass production content or signing checks.
- A failed draft upload can leave an unpublished partial draft. Inspect/delete that draft before re-running; the workflow deliberately refuses to overwrite a published release.

References: [GitHub release API](https://docs.github.com/en/rest/releases/releases), [UnityWebRequest](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/networking/unitywebrequest).
