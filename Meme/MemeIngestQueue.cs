using System.Threading.Channels;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>有界后台队列，避免图片下载阻塞消息事件分发。</summary>
internal sealed class MemeIngestQueue : IAsyncDisposable
{
    private readonly Channel<MemeIngestJob> _channel;
    private readonly MemeStore _store;
    private readonly MemeDownloader _downloader;
    private readonly Task _worker;
    private bool _completed;

    public MemeIngestQueue(MemeStore store, MemeDownloader downloader)
    {
        _store = store;
        _downloader = downloader;
        _channel = Channel.CreateBounded<MemeIngestJob>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(ProcessAsync);
    }

    public bool TryEnqueue(MemeIngestJob job) => _channel.Writer.TryWrite(job);

    private async Task ProcessAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var downloaded = await _downloader.DownloadAsync(job.Url, CancellationToken.None)
                    .ConfigureAwait(false);
                if (downloaded is null) continue;

                var config = _store.Config;
                if (!job.Force && job.StickerOnly &&
                    !MemeTagging.IsStickerLike(job.SubType, downloaded.Mime))
                    continue;

                var tags = MemeTagging.ExtractTags(job.MessageText, job.Summary, job.MarketFace);
                var emotions = MemeTagging.GuessEmotions($"{job.MessageText} {job.Summary}");
                var caption = MemeTagging.BuildCaption(job.Summary, job.MessageText, config.CaptionMaxChars);

                var item = await _store.AddAsync(
                    downloaded.Data,
                    downloaded.Mime,
                    caption,
                    tags,
                    emotions,
                    new MemeSource
                    {
                        Platform = job.Platform,
                        InstanceId = job.InstanceId,
                        ChannelId = job.ChannelId,
                        SenderId = job.SenderId,
                        MessageId = job.MessageId,
                        Timestamp = job.Timestamp,
                    },
                    job.MarketFace).ConfigureAwait(false);

                if (item is not null)
                    BotLog.Info($"Meme 收录 {item.Id[..Math.Min(8, item.Id.Length)]} ({downloaded.Mime}, {downloaded.Data.Length} bytes)");
            }
            catch (Exception ex)
            {
                BotLog.Warning($"Meme 采集失败：{ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed) return;
        _completed = true;
        _channel.Writer.TryComplete();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            BotLog.Warning("Meme 采集队列在卸载时未能及时停止");
        }
    }
}
