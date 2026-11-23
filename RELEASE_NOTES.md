DVR Remaster is a free, open-source FPV DVR restoration application developed by Kris Gleave (KrisTalksTech), based on FPV DVR Cleaner.

## What's new in v1.1

### Installation and packaging
- Added a macOS PKG installer for straightforward installation and upgrades.
- Retained the portable macOS ZIP download.
- Improved the self-contained application bundle layout, with desktop runtime, processing engine and FFmpeg included.
- Verified macOS application code signing and ZIP integrity.
- Updated application version metadata to v1.1.

### Interface and documentation
- Simplified the application window title to DVR Remaster.
- Updated the README with current application screenshots.
- Expanded the illustrated user guide covering export options, queue management and presets.
- Added clearer documentation and examples for original/restored side-by-side video exports.

### Validation
- Successfully built for Apple Silicon.
- Tested the PKG installation and video export.
- No separate Python, .NET or FFmpeg installation required.

## Downloads

- **DVR-Remaster-1.1-arm64.pkg** — recommended macOS installer
- **DVR-Remaster-1.1-Mac.zip** — manual installation
- **SHA256SUMS.txt** — download integrity checksums

## Installation

Download the PKG installer, close any running copy of DVR Remaster, and follow the installation instructions.

Alternatively, extract the ZIP and copy DVR Remaster.app into Applications.

Your personal settings and presets are stored separately from the application.

## Compatibility

- Apple Silicon Mac
- macOS 14 or newer (minimum build target)
- Tested on the developer's Mac; validation on macOS 14 itself remains pending
- Intel Macs, Windows and Linux are not supported by this release

## macOS security

The application is ad-hoc signed but not notarised by Apple. macOS may display a security warning when opening the downloaded app or installer.

See Apple's official instructions:
https://support.apple.com/en-gb/102445

## Licensing

DVR Remaster is licensed under GPL-3.0-or-later. Original FPV DVR Cleaner MIT attribution and third-party licences are preserved.

See THIRD-PARTY-NOTICES.md for component licensing information.

## Links

- Repository: https://github.com/krisgleave/dvr-remaster
- YouTube: https://youtube.com/@kristalkstech
- Support: https://paypal.me/krisgleave
