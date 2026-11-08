# 6 号包交付：Release

## 所有者需决定的事项

1. 自有代码许可证（建议 MIT，亦可 Apache 2.0/GPLv3）和版权主体、历史贡献者公开署名。未添加最终 LICENSE。
2. AYN / Thor 名称、外壳和刻字 logo 的公开使用边界；不宣称官方关系，不分发参考照片/游戏画面。
3. 建议 ADB/FFmpeg 外置，scrcpy-server 保留当前随包方式并附 Apache 通知；如改为全部外置，Track A 必须调整构建/查找及声音降级。
4. 真实维护者身份、公开反馈入口、私密安全/行为举报通道及支持窗口。当前均明确未配置，未捏造联系信息或时限。
5. 公开历史处理方案：保留私有原库后建立经过审查的干净初始公开库，或经批准重写历史；本包只报告位置，不改历史。
6. 最终候选、公开仓库、推送/tag/Release 需最终明确批准。本包不进行任何公开动作。

## 已实现的离线材料

逐文件 CSV（来源、Git 首次/末次提交、公开匿名贡献者代号、哈希、许可状态、改动、是否分发），素材/品牌/许可比较，实际依赖版本/哈希/FFmpeg buildconf，第三方 NOTICE 草稿；贡献、安全、行为准则、变更日志、架构草案、Issue/PR 模板；版本与兼容政策；位置脱敏的工作树/全部可达历史/目录或 ZIP 审查与精确包 allowlist。

Windows CI 已写好，checkout/upload-artifact 均完整 SHA，contents 只读，无发布秘密/写权限/公开步骤。基础与完整合成流分开，窗口/STA失败直接失败，不静默跳过。FFmpeg 固定官方构建版本/归档哈希、只用于开发测试，不上传；真实 hosted runner 尚未执行，因此不能标 P7-5 已验收。

## 接口和线程

本包不新增应用功能类，不修改 Track A 源码/界面/框架。以下命令在调用者线程同步执行；默认无设备操作，不占 OperationGate 或设备/编码器租约。

```powershell
./tools/release-scan.ps1 -Scope WorkingTree -ReportPath evidence/review-worktree.json
./tools/release-scan.ps1 -Scope History -ReportPath evidence/review-history.json
./tools/release-scan.ps1 -Scope Package -PackagePath <staging-folder-or-zip> -ReportPath evidence/review-package.json
./tools/test-release-scan.ps1
./tools/update-release-provenance.ps1 -Revision HEAD
./tools/prepare-release-ci.ps1 -Download
./tools/test-capture-tool.ps1 -Stream
```

`release-scan`: 0=启发式匹配未命中（仍需人工许可/媒体审查），1=需要审查，2=输入/读取/枚举失败；报告 schema=1，scope/filesChecked/status/findings/limitations；每项仅 path/line/rule/revision，不输出匹配值。默认 WorkingTree 包括 tracked 与未忽略 untracked，不审查忽略的本机数据；History 为全部可达 refs，并非不可达对象；Package 要求 exact/case-sensitive allowlist 的每项存在，无额外文件，无重复 ZIP 条目。不会解压候选 ZIP或执行其内容。没有笼统忽略路径规则或“知道有问题还放行”开关。

`update-release-provenance`: 当前检出文件哈希 + 指定 revision 历史，不能对非当前检出版本声称哈希匹配；每次冻结先检出对应版本，生成后核对。作者邮箱和姓名不导出，Contributor 代号仍需权利人确认公开署名。CSV 自身不列入，避免自引用哈希。

`prepare-release-ci`: 固定 URL、归档 SHA-256、版本与执行路径，显式 `-Download` 才下载；校验失败不解压/不执行，检查压缩条目不逃出缓存目录，缓存位于忽略的 evidence。下载超时120秒；不会把该依赖包作为产品分发或上传 CI 产物。

设置键：无。报告必须保存在忽略的 evidence；公开文档只汇总相对位置，不附私人值。错误不降级为“通过”。扫描为保守规则，loopback、合成测试地址、代码正则、上游 binary 等需要人工分类，不能自动断言泄漏。

## Track A 集成需求

- P6-3：集中应用/程序集/manifest 版本来源，填写关于页的非官方说明与第三方归属；依据最后决定生成 LICENSE。
- P6-4：确定可写数据目录与标准用户/只读 EXE 路径行为；明确图库、配置、CSV、日志、临时 server 生命周期和删除方式。
- P1-4：日志不写完整 serial/路径/凭据，异常文本也脱敏；定义保留与清理策略，诊断包默认本地、用户审阅后手工提交。
- README 公开改写：去私人路径/实际 serial/私有作者邮箱，说明已验证功能和未测设备边界；不要把已有实现写成实机验收。
- 发布前确认反馈入口/安全通道，修订行为准则执行与申诉流程；这些未定项当前阻止宣称材料可直接公开。
- 审查 allowlist 与 server 分发决定一致；P7-6 真正构建 candidate ZIP 并扫描，不直接压缩 artifacts/evidence。当前实际 build 输出检查不是完整 release ZIP。
- 来源 CSV 仅覆盖基线 `faaf1463c2331e854c308e07afad85c4d156bdbd` 的68个文件；本包新增材料由本包提交记录。其他 Track B Feature 源码/测试/文档和 Track A 改动由 Claude 集成后重新生成 inventory、扫描全部历史与最终候选包；当前证据不覆盖未来集成代码。

## 验证与建议状态

基线的全量 `-Stream` **221 项通过**，分别使用原配置 FFmpeg 6.0 与固定 CI FFmpeg 8.1.2 完整执行均通过；本包扫描器 **14 项**正反例通过（安全输入、secret命中、不泄露匹配值、已删除历史、额外/缺失包文件、ZIP路径穿越/重复项、输入失败）。从新本地干净克隆按贡献指南，构建、149项基础及221项完整Stream通过，无私人路径配置。源代码未改变应用行为。依赖 host 版本和 buildconf 已核，不连接 Thor。不代表 hosted runner、标准用户干净机器、硬件或最终公开候选已验收。

建议 ROADMAP：P7-1/2/3 为“材料和离线审查完成，决策/清理/候选复查待完成”；P7-4 为“文档模板完成，真实通道/身份待配置”；P7-5 为“工作流和本地步骤准备，真实 Windows runner 待验收”；P7-8 为“规则提案完成，维护者确认待定”。P7-6/P7-7不在包内，不标完成。
