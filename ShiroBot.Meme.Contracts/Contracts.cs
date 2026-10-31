namespace ShiroBot.Meme.Contracts;

/// <summary>
/// 表情包库服务。由 MemePlugin 注册到宿主插件服务表，供其他插件（例如 AI 聊天）调用。
/// 契约程序集必须作为共享程序集随插件发布，确保所有插件看到同一个接口类型。
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
