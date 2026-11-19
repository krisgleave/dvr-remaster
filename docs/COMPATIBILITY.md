# Compatibility

The v1.0.0 local package targets Apple Silicon (osx-arm64). Current engine tests ran on macOS 27.2.1. The bundle declares macOS 14.0 (Sonoma) as a minimum; this is not a claim that macOS 14 or every later version has been tested.

Intel Macs, Windows and Linux are unverified for this release. The inherited build workflow is not evidence of product compatibility.

Input containers: MP4, MOV, MKV, AVI, M4V, TS and WebM, subject to FFmpeg decoder support. The desktop exposes MP4 H.264, MP4 HEVC and MOV ProRes 422 output. Restoration is aimed at analogue interference and HDZero breakup, not every digital recording system.

See [release validation](RELEASE_VALIDATION.md) for completed tests and remaining checks.
