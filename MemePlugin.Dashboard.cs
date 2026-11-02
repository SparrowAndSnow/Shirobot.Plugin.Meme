using ShiroBot.MemePlugin.Meme;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.MemePlugin;

public sealed partial class MemePlugin
{
    public IReadOnlyList<DashboardPage> Pages { get; } =
    [
        new("library", "表情库", DashboardPageKinds.Gallery, Icon: "photo_library",
            Description: "查看和管理已保存的表情，支持按描述、标签或情绪筛选。",
            Filters:
            [
                new("q", "搜索"), new("tag", "标签"),
                new("emotion", "情绪", "select",
                [
                    new("happy", "开心"), new("sad", "难过"), new("angry", "生气"),
                    new("confused", "困惑"), new("shy", "害羞"), new("daily", "日常")
                ])
            ],
            Actions: [new("delete", "删除", "确定删除这个表情？", "danger")])
    ];

    public Task<DashboardPageData> QueryAsync(string pageId, DashboardQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = Library(pageId);
        var q = query.Filter("q")?.Trim() ?? "";
        var tag = query.Filter("tag")?.Trim() ?? "";
        var emotion = query.Filter("emotion")?.Trim() ?? "";
        var matching = store.GetItems()
            .Where(item => q.Length == 0 || item.Caption.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.Tags.Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                item.Emotions.Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Where(item => tag.Length == 0 || item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Where(item => emotion.Length == 0 || item.Emotions.Contains(emotion, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt).ToArray();
        var size = Math.Clamp(query.PageSize <= 0 ? 24 : query.PageSize, 1, 100);
        var offset = (long)(Math.Max(1, query.Page) - 1) * size;
        var items = matching.Skip((int)Math.Min(offset, matching.Length)).Take(size).Select(ToDashboardItem).ToArray();
        return Task.FromResult(new DashboardPageData(items, matching.Length, offset + items.Length < matching.Length));
    }

    public Task<DashboardItem?> GetItemAsync(string pageId, string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = Library(pageId).GetItems().FirstOrDefault(item => item.Id == itemId);
        return Task.FromResult(item is null ? null : ToDashboardItem(item));
    }

    public async Task<PluginActionResult> RunActionAsync(string pageId, string actionId, string itemId, CancellationToken cancellationToken = default)
    {
        var store = Library(pageId);
        // Re-check the ID after the host's check: the entry may have changed in the meantime.
        if (actionId != "delete" || !store.GetItems().Any(item => item.Id == itemId))
            return new PluginActionResult(false, "未找到表情或操作。", Refresh: true);
        var removed = await store.RemoveAsync(itemId, cancellationToken).ConfigureAwait(false);
        return new PluginActionResult(removed, removed ? "已删除。" : "未找到该表情。", Refresh: true);
    }

    public Task<DashboardFile?> OpenFileAsync(string pageId, string fileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = Library(pageId).GetById(fileId);
        return Task.FromResult(item is not null && item.Id == fileId && File.Exists(item.FilePath)
            ? DashboardFile.FromPath(item.FilePath, item.Mime) : null);
    }

    private MemeStore Library(string pageId) => pageId == "library"
        ? _store ?? throw new InvalidOperationException("表情库未加载。")
        : throw new ArgumentException("未找到页面。", nameof(pageId));

    private static DashboardItem ToDashboardItem(MemeItem item) => new(item.Id,
        string.IsNullOrWhiteSpace(item.Caption) ? "表情" : item.Caption,
        Description: string.Join(" · ", item.Emotions), ImageFileId: item.Id, Tags: item.Tags.ToArray(),
        Meta: $"使用 {item.UseCount} 次 · {item.Bytes / 1024d:F1} KB",
        Actions: ["delete"]);
}
