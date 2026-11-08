# 图库监控、应用协调和安全设置：后端合同

本批对应用户11（P5-1）、用户10（P1-2）及 P1-5 可离线底座。新增后端/测试，并为GalleryIndex增加CancellationToken扫描重载；旧 `Scan/Filter`接口保持。MainForm、Controls、Preview、原 Settings 和外壳渲染未改。没有监控或修改生产图库/设置，没有连接 Thor。前端由 Claude 接入；本批不建立第二套媒体 ID 或替代另一个批次的 MediaRepository。

## GalleryMonitor

`ThorCapture.Backend.GalleryMonitor`：

- `SwitchAsync(folder, token)` → `GallerySwitchResult`：Success / Cancelled / Closed / MissingDirectory / FileFailure / TimedOut。所有目录/媒体/sidecar读取在后台；路径参数在提交时固定。默认初始化10秒超时，可配置100–60000ms；取消/超时撤销对应generation，迟到扫描不得发布。
- `Snapshot`：不可变 `GallerySnapshot`，含Folder/Generation/Revision、Outcome、PendingFiles、FullRescan、Items和ChangedPaths。每个item字段readonly，集合只读；不暴露可变CaptureMetadata，不分配组/媒体ID。
- `Changed`：在后台触发，前端marshal后再次核对Generation/Revision，不用事件里扫描磁盘。ChangedPaths含原始事件路径，包括sidecar；MediaRepository可以接收它们做自身身份/关联更新，FullRescan时统一reconcile。
- `Refresh()`：显式完整重扫；watcher溢出/error和4096个路径队列溢出也使用相同路径。默认30秒后台完整对账（可设1–120秒），不能只依赖watcher通知。`NotifyOverflow`内部入口用于确定性溢出测试。
- `StopAsync(timeout)`：返回Success/TimedOut，先撤销发布/取消等待，再等待拥有的watcher/worker退出；最长等待由调用者控制（≤1分钟）。Dispose等待2秒。不会清理外部进程。

第一次全扫复用GalleryIndex，其后按相关路径增删/更新；文件名配对变化更新双方Paired状态。保存 `.partial`/`.tmp` 不显示；最终PNG/MP4须稳定两次、能排他阻止写入地打开、通过PNG首尾或MP4顶层结构检查。直接写最终名的未完成文件只计pending，不能报“损坏”；旧路径正在被改写时不供新的索引读取。pending每次变化重试，约40次后等下一通知/周期重扫，避免永久忙轮询。检查不能证明图片完整解码或视频整段可解码，后续缩略图/媒体验证按其合同处理。

主窗口保留选中项/滚动位置由前端和媒体ID负责，不能按一次快照下标重新选择。绑定新目录先记新generation，旧图可保留为显示缓冲但不得写进新目录结果。网络文件系统若底层同步IO不能退出，Stop会明确TimedOut；不杀线程或伪称释放，前端可以重试/说明仍在清理。后端只读本地文件，没有写图库、删除原片或读取游戏库数据。

## AppCoordinator / AppStateSnapshot

构造时传现有 `OperationGate` 与 `DeviceSessionRegistry`，不创建第二个会话/租约系统、不dispose调用方的gate/registry。

- `BindSession(DeviceSession)`：绑定当前不可变session，取消旧session的本队列任务；实际连接/退役由既有registry/broker负责，不调用ADB。
- `Submit(name, work, timeout, token)`：排本地任务。`SubmitDevice(name, fixedSession, kind, work, timeout, token)`：先通过既有gate租约再调用任务，busy以30ms间隔等到明确超时，Unsupported/Disconnected失败零设备调用。
- `work(AppJobContext)`接受Token、JobId、固定Session和Lease，返回Task。任何设备/进程/文件工作的实际清理由work完成；协调器不结束用户其他进程。
- `AppJob.Result`类型结果：Success/Cancelled/TimedOut/Failed/Disconnected/Unsupported/Closed/QueueFull/Stale，含不泄露异常/路径的Code。`Cancel()`可立即取消排队/在途等待；同一队列最多64个待处理项，顺序执行。
- `Snapshot`不可变AppState：Revision、Closing、当前SessionId、Active与Queued；每个job含Id/Name/Kind/DeviceWork/SessionId/Phase。Phase为Queued/WaitingForLease/Running/Draining/Finished。状态事件由独立后台dispatcher串行发送/合并，不让观察者的continuation反向阻塞作业。
- `StopAsync(timeout)`：取消所有拥有的任务、禁止新提交，等待作业与状态dispatcher，返回Success或TimedOut；Dispose等待2秒。任务超时和停止的时间使用单调计时；上限设置防止无界调用等待。

**前端不能只看Result解锁busy。** 不合作的work虽然有及时TimedOut/Cancelled结果，但仍处Draining、保留设备租约，直到实际拥有资源退出；后续工作不与它重叠。看Snapshot.Active/Phase确定禁用原因及“取消/仍在清理”；再次Stop可等后续完成。取消的排队任务不会执行，异常变类型失败；session退役的在途任务不能重绑新设备。

UI中的预览/录制/输入服务需逐步Submit接入，现有busy标志尚未自动迁移。本批没有擅自打开设备远程控制、声音、无线或Cocoon写入能力；跨进程租约由其独立broker合同处理。

## SettingsStore / DataDirectoryPolicy

`SettingsStore(path)`为一个本地配置域，`LoadAsync(legacyPath,token)`与`SaveAsync(patch,token)`均后台执行、进程内串行；多个store实例/进程通过同路径`.lock`文件（FileShare.None）仲裁，锁等待3秒。提交patch先快照，再在锁内读最新磁盘值合并，保留未知字段，不丢其他writer的修改。

schema1为UTF-8、URL编码值、schemaVersion及整文件内容SHA256；原子临时写、Flush(true)、重新解析验证、File.Replace（保留`.bak`）/首次Move。严格拒绝超大/坏UTF-8/非法键/重复schema与key/校验和不匹配，未来schema返回UnsupportedSchema且绝不覆盖。旧key=value可一次迁移，保留旧文件原字节及灯色/路径/中文/别名等未知字段。`schemaVersion`与`checksumSha256`是保留键。

损坏主文件有有效`.bak`时Load返回Recovered并展示有效备份；修复后的Save将损坏原件放独立`.corrupt-<id>`，保留原有效`.bak`。没有有效备份则Corrupt，不能默默回到默认配置覆盖原件。写失败FileFailure、锁超时TimedOut、取消Cancelled，不能界面宣称“已保存”。Snapshot内数据不可变，CopyValues仅返回拷贝，GetInt/GetBool显式有界默认值。

prefs / calibration / capability cache可分别实例化不同store路径；旧配置本轮保留所有字段，不擅自分类拆写或修改原界面Settings。前端迁移后再关闭旧writer，不能让旧/新格式同时写同一文件。图库路径值始终原样保留，由前端校验，不能悄悄替换目录。大批slider等写请求可由前端合并patch；本层不丢任何单次提交字段。

`DataDirectoryPolicy.ResolveAsync(exeFolder, localAppData, portable, token)`：后台测试写探针，默认LocalAppData/ThorCapture；显式portable使用exeFolder/ThorCapture。返回Success/Cancelled/InvalidPath/NotWritable；便携模式失败不会暗中换存储位置。没有实际调用用户LocalAppData，本批全用fixture根。

普通本地IO不能强制中断正在执行的系统原子替换；前端长等待统一通过AppCoordinator作业预算包裹，报告Draining时等待底层真实完成/重新读确认，不把取消超时当作确定“未写入”。设置取消点在进入提交前，atomic替换不进行破坏性中途回退。使用系统.NET Framework 4.8可用的Task异步continuation选项，仍为C#5、不用NuGet/新运行时。

## 前端需要接什么

1. 图库目录变更调用SwitchAsync，订阅最新只读快照，选择/滚动按稳定ID保存；ChangedPaths交媒体repo、FullRescan做reconcile，半文件显示“写入中”而非“损坏”。目录不可用/初始化超时有明确提示和手动Refresh。
2. 设置目录通过DataDirectoryPolicy取得，迁移弹出当前/旧配置状态，写失败不更新“已保存”文案；Corrupt/Recovered/UnsupportedSchema分别提示且保留恢复材料。确认便携模式入口与三域文件名，再停掉旧Settings writer。
3. 将旧busy/detecting/export等逐批映射为Coordinator Active/Queued/Phase；支持本地队列取消/设备固定绑定，Draining保留禁用直到实际清理完成，不反向dispose公共gate或registry。
4. Changed事件都是后台线程，marshal到UI后拒绝旧revision/session/directory，不在事件回调跑磁盘扫描或线程阻塞。

## 离线证据与边界

专项测试真实watcher创建/删除/原子重命名、慢PNG半文件、sidecar-only、配对、不可变快照、溢出模拟/周期对账、缺失目录恢复/切目录/取消/关闭；协调器队列、容量、取消、异常、租约冲突、断线/能力、不合作任务保留租约及有界关闭；设置迁移、两个store并发、未知字段、UTF-8中文、校验恢复/损坏备份、未来schema、锁超时、写失败/取消、数据目录策略。测试只在Tests.Root，不变更用户图库/配置。

未做实机、生产图库监控、真正OS watcher缓冲溢出/网络挂载/满盘/新电脑权限矩阵。实际目录权限/长IO与前端状态迁移、MediaRepository衔接需统一集成验证；不能把后端API已有称为主窗口已接入。

本批离线验证：50 项新增专项通过，全量 Stream 回归 609 项通过；独立候选构建成功。证据保存在忽略的 evidence/catalog-foundation/{regression.txt,full-stream.txt,build.txt}，主窗口与实机验收仍未完成。
