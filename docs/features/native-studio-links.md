# 素材场景联动与位置重置

2026-10-11。LogoPreviewAsync 新增重载，在原参数后传 logoLayout、useStaged、cancellation；旧签名保持候选模式。logoLayout 为 NativeArtworkLayout，可指定 Logo 自身裁剪/焦点/留边；坐标以实际选中的图像为准。

- useStaged=false：预览所选解码候选。
- useStaged=true：若对应 slot 有草稿，预览已保存变体；无草稿时使用候选。候选与草稿不一致会返回 EDITOR_SELECTION_STALE，重新载入选中状态。已有裁剪不应再次添加同一裁剪配方。
- 选择两种模式可展示候选/暂存像素对比。渲染时草稿 revision 改变返回冲突，不把迟到像素解释为当前状态。
- 尚未暂存的 variantToken 可通过 LogoPreviewVariantsAsync 直接作为场景来源，使用经过验证的临时输出字节，见下节。

LogoResetPreviewAsync(itemId, NativeLogoResetMode.Original, token) 审阅恢复库原始 Logo 字段；Automatic 审阅取消 logoPlacementOverridden。返回标准 metadataPreview，前端展示 changes 后再用 MetadataConfirmAsync 确认。空 changes 不需要确认。取消自定义只提交字段意图，不表示已验证设备默认位置。

通用元数据审阅现在比较“原始基线 + 已暂存字段”的当前值；before 会显示当前草稿值。确认改回基线时删除对应覆盖，保留其他字段；没有剩余覆盖时移除空 metadata 草稿条目。revision 与原始库基线仍复核，不覆盖并发修改。

场景预览仍标注 EDITOR_APPROXIMATION，组合 PNG 不可作为 Hero 素材保存。候选/草稿来源验证后读取实际 CAS 字节，预览继续验证原始 Hero 与 Logo 两份候选归属。

Node 436/436；C# 原生 254/254，含实际 HTTP 裁剪场景和重置查询。本批仅后端和接入合同，界面归 Claude；没有替换用户 EXE 或操作设备。

## 临时裁剪/布局直接联动

~~~csharp
var scene = await client.LogoPreviewVariantsAsync(itemId, heroId, logoId,
    NativeArtworkScreen.Lower, placement, heroVariantToken, logoVariantToken, cancellation);
~~~

两个 token 至少提供一个，另一项可为 null（使用候选）。token 来自同一游戏/slot/候选的 CropAsync 或 LayoutAsync；此方法不同时应用 useStaged 或额外布局，避免重复裁剪。输入经过实际 SHA256 与有效期验证，渲染前再次复核；EDITOR_VARIANT_STALE 时重新请求当前所选素材预览。切换候选/库应取消旧请求并丢弃旧 token。

返回形状与 LogoPreviewAsync 相同，使用 blobSha256 和 Hero 的 PreviewAsync 取组合像素；可按之前的 metadata 审阅流程保存位置。组合本身不暂存素材，若用户确认裁剪，应分别以原 token 调用 StageAsync 并使用最新 revision。整个过程不自动创建永久变体，token 不会因组合而被消耗。Hero 在屏幕按 cover、Logo 在包围框按 contain 显示，仍是编辑器近似布局。

本批 Node 全量 446/446、C# 原生专项 286/286；以前的 436/254 是上一联动批历史结果。前端接入、Cocoon 遮罩/安全区和实机验收尚未完成。
