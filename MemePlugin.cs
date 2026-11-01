using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using ShiroBot.Meme.Contracts;
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
    Author = "ShirokaProject",
    Category = PluginCategory.Media,
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ;ShiroBot.Meme.Contracts")]
public sealed class MemePlugin : PluginBase<MemeConfig>, IMemeService, IPluginDashboardTabProvider
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
        library_token = ""
        ai_tagging_enabled = false
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

    /// <summary>Dashboard-owned gallery tab; the page itself stays a plugin route.</summary>
    public IReadOnlyList<DashboardTabDescriptor> Tabs
    {
        get
        {
            var token = _config.Meme.LibraryToken;
            var tokenQuery = string.IsNullOrWhiteSpace(token) ? "" : "?token=" + Uri.EscapeDataString(token);
            var actionPath = "/plugin/MemePlugin/library/items/delete?id={id}";
            if (!string.IsNullOrWhiteSpace(token)) actionPath += "&token=" + Uri.EscapeDataString(token);

            return
            [
                new DashboardTabDescriptor(
                    Id: "library",
                    Label: "表情库",
                    Kind: DashboardTabKinds.Gallery,
                    Icon: "photo_library",
                    Description: "查看已保存的表情，支持按描述、标签或情绪筛选。",
                    Order: 55,
                    Source: new DashboardTabSource("/plugin/MemePlugin/library/items" + tokenQuery),
                    Fields: new DashboardTabFields(
                        Image: "image",
                        Title: "title",
                        Description: "description",
                        Tags: "tags"),
                    Filters:
                    [
                        new DashboardTabFilter("q", "搜索", "text"),
                        new DashboardTabFilter("tag", "标签", "text"),
                        new DashboardTabFilter("emotion", "情绪", "select",
                        [
                            new DashboardTabFilterOption("happy", "开心"),
                            new DashboardTabFilterOption("sad", "难过"),
                            new DashboardTabFilterOption("angry", "生气"),
                            new DashboardTabFilterOption("confused", "困惑"),
                            new DashboardTabFilterOption("shy", "害羞"),
                            new DashboardTabFilterOption("daily", "日常")
                        ])
                    ],
                    Pagination: new DashboardTabPagination("page", "page_size", 24),
                    Actions:
                    [
                        new DashboardTabAction(
                            "delete",
                            "删除",
                            actionPath,
                            "POST",
                            "确定删除这个表情？",
                            "danger")
                    ])
            ];
        }
    }
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
        _tagger = new MemeAiTagger(_config.Meme, _config.Network);
        _queue = new MemeIngestQueue(_store, _downloader, _tagger);
        _sender = new MemeSender(Context, _config.Meme);

        Context.Services.RegisterSingleton<IMemeService>(this);
        RegisterLibraryRoutes();
        BotLog.Info($"MemePlugin 已加载，表情库：{root}");
    }

    private void RegisterLibraryRoutes()
    {
        var webHost = Context?.WebHost;
        if (webHost is null || !webHost.IsEnabled) return;
        try
        {
            webHost.MapGet("MemePlugin", "library", ServeLibraryPageAsync);
            webHost.MapGet("MemePlugin", "library/image", ServeLibraryImage);
            webHost.MapGet("MemePlugin", "library/items", ServeLibraryItemsAsync);
            webHost.MapPost("MemePlugin", "library/items/delete", ServeLibraryDeleteAsync);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"Meme 管理页路由注册失败：{ex.Message}");
        }
    }

    private Task<IResult> ServeLibraryPageAsync(HttpContext context)
    {
        if (!LibraryTokenMatches(context)) return Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden));
        var store = _store;
        if (store is null) return Task.FromResult(Results.Text("表情库未加载。", "text/plain; charset=utf-8"));

        var query = context.Request.Query["q"].ToString().Trim();
        var items = store.GetItems()
            .Where(item => query.Length == 0 ||
                           item.Caption.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                           item.Emotions.Any(emotion => emotion.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
        var stats = store.GetStats();
        var token = _config.Meme.LibraryToken;
        var tokenQuery = string.IsNullOrWhiteSpace(token) ? "" : "?token=" + Uri.EscapeDataString(token);
        var imageTokenQuery = string.IsNullOrWhiteSpace(token) ? "" : "&token=" + Uri.EscapeDataString(token);

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>表情库 - MemePlugin</title><style>")
            .Append(":root{color-scheme:light dark;--bg:#f6f4ef;--card:#fff;--text:#1b1b1f;--muted:#68666f;--line:#dedad2;--accent:#5b5bd6}")
            .Append("@media(prefers-color-scheme:dark){:root{--bg:#17171a;--card:#222226;--text:#f0eef4;--muted:#a9a6b2;--line:#39383f;--accent:#9b9bff}}")
            .Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif}")
            .Append("header{position:sticky;top:0;z-index:2;background:color-mix(in srgb,var(--bg) 88%,transparent);backdrop-filter:blur(12px);border-bottom:1px solid var(--line);padding:14px 18px}")
            .Append("h1{margin:0;font-size:20px} .sub{color:var(--muted);font-size:12px;margin-top:3px}")
            .Append("form{display:flex;gap:8px;margin-top:10px} input{flex:1;min-width:0;border:1px solid var(--line);border-radius:10px;background:var(--card);color:var(--text);padding:8px 10px}")
            .Append("button{border:0;border-radius:10px;background:var(--accent);color:#fff;padding:8px 14px;cursor:pointer}")
            .Append("main{padding:18px;max-width:1400px;margin:0 auto}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px}")
            .Append(".card{background:var(--card);border:1px solid var(--line);border-radius:14px;overflow:hidden;display:flex;flex-direction:column}")
            .Append(".thumb{display:block;width:100%;height:180px;object-fit:contain;background:repeating-conic-gradient(#ddd 0 25%,#fff 0 50%) 50%/20px 20px}")
            .Append(".meta{padding:9px 10px;display:flex;flex-direction:column;gap:3px}.cap{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}")
            .Append(".tag{color:var(--muted);font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.id{font:11px ui-monospace,monospace;color:var(--muted)}")
            .Append(".empty{color:var(--muted);padding:60px 0;text-align:center}</style></head><body>")
            .Append("<header><h1>表情库</h1><div class=\"sub\">共 ").Append(stats.Count).Append(" 张 · ")
            .Append(stats.Bytes / 1024.0 / 1024.0).Append(" MB")
            .Append(query.Length == 0 ? "" : " · 当前筛选 " + WebUtility.HtmlEncode(query) + "：" + items.Count + " 张")
            .Append("</div><form method=\"get\"><input name=\"q\" value=\"").Append(WebUtility.HtmlEncode(query))
            .Append("\" placeholder=\"按描述/标签/情绪筛选\"><button type=\"submit\">筛选</button></form></header><main>");
        if (items.Count == 0)
        {
            html.Append("<div class=\"empty\">没有匹配的表情。</div>");
        }
        else
        {
            html.Append("<div class=\"grid\">");
            foreach (var item in items)
            {
                var imageUrl = "/plugin/MemePlugin/library/image?id=" + Uri.EscapeDataString(item.Id) + imageTokenQuery;
                html.Append("<article class=\"card\"><img class=\"thumb\" loading=\"lazy\" src=\"")
                    .Append(WebUtility.HtmlEncode(imageUrl)).Append("\" alt=\"")
                    .Append(WebUtility.HtmlEncode(item.Caption)).Append("\">")
                    .Append("<div class=\"meta\"><div class=\"cap\" title=\"")
                    .Append(WebUtility.HtmlEncode(item.Caption)).Append("\">")
                    .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(item.Caption) ? "表情" : item.Caption)).Append("</div>")
                    .Append("<div class=\"tag\">").Append(WebUtility.HtmlEncode(string.Join(" · ", item.Tags.Take(4)))).Append("</div>")
                    .Append("<div class=\"id\">").Append(WebUtility.HtmlEncode(item.Id[..Math.Min(8, item.Id.Length)]))
                    .Append(" · 使用 ").Append(item.UseCount).Append(" 次</div></div></article>");
            }
            html.Append("</div>");
        }
        html.Append("</main></body></html>");
        return Task.FromResult(Results.Content(html.ToString(), "text/html; charset=utf-8"));
    }

    private Task<IResult> ServeLibraryItemsAsync(HttpContext context)
    {
        if (!LibraryTokenMatches(context)) return Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden));
        var store = _store;
        if (store is null)
            return Task.FromResult(Results.Json(new { error = "library_not_ready", message = "表情库未加载。" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        var query = context.Request.Query["q"].ToString().Trim();
        var tag = context.Request.Query["tag"].ToString().Trim();
        var emotion = context.Request.Query["emotion"].ToString().Trim();
        var page = ParsePositiveInt(context, "page", 1);
        var pageSize = Math.Clamp(ParsePositiveInt(context, "page_size", 24), 1, 100);

        var matching = store.GetItems()
            .Where(item => query.Length == 0 ||
                           item.Caption.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Tags.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                           item.Emotions.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Where(item => tag.Length == 0 ||
                           item.Tags.Any(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase)))
            .Where(item => emotion.Length == 0 ||
                           item.Emotions.Any(value => string.Equals(value, emotion, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.CreatedAt)
            .ToList();

        var total = matching.Count;
        var pageItems = matching
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item =>
            {
                var imageQuery = string.IsNullOrWhiteSpace(_config.Meme.LibraryToken)
                    ? ""
                    : "&token=" + Uri.EscapeDataString(_config.Meme.LibraryToken);
                var description = item.Emotions.Count > 0
                    ? string.Join(" · ", item.Emotions)
                    : string.Join(" · ", item.Tags.Take(4));
                return new
                {
                    id = item.Id,
                    title = string.IsNullOrWhiteSpace(item.Caption) ? "表情" : item.Caption,
                    description,
                    image = "/plugin/MemePlugin/library/image?id=" + Uri.EscapeDataString(item.Id) + imageQuery,
                    tags = item.Tags,
                    values = new
                    {
                        kind = item.Kind,
                        bytes = item.Bytes,
                        created_at = item.CreatedAt,
                        use_count = item.UseCount,
                        emotions = item.Emotions
                    },
                    actions = new[] { "delete" }
                };
            })
            .ToArray();

        return Task.FromResult(Results.Json(new
        {
            items = pageItems,
            total,
            page,
            page_size = pageSize,
            has_more = (long)page * pageSize < total
        }));
    }

    private async Task<IResult> ServeLibraryDeleteAsync(HttpContext context)
    {
        if (!LibraryTokenMatches(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var store = _store;
        if (store is null)
            return Results.Json(new { ok = false, message = "表情库未加载。" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var id = context.Request.Query["id"].ToString();
        if (string.IsNullOrWhiteSpace(id))
            return Results.Json(new { ok = false, message = "缺少 id。" }, statusCode: StatusCodes.Status400BadRequest);

        var removed = await store.RemoveAsync(id).ConfigureAwait(false);
        return Results.Json(new { ok = removed, message = removed ? "已删除。" : "未找到该表情。" },
            statusCode: removed ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
    }

    private static int ParsePositiveInt(HttpContext context, string key, int fallback)
    {
        return int.TryParse(context.Request.Query[key].ToString(), out var value) && value > 0
            ? value
            : fallback;
    }

    private Task<IResult> ServeLibraryImage(HttpContext context)
    {
        if (!LibraryTokenMatches(context))
            return Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden));
        var id = context.Request.Query["id"].ToString();
        var selection = _store?.GetById(id);
        if (selection is null || !File.Exists(selection.FilePath))
            return Task.FromResult(Results.NotFound());
        return Task.FromResult(Results.File(selection.FilePath, selection.Mime, enableRangeProcessing: true));
    }

    private bool LibraryTokenMatches(HttpContext context)
    {
        var configured = _config.Meme.LibraryToken;
        if (string.IsNullOrWhiteSpace(configured)) return true;
        var supplied = context.Request.Query["token"].ToString();
        return string.Equals(configured, supplied, StringComparison.Ordinal);
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
            await ReplyTextAsync(message, "AI 标注未配置：请在“表情库设置”里填写 AI 接口地址、模型和 Key。").ConfigureAwait(false);
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
            try
            {
                foreach (var item in items)
                {
                    if (_lifetime.IsCancellationRequested) break;
                    var path = Path.Combine(store.FilesDirectory, item.FileName);
                    if (!File.Exists(path)) continue;

                    var data = await File.ReadAllBytesAsync(path, _lifetime.Token).ConfigureAwait(false);
                    var ai = await tagger.TagAsync(data, item.Mime,
                        $"{item.Caption} {string.Join(" ", item.Tags)}", _lifetime.Token).ConfigureAwait(false);
                    if (ai is null) continue;

                    var caption = string.IsNullOrWhiteSpace(ai.Caption) ? item.Caption : ai.Caption;
                    var tags = ai.Tags.Count > 0 ? ai.Tags : item.Tags;
                    var emotions = ai.Emotions.Count > 0 ? ai.Emotions : item.Emotions;
                    if (await store.UpdateMetadataAsync(item.Id, caption, tags, emotions, ai.Kind, _lifetime.Token).ConfigureAwait(false))
                        updated++;
                }

                BotLog.Info($"Meme AI 重新标注完成：{updated}/{items.Count} 张");
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
        if (item.Caption is "表情包" or "[图片]" or "[动画表情]" or "[image]" or "[sticker]")
            return true;
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[图片]", "[动画表情]", "[image]", "[sticker]", "表情", "图片",
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

    public async Task<MemeSelection?> SelectAsync(MemeQuery query, CancellationToken cancellationToken = default)
    {
        if (!_config.Meme.Enabled || _store is null) return null;

        var key = string.IsNullOrWhiteSpace(query.ChannelId) ? "global" : query.ChannelId;
        if (IsBlocked(key)) return null;

        var candidates = _store.Search(query.Query, query.Emotion, Math.Max(1, query.Limit));
        if (candidates.Count == 0 && (!string.IsNullOrWhiteSpace(query.Query) ||
                                      !string.IsNullOrWhiteSpace(query.Emotion)))
        {
            // 模型/用户给的关键词未必命中本地标签；库里非空时退化为随机可用表情，
            // 避免“明明有图却回复没货”。
            candidates = _store.Search("", null, Math.Max(1, query.Limit));
        }

        if (candidates.Count == 0) return null;

        lock (_recent)
        {
            if (_recent.TryGetValue(key, out var recent) && recent.Count > 0)
            {
                var filtered = candidates.Where(c => !recent.Contains(c.Id)).ToList();
                if (filtered.Count > 0) candidates = filtered;
            }
        }

        return candidates[Random.Shared.Next(candidates.Count)];
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
