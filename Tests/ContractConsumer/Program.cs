using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ShiroBot.MemePlugin.Contracts;
using ShiroBot.SDK.Plugin;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

// This project has no reference to the provider: every contract type must come from DLL extraction.
Check(MemeLibrary.Select.Name == "meme.select" && MemeLibrary.Send.Name == "meme.send" && MemeLibrary.Stats.Name == "meme.stats",
    "The published DLL must export all library services.");
var request = new MemeSendRequest("full-id", new MemeDestination("qq", "Group", "test-group"), "test-caption");
var json = MemeLibrary.Send.ToCallJson(request);
Check((string?)json["arguments"]?["target"]?["channel_id"] == "test-group", "Imported contract must use the expected wire format.");
Check(PluginJson.Deserialize<MemeSendRequest>(PluginJson.Serialize(request)) == request, "Imported messages must round-trip.");
Check(!typeof(MemeLibrary).IsPublic && !typeof(MemeDestination).IsPublic, "Imported contract types must remain private to the consumer.");
Check(!Assembly.GetExecutingAssembly().GetReferencedAssemblies().Any(a => a.Name is "ShiroBot.MemePlugin" or "ShiroBot.AiChatPlugin"),
    "The consumer must not depend on either provider assembly at runtime.");

var dll = Path.GetFullPath(args.Single());
using var stream = File.OpenRead(dll);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var dependencies = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
Check(!dependencies.Any(name => name is "StbImageSharp" or "ShiroBot.AiChatPlugin"), "Private libraries must not remain external dependencies.");
Check(metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Any(t =>
    metadata.GetString(t.Namespace) == "StbImageSharp" && metadata.GetString(t.Name) == "ImageResult"),
    "StbImageSharp must actually be merged into the plugin.");
Check(Directory.GetFiles(Path.GetDirectoryName(dll)!, "*.dll").Length == 1, "Release output must contain exactly one plugin DLL.");
Console.WriteLine("Published Meme DLL: contracts import, JSON round-trip, isolated consumer and merged single-file dependencies passed.");
