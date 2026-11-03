using ShiroBot.SDK.Config;

namespace ShiroBot.MemePlugin.Config;

[ConfigModel]
public sealed class MemeConfig
{
    [ConfigField("表情库开关、自动采集、发送冷却和容量限制。", Label = "表情库设置", GroupOrder = 0)]
    public MemeSection Meme { get; set; } = new();

    [ConfigField("下载 QQ 图片/商城表情时使用的代理；留空直连。", Label = "网络", GroupOrder = 60)]
    public NetworkSection Network { get; set; } = new();
}

public sealed class MemeSection
{
    [ConfigField("关闭后不采集、不发送，也不注册表情服务。", Label = "启用表情库", Group = "basic", GroupLabel = "基础", GroupOrder = 10, GroupDescription = "表情库开关、自动采集范围和采集群。")]
    public bool Enabled { get; set; } = true;

    /// <summary>off：不自动采集；sticker：只收表情/商城表情；all：所有图片。</summary>
    [ConfigField("自动采集范围：off 关闭；sticker 只收表情图/商城表情；all 收集所有图片。", Label = "自动采集", Options = new[] { "off", "sticker", "all" }, Group = "basic", GroupLabel = "基础", GroupOrder = 10)]
    public string AutoCollect { get; set; } = "sticker";

    /// <summary>为空表示所有群；否则只采集这些群 ID。</summary>
    [ConfigField("允许自动采集的群 ID；留空表示所有群。群内可用 #meme off 单独关闭。", Label = "采集群白名单", Group = "basic", GroupLabel = "基础", GroupOrder = 10)]
    public List<string> CollectGroups { get; set; } = [];

    [ConfigField("是否自动采集私聊图片；默认关闭。", Label = "采集私聊", Group = "basic", GroupLabel = "基础", GroupOrder = 10)]
    public bool CollectDirect { get; set; }

    [ConfigField("单个表情文件的最大字节数。", Label = "单文件上限（字节）", Min = 1024, Max = 20_000_000, Group = "storage", GroupLabel = "存储与下载", GroupOrder = 20, GroupDescription = "容量、存储目录、管理页令牌和下载超时。")]
    public long MaxBytes { get; set; } = 5_000_000;

    /// <summary>感知哈希汉明距离阈值；0 关闭视觉去重。越小越严格。</summary>
    [ConfigField("视觉去重阈值：感知哈希汉明距离，0 关闭；默认 5，越小越严格。", Label = "视觉去重阈值", Min = 0, Max = 16, Group = "dedupe", GroupLabel = "去重", GroupOrder = 30, GroupDescription = "感知哈希和颜色容差；用于过滤重复图片。")]
    public int PerceptualThreshold { get; set; } = 5;

    [ConfigField("视觉去重颜色容差：平均 RGB 每通道最大差值；默认 24。", Label = "视觉颜色容差", Min = 0, Max = 255, Group = "dedupe", GroupLabel = "去重", GroupOrder = 30)]
    public int PerceptualColorThreshold { get; set; } = 24;

    [ConfigField("表情库最多保留多少张；超出后清理最久未使用的。", Label = "最大数量", Min = 1, Max = 100_000, Group = "storage", GroupLabel = "存储与下载", GroupOrder = 20)]
    public int MaxItems { get; set; } = 5000;

    [ConfigField("表情库总字节上限；超出后清理最久未使用的。", Label = "总容量上限（字节）", Min = 1_048_576, Group = "storage", GroupLabel = "存储与下载", GroupOrder = 20)]
    public long MaxTotalBytes { get; set; } = 500_000_000;

    [ConfigField("同一会话两次发送之间的最短秒数；0 表示不限制。", Label = "发送冷却（秒）", Min = 0, Max = 3600, Group = "send", GroupLabel = "发送", GroupOrder = 40, GroupDescription = "发送频率、@/引用和发送方式。")]
    public int CooldownSeconds { get; set; } = 45;

    [ConfigField("同一会话每小时最多发送多少张；0 表示不限制。", Label = "每小时上限", Min = 0, Max = 1000, Group = "send", GroupLabel = "发送", GroupOrder = 40)]
    public int MaxPerHour { get; set; } = 10;

    [ConfigField("发送表情时是否 @ 触发消息的发送者。", Label = "群聊 @ 发送者", Group = "send", GroupLabel = "发送", GroupOrder = 40)]
    public bool SendWithMention { get; set; } = true;

    [ConfigField("发送表情时是否引用触发消息。", Label = "引用触发消息", Group = "send", GroupLabel = "发送", GroupOrder = 40)]
    public bool SendWithQuote { get; set; }

    /// <summary>auto：QQ 尝试 sticker 图片；sticker：强制 sticker；image：普通图片。</summary>
    [ConfigField("QQ 发送方式：auto 自动；sticker 表情图；image 普通图片。", Label = "发送方式", Options = new[] { "auto", "sticker", "image" }, Group = "send", GroupLabel = "发送", GroupOrder = 40)]
    public string SendMode { get; set; } = "auto";

    [ConfigField("自动生成的表情描述最大字符数。", Label = "描述长度", Min = 20, Max = 500, Group = "send", GroupLabel = "发送", GroupOrder = 40)]
    public int CaptionMaxChars { get; set; } = 120;

    [ConfigField("下载超时秒数。", Label = "下载超时（秒）", Min = 3, Max = 120, Group = "storage", GroupLabel = "存储与下载", GroupOrder = 20)]
    public int DownloadTimeoutSeconds { get; set; } = 15;

    [ConfigField("表情文件存放目录，相对插件目录。", Label = "存储目录", Group = "storage", GroupLabel = "存储与下载", GroupOrder = 20)]
    public string LibraryDir { get; set; } = "memes";

    [ConfigField("向 AI 聊天等插件提供 send_meme 工具（需要宿主 1.0.3）；关闭后它们不会再发表情。", Label = "提供 AI 表情工具", Group = "chat_tool", GroupLabel = "AI 聊天工具", GroupOrder = 45, GroupDescription = "给 AI 聊天插件使用的表情工具。")]
    public bool AiToolEnabled { get; set; } = true;

    [ConfigField("explicit：只在用户明确要求发表情时使用；auto：轻松聊天时主动配表情。", Label = "使用倾向", Options = new[] { "explicit", "auto" }, Group = "chat_tool", GroupLabel = "AI 聊天工具", GroupOrder = 45)]
    public string AiToolPolicy { get; set; } = "explicit";

    [ConfigField("启用 AI 视觉标注：让多模态模型识别表情内容，生成描述、标签和情绪；失败时回退本地规则。", Label = "AI 标注", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50, GroupDescription = "视觉模型接口、标签和表情过滤。")]
    public bool AiTaggingEnabled { get; set; }

    [ConfigField("模型来源：auto 填了接口地址就用自己的接口，否则借用 AI 聊天插件的模型；aichat 只借用 AI 聊天插件的模型；custom 只用自己的接口。", Label = "模型来源", Options = new[] { "auto", "aichat", "custom" }, Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public string AiProvider { get; set; } = "auto";

    [ConfigField("自己的 OpenAI 兼容视觉接口，例如 http://10.157.0.10:8888/v1；借用 AI 聊天插件的模型时留空。", Label = "AI 接口地址", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public string AiBaseUrl { get; set; } = "";

    [ConfigField("AI API Key；本地免鉴权服务可留空。", Label = "AI API Key", Type = "password", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public string AiApiKey { get; set; } = "";

    [ConfigField("支持图片输入的模型名。用自己的接口时必填；借用 AI 聊天插件时可留空，由它选一个识图模型。", Label = "AI 模型", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public string AiModel { get; set; } = "";

    [ConfigField("AI 标注请求超时秒数。", Label = "AI 超时（秒）", Min = 5, Max = 120, Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public int AiTimeoutSeconds { get; set; } = 30;

    [ConfigField("AI 最多生成多少个标签。", Label = "AI 标签数", Min = 1, Max = 16, Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public int AiMaxTags { get; set; } = 8;

    [ConfigField("是否让 AI 同时判定情绪（happy/sad/angry/confused/shy/daily）。", Label = "AI 判定情绪", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public bool AiInferEmotions { get; set; } = true;

    [ConfigField("AI 附加提示词；留空使用默认。", Label = "AI 附加提示", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public string AiPrompt { get; set; } = "";

    [ConfigField("只收录 AI 判定为表情包的内容；截图、照片和其他图片会跳过（仅自动采集，手动 add/save 不受影响）。", Label = "只收录表情包", Group = "ai", GroupLabel = "AI 标注", GroupOrder = 50)]
    public bool AiFilterNonMeme { get; set; } = true;
}

public sealed class NetworkSection
{
    [ConfigField("HTTP 代理地址，例如 http://127.0.0.1:7890；留空直连。", Label = "HTTP 代理")]
    public string HttpProxy { get; set; } = "";
}
