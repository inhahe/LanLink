# LanLink — project instructions

## Versioning

- **The `VERSION` file (next to this document) is the single source of truth for the app version.** It holds a 4-part version like `1.0.1.0`. `build-msi.bat`, `build-apk.bat`, and `release-github.bat` all read it.
- **Bump the version in `VERSION` whenever the project is rebuilt for distribution** (a new exe / MSI / APK that will be shipped). Which part you bump depends on what actually changed: **patch** (third number, `1.0.1.0` → `1.0.2.0`) for bug fixes, internal refactors, or a plain rebuild; **minor** (second number, `1.0.1.0` → `1.1.0.0`) for a new feature or a noticeable change to an existing one; **major** (first number, `1.0.1.0` → `2.0.0.0`) for either a breaking change — anything that changes the discovery/transfer protocol between instances or invalidates saved settings — or a milestone overhaul, such as a redesigned desktop/mobile UI or a substantially reworked feature set, even when older instances still interoperate. Keep the two `.csproj` version properties (`LanLink/LanLink.csproj` `<Version>/<FileVersion>/<AssemblyVersion>` and `LanLink.Mobile`'s `ApplicationDisplayVersion`) consistent with it — the build scripts override them via `-p:` on the command line, so the csproj values are just defaults, but update them when you bump so a plain `dotnet build` matches.
- Bumping the version matters for real reasons: the MSI `ProductVersion` must increase for Windows to treat an install as an upgrade, and the Android `versionCode` (derived from the version by `build-apk.bat`) must increase for Android to accept an update.

## Releasing

- `release-github.bat` publishes a GitHub release to `github.com/inhahe/LanLink`, tagged `v<version>` from the `VERSION` file. It **refuses to publish if a release for that version already exists** — bump `VERSION` first. It rebuilds the installer + self-contained exe, best-effort builds the APK, and uploads `LanLink-<version>.exe`, `LanLink-<version>.msi`, and (when built) `LanLink-<version>.apk`.
- Requires the GitHub CLI (`gh`) authenticated with `repo` scope.

## Android SDK note

- The Android build needs `android-35`, which lives in the per-user SDK at `%LOCALAPPDATA%\Android\Sdk` (the Program Files SDK only has `android-36`). `build-apk.bat` auto-points at the per-user SDK when that platform is present.
