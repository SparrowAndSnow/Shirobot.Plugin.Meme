namespace ShiroBot.MemePlugin.Meme;

internal static class MemeManualSaver
{
    internal static async Task<string> SaveAsync(MemeStore store, MemeDownloader downloader, MemeAiTagger? tagger,
        MemeIngestJob job, string caption, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var downloaded = await downloader.DownloadAsync(job.Url, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (downloaded is null || downloaded.Data.Length > store.Config.MaxBytes ||
            MemeDownloader.SniffMime(downloaded.Data) is not { } mime)
            return "保存失败：图片下载失败、格式不支持或超过大小限制。";
        var existing = store.FindExisting(downloaded.Data, job.MarketFace);
        if (existing is not null) return Describe(existing, "已在库中，无需重复保存");

        var mergedTags = tags.Select(t => t.Trim()).ToList();
        var emotions = MemeTagging.GuessEmotions(caption + " " + string.Join(" ", tags));
        var kind = "unknown";
        if (tagger is { IsConfigured: true })
        {
            var ai = await tagger.TagAsync(downloaded.Data, mime, caption, ct).ConfigureAwait(false);
            if (ai is not null)
            {
                if (!string.IsNullOrWhiteSpace(ai.Caption)) caption = ai.Caption;
                mergedTags.AddRange(ai.Tags);
                if (ai.Emotions.Count > 0) emotions = ai.Emotions.ToList();
                kind = ai.Kind;
            }
        }
        ct.ThrowIfCancellationRequested();
        var item = await store.AddAsync(downloaded.Data, mime, caption, mergedTags, emotions,
            new MemeSource
            {
                Platform = job.Platform, InstanceId = job.InstanceId, ChannelId = job.ChannelId,
                SenderId = job.SenderId, MessageId = job.MessageId, Timestamp = job.Timestamp,
            }, job.MarketFace, kind, ct).ConfigureAwait(false);
        // 容量清理可能立即移除无法容纳的新图；只有可读回时才报告成功。
        return item is not null && store.GetById(item.Id) is not null
            ? Describe(item, "已保存") : "保存失败：文件无效、写入失败或表情库容量不足。";
    }

    private static string Describe(MemeItem item, string status) =>
        $"{status}：{item.Id[..8]}；描述：{item.Caption}；标签：{string.Join("、", item.Tags)}";
}
