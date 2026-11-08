# C# Portable 后端启动探针

`tools/check-cocoon-portable.ps1` 用明确提供的生产源码编译 C# 5 探针，固定 `/main:ThorCapture.CocoonPortableCheck`。编译完整生产源码确保使用真正的 CocoonBackend、ProcessRequest、Json、Settings、Adb.AppDir 实现；执行时不进入 Program.Main、不创建 MainForm、不调用 Settings.Load、不执行 ADB 或 FFmpeg。

```powershell
./tools/check-cocoon-portable.ps1 -ProductionRoot <生产源码目录> `
  -VerifiedCandidate <已经Verify的纯代码候选目录> `
  -BackendCommit <40位Node源码commit> -ManifestSha256 <64位独立manifest摘要> `
  -Node <外置现有node.exe绝对路径>
```

脚本默认在自身项目 `evidence/cocoon-portable/<runId>` 新建一次性证据目录，不能放进生产 src 或被校验的候选目录，不能复用已有 runId。生产源码可以含在途修改，脚本不会覆盖它们：先复制全部 src/*.cs 为只读用途的 evidence 编译快照，再逐文件比对原始/复制哈希、文件数；执行后重新比对原源码。生产 HEAD 和实际逐文件哈希都记录，HEAD 不代替在途源码版本证据。原源文件变更会明确返回 PRODUCTION_SOURCE_CHANGED。

先调用原 `cocoon-code-package.cjs verify` 固定源候选的 commit/manifest 摘要，再按 manifest 复制 101 文件至 `app/cocoon`；EXE 只在 `app`，没有设置文件、node.exe、ADB、FFmpeg 或用户库。运行数据放平行的全新 `empty-data`，不会读取项目或旧游戏库。复制前后/运行后比较全部组件文件数与 SHA，再 Verify 原候选。包 manifest 不复制为可执行代码，不会为此放宽旧 Verify 对额外文件的拒绝规则。

探针显式给生产 CocoonBackend 外置 NodePath 和全新绝对 DataRoot；BackendDir 使用 **DefaultBackendDir() 自动发现**，必须等于 EXE 旁的 cocoon。禁用自动重启，执行 Start→Ready；只做 GET：无凭据 games 401、带凭据 games=[]、持久身份未导入、managed 设备/旧 apply 禁用、诊断 contract 且不包含本轮应用/数据绝对路径、原生编辑器 EDITOR_LIBRARY_REQUIRED/409、library 状态健康。通过真实 StopAsync 等待结束，并用启动后持有的同一拥有进程句柄验证 ExitCode=0，最后验证 State/Endpoint/OwnedProcessId 清空。只观察和停止本次 CocoonBackend 拥有的 Node，没有按进程名称终止用户程序。

探针 stdout/stderr 只含 PASS 标签或 typed 失败码，不打印 token、个人目录或响应原文。输出 `probe.txt` 和 `portable-result.json`：后者包含固定 main、生产 HEAD、相对源码路径及 SHA、探针/Node/EXE SHA、Node组件 commit/manifest 摘要、exitCode、源码与组件不变标志。`publicReleaseReady` 始终 false。失败证据保留，不递归删除计算目录或偷偷复用数据根。

2026-09-27 实测 **17 项 PASS**，run `e47e13b0f19442d29598891346154754`，生产 HEAD `592994aca41a16852bd3e5066290f8605daaf072`，92 个生产源文件的实际哈希见该 run 的 portable-result.json；编译成功、退出码 0。Node组件 commit `ec4cde82409d050ee7dd72e8e05ac9c898d572da`，manifest `9d20a4a31e4fe0af02095ed4cbcc4f230907aba5e5420c3cbc00f3ae5d95ee74`，101 个组件文件前后不变。本轮 Node v24.18.0；源候选及运行数据均仅在 evidence。第一轮探针观察代码没有预先持有进程句柄，进程退出后查询失败；仅修复探针持有句柄，第二轮完整通过，没有改生产生命周期实现。

最终主任务在 BinaryProcessRunner 取消修复后重新执行：run `86c7fde83686417db5f3bf09aab17224`，生产 HEAD `4a254a75f5b05dd5e2d2e6b7c4d0d67886ce7dea`，**17项PASS**、退出码0；92个生产源文件与101个组件文件运行前后不变。Node提交与manifest同上。最终证据为该run的portable-result.json；上面的早期run保留为历史，不替代最终源码验证。

这证明在当前 Windows/.NET/外置 Node 环境里，portable 目录可由真实 C# 后端自动发现、鉴权访问与退出。**不等于干净机器安装、运行时捆绑许可、全软件 UI、公测发布或实机验收。** 此后任何生产源码变更仍需重新运行本脚本。
