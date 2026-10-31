using ShiroBot.Meme.Contracts;
using ShiroBot.MemePlugin.Config;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>从群消息中提取可采集的图片/商城表情。只生成任务，实际下载由后台队列完成。</summary>
internal static class MemeCollector
{
    public static List<MemeIngestJob> BuildJobs(
        MessageEvent message,
        MemeSection config,
        GroupSettings? groupSettings,
        bool force)
    {
        var jobs = new List<MemeIngestJob>();
        if (string.Equals(message.Sender.Id, message.SelfId, StringComparison.Ordinal))
            return jobs;

        var autoCollect = (groupSettings?.AutoCollect ?? config.AutoCollect).Trim().ToLowerInvariant();
        if (autoCollect is not ("off" or "sticker" or "all"))
            autoCollect = "sticker";
        if (!force)
        {
            if (string.Equals(autoCollect, "off", StringComparison.OrdinalIgnoreCase))
                return jobs;
            if (message.IsDirect && !config.CollectDirect)
                return jobs;
            if (!message.IsDirect && config.CollectGroups.Count > 0 &&
                !config.CollectGroups.Contains(message.Channel.Id, StringComparer.Ordinal))
                return jobs;
        }

        var text = message.GetPlainText().Trim();
        if (!force && (text.StartsWith('#') || text.StartsWith('/')))
            return jobs;

        foreach (var segment in message.Segments)
        {
            if (jobs.Count >= 4) break;

            switch (segment)
            {
                case RawSegment { Payload: QIncomingMarketFace market }:
                    jobs.Add(CreateJob(message, text, market.Url, market.Summary, "sticker",
                        new MarketFaceInfo(market.EmojiId, market.EmojiPackageId, market.Key,
                            market.Summary, market.Url), force, stickerOnly: true));
                    break;

                case RawSegment { Payload: QIncomingImage image }:
                    // 有些 OneBot/Milky 适配器不填 SubType；先入队，下载后按 MIME 判断。
                    jobs.Add(CreateJob(message, text, image.TempUrl, image.Summary, image.SubType,
                        null, force, stickerOnly: !force &&
                        string.Equals(autoCollect, "sticker", StringComparison.OrdinalIgnoreCase)));
                    break;

                case ImageSegment image:
                    // Milky/OneBot 常把入站图片映射成通用 ImageSegment，没有 SubType；
                    // sticker 模式先入队，下载后按 MIME 决定是否收（GIF/WebP 收，静态图跳过）。
                    jobs.Add(CreateJob(message, text, image.Uri, image.Summary, null, null, force,
                        stickerOnly: !force &&
                        string.Equals(autoCollect, "sticker", StringComparison.OrdinalIgnoreCase)));
                    break;
            }
        }

        return jobs;
    }

    private static MemeIngestJob CreateJob(
        MessageEvent message,
        string text,
        string? url,
        string? summary,
        string? subType,
        MarketFaceInfo? marketFace,
        bool force,
        bool stickerOnly) =>
        new()
        {
            Platform = message.Platform,
            InstanceId = message.InstanceId,
            ChannelId = message.Channel.Id,
            SenderId = message.Sender.Id,
            MessageId = message.MessageId,
            Timestamp = message.Timestamp,
            Url = url,
            Summary = summary,
            SubType = subType,
            MessageText = text,
            MarketFace = marketFace,
            Force = force,
            StickerOnly = stickerOnly,
        };
}
