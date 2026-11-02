using ShiroBot.SDK.Plugin;

namespace ShiroBot.MemePlugin.Contracts;

/// <summary>供其他插件选择、发送表情和查询容量。调用方只传表情 ID，不接触本地文件路径。</summary>
[PluginContract]
public static partial class MemeLibrary
{
    [PluginService("meme.select")]
    public static partial PluginService<MemeSelectRequest, MemeSelectResponse> Select { get; }

    [PluginService("meme.send")]
    public static partial PluginService<MemeSendRequest, MemeSendResponse> Send { get; }

    [PluginService("meme.stats")]
    public static partial PluginService<MemeStats> Stats { get; }
}

/// <summary>关键词为空时随机选择；ChannelId 用于检查同一会话的发送冷却。</summary>
public sealed record MemeSelectRequest(string Query = "", string? Emotion = null, string? ChannelId = null, int Limit = 8);

/// <summary>Meme 为空表示关闭、冷却或暂无可用表情；服务本身不存在时 TryCallAsync 返回 null。</summary>
public sealed record MemeSelectResponse(MemeEntry? Meme);

/// <summary>仅暴露可显示的元数据，不包含磁盘路径或图片字节。</summary>
public sealed record MemeEntry(string Id, string Caption, IReadOnlyList<string> Tags, string Mime);

/// <summary>使用 Select 返回的完整 ID，由 MemePlugin 查找并发送库内图片。</summary>
public sealed record MemeSendRequest(string Id, MemeDestination Target, string? Caption = null);

/// <summary>ChannelType 为 Group 或 Direct；InstanceId 指定适配器实例，留空使用当前默认实例。</summary>
public sealed record MemeDestination(string Platform, string ChannelType, string ChannelId,
    string? InstanceId = null, string? MessageId = null, string? SenderId = null, bool Mention = false, bool Quote = false);

public sealed record MemeSendResponse(bool Ok, string? MessageId = null, string? Error = null);

public sealed record MemeStats(bool Enabled, int Count, long Bytes, DateTimeOffset? LastUpdated);
