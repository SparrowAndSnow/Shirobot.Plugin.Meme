namespace ShiroBot.MemePlugin.Meme;

/// <summary>
/// 表情库内部的选择与发送接口。其他插件使用 Contracts.MemeLibrary，
/// 按服务名交换 JSON，不共享这里的接口、文件路径或服务对象。
/// </summary>
public interface IMemeService
{
    /// <summary>按关键词/情绪挑选一张表情；受每会话冷却和每小时上限限制。没有合适结果时返回 null。</summary>
    Task<MemeSelection?> SelectAsync(MemeQuery query, CancellationToken cancellationToken = default);

    /// <summary>发送已经选中的表情。成功后才计入冷却和使用次数。</summary>
    Task<MemeSendResult> SendAsync(
        MemeSelection selection,
        MemeSendTarget target,
        string? caption = null,
        CancellationToken cancellationToken = default);

    Task<MemeLibraryStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>表情检索条件。Query 可为空，表示只按情绪/随机挑选。</summary>
public sealed record MemeQuery(
    string Query,
    string? Emotion = null,
    string? ChannelId = null,
    string? UserId = null,
    int Limit = 8);

/// <summary>选中的表情。FilePath 是插件库中的绝对路径；发送由 IMemeService 负责。</summary>
public sealed record MemeSelection(
    string Id,
    string Caption,
    IReadOnlyList<string> Tags,
    string FilePath,
    string Mime,
    MarketFaceInfo? MarketFace = null);

/// <summary>发送目标使用平台无关的原语，避免契约程序集依赖宿主 SDK 的具体模型。</summary>
public sealed record MemeSendTarget(
    string Platform,
    string? InstanceId,
    string ChannelType,
    string ChannelId,
    string? MessageId,
    string? SenderId,
    bool IsGroup,
    bool Mention,
    bool Quote);

public sealed record MemeSendResult(bool Success, string? MessageId = null, string? Error = null);

public sealed record MemeLibraryStats(int Count, long Bytes, DateTimeOffset? LastUpdated);

/// <summary>QQ 商城表情元数据。当前宿主契约没有出站 mface 类型，发送时通常降级为图片。</summary>
public sealed record MarketFaceInfo(
    string EmojiId,
    int EmojiPackageId,
    string? Key = null,
    string? Summary = null,
    string? Url = null);

/// <summary>send_meme 工具的参数，[Description] 会成为模型看到的参数说明。</summary>
[global::ShiroBot.SDK.Plugin.PluginJson]
public sealed record SendMemeToolArgs(
    [property: global::System.ComponentModel.Description("Short Chinese keyword(s) describing the desired meme, e.g. 猫, 无语, 大笑, 摸鱼.")] string? Query = null,
    [property: global::System.ComponentModel.Description("Optional emotion category: happy, sad, angry, confused, shy or daily.")] string? Emotion = null,
    [property: global::System.ComponentModel.Description("Optional short caption, used only when you write no reply text.")] string? Caption = null);

/// <summary>选中的表情，交给回复后动作发送。</summary>
[global::ShiroBot.SDK.Plugin.PluginJson]
public sealed record SendMemeArgs(string Id, string? Caption = null);

