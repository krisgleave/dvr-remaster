# DVR Remaster v1.0.0 - Build documentation

## Release platform

- Apple Silicon (arm64)
- Minimum deployment target: macOS 14.0 Sonoma
- .NET runtime 8.0.33
- Avalonia 12.1.4
- CPython 3.12.16
- OpenSSL 3.5.9
- FFmpeg 9.0.2
- OpenCV 5.0.0
- NumPy 2.5.4
- Pillow 13.0.0
- PyInstaller 6.22.3

The complete application was tested on the development Mac.
All native ARM binaries were inspected for minimum macOS
deployment targets. Testing on macOS 14 itself remains pending.

## Build environment

Install Xcode Command Line Tools, CMake, Ninja, NASM,
pkg-config and the .NET 8 SDK.

The original release environment uses the following layout:

    ~/Developer/DVR Remaster/
        Github/dvr-remaster/
        Dependencies/ffmpeg/
        build-tools/python-release/
        build-tools/dotnet/
        build-tools/package_local.py
        Build DVR Remaster.command

    ~/dvr-remaster-media/
        prefix/
        runtime-compatible/python/
        runtime-compatible/openssl/

The preserved launcher and packaging script are available
in packaging/local-build in the application repository.

## OpenSSL

Use the supplied OpenSSL 3.5.9 source archive.

    export MACOSX_DEPLOYMENT_TARGET=14.0
    ROOT="$HOME/dvr-remaster-media"
    ./Configure darwin64-arm64-cc \
        --prefix="$ROOT/runtime-compatible/openssl" \
        --openssldir="$ROOT/runtime-compatible/openssl/ssl" \
        no-shared no-tests -mmacosx-version-min=14.0
    make -j8
    make install_sw

## CPython

Use the supplied Python 3.12.16 source archive.

    export MACOSX_DEPLOYMENT_TARGET=14.0
    export CFLAGS="-mmacosx-version-min=14.0"
    export LDFLAGS="-mmacosx-version-min=14.0"
    ./configure \
        --prefix="$ROOT/runtime-compatible/python" \
        --enable-shared \
        --with-openssl="$ROOT/runtime-compatible/openssl" \
        --with-openssl-rpath=no \
        --with-ensurepip=install \
        --without-system-mpdecimal
    make -j8
    make install

Create a virtual environment using this Python installation.
Install the recorded Python package versions, substituting
the custom OpenCV wheel for the standard PyPI wheel.

## Custom OpenCV

Source: opencv_python_headless-5.0.0.93.tar.gz

    export MACOSX_DEPLOYMENT_TARGET=14.0
    export ENABLE_HEADLESS=1
    export ENABLE_CONTRIB=0
    export CMAKE_BUILD_PARALLEL_LEVEL=8
    export CMAKE_ARGS="-DWITH_FFMPEG=OFF \
    -DWITH_AVFOUNDATION=ON -DWITH_OPENCL=ON \
    -DWITH_IPP=OFF -DWITH_TBB=OFF \
    -DWITH_OPENEXR=OFF -DWITH_OPENJPEG=OFF \
    -DWITH_JASPER=OFF -DWITH_WEBP=OFF \
    -DWITH_TIFF=OFF -DWITH_GSTREAMER=OFF \
    -DWITH_ITT=OFF -DBUILD_TESTS=OFF \
    -DBUILD_PERF_TESTS=OFF -DBUILD_EXAMPLES=OFF \
    -DBUILD_JPEG=ON -DBUILD_PNG=ON -DBUILD_ZLIB=ON"

    python -m pip wheel . --no-deps --no-cache-dir -w wheels

The original download cache is included with the source materials.

## Custom FFmpeg

The macOS deployment target for FFmpeg and its static
dependencies is macOS 12.0.

Build and install the supplied dependency sources into
a common prefix before compiling FFmpeg.

Dependency build order:
1. zlib
2. x264
3. x265
4. FreeType
5. HarfBuzz
6. libvpx
7. Opus
8. FFmpeg

Use static libraries. Disable shared builds, unnecessary
command-line utilities, tests and examples.

FreeType was built without HarfBuzz, bzip2, PNG or Brotli.
HarfBuzz was built with FreeType support.

    export MACOSX_DEPLOYMENT_TARGET=12.0
    PREFIX="$HOME/dvr-remaster-media/prefix"
    export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"

    ./configure --prefix="$PREFIX" \
        --arch=arm64 --target-os=darwin \
        --cc=clang --cxx=clang++ \
        --enable-gpl --enable-version3 \
        --enable-static --disable-shared \
        --disable-autodetect --disable-doc --disable-ffplay \
        --enable-libx264 --enable-libx265 \
        --enable-libfreetype --enable-libharfbuzz \
        --enable-libvpx --enable-libopus --enable-zlib \
        --enable-videotoolbox --enable-audiotoolbox \
        --pkg-config-flags=--static \
        --extra-cflags="-I$PREFIX/include" \
        --extra-ldflags="-L$PREFIX/lib"

    make -j8
    make install

Copy the resulting ffmpeg and ffprobe executables into
Dependencies/ffmpeg in the application build layout.

## Final application build

From the application repository:

    ./scripts/build.sh

This verifies the approved dependency binaries, runs
regression tests, compiles the desktop and video engine,
packages the application and installs it into Applications.

The release dependency manifest records the hashes of the
original approved binaries. Independent source builds may
produce different binary hashes and require a locally
reviewed dependency manifest.

These instructions describe the original build environment.
An independent clean-room rebuild has not yet been verified.

## Licensing

DVR Remaster: GPL-3.0-or-later.
Original FPV DVR Cleaner: retained MIT licence.
Third-party components retain their respective licences.

See the accompanying licence texts, source archives,
dependency manifest and recorded build configuration.
