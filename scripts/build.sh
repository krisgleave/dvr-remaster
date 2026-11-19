#!/bin/bash
export DYLD_LIBRARY_PATH="$HOME/dvr-remaster-media/runtime-compatible/python/lib"
export MACOSX_DEPLOYMENT_TARGET=14.0
"$HOME/Developer/DVR Remaster/build-tools/python-release/bin/python" "$HOME/Developer/DVR Remaster/Github/dvr-remaster/scripts/verify-release-dependencies.py" || exit 1
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="$(cd "$REPO/../.." && pwd)"
PYTHON="$ROOT/build-tools/python-release/bin/python"

cd "$REPO"

VERSION="$("$PYTHON" -c 'import tomllib; print(tomllib.load(open("pyproject.toml","rb"))["project"]["version"])')"

APP="$ROOT/local-build/Test Release/DVR Remaster.app"
DEST="/Applications/DVR Remaster.app"
ZIP="$ROOT/Releases/DVR-Remaster-${VERSION}-Mac.zip"

export PYTHONPATH="$REPO/src"
export PATH="$ROOT/Dependencies/ffmpeg:$PATH"

echo "========================================"
echo " DVR Remaster v$VERSION"
echo "========================================"

if pgrep -f 'DVR Remaster.app/Contents/MacOS/' >/dev/null; then
    echo "Please close DVR Remaster before building."
    exit 1
fi

echo "===== RUNNING REGRESSION TESTS ====="
"$PYTHON" -m pytest tests/ -q --tb=short

echo "===== BUILDING APPLICATION ====="
printf '\n\n' | "$ROOT/Build DVR Remaster.command"

echo "===== VERIFYING APPLICATION ====="
test -d "$APP"

ACTUAL_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"

if [[ "$ACTUAL_VERSION" != "$VERSION" ]]; then
    echo "ERROR: Application version mismatch."
    exit 1
fi

echo "===== CREATING RELEASE ZIP ====="
mkdir -p "$ROOT/Releases"

TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT

ditto -c -k --sequesterRsrc --keepParent "$APP" "$TEMP/release.zip"
unzip -tq "$TEMP/release.zip"
mv "$TEMP/release.zip" "$ZIP"

echo "===== INSTALLING APPLICATION ====="

STAGING="$(mktemp -d /Applications/.dvr-remaster.XXXXXX)"
ditto "$APP" "$STAGING/DVR Remaster.app"

if [[ -e "$DEST" ]]; then
    mv "$DEST" "$STAGING/Previous.app"
fi

if mv "$STAGING/DVR Remaster.app" "$DEST"; then
    rm -rf "$STAGING"
else
    if [[ -e "$STAGING/Previous.app" ]]; then
        mv "$STAGING/Previous.app" "$DEST"
    fi
    exit 1
fi

echo "========================================"
echo " BUILD SUCCESSFUL"
echo " Version: $VERSION"
echo " Installed: $DEST"
echo " Release: $ZIP"
echo "========================================"
