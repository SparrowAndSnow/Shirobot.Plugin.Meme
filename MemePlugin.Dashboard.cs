using ShiroBot.MemePlugin.Meme;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.MemePlugin;

public sealed partial class MemePlugin
{
    private void RegisterDashboardPage() => Context.Dashboard.AddPage("library", "表情库", RenderLibraryAsync,
        icon: "photo_library", description: "上传、搜索和管理已保存的表情。", files: OpenLibraryImageAsync);

    internal Task<UiNode> RenderLibraryAsync(DashboardContext page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = Library();
        var q = page.Filter("q") ?? "";
        var tag = page.Filter("tag") ?? "";
        var emotion = page.Filter("emotion") ?? "";
        var matching = store.GetItems()
            .Where(item => q.Length == 0 || item.Caption.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.Tags.Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                item.Emotions.Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Where(item => tag.Length == 0 || item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Where(item => emotion.Length == 0 || item.Emotions.Contains(emotion, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt).ToArray();
        const int size = 24;
        var offset = (long)(page.Page() - 1) * size;
        var items = matching.Skip((int)Math.Min(offset, matching.Length)).Take(size).ToArray();
        return Task.FromResult<UiNode>(Ui.Column(
            Ui.Text("支持批量上传 PNG、JPEG、GIF、WebP，自动去重；开启 AI 标注时会生成描述和标签。"),
            _config.Meme.Enabled && _config.Meme.MaxBytes > 0
                ? Ui.Upload("import", "上传图片", (file, e, ct) => ImportLibraryImageAsync(file, ct),
                    accept: ["image/png", "image/jpeg", "image/gif", "image/webp"], maxBytes: Math.Min(_config.Meme.MaxBytes, 20_000_000))
                : Ui.Notice("表情库已关闭，不能上传新图片。", "warning"),
            Ui.Filters(Ui.Input("q", "搜索"), Ui.Input("tag", "标签"),
                Ui.Select("emotion", "情绪", [("happy", "开心"), ("sad", "难过"), ("angry", "生气"),
                    ("confused", "困惑"), ("shy", "害羞"), ("daily", "日常")])),
            Ui.Gallery(items, item => new UiCard(item.Id,
                string.IsNullOrWhiteSpace(item.Caption) ? "表情" : item.Caption)
            {
                Description = string.Join(" · ", item.Emotions),
                Image = item.Id,
                Tags = item.Tags.ToArray(),
                Meta = $"使用 {item.UseCount} 次 · {item.Bytes / 1024d:F1} KB"
            })
                .Action("delete", "删除", async (item, e, ct) =>
                    await Library().RemoveAsync(item.Id, ct).ConfigureAwait(false)
                        ? DashboardResult.Done("已删除。") : DashboardResult.Error("未找到该表情，请刷新。"),
                    confirm: "确定删除这个表情？", danger: true),
            Ui.Empty("没有匹配的表情。", when: matching.Length == 0),
            Ui.Pager(matching.Length, size)));
    }

    internal async Task<DashboardResult> ImportLibraryImageAsync(DashboardUploadFile file, CancellationToken cancellationToken = default)
    {
        var store = Library();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_config.Meme.Enabled) return DashboardResult.Error("表情库已关闭。");
        if (file.Content.Length == 0 || file.Content.Length > Math.Min(_config.Meme.MaxBytes, 20_000_000))
            return DashboardResult.Error("图片为空或超过单文件大小上限。");
        var data = file.Content.ToArray();
        // Use the bytes, not the browser's extension or MIME header, to identify the image.
        var mime = MemeDownloader.SniffMime(data);
        if (mime is null) return DashboardResult.Error("只支持 PNG、JPEG、GIF 和 WebP 图片。");
        if (store.FindExisting(data, null) is not null)
            return DashboardResult.Done("图片已存在，已跳过重复上传。");
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
        return item is null ? DashboardResult.Error("图片未能入库，请检查大小和存储配置。") : DashboardResult.Done("图片已上传。");
    }

    internal Task<DashboardFile?> OpenLibraryImageAsync(string fileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = Library().GetById(fileId);
        return Task.FromResult(item is not null && item.Id == fileId && File.Exists(item.FilePath)
            ? DashboardFile.FromPath(item.FilePath, item.Mime) : null);
    }

    private MemeStore Library() => _store ?? throw new InvalidOperationException("表情库未加载。");
}
