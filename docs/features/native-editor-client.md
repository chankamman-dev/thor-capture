# C# 原生编辑客户端

`src/Library/NativeEditorClient.cs` 接真实 `cocoon.native-editor/1` 后端；本批不修改 UI、CocoonBackend 生命周期或 Node/server。默认不自动连接、不自动暂存或设备写入。

## Root 最小接线

2026-10-11 新增 PreviewAsync 候选/裁剪像素接口与 editor/library 前后复核，接线和本轮依赖见[缩略图会话隔离与原生编辑预览](thumbnail-session-safety.md)。下文历史“root 负责 helper”已经在当前 CocoonBackend 实现，不需要重复添加。

后端 owner 在 `CocoonBackend` 内添加以下 helper。捕获/复核都在其现有 gate 下，不公开 token，不再获取第二套会话锁：

```csharp
internal NativeEditorHttpTransport CaptureNativeEditorTransport()
{
    lock (gate)
    {
        if (State != BackendState.Ready || endpoint == null || token == null)
            throw new NativeEditorException("BACKEND_NOT_READY", 0);
        Uri address = endpoint;
        string identity = instanceId;
        return new NativeEditorHttpTransport(address, token, delegate
        {
            lock (gate)
                return State == BackendState.Ready && endpoint != null
                    && endpoint.Equals(address) && instanceId == identity;
        });
    }
}
```

客户端构造：`new NativeEditorClient(backend, backend.CaptureNativeEditorTransport)`。helper 在本批工作树中没有修改，root 负责加到当前生命周期版本；客户端文件独立递归编译。

## API

所有耗时 API 返回 Task 并显式接受 CancellationToken：

- `ConnectAsync(token) -> NativeEditorInfo`：固定 GET `/api/native-editor`，验证 contract、UUID instanceId/libraryId、deviceWritesEnabled=false、previewAppliesAutomatically=false。
- `CandidatesAsync(itemId, ArtworkSlot, token)`、`PendingAsync(token)`。
- `CropAsync(itemId, slot, candidateId, x, y, width, height, outputWidth, outputHeight, token)`：原生像素 crop，固定 PNG/bilinear profile；源边界最终由后端验证。
- `StageAsync(itemId, slot, candidateId, expectedRevision, variantToken或null, token)`、`UndoAsync(entryId, expectedRevision, token)`。
- `MetadataPreviewAsync(itemId, Dictionary<string,object> fields, token)`、`MetadataConfirmAsync(previewToken, string[] confirmedFields, expectedRevision, token)`。

操作返回 `NativeEditorResult`：readonly Operation/RequestId/InstanceId/LibraryId，Data 为经 operation 对应 schema 校验后的 Dictionary 深拷贝。调用方修改 Data 不改变 DTO。候选/裁剪/变化字段/草稿分别限制字段、数量、ID、slot、SHA256、尺寸、布尔/数值/文本类型，拒绝未知字段。前端仍需以所选目录项和 request intent 丢弃选择切换后的响应。

编辑操作 POST 固定 `/api/native-editor/invoke`，发送固定 `{contract,instanceId,operation,input}`。不提供任意 URL/path/operation API，不请求 apply，不自动开启设备控制。逻辑 itemId 限现有 legacy-item:N / unresolved-native:N，revision safe integer，metadata 仅已定义字段；variant 同步 Node 的普通ID或 asset-variant:SHA256 namespace。

## 生命周期、错误与边界

每次请求由 owner 捕获私有 token/endpoint/后端 instance。token 只放认证 header，Origin 来自已验证 owned loopback endpoint，无 query、文件或日志。禁止 HTTP redirect、禁代理；15秒网络超时，CancellationToken 使用 HttpWebRequest.Abort 中止读/写。后台请求和 ConfigureAwait(false) 避免 WinForms 同步上下文阻塞。

握手显式绑定 editor instance/library。发出前及返回后再次复核 owner 当前 endpoint、captured instance；重新握手换对象后旧响应也拒绝。HTTP成功不代替 schema/version 校验；收到旧 instance/version、设备写入开启、重复 JSON key、未知字段、非 JSON、过大/不完整响应均拒绝。JSON请求 ≤64KiB、二进制上传 ≤32MiB、响应 ≤1MiB、JSON depth≤12，UTF-8严格解码，数字语法标准，不接受重复键或 trailing data。

`NativeEditorException` 仅有稳定 Code/Status，Message 只包含固定 code，不携带响应文本/URI/token。嵌套 error{code,phase,status} 保留真实 editor code/HTTPstatus；pre-envelope code/phase 形状也受检。取消返回 OperationCanceledException；网络超时/失败为 EDITOR_TIMEOUT / EDITOR_CONNECTION_FAILED。取消无法证明已提交草稿回滚，界面取消写意图后应刷新 pending revision，不能直接宣称“未保存”。

## 上传、异库归档和安全诊断

新增固定类型 API，沿用同一个 owner 捕获 helper，不添加任意 URI/route：

- `UploadAsync(itemId, slot, byte[], token)` 或 `UploadAsync(itemId, slot, Stream source, long length, token)` → `NativeEditorUploadResult`。明确请求上传本地候选，固定 POST `/api/native-editor/upload`，query只携带经校验的 instanceId/libraryId/itemId/slot。认证仍仅在 header。上传1字节至32MiB、16KiB分块、不缓冲整条网络请求；字节数组在返回Task前克隆，stream从当前位置精确读length字节，保持调用方所有权。调用方不要并发修改/Dispose stream；自定义ReadAsync需遵守取消token，客户端不会抛弃仍在读取的借用流后假报结束。文件路径和名称不进入协议。
- `ArchiveForeignAsync(bool explicitConfirm, token)` → `NativeEditorArchiveResult`。false直接固定 EDITOR_CONFIRM_REQUIRED，不发HTTP；true仅固定 POST `/api/native-editor/archive-foreign`，携带 instanceId/libraryId/confirm。成功必须 archived=true、revision0、空entries，并提供UUID ArchiveId和非负 PreviousRevision；同库草稿的服务端409必须保留，不能借归档绕过CAS。取消或归档响应丢弃不证明服务端无变动，应重新查看草稿状态。
- `DiagnosticsAsync(int maximum, token)` → `NativeBackendDiagnostics`，仅固定 GET `/api/backend/diagnostics?max=N`，1–256项。要求已经连接编辑会话并验证当前context；独立 `cocoon.diagnostics/1` UUID不是editor instanceId，同context内若诊断UUID变化拒绝旧会话，显式Connect重新绑定。

上传/归档是实际专用响应形状，不伪造invoke的requestId/error envelope。上传严格校验 contract/instance/library/deviceWritesEnabled=false/previewAppliesAutomatically=false/ok=true；仅接受 data.candidate 与 pendingChanged=false，候选ID必须等于SHA256、尺寸1–8192且面积≤16Mi像素。DTO的候选字段与context全部readonly，不暴露原始响应或路径。上传成功只是加入本地候选CAS，不暂存、更不设备写入。

归档专用DTO不保留文件路径、原库fingerprint或原始entries。响应同时校验editor/library身份，提交前后再 GET info核对同一实例/库，并在返回前拒绝owner endpoint或显式Connect上下文变化；所有三类新接口沿用严格JSON/UTF-8、有限响应、禁止重定向和token Abort。每个HTTP请求独立15秒期限，上传源异常转换为固定 EDITOR_UPLOAD_SOURCE_FAILED，不包含源错误文本。

诊断DTO只投影readonly UUID、UTC时间、capacity/recorded/dropped/retained以及readonly事件集合；事件类别/代码/组件使用与Node `backendDiagnostics.js`完全对应的枚举白名单。可选 operationId/durationMs/count/success严格按UUID、整数范围和bool验证。未知自由文本字段、任意code、非UTC格式、浮点计数、重复事件UUID、超额/缺失行或不匹配contract均拒绝。客户端不提供raw日志或raw诊断JSON导出。library identity单独端点仍未封装，身份来自editor info。

## 离线证据

`tools/test-capture-tool.ps1 -Filter NativeEditorClient`：本机自有 Node HTTP 夹具验证 token header、严格 JSON/schema、嵌套错误、重定向、超大响应、取消、实际迟到响应、同 endpoint 换 instance。自有实际 Cocoon backend 使用新 demo root，候选→crop worker→stage→CAS冲突→undo→metadataPreview→单字段confirm通过，原 demo cocoon_db 字节完全不变；退出只关闭本次拥有的 backend。

测试仅对自己创建的 demo backend 用反射捕获其私有 token，以避免修改用户在途的 CocoonBackend；正式接线使用上面的 owner helper，不反射生产会话。没有实机、生产库或用户已有进程的操作。证据输出在 evidence/native-editor-client-regression.txt；本批 source 中没有日志记录请求内容或 token。


扩展回归保留原39项，并新增上传字节快照/借用stream/32MiB预算、读取期间取消和源异常脱敏、明确归档确认、跨库响应、诊断白名单/UUID/UTC时间/整数/集合不可变、三路实际HTTP取消与迟到response。真实自有demo Node backend额外验证PNG上传CAS、诊断投影、同库归档409、有效checksum异库fixture归档保留原文件并初始化revision0，原cocoon_db字节仍不变。扩展证据 `evidence/native-editor-extra-regression.txt`；只操作本次生成的demo root，未连接Thor或访问生产素材库。

并发连接按本客户端递增代次绑定，迟到的旧 Connect 响应不能覆盖较新的连接。七种 invoke 操作在发送前和成功结果返回前重新读取固定 Info，确认相同外壳编辑 instance 与 library UUID；换库后拒绝旧结果。服务端已完成的 CAS 提交不会因后置检查失败回滚，调用方遇会话失效须重新连接和读取 pending，不能自动重放写请求。该保护已用拥有的离线 Node HTTP fixture 覆盖，未进行实机操作。
