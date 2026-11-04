using System.Reflection;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.MemePlugin;
using ShiroBot.MemePlugin.Config;
using ShiroBot.MemePlugin.Contracts;
using ShiroBot.MemePlugin.Meme;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

var failed = 0;

void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS" : "FAIL") + ": " + name);
    if (!condition) failed++;
}

static string TempRoot(string prefix)
{
    var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

var png = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII=");

// ---- store ----
{
    var root = TempRoot("meme-store-");
    var config = new MemeSection { MaxBytes = 5_000_000, MaxItems = 100, MaxTotalBytes = 50_000_000 };
    var store = new MemeStore(root, config);
    await store.LoadAsync();
    var item = await store.AddAsync(png, "image/png", "猫猫", ["猫", "开心"], ["happy"],
        new MemeSource { Platform = "qq", ChannelId = "g1", SenderId = "u1", MessageId = "m1" }, null);
    Check(item is not null && item.Tags.Contains("猫"), "store add and tags");
    var duplicate = await store.AddAsync(png, "image/png", "duplicate", ["重复"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    Check(duplicate?.Id == item!.Id && store.GetStats().Count == 1, "store dedupe by SHA-256");
    Check(store.Search("猫", null, 8).Count == 1, "store search by tag");
    Check(store.CountMatching("猫", null) == 1 && store.CountMatching("不存在", null) == 0,
        "store count matching");
    Check(store.FindExisting(png, null)?.Id == item!.Id, "store find existing before AI tagging");
    Check(await store.UpdateMetadataAsync(item!.Id, "AI 猫猫", ["猫猫", "搞笑"], ["happy"], "meme"),
        "store update AI metadata");
    Check(store.Search("搞笑", null, 8).Count == 1, "store search AI tag");
    Check(store.GetItems().First(i => i.Id == item.Id).Kind == "meme", "store persists AI kind");
    Check(store.Search("", "happy", 8).Count == 1, "store search by emotion");
    Check(store.GetById(item!.Id[..8])?.Id == item.Id, "store unique ID prefix lookup");
    Check(await store.UpdateTagsAsync(item!.Id, ["猫", "无语"]), "store update tags");
    Check(store.Search("无语", null, 8).Count == 1, "store search updated tag");
    Check(await store.MarkUsedAsync(item.Id), "store mark used");
    Check(store.GetStats().Count == 1 && store.GetStats().Bytes == png.Length, "store stats");

    var reloaded = new MemeStore(root, config);
    await reloaded.LoadAsync();
    Check(reloaded.Search("猫", null, 8).Count == 1, "store reload index");
    reloaded.Dispose();

    Check(await store.RemoveAsync(item.Id), "store remove");
    Check(store.GetStats().Count == 0, "store remove updates stats");
    store.Dispose();

    await File.WriteAllTextAsync(Path.Combine(root, "index.json"), "{not-json");
    var corrupt = new MemeStore(root, config);
    await corrupt.LoadAsync();
    Check(corrupt.GetStats().Count == 0, "corrupt index starts empty");
    Check(Directory.EnumerateFiles(root, "index.json.corrupt-*").Any(), "corrupt index backed up");
    corrupt.Dispose();
}


// ---- perceptual dedupe ----
{
    var root = TempRoot("meme-visual-");
    var config = new MemeSection
    {
        MaxBytes = 5_000_000, MaxItems = 100, MaxTotalBytes = 50_000_000,
        PerceptualThreshold = 5,
    };
    var store = new MemeStore(root, config);
    await store.LoadAsync();

    var bmpA = CreateBmp(64, 64, false);
    var bmpB = CreateBmp(64, 64, false);
    bmpB[6] = 1; // 同一画面，仅 BMP 保留字段不同
    var first = await store.AddAsync(bmpA, "image/bmp", "视觉图 A", ["图"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    var similar = await store.AddAsync(bmpB, "image/bmp", "视觉图 B", ["图"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    Check(first is not null && similar?.Id == first.Id && store.GetStats().Count == 1,
        "store dedupe visually similar image");

    var different = await store.AddAsync(CreateBmp(64, 64, true), "image/bmp", "视觉图 C", ["图"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    Check(different is not null && different.Id != first!.Id && store.GetStats().Count == 2,
        "store keeps visually different image");

    var red = await store.AddAsync(CreateSolidBmp(32, 32, 255, 0, 0), "image/bmp", "红", ["图"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    var blue = await store.AddAsync(CreateSolidBmp(32, 32, 0, 0, 255), "image/bmp", "蓝", ["图"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    Check(red is not null && blue is not null && blue.Id != red.Id && store.GetStats().Count == 4,
        "store does not merge different solid colors");
    store.Dispose();
}

// ---- market face dedupe ----
{
    var root = TempRoot("meme-market-");
    var config = new MemeSection { MaxBytes = 5_000_000, MaxItems = 100, MaxTotalBytes = 50_000_000 };
    var store = new MemeStore(root, config);
    await store.LoadAsync();
    var marketFace = new MarketFaceInfo("e1", 2, "key", "商城表情", "https://example.com/a.png");
    var first = await store.AddAsync(png, "image/png", "商城表情", ["商城表情"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, marketFace);
    var second = await store.AddAsync(CreateBmp(32, 32, false), "image/bmp", "商城表情", ["商城表情"], ["daily"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, marketFace);
    Check(first is not null && second?.Id == first.Id && store.GetStats().Count == 1,
        "store dedupe market face by emoji key");
    store.Dispose();
}

// ---- AI tagger ----
{
    var config = new MemeSection { CaptionMaxChars = 120, AiMaxTags = 8, AiInferEmotions = true };
    var json = """{"choices":[{"message":{"content":"{\"caption\":\"猫猫托腮\",\"tags\":[\"猫\",\"托腮\",\"表情包\"],\"emotions\":[\"happy\",\"daily\"],\"kind\":\"meme\"}"}}]}""";
    var parsed = MemeAiTagger.Parse(json, config);
    Check(parsed is not null && parsed.Caption == "猫猫托腮" &&
          parsed.Tags.Contains("猫") && !parsed.Tags.Contains("表情包") &&
          parsed.Emotions.Contains("happy") && parsed.IsMeme, "AI tagger parses JSON, kind and filters generic tags");
    Check(MemeAiTagger.Parse("{\"choices\":[]}", config) is null, "AI tagger rejects empty choices");
    Check(MemeAiTagger.ParseContent("好的，结果如下：{\"caption\":\"狗\",\"tags\":[\"狗\"],\"kind\":\"meme\"} 以上", config)?.Caption == "狗",
        "AI tagger finds the JSON after leading text");

    // 模型来源：auto 有接口地址时用自己的接口，否则借用 AI 聊天插件；aichat / custom 固定。
    Check(MemeAiTagger.UsesCustomEndpoint(new MemeSection { AiProvider = "auto", AiBaseUrl = "http://x/v1" }) &&
          !MemeAiTagger.UsesCustomEndpoint(new MemeSection { AiProvider = "auto" }) &&
          !MemeAiTagger.UsesCustomEndpoint(new MemeSection { AiProvider = "aichat", AiBaseUrl = "http://x/v1" }) &&
          MemeAiTagger.UsesCustomEndpoint(new MemeSection { AiProvider = "custom" }),
        "AI tagger picks its model source from ai_provider");

    var borrowed = new MemeSection { AiTaggingEnabled = true, AiMaxTags = 8, AiInferEmotions = true, AiModel = "vision" };
    using var tagger = new MemeAiTagger(borrowed, new NetworkSection());
    Check(!tagger.IsConfigured, "Borrowing AIChat needs the AIChat bridge");
    (string Prompt, byte[] Image, string Mime, string? Model)? asked = null;
    tagger.AiChat = (prompt, image, mime, model, _) =>
    {
        asked = (prompt, image, mime, model);
        return Task.FromResult<string?>("{\"caption\":\"猫\",\"tags\":[\"猫\"],\"emotions\":[\"happy\"],\"kind\":\"meme\"}");
    };
    var tags = await tagger.TagAsync([1, 2, 3], "image/png", "上下文");
    Check(tagger.IsConfigured && tags is { Caption: "猫", IsMeme: true } && asked is { Mime: "image/png", Model: "vision" } &&
          asked.Value.Image.Length == 3 && asked.Value.Prompt.Contains("上下文"),
        "AI tagger borrows the AIChat model with the image, prompt and configured model");
    tagger.AiChat = (_, _, _, _, _) => Task.FromResult<string?>(null);
    Check(await tagger.TagAsync([1], "image/png", "") is null, "AI tagger skips tagging while AIChat is missing");
}

// ---- ingest queue ----
{
    var root = TempRoot("meme-queue-");
    var config = new MemeSection
    {
        MaxBytes = 5_000_000, MaxItems = 100, MaxTotalBytes = 50_000_000,
        PerceptualThreshold = 0,
    };
    var store = new MemeStore(root, config);
    await store.LoadAsync();
    using var downloader = new MemeDownloader(config, new NetworkSection());
    var queue = new MemeIngestQueue(store, downloader);
    queue.TryEnqueue(new MemeIngestJob
    {
        Platform = "qq",
        ChannelId = "g1",
        Url = "base64://" + Convert.ToBase64String(png),
        Summary = "猫猫",
        MessageText = "猫猫",
        StickerOnly = false,
    });
    await Task.Delay(500);
    Check(store.GetStats().Count == 1, "ingest queue downloads and stores");

    var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
    var jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==");
    queue.TryEnqueue(new MemeIngestJob
    {
        Platform = "qq", ChannelId = "g1",
        Url = "base64://" + Convert.ToBase64String(gif),
        Summary = "gif", MessageText = "gif", StickerOnly = true,
    });
    queue.TryEnqueue(new MemeIngestJob
    {
        Platform = "qq", ChannelId = "g1",
        Url = "base64://" + Convert.ToBase64String(jpeg),
        Summary = "jpeg", MessageText = "jpeg", StickerOnly = true,
    });
    await Task.Delay(700);
    Check(store.GetStats().Count == 2, "sticker mode keeps GIF and skips JPEG");
    await queue.DisposeAsync();
    store.Dispose();
}

// ---- tagging ----
{
    Check(MemeTagging.GuessEmotions("哈哈哈 太可爱了").Contains("happy"), "emotion happy");
    Check(MemeTagging.GuessEmotions("无语 问号").Contains("confused"), "emotion confused");
    Check(MemeTagging.GuessEmotions("普通一句话").Contains("daily"), "emotion fallback daily");
    var tags = MemeTagging.ExtractTags("猫猫 无语", "猫猫表情",
        new MarketFaceInfo("e1", 2, "key", "商城表情", "https://example.com/a.png"));
    Check(tags.Contains("猫猫") && tags.Contains("商城表情"), "tag extraction");
    var imageTags = MemeTagging.ExtractTags("", "[图片]", null);
    Check(imageTags.Contains("表情") && imageTags.Contains("图片"), "generic image tags");
    Check(MemeTagging.BuildCaption("[图片]", "", 120) == "表情包", "generic image caption");
    Check(MemeTagging.IsStickerLike("sticker", null), "sticker subtype detection");
    Check(MemeTagging.IsStickerLike(null, "image/gif"), "animated image sticker detection");
}

// ---- downloader guards ----
{
    using var downloader = new MemeDownloader(new MemeSection(), new NetworkSection());
    Check(await downloader.DownloadAsync("http://127.0.0.1/x.png", CancellationToken.None) is null,
        "downloader blocks loopback");
    Check(await downloader.DownloadAsync("http://localhost/x.png", CancellationToken.None) is null,
        "downloader blocks localhost");
    var decoded = await downloader.DownloadAsync("base64://" + Convert.ToBase64String(png), CancellationToken.None);
    Check(decoded is not null && decoded.Data.Length == png.Length && decoded.Mime == "image/png",
        "downloader accepts base64 image");
}

// ---- collector ----
{
    var config = new MemeSection { AutoCollect = "sticker", CollectDirect = false, CaptionMaxChars = 120 };
    var message = new MessageEvent
    {
        Platform = "qq",
        InstanceId = "test",
        SelfId = "bot",
        MessageId = "m1",
        Channel = Channel.Group("g1"),
        Sender = new User("u1") { Name = "测试" },
        Timestamp = DateTimeOffset.UtcNow,
        Segments =
        [
            new RawSegment("qq", "market_face",
                new QIncomingMarketFace("e1", "https://example.com/a.png")
                {
                    EmojiPackageId = 2,
                    Key = "key",
                    Summary = "猫猫",
                }),
            new RawSegment("qq", "image",
                new QIncomingImage("r1", "https://example.com/b.png") { SubType = "sticker" }),
            new ImageSegment("https://example.com/c.png"),
        ],
    };

    var jobs = MemeCollector.BuildJobs(message, config, null, force: false);
    Check(jobs.Count == 3, "collector keeps market face and image segments");
    Check(jobs.All(j => j.StickerOnly), "collector marks sticker-only jobs");
    Check(jobs.Any(j => j.MarketFace is not null), "collector stores market face metadata");

    var direct = message with { Channel = Channel.Direct("u1") };
    Check(MemeCollector.BuildJobs(direct, config, null, false).Count == 0, "collector skips direct by default");

    var all = new MemeSection { AutoCollect = "all", CollectDirect = false, CaptionMaxChars = 120 };
    Check(MemeCollector.BuildJobs(message, all, null, false).Count == 3, "collector all mode");
    Check(MemeCollector.BuildJobs(message, config, null, force: true).Count == 3, "collector force mode");
}

// ---- plugin lifecycle, service and sender ----
{
    var root = TempRoot("meme-plugin-");
    var configPath = Path.Combine(root, "config.toml");
    var config = new MemeConfig();
    config.Meme.CooldownSeconds = 45;

    var configContext = DispatchProxy.Create<IConfigContext, TestProxy>();
    ((TestProxy)(object)configContext).Call = (method, _) => method.Name switch
    {
        "get_ConfigPath" => configPath,
        "Load" => config,
        _ => null,
    };

    var messageContext = DispatchProxy.Create<IMessageContext, TestProxy>();
    var sentSegments = new List<MessageSegment>();
    ((TestProxy)(object)messageContext).Call = (method, args) =>
    {
        if (method.Name == "SendMessageAsync")
        {
            if (args?[1] is IReadOnlyList<MessageSegment> segments)
                sentSegments.AddRange(segments);
            return Task.FromResult(new SentMessage("sent-1"));
        }
        return null;
    };

    var services = DispatchProxy.Create<IPluginServices, TestProxy>();
    // 契约的服务最终通过 ProvideUtf8 注册：按服务名记下处理函数和描述。
    var provided = new Dictionary<string, (PluginUtf8Handler Handler, ReadOnlyMemory<byte> Metadata)>();
    ((TestProxy)(object)services).Call = (method, args) =>
    {
        if (method.Name == "ProvideUtf8" && args is { Length: 3 })
            provided[(string)args[0]!] = ((PluginUtf8Handler)args[1]!, (ReadOnlyMemory<byte>)args[2]!);
        return null;
    };

    async Task<TResponse?> CallAsync<TRequest, TResponse>(string service, TRequest request)
    {
        var response = await provided[service].Handler(PluginJson.Serialize(request), CancellationToken.None);
        return PluginJson.Deserialize<TResponse>(response.Span);
    }

    var pages = new Dictionary<string, Func<DashboardContext, CancellationToken, Task<UiNode>>>();
    var dashboardRegistration = DispatchProxy.Create<IPluginDashboard, TestProxy>();
    ((TestProxy)(object)dashboardRegistration).Call = (method, args) =>
    {
        if (method.Name == "AddPage") pages[(string)args![0]!] = (Func<DashboardContext, CancellationToken, Task<UiNode>>)args[2]!;
        return null;
    };
    var bot = DispatchProxy.Create<IBotContext, TestProxy>();
    ((TestProxy)(object)bot).Call = (method, args) => method.Name switch
    {
        "get_Config" => configContext,
        "get_Dashboard" => dashboardRegistration,
        "get_PluginDirectory" => root,
        "get_Services" => services,
        "get_Message" => messageContext,
        "get_Platform" => "qq",
        "get_InstanceId" => "test",
        "GetAdapterExtension" => null,
        "UseInstance" => new DummyDisposable(),
        "IsAdmin" => true,
        "get_OwnerList" => Array.Empty<UserReference>(),
        "get_AdminList" => Array.Empty<UserReference>(),
        "get_AdapterInstance" => null,
        "GetAdapterInstances" => Array.Empty<AdapterInstanceInfo>(),
        _ => null,
    };

    var plugin = new MemePlugin();
    await plugin.OnLoad(bot);
    var toolInfo = PluginJson.Deserialize<ChatToolInfo>(provided.GetValueOrDefault("chat.tool.send_meme").Metadata.Span);
    Check(provided.ContainsKey("chat.after_reply.send_meme") && toolInfo is { MaxCallsPerTurn: 1 } &&
          (string?)toolInfo.Parameters?["properties"]?["query"]?["type"] == "string" && toolInfo.Prompt?.Contains("明确要求") == true,
        "plugin provides the send_meme chat tool and after-reply action through the AIChat contract");

    var storeField = typeof(MemePlugin).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var store = (MemeStore)storeField.GetValue(plugin)!;
    var item = await store.AddAsync(png, "image/png", "猫猫", ["猫"], ["happy"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    Check(item is not null, "plugin store add");

    var contractSelection = await CallAsync<MemeSelectRequest, MemeSelectResponse>(MemeLibrary.Select.Name,
        new MemeSelectRequest("猫", ChannelId: "contract-group"));
    Check(contractSelection?.Meme is { } chosen && chosen.Id == item!.Id && chosen.Mime == "image/png" &&
          PluginJson.Serialize(contractSelection).AsSpan().IndexOf("file_path"u8) < 0,
        "exported library contract selects metadata without exposing file paths");
    var contractTarget = new MemeDestination("qq", "Group", "contract-group", "test", SenderId: "u1", Mention: true);
    var invalidContractSend = await CallAsync<MemeSendRequest, MemeSendResponse>(MemeLibrary.Send.Name,
        new MemeSendRequest(item!.Id[..8], contractTarget));
    Check(invalidContractSend is { Ok: false } && sentSegments.Count == 0,
        "library contract rejects abbreviated IDs before sending");
    var invalidTargetSend = await CallAsync<MemeSendRequest, MemeSendResponse>(MemeLibrary.Send.Name,
        new MemeSendRequest(item.Id, contractTarget with { ChannelType = "invalid" }));
    Check(invalidTargetSend is { Ok: false } && sentSegments.Count == 0,
        "library contract rejects invalid destinations");
    var contractSend = await CallAsync<MemeSendRequest, MemeSendResponse>(MemeLibrary.Send.Name,
        new MemeSendRequest(item.Id, contractTarget, "契约测试"));
    Check(contractSend is { Ok: true, MessageId: "sent-1" } && sentSegments.OfType<MentionSegment>().Any(),
        "exported library contract sends the stored image by ID");
    Check((await CallAsync<MemeSelectRequest, MemeSelectResponse>(MemeLibrary.Select.Name,
        new MemeSelectRequest("猫", ChannelId: "contract-group"))) is { Meme: null },
        "library contract honors per-conversation cooldown");
    var statsJson = await provided[MemeLibrary.Stats.Name].Handler(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
    Check(PluginJson.Deserialize<MemeStats>(statsJson.Span) is { Enabled: true, Count: 1 },
        "exported library contract provides statistics");
    config.Meme.Enabled = false;
    Check((await CallAsync<MemeSelectRequest, MemeSelectResponse>(MemeLibrary.Select.Name,
        new MemeSelectRequest("猫", ChannelId: "disabled-group"))) is { Meme: null } &&
        (await CallAsync<MemeSendRequest, MemeSendResponse>(MemeLibrary.Send.Name,
            new MemeSendRequest(item.Id, contractTarget with { ChannelId = "disabled-group" }))) is { Ok: false, Error: "表情库未启用" },
        "disabled library cannot be used through plugin contracts");
    config.Meme.Enabled = true;
    using (var canceledContract = new CancellationTokenSource())
    {
        canceledContract.Cancel();
        try
        {
            await provided[MemeLibrary.Select.Name].Handler(PluginJson.Serialize(new MemeSelectRequest("猫")), canceledContract.Token);
            Check(false, "library contract cancellation propagated");
        }
        catch (OperationCanceledException) { Check(true, "library contract cancellation propagated"); }
    }
    sentSegments.Clear();

    var fallback = await plugin.SelectAsync(new MemeQuery("不存在的关键词", ChannelId: "g1"));
    Check(fallback is not null, "plugin selection falls back to library");
    var selection = await plugin.SelectAsync(new MemeQuery("猫", ChannelId: "g1"));
    Check(selection is not null && selection.Id == item!.Id, "plugin select");

    var target = new MemeSendTarget("qq", "test", "Group", "g1", "m1", "u1", true, false, false);
    var result = await plugin.SendAsync(selection!, target, "猫猫来了");
    Check(result.Success && result.MessageId == "sent-1", "plugin send");
    Check(sentSegments.OfType<ImageSegment>().Any(), "sender uses image segment");
    Check(sentSegments.OfType<TextSegment>().Any(t => t.Text.Contains("猫猫来了")), "sender includes caption");
    Check((await plugin.SelectAsync(new MemeQuery("猫", ChannelId: "g1"))) is null, "cooldown blocks second select");

    // AI 聊天工具：只选图并要求回复后发送；没有文字时带配文、按要求 @。
    var chat = new ChatToolContext("qq", "test", "Group", "g2", "m2", "u2", true, false);
    var sentBefore = sentSegments.Count;
    var toolResult = await CallAsync<ChatToolCall, ChatToolResult>("chat.tool.send_meme",
        new ChatToolCall(new JsonObject { ["query"] = "猫", ["caption"] = "喵" }, chat));
    var after = toolResult?.AfterReply;
    Check(after is { Name: "send_meme" } && (string?)after.Arguments?["id"] == item!.Id && sentSegments.Count == sentBefore,
        "send_meme tool selects without sending");
    sentSegments.Clear();
    var sendResult = await CallAsync<ChatAfterReplyCall, ChatAfterReplyResult>("chat.after_reply.send_meme",
        new ChatAfterReplyCall(after!.Arguments, chat, new ChatReplyState(TextSent: false, Mention: true, Quote: false)));
    Check(sendResult is { Ok: true, History: "喵" } &&
          sentSegments.OfType<MentionSegment>().Any() && sentSegments.OfType<TextSegment>().Any(t => t.Text.Contains("喵")),
        "the after-reply action sends with caption and mention when no reply text was sent");

    var stats = await plugin.GetStatsAsync();
    Check(stats.Count == 1, "plugin stats");
    // Dashboard uses the host contract, with no public library routes or signed image URLs.
    Check(pages.ContainsKey("library"), "library page registered through Context.Dashboard");
    async Task<UiLayout> Render(Dictionary<string, string>? state = null) =>
        (UiLayout)await pages["library"](new DashboardContext("library", state ?? []), CancellationToken.None);
    UiCollection Collection(UiLayout tree) => tree.Children.OfType<UiCollection>().Single();
    var gallery = await Render(new() { ["q"] = "猫", ["emotion"] = "happy", ["tag"] = "猫" });
    Check(Collection(gallery).Rows() is [{ Key: var key, Card.Image: var imageId }] && key == item!.Id && imageId == item.Id &&
        gallery.Children.OfType<UiPager>().Single().Total == 1, "component tree filters and opaque image IDs");
    var empty = await Render(new() { ["page"] = int.MaxValue.ToString() });
    Check(Collection(empty).Rows().Count == 0, "dashboard page overflow does not wrap to first page");
    var image = await plugin.OpenLibraryImageAsync(item!.Id);
    Check(image?.Path == selection!.FilePath && image.ContentType == "image/png", "dashboard file resolver serves library image");
    var e = new DashboardEvent(new DashboardContext("library", new Dictionary<string, string>()), "gallery");
    Check(Collection(gallery).Invoke("unknown", item.Id, e, CancellationToken.None) is null &&
          Collection(gallery).Invoke("delete", item.Id[..8], e, CancellationToken.None) is null,
        "component action rejects unknown actions and abbreviated IDs");
    var deleted = await Collection(gallery).Invoke("delete", item.Id, e, CancellationToken.None)!;
    Check(deleted is { Ok: true, Refresh: true } && await plugin.OpenLibraryImageAsync(item.Id) is null,
        "component delete refreshes and removes image");

    var dashboard = plugin;
    Check((await Render()).Children.OfType<UiUpload>().Single() is { MaxBytes: 5000000 } descriptor && descriptor.Accept.Contains("image/webp"),
        "dashboard declares configured image upload limits");
    var invalid = await dashboard.ImportLibraryImageAsync( new DashboardUploadFile("fake.png", "image/png", "<script>bad</script>"u8.ToArray()));
    Check(!invalid.Ok && store.GetStats().Count == 0, "dashboard rejects renamed non-image uploads");
    config.Meme.MaxBytes = 1;
    Check(!(await dashboard.ImportLibraryImageAsync( new DashboardUploadFile("too-large.png", "image/png", png))).Ok &&
          (await Render()).Children.OfType<UiUpload>().Single().MaxBytes == 1, "dashboard upload follows the current size limit");
    config.Meme.MaxBytes = 5_000_000;
    config.Meme.Enabled = false;
    Check(!(await Render()).Children.OfType<UiUpload>().Any() && !(await dashboard.ImportLibraryImageAsync( new DashboardUploadFile("disabled.png", "image/png", png))).Ok,
        "disabled meme library does not accept uploads");
    config.Meme.Enabled = true;
    var tagger = (MemeAiTagger)typeof(MemePlugin).GetField("_tagger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
    config.Meme.AiTaggingEnabled = true;
    config.Meme.AiProvider = "aichat";
    config.Meme.AiFilterNonMeme = true;
    var taggingCalls = 0;
    tagger.AiChat = (_, _, _, _, _) =>
    {
        taggingCalls++;
        return Task.FromResult<string?>("""{"caption":"上传测试","tags":["上传"],"emotions":["happy"],"kind":"other"}""");
    };
    var uploaded = await dashboard.ImportLibraryImageAsync( new DashboardUploadFile("test.jpg", "image/jpeg", png));
    var uploadedItem = store.GetItems().Single();
    Check(uploaded is { Ok: true, Refresh: true } && uploadedItem.Mime == "image/png" && uploadedItem.Caption == "上传测试" &&
          uploadedItem.Tags.Contains("上传") && uploadedItem.Kind == "other", "manual upload sniffs content and keeps intentionally uploaded images with AI tags");
    Check((await File.ReadAllBytesAsync(store.GetById(uploadedItem.Id)!.FilePath)).SequenceEqual(png),
        "uploaded image bytes are preserved without re-encoding");
    var duplicateUpload = await dashboard.ImportLibraryImageAsync( new DashboardUploadFile("same.png", "image/png", png));
    Check(duplicateUpload.Ok && store.GetStats().Count == 1 && taggingCalls == 1, "repeated uploads deduplicate before paid AI tagging");
    using (var canceledUpload = new CancellationTokenSource())
    {
        canceledUpload.Cancel();
        try
        {
            await dashboard.ImportLibraryImageAsync( new DashboardUploadFile("canceled.png", "image/png", png), canceledUpload.Token);
            Check(false, "upload cancellation propagated");
        }
        catch (OperationCanceledException) { Check(true, "upload cancellation propagated"); }
    }

    await plugin.OnUnload();
    Check(File.Exists(Path.Combine(root, "config.toml")), "default config template written");
}



static byte[] CreateSolidBmp(int width, int height, byte r, byte g, byte b)
{
    var stride = ((width * 3 + 3) / 4) * 4;
    var size = 54 + stride * height;
    var bytes = new byte[size];
    bytes[0] = (byte)'B';
    bytes[1] = (byte)'M';
    BitConverter.GetBytes(size).CopyTo(bytes, 2);
    BitConverter.GetBytes(54).CopyTo(bytes, 10);
    BitConverter.GetBytes(40).CopyTo(bytes, 14);
    BitConverter.GetBytes(width).CopyTo(bytes, 18);
    BitConverter.GetBytes(height).CopyTo(bytes, 22);
    BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
    BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
    BitConverter.GetBytes(stride * height).CopyTo(bytes, 34);
    for (var y = 0; y < height; y++)
    {
        var row = 54 + (height - 1 - y) * stride;
        for (var x = 0; x < width; x++)
        {
            var offset = row + x * 3;
            bytes[offset] = b;
            bytes[offset + 1] = g;
            bytes[offset + 2] = r;
        }
    }
    return bytes;
}

static byte[] CreateBmp(int width, int height, bool invert)
{
    var stride = ((width * 3 + 3) / 4) * 4;
    var size = 54 + stride * height;
    var bytes = new byte[size];
    bytes[0] = (byte)'B';
    bytes[1] = (byte)'M';
    BitConverter.GetBytes(size).CopyTo(bytes, 2);
    BitConverter.GetBytes(54).CopyTo(bytes, 10);
    BitConverter.GetBytes(40).CopyTo(bytes, 14);
    BitConverter.GetBytes(width).CopyTo(bytes, 18);
    BitConverter.GetBytes(height).CopyTo(bytes, 22);
    BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
    BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
    BitConverter.GetBytes(stride * height).CopyTo(bytes, 34);
    for (var y = 0; y < height; y++)
    {
        var row = 54 + (height - 1 - y) * stride;
        for (var x = 0; x < width; x++)
        {
            var white = (((x / 8) + (y / 8)) % 2 == 0) ^ invert;
            var value = white ? (byte)255 : (byte)0;
            var offset = row + x * 3;
            bytes[offset] = value;
            bytes[offset + 1] = value;
            bytes[offset + 2] = value;
        }
    }
    return bytes;
}

failed += await SaveToolVerification.RunAsync();

Console.WriteLine(failed == 0 ? "ALL CHECKS PASSED" : $"{failed} CHECK(S) FAILED");
return failed == 0 ? 0 : 1;

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = (_, _) => null;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
}

public sealed class DummyDisposable : IDisposable
{
    public void Dispose() { }
}
