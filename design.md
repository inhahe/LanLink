# LanLink — design

Cross-platform file and text sharing between machines on a LAN, and between LANs
over the internet. Two apps, one wire protocol:

| Project | Path | UI |
|---|---|---|
| Desktop | `LanLink/` | WPF, `net8.0-windows` (WinForms interop for the tray icon) |
| Mobile | `../LanLink.Mobile/LanLink.Mobile/` | .NET MAUI, `net9.0-android` |

The two projects are near file-for-file mirrors: `Protocol.cs`, `Discovery.cs`,
`NetworkManager.cs`, `PeerConnection.cs`, `TransferManager.cs`, `MessageStore.cs`,
`Peer.cs`, `AppSettings.cs` and `LinkBehavior.cs` exist in both, with only the UI
layer and platform glue differing. **There is no shared project** — the files are
duplicated, so a change to protocol or networking behaviour has to be made in
both copies or the platforms stop interoperating.

## Features

- Automatic peer discovery on the LAN (UDP broadcast, every 3 s, on every active
  interface's subnet broadcast address)
- Text messages, single files, whole directories, with live progress — on
  both platforms (mobile browses storage with its own file picker)
- Remote connect: reach another instance over the internet by IP/host
- LAN bridging: one remote link makes *both* LANs' peers mutually visible,
  messages relayed hop-by-hop
- Auto-accept of incoming files into a configurable download folder
- Drag & drop onto the desktop window
- Desktop: system tray, single-instance, start-with-Windows, firewall setup
- Persistent history, known peers, and queued messages for offline peers

## Network layer

**Port 37656** by default — TCP for data, UDP for discovery.

- `Protocol.cs` — `WireMessage` (one flat JSON class covering every message kind,
  nullable fields per kind), `MessageTypes` string constants, and the binary
  frame codec: `[4B header len][4B payload len][UTF-8 JSON header][raw payload]`,
  lengths big-endian. Bulk bytes ride in the payload, never in the JSON.
- Message kinds: `hello`, `peer_list`, `text`, `file_start`, `file_chunk`,
  `file_end`, `dir_start`, `dir_end`, `relay`, `ping`, `pong`.
- `Discovery.cs` — UDP broadcast announce/listen; skips loopback interfaces.
  Android additionally holds a WiFi `MulticastLock` so broadcasts are received.
- `PeerConnection.cs` — one TCP connection: framing, send queue, message events.
- `NetworkManager.cs` — the core. Owns the listener, discovery, the connection
  table and the routing table.
  - `Start()` binds the TCP listener with `ExclusiveAddressUse` so a second copy
    can't half-start; failure surfaces as `PortInUseException`.
  - After discovery, **the node with the lexicographically lower ID dials**, so
    two peers don't establish two connections to each other.
  - `OnConnectionMessage` dispatches `hello` / `peer_list` / `relay` / `ping`
    itself and raises `MessageReceived` for everything else (the application
    messages `TransferManager` handles).
  - A fresh `hello` on a second connection from a known node *replaces* the
    existing entry — the old one is nearly always stale (peer rebooted, no FIN),
    and rejecting the new one would lock the peer out permanently.
  - `SendToAsync(targetNodeId, msg, payload)` routes: direct if the peer is a
    local connection, otherwise wrapped in a `relay` envelope toward its
    next hop.
- **Bridging**: peers exchange peer lists and propagate them onward, so every
  node learns a route (next-hop) to every node on the far LAN. Relay envelopes
  carry a hop list for loop detection; max depth 4.
- Inbound connections from outside the local network are rejected unless
  `AcceptExternalConnections` is on. Outgoing connections the user initiates are
  always allowed.

## Application layer

- `TransferManager.cs` — chunks files at 256 KB, streams them, reports progress
  ~2×/second; reassembles incoming files, resolving name collisions as
  `file (2).ext`. Handles the `text` / `file_*` / `dir_*` messages.
- `MessageStore.cs` — persists chat history, known peers and pending (unsent)
  messages to `%LOCALAPPDATA%\LanLink\store.json`.
- `AppSettings.cs` — `%LOCALAPPDATA%\LanLink\settings.json`. Display name,
  download folder, port, `RunOnStartup`, `StartupMode`,
  `AcceptExternalConnections`, `AutostartRequestHandled`, saved remotes.
  `StartMinimized` is a legacy bool migrated to `StartupMode` on load.
  `ExistedOnDisk` (not persisted) distinguishes "never configured" from
  "configured to the defaults".

## Desktop specifics

- `App.xaml.cs` — single instance via a named `Mutex`, plus two named
  `EventWaitHandle`s as IPC: *show* (a manual re-launch surfaces the running
  window) and *exit* (`--exit`, used by the installer to unlock the exe before
  an upgrade). A duplicate launch carrying `--minimized` / `--minimized-taskbar`
  exits silently instead of popping the window, so autostart can't undo the
  chosen start mode. Also consumes the installer's autostart request, then
  applies `StartupMode` (CLI flag overrides the saved setting).
- `MainWindow.xaml.cs` — UI, tray icon (created in the constructor so it exists
  even in tray-only mode; its menu has Show / Settings / Exit), drag & drop,
  logging, and first-run firewall setup via an elevated `netsh` batch.
  `RequestExit()` distinguishes a window that was shown (`Close()`) from one that
  never was (direct cleanup — a never-shown WPF window has no HWND, and things
  like setting `Owner` on it throw).
- `Autostart.cs` — sole owner of the "start with Windows" registry state.
  **Invariant: exactly one `Run` entry, per-user (`HKCU`), written only by the
  app.** Windows launches once per `Run` entry and each carries its own mode
  flag, so a second (machine-wide) entry would race the first and could override
  the mode chosen in Settings — and removing an `HKLM` entry needs elevation the
  app doesn't have. The installer therefore writes only a *request*
  (`HKLM\SOFTWARE\LanLink\AutostartRequest` = `<version>|<1 if ticked>`), which
  `ConsumeInstallerRequest` applies on the first run after that install and
  records in `AutostartRequestHandled`, so setup's answer lands exactly once and
  later changes in Settings stick. `Apply()` also deletes a leftover `HKLM` entry
  from installers ≤ 1.0.3.0 when running elevated, and reports when it can't.
- `LinkBehavior.cs` — attached property turning URLs in message text into
  clickable hyperlinks.

## Mobile specifics

- `MainPage` / `SettingsPage` mirror the desktop window and settings dialog.
- `Platforms/Android/AndroidManifest.xml` declares the network/storage
  permissions; `MainActivity` acquires the WiFi `MulticastLock`.
- **`LanLink.Mobile.csproj` must set `<AndroidManifest>` explicitly.** Left to
  the default, `$(AndroidManifest)` evaluates to the *empty string* in this
  project, and the .NET Android build then synthesises the manifest purely from
  `[assembly: UsesPermission]` attributes — `Platforms/Android/AndroidManifest.xml`
  is never read. It silently swallowed both storage permissions,
  `usesCleartextTraffic` and the icon attributes while sitting there looking
  authoritative; the `[assembly:]` attributes in `MainActivity.cs` are a partial
  workaround for the same problem, covering only the four network permissions.
  Symptom when it regresses: a permission is present in the XML, absent from
  `adb shell dumpsys package com.lanlink.mobile`, and its toggle is greyed out in
  Android's settings because the app never requested it. Assembly attributes are
  merged *into* the file, so both sources work once it's wired up.
- Download folder defaults to
  `/storage/emulated/0/Android/data/com.lanlink.mobile/files/LanLink/`.
- Requires the `maui-android` workload **and SDK platform android-35**, which
  only exists in the per-user SDK at `%LOCALAPPDATA%\Android\Sdk` on this
  machine — `build-apk.bat` points the build at it.

### The network outlives the page

`MainPage` constructs `NetworkManager`/`TransferManager`, but **must not dispose
them in `OnDisappearing`**. That event fires whenever the page stops being the
visible one — pushing a modal (the file browser), navigating to Settings, the app
being backgrounded, or the screen locking — and `OnAppearing` is guarded by
`_started`, so nothing ever restarted it. The result was an app that looked
healthy (the peer list still showed the last known state, so the send buttons
stayed enabled) while holding no listener, no discovery and no connections: sends
failed with *"No active connection to next-hop"* and nothing could reach the
phone. A LAN sharing app also *wants* to keep listening while backgrounded.
Teardown is therefore hooked to `Window.Destroying`.

Related trap when reading a failure like that: a route can exist with **no
connection behind it**. `OnLanPeerDiscovered` sets `_routes[node] = node` the
moment a UDP announce arrives, long before (or entirely without) a TCP session,
and `_routes` is never pruned for direct peers on disconnect. So
`SendToAsync` finding a route proves nothing about reachability.

### Send controls and the "nothing happens" trap

The send controls are gated on peer selection: text needs a selected peer (it
queues if the peer is offline), files and folders need a *connected* one.
`UpdateSendControls()` is the single place that applies that, and it runs on
selection change **and** whenever the selected peer's connection state moves
(`RefreshIfSelected`).

Two things about this are easy to get wrong, and both were live bugs:

- **A disabled control must look disabled.** Setting `BackgroundColor` /
  `TextColor` inline on a MAUI `Button` overrides Android's default disabled
  rendering, so a dead button still painted itself vivid blue. The styles in
  `App.xaml` (`PrimaryButton`, `AccentButton`, `SubtleButton`, `SendEntry`)
  therefore declare `Normal` **and** `Disabled` visual states explicitly. Don't
  set those colours inline on the control.
- **A disabled control swallows the tap silently**, which reads as the app being
  broken. `NoPeerBlocker` / `OfflineBlocker` are transparent tap-catchers layered
  over the send panel that fire a toast explaining the gate instead, and pulse
  the peer list. They are driven by the same `UpdateSendControls()`.

Likewise the peer row's selection highlight comes from a `VisualStateManager` on
the `SwipeView` — the `DataTemplate`'s **root** element, which is the only one
`CollectionView` drives the `Selected` state on. The inner `Grid` must stay
`Transparent`; hardcoding `White` there paints over the highlight and makes
tapping a peer look like it did nothing.

### Staying reachable in the background

Android throttles a backgrounded app's networking: measured on an S21 (One UI 6 /
Android 14), `PC -> phone:37656` succeeded in the foreground, failed on every
attempt after HOME, and recovered immediately on return — while the listening
socket was still open and the process alive. `LanLinkForegroundService` is the
fix: a `dataSync` foreground service that gives the process foreground priority
and holds a `WifiLock` (`FULL_HIGH_PERF`, because the modern `LOW_LATENCY` mode
only applies while the app *is* foregrounded).

**The service does not own the network.** `MainPage` still constructs and owns
`NetworkManager`/`TransferManager`; the service only keeps the process alive
around them. That is why it returns `NotSticky` — an Android-initiated restart
would post a "reachable" notification with no network behind it. It is started
from `StartNetwork()` only after the listener binds, and stopped from
`Window.Destroying` alongside disposal. Moving network ownership into the service
is the fuller fix and a larger refactor.

`POST_NOTIFICATIONS` (13+) governs only whether the status notification is
*visible*; the service runs regardless, so a refusal is never fatal — and for the
same reason **the request must come after `StartNetwork()`, never before**.
`Permissions.RequestAsync` does not return until the user answers the system
dialog, so awaiting it first stalls `OnAppearing` indefinitely and the listener
never binds: the app comes up with no sockets at all, looking identical to the
`OnDisappearing` teardown bug above.

### A CollectionView needs a bounded height

Both `CollectionView`s on `MainPage` must sit in a **`Grid` row**, never directly
in a `VerticalStackLayout`. A stack layout measures its children with infinite
height, so the list sizes itself to its whole content, overflows its parent and
gets clipped — it then scrolls by the few pixels of slack and no further. The
activity log hit exactly this: every new entry, transfer progress included,
landed below the fold and was unreachable, which read as "transfers report
nothing". Bounding the height also restores
`ItemsUpdatingScrollMode="KeepLastItemInView"`, which silently does nothing while
the list is unbounded. The peer list escapes the bug only because it carries an
explicit `HeightRequest`.

### Browsing files to send

`FileBrowserPage` is an in-app file/folder browser over real filesystem paths
(storage roots → directories → multi-select files, or drill in and send a whole
folder). `MainPage` offers it from **Send Files** and **Send Folder**, with
**Pick from other apps** as an escape hatch to Android's own picker.

It exists because Android's Storage Access Framework is a poor fit here twice
over: it returns opaque `content://` URIs rather than paths, so
`TransferManager.SendDirectoryAsync` — which walks a real directory tree — would
need the whole folder copied into the cache first; and the picker only surfaces
whichever `DocumentsProvider`s the OEM chooses to show, which on a Samsung device
can be nothing but Google Drive. The SAF path is still there for files that
genuinely aren't on the filesystem (Drive, other apps), routed through
`EnsureLocalPathAsync`, which copies the stream to the cache before sending.

The cost is `MANAGE_EXTERNAL_STORAGE` ("All files access"), gated by
`StoragePermission` in `PlatformHelpers.cs`. It cannot be granted from a runtime
prompt on Android 11+ — the user has to flip it in system Settings, so the
helper can only explain and open that screen, and the caller backs off until they
return. Android 10 and below take the ordinary `StorageRead` runtime prompt
instead. Google Play restricts apps that declare this permission, which doesn't
bind LanLink: it ships as a sideloaded APK from GitHub releases.

`Toasts` (same file) wraps the platform toast, because MAUI has no built-in one
and `CommunityToolkit.Maui` isn't worth a dependency for it.

## Build, versioning and release

- `VERSION` (repo root) is the single source of truth. `build-msi.bat`,
  `build-apk.bat` and `release-github.bat` all read it; the `.csproj` version
  properties are defaults kept in sync by hand. Bump it whenever something is
  rebuilt for distribution — the MSI `ProductVersion` must increase for Windows
  to treat an install as an upgrade, and the derived Android `versionCode` must
  increase for Android to accept an update.
- `installer/LanLink.wxs` — WiX v4, per-machine, `ProgramFiles6432Folder\LanLink`.
  A `RegistrySearch` on `HKLM\SOFTWARE\LanLink\InstallDir` makes upgrades reuse a
  custom install path; `StopRunningLanLink` runs `LanLink.exe --exit` before
  `InstallValidate` on upgrades. The autostart wizard page sets `AUTOSTART`,
  which feeds the request value described above.
- `release-github.bat` publishes `v<version>` to `github.com/inhahe/LanLink`,
  refusing to overwrite an existing release. `copy-to-github.bat` mirrors sources
  to `D:\github\lanlink` (excluding `todo*.txt`).
