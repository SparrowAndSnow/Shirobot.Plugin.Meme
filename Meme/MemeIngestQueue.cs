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
    private readonly MemeAiTagger? _tagger;
    private readonly Task _worker;
    private readonly CancellationTokenSource _stop = new();
    private bool _completed;

    public MemeIngestQueue(MemeStore store, MemeDownloader downloader, MemeAiTagger? tagger = null)
    {
        _store = store;
        _downloader = downloader;
        _tagger = tagger;
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
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var downloaded = await _downloader.DownloadAsync(job.Url, _stop.Token)
                        .ConfigureAwait(false);
                    if (downloaded is null) continue;

                    var config = _store.Config;
                    if (!job.Force && job.StickerOnly &&
                        !MemeTagging.IsStickerLike(job.SubType, downloaded.Mime))
                        continue;

                    // 先查重再调用 AI，避免为同一张转发图重复付费。
                    var existing = _store.FindExisting(downloaded.Data, job.MarketFace, out var fingerprint);
                    if (existing is not null)
                    {
                        BotLog.Info($"Meme 已存在 {existing.Id[..Math.Min(8, existing.Id.Length)]}，跳过");
                        continue;
                    }

                    var tags = MemeTagging.ExtractTags("", job.Summary, job.MarketFace);
                    var emotions = MemeTagging.GuessEmotions(job.Summary ?? "");
                    var caption = MemeTagging.BuildCaption(job.Summary, "", config.CaptionMaxChars);
                    var kind = "unknown";

                    if (_tagger is { } tagger && tagger.IsConfigured)
                    {
                        var ai = await tagger.TagAsync(
                            downloaded.Data,
                            downloaded.Mime,
                            $"{job.MessageText} {job.Summary} {job.MarketFace?.Summary}",
                            _stop.Token).ConfigureAwait(false);
                        if (ai is not null)
                        {
                            kind = ai.Kind;
                            if (!job.Force && config.AiFilterNonMeme &&
                                kind is "screenshot" or "photo" or "other")
                            {
                                BotLog.Info($"Meme AI 判定为 {kind}，跳过收录");
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(ai.Caption)) caption = ai.Caption;
                            if (ai.Tags.Count > 0) tags = ai.Tags.ToList();
                            if (ai.Emotions.Count > 0) emotions = ai.Emotions.ToList();
                        }
                    }

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
                        job.MarketFace,
                        kind, cancellationToken: _stop.Token, precomputedFingerprint: fingerprint).ConfigureAwait(false);

                    if (item is not null)
                        BotLog.Info($"Meme 收录 {item.Id[..Math.Min(8, item.Id.Length)]} ({downloaded.Mime}, {downloaded.Data.Length} bytes)");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    BotLog.Warning($"Meme 采集失败：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed) return;
        _completed = true;
        _channel.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _stop.Dispose();
        }
        catch (TimeoutException)
        {
            BotLog.Warning("Meme 采集队列在卸载时未能及时停止");
        }
    }
}
