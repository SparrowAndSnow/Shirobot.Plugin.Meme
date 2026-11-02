using System.Text.RegularExpressions;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>轻量标注：从消息文本、图片摘要和商城表情元数据提取标签与情绪，不依赖外部模型。</summary>
internal static partial class MemeTagging
{
    private static readonly (string Emotion, string[] Keywords)[] EmotionKeywords =
    [
        ("happy", ["哈哈", "笑", "开心", "可爱", "喜欢", "好耶", "乐", "233", "hhh", "lol", "贴贴", "抱抱", "赞", "棒"]),
        ("sad", ["哭", "难过", "呜呜", "泪", "破防", "委屈", "伤心", "唉"]),
        ("angry", ["气", "怒", "生气", "恼", "滚", "烦", "讨厌"]),
        ("confused", ["无语", "问号", "?", "？", "啊这", "懵", "疑惑", "离谱", "6", "哈?"]),
        ("shy", ["害羞", "脸红", "羞涩", "捂脸", "不好意思", "羞"]),
        ("daily", ["早", "晚安", "摸鱼", "上班", "吃饭", "睡", "下班", "困"]),
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "的", "了", "是", "我", "你", "他", "她", "它", "在", "有", "和", "就", "都", "也",
        "这", "那", "一个", "什么", "怎么", "可以", "不是", "没有", "表情", "图片", "这个",
    };

    public static List<string> GuessEmotions(string text)
    {
        var result = new List<string>();
        foreach (var (emotion, keywords) in EmotionKeywords)
        {
            if (keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                result.Add(emotion);
        }

        return result.Count > 0 ? result : ["daily"];
    }

    public static List<string> ExtractTags(string text, string? summary, MarketFaceInfo? marketFace)
    {
        var result = new List<string>();
        Add(result, summary);
        if (marketFace is not null)
        {
            Add(result, marketFace.Summary);
            Add(result, marketFace.Key);
            Add(result, "商城表情");
            if (marketFace.EmojiPackageId > 0)
                Add(result, $"包{marketFace.EmojiPackageId}");
        }

        foreach (var (_, keywords) in EmotionKeywords)
        {
            foreach (var keyword in keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    Add(result, keyword);
            }
        }

        foreach (var token in Tokenize(text))
            Add(result, token);

        // 适配器常把图片摘要写成 “[图片]”，没有语义；补通用标签，便于“发个表情/图片”命中。
        Add(result, "表情");
        Add(result, "图片");

        return result.Take(16).ToList();
    }

    public static string BuildCaption(string? summary, string text, int maxChars)
    {
        var value = !string.IsNullOrWhiteSpace(summary) ? summary : text;
        value = (value ?? "").Trim();
        if (value.Length == 0 || value is "[图片]" or "[表情]" or "[image]" or "[sticker]")
            return "表情包";
        return value.Length <= maxChars ? value : value[..maxChars];
    }

    public static bool IsStickerLike(string? subType, string? mime)
    {
        if (!string.IsNullOrWhiteSpace(subType) &&
            (subType.Contains("sticker", StringComparison.OrdinalIgnoreCase) ||
             subType.Contains("face", StringComparison.OrdinalIgnoreCase)))
            return true;
        return string.Equals(mime, "image/gif", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mime, "image/webp", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (var match in ChineseTokenRegex().Matches(text).Cast<Match>())
        {
            var token = match.Value.Trim();
            if (token.Length is >= 2 and <= 12 && !StopWords.Contains(token))
                yield return token;
        }

        foreach (var part in Regex.Split(text, @"[\s\p{P}\p{S}]+"))
        {
            var token = part.Trim();
            if (token.Length is >= 2 and <= 12 && !StopWords.Contains(token))
                yield return token;
        }
    }

    private static void Add(List<string> values, string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40) return;
        if (values.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase))) return;
        values.Add(trimmed);
    }

    [GeneratedRegex(@"[\u4e00-\u9fff]{2,6}")]
    private static partial Regex ChineseTokenRegex();
}
