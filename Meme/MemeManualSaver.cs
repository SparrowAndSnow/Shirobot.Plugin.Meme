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
        var existing = store.FindExisting(downloaded.Data, job.MarketFace, out var fingerprint);

        var mergedTags = tags.Select(t => t.Trim()).ToList();
        var emotions = MemeTagging.GuessEmotions(caption + " " + string.Join(" ", tags));
        var kind = "unknown";
        // The calling AIChat model has already inspected this exact image and supplied labels.
        // Calling it again here can deadlock when its configured concurrency is one.
        ct.ThrowIfCancellationRequested();
        var item = await store.AddAsync(downloaded.Data, mime, caption, mergedTags, emotions,
            new MemeSource
            {
                Platform = job.Platform, InstanceId = job.InstanceId, ChannelId = job.ChannelId,
                SenderId = job.SenderId, MessageId = job.MessageId, Timestamp = job.Timestamp,
            }, job.MarketFace, kind, ct, precomputedFingerprint: fingerprint).ConfigureAwait(false);
        if (item is not null)
        {
            // Auto-collection can win the race and create an entry with chat text as tags.
            // Explicit saving must replace that metadata with the confirmed visual description.
            await store.UpdateMetadataAsync(item.Id, caption, mergedTags, emotions, kind == "unknown" ? null : kind, ct).ConfigureAwait(false);
            item = store.GetItems().FirstOrDefault(candidate => candidate.Id == item.Id);
        }
        // 容量清理可能立即移除无法容纳的新图；只有可读回时才报告成功。
        return item is not null && store.GetById(item.Id) is not null
            ? Describe(item, existing is null ? "已保存" : "已在库中，已更新描述和标签") : "保存失败：文件无效、写入失败或表情库容量不足。";
    }

    private static string Describe(MemeItem item, string status) =>
        $"{status}：{item.Id[..8]}；描述：{item.Caption}；标签：{string.Join("、", item.Tags)}";
}
