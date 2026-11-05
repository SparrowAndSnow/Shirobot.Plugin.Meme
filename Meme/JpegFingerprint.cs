using System.Runtime.InteropServices;
using SkiaSharp;

namespace ShiroBot.MemePlugin.Meme;

internal static class JpegFingerprint
{
    internal static MemeFingerprint? Compute(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var codec = SKCodec.Create(input);
        // Progressive JPEGs may require a large coefficient buffer even when the
        // output is read by row. Keep that workspace bounded as well.
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg ||
            (long)codec.Info.Width * codec.Info.Height > 8_388_608) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (codec.StartScanlineDecode(info) != SKCodecResult.Success || codec.ScanlineOrder != SKCodecScanlineOrder.TopDown) return null;
        var row = new byte[info.RowBytes];
        var handle = GCHandle.Alloc(row, GCHandleType.Pinned);
        try
        {
            var sums = new PngFingerprint.FingerprintSums(info.Width, info.Height);
            for (int y = 0; y < info.Height; y++)
            {
                if (codec.GetScanlines(handle.AddrOfPinnedObject(), 1, info.RowBytes) != 1) return null;
                for (int x = 0; x < info.Width; x++) sums.Add(x, y, row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
            }
            return sums.Finish();
        }
        finally { handle.Free(); }
    }
}
