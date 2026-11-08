# 统一媒体组、稳定轨道 ID 与游戏关联后端

2026-09-27；后端包位于 `src/Media/`。仅实现独立组件，未替换 GalleryIndex/CaptureMetadata，未连接前端、采集器、文件监控或 Node 路由；没有设备操作。

## 接入契约

命名空间 `ThorCapture.Media`，构造 `MediaRepository(mediaDirectory, repositoryDirectory)`，两者必须不同。媒体目录当前限定平面目录，PNG/JPG/JPEG/BMP/MP4；仓库目录由调用方选取可信、可写的本地应用数据目录。新采集优先调用 `RegisterGroupAsync`，传一或两条 `MediaTrackInput(path, Upper/Lower)`，双屏同组且必须同媒体类型。成功后保存返回的 GroupId/TrackId；不要继续把文件名或 SHA 当作业务 ID。同一批来源和角色重复注册返回原组；已属于不同组的来源返回冲突，不自动拆组或合并。

启动调用 `RefreshAsync(token)`；监控事件调用 `ImportPathsAsync(paths, token)`，重命名同时提供旧、新路径。组件不自建 watcher、AppState 或轮询。新路径在后台读取，原路径消失会保留 Missing 轨道。GetSnapshot 只读内存，返回不可变快照；Groups/Tracks/Tags/Issues 均不可修改。Generation 是进程内发布版本；RepositoryId 是持久仓库身份；GroupId/TrackId 是持久 GUID，不因进程重启或受支持的重命名变化。Dispose 立即取消并拒绝后续请求，不等待任务；关闭界面前调用方应取消并等待手中任务。Changed 在后台线程、释放写锁后触发，前端必须 Marshal 到 UI；订阅者异常不影响其他订阅者。

游戏关联使用 `GameReference(provider, libraryId, gameId, displayName)`；身份 Key 是前三项的组合，显示名不是身份。Cocoon 可传 provider=cocoon、持久图库 UUID、数字游戏 ID 字符串。libraryId 必须跨重启稳定，不能使用 Node 进程 ID、临时令牌、URL 或游戏标题；替换图库应使用新 ID。若外部游戏库复用同一 ID 表示另一游戏，调用方需提供新 namespace 或明确修复，组件无法猜测。

`AssociateGameAsync`/`UnlinkGameAsync`、`SetAnnotationsAsync`、`AcknowledgeSourceChangesAsync`、`RestorePreviousAnnotationsAsync` 均要求当前 expectedRevision，避免覆盖并发修改。返回 Conflict 后读取新快照并交由调用者处理，不能无条件重试写入。收藏、标签、游戏均属于整组。Tags trim、按不区分大小写去重，最多 64 个、每项 128 字符。按游戏反查用 `snapshot.ForGame(reference)`。

推荐 `ValidateGameLinksAsync(provider, libraryId, availableIds, token)`：仅检查指定游戏库，删除游戏后保留原引用并标 GameMissing，再出现同一身份恢复。全局 overload 的 available 必须是所有权威库的完整列表；未完整加载库时不要调用全局校验。单次解除关联可通过 RestorePreviousAnnotations 恢复上一备份的游戏、收藏、标签；它创建新 revision，不回滚当前路径/轨道/指纹，也不是无限撤销历史。

`ExportSnapshotJson(offset, count)` 为 Node 桥接提供 `thor.media-snapshot/1` JSON，每页最多 500 组；generation/revision/length 用字符串避免 JS 数值精度损失，包含 repositoryId、稳定 ID、角色、指纹、状态、游戏键、标签、总数。issues 最多输出前 200 项，完整问题读取 C# snapshot。分页期间核对 generation，一旦变化从头读；当前没有固定服务端游标。JSON 不含媒体根目录、设备序列号；相对文件名和 package 仍是用户数据。媒体读取由授权调用方使用 `ResolvePath(track)`，接口本身不是 HTTP 文件服务器。

## 一次性兼容与一致性

旧 `thor-upper/lower-同批次.png/mp4` 按角色和同扩展名批次形成组，第一次持久化后以 ID 为准；任意改名也保持角色。读取旧 `.thor-meta.txt` 的 package，不改旧格式或旧 sidecar，不声称完整迁移捕获时间/物理显示 ID 等信息。CreatedUtc 当前取源文件 WriteUtc。没有规则的自定义文件各成一组，不能猜测任意两个视频是否同次录制。

路径更新优先 Windows volume/file ID，再用同路径指纹、唯一缺失轨道 SHA 回退。相同内容不是业务同一对象；歧义会报告 SOURCE_ID_AMBIGUOUS，不自动绑定。新的不同 legacy 批次即使字节相同，也不会继承旧缺失组。硬链接别名不会抢走仍存在的已知路径。SHA 回退只用于唯一缺失候选，无法证明任意离线复制的身份；正常复制应另行注册。更改源字节时保持既有 ID、更新 SHA 并标 Changed，直到调用方明确确认；Missing/Unreadable 不会自动移除组关联。Present 表示可读取并校验文件版本，不保证图片可解码、MP4 可播放，视频完整性仍由探测/播放器负责。

仓库绑定规范化的媒体根目录，另一个 mediaDirectory 复用仓库返回 ROOT_MISMATCH，绑定丢失但组存在返回 ROOT_BINDING_MISSING，不静默混库。整库搬迁由调用方先复制/移动原片，再显式调用 `RebindRootAsync`；它核对新根下所有仍存在的已知文件 SHA，至少验证一条（有组时），不一致返回 ROOT_SOURCE_MISMATCH。缺失轨道仍 Missing；只验证幸存文件，不能证明所有历史文件均已搬齐。它不移动原片。旧绑定存在时保留 RepositoryId；绑定及备份都丢失时明确验证恢复会生成新 RepositoryId、保留幸存组 ID。损坏绑定不会自动重置。新独立图库应选新仓库，不调用重定位。

组以版本化 `group-ID.thor-group` 保存：schema header + SHA256 + JSON。每组写入使用专属 CreateNew 临时文件、Flush(true)、回读验证、File.Replace/Move，保留上一完整 `.bak`。一次操作多组时是逐组提交，取消/异常可能留下已提交前缀；重新 Refresh 恢复当前磁盘状态，不承诺整个库单事务。发布快照在扫描完成后；失败返回的 Snapshot 可能仍是上一发布版本。根重绑定提交后若刷新失败，根绑定可能已完成，下一次正常 Refresh 重建；不是跨绑定和所有组的总事务。

损坏组记录移入 quarantine，合法备份可以恢复；不存在主文件也可从备份重建。恢复会带 SIDECAR_RECOVERED 问题，无法恢复报告 SIDECAR_CORRUPT，重新兼容原片可能产生新 ID：所有记录/备份丢失不可能承诺保留原关联。未来 schema 和暂时不可读记录留在原位并阻止未知来源再适配（RECORDS_UNAVAILABLE），防止创建重复身份。未知 JSON 字段当前不保留，未来版本需升级 schema。读只读/锁定仓库失败是明确结果，不伪造成功或悄悄内存保存。根重定位遇到任何记录问题先返回 RECORDS_NEED_REVIEW；首次可恢复操作本身可能已修复单组，先刷新审阅再重试。

写入用同进程异步串行 gate 和跨实例 repository.lock 租约；争用 3 秒超时，轮询 25ms 支持取消。源读取使用 ReadWrite|Delete，64KiB 分块 SHA，前后长度/mtime 和打开文件身份检查、释放句柄，不阻止外部修改，不写源字节。每轨最多 64GiB，组记录最多 1MiB，组最多 20,000，每组最多 16 轨（公开注册最多 2）；这些是输入边界，不保证机器内存足够。全量扫描会重新 hash 全部源，尤其大视频会慢；生产监控应使用增量路径，并在 UI 去抖。后台任务不会在 UI 执行全图解码。

## 验证与待接入

界面接入默认：新组未关联游戏、未收藏、空标签；缺轨保留另一屏，Unknown 角色必须明确标未知。没有自动游戏识别或自动删除。游戏选择需要 provider/libraryId/gameId/title，关联写入期间禁用重复操作，Revision 冲突显示“媒体记录已更新，请刷新后重试”；缺失游戏显示保留关联和重新选择/解除入口；Changed 显示“原片已变化，确认后继续”，不能当自动校验通过。没有持久 settings 键，本构造器目录参数由上层配置管理；限制与 3 秒租约目前是固定实现。没有百分比进度事件，前端可显示可取消的处理中状态，不能虚构百分比。重复事件或线程调度造成旧 Generation 晚到时，消费者应丢弃小于当前 Generation 的快照。

计划状态建议：用户 12 的稳定媒体组与轨道、用户 6 的游戏关联核心可标“后端已实现并离线验证，待采集/监控/前端/Node 接线”；组级实际文件事务、完整软件场景与公开测试验收仍待完成。总计划与交接表由主 agent 更新。

专项 58 项 + 核心 64 项 = 122 PASS；全量 Stream 617 PASS（基线 559 + 新增 58），复用合成流/假设备，日志见 evidence/media-repository/full-stream.txt。涵盖实际 NTFS 硬链接、跨实例 CAS/租约、取消、读写故障、损坏/未来记录、恢复/解除、外部修改、目录错配和 SHA 验证重定位，所有被测原片写入仅由 fixture 明确模拟，组件操作前后核对 SHA。只在独立 evidence 输出候选 exe，不替换现用软件。

1000 个 16×16 合成 PNG、1000 个原子 sidecar 的组件实测：首轮约 8.1–9.0 秒，完整 SHA 热刷新约 232–267ms，内存 GetSnapshot 计时 0ms（低于毫秒精度）。最终测试重跑有环境波动，原始 media-repository-performance.txt 与日志在测试 evidence。不是完整图库 UI 首屏成绩，未测真实大视频、多万张媒体、磁盘断电或真实设备。

后续集成：采集结束注册稳定组；GalleryMonitor 转发变更路径；前端按 GroupId 渲染双屏并持有 Revision，提供游戏选择/缺失提示/恢复审阅；Node 采用稳定游戏库 namespace。实际组级文件重命名/删除/移动及旧 .thor-meta/.input 配套文件的事务仍未实现，本包只跟踪外部路径变更且保持原片只读。不得把这些未接线功能标为软件已可用或公开发布已验收。
