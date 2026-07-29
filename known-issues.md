# LanLink — known issues and technical debt

Open items only; remove an entry when it's fixed.

## Bugs / warnings

### CS0618 — `Application.MainPage` is obsolete (Android)
`LanLink.Mobile/App.xaml.cs:8` sets `MainPage`, which .NET 9 MAUI deprecates in
favour of overriding `CreateWindow(IActivationState)` and returning a `Window`
wrapping the `AppShell`. Builds with a warning today; will break on a future
MAUI major. **Fix:** replace the `MainPage = new AppShell();` assignment with a
`CreateWindow` override.

### CS8602 — possible null dereference in mobile `Discovery.cs`
`LanLink.Mobile/Discovery.cs:51` dereferences a value the compiler can't prove
non-null. Needs a look at whether the null case is actually reachable (a socket
or interface-address lookup returning null) rather than just silencing it with
`!`. **Fix:** guard the null case explicitly, or restructure so the value can't
be null.

## Technical debt

### Desktop and mobile duplicate the entire protocol/network layer
`Protocol.cs`, `Discovery.cs`, `NetworkManager.cs`, `PeerConnection.cs`,
`TransferManager.cs`, `MessageStore.cs`, `Peer.cs` and `AppSettings.cs` exist as
separate copies in `LanLink/` and `LanLink.Mobile/LanLink.Mobile/`. Any protocol
change must be made twice, and a missed edit silently breaks interoperability
between the two apps — the failure shows up as "the phone can't see the PC",
which is slow to diagnose. **Fix:** extract a shared `LanLink.Core` project
(`netstandard2.0`/`net8.0`) referenced by both, with the few platform-specific
bits behind an interface (download folder, multicast lock, logging sink).

### No local version control
Neither `D:\visual studio projects\LanLink` nor its parent is a git repository;
history only exists as whatever has been copied to `D:\github\lanlink` and
published as GitHub releases. **Fix:** `git init` the project directory with a
`.gitignore` covering `bin/`, `obj/`, `publish/`, `*.msi`, `*.wixpdb`, `*.apk`,
`*.exe`, `*.log`.

### Stale v1.0.2.0 GitHub release
The published v1.0.2.0 assets predate the autostart fixes that were meant to be
in that release (they were built ~15 h before the fix landed), so anyone
downloading v1.0.2.0 gets a build that behaves like 1.0.1.0 in that area.
**Fix:** delete the release, or edit its notes to point at a later version.
