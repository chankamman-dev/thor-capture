# 固定截图字节的仓库发布

`MediaRepository.PublishGroupAsync(expectedRepositoryId, IEnumerable<PublishedPngInput>, CancellationToken)` 接收真实仓库，以及 1–2 轨固定 PNG 字节；无需外侧先写文件。`PublishedPngInput(relativeName, role, pngBytes, metadataBytes = null, inputTimelineBytes = null)` 复制调用者数组，侧车只能派生为 PNG 名称 + `.thor-meta.txt` / `.input.csv`。不能传路径回调或任意文件操作。可选 `IProgress<int>` 重载只报告已完成的主 PNG 数量，适合显示进度；回调线程在后台，禁止同步调用本仓库操作。

单轨可使用 Unknown；双轨必须各有 Upper / Lower。文件仅允许选定图库的叶名称，PNG 每轨 ≤32 MiB、≤8192 边长且总像素 ≤2400 万，校验签名、完整块边界、CRC、IHDR、IDAT、IEND 和真实解码。元数据 ≤64 KiB，输入 CSV ≤4 MiB。旧同名文件、侧车、目录或已登记轨道返回 `PUBLICATION_NAME_EXISTS`，不覆盖、不把旧文件算成功。仓库身份不一致返回 `PUBLICATION_ROOT_CHANGED`。

仓库持有自己的异步工作门和跨实例文件锁，先记录恢复日志并暂存所有字节，flush 落盘。最终文件发布前持久保存包含预定 UUID 的 Missing 组，发布侧车后才原子移动对应 PNG。这样 watcher 只能在仓库释放锁后匹配现有路径与 SHA，保留组与轨道身份，不会抢先建立一个单轨组。返回的 Group 使用真实文件重新读取后的指纹与包名。此过程在后台执行，没有修改主窗口或旧图库接口。

取消返回 `PUBLICATION_CANCELLED`；写失败 `PUBLICATION_WRITE_FAILED`；恢复输入缺失 `PUBLICATION_INCOMPLETE`；内容/目标冲突 `PUBLICATION_INTEGRITY_CONFLICT`。已真实发布至少一轨时返回含 Present / Missing 轨道的 Group，未发布任何主 PNG 时 Group 为 null；不会把未落地轨道标为 Present。仓库索引无法保存时返回 `PUBLICATION_INDEX_PENDING` 并保留文件与日志，不能声称元数据已经成功提交。最终索引保存对所有轨道在锁内完成；元数据错误不回滚删除原片。

`RecoverPublicationAsync(transactionId, token)` 根据私有 `DataRoot/transactions/<id>.thor-publish` 校验日志恢复；部分组的 `LegacyKey` 为 `publish:<id>`。未产生 Group 的中断日志只能由应用恢复入口枚举这个固定目录并展示，当前尚未提供自动启动恢复入口。临时文件不在完成后主动删掉以掩盖失败；日志失败时保留实际结果供审阅。恢复保留用户已改的收藏/标签/游戏关联；日志记录稳定 UUID 和固定名称、长度与 SHA，不携带像素内容。日志属于本地私有数据，不能进入公开诊断包。checksum 仅用于意外损坏检测，不构成同一用户恶意篡改下的授权边界。

针对真实临时仓库的 16 项专项检查通过：双屏唯一组、侧车、数组防御复制、刷新身份保留、旧目标拒绝、旧仓库拒绝、路径/角色拒绝、损坏 PNG、第一轨完成后的确定性取消、watcher 等效刷新不拆组、关闭重开恢复、保留用户注释、重复恢复、日志损坏、预取消。总计 80 PASS（含 64 个核心检查）。没有运行真实设备或修改生产图库；UI/截图服务适配由总集成负责。

root整合补记：现已提供 [RecoveryCatalog](recovery-catalog.md) 只读启动枚举，不自动恢复；CaptureService已经直接使用本接口，真实watcher在两轨发布间保留预定同一UUID。目录/祖先/日志/备份链接在写入前拒绝，真实junction保证零仓库外写入。前端恢复选择器仍未接入，旧“尚未提供”只描述单包初次交付。
