# 游戏库与媒体组的 C# 桥接

2026-09-27；`src/Library/GameMediaClient.cs`，命名空间 ThorCapture。仅新增客户端和测试，不改 UI、Node server、CocoonBackend 或 MediaRepository。

## 构造和调用

```csharp
// Same-assembly integration; client does not own backend or repository lifetimes.
GameMediaClient client = new GameMediaClient(backend, mediaRepository);
GameMediaResult resolved = await client.ResolveGameAsync(game.Id, cancellation);
GameMediaResult linked = await client.AssociateAsync(group.GroupId, group.Revision,
    resolved.Game.GameId, cancellation);
GameMediaResult query = await client.QueryByGameAsync("native:42", cancellation);
GameMediaResult checkedReference = await client.CheckReferenceAsync(savedReference, cancellation);
GameMediaResult detached = await client.UnlinkAsync(group.GroupId, group.Revision, cancellation);
```

构造器依赖 `CocoonBackend.GetJson(string path, CancellationToken cancellation, bool allowMissing=true)`；这个取消 overload 由主 agent 在 pkg2 开发，当前本分支旧 CocoonBackend 不提供它。不能直接声称本分支已独立接线。这里只调用 GET；媒体写入仍调用 IMediaRepository 的组级 CAS 接口。HTTP helpers 为阻塞实现，所以所有 HTTP 工作在 Task.Run 后台执行；没有 UI 事件、不建进程、不自行启动或停止后端。返回不可变 GameMediaResult（Outcome、Code、Game、RepositoryResult、Groups），Groups 是调用结束时的不可变查询视图。调用方负责 UI Marshal、请求序号/晚到界面结果丢弃；不在 UI 直接运行 HTTP。无 settings 新键、无百分比进度事件，可使用可取消的处理中状态。

root整合补记：该取消overload已在当前CocoonBackend提供，真实Node身份/目录项与仓库CAS联合回归通过；上段“不提供”仅描述独立包的旧底座。前端选择/generation的接入仍由Claude负责。

默认不自动关联、不自动解除。ResolveGameAsync(string/int) 验证当前游戏存在；QueryByGameAsync 同时验证并查关联媒体。QueryStored(GameReference) 只读仓库内存，适用于离线或失效历史引用；CheckReferenceAsync 验证保存引用仍属于当前库且游戏仍存在，但不删除/改写引用。UnlinkAsync 是用户明确解除的本地操作，后端未就绪时仍可执行，必须传当前组 Revision。

## 稳定身份与 ID 规则

读取 `GET /api/backend/library/identity`，要求 contract=cocoon.library-identity/1、provider=cocoon、明确存在的 libraryId 字段。null 表示未导入；缺失字段、坏 UUID、未来 contract 均 Invalid，不生成临时 ID。UUID 字符统一为小写。后端重启 Endpoint/instanceId 可以变化，保存的 libraryId 仍是库身份；不能把当前进程身份作为 game namespace。

统一 GameReference(provider=cocoon, libraryId=持久UUID, gameId=native:n)。接受字符串整数、native:n、legacy-item:n、unresolved-native:n 和 ResolveGameAsync 的 Int32 overload，n 取非负整数规范十进制（去前导零），上限 9007199254740991，与 JS 精确整数范围一致。真实 Node 当前 V2 ID 前缀由 game_identities 是否存在决定，同一原生游戏 identityResolved 变化不应改变媒体键。旧手工引用如 gameId="42" 或其他业务 namespace 不自动迁移，调用方需要明确迁移/重关联。

存在校验实际使用 `GET /api/v2/catalog/items/legacy-item%3An`；这是当前 LocalV2Facade 的单项路由。服务端解析 legacy-item/unresolved-native 相同 nativeId，返回当前真实 id/nativeGameId/displayName；客户端同时校验返回字符串 ID 与 nativeGameId 对得上请求。没有对分页 `/api/games` 做“未找到就删除”的推断，不会漏掉隐藏或不在第一页的游戏。未依赖尚未存在的 `/api/v2/catalog/{id}` 路由。

## 一致性、失败与生命周期

每次捕获 backend.Endpoint 与 StateChanged generation，HTTP 前后都检查；同 Endpoint 被另一次后端启动复用也会拒绝迟到结果。游戏读取前后分别读取持久库身份，切库而 Endpoint 不变时返回 LIBRARY_REPLACED。全部 HTTP 接受传入 CancellationToken；预取消不发 HTTP，Dispose 取消后续请求并取消在途 HTTP，不停止或 Dispose backend/repository。

组关联写入前完成游戏和双身份验证；仓库 expectedRevision 防止覆盖并发修改。不重试 CAS 冲突，不自动合并或更换游戏。HTTP 与本地 sidecar 不能构成一个总事务：写入后再检查端点/库身份。若切库、取消或 HTTP 超时发生在本地提交后，结果携带 RepositoryResult，可能显示其 Outcome=Success，保存的是原先验证的旧库 namespace；客户端不自动回滚、删除或改成新库关联。调用方必须审阅已提交状态/刷新，不能看到外层失败就声称“没有变更”。恰好在最后一次检查之后的库改变仍需下次 CheckReference 检出；不声称全局锁住 Node 目录。StateChanged 通知本身可能经 UI SynchronizationContext 调度；Endpoint 检查仍独立执行。

Outcome 为 Success、EmptyLibrary、NotFound、Invalid、ContextChanged、Cancelled、RepositoryFailure、Failed。典型 Code：LIBRARY_NOT_IMPORTED、LIBRARY_IDENTITY_INVALID、GAME_NOT_FOUND、GAME_RESPONSE_INVALID、BACKEND_CONTEXT_CHANGED、LIBRARY_REPLACED、INVALID_GAME_ID、BACKEND_NOT_READY、CANCELLED；HTTP 安全错误码和 repository 的 CONFLICT 等原样保留。没有原始 URL、令牌、响应文本或路径回显。前端空库提示“请先导入游戏库”；NotFound 提示保留关联并提供重新选择/明确解除；ContextChanged 提示“游戏库已变化，请刷新后重试”；RepositoryFailure 的 CAS 提示“媒体记录已更新”。完整游戏缺失标记仍由权威全列表交给 repo 的 scoped ValidateGameLinksAsync，本桥接不拿单项列表标记整个库。

## 证据与未测

24 项组件测试 + 64 项核心检查 = 88 PASS，覆盖 ID 映射、坏返回、空库、不存在游戏、双库相同数字 ID 隔离、重启持久身份、端点/同端点generation迟到、验证中换库、提交后换库/HTTP超时、CAS、取消、离线解除、失效引用保持、原片字节不变。测试为注入 transport + 真实 MediaRepository/PNG fixture，不是完整 HTTP/实机验收。24 新项全量 Stream 641 PASS，最终结果见 `evidence/media-repository/game-media-full-stream.txt`。

编译依赖从 pkg2 当前 CocoonBackend 和 src/App/Backend 只读快照到 `evidence/game-media-dependencies`；排除与本任务无关 GalleryMonitor（依赖父库更新的 GalleryIndex）。没有覆盖本分支 tracked 依赖，不将快照提交。哈希见 `game-media-dependency-hashes.txt`，候选为 `evidence/ThorCapture-game-media-candidate.exe`；既有 tools 脚本会在依赖正式集成后自动发现本类。本交付尚未测试 Node 路由的端到端调用、真正导入/恢复、UI入口或真实设备。媒体源、Node数据目录均未被测试访问或修改。
