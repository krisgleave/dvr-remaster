# v1.0.0 release validation — 19 November 2026

## Verified

- Current local repository inspected; existing maintainer changes preserved.
- Required build regression suite: 102 passed, 56 subtests passed (17.75 seconds); no skipped tests in this run.
- Delivery tests cover multi-segment exports, H.264/HEVC/ProRes, audio removal, frame counts, timing, dimensions and comparison synchronisation.
- Final queue buttons changed to horizontal pencil/red trash icons, with tooltips and accessible names. Desktop compilation passed. Visual confirmation of the final queue remains pending.
- Original DVR Remaster artwork retained. OSD Editor and P1 Transfer repository links verified through GitHub; original icon URLs both return HTTP 200.
- Original upstream MIT copyright and licence retained.
- Version 1.0.0 agrees in Python metadata and desktop project; bundle template uses the package version.
- GitHub repository exists, is private and has no releases.

## Build and packaged checks

- scripts/build.sh completed: desktop/engine compilation, version check, ZIP integrity check and installation in /Applications passed. Two pre-existing obsolete Watermark warnings remain.
- Packaged engine smoke test passed three exports from two synthetic sources, including repeated-source sections, H.264/HEVC/ProRes, six-frame output counts and side-by-side dimensions. No personal recordings were used.
- Local candidate: ../../Releases/DVR-Remaster-1.0.0-Mac.zip (not approved for redistribution).

## Outstanding

- Run a multi-recording queue through the packaged desktop app. The engine tests do not verify the complete desktop queue UI.
- FFmpeg/ffprobe 9.0.2 enable GPL and version3. The existing 109-byte licence file is only two links. Full licence texts and exact corresponding source/build material for FFmpeg and static libraries are missing from the inspected dependency folder. Generic upstream URLs are insufficient evidence of matching source.
- Verify all frozen Python and .NET dependency notices in the final distributable.
- The inherited GitHub workflow builds multiple unverified platforms and publishes on version tags. Do not push a release tag while these checks are unresolved.
- Make the repository public only once the release audit and binary checks pass; no visibility change or publication was performed.

No release tag or GitHub release has been created.
