# ShiroBot MemePlugin

独立的表情包插件：本地表情库、群聊自动采集（“盗图”）、QQ 商城表情入库、
按关键词/情绪检索，并通过共享契约向 AI 聊天插件提供 `send_meme` 能力。

## 能力

- 本地图片：PNG / JPEG / GIF / WebP，原始字节保存，不重编码，保留动图。
- 群聊采集：默认只收表情图（`SubType=sticker`、GIF/WebP）和 `QIncomingMarketFace`；
  可用 `#meme save` 手动收录被引用的图片；`#meme off` 按群关闭。
- 去重：SHA-256 精确去重 + dHash 视觉指纹（可选）+ 商城表情 EmojiId/PackageId；索引原子写；容量上限和 LRU 清理。
- 检索：标签、描述、情绪（happy/sad/angry/confused/shy/daily），支持关键词。
- 发送：通用 `ImageSegment`；QQ 适配器可用时尝试 `QOutgoingImage.SubType="sticker"`，
  失败自动回退普通图片。
- 共享服务：`ShiroBot.Meme.Contracts.IMemeService`，供 AI 聊天插件调用。
- 冷却：每会话冷却和每小时上限，避免刷屏。

## 构建

需要 .NET 10 SDK。SDK 版本与目标宿主一致；本项目当前引用 `ShiroBot.SDK 1.0.1`，
宿主 1.0.2 兼容。

```bash
dotnet publish ShiroBot.MemePlugin.csproj -c Release
```

输出目录：`bin/Release/net10.0/publish/`

```text
ShiroBot.MemePlugin.dll
ShiroBot.Meme.Contracts.dll
```

> `ShiroBot.Meme.Contracts` 是共享契约程序集，必须和插件 DLL 放在同一个插件目录。
> 宿主的 `SharedAssemblies` 机制会把它加载到 Default ALC，跨插件服务才能成立。

## 安装

把两个 DLL 放到：

```text
ShiroBot/plugins/MemePlugin/
  ShiroBot.MemePlugin.dll
  ShiroBot.Meme.Contracts.dll
```

如果同时使用 AI 聊天插件，`plugins/AiChatPlugin/` 也需要同一个
`ShiroBot.Meme.Contracts.dll`。**请复制同一个文件**，不要分别从不同构建取契约 DLL；
宿主会校验共享程序集的 MVID，版本不一致会要求重启或拒绝加载。

## 配置

首次加载会生成 `plugins/MemePlugin/config.toml`：

```toml
[meme]
enabled = true
auto_collect = "sticker"   # off / sticker / all
collect_groups = []        # 空表示所有群；否则只采集这些群 ID
collect_direct = false
max_bytes = 5000000
perceptual_threshold = 5       # dHash 汉明距离；0 关闭视觉去重
perceptual_color_threshold = 24 # 平均色每通道容差，避免纯色图误判
max_items = 5000
max_total_bytes = 500000000
cooldown_seconds = 45
max_per_hour = 10
send_with_mention = true
send_with_quote = false
send_mode = "auto"         # auto / sticker / image
caption_max_chars = 120
download_timeout_seconds = 15
library_dir = "memes"
library_token = ""         # 可选：管理页/图片接口令牌；留空不校验
ai_tagging_enabled = false # 启用多模态 AI 标注
ai_base_url = ""           # OpenAI 兼容接口，例如 http://10.157.0.10:8888/v1
ai_api_key = ""            # 本地免鉴权可留空
ai_model = ""              # 支持图片输入的模型名
ai_timeout_seconds = 30
ai_max_tags = 8
ai_infer_emotions = true
ai_prompt = ""

[network]
http_proxy = ""
```

配置热更新会立即更新检索、发送、冷却和容量限制；`library_dir`、网络代理等初始化参数
建议重启宿主后生效。

视觉去重使用 `StbImageSharp`（Unlicense OR MIT）解码图片，计算 64 位 dHash，并用平均色
做二次校验；只支持 StbImageSharp 能解码的格式（JPEG/PNG/GIF/BMP 等），WebP 会退回精确
SHA-256 去重。`perceptual_threshold=0` 可关闭视觉去重，只保留精确去重。

## 命令

```text
#meme                    查看帮助
#meme list [关键词]      查看数量
#meme random [关键词]    随机发送一张
#meme save               引用图片/商城表情后收录
#meme add <url> [标签]   管理员添加外部图片
#meme tag <ID> <标签>    管理员修改标签（ID 可用列表中的 8 位前缀）
#meme del <ID>           管理员删除（ID 可用前缀）
#meme off / on           管理员开关本群自动采集
#meme stats              查看容量
#meme retag [all]        管理员：AI 重新标注；all 标注全部
```

插件配置弹窗里新增“表情库”标签，可直接查看已保存的表情；该页面地址为
`/plugin/MemePlugin/library`，图片接口为 `/plugin/MemePlugin/library/image?id=<ID>`。
如果设置了 `library_token`，管理页和图片接口都需要带 `token` 查询参数。

启用 `ai_tagging_enabled` 并配置接口/模型后，新采集的图片会由多模态模型生成描述、标签和情绪；
AI 失败或超时自动回退到本地关键词规则。已有表情可用 `#meme retag` 重新标注空泛标签，
`#meme retag all` 重新标注全部。

## 与 AI 聊天插件配合

AI 聊天插件通过共享契约获取 `IMemeService`，并在轻松聊天时向模型提供 `send_meme`
工具。工具只负责“选图”，最终由 AI 回复管线调用 `IMemeService.SendAsync` 发送，
避免工具副作用和空回复。

AI 聊天配置中：

```toml
[tool_use.meme]
enabled = true
allow_proactive = false
allow_direct = true
```

如果 MemePlugin 未安装/未启用，AI 聊天插件会自动跳过该工具。

注意：AI 聊天的 `behaviour.reply_mode` 默认是 `answer`，该模式不会提供表情工具；
要启用 AI 发表情，请改为 `chat` 或 `auto`。`#meme` 命令不受此限制。

## OneBot / Milky 适配器兼容

- 入站：优先识别 QQ 适配器的 `QIncomingMarketFace` / `QIncomingImage`；普通适配器只提供
  `ImageSegment` 时，`auto_collect="all"` 或 `#meme save` 仍可收录。
- 出站：优先走通用 `ImageSegment`（`base64://`），OneBot/Milky 一般都能转发；
  QQ 适配器可用时再尝试 `QOutgoingImage.SubType="sticker"`。
- 部分适配器不填 `QIncomingImage.SubType`，插件会先下载再按 GIF/WebP MIME 判断是否为表情；
  这也意味着 `auto_collect="sticker"` 会对未知类型图片做一次受大小限制的下载。

## QQ 商城表情说明

当前 `ShiroBot.Model.QQ` 只有入站 `QIncomingMarketFace`，没有出站
`QOutgoingMarketFace`。因此：

- 收到商城表情时，下载 `Url` 并保存 `EmojiId`、`EmojiPackageId`、`Key`、`Summary`。
- 发送时优先尝试 `QOutgoingImage { SubType = "sticker" }`；OneBot/Milky 适配器是否
  真正显示为表情取决于适配器实现，失败会回退普通图片。
- 若要**原生**发送商城表情，需要宿主/适配器增加出站 `mface` 契约；仅靠插件无法完成。

## 安全与隐私

- 自动采集默认不收私聊、只收表情类图片；群内可用 `#meme off` 关闭。
- 本插件用观察者路由，不阻断 AI 聊天。若 AI 聊天被配置为更高优先级并在回复后
  `MarkHandled`，Meme 的自动采集可能收不到该消息；可把 MemePlugin 调到同一优先级。
- 外部 URL 仅允许公网 http(s)，校验 DNS/IP，限制大小和内容类型；`#meme add` 仅管理员可用。
- 不记录图片字节到日志；索引只保存元数据。
- 请只在你有权使用的群/内容上开启自动采集，并尊重版权和用户隐私。

## 验证

```bash
dotnet build Tests/Verification/Verification.csproj -c Debug
dotnet Tests/Verification/bin/Debug/net10.0/ShiroBot.MemePlugin.Verification.dll
```

覆盖：存储/去重/检索/标签/损坏索引、采集过滤、插件生命周期、服务注册、发送段和冷却。

## 发布注意

- `ShiroBot.MemePlugin.dll` 与 `ShiroBot.Meme.Contracts.dll` 必须一起发布。
- AI 聊天插件的 Release 工作流会 checkout 本仓库的契约项目；如果仓库名/所有者不同，
  请修改 `.github/workflows/release.yml` 中的 `repository` 和 `MemeContractsProject`。
- 两个插件发布时应使用**同一次构建**的契约 DLL，避免 MVID 不一致。

## 许可证

[GPL-3.0-only](LICENSE)
