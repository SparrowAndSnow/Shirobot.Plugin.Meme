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
- AI 聊天工具：通过 AI 聊天插件的契约提供 `send_meme` 工具（只选图）和同名的回复后动作（在 AI 聊天发出文字后发图）。
  运行时只交换 JSON，两个插件都可以单独卸载和更新。
- 冷却：每会话冷却和每小时上限，避免刷屏。

## 构建

需要 .NET 10 SDK。本项目引用 `ShiroBot.SDK 1.0.3`，需要宿主 1.0.3 及以上。

```bash
dotnet publish ShiroBot.MemePlugin.csproj -c Release
```

输出目录：`bin/Release/net10.0/publish/`

```text
ShiroBot.MemePlugin.dll
```

### AI 聊天插件的契约

`contracts/ShiroBot.AiChatPlugin.dll` 是 AI 聊天插件的 DLL，只在构建时读取它的契约（`ChatTools` 等类型），
不会被引用，也不会进入本插件的成品。AI 聊天插件的契约变化时，换成新版 DLL 重新构建即可。

## 安装

把 DLL 放到 `ShiroBot/plugins/MemePlugin/`。和 AI 聊天插件一起使用时不需要复制任何其他文件。

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
ai_tool_enabled = true     # 向 AI 聊天插件提供 send_meme 工具
ai_tool_policy = "explicit" # explicit：用户明确要求时才发；auto：轻松聊天时主动配图
ai_tagging_enabled = false # 启用多模态 AI 标注
ai_provider = "auto"       # auto：填了 ai_base_url 用自己的接口，否则借用 AI 聊天插件的模型；aichat / custom 固定其一
ai_base_url = ""           # 自己的 OpenAI 兼容接口，例如 http://10.157.0.10:8888/v1
ai_api_key = ""            # 本地免鉴权可留空
ai_model = ""              # 支持图片输入的模型名；借用 AI 聊天插件时可留空，由它选识图模型
ai_timeout_seconds = 30
ai_max_tags = 8
ai_infer_emotions = true
ai_prompt = ""
ai_filter_non_meme = true # 自动采集时只保留 AI 判定的表情包

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
#meme clean [confirm]    管理员：预览/删除 AI 判定为截图、照片的内容
```

在 Dashboard 的插件详情里打开独立的“页面”窗口，再选择“表情库”，可以搜索、按标签和情绪筛选、分页查看和删除表情。
此功能使用宿主 1.0.3 的 `IPluginDashboardProvider`，与配置表单分开；列表、操作和图片全部走宿主统一鉴权的 API。
插件不再注册 `/plugin/MemePlugin/library/...` 公开管理路由，旧 `library_token` 不再使用，也不再生成签名图片链接。

启用 `ai_tagging_enabled` 后，新采集的图片会由多模态模型生成描述、标签和情绪。模型可以借用 AI 聊天插件配置好的
（通过它的 `ChatModels` 契约，不需要再填接口和密钥），也可以填写自己的接口和模型；
AI 失败或超时自动回退到本地关键词规则。已有表情可用 `#meme retag` 重新标注空泛标签，
`#meme retag all` 重新标注全部；AI 会把图片分为 meme/screenshot/photo/other，
开启 `ai_filter_non_meme` 后自动采集只保留 meme。`#meme clean` 可预览并删除已有截图/照片。

## 与 AI 聊天插件配合

Meme 通过 AIChat 的 `ChatTools` 契约提供 `chat.tool.send_meme` 和 `chat.after_reply.send_meme`。
工具负责选图，AIChat 发完文字后调用回复后动作发图；运行时只交换 JSON，不共享服务对象。
Meme 的 AI 标注通过 `ChatModels` 契约借用 AIChat 的识图模型，与 Dashboard 页面接口互相独立。

这项工具由 Meme 的 `ai_tool_enabled` 和 `ai_tool_policy` 控制。AIChat 按 `ChatTools` 契约发现工具，
没有安装 Meme 时也可以正常聊天；无需配置已经移除的 `[tool_use.meme]` 专用桥接。

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

覆盖：存储/去重/检索/标签/损坏索引、采集过滤、插件生命周期、契约工具与回复后动作、发送段和冷却，以及独立 Dashboard 页面的筛选、分页、精确条目校验、图片和删除。

## 发布注意

- 只发布 `ShiroBot.MemePlugin.dll`；和 AI 聊天插件之间只约定服务名和 JSON 字段，可以各自独立发布。

## 许可证

[GPL-3.0-only](LICENSE)
