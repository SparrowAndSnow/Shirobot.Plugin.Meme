using ShiroBot.MemePlugin.Contracts;
using ShiroBot.MemePlugin.Meme;

namespace ShiroBot.MemePlugin;

public sealed partial class MemePlugin
{
    private void RegisterLibraryServices()
    {
        MemeLibrary.Select.Provide(Context, async (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var selection = await SelectAsync(new MemeQuery(request.Query, request.Emotion, request.ChannelId,
                Limit: Math.Clamp(request.Limit, 1, 100)), ct).ConfigureAwait(false);
            return new MemeSelectResponse(selection is null ? null :
                new MemeEntry(selection.Id, selection.Caption, selection.Tags, selection.Mime));
        });
        MemeLibrary.Send.Provide(Context, SendLibraryMemeAsync);
        MemeLibrary.Stats.Provide(Context, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            var stats = await GetStatsAsync(ct).ConfigureAwait(false);
            return new MemeStats(_config.Meme.Enabled, stats.Count, stats.Bytes, stats.LastUpdated);
        });
    }

    private async Task<MemeSendResponse> SendLibraryMemeAsync(MemeSendRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = request.Target;
        if (target is null || string.IsNullOrWhiteSpace(target.Platform) || string.IsNullOrWhiteSpace(target.ChannelId) ||
            !(string.Equals(target.ChannelType, "Group", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(target.ChannelType, "Direct", StringComparison.OrdinalIgnoreCase)))
            return new MemeSendResponse(false, Error: "发送目标无效，ChannelType 必须为 Group 或 Direct。");
        var selection = string.IsNullOrWhiteSpace(request.Id) ? null : _store?.GetById(request.Id);
        if (selection is null || !string.Equals(selection.Id, request.Id, StringComparison.Ordinal))
            return new MemeSendResponse(false, Error: "未找到该表情，请使用完整 ID。");
        var result = await SendAsync(selection, new MemeSendTarget(target.Platform, target.InstanceId,
            target.ChannelType, target.ChannelId, target.MessageId, target.SenderId,
            string.Equals(target.ChannelType, "Group", StringComparison.OrdinalIgnoreCase), target.Mention, target.Quote),
            request.Caption, ct).ConfigureAwait(false);
        return new MemeSendResponse(result.Success, result.MessageId, result.Error);
    }
}
