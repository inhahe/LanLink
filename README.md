# LanLink

Cross-platform file and text sharing between computers and phones on the same LAN — or across the internet. Like EasyJoin/KDE Connect, but simpler.

## Features

- **Auto-discovery**: all instances on the same LAN find each other automatically (UDP broadcast)
- **Text messaging**: send text instantly between devices
- **File transfer**: send single files or entire directories with live progress, from desktop or phone
- **Built-in file browser** (Android): pick files and folders straight off the phone's storage, instead of fighting Android's system picker
- **Remote connect**: connect to any instance over the internet by entering its IP/domain
- **LAN bridging**: if *any* device on your LAN connects to a remote device, all LAN devices can see and send to all devices on the remote's LAN — multi-hop relay with loop detection
- **Auto-accept**: received files save to a configurable download folder
- **Drag & drop** (desktop): drop files/folders onto the window to send
- **Zero configuration**: works out of the box, no accounts or pairing needed

## Platforms

| Platform | Project | UI Framework |
|----------|---------|-------------|
| Windows (desktop) | `LanLink/` | WPF (.NET 8+) |
| Android (mobile) | `LanLink.Mobile/` | .NET MAUI |

Both use the same wire protocol and discover each other seamlessly — send a file from your phone to your PC or vice versa.

## Building

### Desktop (Windows)

```
dotnet build LanLink/LanLink.csproj -c Release
dotnet publish LanLink/LanLink.csproj -c Release -r win-x64 --self-contained false -o publish
```

The exe lands in `publish/LanLink.exe`. Requires .NET 8+ runtime on the target machine.

For a fully self-contained exe (no .NET install needed):
```
dotnet publish LanLink/LanLink.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-standalone
```

### Installer (MSI)

Run `build-msi.bat` to produce `LanLink-<version>.msi` in the current directory. It publishes a self-contained single-file exe and packages it with [WiX](https://wixtoolset.org/) (installed automatically as a global dotnet tool if missing).

- **Install location**: `C:\Program Files\LanLink` (a 64-bit package; a 32-bit build would use `C:\Program Files (x86)\LanLink`).
- **Autostart prompt**: setup asks whether LanLink should start automatically when Windows starts (minimized to the tray). Setup only *records* that answer; LanLink applies it on first run by writing its own per-user entry — see [Starting with Windows](#starting-with-windows).
- **Upgrades**: the MSI's `ProductVersion` tracks the exe version, so installing a newer build automatically removes the old one. The previous install location is remembered (via `HKLM\SOFTWARE\LanLink\InstallDir`), so upgrades reinstall to the same folder even if you originally chose a custom path. Before replacing files it tells any running LanLink to quit via the `--exit` IPC command and waits for it to release the executable.

Bump the `VERSION` file next to `build-msi.bat` for each release — it is the single source of truth and flows into the exe version, the MSI `ProductVersion`, the APK `versionName`/`versionCode`, and the GitHub release tag.

### Releasing

`release-github.bat` publishes a GitHub release tagged `v<version>` from the `VERSION` file, attaching the self-contained exe, the MSI, and (when it builds) the APK. It refuses to publish if that version was already released, so bump `VERSION` first.

### Command-line flags

| Flag | Effect |
|------|--------|
| `--minimized` | Start hidden in the tray (only the tray icon shows). |
| `--minimized-taskbar` | Start minimized to the taskbar (window exists but is minimized). |
| `--exit` | Signal the already-running instance to exit cleanly, then wait for it to terminate. Used by the installer during upgrades. |

The startup appearance is normally driven by the **When LanLink starts** setting (Show window normally / Start minimized (taskbar) / Start minimized to tray); the flags above are how the Windows-startup entry passes that choice and override the setting for a single launch.

LanLink is single-instance: launching it again brings the existing window forward and exits the new copy (a `--minimized`/`--minimized-taskbar` relaunch is ignored so autostart won't pop the window open at boot). It claims the network port exclusively, so if a stale copy is already running, a second launch surfaces that copy rather than starting a second, half-working instance.

### Settings

Open **⚙ Settings** from the bottom-right of the window, or **Settings…** on the tray icon's right-click menu (the tray route matters when LanLink starts hidden and there's no window to click). Available options:

| Setting | Effect |
|---|---|
| Display name | How this device appears to other peers. |
| Download folder | Where received files are saved. |
| Port | TCP/UDP port (requires restart). |
| When LanLink starts | Show window normally / Start minimized (taskbar) / Start minimized to tray. |
| Start LanLink when Windows starts | Adds or removes the autostart entry. |
| Accept connections from outside the LAN | Off by default; see [Remote connections](#remote-connections). |

#### Starting with Windows

Exactly one autostart entry exists, and only LanLink writes it: a per-user `HKCU\...\Run` value whose command-line flag matches the chosen start mode.

That matters because Windows launches an app once per `Run` entry. If a machine-wide `HKLM` entry existed alongside the per-user one, each launch would carry its own mode flag and whichever instance won the single-instance race would decide how the app appears — so setup's mode could silently override the one picked in Settings, and turning autostart off would look like it did nothing. Removing an `HKLM` entry also needs administrator rights the app doesn't have.

So the installer never writes a `Run` entry. Its autostart checkbox writes `HKLM\SOFTWARE\LanLink\AutostartRequest` = `<version>|<1 if ticked>`, and LanLink applies that on its first run after the install, creating or removing the per-user entry to match. The version stamp means each install's answer is applied exactly once: change the setting afterwards and it sticks, because the stamp hasn't changed.

Installs from **1.0.3.0 and earlier** did create a machine-wide entry. Upgrading removes it. To check or clear one by hand, from an elevated prompt:

```
reg query  "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v LanLink
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v LanLink /f
```

Saving Settings also drops such a leftover entry when LanLink happens to be elevated, and warns in the log when it can't.

Note that the checkbox reflects the *actual* registry state (either hive), not a saved flag, so it stays honest about an entry an old installer created.

### Android

Requires the MAUI Android workload:
```
dotnet workload install maui-android
```

Then build the APK:
```
dotnet publish LanLink.Mobile/LanLink.Mobile.csproj -c Release -f net9.0-android
```

The APK is at `LanLink.Mobile/bin/Release/net9.0-android/publish/com.lanlink.mobile-Signed.apk`. Sideload it via ADB or copy to your phone.

A convenience script is included: run `build-apk.bat`.

## Staying reachable on Android

While LanLink is open it shows an ongoing **"Reachable by your other devices"**
notification. That is a foreground service, and it is what lets other devices
still reach the phone once you switch away from the app — Android otherwise
throttles a backgrounded app's networking, so inbound connections silently fail
even though the app is still running. It also keeps the WiFi radio out of
power-save so the phone answers with the screen off.

Closing LanLink stops the service and the notification. Sending *from* the phone
never needed it; this only affects other devices reaching *in*.

If inbound still fails after backgrounding, check Android **Settings → Apps →
LanLink → Battery** and set it to **Unrestricted** — Samsung's power management
can sleep the app regardless.

## Sending files from Android

Tap a peer in the list first — the row highlights and the send panel switches
from *"Select a peer above to send to"* to *"Sending to &lt;name&gt;"*. Until then the
message box and the send buttons are greyed out, and tapping them explains why.

Three ways to choose what to send:

| Button | What it does |
|---|---|
| **Send Files** | Built-in browser: storage roots, then folders, then tap files to tick them. |
| **Send Folder** | Same browser; open a folder and send the whole tree. |
| **Pick from other apps** | Android's own picker, for things not on the filesystem (Google Drive, other apps). Files only. |

The first two need Android's **All files access** permission, because scoped
storage otherwise forbids an app from listing shared storage at all. LanLink
asks the first time and offers to open the settings screen — turn on *"Allow
access to manage all files"*, press Back, and tap the button again. If you'd
rather not grant it, **Pick from other apps** works without it.

> Google Play restricts apps that request this permission. That doesn't affect
> LanLink, which is distributed as a sideloaded APK from GitHub releases.

## How it works

### Network

- **Port 37656** — TCP for data, UDP for discovery (same port number, different protocols)
- **Discovery**: every 3 seconds each instance sends a UDP broadcast on every active network interface's subnet broadcast address
- **Connection**: after discovery, the node with the lexicographically lower ID initiates a TCP connection; both exchange Hello messages
- **Protocol**: binary framed — `[4B header len][4B payload len][UTF-8 JSON header][raw binary payload]`
- **File transfer**: files are chunked at 256 KB and streamed; progress is reported ~2x/second

### Bridging (the interesting part)

When device A on LAN 1 connects to device B on LAN 2 over the internet:

1. A and B exchange their peer lists over TCP
2. Each propagates the remote peers to its own LAN connections
3. Every device on LAN 1 learns routes to LAN 2 devices (next-hop = A)
4. Every device on LAN 2 learns routes to LAN 1 devices (next-hop = B)
5. Messages to non-local peers are wrapped in `relay` envelopes and forwarded hop-by-hop
6. Loop detection via hop-list tracking; max relay depth = 4 hops

This means: connect *one* device on each LAN and everything else is automatic.

### Firewall

On first launch (Windows), the app creates inbound firewall rules for TCP/UDP port 37656 via an elevated `netsh` command (you'll see one UAC prompt). If you decline, LAN discovery won't work until you manually allow the port.

On Android, no firewall setup is needed — the manifest declares the required permissions and the app acquires a WiFi MulticastLock for reliable broadcast reception.

### Remote connections

To connect two instances over the internet:
1. Forward port 37656 (TCP) on the remote router to the target machine
2. On the **target** machine, enable **Accept connections from outside the LAN** in Settings (off by default — inbound non-LAN connections are rejected for safety). Connections you *initiate* with the "Remote address" field always work regardless of this setting.
3. On the connecting side, enter the public IP or domain in the "Remote address" field
4. Once connected, all LAN peers on both sides can see each other (bridging)

## Download folder

| Platform | Default location |
|----------|-----------------|
| Windows | `%USERPROFILE%\Downloads\LanLink\` |
| Android | `/storage/emulated/0/Android/data/com.lanlink.mobile/files/LanLink/` |

Configurable in Settings on both platforms. Files with name conflicts are saved as `file (2).ext`, `file (3).ext`, etc.

## License

Do whatever you want with it.
