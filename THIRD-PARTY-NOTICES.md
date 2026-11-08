# Third-party notices — draft

This draft describes the current source tree. It does not grant a license for Thor Capture itself; the owner's project license and copyright identity are pending.

## Included scrcpy server

Unmodified `scrcpy-server-v3.3.4`, from [Genymobile/scrcpy v3.3.4](https://github.com/Genymobile/scrcpy/releases/tag/v3.3.4), copyright Genymobile and upstream contributors as specified in the upstream sources, Apache License 2.0. SHA-256: `8588238c9a5a00aa542906b6ec7e6d5541d9ffb9b5d0f6e1bc0e365e2303079e`.

Full license: [third-party/scrcpy/LICENSE](third-party/scrcpy/LICENSE). Component attribution and operation details: [third-party/scrcpy/NOTICE.md](third-party/scrcpy/NOTICE.md). No scrcpy desktop client, SDL or bundled FFmpeg libraries are included. No modification of the server is claimed. If the server is included in a binary package, include its license and notice at the same relative paths. Re-check upstream NOTICE requirements at the pinned source before release.

## External tools not included

ADB / Android Platform-Tools and FFmpeg are user-provided executables. They retain their own licenses; Thor Capture does not claim ownership. The locally audited FFmpeg 6.0 build enables GPL and version3 and must not be described as LGPL-only. Corresponding-source and component obligations must be re-evaluated before distributing any such binary. See [the actual dependency audit](docs/release/third-party.md).

## Names and visual references

AYN / Thor names and the etched visual logo refer to third-party hardware. No affiliation, approval or trademark license is claimed. Product and game photographs are not part of the proposed public package. Distribution of the drawn housing/logo remains an owner decision.
