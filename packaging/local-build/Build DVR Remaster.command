#!/bin/bash
export DYLD_LIBRARY_PATH="$HOME/dvr-remaster-media/runtime-compatible/python/lib"
export MACOSX_DEPLOYMENT_TARGET=14.0
"$HOME/Developer/DVR Remaster/build-tools/python-release/bin/python" "$HOME/Developer/DVR Remaster/Github/dvr-remaster/scripts/verify-release-dependencies.py" || exit 1
set -e
cd "$(dirname "$0")"
ROOT="$PWD"
export DOTNET_CLI_HOME="$ROOT/build-tools/dotnet-home"
export NUGET_PACKAGES="$ROOT/build-tools/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export PYINSTALLER_CONFIG_DIR="$ROOT/build-tools/pyinstaller-cache"
echo 'Close DVR Remaster before rebuilding. The downloaded original can stay open.'
read -r -p 'Press Return when the local app is closed: '
build-tools/dotnet/dotnet publish Github/dvr-remaster/desktop/FpvDvrCleaner.Desktop/FpvDvrCleaner.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:DebugType=none -p:UsedAvaloniaProducts= -o local-build/desktop
build-tools/python-release/bin/python -m PyInstaller --noconfirm --clean --name flugbild-engine --paths "$ROOT/Github/dvr-remaster/src" --add-data "$ROOT/Github/dvr-remaster/src/flugbild/assets:flugbild/assets" --exclude-module PySide6 --exclude-module tkinter --distpath local-build/engine-dist --workpath local-build/engine-build --specpath local-build Github/dvr-remaster/packaging/engine_entry.py
build-tools/python-release/bin/python build-tools/package_local.py
echo 'Build complete. Open DVR Remaster in the folder beside this script.'
read -r -p 'Press Return to close.'
