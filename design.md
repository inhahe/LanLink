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
- Text messages, single files, whole directories, with live progress
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
- Download folder defaults to
  `/storage/emulated/0/Android/data/com.lanlink.mobile/files/LanLink/`.
- Requires the `maui-android` workload **and SDK platform android-35**, which
  only exists in the per-user SDK at `%LOCALAPPDATA%\Android\Sdk` on this
  machine — `build-apk.bat` points the build at it.

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
