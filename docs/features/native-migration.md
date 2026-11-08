# 原生迁移审阅与还原客户端

2026-10-11。NativeEditorClient 增加受管理扩展库迁移入口；界面仍由 Claude 接入。本批未迁移生产数据或替换正在使用的 EXE。

## 接入流程

复用拥有 CocoonBackend 的 NativeEditorClient，先 ConnectAsync，再调用：

~~~csharp
NativeMigrationPlan plan = await client.MigrationPlanAsync(cancellation);
// Show versions, pending steps, rehearsal counts and warning counts from plan.Data.
NativeMigrationRecord record = await client.ConfirmMigrationAsync(plan, true, cancellation);
record = await client.MigrationStatusAsync(record.MigrationId, cancellation);
IList<NativeMigrationRecord> records = await client.MigrationRecordsAsync(cancellation);
// Only after reviewing the restore record and receiving explicit confirmation:
record = await client.RestoreMigrationAsync(record, true, cancellation);
~~~

- confirm 参数只可在用户审阅后设置为 true。接口拒绝 false，不默认迁移；NoChanges 时不需要提供迁移按钮。
- 计划绑定 LibraryId 和 InstanceId；只在副本演练，实际扩展库字节不变。展示版本源不一致及 catalogReconciliationRequired 警告，不能写成目录已全面导入。
- 计划最多保留 15 分钟。确认时再次校验扩展数据库、WAL/SHM、候选及库身份，变化后重做预演。
- HTTP 取消和 15 秒期限不能代表已经回滚。发布开始后后端先完成提交或还原；按 plan.PlanId 查询 MigrationStatusAsync，或重试同一计划，不自动启动另一轮迁移。
- 已最新时返回 State=unchanged，不写库、不创建持久记录；这个 no-op 的 UUID 不用于后续状态查询。
- 页面重开用 MigrationRecordsAsync 获取当前库最近 50 条记录。CanRestore 是读取时的字节状态；确认还原时后端会再次核对，不覆盖迁移后新数据。
- 后端重启需重新 ConnectAsync，再从记录列表获得当前实例句柄。旧会话对象不会被悄悄转到新库。
- 所有公开 DTO 校验固定合同、字段类型、版本/统计范围和能力标志；plan.Data 为深拷贝。异常只返回白名单代码。页面仍需 UI 线程派发和自己的 generation 检查。

详细 HTTP、事务中断恢复和备份语义见 Cocoon docs/MANAGED-MIGRATION.md。固定合同 cocoon.managed-migration/1 仅操作受管理数据根的 cocoon-art.db 与两个 SQLite 侧文件；原生 cocoon_db 保持不写。

## 验证范围

Node 全量 397/397 通过，无跳过。C# 新增 20 项响应合同检查和 9 项实际 Node 后端迁移流程检查；后者覆盖源字节未改、显式确认、重复执行、列表/状态、no-op 和精确还原。专项日志位于 evidence/cocoon-completion-20261011/migration-client-tests.txt 和 migration-parser-tests.txt；完整回归、组件及便携探针证据另记交付记录。

实际窗口未接入/验收，生产数据未迁移、掌机未连接。原生草稿到 ChangeSet 的通路仍需后续完成。
