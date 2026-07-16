# LanLink

Cross-platform file and text sharing between computers and phones on the same LAN — or across the internet. Like EasyJoin/KDE Connect, but simpler.

## Features

- **Auto-discovery**: all instances on the same LAN find each other automatically (UDP broadcast)
- **Text messaging**: send text instantly between devices
- **File transfer**: send single files or entire directories with live progress
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
- **Autostart prompt**: setup asks whether LanLink should start automatically when Windows starts (minimized to the tray). This writes an `HKLM\...\Run` entry.
- **Upgrades**: the MSI's `ProductVersion` tracks the exe version, so installing a newer build automatically removes the old one. Before replacing files it tells any running LanLink to quit via the `--exit` IPC command and waits for it to release the executable.

Bump `VERSION` at the top of `build-msi.bat` for each release — it flows into both the exe version and the MSI `ProductVersion`.

### Command-line flags

| Flag | Effect |
|------|--------|
| `--minimized` | Start hidden in the tray (also honored automatically when "Start minimized" is enabled in Settings). |
| `--exit` | Signal the already-running instance to exit cleanly, then wait for it to terminate. Used by the installer during upgrades. |

LanLink is single-instance: launching it again brings the existing window forward (a `--minimized` relaunch is ignored so it won't pop the window open at boot).

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
2. On the connecting side, enter the public IP or domain in the "Remote address" field
3. Once connected, all LAN peers on both sides can see each other (bridging)

## Download folder

| Platform | Default location |
|----------|-----------------|
| Windows | `%USERPROFILE%\Downloads\LanLink\` |
| Android | `/storage/emulated/0/Android/data/com.lanlink.mobile/files/LanLink/` |

Configurable in Settings on both platforms. Files with name conflicts are saved as `file (2).ext`, `file (3).ext`, etc.

## License

Do whatever you want with it.
