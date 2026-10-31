using ShiroBot.SDK.Config;

namespace ShiroBot.MemePlugin.Config;

[ConfigModel]
public sealed class MemeConfig
{
    [ConfigField("表情库开关、自动采集、发送冷却和容量限制。", Label = "表情库")]
    public MemeSection Meme { get; set; } = new();

    [ConfigField("下载 QQ 图片/商城表情时使用的代理；留空直连。", Label = "网络")]
    public NetworkSection Network { get; set; } = new();
}

public sealed class MemeSection
{
    [ConfigField("关闭后不采集、不发送，也不注册表情服务。", Label = "启用表情库")]
    public bool Enabled { get; set; } = true;

    /// <summary>off：不自动采集；sticker：只收表情/商城表情；all：所有图片。</summary>
    [ConfigField("自动采集范围：off 关闭；sticker 只收表情图/商城表情；all 收集所有图片。", Label = "自动采集", Options = new[] { "off", "sticker", "all" })]
    public string AutoCollect { get; set; } = "sticker";

    /// <summary>为空表示所有群；否则只采集这些群 ID。</summary>
    [ConfigField("允许自动采集的群 ID；留空表示所有群。群内可用 #meme off 单独关闭。", Label = "采集群白名单")]
    public List<string> CollectGroups { get; set; } = [];

    [ConfigField("是否自动采集私聊图片；默认关闭。", Label = "采集私聊")]
    public bool CollectDirect { get; set; }

    [ConfigField("单个表情文件的最大字节数。", Label = "单文件上限（字节）", Min = 1024, Max = 20_000_000)]
    public long MaxBytes { get; set; } = 5_000_000;

    /// <summary>感知哈希汉明距离阈值；0 关闭视觉去重。越小越严格。</summary>
    [ConfigField("视觉去重阈值：感知哈希汉明距离，0 关闭；默认 5，越小越严格。", Label = "视觉去重阈值", Min = 0, Max = 16)]
    public int PerceptualThreshold { get; set; } = 5;

    [ConfigField("视觉去重颜色容差：平均 RGB 每通道最大差值；默认 24。", Label = "视觉颜色容差", Min = 0, Max = 255)]
    public int PerceptualColorThreshold { get; set; } = 24;

    [ConfigField("表情库最多保留多少张；超出后清理最久未使用的。", Label = "最大数量", Min = 1, Max = 100_000)]
    public int MaxItems { get; set; } = 5000;

    [ConfigField("表情库总字节上限；超出后清理最久未使用的。", Label = "总容量上限（字节）", Min = 1_048_576)]
    public long MaxTotalBytes { get; set; } = 500_000_000;

    [ConfigField("同一会话两次发送之间的最短秒数；0 表示不限制。", Label = "发送冷却（秒）", Min = 0, Max = 3600)]
    public int CooldownSeconds { get; set; } = 45;

    [ConfigField("同一会话每小时最多发送多少张；0 表示不限制。", Label = "每小时上限", Min = 0, Max = 1000)]
    public int MaxPerHour { get; set; } = 10;

    [ConfigField("发送表情时是否 @ 触发消息的发送者。", Label = "群聊 @ 发送者")]
    public bool SendWithMention { get; set; } = true;

    [ConfigField("发送表情时是否引用触发消息。", Label = "引用触发消息")]
    public bool SendWithQuote { get; set; }

    /// <summary>auto：QQ 尝试 sticker 图片；sticker：强制 sticker；image：普通图片。</summary>
    [ConfigField("QQ 发送方式：auto 自动；sticker 表情图；image 普通图片。", Label = "发送方式", Options = new[] { "auto", "sticker", "image" })]
    public string SendMode { get; set; } = "auto";

    [ConfigField("自动生成的表情描述最大字符数。", Label = "描述长度", Min = 20, Max = 500)]
    public int CaptionMaxChars { get; set; } = 120;

    [ConfigField("下载超时秒数。", Label = "下载超时（秒）", Min = 3, Max = 120)]
    public int DownloadTimeoutSeconds { get; set; } = 15;

    [ConfigField("表情文件存放目录，相对插件目录。", Label = "存储目录")]
    public string LibraryDir { get; set; } = "memes";
}

public sealed class NetworkSection
{
    [ConfigField("HTTP 代理地址，例如 http://127.0.0.1:7890；留空直连。", Label = "HTTP 代理")]
    public string HttpProxy { get; set; } = "";
}
