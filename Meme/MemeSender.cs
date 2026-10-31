using ShiroBot.Meme.Contracts;
using ShiroBot.MemePlugin.Config;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>把选中的表情发送到目标会话。QQ 优先尝试原生 sticker 图片，失败后回退通用图片段。</summary>
internal sealed class MemeSender(IBotContext bot, MemeSection config)
{
    public async Task<MemeSendResult> SendAsync(
        MemeSelection selection,
        MemeSendTarget target,
        string? caption,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(selection.FilePath))
            return new MemeSendResult(false, Error: "表情文件不存在");

        byte[] data;
        try
        {
            var info = new FileInfo(selection.FilePath);
            if (info.Length <= 0 || info.Length > config.MaxBytes)
                return new MemeSendResult(false, Error: "表情文件超过大小限制");
            data = await File.ReadAllBytesAsync(selection.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new MemeSendResult(false, Error: ex.Message);
        }

        var uri = "base64://" + Convert.ToBase64String(data);
        var text = TrimCaption(caption, config.CaptionMaxChars);

        IDisposable? scope = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(target.InstanceId))
                scope = bot.UseInstance(target.InstanceId);
        }
        catch (Exception)
        {
            // 旧宿主/实例已卸载时退回当前默认实例。
        }

        using (scope)
        {
            var channel = string.Equals(target.ChannelType, "Group", StringComparison.OrdinalIgnoreCase)
                ? Channel.Group(target.ChannelId)
                : Channel.Direct(target.ChannelId);

            var generic = BuildGenericSegments(uri, target, text);
            var qq = bot.GetAdapterExtension<IQMessageApi>();

            if (qq is not null && string.Equals(target.Platform, "qq", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var scene = target.IsGroup ? QMessageScene.Group : QMessageScene.Friend;
                    var native = new List<QOutgoingSegment>();
                    if (target.Quote && !string.IsNullOrWhiteSpace(target.MessageId))
                        native.Add(new QOutgoingReply(target.MessageId));
                    if (target.Mention && target.IsGroup && !string.IsNullOrWhiteSpace(target.SenderId))
                        native.Add(new QOutgoingMention(target.SenderId));
                    native.Add(new QOutgoingImage(uri)
                    {
                        Summary = text,
                        SubType = string.Equals(config.SendMode, "image", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : "sticker",
                    });
                    if (text.Length > 0)
                        native.Add(new QOutgoingText(" " + text));

                    var messageId = await qq.SendMessageAsync(scene, target.ChannelId, native, cancellationToken)
                        .ConfigureAwait(false);
                    return new MemeSendResult(true, messageId);
                }
                catch (Exception ex)
                {
                    // OneBot/Milky 适配器不一定实现 QOutgoingImage.SubType；回退通用图片段。
                    BotLog.Warning($"Meme QQ 原生发送失败，回退通用图片：{ex.Message}");
                }
            }

            try
            {
                var sent = await bot.Message.SendMessageAsync(channel, generic, cancellationToken)
                    .ConfigureAwait(false);
                return sent.IsSuccess
                    ? new MemeSendResult(true, sent.MessageId)
                    : new MemeSendResult(false, Error: sent.ErrorMessage ?? "发送失败");
            }
            catch (Exception ex)
            {
                return new MemeSendResult(false, Error: ex.Message);
            }
        }
    }

    private static List<MessageSegment> BuildGenericSegments(
        string uri,
        MemeSendTarget target,
        string caption)
    {
        var segments = new List<MessageSegment>();
        if (target.Quote && !string.IsNullOrWhiteSpace(target.MessageId))
            segments.Add(new QuoteSegment(target.MessageId));
        if (target.Mention && target.IsGroup && !string.IsNullOrWhiteSpace(target.SenderId))
            segments.Add(new MentionSegment(target.SenderId));
        segments.Add(new ImageSegment(uri) { Summary = caption });
        if (caption.Length > 0)
            segments.Add(new TextSegment(" " + caption));
        return segments;
    }

    private static string TrimCaption(string? caption, int maxChars)
    {
        var value = (caption ?? "").Trim();
        return value.Length <= maxChars ? value : value[..Math.Max(0, maxChars)];
    }
}
