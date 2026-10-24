# Production CI and startup updates

The owner requested production CI and a GitHub version check when the game opens, and authorized using judgment to proceed on 2026-10-24.

Reuse the existing Unity 6000.3.26f1 Windows and signed Android family release pipeline. Run portable Core and tooling tests on GitHub-hosted runners for pull requests and pushes. Version tags run Unity verification and packaging on a dedicated Windows runner, under a GitHub `production` environment. Upload logs and publish the complete package set as a draft GitHub release. Publishing the reviewed draft makes it visible to players. Existing content, identity, signing, and qualification rules remain in force; CI does not certify outstanding device/human acceptance.

At startup the Windows/Android player asynchronously queries the public `danlil240/race-game` latest release endpoint once, with a six-second timeout. Compare stable three-part versions numerically with `Application.version`. Ignore drafts, prereleases, malformed versions and releases without a matching platform package. Offer a localized, dismissible menu notice with the installed/new version and a button opening that release's GitHub page. Offline, missing-release, rate-limit and malformed-response cases silently allow play. No automatic installation or credentials in the player.

Alternatives considered: GitHub-hosted Unity builds require a new licensing/build integration; a dedicated runner reuses the installed toolchain and offline Gradle caches. Automatic binary replacement adds installer/platform complexity beyond the requested suggestion. Choices are implementation judgments, not owner-stated requirements.

Runner registration, production environment secrets and draft publication require GitHub/release-owner setup. Keep release source clean, never publish rehearsal output, and document exact setup and tag/version progression. Work in the existing checkout to preserve and test against current gameplay edits; stage no unrelated files.
