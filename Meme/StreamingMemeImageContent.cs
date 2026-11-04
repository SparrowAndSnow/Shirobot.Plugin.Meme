using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>Write the vision request without constructing a full Base64 or JSON string.</summary>
internal sealed class StreamingMemeImageContent(string model, string prompt, byte[] data, string mime) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => WriteAsync(stream, ct);

    private async Task WriteAsync(Stream stream, CancellationToken ct)
    {
        if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) throw new InvalidDataException("Unsupported image MIME.");
        await Text(stream, "{\"model\":", ct);
        await JsonSerializer.SerializeAsync(stream, model, cancellationToken: ct);
        await Text(stream, ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":", ct);
        await JsonSerializer.SerializeAsync(stream, prompt, cancellationToken: ct);
        await Text(stream, "},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:" + mime + ";base64,", ct);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            for (int offset = 0; offset < data.Length; offset += 12 * 1024)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(12 * 1024, data.Length - offset);
                System.Buffers.Text.Base64.EncodeToUtf8(data.AsSpan(offset, count), buffer, out _, out int written);
                await stream.WriteAsync(buffer.AsMemory(0, written), ct);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        await Text(stream, "\"}}]}],\"temperature\":0,\"max_tokens\":2048,\"chat_template_kwargs\":{\"enable_thinking\":false}}", ct);
    }
    private static Task Text(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();
}
