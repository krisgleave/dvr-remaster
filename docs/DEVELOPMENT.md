# Development

## Local build

The current development environment uses Apple Silicon macOS, Python, .NET, PyInstaller and FFmpeg.

Run the following from the repository:

./scripts/build.sh

The script:

1. Runs the automated regression tests.
2. Compiles the desktop application.
3. Compiles the restoration engine.
4. Packages the macOS application.
5. Verifies the application version.
6. Creates and verifies a versioned ZIP.
7. Installs the application into Applications.

The current build process depends on locally configured build tools and packaging scripts outside the repository. The original build scripts and pinned dependency versions are preserved in packaging/local-build. See [release build instructions](RELEASE_BUILD.md).

## Releases

Local builds do not automatically publish GitHub releases.

Public distribution requires functional testing, documentation updates and licensing checks.
