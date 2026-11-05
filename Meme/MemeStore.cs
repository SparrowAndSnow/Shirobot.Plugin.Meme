using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiroBot.MemePlugin.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>表情库索引和文件存储。所有写操作串行化，索引通过临时文件原子替换。</summary>
internal sealed class MemeStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<MemeItem> _items = [];
    private readonly Dictionary<string, MemeItem> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemeItem> _byMarketFace = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupSettings> _groups = new(StringComparer.Ordinal);

    public string Root { get; }
    public string FilesDirectory { get; }
    public string IndexPath { get; }
    public string GroupsPath { get; }
    public MemeSection Config { get; private set; }

    public MemeStore(string root, MemeSection config)
    {
        Root = root;
        FilesDirectory = Path.Combine(root, "files");
        IndexPath = Path.Combine(root, "index.json");
        GroupsPath = Path.Combine(root, "groups.json");
        Config = config;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(FilesDirectory);
    }

    public void UpdateConfig(MemeSection config) => Config = config;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(IndexPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(IndexPath, cancellationToken).ConfigureAwait(false);
                    var index = JsonSerializer.Deserialize(json, MemeJsonContext.Default.MemeIndex);
                    if (index is not null)
                    {
                        lock (_sync)
                        {
                            _items.Clear();
                            _byHash.Clear();
                            _byMarketFace.Clear();
                            foreach (var item in index.Items)
                            {
                                if (string.IsNullOrWhiteSpace(item.Hash) || string.IsNullOrWhiteSpace(item.FileName))
                                    continue;
                                if (!string.Equals(Path.GetFileName(item.FileName), item.FileName,
                                        StringComparison.Ordinal))
                                    continue;
                                if (!File.Exists(Path.Combine(FilesDirectory, item.FileName)))
                                    continue;
                                _items.Add(item);
                                _byHash[item.Hash] = item;
                                if (MarketFaceKey(item.MarketFace) is { } marketKey)
                                    _byMarketFace[marketKey] = item;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    var backup = $"{IndexPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    TryMove(IndexPath, backup);
                    BotLog.Warning($"Meme 索引损坏，已备份到 {Path.GetFileName(backup)}：{ex.Message}");
                }
            }

            if (File.Exists(GroupsPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(GroupsPath, cancellationToken).ConfigureAwait(false);
                    var entries = JsonSerializer.Deserialize(json, MemeJsonContext.Default.ListGroupSettingsEntry);
                    if (entries is not null)
                    {
                        lock (_sync)
                        {
                            _groups.Clear();
                            foreach (var entry in entries)
                            {
                                if (!string.IsNullOrWhiteSpace(entry.Key))
                                    _groups[entry.Key] = entry.Settings;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    var backup = $"{GroupsPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    TryMove(GroupsPath, backup);
                    BotLog.Warning($"Meme 群设置损坏，已备份到 {Path.GetFileName(backup)}：{ex.Message}");
                }
            }

            await CleanupLockedAsync(cancellationToken).ConfigureAwait(false);
            await EnsurePerceptualHashesLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemeItem?> AddAsync(
        byte[] data,
        string mime,
        string caption,
        IEnumerable<string> tags,
        IEnumerable<string> emotions,
        MemeSource source,
        MarketFaceInfo? marketFace,
        string? kind = null,
        CancellationToken cancellationToken = default,
        MemeFingerprint? precomputedFingerprint = null)
    {
        if (data.Length == 0 || data.Length > Config.MaxBytes)
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_byHash.TryGetValue(hash, out var existing))
                    return existing;
                if (MarketFaceKey(marketFace) is { } marketKey &&
                    _byMarketFace.TryGetValue(marketKey, out existing))
                    return existing;
            }

            MemeFingerprint? fingerprint = null;
            if (Config.PerceptualThreshold > 0)
            {
                fingerprint = precomputedFingerprint ?? MemePerceptualHash.Compute(data);
                if (fingerprint is { } candidate)
                {
                    lock (_sync)
                    {
                        foreach (var existing in _items)
                        {
                            if (existing.PerceptualHash is { } otherHash &&
                                existing.AverageColor is { } otherColor &&
                                MemePerceptualHash.Distance(candidate.Hash, otherHash) <= Config.PerceptualThreshold &&
                                MemePerceptualHash.ColorClose(candidate.AverageColor, otherColor,
                                    Config.PerceptualColorThreshold))
                                return existing;
                        }
                    }
                }
            }

            var fileName = hash + "." + ExtensionForMime(mime);
            var path = Path.Combine(FilesDirectory, fileName);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            var item = new MemeItem
            {
                Id = hash,
                Hash = hash,
                FileName = fileName,
                Mime = mime,
                Caption = Trim(caption, Config.CaptionMaxChars),
                Tags = Normalize(tags, 16),
                Emotions = Normalize(emotions, 8),
                Source = source,
                MarketFace = marketFace,
                Kind = NormalizeKind(kind),
                PerceptualHash = fingerprint?.Hash,
                AverageColor = fingerprint?.AverageColor,
                Bytes = data.Length,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            lock (_sync)
            {
                _items.Add(item);
                _byHash[hash] = item;
                if (MarketFaceKey(marketFace) is { } marketKey)
                    _byMarketFace[marketKey] = item;
            }

            await CleanupLockedAsync(cancellationToken).ConfigureAwait(false);
            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            return item;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"Meme 保存失败：{ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<MemeSelection> Search(string? query, string? emotion, int limit,
        IReadOnlySet<string>? excludedIds = null)
    {
        List<MemeItem> snapshot;
        lock (_sync)
            snapshot = _items.ToList();

        var normalizedQuery = (query ?? "").Trim();
        var normalizedEmotion = (emotion ?? "").Trim();

        var scored = new List<(MemeItem Item, double Score)>();
        foreach (var item in snapshot)
        {
            // 在截断候选集前排除最近发过的图片，避免候选窗口被旧图占满。
            if (excludedIds?.Contains(item.Id) == true)
                continue;
            if (normalizedEmotion.Length > 0 &&
                !item.Emotions.Any(e => string.Equals(e, normalizedEmotion, StringComparison.OrdinalIgnoreCase)))
                continue;

            var score = 0d;
            if (normalizedQuery.Length > 0)
            {
                var inCaption = item.Caption.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
                var exactTag = item.Tags.Any(t => string.Equals(t, normalizedQuery, StringComparison.OrdinalIgnoreCase));
                var partialTag = item.Tags.Any(t => t.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));
                if (!inCaption && !partialTag)
                    continue;
                if (exactTag) score += 6;
                if (partialTag) score += 3;
                if (inCaption) score += 2;
            }
            else
            {
                score = 1;
            }

            scored.Add((item, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(_ => Random.Shared.Next())
            .Take(Math.Clamp(limit, 1, 50))
            .Select(x => ToSelection(x.Item))
            .ToList();
    }

    public MemeItem? FindExisting(byte[] data, MarketFaceInfo? marketFace)
        => FindExisting(data, marketFace, out _);

    public MemeItem? FindExisting(byte[] data, MarketFaceInfo? marketFace, out MemeFingerprint? fingerprint)
    {
        fingerprint = null;
        if (data.Length == 0) return null;
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        lock (_sync)
        {
            if (_byHash.TryGetValue(hash, out var existing)) return existing;
            if (MarketFaceKey(marketFace) is { } marketKey &&
                _byMarketFace.TryGetValue(marketKey, out existing)) return existing;
        }

        if (Config.PerceptualThreshold <= 0) return null;
        fingerprint = MemePerceptualHash.Compute(data);
        if (fingerprint is not { } candidate) return null;
        lock (_sync)
        {
            foreach (var existing in _items)
            {
                if (existing.PerceptualHash is { } otherHash &&
                    existing.AverageColor is { } otherColor &&
                    MemePerceptualHash.Distance(candidate.Hash, otherHash) <= Config.PerceptualThreshold &&
                    MemePerceptualHash.ColorClose(candidate.AverageColor, otherColor,
                        Config.PerceptualColorThreshold))
                    return existing;
            }
        }

        return null;
    }

    public IReadOnlyList<MemeItem> GetItems()
    {
        lock (_sync)
            return _items.ToList();
    }

    public int CountMatching(string? query, string? emotion)
    {
        List<MemeItem> snapshot;
        lock (_sync)
            snapshot = _items.ToList();

        var normalizedQuery = (query ?? "").Trim();
        var normalizedEmotion = (emotion ?? "").Trim();
        var count = 0;
        foreach (var item in snapshot)
        {
            if (normalizedEmotion.Length > 0 &&
                !item.Emotions.Any(e => string.Equals(e, normalizedEmotion, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (normalizedQuery.Length > 0 &&
                !item.Caption.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) &&
                !item.Tags.Any(t => t.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
                continue;

            count++;
        }

        return count;
    }

    public MemeSelection? GetById(string id)
    {
        lock (_sync)
        {
            var index = FindItemIndex(id);
            return index < 0 ? null : ToSelection(_items[index]);
        }
    }

    private int FindItemIndex(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        var exact = _items.FindIndex(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;

        var matches = _items
            .Select((item, index) => (item, index))
            .Where(x => x.item.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].index : -1;
    }

    public async Task<bool> MarkUsedAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MemeItem? updated = null;
            lock (_sync)
            {
                var index = FindItemIndex(id);
                if (index < 0) return false;
                updated = _items[index] with
                {
                    LastUsedAt = DateTimeOffset.UtcNow,
                    UseCount = _items[index].UseCount + 1,
                };
                _items[index] = updated;
                _byHash[updated.Hash] = updated;
                if (MarketFaceKey(updated.MarketFace) is { } marketKey)
                    _byMarketFace[marketKey] = updated;
            }

            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateTagsAsync(string id, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var index = FindItemIndex(id);
                if (index < 0) return false;
                var updated = _items[index] with { Tags = Normalize(tags, 16) };
                _items[index] = updated;
                _byHash[updated.Hash] = updated;
                if (MarketFaceKey(updated.MarketFace) is { } marketKey)
                    _byMarketFace[marketKey] = updated;
            }

            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateMetadataAsync(
        string id,
        string caption,
        IEnumerable<string> tags,
        IEnumerable<string> emotions,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var index = FindItemIndex(id);
                if (index < 0) return false;
                var updated = _items[index] with
                {
                    Caption = Trim(caption, Config.CaptionMaxChars),
                    Tags = Normalize(tags, 16),
                    Emotions = Normalize(emotions, 8),
                    Kind = kind is null ? _items[index].Kind : NormalizeKind(kind),
                };
                _items[index] = updated;
                _byHash[updated.Hash] = updated;
                if (MarketFaceKey(updated.MarketFace) is { } marketKey)
                    _byMarketFace[marketKey] = updated;
            }

            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> QuarantineAsync(string id, CancellationToken ct = default)
    {
        var item = GetItems().FirstOrDefault(item => item.Id == id);
        if (item is null) return false;
        var rejected = Path.Combine(Path.GetDirectoryName(FilesDirectory)!, "rejected");
        Directory.CreateDirectory(rejected);
        // Preserve the original before removing it from the searchable library.
        var source = Path.Combine(FilesDirectory, item.FileName);
        var destination = Path.Combine(rejected, item.FileName);
        await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return await RemoveAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MemeItem? removed;
            lock (_sync)
            {
                var index = FindItemIndex(id);
                if (index < 0) return false;
                removed = _items[index];
                _items.RemoveAt(index);
                _byHash.Remove(removed.Hash);
                if (MarketFaceKey(removed.MarketFace) is { } marketKey)
                    _byMarketFace.Remove(marketKey);
            }

            TryDelete(Path.Combine(FilesDirectory, removed.FileName));
            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public MemeLibraryStats GetStats()
    {
        lock (_sync)
        {
            return new MemeLibraryStats(
                _items.Count,
                _items.Sum(i => i.Bytes),
                _items.Count == 0 ? null : _items.Max(i => i.CreatedAt));
        }
    }

    public GroupSettings GetGroupSettings(string key)
    {
        lock (_sync)
            return _groups.TryGetValue(key, out var settings) ? settings : new GroupSettings();
    }

    public async Task SetGroupSettingsAsync(string key, GroupSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
                _groups[key] = settings;
            await SaveGroupsLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GroupKey(string platform, string? instanceId, string channelId) =>
        $"{platform}:{instanceId ?? "-"}:{channelId}";

    private async Task CleanupLockedAsync(CancellationToken cancellationToken)
    {
        List<MemeItem> remove;
        lock (_sync)
        {
            var totalBytes = _items.Sum(i => i.Bytes);
            if (_items.Count <= Config.MaxItems && totalBytes <= Config.MaxTotalBytes)
                return;

            remove = _items
                .OrderBy(i => i.LastUsedAt == default ? DateTimeOffset.MinValue : i.LastUsedAt)
                .ThenBy(i => i.CreatedAt)
                .Take(Math.Max(0, _items.Count - Config.MaxItems))
                .ToList();

            var remainingBytes = totalBytes - remove.Sum(i => i.Bytes);
            if (remainingBytes > Config.MaxTotalBytes)
            {
                foreach (var item in _items
                             .Where(i => !remove.Contains(i))
                             .OrderBy(i => i.LastUsedAt == default ? DateTimeOffset.MinValue : i.LastUsedAt))
                {
                    remove.Add(item);
                    remainingBytes -= item.Bytes;
                    if (remainingBytes <= Config.MaxTotalBytes) break;
                }
            }

            foreach (var item in remove)
            {
                _items.Remove(item);
                _byHash.Remove(item.Hash);
                if (MarketFaceKey(item.MarketFace) is { } marketKey)
                    _byMarketFace.Remove(marketKey);
                TryDelete(Path.Combine(FilesDirectory, item.FileName));
            }
        }

        if (remove.Count > 0)
        {
            BotLog.Info($"Meme 清理了 {remove.Count} 张超出容量限制的表情");
            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsurePerceptualHashesLockedAsync(CancellationToken cancellationToken)
    {
        if (Config.PerceptualThreshold <= 0) return;

        List<MemeItem> snapshot;
        lock (_sync)
            snapshot = _items.ToList();

        var changed = false;
        foreach (var item in snapshot)
        {
            if (item.PerceptualHash is not null) continue;
            try
            {
                var path = Path.Combine(FilesDirectory, item.FileName);
                if (!File.Exists(path)) continue;
                var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var fingerprint = MemePerceptualHash.Compute(data);
                if (fingerprint is null) continue;

                lock (_sync)
                {
                    var index = _items.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.Ordinal));
                    if (index < 0) continue;
                    var updated = _items[index] with
                    {
                        PerceptualHash = fingerprint.Value.Hash,
                        AverageColor = fingerprint.Value.AverageColor,
                    };
                    _items[index] = updated;
                    _byHash[updated.Hash] = updated;
                    if (MarketFaceKey(updated.MarketFace) is { } marketKey)
                        _byMarketFace[marketKey] = updated;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                BotLog.Warning($"Meme 计算视觉指纹失败 {item.Id[..Math.Min(8, item.Id.Length)]}：{ex.Message}");
            }
        }

        if (changed)
            await SaveIndexLockedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? MarketFaceKey(MarketFaceInfo? marketFace)
    {
        if (marketFace is null || string.IsNullOrWhiteSpace(marketFace.EmojiId)) return null;
        return $"{marketFace.EmojiPackageId}:{marketFace.EmojiId}";
    }

    private async Task SaveIndexLockedAsync(CancellationToken cancellationToken)
    {
        MemeIndex index;
        lock (_sync)
            index = new MemeIndex { UpdatedAt = DateTimeOffset.UtcNow, Items = _items.ToList() };

        var json = JsonSerializer.Serialize(index, MemeJsonContext.Default.MemeIndex);
        var temp = IndexPath + ".tmp";
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temp, IndexPath, overwrite: true);
    }

    private async Task SaveGroupsLockedAsync(CancellationToken cancellationToken)
    {
        List<GroupSettingsEntry> entries;
        lock (_sync)
            entries = _groups.Select(kv => new GroupSettingsEntry { Key = kv.Key, Settings = kv.Value }).ToList();

        var json = JsonSerializer.Serialize(entries, MemeJsonContext.Default.ListGroupSettingsEntry);
        var temp = GroupsPath + ".tmp";
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temp, GroupsPath, overwrite: true);
    }

    private MemeSelection ToSelection(MemeItem item) =>
        new(item.Id, item.Caption, item.Tags, Path.Combine(FilesDirectory, item.FileName), item.Mime, item.MarketFace);

    private static string ExtensionForMime(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "png",
    };

    private static List<string> Normalize(IEnumerable<string> values, int max)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > 40) continue;
            if (result.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(trimmed);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static string NormalizeKind(string? kind) =>
        (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "meme" => "meme",
            "screenshot" => "screenshot",
            "photo" => "photo",
            "other" => "other",
            _ => "unknown",
        };

    private static string Trim(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim()[..Math.Min(value.Trim().Length, Math.Max(0, max))];

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 文件可能仍被占用，留给下次清理 */ }
    }

    private static void TryMove(string source, string destination)
    {
        try { File.Move(source, destination, overwrite: true); }
        catch { /* 备份失败不阻塞启动 */ }
    }

    public void Dispose() => _gate.Dispose();
}
