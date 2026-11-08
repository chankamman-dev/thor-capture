# 原生批量刮削客户端与来源调度

2026-10-11。Codex 后端交付；Claude 继续负责原生设置、来源候选与批量任务界面。

## 当前交付

Cocoon 后端提交 da89d8bdebec0bd8b1896bda49636b83ef11a462：

- 六来源有界 JSON 请求：覆盖正文的超时、16 MiB 上限、取消、固定主机与拒绝重定向。
- 来源间并行，同来源操作与网络请求共享有界队列，凭据隔离缓存。
- 库身份绑定的后台批量任务，创建/进度/列表/取消，部分失败仍保留可审阅候选；换库不继续处理旧 ID，不自动应用。
- 原同步接口保留；普通搜索保留用户原有的明确候选选择。

组件清单 045c71405a11f05cc90b943a4e10a29554f791f0a343325626b1b81c0d997d85，104 个文件、845030 字节。候选在 evidence/cocoon-completion-20261011/scrape-component。发布状态仍为 false，Node 和 FFmpeg 运行时未内置。

详细 HTTP 合同见 Cocoon 源码 docs/SCRAPE-JOBS.md。

## 前端接法

复用已有 NativeEditorClient，经 ConnectAsync 建立编辑实例和库绑定：

~~~csharp
NativeScrapeJob job = await client.StartScrapeAsync(
    selectedGameIds, selectedProviderIds, "balanced", "en", false, cancellation);
job = await client.ScrapeStatusAsync(job, cancellation);
IList<NativeScrapeJob> jobs = await client.ScrapeJobsAsync(cancellation);
job = await client.CancelScrapeAsync(job, cancellation);
~~~

- gameIds 为 1–200 个正整数；providerIds 只允许六个已接入来源。useExistingHashes 只使用本地已有 Hash，不读取掌机 ROM。
- 页面恢复时读 ScrapeJobsAsync；列表句柄 HasDetails=false，再读 ScrapeStatusAsync 获得逐条记录。只展示当前库任务。
- NativeScrapeJob 含 JobId/LibraryId/InstanceId/State/Total/Completed/Identified/Failed，Data 为独立深拷贝。Finished 判断终态；completed 仍需检查 Failed。
- 取消令牌仅取消当前 HTTP 操作。后台任务需显式 CancelScrapeAsync；创建回复丢失时先恢复任务列表，避免重复启动。
- 客户端捕获拥有 transport，前后重新核对编辑实例和库。旧会话句柄不能指向新编辑实例。
- UI 更新时仍需前端自己的页面 generation/选中库检查和 UI 线程派发。
- 获取候选、逐字段复核及显式暂存沿用原有编辑合同。批量结果不自动选择、不自动暂存、更不直接写掌机。

队列最多 4 个活动任务、20 个本次会话结束任务；每批最多 15 分钟。后端重启不自动续作，已完成候选保留在对应扩展库。每行错误列表最多显示前 4 个，完整进度受 1 MiB 客户端响应上限约束。

## 验证

- Node 全量 384/384，无跳过；日志在 Cocoon evidence/completion-20261011/scrape-full-tests.txt。
- 实际隔离后端创建/轮询/任务恢复/幂等取消已通过，原游戏 DB 字节保持不变。
- C# 专项包含会话更换、不同库、取消、固定错误代码、统计与逐行数据一致性、不可变 DTO；宿主完整离线回归 1203/1203 通过，日志 evidence/cocoon-completion-20261011/scrape-host-full-tests.txt。
- 隔离 EXE 构建成功，SHA256 为 28A14C559FC6174C0B603C505098C7E14769910773C4E65CB89FC3D46423D912；输出 evidence/cocoon-completion-20261011/scrape-candidate/ThorCapture.exe。保留 ImportDialog.cs 的既有 CS4014 警告。最终便携探针结果单列证据。

没有改界面，没有使用真实来源凭据，没有 ADB/设备写入，没有替换用户 EXE。主目录在途 MetadataDialog 的 C# 5 编译问题仍属于 Claude 前端接续范围。Cocoon 全功能目标仍包括迁移/变更中心、布局/视频素材、文件管理、真实 helper 与设备/发布验收。
