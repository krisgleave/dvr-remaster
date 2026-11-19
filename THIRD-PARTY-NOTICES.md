# Third-party notices

DVR Remaster is distributed under GPL-3.0-or-later.
The original FPV DVR Cleaner copyright and MIT licence
are preserved in licenses/FPV-DVR-Cleaner-MIT.txt.

Third-party components retain their own licences.

## Desktop application

| Component | Version | Licence |
|---|---|---|
| Avalonia | 12.1.4 | MIT |
| Inter typeface | | SIL OFL 1.1 |
| SkiaSharp | 3.119.4 | MIT; native third-party notices |
| HarfBuzzSharp | 8.3.1.3 | MIT; native third-party notices |
| MicroCom.Runtime | 0.11.6 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT |
| Microsoft .NET runtime | 8.0.33 | MIT; third-party notices |
| Microsoft.Extensions and System.IO.Pipelines | 8.0 series | MIT |

## Video restoration engine

| Component | Version | Licence |
|---|---|---|
| CPython | 3.12.16 | PSF and bundled component licences |
| OpenSSL | 3.5.9 | Apache-2.0 |
| NumPy | 2.5.4 | BSD-3-Clause and bundled component licences |
| Pillow | 13.0.0 | MIT-CMU and bundled component licences |
| OpenCV | 5.0.0 | Apache-2.0 and bundled component licences |
| OpenCV Python packaging | 5.0.0.93 | MIT |
| PyInstaller | 6.22.3 | GPL with bootloader exception |
| Inter typeface | | SIL OFL 1.1 |

The custom OpenCV build disables FFmpeg integration and
uses macOS AVFoundation support. Video decoding and
encoding are primarily handled by separate FFmpeg tools.

## External FFmpeg tools

FFmpeg and ffprobe version 9.0.2 are custom-built for
Apple Silicon, with GPL and version3 enabled.

The statically linked libraries include x264, x265,
FreeType, HarfBuzz, libvpx, Opus and zlib.

The resulting FFmpeg executables are GPL-3.0-or-later.
They are invoked as separate programs.

## Licence texts and corresponding source

Full collected licence texts and notices are supplied
with the application in its Third-Party-Licences directory.

The accompanying version-matched third-party source
archive supplies source materials, provenance and build
configuration information.

Release repository:
https://github.com/krisgleave/dvr-remaster

FFmpeg licensing information:
https://ffmpeg.org/legal.html

Development-only packages and legacy prototype
dependencies are not necessarily included in the
distributed macOS application.
