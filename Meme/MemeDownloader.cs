using System.Net;
using System.Net.Sockets;
using ShiroBot.MemePlugin.Config;

namespace ShiroBot.MemePlugin.Meme;

internal sealed record MemeDownloadResult(byte[] Data, string Mime);

/// <summary>受限图片下载器：只允许公网 http(s)、限制大小和内容类型，支持 base64/file URI。</summary>
internal sealed class MemeDownloader : IDisposable
{
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp",
    };

    private readonly HttpClient _http;
    private readonly MemeSection _config;
    private bool _disposed;

    public MemeDownloader(MemeSection config, NetworkSection network)
    {
        _config = config;
        var handler = new SocketsHttpHandler
        {
            // 手动跟随重定向，每一跳都重新做公网地址校验，避免重定向 SSRF。
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, Math.Max(3, config.DownloadTimeoutSeconds))),
        };

        if (!string.IsNullOrWhiteSpace(network.HttpProxy) &&
            Uri.TryCreate(network.HttpProxy, UriKind.Absolute, out var proxyUri))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(3, config.DownloadTimeoutSeconds)),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ShiroBot-MemePlugin/1.0");
    }

    public async Task<MemeDownloadResult?> DownloadAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (url.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var data = Convert.FromBase64String(url["base64://".Length..]);
                return data.Length is > 0 and <= 20_000_000
                    ? new MemeDownloadResult(data, SniffMime(data) ?? "image/png")
                    : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var path = new Uri(url).LocalPath;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > _config.MaxBytes) return null;
                var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var mime = SniffMime(data);
                return mime is null ? null : new MemeDownloadResult(data, mime);
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        if (!await IsPublicUrlAsync(uri, cancellationToken).ConfigureAwait(false))
            return null;

        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; hop <= 3; hop++)
            {
                response?.Dispose();
                response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null) return null;
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) ||
                        !await IsPublicUrlAsync(next, cancellationToken).ConfigureAwait(false))
                        return null;
                    uri = next;
                    continue;
                }

                break;
            }

            if (response is null || !response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > 0 and var length &&
                length > _config.MaxBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > _config.MaxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            var data = buffer.ToArray();
            if (data.Length == 0) return null;

            var declared = response.Content.Headers.ContentType?.MediaType;
            var mime = SniffMime(data);
            if (mime is null)
                return null;
            if (!string.IsNullOrWhiteSpace(declared) &&
                !AllowedMimeTypes.Contains(declared) &&
                !string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return null;

            return new MemeDownloadResult(data, mime);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<bool> IsPublicUrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Port is not (80 or 443)) return false;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                address.IsIPv6Teredo || address.Equals(IPAddress.IPv6Loopback) ||
                address.Equals(IPAddress.IPv6Any))
                return false;

            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) != 0xFC; // fc00::/7
        }

        var v4 = address.GetAddressBytes();
        if (v4[0] is 0 or 10 or 127) return false;
        if (v4[0] == 169 && v4[1] == 254) return false;
        if (v4[0] == 172 && v4[1] is >= 16 and <= 31) return false;
        if (v4[0] == 192 && v4[1] == 168) return false;
        if (v4[0] == 100 && v4[1] is >= 64 and <= 127) return false;
        if (v4[0] >= 224) return false;
        if (v4[0] == 192 && v4[1] is 0 or 2) return false;
        if (v4[0] == 198 && v4[1] is 18 or 19) return false;
        if (v4[0] == 198 && v4[1] == 51 && v4[2] == 100) return false;
        if (v4[0] == 203 && v4[1] == 0 && v4[2] == 113) return false;
        return true;
    }

    private static string? SniffMime(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";
        if (data.Length >= 6 &&
            data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
            return "image/gif";
        if (data.Length >= 12 &&
            data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            return "image/webp";
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
