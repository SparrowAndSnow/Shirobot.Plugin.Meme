using ShiroBot.MemePlugin.Meme;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.MemePlugin;

public sealed partial class MemePlugin
{
    public IReadOnlyList<DashboardPage> Pages =>
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
        {
            Upload = _config.Meme.Enabled && _config.Meme.MaxBytes > 0
                ? new DashboardUpload("上传图片", ["image/png", "image/jpeg", "image/gif", "image/webp"],
                    Math.Min(_config.Meme.MaxBytes, 20_000_000), "支持批量选择，自动去重；开启 AI 标注时会自动生成描述和标签。")
                : null
        }
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

    public async Task<PluginActionResult> UploadAsync(string pageId, DashboardUploadFile file, CancellationToken cancellationToken = default)
    {
        var store = Library(pageId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_config.Meme.Enabled) return new PluginActionResult(false, "表情库已关闭。");
        if (file.Content.Length == 0 || file.Content.Length > Math.Min(_config.Meme.MaxBytes, 20_000_000))
            return new PluginActionResult(false, "图片为空或超过单文件大小上限。");
        var data = file.Content.ToArray();
        // Use the bytes, not the browser's extension or MIME header, to identify the image.
        var mime = MemeDownloader.SniffMime(data);
        if (mime is null) return new PluginActionResult(false, "只支持 PNG、JPEG、GIF 和 WebP 图片。");
        if (store.FindExisting(data, null) is not null)
            return new PluginActionResult(true, "图片已存在，已跳过重复上传。", Refresh: true);
        var caption = Path.GetFileNameWithoutExtension(file.FileName.Replace('\\', '/')).Trim();
        if (caption.Length == 0) caption = "上传的表情";
        var tags = MemeTagging.ExtractTags(caption, null, null);
        var emotions = MemeTagging.GuessEmotions(caption);
        var kind = "unknown";
        if (_tagger is { IsConfigured: true } tagger)
        {
            var ai = await tagger.TagAsync(data, mime, caption, cancellationToken).ConfigureAwait(false);
            if (ai is not null)
            {
                if (!string.IsNullOrWhiteSpace(ai.Caption)) caption = ai.Caption;
                if (ai.Tags.Count > 0) tags = ai.Tags.ToList();
                if (ai.Emotions.Count > 0) emotions = ai.Emotions.ToList();
                kind = ai.Kind;
            }
        }
        // Manual uploads are intentional; the automatic non-meme collection filter does not discard them.
        var item = await store.AddAsync(data, mime, caption, tags, emotions,
            new MemeSource { Platform = "dashboard", ChannelId = "dashboard" }, null, kind, cancellationToken).ConfigureAwait(false);
        return new PluginActionResult(item is not null, item is null ? "图片未能入库，请检查大小和存储配置。" : "图片已上传。", Refresh: true);
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
