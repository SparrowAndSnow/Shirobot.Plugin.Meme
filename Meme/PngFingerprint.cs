using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace ShiroBot.MemePlugin.Meme;

/// <summary>Read PNG pixels a row at a time, including Adam7 passes. No full-size pixel buffer.</summary>
internal static class PngFingerprint
{
    internal static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(new byte[] {137,80,78,71,13,10,26,10});

    internal static MemeFingerprint? Compute(byte[] data)
    {
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return null;
        var header = data.AsSpan(8, 25);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 13 || !header.Slice(4, 4).SequenceEqual("IHDR"u8)) return null;
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8)));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12)));
        int depth = header[16], color = header[17], interlace = header[20];
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || header[18] != 0 || header[19] != 0 || interlace > 1) return null;
        int channels = color switch {0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0};
        if (channels == 0 || !(depth is 8 or 16 || depth is 1 or 2 or 4 && color is 0 or 3) || color == 3 && depth == 16) return null;
        ReadOnlyMemory<byte> palette = default;
        var offset = 33;
        while (offset < data.Length)
        {
            var chunk = Chunk(data, offset);
            if (data.AsSpan(offset + 4, 4).SequenceEqual("PLTE"u8))
            {
                if (chunk.Length is < 3 or > 768 || chunk.Length % 3 != 0) return null;
                palette = data.AsMemory(offset + 8, chunk.Length);
            }
            if (data.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) break;
            if (data.AsSpan(offset + 4, 4).SequenceEqual("CgBI"u8)) return null;
            offset = chunk.Next;
        }
        if (offset >= data.Length || color == 3 && palette.IsEmpty) return null;
        var sums = new FingerprintSums(width, height);
        using var idat = new IdatStream(data, offset);
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var rowBytes = checked((width * channels * depth + 7) / 8);
        var row = ArrayPool<byte>.Shared.Rent(rowBytes);
        var previous = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            ReadOnlySpan<int> xStarts = [0,4,0,2,0,1,0], yStarts = [0,0,4,0,2,0,1];
            ReadOnlySpan<int> xSteps = [8,8,4,4,2,2,1], ySteps = [8,8,8,4,4,2,2];
            for (int pass = 0; pass < (interlace == 0 ? 1 : 7); pass++)
            {
                int xs = interlace == 0 ? 0 : xStarts[pass], ys = interlace == 0 ? 0 : yStarts[pass];
                int dx = interlace == 0 ? 1 : xSteps[pass], dy = interlace == 0 ? 1 : ySteps[pass];
                int columns = width <= xs ? 0 : (width - xs + dx - 1) / dx;
                if (columns == 0 || height <= ys) continue;
                int length = (columns * channels * depth + 7) / 8, bpp = Math.Max(1, (channels * depth + 7) / 8);
                previous.AsSpan(0, length).Clear();
                for (int y = ys; y < height; y += dy)
                {
                    int filter = zlib.ReadByte();
                    if (filter is < 0 or > 4) return null;
                    zlib.ReadExactly(row.AsSpan(0, length));
                    Unfilter(row.AsSpan(0, length), previous.AsSpan(0, length), bpp, filter);
                    for (int i = 0, x = xs; i < columns; i++, x += dx)
                    {
                        int r, g, b;
                        if (color == 3)
                        {
                            var index = depth == 8 ? row[i] : (row[i * depth / 8] >> (8 - depth - i * depth % 8)) & ((1 << depth) - 1);
                            var entry = palette.Span;
                            if (index * 3 + 2 >= entry.Length) return null;
                            r = entry[index * 3]; g = entry[index * 3 + 1]; b = entry[index * 3 + 2];
                        }
                        else if (depth < 8)
                        {
                            int value = (row[i * depth / 8] >> (8 - depth - i * depth % 8)) & ((1 << depth) - 1);
                            r = g = b = value * 255 / ((1 << depth) - 1);
                        }
                        else
                        {
                            int step = depth / 8, start = i * channels * step;
                            r = row[start];
                            if (color is 0 or 4) g = b = r;
                            else { g = row[start + step]; b = row[start + step * 2]; }
                        }
                        sums.Add(x, y, r, g, b);
                    }
                    (row, previous) = (previous, row);
                }
            }
            if (zlib.ReadByte() != -1) return null;
            return sums.Finish();
        }
        finally { ArrayPool<byte>.Shared.Return(row); ArrayPool<byte>.Shared.Return(previous); }
    }

    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, int filter)
    {
        if (filter == 0) return;
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, up = previous[i], upperLeft = i >= bpp ? previous[i - bpp] : 0;
            int predictor = filter switch {0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, upperLeft)};
            row[i] = unchecked((byte)(row[i] + predictor));
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static (int Length, int Next) Chunk(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - 12) throw new InvalidDataException("Truncated PNG chunk.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        if (length > data.Length - offset - 12) throw new InvalidDataException("Truncated PNG chunk.");
        return ((int)length, checked(offset + 12 + (int)length));
    }

    private sealed class IdatStream(byte[] data, int offset) : Stream
    {
        private int _offset = offset, _position, _remaining;
        private bool _ended;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int written = 0;
            while (!buffer.IsEmpty && !_ended)
            {
                if (_remaining == 0)
                {
                    var chunk = Chunk(data, _offset);
                    if (!data.AsSpan(_offset + 4, 4).SequenceEqual("IDAT"u8)) { _ended = true; break; }
                    _position = _offset + 8; _remaining = chunk.Length; _offset = chunk.Next;
                    if (_remaining == 0) continue;
                }
                int take = Math.Min(_remaining, buffer.Length);
                data.AsSpan(_position, take).CopyTo(buffer); buffer = buffer[take..];
                _position += take; _remaining -= take; written += take;
            }
            return written;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position {get => throw new NotSupportedException(); set => throw new NotSupportedException();}
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class FingerprintSums(int width, int height)
    {
        private readonly long[] _gray = new long[72], _counts = new long[72];
        private readonly int[] _xMasks = Masks(width, 9), _yMasks = Masks(height, 8);
        private long _r, _g, _b;
        private static int[] Masks(int size, int cells)
        {
            var masks = new int[size];
            for (int cell = 0; cell < cells; cell++)
                for (int p = cell * size / cells; p < Math.Min(size, Math.Max(cell * size / cells + 1, (cell + 1) * size / cells)); p++) masks[p] |= 1 << cell;
            return masks;
        }
        internal void Add(int x, int y, int r, int g, int b)
        {
            int xs = _xMasks[x], ys = _yMasks[y], gray = (r * 299 + g * 587 + b * 114) / 1000;
            for (int ym = ys; ym != 0; ym &= ym - 1)
                for (int xm = xs; xm != 0; xm &= xm - 1)
                {
                    int index = System.Numerics.BitOperations.TrailingZeroCount((uint)ym) * 9 + System.Numerics.BitOperations.TrailingZeroCount((uint)xm);
                    _gray[index] += gray; _counts[index]++; _r += r; _g += g; _b += b;
                }
        }
        internal MemeFingerprint Finish()
        {
            Span<byte> gray = stackalloc byte[72];
            for (int i = 0; i < 72; i++) gray[i] = _counts[i] == 0 ? (byte)0 : (byte)(_gray[i] / _counts[i]);
            ulong hash = 0;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if (gray[y * 9 + x] > gray[y * 9 + x + 1]) hash |= 1UL << (y * 8 + x);
            long pixels = (long)width * height;
            return new(hash, ((int)(_r / pixels) << 16) | ((int)(_g / pixels) << 8) | (int)(_b / pixels));
        }
    }
}
