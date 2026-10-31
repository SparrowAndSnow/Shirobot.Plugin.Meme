using ShiroBot.Meme.Contracts;

namespace ShiroBot.MemePlugin.Meme;

internal sealed record MemeIndex
{
    public int Version { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<MemeItem> Items { get; init; } = [];
}

internal sealed record MemeItem
{
    public string Id { get; init; } = "";
    public string Hash { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Mime { get; init; } = "image/png";
    public string Caption { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public List<string> Emotions { get; init; } = [];
    public MemeSource Source { get; init; } = new();
    public MarketFaceInfo? MarketFace { get; init; }
    /// <summary>64 位 dHash；旧索引没有该字段时为 null，加载后会补算。</summary>
    public ulong? PerceptualHash { get; init; }
    /// <summary>平均 RGB（0xRRGGBB），用于避免纯色图 dHash 全 0 时误判。</summary>
    public int? AverageColor { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUsedAt { get; init; }
    public int UseCount { get; init; }
}

internal sealed record MemeSource
{
    public string Platform { get; init; } = "";
    public string? InstanceId { get; init; }
    public string ChannelId { get; init; } = "";
    public string? SenderId { get; init; }
    public string? MessageId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record GroupSettings
{
    public bool Enabled { get; init; } = true;
    public string? AutoCollect { get; init; }
}

internal sealed record GroupSettingsEntry
{
    public string Key { get; init; } = "";
    public GroupSettings Settings { get; init; } = new();
}

internal sealed record MemeIngestJob
{
    public required string Platform { get; init; }
    public string? InstanceId { get; init; }
    public required string ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? MessageId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? Url { get; init; }
    public string? Summary { get; init; }
    public string? SubType { get; init; }
    public string MessageText { get; init; } = "";
    public MarketFaceInfo? MarketFace { get; init; }
    public bool Force { get; init; }
    public bool StickerOnly { get; init; }
}
