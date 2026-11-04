using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.MemePlugin.Config;
using ShiroBot.MemePlugin.Meme;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotPackage("shirobot.plugin.meme", ShiroBotPackageKind.Plugin, "1.0.1")]
[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "0.9.8")]

namespace ShiroBot.MemePlugin;

[BotPlugin(
    "MemePlugin",
    Name = "表情包插件",
    Version = "1.0.1",
    Description = "本地表情库、群聊盗图采集、QQ 商城表情降级发送，并向其他插件提供表情服务。",
    Author = "SparrowAndSnow",
    GithubRepo = "SparrowAndSnow/Shirobot.Plugin.Meme",
    Category = PluginCategory.Media,
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ")]
public sealed partial class MemePlugin : PluginBase<MemeConfig>, IMemeService
{
    private const string CommandPrefix = "#meme";
    private const string DefaultConfig = """
        [meme]
        enabled = true
        auto_collect = "sticker"
        collect_groups = []
        collect_direct = false
        max_bytes = 5000000
        max_items = 5000
        max_total_bytes = 500000000
        cooldown_seconds = 45
        max_per_hour = 10
        send_with_mention = true
        send_with_quote = false
        send_mode = "auto"
        caption_max_chars = 120
        download_timeout_seconds = 15
        library_dir = "memes"
        ai_tool_enabled = true
        ai_tagging_enabled = false
        ai_provider = "auto"
        ai_base_url = ""
        ai_api_key = ""
        ai_model = ""
        ai_timeout_seconds = 30
        ai_max_tags = 8
        ai_infer_emotions = true
        ai_prompt = ""
        ai_filter_non_meme = true

        [network]
        http_proxy = ""
        """;

    private MemeConfig _config = new();
    private MemeStore? _store;
    private MemeDownloader? _downloader;
    private MemeIngestQueue? _queue;
    private MemeSender? _sender;
    private MemeAiTagger? _tagger;
    private readonly CancellationTokenSource _lifetime = new();
    private int _retagRunning;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, ChannelUsage> _usage = new(StringComparer.Ordinal);
    private IDisposable? _chatTool;
    private IDisposable? _saveChatTool;

    private readonly Dictionary<string, Queue<string>> _recent = new(StringComparer.Ordinal);

    protected override Task OnConfigLoadedAsync(MemeConfig config, CancellationToken cancellationToken)
    {
        _config = config;
        return Task.CompletedTask;
    }

    protected override Task OnConfigChangedAsync(MemeConfig previous, MemeConfig current, CancellationToken cancellationToken)
    {
        _config = current;
        _store?.UpdateConfig(current.Meme);
        if (_sender is not null && Context is not null)
            _sender = new MemeSender(Context, current.Meme);
        _tagger?.UpdateConfig(current.Meme, current.Network);
        if (_store is not null) UpdateChatTool();
        return Task.CompletedTask;
    }

    protected override void ConfigureRoutes()
    {
        GroupCommands.MapPrefix(CommandPrefix, HandleMemeCommandAsync);
        DirectCommands.MapPrefix(CommandPrefix, HandleMemeCommandAsync);

        // 观察者路由：不标记 handled，因此不会阻断 AI 聊天等优先级更高的插件。
        Events.Map<MessageEvent>(HandleMessageAsync);
    }

    protected override async Task LoadAsync()
    {
        await EnsureConfigTemplateAsync().ConfigureAwait(false);

        var root = Path.Combine(Context.PluginDirectory, _config.Meme.LibraryDir);
        _store = new MemeStore(root, _config.Meme);
        await _store.LoadAsync().ConfigureAwait(false);

        _downloader = new MemeDownloader(_config.Meme, _config.Network);
        _tagger = new MemeAiTagger(_config.Meme, _config.Network) { AiChat = TagWithAiChatModelAsync };
        _queue = new MemeIngestQueue(_store, _downloader, _tagger);
        _sender = new MemeSender(Context, _config.Meme);

        RegisterDashboardPage();
        RegisterLibraryServices();
        RegisterChatServices();
        BotLog.Info($"MemePlugin 已加载，表情库：{root}");
    }

    protected override async Task OnUnloadAsync()
    {
        if (_queue is not null)
            await _queue.DisposeAsync().ConfigureAwait(false);
        _queue = null;

        _lifetime.Cancel();
        _downloader?.Dispose();
        _downloader = null;
        _tagger?.Dispose();
        _tagger = null;
        _store?.Dispose();
        _store = null;
        _lifetime.Dispose();
        _sendGate.Dispose();
    }

    private async Task EnsureConfigTemplateAsync()
    {
        var path = Context.Config.ConfigPath;
        if (File.Exists(path)) return;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, DefaultConfig).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"Meme 默认配置写入失败：{ex.Message}");
        }
    }

    private async Task HandleMessageAsync(MessageEvent message)
    {
        if (!_config.Meme.Enabled || _store is null || _queue is null) return;

        var key = _store.GroupKey(message.Platform, message.InstanceId, message.Channel.Id);
        var settings = _store.GetGroupSettings(key);
        if (!message.IsDirect && !settings.Enabled) return;

        foreach (var job in MemeCollector.BuildJobs(message, _config.Meme, settings, force: false))
            _queue.TryEnqueue(job);
    }

    private async Task HandleMemeCommandAsync(MessageEvent message)
    {
        var text = message.GetPlainText().Trim();
        var args = text.Length > CommandPrefix.Length
            ? text[CommandPrefix.Length..].Trim()
            : "";

        if (!_config.Meme.Enabled)
        {
            await ReplyTextAsync(message, "表情库当前已关闭。").ConfigureAwait(false);
            return;
        }

        if (_store is null || _sender is null)
        {
            await ReplyTextAsync(message, "表情库尚未初始化完成。").ConfigureAwait(false);
            return;
        }

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        switch (sub)
        {
            case "help":
                await ReplyTextAsync(message, HelpText).ConfigureAwait(false);
                break;
            case "stats":
                var stats = _store.GetStats();
                await ReplyTextAsync(message,
                    $"表情库：{stats.Count} 张，{stats.Bytes / 1024.0 / 1024.0:F1} MB，最近更新 {stats.LastUpdated?.ToLocalTime():yyyy-MM-dd HH:mm}").ConfigureAwait(false);
                break;
            case "retag":
                await RetagAsync(message, rest).ConfigureAwait(false);
                break;
            case "clean":
                await CleanAsync(message, rest).ConfigureAwait(false);
                break;
            case "list":
                await ListAsync(message, rest).ConfigureAwait(false);
                break;
            case "random":
                await RandomAsync(message, rest).ConfigureAwait(false);
                break;
            case "save":
                await SaveQuotedAsync(message).ConfigureAwait(false);
                break;
            case "add":
                await AddUrlAsync(message, rest).ConfigureAwait(false);
                break;
            case "tag":
                await TagAsync(message, rest).ConfigureAwait(false);
                break;
            case "del":
                await DeleteAsync(message, rest).ConfigureAwait(false);
                break;
            case "off":
                await SetGroupEnabledAsync(message, false).ConfigureAwait(false);
                break;
            case "on":
                await SetGroupEnabledAsync(message, true).ConfigureAwait(false);
                break;
            default:
                await ReplyTextAsync(message, HelpText).ConfigureAwait(false);
                break;
        }
    }

    private async Task CleanAsync(MessageEvent message, string argument)
    {
        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyTextAsync(message, "只有管理员可以清理表情库。").ConfigureAwait(false);
            return;
        }

        var items = _store!.GetItems();
        var candidates = items
            .Where(item => item.Kind is "screenshot" or "photo" or "other")
            .ToList();
        var unknown = items.Count(item => item.Kind is null or "" or "unknown");

        if (!string.Equals(argument.Trim(), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            if (candidates.Count == 0)
            {
                var hint = unknown > 0 ? $"另有 {unknown} 张未分类，可先执行 #meme retag all。" : "";
                await ReplyTextAsync(message, $"没有 AI 判定为截图/照片/其他的条目。{hint}").ConfigureAwait(false);
                return;
            }

            var lines = candidates.Take(20).Select(item =>
                $"{item.Id[..Math.Min(8, item.Id.Length)]} [{item.Kind}] {item.Caption}");
            var more = candidates.Count > 20 ? $"\n……另有 {candidates.Count - 20} 张" : "";
            var unknownHint = unknown > 0 ? $"\n另有 {unknown} 张未分类，可先执行 #meme retag all。" : "";
            await ReplyTextAsync(message,
                $"AI 判定非表情 {candidates.Count} 张：\n{string.Join("\n", lines)}{more}{unknownHint}\n确认删除请发送 #meme clean confirm。")
                .ConfigureAwait(false);
            return;
        }

        if (candidates.Count == 0)
        {
            await ReplyTextAsync(message, "没有需要删除的条目。").ConfigureAwait(false);
            return;
        }

        var removed = 0;
        foreach (var item in candidates)
        {
            if (await _store.RemoveAsync(item.Id).ConfigureAwait(false))
                removed++;
        }

        BotLog.Info($"Meme 清理完成：删除 {removed} 张截图/照片");
        await ReplyTextAsync(message, $"已删除 {removed} 张 AI 判定为截图/照片/其他的条目。").ConfigureAwait(false);
    }

    private async Task RetagAsync(MessageEvent message, string argument)
    {
        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyTextAsync(message, "只有管理员可以触发 AI 重新标注。").ConfigureAwait(false);
            return;
        }

        if (_tagger is null || !_tagger.IsConfigured)
        {
            await ReplyTextAsync(message, "AI 标注不可用：请开启 AI 标注，并安装 AI 聊天插件借用它的模型，或在“表情库设置”里填写自己的 AI 接口地址和模型。").ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref _retagRunning, 1) != 0)
        {
            await ReplyTextAsync(message, "AI 重新标注已经在运行。").ConfigureAwait(false);
            return;
        }

        var all = string.Equals(argument.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var store = _store;
        var tagger = _tagger;
        var items = store!.GetItems()
            .Where(item => all || NeedsAiTags(item))
            .ToList();
        if (items.Count == 0)
        {
            Interlocked.Exchange(ref _retagRunning, 0);
            await ReplyTextAsync(message, "没有需要重新标注的表情。").ConfigureAwait(false);
            return;
        }

        await ReplyTextAsync(message, $"已开始 AI 重新标注 {items.Count} 张（{(all ? "全部" : "仅空泛标签")}），完成后会写入日志。")
            .ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            var updated = 0;
            var removed = 0;
            var failed = 0;
            try
            {
                foreach (var item in items)
                {
                    if (_lifetime.IsCancellationRequested) break;
                    try
                    {
                        var path = Path.Combine(store.FilesDirectory, item.FileName);
                        BotLog.Info($"Meme 重新标注处理 id={item.Id[..8]} 文件={item.FileName}");
                        if (!File.Exists(path)) { failed++; BotLog.Warning($"Meme 重新标注跳过 id={item.Id[..8]}：文件不存在"); continue; }

                        var data = await File.ReadAllBytesAsync(path, _lifetime.Token).ConfigureAwait(false);
                        var ai = await tagger.TagAsync(data, item.Mime,
                            $"{item.Caption} {string.Join(" ", item.Tags)}", _lifetime.Token).ConfigureAwait(false);
                        if (ai is null) { failed++; continue; }
                        if (_config.Meme.AiFilterNonMeme && ai.Kind is "screenshot" or "photo" or "other")
                        {
                            if (await store.QuarantineAsync(item.Id, _lifetime.Token).ConfigureAwait(false)) removed++;
                            else failed++;
                            BotLog.Info($"Meme 重新标注清理 id={item.Id[..8]} kind={ai.Kind}：移出表情库，原图保存在 rejected 目录");
                            continue;
                        }

                        var caption = string.IsNullOrWhiteSpace(ai.Caption) ? item.Caption : ai.Caption;
                        var tags = ai.Tags.Count > 0 ? ai.Tags : item.Tags;
                        var emotions = ai.Emotions.Count > 0 ? ai.Emotions : item.Emotions;
                        if (await store.UpdateMetadataAsync(item.Id, caption, tags, emotions, ai.Kind, _lifetime.Token).ConfigureAwait(false))
                            updated++;
                        else { failed++; BotLog.Warning($"Meme 重新标注写入失败 id={item.Id[..8]}：条目已不存在"); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed++; BotLog.Warning($"Meme 重新标注失败 id={item.Id[..8]}：{ex.GetType().Name}: {ex.Message}"); }
                }

                BotLog.Info($"Meme AI 重新标注完成：更新 {updated}，清理 {removed}，失败/跳过 {failed}，总计 {items.Count} 张");
            }
            catch (OperationCanceledException)
            {
                BotLog.Info($"Meme AI 重新标注已取消：{updated}/{items.Count} 张");
            }
            catch (Exception ex)
            {
                BotLog.Warning($"Meme AI 重新标注失败：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _retagRunning, 0);
            }
        }, _lifetime.Token);
    }

    private static bool NeedsAiTags(MemeItem item)
    {
        if (item.Caption is "表情包" or "[图片]" or "[动态表情]" or "[动画表情]" or "[image]" or "[sticker]")
            return true;
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[图片]", "[动画表情]", "[动态表情]", "[image]", "[sticker]", "表情", "图片",
        };
        return item.Tags.Count == 0 || item.Tags.All(generic.Contains);
    }

    private async Task ListAsync(MessageEvent message, string tag)
    {
        var stats = _store!.GetStats();
        if (string.IsNullOrWhiteSpace(tag))
        {
            await ReplyTextAsync(message, $"表情库：共 {stats.Count} 张。").ConfigureAwait(false);
            return;
        }

        var count = _store.CountMatching(tag, null);
        await ReplyTextAsync(message, count == 0
            ? $"没有找到匹配“{tag}”的表情；表情库共 {stats.Count} 张。"
            : $"匹配“{tag}”：{count} 张；表情库共 {stats.Count} 张。").ConfigureAwait(false);
    }

    private async Task RandomAsync(MessageEvent message, string query)
    {
        var selection = await SelectAsync(new MemeQuery(query, ChannelId: message.Channel.Id), CancellationToken.None)
            .ConfigureAwait(false);
        if (selection is null)
        {
            await ReplyTextAsync(message, "暂时没有合适的表情，或本会话正在冷却。").ConfigureAwait(false);
            return;
        }

        var result = await SendAsync(selection, BuildTarget(message), null, CancellationToken.None)
            .ConfigureAwait(false);
        if (!result.Success)
            await ReplyTextAsync(message, $"发送失败：{result.Error}").ConfigureAwait(false);
    }

    private async Task SaveQuotedAsync(MessageEvent message)
    {
        var quote = message.GetQuote();
        if (quote is null)
        {
            await ReplyTextAsync(message, "请引用一张图片/商城表情后再发送 #meme save。").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.InstanceId))
        {
            await ReplyTextAsync(message, "当前消息没有适配器实例信息，无法读取引用消息。").ConfigureAwait(false);
            return;
        }

        MessageEvent? quoted;
        try
        {
            quoted = await Context.Message.GetMessageAsync(
                new MessageReference(message.InstanceId, message.Channel, quote.MessageId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReplyTextAsync(message, $"读取引用消息失败：{ex.Message}").ConfigureAwait(false);
            return;
        }

        if (quoted is null)
        {
            await ReplyTextAsync(message, "引用消息不存在或无法读取。").ConfigureAwait(false);
            return;
        }

        var key = _store!.GroupKey(message.Platform, message.InstanceId, message.Channel.Id);
        var settings = _store.GetGroupSettings(key);
        var jobs = MemeCollector.BuildJobs(quoted, _config.Meme, settings, force: true);
        foreach (var job in jobs)
            _queue!.TryEnqueue(job);

        await ReplyTextAsync(message, jobs.Count > 0 ? "已加入表情采集队列。" : "引用消息里没有可采集的图片。")
            .ConfigureAwait(false);
    }

    private async Task AddUrlAsync(MessageEvent message, string rest)
    {
        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyTextAsync(message, "只有管理员可以添加外部图片。").ConfigureAwait(false);
            return;
        }

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            await ReplyTextAsync(message, "用法：#meme add <http(s) 图片地址> [标签]").ConfigureAwait(false);
            return;
        }

        var downloaded = await _downloader!.DownloadAsync(parts[0], CancellationToken.None).ConfigureAwait(false);
        if (downloaded is null)
        {
            await ReplyTextAsync(message, "下载失败或不是允许的图片格式。").ConfigureAwait(false);
            return;
        }

        var existing = _store!.FindExisting(downloaded.Data, null);
        if (existing is not null)
        {
            await ReplyTextAsync(message, $"这张图已经在表情库里了（{existing.Id[..Math.Min(8, existing.Id.Length)]}）。")
                .ConfigureAwait(false);
            return;
        }

        var text = parts.Length > 1 ? parts[1] : "";
        var tags = MemeTagging.ExtractTags(text, null, null);
        var caption = MemeTagging.BuildCaption(null, text, _config.Meme.CaptionMaxChars);
        var emotions = MemeTagging.GuessEmotions(text);
        var kind = "unknown";
        if (_tagger is { } tagger && tagger.IsConfigured)
        {
            var ai = await tagger.TagAsync(downloaded.Data, downloaded.Mime, text, CancellationToken.None)
                .ConfigureAwait(false);
            if (ai is not null)
            {
                kind = ai.Kind;
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
                Platform = message.Platform,
                InstanceId = message.InstanceId,
                ChannelId = message.Channel.Id,
                SenderId = message.Sender.Id,
                MessageId = message.MessageId,
                Timestamp = DateTimeOffset.UtcNow,
            },
            null,
            kind).ConfigureAwait(false);

        await ReplyTextAsync(message,
            item is null ? "添加失败：文件过大或不是有效图片。" : $"已添加：{item.Id[..Math.Min(8, item.Id.Length)]}").ConfigureAwait(false);
    }

    private async Task TagAsync(MessageEvent message, string rest)
    {
        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyTextAsync(message, "只有管理员可以修改标签。").ConfigureAwait(false);
            return;
        }

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            await ReplyTextAsync(message, "用法：#meme tag <ID> <标签1 标签2...>").ConfigureAwait(false);
            return;
        }

        var tags = parts[1].Split([' ', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ok = await _store!.UpdateTagsAsync(parts[0], tags).ConfigureAwait(false);
        await ReplyTextAsync(message, ok ? "标签已更新。" : "没有找到该表情。").ConfigureAwait(false);
    }

    private async Task DeleteAsync(MessageEvent message, string id)
    {
        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyTextAsync(message, "只有管理员可以删除表情。").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            await ReplyTextAsync(message, "用法：#meme del <ID>").ConfigureAwait(false);
            return;
        }

        var ok = await _store!.RemoveAsync(id.Trim()).ConfigureAwait(false);
        await ReplyTextAsync(message, ok ? "已删除。" : "没有找到该表情。").ConfigureAwait(false);
    }

    private async Task SetGroupEnabledAsync(MessageEvent message, bool enabled)
    {
        if (message.IsDirect)
        {
            await ReplyTextAsync(message, "该命令仅用于群聊。").ConfigureAwait(false);
            return;
        }

        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyTextAsync(message, "只有管理员可以修改本群的采集开关。").ConfigureAwait(false);
            return;
        }

        var key = _store!.GroupKey(message.Platform, message.InstanceId, message.Channel.Id);
        var settings = _store.GetGroupSettings(key) with { Enabled = enabled };
        await _store.SetGroupSettingsAsync(key, settings).ConfigureAwait(false);
        await ReplyTextAsync(message, enabled ? "本群表情采集已开启。" : "本群表情采集已关闭。").ConfigureAwait(false);
    }

    private Task ReplyTextAsync(MessageEvent message, string text)
    {
        if (message.IsDirect)
            return Context.Message.ReplyAsync(message, text);

        var segments = new List<MessageSegment>();
        if (_config.Meme.SendWithMention)
            segments.Add(new MentionSegment(message.Sender.Id));
        segments.Add(new TextSegment(text));
        return Context.Message.SendMessageAsync(message.Channel, segments);
    }

    private MemeSendTarget BuildTarget(MessageEvent message) =>
        new(
            message.Platform,
            message.InstanceId,
            message.Channel.Type.ToString(),
            message.Channel.Id,
            message.MessageId,
            message.Sender.Id,
            !message.IsDirect,
            _config.Meme.SendWithMention,
            _config.Meme.SendWithQuote);

    /// <summary>
    /// 通过 AI 聊天插件的契约提供表情工具：工具只选图，回复后动作在 AI 聊天发出文字后发送。
    /// 运行时双方只交换 JSON，AI 聊天插件不在时这些服务没人调用，本插件随时可以卸载和更新。
    /// </summary>
    private void RegisterChatServices()
    {
        ChatTools.AfterReply.Provide(Context, ChatToolName, HandleAfterReplyAsync, new ChatAfterReplyInfo("发送选中的表情"));
        UpdateChatTool();
    }

    private const string ChatToolName = "send_meme";

    /// <summary>借用 AI 聊天插件的识图模型做标注；AI 聊天插件不在时返回 null。</summary>
    private async Task<string?> TagWithAiChatModelAsync(string prompt, byte[] image, string mime, string? model, CancellationToken cancellationToken)
    {
        var models = await ChatModels.List.TryCallAsync(Context, cancellationToken).ConfigureAwait(false);
        string? path = null;
        try
        {
            ChatModelImage input;
            if (models?.SupportsImageFiles == true)
            {
                var temporary = Context.CreateTempDirectory(TimeSpan.FromMinutes(10));
                path = Path.Combine(temporary.Path, "tagging-image");
                await File.WriteAllBytesAsync(path, image, cancellationToken).ConfigureAwait(false);
                input = new ChatModelImage(mime, FilePath: path);
            }
            else input = new ChatModelImage(mime, Convert.ToBase64String(image));
            var reply = await ChatModels.Complete.TryCallAsync(Context, new ChatModelRequest(
                [new ChatModelMessage("user", prompt, [input])],
                model, NeedsVision: true, MaxOutputTokens: 2048, Temperature: 0), cancellationToken).ConfigureAwait(false);
            return reply?.Text;
        }
        finally
        {
            if (path is not null)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>注册表情工具；是否调用由 AIChat 的模型和人设决定。</summary>
    private void UpdateChatTool()
    {
        _chatTool?.Dispose();
        _chatTool = null;
        _saveChatTool?.Dispose();
        _saveChatTool = null;
        if (!_config.Meme.Enabled || !_config.Meme.AiToolEnabled) return;
        _chatTool = ChatTools.Tools.Provide(Context, ChatToolName, HandleChatToolAsync, ChatToolInfoFor());
        _saveChatTool = ChatTools.Tools.Provide(Context, "save_meme", HandleSaveMemeAsync, SaveToolInfo());
    }

    internal static ChatToolInfo ChatToolInfoFor()
    {
        return new ChatToolInfo(
            "Select one local meme matching the supplied keyword and/or emotion and send it after the current reply. " +
            "Returns unavailable when no matching meme exists or the conversation is in cooldown.",
            PluginJsonSchema.For<SendMemeToolArgs>(),
            MaxCallsPerTurn: 1);
    }

    /// <summary>send_meme 工具：选一张表情，要求 AI 聊天在回复后调用同名的回复后动作发送。</summary>
    private async Task<ChatToolResult> HandleChatToolAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        var args = call.Arguments.As<SendMemeToolArgs>();
        var query = args?.Query?.Trim() ?? "";
        var emotion = args?.Emotion?.Trim();
        if (query.Length == 0 && string.IsNullOrWhiteSpace(emotion))
            return new ChatToolResult("query or emotion is required.");

        var selection = await SelectAsync(new MemeQuery(query, emotion, call.Context.ChannelId, call.Context.SenderId), cancellationToken)
            .ConfigureAwait(false);
        if (selection is null)
            return new ChatToolResult("No matching meme is available, or this conversation is in cooldown. No image has been queued.");

        var content = $"Selected a meme ({selection.Caption}; tags: {string.Join(", ", selection.Tags)}). " +
                      "It is queued for sending after your reply.";
        return new ChatToolResult(content,
            new ChatAfterReply(ChatToolName, PluginJson.ToNode(new SendMemeArgs(selection.Id, args?.Caption))!.AsObject()));
    }

    /// <summary>
    /// 回复后发送选中的表情。AI 聊天已经发了文字时只发图；没发文字时带上配文，并按 Reply.Mention / Reply.Quote @、引用。
    /// </summary>
    private async Task<ChatAfterReplyResult> HandleAfterReplyAsync(ChatAfterReplyCall call, CancellationToken cancellationToken)
    {
        var args = call.Arguments.As<SendMemeArgs>();
        var selection = args is null ? null : _store?.GetById(args.Id);
        if (selection is null) return new ChatAfterReplyResult(false, "未找到该表情");

        var caption = call.Reply.TextSent ? "" : args!.Caption?.Trim() ?? "";
        var context = call.Context;
        var target = new MemeSendTarget(context.Platform, context.InstanceId, context.ChannelType, context.ChannelId,
            context.MessageId, context.SenderId, context.IsGroup, call.Reply.Mention, call.Reply.Quote);
        var result = await SendAsync(selection, target, caption, cancellationToken).ConfigureAwait(false);
        return new ChatAfterReplyResult(result.Success, result.Error,
            result.Success ? caption.Length > 0 ? caption : $"[表情：{selection.Caption}]" : null);
    }

    public async Task<MemeSelection?> SelectAsync(MemeQuery query, CancellationToken cancellationToken = default)
    {
        if (!_config.Meme.Enabled || _store is null) return null;

        var key = string.IsNullOrWhiteSpace(query.ChannelId) ? "global" : query.ChannelId;
        if (IsBlocked(key)) return null;

        string[] recentIds;
        lock (_recent)
            recentIds = _recent.TryGetValue(key, out var recent) ? recent.ToArray() : [];

        var excluded = recentIds.ToHashSet(StringComparer.Ordinal);
        var candidates = SearchAvailable(excluded);
        if (candidates.Count == 0 && recentIds.Length > 0)
        {
            // 小库可能全部落在最近 30 张内；开启下一轮，仍避免连续发送上一张。
            excluded = new HashSet<string>(StringComparer.Ordinal) { recentIds[^1] };
            candidates = SearchAvailable(excluded);
        }

        // 库里只有一张图片时仍允许发送。
        if (candidates.Count == 0)
            candidates = SearchAvailable(null);
        if (candidates.Count == 0) return null;
        return candidates[Random.Shared.Next(candidates.Count)];

        IReadOnlyList<MemeSelection> SearchAvailable(IReadOnlySet<string>? excludedIds)
        {
            var limit = Math.Max(1, query.Limit);
            return _store.Search(query.Query, query.Emotion, limit, excludedIds);
        }
    }

    public async Task<MemeSendResult> SendAsync(
        MemeSelection selection,
        MemeSendTarget target,
        string? caption = null,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Meme.Enabled || _sender is null || _store is null)
            return new MemeSendResult(false, Error: "表情库未启用");

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = string.IsNullOrWhiteSpace(target.ChannelId) ? "global" : target.ChannelId;
            if (IsBlocked(key))
                return new MemeSendResult(false, Error: "本会话表情发送正在冷却或已达每小时上限");

            var result = await _sender.SendAsync(selection, target, caption, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success) return result;

            await _store.MarkUsedAsync(selection.Id, cancellationToken).ConfigureAwait(false);

            lock (_usage)
            {
                if (!_usage.TryGetValue(key, out var usage))
                    _usage[key] = usage = new ChannelUsage();
                var now = DateTimeOffset.UtcNow;
                usage.LastSent = now;
                usage.SentAt.Enqueue(now);
                while (usage.SentAt.Count > 0 && now - usage.SentAt.Peek() > TimeSpan.FromHours(1))
                    usage.SentAt.Dequeue();
            }

            lock (_recent)
            {
                if (!_recent.TryGetValue(key, out var recent))
                    _recent[key] = recent = new Queue<string>();
                recent.Enqueue(selection.Id);
                while (recent.Count > 30)
                    recent.Dequeue();
            }

            return result;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task<MemeLibraryStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store?.GetStats() ?? new MemeLibraryStats(0, 0, null));

    private bool IsBlocked(string key)
    {
        lock (_usage)
        {
            if (!_usage.TryGetValue(key, out var usage)) return false;
            var now = DateTimeOffset.UtcNow;
            while (usage.SentAt.Count > 0 && now - usage.SentAt.Peek() > TimeSpan.FromHours(1))
                usage.SentAt.Dequeue();

            if (_config.Meme.CooldownSeconds > 0 &&
                usage.LastSent != default &&
                now - usage.LastSent < TimeSpan.FromSeconds(_config.Meme.CooldownSeconds))
                return true;

            return _config.Meme.MaxPerHour > 0 && usage.SentAt.Count >= _config.Meme.MaxPerHour;
        }
    }

    private const string HelpText = """
        #meme 用法：
        #meme list [关键词]     查看数量
        #meme random [关键词]   随机发送一张
        #meme save              引用图片/商城表情后收录
        #meme add <url> [标签]  管理员添加外部图片
        #meme tag <ID> <标签>   管理员修改标签
        #meme del <ID>          管理员删除
        #meme off / on          管理员开关本群自动采集
        #meme stats             查看容量
        #meme retag [all]       管理员：AI 重新标注空泛标签；all 标注全部
        #meme clean [confirm]   管理员：预览/删除 AI 判定为截图、照片的内容
        """;

    private sealed class ChannelUsage
    {
        public DateTimeOffset LastSent { get; set; }
        public Queue<DateTimeOffset> SentAt { get; } = new();
    }
}
