using System.Reflection;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.MemePlugin;
using ShiroBot.MemePlugin.Config;
using ShiroBot.MemePlugin.Meme;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class SaveToolVerification
{
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
            if (!ok) failures++;
        }
        var root = Path.Combine(Path.GetTempPath(), "meme-save-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new MemeConfig();
        config.Meme.AutoCollect = "off";
        config.Meme.CollectGroups = ["another-group"];
        var configContext = DispatchProxy.Create<IConfigContext, TestProxy>();
        ((TestProxy)(object)configContext).Call = (m, _) => m.Name switch
        {
            "get_ConfigPath" => Path.Combine(root, "config.toml"), "Load" => config, _ => null,
        };
        var provided = new Dictionary<string, PluginUtf8Handler>();
        var services = DispatchProxy.Create<IPluginServices, TestProxy>();
        ((TestProxy)(object)services).Call = (m, a) =>
        {
            if (m.Name == "ProvideUtf8") provided[(string)a![0]!] = (PluginUtf8Handler)a[1]!;
            return null;
        };
        var messages = new Dictionary<string, MessageEvent>();
        var reads = new List<MessageReference>();
        var messageContext = DispatchProxy.Create<IMessageContext, TestProxy>();
        ((TestProxy)(object)messageContext).Call = (m, a) =>
        {
            if (m.Name != "GetMessageAsync") return null;
            var reference = (MessageReference)a![0]!;
            reads.Add(reference);
            return Task.FromResult(messages.GetValueOrDefault(reference.MessageId));
        };
        var dashboard = DispatchProxy.Create<IPluginDashboard, TestProxy>();
        var bot = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)bot).Call = (m, _) => m.Name switch
        {
            "get_Config" => configContext, "get_Dashboard" => dashboard, "get_PluginDirectory" => root,
            "get_Services" => services, "get_Message" => messageContext, "get_Platform" => "qq",
            "get_InstanceId" => "test", "UseInstance" => new DummyDisposable(),
            "get_OwnerList" or "get_AdminList" => Array.Empty<UserReference>(),
            "GetAdapterInstances" => Array.Empty<AdapterInstanceInfo>(), _ => null,
        };
        var plugin = new MemePlugin();
        await plugin.OnLoad(bot);
        try
        {
            Check(provided.ContainsKey("chat.tool.save_meme"), "save_meme registered through AIChat contract");
            var store = (MemeStore)typeof(MemePlugin).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII=");
            var message = new MessageEvent
            {
                Platform = "qq", InstanceId = "test", SelfId = "bot", MessageId = "request",
                Channel = Channel.Group("g1"), Sender = new User("u1"),
                Segments = [new ImageSegment("base64://" + Convert.ToBase64String(png))],
            };
            messages["request"] = message;
            var chat = new ChatToolContext("qq", "test", "Group", "g1", "request", "u1", true, false);
            JsonObject Args() => new() { ["caption"] = "测试猫猫", ["tags"] = new JsonArray("猫", "乖巧") };
            async Task<string> Call(ChatToolContext? context = null, JsonObject? args = null, CancellationToken ct = default)
            {
                var response = await provided["chat.tool.save_meme"](PluginJson.Serialize(new ChatToolCall(args ?? Args(), context ?? chat)), ct);
                return PluginJson.Deserialize<ChatToolResult>(response.Span)!.Content;
            }
            Check((await Call(chat with { Proactive = true })).Contains("未保存") && reads.Count == 0,
                "save tool rejects proactive calls before accessing messages");
            Check((await Call(args: new JsonObject())).Contains("未保存") && reads.Count == 0,
                "save tool requires caption and tags");
            messages["request"] = message with { Channel = Channel.Group("other") };
            Check((await Call()).Contains("未保存") && store.GetStats().Count == 0, "save tool rejects cross-channel message responses");
            messages["request"] = message;
            config.Meme.AiToolEnabled = false;
            Check((await Call()).Contains("关闭") && store.GetStats().Count == 0, "disabled save tool cannot write");
            config.Meme.AiToolEnabled = true;
            var groupKey = store.GroupKey("qq", "test", "g1");
            await store.SetGroupSettingsAsync(groupKey, new GroupSettings { Enabled = false });
            Check((await Call()).Contains("关闭") && store.GetStats().Count == 0, "save tool honors per-group disable switch");
            await store.SetGroupSettingsAsync(groupKey, new GroupSettings { Enabled = true });
            var saved = await Call();
            Check(saved.Contains("已保存") && saved.Contains("猫、乖巧") && store.GetItems().Single().Tags.Contains("乖巧"),
                "save tool persists supplied labels despite automatic collection being off and outside whitelist");
            Check((await Call()).Contains("已在库中") && store.GetStats().Count == 1, "save tool confirms duplicates without adding another image");
            await store.RemoveAsync(store.GetItems().Single().Id);
            messages["request"] = message with { Segments = [new QuoteSegment("quoted")] };
            messages["quoted"] = message with { MessageId = "quoted", Sender = new User("bot") };
            var tagger = (MemeAiTagger)typeof(MemePlugin).GetField("_tagger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            config.Meme.AiTaggingEnabled = true;
            config.Meme.AiProvider = "aichat";
            tagger.AiChat = (_, _, _, _, _) => Task.FromResult<string?>("""{"caption":"视觉描述","tags":["识图标签"],"emotions":["happy"],"kind":"other"}""");
            saved = await Call();
            Check(saved.Contains("已保存") && saved.Contains("识图标签") && saved.Contains("乖巧") &&
                store.GetItems().Single().Source.MessageId == "quoted" && reads.All(r => r.InstanceId == "test" && r.Channel.Id == "g1"),
                "save tool resolves quoted bot image in same conversation and merges vision labels without non-meme filtering");
            await store.RemoveAsync(store.GetItems().Single().Id);
            messages["quoted"] = message with { MessageId = "quoted", InstanceId = "wrong-instance" };
            Check((await Call()).Contains("未保存") && store.GetStats().Count == 0, "save tool rejects foreign-instance quotes");
            messages["request"] = message with { Segments = [new TextSegment("保存一下")] };
            Check((await Call()).Contains("未保存") && store.GetStats().Count == 0, "save tool does not claim success without an image");
            messages["request"] = message with { Segments = [new ImageSegment("base64://" + Convert.ToBase64String("not an image"u8.ToArray()))] };
            Check((await Call()).Contains("保存失败") && store.GetStats().Count == 0, "save tool rejects invalid image bytes");
            messages["request"] = message;
            config.Meme.MaxBytes = 1;
            Check((await Call()).Contains("保存失败") && store.GetStats().Count == 0, "save tool enforces image size for base64 attachments");
            config.Meme.MaxBytes = 5_000_000;
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await Call(ct: canceled.Token); Check(false, "save tool propagates cancellation"); }
            catch (OperationCanceledException) { Check(store.GetStats().Count == 0, "save tool propagates cancellation without saving"); }
        }
        finally
        {
            await plugin.OnUnload();
            Directory.Delete(root, recursive: true);
        }
        return failures;
    }
}
