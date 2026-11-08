# Thor Capture quick start

The interface is in Simplified Chinese only.

1. Unzip the package into an ordinary folder with a short path (for example `D:\ThorCapture`).
2. Download Google's [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) and put the whole `platform-tools` folder next to `ThorCapture.exe`.
   If adb is already installed, you can instead create `adb-path.txt` next to the exe containing the full path to `adb.exe`.
3. On the Thor, turn on Developer options → USB debugging and connect it to the PC. The first time, the handheld asks to authorise the PC; allow it.
4. Run `ThorCapture.exe`. Once the device shows up in the top bar, press F5 for a screenshot and F6 to record.

The program isn't code-signed, so Windows may say "Windows protected your PC" on first run. Click "More info → Run anyway".

## Optional

- **FFmpeg** is needed for live video preview, framed video export and system audio recording. Put `ffmpeg.exe` and `ffprobe.exe` next to the exe, or set the `THOR_FFMPEG` environment variable.
- **Game library** needs Node.js 22 or later and the Cocoon backend component `cocoon-art-backend` (not included in this package). Reading and writing the library on the handheld also needs a rooted Thor.

## What's in the package

| File | Purpose |
|---|---|
| `ThorCapture.exe` | The program |
| `scrcpy-server-v3.3.4` | Pushed to the handheld temporarily to capture system audio; keep it next to the exe |
| `third-party/scrcpy/` | scrcpy's licence and notice |
| `THIRD-PARTY-NOTICES.md` | Third-party components |

Settings (`thor-capture-settings.txt`) and captures (`captures\`) are stored next to the exe. Deleting the folder removes everything.

See the README on the project page for more.
