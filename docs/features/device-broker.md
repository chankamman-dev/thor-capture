# 共有设备队列与只读broker

`DeviceSessionRegistry`/`OperationGate`继续作为唯一会话与租约域；`DeviceCommandBroker`在loopback动态端口提供 `thor.device-broker/1`。`CocoonBackend.BindDeviceBrokerAsync`仅经拥有Node child的stdin发送地址、UUID和随机凭据，重启自动重新绑定。HTTP前端、环境、URL、命令参数和诊断不暴露token。

每次请求指定serial和当前session UUID，只允许catalog/catalog-wal两个固定scope。先验证当前registry、取得ReadLibrary租约，再由拥有C# reader执行；Node不持有租约、不执行adb、不接收任意shell/path。最多64连接、4096字节JSON、30秒请求、64MiB数据；定长frame、长度/SHA校验、超时/断开/取消均覆盖真实loopback测试。

`DeviceTransportGate`按显式serial使用Windows命名mutex排短命令，另持有同serial散列的FileShare.None文件句柄；清理结果false时保留文件占用直到明确ack，即使返回后释放mutex，另一Capture进程也不能发设备调用。不同serial独立。仅支持devices-l/version/help等无目标主机查询；设备命令必须带唯一首部-s选择。长预览/录制/输入只对拥有Process.Start排队，运行期生命周期仍由对应owner及OperationGate负责，不能声称外部第三方ADB也被本工具控制。

`BrokerFileReader`使用固定普通exec-out cat，无su/root、应用force-stop、设备临时写入或整库apply。Android私有目录可能不可读，返回DEVICE_READ_DENIED并保留功能禁用。Node拉取两次DB/WAL内容比较，在私有隔离根验证，成功仅生成可审阅import plan，不自动写库；草稿非空、旧会话或未配置broker均零设备调用。

`Adb.Run`旧签名保留，内部改为拥有的有界文本runner；新增CancellationToken/RunAsync。二进制调用有64MiB限制并排队；旧Capture包装仍同步，前端迁移到CaptureService。LiveVideo/InputCapture/RecordingAudio启动需明确serial，不以默认第一设备启动。清理未确认时返回失败并阻止后续同serial调用；不结束用户其他进程。

关闭先请求broker.StopAsync，取消socket和拥有reader，只有jobs/reader实际结束才释放保留租约；false继续由ShutdownCoordinator持有owner重试。Dispose不等于清理ack。进程崩溃释放OS句柄不能证明设备端长任务已停止，真实断线/孤儿远端任务恢复仍需实机验收。

旧静态文本/二进制owner可注册 `Adb.StopOwnedAsync(timeout)` 到全局关闭协调器；此API用于最终关闭，会永久关闭其runner，不能关闭后继续在旧静态域执行新命令。false保留占用，true才释放这些owner自己记录的清理滞留serial。其他长流owner仍各自停止，不按进程名结束ADB。

离线证据：DeviceBrokerTests包含真实C#/Node握手、只读fixture清单/确认导入、权限/认证/旧session拒绝、排队/断开、清理滞留及另一测试进程零调用；来源DB不变。未操作Thor或生产数据。Node详细HTTP合同见其docs/DEVICE-READ-BROKER.md。
