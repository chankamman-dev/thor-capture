# scrcpy audio server

Unmodified upstream binary: `scrcpy-server-v3.3.4`, Genymobile/scrcpy v3.3.4.

- Source and release: https://github.com/Genymobile/scrcpy/releases/tag/v3.3.4
- Source tree: https://github.com/Genymobile/scrcpy/tree/v3.3.4/server
- License: Apache License 2.0, reproduced in LICENSE.
- Copyright: Genymobile and scrcpy contributors, as stated in upstream sources.
- SHA256: `8588238c9a5a00aa542906b6ec7e6d5541d9ffb9b5d0f6e1bc0e365e2303079e`
- Verification: compared with upstream release SHA256SUMS.txt; no signature verification performed.

Thor Capture implements its own PCM socket receiver in C#. It does not distribute
the scrcpy desktop client, SDL, or its FFmpeg libraries. The existing user-provided
FFmpeg dependency remains separate. This file and LICENSE must accompany builds
that include the server. Thor Capture's project license still requires the owner's
decision before public release.

The optional system-audio mode uploads this server to a uniquely named temporary
device path and runs it as the already-authorized ADB shell user. It is not an APK
installation, does not elevate privileges, and enables neither microphone nor
device controls. Closing the audio connection stops capture; the client removes
its own port forward and temporary files. A disconnected device may retain these
uniquely named files until manually removed.
