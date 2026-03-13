# Building AES Lacrima

AES Lacrima uses [NUKE](https://nuke.build/) to drive local builds, tests, publishing, and CI automation.

## Prerequisites

- .NET SDK 10
- Git
- Bash on Linux/macOS, or PowerShell/CMD on Windows

For local packaging work, you may also need platform-specific tools:

- macOS bundling: `sips`, `iconutil`, and `codesign`
- Linux desktop integration script: `ffmpeg`, `update-desktop-database`, `gtk-update-icon-cache`, and `gio` are optional
- Linux AppImage packaging: `linuxdeploy` and `appimagetool`, or an equivalent AppImage toolchain

## Build entrypoints

- Linux/macOS: `./build.sh`
- Windows PowerShell: `./build.ps1`
- Windows CMD: `build.cmd`

Common NUKE targets:

- `Compile`
- `Run`
- `Test`
- `Publish`

## Local development

### Compile

```bash
./build.sh Compile
```

### Run

```bash
./build.sh Run
```

### Test

```bash
./build.sh Test
```

Test results and coverage reports are written under `output/test-results`.

## Publishing

Published output is written under `output/publish/<Configuration>/<RuntimeIdentifier>`.

Use `Release` for distributable builds.

### Windows

```bash
./build.sh Publish --configuration Release --runtime win-x64
```

### macOS (Apple Silicon)

```bash
./build.sh Publish --configuration Release --runtime osx-arm64
```

On macOS, `AES_Lacrima/Mac/publish-macos.sh` runs automatically after publish and converts the publish output into `AES_Lacrima.app`.

### Linux x64

```bash
./build.sh Publish --configuration Release --runtime linux-x64
```

### Linux ARM64

```bash
./build.sh Publish --configuration Release --runtime linux-arm64
```

Raw Linux publish output is useful for local testing, but it is not the recommended end-user distribution format.

## Linux distribution policy

Linux releases should be distributed as AppImages only.

- Do not upload raw Linux publish folders as CI artifacts.
- Do not attach raw Linux binaries to GitHub Releases.
- Supported Linux release assets should be:
  - `AES-Lacrima-x86_64.AppImage`
  - `AES-Lacrima-aarch64.AppImage`

This keeps Linux distribution portable and avoids encouraging unsupported binary drops.

## Linux desktop assets

The repository already includes the assets needed for Linux packaging:

- Desktop entry template: `AES_Lacrima/Linux/aes-lacrima.desktop`
- Icon: `AES_Lacrima/Assets/AES.png`
- Local desktop installer script: `AES_Lacrima/Linux/install-desktop-entry.sh`

The install script is intended for local desktop integration after publishing. It should not be used as a real installation step in CI runners.

## AppImage packaging

AppImage packaging should start from a Linux publish directory and then build an AppDir containing:

- the published executable and dependencies
- the desktop file
- the application icon

Recommended tooling:

- AppImage packaging guide: `https://docs.appimage.org/packaging-guide/index.html`
- `linuxdeploy`: `https://docs.appimage.org/packaging-guide/from-source/linuxdeploy-user-guide.html`
- `appimagetool`: `https://github.com/AppImage/appimagetool`

General flow:

1. Publish the app for `linux-x64` or `linux-arm64`.
2. Assemble an AppDir using the publish output.
3. Add the desktop file and icon.
4. Generate the final `.AppImage`.

For CI and release automation, AppImage generation should be the final Linux packaging step, and only the resulting `.AppImage` files should be uploaded.

## CI artifacts

The intended CI layout is:

- a Linux test job running `./build.sh Test --configuration Release`
- packaged artifact jobs for Windows and macOS
- AppImage jobs for Linux x64 and Linux ARM64

Linux jobs should use publish output only as an intermediate build directory. The final user-facing artifacts should be AppImages only.

Recommended artifact names:

- `AES-Lacrima-windows-win-x64.zip`
- `AES-Lacrima-macos-osx-arm64.zip`
- `AES-Lacrima-x86_64.AppImage`
- `AES-Lacrima-aarch64.AppImage`

## GitHub Releases

GitHub Actions artifacts and GitHub Releases serve different purposes:

- CI artifacts are for validation and quick downloads from workflow runs.
- GitHub Releases are for versioned end-user distribution.

The recommended release flow is tag-driven:

1. Create and push a tag such as `v1.2.3`.
2. Build all release targets in GitHub Actions.
3. Create a GitHub Release from that tag.
4. Attach only the final packaged assets.

Recommended release assets:

- Windows packaged archive
- macOS Apple Silicon packaged archive
- Linux x64 AppImage
- Linux ARM64 AppImage

## Notes for CI maintainers

- `AES_Lacrima/AES_Lacrima.csproj` currently auto-installs a Linux desktop entry after `linux-x64` publish when `AutoInstallLinuxDesktopEntry` is `true`.
- That behavior is useful locally, but CI packaging should disable or bypass it so the runner does not try to install desktop entries into the build machine's home directory.
- macOS publish already has a repo-managed post-publish bundle step for `osx-x64` and `osx-arm64`.

Linux ARM64 AppImages are best generated on a native ARM64 Linux runner. If GitHub-hosted ARM64 runners are unavailable, use a self-hosted ARM64 Linux runner as a fallback.

## References

- .NET publishing overview: `https://learn.microsoft.com/en-us/dotnet/core/deploying/`
- `dotnet publish`: `https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish`
- .NET RID catalog: `https://learn.microsoft.com/en-us/dotnet/core/rid-catalog`
- Avalonia deployment overview: `https://docs.avaloniaui.net/docs/deployment/`
- Avalonia macOS deployment: `https://docs.avaloniaui.net/docs/deployment/macOS`
- Avalonia Debian/Ubuntu packaging: `https://docs.avaloniaui.net/docs/deployment/debian-ubuntu`
- AppImage packaging guide: `https://docs.appimage.org/packaging-guide/index.html`
- GitHub Actions for .NET: `https://docs.github.com/en/actions/use-cases-and-examples/building-and-testing/building-and-testing-net`
- GitHub Actions artifacts: `https://docs.github.com/en/actions/how-tos/writing-workflows/choosing-what-your-workflow-does/storing-and-sharing-data-from-a-workflow`
