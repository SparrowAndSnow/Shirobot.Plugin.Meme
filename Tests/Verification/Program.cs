using System.Reflection;
using ShiroBot.Meme.Contracts;
using ShiroBot.MemePlugin;
using ShiroBot.MemePlugin.Config;
using ShiroBot.MemePlugin.Meme;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Config;
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
    IMemeService? registered = null;
    ((TestProxy)(object)services).Call = (method, args) =>
    {
        if (method.Name == "RegisterSingleton" && args is { Length: > 0 })
            registered = args[0] as IMemeService;
        return null;
    };

    var bot = DispatchProxy.Create<IBotContext, TestProxy>();
    ((TestProxy)(object)bot).Call = (method, args) => method.Name switch
    {
        "get_Config" => configContext,
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
    Check(registered is not null, "plugin registers IMemeService");

    var storeField = typeof(MemePlugin).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var store = (MemeStore)storeField.GetValue(plugin)!;
    var item = await store.AddAsync(png, "image/png", "猫猫", ["猫"], ["happy"],
        new MemeSource { Platform = "qq", ChannelId = "g1" }, null);
    Check(item is not null, "plugin store add");

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

    var stats = await plugin.GetStatsAsync();
    Check(stats.Count == 1, "plugin stats");
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
