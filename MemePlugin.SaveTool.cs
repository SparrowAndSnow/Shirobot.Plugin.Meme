using System.ComponentModel;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.MemePlugin.Meme;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.MemePlugin;

[PluginJson]
public sealed record SaveMemeToolArgs(
    [property: Description("Short Chinese description of the image being saved. Describe only visible content.")] string Caption,
    [property: Description("1–16 short Chinese search tags describing visible subjects, actions and emotions.")] string[] Tags);

public sealed partial class MemePlugin
{
    internal static ChatToolInfo SaveToolInfo() => new(
        "Save images attached to the current user message, or its quoted message, to the local meme library. Only when the user explicitly asks to save/collect them. Supply a caption and search tags. Returns confirmed storage results and actual tags.",
        PluginJsonSchema.For<SaveMemeToolArgs>(),
        "用户明确要求保存、收藏图片或收进表情库时，调用 save_meme，并填写图片描述和搜索标签。" +
        "工具读取本条消息的图片，没有时读取引用消息的图片。仅根据看得到的内容标注，不确定时使用概括性标签。" +
        "必须根据工具结果回复；成功后可以简短告知标签，失败时说明失败，不得只口头承诺已保存。" +
        "保存请求无需调用 send_meme。", MaxCallsPerTurn: 1);

    private async Task<ChatToolResult> HandleSaveMemeAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        if (!_config.Meme.Enabled || !_config.Meme.AiToolEnabled || _store is null || _downloader is null)
            return new ChatToolResult("表情库保存工具已关闭，未保存。");
        var chat = call.Context;
        if (chat.Proactive || string.IsNullOrWhiteSpace(chat.InstanceId) || string.IsNullOrWhiteSpace(chat.MessageId) ||
            string.IsNullOrWhiteSpace(chat.SenderId) || string.IsNullOrWhiteSpace(chat.ChannelId) ||
            !Enum.TryParse<ChannelType>(chat.ChannelType, out var type) || !Enum.IsDefined(type))
            return new ChatToolResult("缺少用户触发消息或会话信息，未保存。");
        var args = call.Arguments.As<SaveMemeToolArgs>();
        if (args is null || string.IsNullOrWhiteSpace(args.Caption) || args.Caption.Length > 500 ||
            args.Tags is not { Length: > 0 and <= 16 } ||
            args.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 40))
            return new ChatToolResult("请提供图片描述（最多 500 字）和 1–16 个简短标签（每个最多 40 字），未保存。");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;
        var channel = new Channel(chat.ChannelId, type);
        bool InScope(MessageEvent m) => m.Platform == chat.Platform && m.InstanceId == chat.InstanceId &&
            m.Channel.Id == channel.Id && m.Channel.Type == channel.Type;
        try
        {
            var message = await Context.Message.GetMessageAsync(
                new MessageReference(chat.InstanceId, channel, chat.MessageId), ct).ConfigureAwait(false);
            if (message is null || !InScope(message) || message.MessageId != chat.MessageId || message.Sender.Id != chat.SenderId)
                return new ChatToolResult("无法读取本次用户消息，未保存。");
            var settings = _store.GetGroupSettings(_store.GroupKey(chat.Platform, chat.InstanceId, chat.ChannelId));
            if (type != ChannelType.Direct && !settings.Enabled)
                return new ChatToolResult("本群表情库已关闭，未保存。");
            var jobs = MemeCollector.BuildJobs(message, _config.Meme, settings, force: true, allowSelf: true);
            if (jobs.Count == 0 && message.GetQuote() is { } quote)
            {
                var quoted = await Context.Message.GetMessageAsync(
                    new MessageReference(chat.InstanceId, channel, quote.MessageId), ct).ConfigureAwait(false);
                if (quoted is not null && InScope(quoted) && quoted.MessageId == quote.MessageId)
                    jobs = MemeCollector.BuildJobs(quoted, _config.Meme, settings, force: true, allowSelf: true);
            }
            if (jobs.Count == 0) return new ChatToolResult("本条及引用消息中没有可读取的图片，未保存。");

            var results = new List<string>();
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                var result = await MemeManualSaver.SaveAsync(_store, _downloader, _tagger, job,
                    args.Caption, args.Tags, ct).ConfigureAwait(false);
                results.Add(result);
            }
            return new ChatToolResult(string.Join("\n", results));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new ChatToolResult("读取图片或保存失败；此前图片如已成功入库，仍会保留。请刷新表情库确认，不要声称全部保存成功。");
        }
    }
}
