using System.Text.Json.Serialization;

namespace ShiroBot.MemePlugin.Meme;

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MemeIndex))]
[JsonSerializable(typeof(MemeItem))]
[JsonSerializable(typeof(MemeSource))]
[JsonSerializable(typeof(MarketFaceInfo))]
[JsonSerializable(typeof(List<MemeItem>))]
[JsonSerializable(typeof(List<GroupSettingsEntry>))]
internal partial class MemeJsonContext : JsonSerializerContext;
