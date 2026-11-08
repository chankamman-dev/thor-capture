# 原生游戏库导入：给 Claude 的接入说明

更新：2026-09-27。仅后端传输/类型，不改游戏库界面。Node 合同与数据/恢复边界见 [Cocoon LIBRARY-IMPORT.md](<../cocoon-art-backend/docs/LIBRARY-IMPORT.md>)，任务对应 [BACKEND-WORKPLAN.md](../BACKEND-WORKPLAN.md) 的 1、3。

## 接口

复用当前游戏库持有的 `CocoonBackend`，服务 `Ready` 后创建 `new LibraryImportClient(backend)`，不能另外启动服务或直接读取 SQLite。所有异步调用在线程池上运行，前端 `await` 后更新界面；不要 `.Result`/同步等待或每轮刷新重建页面。

```csharp
LibraryImportClient imports = new LibraryImportClient(backend);
LibraryImportPlan plan = await imports.PlanAsync(sourceRoot, includeCache: true, includeStaging: true);
// 显示 plan，用户审阅与确认之后：
LibraryImportJob job = await imports.ExecuteAsync(plan.PlanId, confirmed: true);
// 页面自己的非阻塞定时器每 250–500ms 调用，并避免重叠请求：
job = await imports.StatusAsync(job.JobId);
// 完成后请求现有游戏库加载方法；可选撤销需再次确认：
await imports.RestoreAsync(job.ImportId, confirmed: true);
```

选择目录用文件夹选择器，根目录应包含 `_data/cocoon_db`。默认带缓存和暂存，用户可分别关闭；不能把导入入口连到“直接应用到设备”。预览任务可以较慢，显示工作中状态并允许退出页面；暂停轮询/关闭页面不等于取消已提交导入。当前没有取消导入接口，服务正常关闭会等待安全结束。

类型位于 `src/Library/LibraryImportClient.cs`：

| 类型 | 字段 |
|---|---|
| LibraryImportPlan | PlanId、TotalBytes、TotalGames、VisibleGames、PendingChanges、ReplacesExistingLibrary、Files、Warnings、ExpiresUtc |
| LibraryImportFile | RelativePath、Bytes、Sha256；展示相对清单，不打印公开日志 |
| LibraryImportJob | JobId、State、Code、ImportId、CopiedFiles/TotalFiles、CopiedBytes/TotalBytes、CanRestore、Finished |
| BackendRequestException | Code、Status（0 表示本地传输/合同错误）；Message 仅安全 code，不含错误正文或路径 |

`Finished` 包含 completed、failed、rollback_failed、restored，不能把 Finished 当作成功。完成时刷新全部库状态、平台/游戏和素材；restore 返回后也刷新并清掉撤销按钮。CopiedBytes 是来源复制进度，随后仍有备份/校验/发布；按 State 显示阶段，不能把复制 100% 显示成整个任务完成。

## 空页面与错误提示

缺库 `/api/games` 返回 `[]`，使用 `backend.GetJson("/api/backend/library")` 的 state 区分“未导入”和“已有空库”。坏库返回 `LIBRARY_INVALID`，权限错误为 `LIBRARY_UNREADABLE`；不要再把所有错误吞成空库。

`IMPORT_SOURCE_CHANGED / IMPORT_TARGET_CHANGED / IMPORT_PLAN_EXPIRED`：重新生成 plan 并审阅，不自动确认重试。`LIBRARY_IMPORT_BUSY`：暂停普通库请求，继续查 job；完成后刷新。`IMPORT_ROLLBACK_FAILED / IMPORT_BACKUP_INVALID`：保留恢复记录，明确提示失败，禁止重复执行。完整 code/HTTP 映射见 Node 文档。

`GetJson(path, allowMissing:false)` 用于必需的 JSON 资源，404 保留服务 code；默认 true 兼容现有可选资源返回 null。导入 StatusAsync 使用 false。POST 与已认证 GET/素材 GET 只允许拥有会话的 `/api/` 同源路径，不跟随重定向，令牌不发送到外部地址。JSON 最大 32MiB；原素材读取维持 64MiB 上限。请求超时不代表任务已撤销，不自动再发 execute。

## 已验证与尚未接入

- `tests/LibraryImportTests.cs` 12 项真实本机 HTTP/进程检查：带中文和空格的来源、类型化清单/错误/状态、来源不变、可立即刷新游戏/封面、撤销、同源 GET/POST、防遗漏清理。复用现有安全参数/环境构建，修复后端启动的 `Path/PATH` 冲突。
- 全量 `-Stream` **546/546** 通过，包括现有原生游戏库 12 项，以及录屏、音频、双屏回放、带壳导出的合成检查。构建位于独立 worktree；未替换用户正在运行的主目录 EXE。
- Node 实库导入隔离验收：770 总游戏、732 可见、5 待处理、380 文件/约 248MiB；缓存封面可读、源哈希/时间保持不变。主程序生产数据根依然为空，需前端接入按钮并由用户审阅后导入。
- 没有设备操作或实机测试，没有扩展数据库结构升级。缩略图、跨进程设备协调、只读拉取、媒体组关联与编辑 Facade 仍按工作计划推进。

证据：本分支 `evidence/backend-import-compatibility-tests.txt`、`evidence/backend-import-build.txt`；Node 目录 `evidence/backend-import-all-tests.txt`、`evidence/real-library-import-acceptance.json`。设备和公开测试版验收不由这些离线结果代替。
