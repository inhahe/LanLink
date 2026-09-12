# LanLink — known issues and technical debt

Open items only; remove an entry when it's fixed.

## Bugs / warnings

### NETSDK1202 — `net9.0-android` is out of support
The mobile project targets `net9.0-android`, which no longer receives security
updates. It is the only warning either project emits. **Fix:** retarget to
`net10.0-android`, which needs the matching `maui-android` workload and an SDK
platform newer than the android-35 currently pinned in `build-apk.bat`.

## Technical debt

### Desktop and mobile duplicate the protocol/network layer
`Protocol.cs`, `Discovery.cs`, `NetworkManager.cs`, `PeerConnection.cs`,
`TransferManager.cs`, `MessageStore.cs`, `Peer.cs` and `AppSettings.cs` exist as
separate copies in `LanLink/` and `LanLink.Mobile/LanLink.Mobile/`. Any protocol
change must be made twice, and a missed edit silently breaks interoperability —
the failure presents as "the phone can't see the PC", which is slow to diagnose.

*This is not hypothetical.* On 2026-09-12 mobile's `HandleHello` was still doing
"first connection wins" (disposing the newcomer) long after desktop had switched
to replacing the stale entry, so when both ends dialled simultaneously they kept
different sockets and mobile ended up with none.

Current state after that day's sync:

| File | Status |
|---|---|
| `Protocol.cs`, `PeerConnection.cs` | byte-identical |
| `NetworkManager.cs` | 703 desktop / 641 mobile |
| others | diverged (UI/platform glue, mostly legitimately) |

Mobile is still missing desktop's `AutoConnectAsync` and
`EstablishConnectionAsync`, which provide **dial de-duplication** (one in-flight
connect per peer, via `_connectInProgress`) and **quiet retry logging** (via
`_connectFailLogged`, so an unreachable peer logs once rather than every
discovery tick, currently every 3 s).

**Fix:** extract a shared `LanLink.Core` project (`netstandard2.0`/`net8.0`)
referenced by both, with the platform-specific bits behind an interface
(download folder, multicast lock, logging sink). Porting the two methods by hand
is the cheaper half-measure and leaves the underlying duplication in place.

### Half of the desktop's connection management is untested on mobile
`ConfigureKeepAlive` and `CloseIfNoHelloAsync` were ported to mobile on
2026-09-12 but only compile-checked — neither a peer vanishing without a FIN nor
a silent connection has been exercised on the phone. **Verify:** kill WiFi on the
phone mid-connection and confirm the desktop marks it offline within ~30 s;
`nc <phone> 37656` without sending anything and confirm the socket is dropped
after 15 s.
