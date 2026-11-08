# MediaLibraryService 后台图库组合

组合 `GalleryMonitor` 与真实 `MediaRepository`；只有仓库生成/持久化 GroupId、TrackId。文件监控不创造身份，前端不按文件名保存选择。源目录和仓库目录必须分离，切目录由调用方指定相应独立仓库；同根复用仓库保留UUID。不同根复用同仓库返回 repository 的 ROOT_MISMATCH，不会自动重绑/混入旧根数据；显式重定位仍用仓库自身验证合同。

接口（internal，C#5、系统框架）：

- `SwitchAsync(mediaRoot,repositoryRoot,token)`：后台建立该根的 watcher/repo、首次 reconcile；返回 typed MediaLibraryResult。只在路径字符串校验与锁内更新代号，目录/文件/仓库 IO 均后台执行。初始调用取消在初始化完成后失效，不充当长期监控开关。初始化等待上限15秒，超时撤销发布并取消该根。
- `Snapshot`：只读 immutable MediaLibrarySnapshot；根、service Generation/Revision、Outcome/Code、PendingFiles、仓库 immutable MediaSnapshot。读属性没有磁盘IO；切换立即换成新根空 INITIALIZING 快照，旧媒体不会继续显示为新根。
- `Changed`：后台线程发布，UI必须marshal后核对service Generation/Revision，按稳定GroupId恢复选择/滚动。不在回调阻塞或做IO。subscriber异常被隔离；故意不合作的阻塞subscriber会保持该根的owned worker，Stop超时能显示其未完成状态。
- `Refresh()`：请求当前 watcher 的full reconcile，非同步扫描。`NotifyOverflow()`为内部确定性溢出恢复注入，走与watcher.Error/周期检查相同路径。正常周期默认30秒，可internal构造短周期用于fixture。
- `StopAsync(timeout)`：关闭接受新根，撤销所有发布，取消待队列/旧根初始化，等待全部拥有worker和watcher实际结束；Success/TimedOut/FileFailure。若native IO或subscriber不合作，返回TimedOut保留任务，后续Stop可继续等；绝不Kill或宣称资源已释放。Dispose只作2秒有界等待，应用关闭必须处理非Success。

每根拥有独立monitor、repo与取消源；Changed路径合并HashSet、150ms组合防抖，repo调用串行，同根最多一条在途操作。目录代号拒绝旧根的迟到repo结果；旧根结束前不能假称已清理。最多8个尚未结束binding（含当前），避免反复切根无限堆积，不合作任务达到上限返回Busy；所有正常结束binding在下次切换回收引用。

初始/full/周期/overflow在PendingFiles=0时走RefreshAsync；存在半PNG/MP4时仅ImportPathsAsync稳定完成的peer，不对pending文件做仓库指纹读取，并记住待full reconcile直到writer完成。增量改变经sidecar映射交ImportPathsAsync；删除触碰旧track使其Missing，保留组/身份/关联/收藏标签。配对、rename匹配、sidecar恢复和元数据CAS规则完全复用仓库，组合层没有第二套媒体模型。监控稳定完成判定为PNG/MP4结构+稳定stamp/读取共享检查，不能称完整解码/不存在写入竞争；采集方仍应采用完成后原子rename。自动增量重点是Thor PNG/MP4；仓库其他支持格式在full刷新按其既有源校验规则处理，本批不宣称其他格式半文件验收。

切根只取消旧根自己的任务，不能撤销已经原子提交的记录；该记录仍留在旧仓库可恢复，不被发布到新根。各根仓库的目录锁/CAS保持原合同，目录锁等待和媒体读取取消由repo处理；发布事件不是主窗口已经接入。主窗口改造、采集注册与设置入口均未修改。

专项用真实FileSystemWatcher与Tests.Root临时媒体/仓库：空库初始、PNG自动UUID、双屏配对、immutable旧快照、sidecar元数据、rename保留UUID、删除保留Missing、overflow半文件延迟、晚完成、.partial原子rename、周期刷新、切根旧事件、同目录无效切换不破坏当前根、旧根仓库锁取消、后台事件、防抖请求、无IO静态快照、关闭取消与晚事件拒绝、缺失目录、预取消、阻塞subscriber有界Stop及二次等待。未操作生产captures、真实Cocoon或ADB，未替换运行exe。

证据：evidence/media-library-regression.txt、media-library-build.txt。仅交付service源码/专项/本文；从主集成复制的src/Media其他源码和broker依赖只用于编译，不能提交覆盖主集成版本。前端接法：用此服务代替单独图库watcher+repo刷新流程，避免双重watcher写同一repo；必要的收藏标签等命令直接由所选repo合同负责，再请求Refresh同步快照，后续adapter批次另行集成。