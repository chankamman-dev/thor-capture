# 原生草稿与变更中心客户端

2026-10-11。Codex 后端；Claude 接入变更审阅与记录页面。Node 源码提交 d3ac026，HTTP 合同 cocoon.native-changes/1，详细规则见 Cocoon docs/NATIVE-CHANGESETS.md。

## 前端接法

使用已 ConnectAsync 的 NativeEditorClient 和当前 pending revision：

~~~csharp
NativeChangeReview preview = await client.PreviewChangesAsync(revision, cancellation);
// Render the exact before/after fields, artwork descriptor and blockers from preview.Data.
NativeChangeReview saved = await client.ConfirmChangesAsync(preview, true, cancellation);
IList<NativeChangeReviewHandle> records = await client.ChangeReviewsAsync(cancellation);
saved = await client.ChangeStatusAsync(records[0], cancellation);
// After explicitly confirming that this only revokes the unapplied intent:
saved = await client.RevokeChangesAsync(saved, true, cancellation);
~~~

NativeChangeReview 包含 ReviewId、LibraryId、InstanceId、ChangeSetId、Revision、State、IsPreview、ChangeCount。Data 每次返回独立深拷贝，含已验证 changes、warnings、blockerCodes、expiresAt；只有预览有到期时间。图库素材保持 content-addressed 引用，图像预览沿用原生编辑像素接口。

- 先以 MigrationPlanAsync/ConfirmMigrationAsync 完成用户审阅过的结构迁移；EDITOR_MIGRATION_REQUIRED 不得通过自动迁移绕过。
- PreviewChangesAsync 读取当前整个草稿，最多 100 项，总素材最多 128 MiB，预览 5 分钟到期；无实际差异返回 EDITOR_REVIEW_EMPTY。
- 确认后不自动清空草稿。新编辑仍保留，前端可用 Revision/ChangeSetId 标识已提交审阅；不要自行覆盖 pending 或复用旧 revision 撤销。
- EDITOR_REVIEW_STALE/EDITOR_REVISION_CONFLICT/EDITOR_REVIEW_EXPIRED 表示需重新预览。前端不可悄悄重审后自动确认。
- HTTP 超时、取消或断线不证明提交未发生。可用原 preview 调用 ChangeStatusAsync，或重复 ConfirmChangesAsync；后端重启后重新 ConnectAsync，再列记录恢复当前实例句柄。
- 列表仅当前库最近 50 个审阅入口；重复相同意图可指向同一 ChangeSet。撤销一个会同步反映到其他入口的生命周期。
- 已验证的 DevicePlan 仍为不可执行。blockerCodes 必须展示，不能把 READY/提交成功写成“已同步掌机”。RevokeChangesAsync 撤销本地意图，不能写成“掌机已还原”。
- 所有调用绑定编辑实例和库；迟到响应拒绝。界面仍需自己的页面 generation 检查和 UI 线程派发。

## 验证

Node 完整回归 409/409，无跳过；C# 实际后端新增 7 项流程检查，覆盖迁移门槛、元数据+裁剪素材审阅、明确确认、重复提交、列表/状态与保留草稿的撤销。另有 17 项响应校验检查，覆盖库/实例/审阅绑定、只读能力、字段类型、before/after 和统计一致性。完整宿主回归与包清单见 evidence/cocoon-completion-20261011/native-changes-delivery.json。

本批未接界面、未迁移生产库、未写掌机、未替换用户正在使用的 EXE。原目录三份前端在途文件继续保留。
