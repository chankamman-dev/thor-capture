# Thor Capture 快速开始

1. 把压缩包解压到一个普通文件夹，路径别太深（例如 `D:\ThorCapture`）。
2. 下载 Google 的 [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools)，把解压出来的 `platform-tools` 文件夹整个放到 `ThorCapture.exe` 旁边。
   已经装过 adb 的话，也可以在 exe 旁新建 `adb-path.txt`，里面写上 `adb.exe` 的完整路径。
3. 在 Thor 上打开「开发者选项 → USB 调试」，用数据线连接电脑。第一次连接时掌机上会弹出授权提示，选「允许」。
4. 双击 `ThorCapture.exe`。顶栏显示出设备后，按 F5 截图，F6 录屏。

程序没有数字签名，第一次运行时 Windows 可能提示「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可。

## 可选

- **FFmpeg**：实时视频预览、导出带壳视频、录系统声音需要它。把 `ffmpeg.exe` 和 `ffprobe.exe` 放到 exe 旁，或设置环境变量 `THOR_FFMPEG`。
- **游戏库**：需要 Node.js 22 以上和 Cocoon 后端组件 `cocoon-art-backend`（这个包里没有）；从掌机读写游戏库还需要 Thor 已 root。

## 包里的文件

| 文件 | 用途 |
|---|---|
| `ThorCapture.exe` | 程序本体 |
| `scrcpy-server-v3.3.4` | 录系统声音时临时推到掌机运行的组件，请和 exe 放在一起 |
| `third-party/scrcpy/` | scrcpy 的许可证和声明 |
| `THIRD-PARTY-NOTICES.md` | 第三方组件说明 |

设置（`thor-capture-settings.txt`）和截图（`captures\`）会保存在 exe 旁边。删掉整个文件夹就是完全卸载。

更多说明见项目主页的 README。
