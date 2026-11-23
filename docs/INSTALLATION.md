# Installing DVR Remaster v1.1

## Requirements

- Apple Silicon Mac
- macOS 14 or newer (minimum build target)
- No separate Python, .NET or FFmpeg installation required

## Option 1: PKG installer

1. Download `DVR-Remaster-1.1-arm64.pkg` from GitHub Releases.
2. Close DVR Remaster if it is running.
3. Open the package and follow the installation instructions.
4. Launch DVR Remaster from Applications.

The installer places the application in `/Applications`.

## Option 2: ZIP download

1. Download `DVR-Remaster-1.1-Mac.zip`.
2. Extract the ZIP.
3. Move `DVR Remaster.app` into Applications, replacing an older version if required.
4. Open DVR Remaster.

Do not use GitHub's **Code > Download ZIP** option to download the application. That contains source code.

## macOS security

The application uses ad-hoc code signing and is not Apple-notarised.
macOS may display a security warning.

Follow Apple's guidance for safely opening a downloaded application:
https://support.apple.com/en-gb/102445

Do not disable system-wide security protections.

## Updating

Close DVR Remaster before installing an update.

Your settings and presets are stored separately at:

`~/Library/Application Support/DVR Remaster/`

Replacing the application should not remove them.

## Other systems

Intel Macs, Windows and Linux are not supported by this macOS release.
Testing on the minimum macOS 14 target remains pending.
