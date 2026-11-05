using System.Numerics;
using StbImageSharp;

namespace ShiroBot.MemePlugin.Meme;

internal readonly record struct MemeFingerprint(ulong Hash, int AverageColor);

/// <summary>基于 dHash + 平均色的轻量视觉指纹，用来识别转发/二次压缩后的近似重复图片。</summary>
internal static class MemePerceptualHash
{
    private const int Width = 9;
    private const int Height = 8;

    public static MemeFingerprint? Compute(byte[] data)
    {
        try
        {
            if (PngFingerprint.IsPng(data)) return PngFingerprint.Compute(data);
            using var header = new MemoryStream(data, writable: false);
            var info = ImageInfo.FromStream(header);
            if (info is not { Width: > 0 and <= 8192, Height: > 0 and <= 8192 })
                return null;

            // The legacy decoder allocates both native and managed full-size RGBA.
            // Large JPEGs can be processed in scanlines; unsupported large formats
            // retain SHA-256 deduplication and collection without a full pixel decode.
            if ((long)info.Value.Width * info.Value.Height > 1_048_576)
                return JpegFingerprint.Compute(data);

            var image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            if (image.Width <= 0 || image.Height <= 0 || image.Data is null)
                return null;

            var gray = new byte[Width * Height];
            long sumR = 0, sumG = 0, sumB = 0;
            for (var ty = 0; ty < Height; ty++)
            {
                var y0 = ty * image.Height / Height;
                var y1 = Math.Max(y0 + 1, (ty + 1) * image.Height / Height);
                for (var tx = 0; tx < Width; tx++)
                {
                    var x0 = tx * image.Width / Width;
                    var x1 = Math.Max(x0 + 1, (tx + 1) * image.Width / Width);
                    var sum = 0L;
                    var count = 0;
                    for (var y = y0; y < Math.Min(y1, image.Height); y++)
                    {
                        var row = y * image.Width * 4;
                        for (var x = x0; x < Math.Min(x1, image.Width); x++)
                        {
                            var offset = row + x * 4;
                            var r = image.Data[offset];
                            var g = image.Data[offset + 1];
                            var b = image.Data[offset + 2];
                            sum += (r * 299 + g * 587 + b * 114) / 1000;
                            sumR += r;
                            sumG += g;
                            sumB += b;
                            count++;
                        }
                    }

                    gray[ty * Width + tx] = count == 0 ? (byte)0 : (byte)(sum / count);
                }
            }

            ulong hash = 0;
            var bit = 0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width - 1; x++)
                {
                    if (gray[y * Width + x] > gray[y * Width + x + 1])
                        hash |= 1UL << bit;
                    bit++;
                }
            }

            var pixels = image.Width * image.Height;
            var color = pixels == 0
                ? 0
                : ((int)(sumR / pixels) << 16) | ((int)(sumG / pixels) << 8) | (int)(sumB / pixels);
            return new MemeFingerprint(hash, color);
        }
        catch
        {
            // 损坏/不支持的图片不参与视觉去重，但仍可按原始字节入库。
            return null;
        }
    }

    public static int Distance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    public static bool ColorClose(int left, int right, int threshold) =>
        Math.Abs(((left >> 16) & 0xFF) - ((right >> 16) & 0xFF)) <= threshold &&
        Math.Abs(((left >> 8) & 0xFF) - ((right >> 8) & 0xFF)) <= threshold &&
        Math.Abs((left & 0xFF) - (right & 0xFF)) <= threshold;
}
