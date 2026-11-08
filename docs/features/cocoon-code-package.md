# CI6：独立 Cocoon 纯代码候选组件

本组件把冻结 Node 后端复制到全新的 `candidate/cocoon`，让 `CocoonBackend.DefaultBackendDir()` 优先发现程序旁的 `cocoon/backend.cjs`。没有修改 C#、UI、旧发布脚本或旧发布 allowlist，没有捆绑运行时、决定 LICENSE、发布或推送。当前产物仅是 evidence 下离线候选，`publicReleaseReady` 始终为 false。

## 入口

使用已有 Node >=22（本轮实测 v24.18.0），它是执行工具，不复制进候选。脚本不下载、不安装依赖。`tools/package-cocoon-code.ps1` 是 UTF-8 BOM PowerShell 包装器，核心为 `tools/cocoon-code-package.cjs`。

```powershell
# 父目录必须已存在；Candidate 本身必须不存在，空目录也不能复用。
./tools/package-cocoon-code.ps1 -Mode Pack -SourceRoot ./frozen-cocoon-source `
  -Candidate ./evidence/new-candidate -ExpectedCommit <40位冻结commit>

# 从 Pack 的单独结果保存 manifestSha256，随后显式校验。
./tools/package-cocoon-code.ps1 -Mode Verify -Candidate ./evidence/new-candidate `
  -ExpectedCommit <40位冻结commit> -ExpectedManifestSha256 <64位manifest摘要>

# DataRoot 必须为不存在的绝对路径，位于 candidate 之外；不会复用任何旧游戏库。
./tools/package-cocoon-code.ps1 -Mode Smoke -Candidate ./evidence/new-candidate `
  -ExpectedCommit <40位冻结commit> -ExpectedManifestSha256 <64位manifest摘要> `
  -DataRoot <全新绝对数据目录>

node --test tools/test-cocoon-code-package.cjs
```

命令行只输出相对路径信息的摘要或 typed 错误码，不输出 session token、环境凭据、数据根或进程原始错误。Pack 和 Verify 都不启动后端。Smoke 启动拥有的 `node backend.cjs`，强制 managed/设备禁用，仅请求回环 HTTP；剥离 Node 注入选项、Cocoon 和常见 provider 环境配置，token 仅在内存中。检查结束通过带 instanceId 的 stdin shutdown 并关闭 stdin，超时仅结束自己创建的子进程。

## 固定白名单与原代码完整性

`docs/release/cocoon-code-allowlist.json` 是显式 101 文件列表，包含 backend.cjs、server.js、package.json、已跟踪 lib/*.js、唯一必需契约 JSON、唯一入口 HTML。不接受按目录通配复制，也不包含 config、_data、_staging、库 DB、缓存、日志、test、docs、git、照片、凭据、node_modules、node.exe、ADB、FFmpeg 或其他二进制。

入口页实际是 `ui/index.html`。`server.js` 的 entryPage 读取该文件并注入 session token；这是现有后端的鉴权入口依赖。页面 CSS/JS 内联，无额外本地静态素材需捆绑。包含该页面不代表浏览器 UI 已获发布许可或完成界面验收。`contracts/fixtures/cocoon-beta-3.04-candidate-profile-v1.json` 是 beta304ReadOnlyAdapter 顶层 require 的必需静态代码契约，描述 schema、明确 containsUserRows=false/NOT_DEVICE_ATTESTED，并不包含游戏库或证明真实设备兼容。

Pack 要求源目录恰为 Git 根、HEAD 等于显式 ExpectedCommit、工作树干净，全部输入已跟踪。Git 只读检查禁用 optional locks，不刷新原索引；所有代码只读取。文件和祖先拒绝符号链接/junction，文件拒绝 nlink !=1 的硬链接，禁止特殊文件。每文件 2MiB、总计 16MiB；复制前后检查源长度、mtime、inode、内容和提交/dirty 状态。拒绝个人绝对路径、私钥/常见 token 和明文凭据。唯一允许的盘符示例是 **ui/index.html:2246 的精确 `C:\\...\\prod.keys` 非个人占位符**，由主任务明确审阅批准；相同字符串放在其他源码仍拒绝，真实用户名和本机路径没有豁免。原网页不重写。

字面 require 必须指向白名单相对文件或 Node 内置模块；未知必需文件、外部包、计算 require、运行时动态 import 均要求重新审查，不静默漏装。当前两个 path.join worker 显式在白名单，并核对字面 worker 路径。JSDoc 的 import 类型注释及 nativeAssetService 的已知 import 方法名不会当成运行时依赖。此检查是针对现有 CommonJS 代码的保守规则，不是完整 JavaScript 静态解析器；未知加载方式不能据此宣称已审，变更 runtime loader 必须复核并扩展规则。

候选目录以 mkdir 独占新建，文件以 wx 写入，拒绝覆盖已有文件。失败后保留不完整 evidence 供检查，不对计算路径递归删除或继续补写。需要重试时使用新目录。后端运行数据另放独立 dataRoot；Smoke 不将新产生的数据混回候选。

## Manifest 与 Verify

根部 `cocoon-code-manifest.json` 包含版本化 contract、纯代码 sourceCommit、profile、权属未审标志，以及每文件相对路径/字节数/SHA-256。manifestSha256 是除自身字段以外的规范序列 JSON 摘要。没有包含源项目绝对位置、机器路径、姓名或时间戳。

Verify 不需要源项目，检查 manifest contract/字段/摘要、精确白名单、文件哈希和大小、未知额外文件/空目录、链接、代码隐私规则及必需依赖。传入 ExpectedCommit 和在独立交付记录保存的 ExpectedManifestSha256，可拒绝整体替换的候选。**内置 checksum 不是数字签名**；不提供外部摘要时只能证明包内自洽，不能证明来源真实性。正式 CI 应从受信提交中获取白名单并固定两项期望值。

已有 `tools/release-scan.ps1` 仍负责整包和历史隐私/人工审查，此脚本不取代它。将来的统一发布脚本须把根 manifest 与这 101 个 cocoon/ 路径纳入其最终 allowlist，并继续跑旧扫描。现有旧 allowlist 未修改；服务中的回环地址、品牌、契约来源和非个人示例仍需由总发布审查记录处理，不能把组件 Verify 当作公测许可结论。

## 实测与未完成门槛

冻结源 commit：`ec4cde82409d050ee7dd72e8e05ac9c898d572da`。源码候选 101 文件、821045 字节；manifestSha256：`9d20a4a31e4fe0af02095ed4cbcc4f230907aba5e5420c3cbc00f3ae5d95ee74`。

`evidence/cocoon-package-candidate` 通过独立 Verify。用候选中的源码、全新空数据根启动，8 项真实 HTTP/进程检查通过：无鉴权 games=401、入口页注入 token、鉴权 games=[]、持久库身份 null、脱敏 diagnostics、原生编辑器空库返回 EDITOR_LIBRARY_REQUIRED/409、library 状态健康、stdin 干净退出。随后再次验证候选哈希不变；原 Node 工作树仍干净；连续两次完整源捕获前后原 Git index SHA 相同。日志 `evidence/cocoon-package-verify.json`、`evidence/cocoon-package-smoke.json`、`evidence/cocoon-package-source-index.txt`、`evidence/cocoon-package-tests.txt`。17 项专项自动测试全部通过，覆盖冻结提交、dirty、覆盖拒绝、未知依赖、隐私、HTTPS误报、精确示例豁免、硬链接/junction、哈希篡改、缺文件/额外文件目录和 manifest 外部绑定。

尚未完成：101 文件和原页面/契约的最终版权与 LICENSE 决策、Node 运行时分发策略、第三方依赖/ADB/FFmpeg 审查、ThorCapture.exe 与此目录的最终统一发布接线、干净机器 portable C# 启动、Windows CI 实际执行及公开测试版的前端/实机验收。此轮只在已有 Node 上证明候选后端 API 可启动，不宣称安装包、浏览器 UI或游戏/设备功能已验收。
