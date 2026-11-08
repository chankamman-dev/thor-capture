# 应用到掌机（写回 Cocoon 游戏库）

2026-10-14。用户明确要求在电脑上编辑并写回 Thor，并把方式和实现都交给 Claude 决定；此前的分工（后端归 Codex）在这一块由用户改为 Claude 实现。**离线与沙箱验证完成，尚未对真实的 Cocoon 游戏库执行过一次写入。**

## 为什么是「快照-修改-换回」

- 掌机（AYN Thor，Android 13，Magisk root）上没有 `sqlite3`，不能在设备上逐行修改。
- 后端原设计的 ARM64 helper 尚不存在（见 Cocoon `docs/HELPER_PROTOCOL.md`），短期内用不上。
- 掌机的 Cocoon 已是 3.07，数据库 `user_version` 47（8 月的旧库是 35，新增 6 张表和若干列）。**任何用旧库整库覆盖的做法都会毁掉游戏库**；旧的 `/api/apply` 整库路径继续冻结，这里不使用它。
- 所以每次应用都从掌机取**当时的**数据库，在电脑上只改审阅过的字段，再放回去。数据库的结构版本和 Room identity 进出必须一致。

实测：这份 v47 数据库没有 Android 专属排序规则、触发器或 FTS，桌面 SQLite（`node:sqlite`）可以合并 WAL、更新 `games` 并通过 `integrity_check`。

## 流程（`src/Features/DeviceApply/DeviceApplier.cs`）

1. **inspect**：root 可用、Cocoon 处于启用状态、记录数据库文件的属主/权限/SELinux 标签。Cocoon 已被停用则拒绝（不是我们停的，不替用户启用）。
2. **freeze**：先把 journal 落盘，再 `pm disable-user` + `am force-stop`，轮询到进程消失。
3. **read**：在设备上算 `cocoon_db` 与 `-wal` 的 SHA-256，设备端备份到 `/data/local/tmp/thor-capture/backup_db*`，读回电脑并核对哈希，另存一份到电脑 `device-apply\backups\<时间>-<哈希前8位>\`。
4. **patch**：调用后端组件 `tools/snapshot-patch.cjs`（合同 `cocoon.snapshot-patch/1`，实现 `lib/device/snapshotPatch.js`）。合并 WAL，在一个事务里对每个字段做「当前值必须等于审阅时的旧值」检查后更新；任何一处不符整体回滚并报 `PATCH_CONFLICT`。可选核对 `fileName`/`platformId`，防止本机 id 对到设备上的另一款游戏。只允许白名单里的元数据列。
5. **push**：`adb push` 到 `/data/local/tmp/thor-capture/new_db`，设备上核对哈希。
6. **install**：`cat new_db > cocoon_db` 原地覆盖（保留 inode，因此属主、权限、SELinux 类别不变），`-wal`/`-shm` 清空而不删除；核对哈希和文件属性。
7. **thaw**：`pm enable`，确认不在停用列表里，回到桌面。
8. 界面随后把这份快照导入为本机游戏库副本，并撤销已应用的暂存条目。

第 2 步之后任何失败：若已开始覆盖则用设备端备份还原并核对哈希，然后启用 Cocoon。还原或启用无法确认时保留 journal（`device-apply\state\journal.json`，状态 `frozen` / `installing`），下次打开对话框只提供「恢复掌机」：`frozen` 直接启用；`installing` 按数据库哈希判断是已写好（保留）还是需要用备份还原。journal 绑定序列号，换了设备拒绝恢复。

取消只在覆盖开始之前生效；对话框在运行中不能关闭。

## 素材（封面 / Logo / 背景，含背景视频）

- 变更集里的素材条目带 `stagedRef`（本机 `_staging/native-assets/...` 里的文件）和目标 `destination{platform,fileName}`。应用时它变成两件事：把文件放到掌机 `/storage/emulated/0/cocoon/downloaded_media/<平台>/<icon|logo|hero>/<文件名>`，再把 `boxArtLocal`/`logoLocal`/`heroLocal` 改成与 `lib/cocoondb.js` `uriFor` 相同形式的 SAF URI，对应 `*Source` 设为 `user`。
- 文件在**停用 Cocoon 之前**推送：先推到 `<名字>.thor-partial` 再 `mv` 到位，设备上用 `sha256sum` 核对；推送不需要 root。此时数据库还没引用它们，所以这一步本身不改变任何可见内容。数据库换回失败并确认已还原后，这次推上去的文件会被删除；已存在且哈希一致的文件不重复推送。
- 补丁工具新增这六列；`*Local` 仍以审阅时的旧引用为前提，`*Source` 允许不带旧值直接设置（`UNGUARDED`）。路径只接受上述目录下的普通文件名（`CocoonMedia.IsMediaPath`）。
- 视频直接以原文件作为背景；你掌机上已有不少游戏用 mp4 当背景，但新视频能否被 Cocoon 正常播放仍以实机为准（变更集带 `VIDEO_PROFILE_UNVERIFIED`）。

## 设备命令（`CocoonDevice.cs`）

`ICocoonDevice` 是固定的八个操作，调用方不能传入路径、包名或命令。`AdbCocoonDevice` 的脚本不含单引号、路径是常量；`exec-out` 不回传退出码，所以每个脚本末尾自报 `THOR_RC=<码>`，缺这一行视为连接中断。内部构造函数可把同样的命令指向一个沙箱文件并跳过包管理，用于实机演练。

## 界面

「暂存的修改 → 应用到掌机…」打开 `ApplyDialog`：（必要时）升级本机扩展数据 → 逐条列出游戏/字段/旧值→新值 → 「应用到掌机」→ 分阶段进度 → 结果。未完成的应用存在时，顶栏入口变为「需要恢复掌机」。

- 审阅来自 `PreviewChangesAsync`，点击应用时先 `ConfirmChangesAsync` 留下变更记录，再执行设备步骤。
- 后端的 `DevicePlan` 仍带 `M2_PLAN_ONLY` 等阻塞并保持 `executionAllowed:false`；这条路径不执行 DevicePlan，也没有改动它。`LIBRARY_ITEM_UNRESOLVED` 由补丁的旧值和文件名检查兜底。
- 这一版只应用元数据。变更集中有素材时提示先撤销素材修改。
- 每次导入本机副本后，后端的扩展库回到旧结构，下次应用前会再次要求「升级并继续」（后端规定迁移必须明确确认）。

## 验证

- 后端 `test/snapshotPatch.test.js` 7 项；另在真实设备数据库的临时副本上执行通过（746 款，v47）。
- `tests/DeviceApplyTests.cs` 25 项：目录模拟设备 + 真实补丁工具。覆盖顺序、哈希、冲突、已停用/无 root、写一半断开、校验不符、标签变化、传输失败、取消、撤销失败后的 journal 与三种恢复、错设备、journal 损坏。
- `tests/DeviceApplyFlowTests.cs` 21 项：页面 + 对话框 + 真实后端全流程，含设备被改动的冲突和恢复入口。
- 实机**沙箱演练**（`/data/local/tmp` 下的数据库副本，不碰 Cocoon）：10.9 MB 数据库读、备份、补丁、推送、原地写入、核对、再用备份还原，哈希和文件属性全部一致。
- 实机只读：真实路径上的 inspect 与哈希正常。

**没有验证的**：`pm disable-user` / `pm enable` 对这台机器的桌面应用的实际效果、Cocoon 3.07 重新启动后读新数据库的表现、Hive 同步是否会覆盖或上传被改的字段、`metadataLocks` 是否影响后续刮削。这些只能在第一次真实应用时观察。

## 给 Codex

- 新增后端文件 `lib/device/snapshotPatch.js`、`tools/snapshot-patch.cjs`、`test/snapshotPatch.test.js`，没有改 `server.js` 或其他在途文件。打包组件时需要把前两个文件加入代码包清单，否则 `PATCH_TOOL_MISSING`。
- `BrokerFileReader` 增加了文件头校验和用户选择后的 `su` 只读回退；`CocoonBackend` 的 stdin 命令改为原始 UTF-8 并另起一行（BOM 问题）。
- 应用路径目前没有接入 `DeviceSessionRegistry` / `OperationGate` 的租约，只靠界面在截图/录屏忙碌时拒绝。
