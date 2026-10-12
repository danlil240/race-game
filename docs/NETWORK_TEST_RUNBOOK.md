# LAN Transport Test Runbook

Milestone 3 transport evidence. Scope: the pinned LAN transport
(Netcode for GameObjects 2.7.0 on Unity Transport 2.6.0) running on the
existing build pipeline — Windows Mono development/ordinary players and the
offline Android Gradle build. All gameplay is offline LAN; no Relay, Unity
Services authentication, or cloud lobby dependency exists anywhere in the
path.

## Fixed facts

| Item | Value |
|---|---|
| Gameplay socket | UDP 47777 default (`LanTransport.DefaultGameplayPort`), configurable per `StartHost`/`StartClient` so several processes can share one machine |
| Discovery socket | UDP 47778 default (`LanDiscovery.DefaultDiscoveryPort`), configurable |
| Discovery protocol | IPv4 UDP broadcast query/response on every active LAN adapter; 1-second query cadence; entries expire after 3 s; every datagram bounded to 1024 bytes |
| Validation sockets | Loopback only (`ValidationBridge`, ADB-forwarded on Android), token-protected, compiled out of ordinary builds — LAN work does not touch them |
| Manual join | Always available: `StartClient(address, port)` is unicast and independent of discovery; blocked broadcast only reduces auto-discovery, never the manual path |

## Automated gate

`Tools\Verify.ps1 -Mode PlayMode` runs the three smoke tests in
`Assets/FamilyRacing/Tests/PlayMode/LanTransportPlayTests.cs`:

- `HostClientExchangeUsesRealTransport` — real host/client UDP handshake and
  bidirectional payload round-trip inside one process.
- `StopReleasesSockets` — `Stop()` frees the bound port; a second host binds
  the exact same port and accepts a client.
- `DeniedAccessDoesNotOpenSockets` — a denied `ILanNetworkAccess` gate makes
  start fail immediately; no socket opens and no pending join lingers.

Latest recorded result: **124/124 PlayMode** (baseline 121 + 3 new),
`Logs/verify-playmode-results.xml`, 2026-10-12. Regression gates unchanged:
EditMode 393 + 1 explicit Ignore, Core 362.

## Windows: private-network firewall setup (do NOT disable the firewall)

Windows Defender Firewall may prompt the first time the player binds
UDP 47777/47778. Two supported setups:

1. Accept the prompt and check **Private networks only**.
2. Or pre-authorize the exact executable from an elevated PowerShell:

   ```powershell
   New-NetFirewallRule -DisplayName "Family Racing LAN (UDP In)" `
       -Direction Inbound -Protocol UDP -LocalPort 47777,47778 `
       -Profile Private -Program "C:\path\to\FamilyRacing.exe" -Action Allow
   ```

Notes:

- Same-machine testing (host + client on one PC) needs **no** rule — the
  firewall does not filter loopback.
- If the prompt was declined or the rule is missing, the client sees
  "access denied"/timeout and manual join by IP still reaches hosts that
  are allowed; fix by adding the rule above, never by disabling the
  firewall or switching the network profile to Public.

## Android permissions (verified on the exported project)

Exported target/compile SDK: **36** (automatic), min SDK **25**
(`unityLibrary/build.gradle` under the offline export). The merged
`unityLibrary` manifest carries exactly the three additions this slice
needs (`Assets/Plugins/Android/AndroidManifest.xml`):

- `INTERNET`, `ACCESS_NETWORK_STATE`, `CHANGE_WIFI_MULTICAST_STATE` — all
  Android *normal* permissions granted at install; **no runtime dialog is
  required or shown** for LAN unicast/broadcast on the actual OS/target
  combination.
- `NEARBY_WIFI_DEVICES` is intentionally **not** declared: the game uses
  plain UDP sockets, not Wi-Fi Aware/RTT, so requesting it would be an
  unjustified dangerous permission.
- `AndroidLanNetworkAccess.EnsureAccess()` acquires a
  `WifiManager.MulticastLock` so devices that filter broadcast replies
  still deliver them. Lock failure degrades discovery only — unicast
  manual join is unaffected.
- `LanNetworkAccess` reports `available|requesting|denied|unsupported`
  before any socket opens; denial fails start immediately
  (`DeniedAccessDoesNotOpenSockets`).

## Evidence log

| Date (UTC) | Check | Result |
|---|---|---|
| 2026-10-12 | `Verify.ps1 -Mode PlayMode` smoke tests incl. same-machine host↔client UDP exchange over real sockets | 124/124 pass (`Logs/verify-playmode-results.xml`) |
| 2026-10-12 | `Verify.ps1 -Mode Windows` Mono development build | Succeeded, 0 errors/warnings; `Unity.Netcode.Runtime.dll` + `Unity.Networking.Transport.dll` + updated `FamilyRacing.Runtime.dll` in `Builds/Windows/FamilyRacing_Data/Managed` |
| 2026-10-12 | `Verify.ps1 -Mode Android` export + offline Gradle | Succeeded; APK SHA-256 `9B6D7C4406C2CDD05414B4D162B1307FA046557BCF045564E883D6D7A4E5A12E`; merged manifest carries the three LAN permissions |

## Open evidence gates (explicit — hardware required)

These require physical devices that are not available in this environment.
They remain open gates for milestone qualification (Task 10), not silent
assumptions:

- Tablet-to-tablet handshake (two Galaxy Tab A7 Lite / supported tablets,
  internet disconnected).
- Mixed-platform handshake (Windows host ↔ Android client and reverse,
  internet disconnected).
- Discovery on a real home AP (including AP-isolation off) vs. manual
  join fallback when broadcast is blocked.
- Four-human session (four supported devices).
