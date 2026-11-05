using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using StbImageSharp;
using ShiroBot.MemePlugin.Meme;

internal static class FingerprintChecks
{
    internal static void Run()
    {
        var cases = new (int Color, int Depth)[] {(0,1),(0,2),(0,4),(0,8),(0,16),(2,8),(2,16),(3,1),(3,2),(3,4),(3,8),(4,8),(4,16),(6,8),(6,16)};
        int checkedCases = 0;
        foreach (var (color, depth) in cases)
        foreach (var size in new[] {(1,1),(17,13),(36,24)})
        foreach (int interlace in new[] {0,1})
        foreach (int filter in Enumerable.Range(0,5))
        {
            var png = Png(size.Item1,size.Item2,color,depth,interlace,filter);
            var expected = Legacy(png);
            var actual = MemePerceptualHash.Compute(png);
            if (actual != expected) throw new Exception($"PNG fingerprint changed: {color}/{depth}, {size}, Adam7={interlace}, filter={filter}: {actual} vs {expected}");
            checkedCases++;
        }
        var big = Png(4096,4096,6,8,0,0,solid:true);
        _ = MemePerceptualHash.Compute(big); // pool and decoder warmup
        long before = GC.GetAllocatedBytesForCurrentThread();
        var hash = MemePerceptualHash.Compute(big);
        long allocated = GC.GetAllocatedBytesForCurrentThread()-before;
        if (hash is not {Hash:0,AverageColor:0x6688aa} || allocated > 2_000_000)
            throw new Exception($"Large PNG decode is not bounded: hash={hash}, allocated={allocated}");
        if (MemePerceptualHash.Compute(big[..40]) is not null) throw new Exception("Truncated PNG accepted");
        using (var bitmap = new SkiaSharp.SKBitmap(1536, 1024))
        {
            bitmap.Erase(new SkiaSharp.SKColor(102, 136, 170));
            using var jpeg = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 95);
            var bytes = jpeg.ToArray();
            var expected = Legacy(bytes);
            _ = MemePerceptualHash.Compute(bytes);
            long jpegBefore = GC.GetAllocatedBytesForCurrentThread();
            var actual = MemePerceptualHash.Compute(bytes);
            long jpegAllocated = GC.GetAllocatedBytesForCurrentThread() - jpegBefore;
            if (actual is null || actual.Value.Hash != expected.Hash ||
                !MemePerceptualHash.ColorClose(actual.Value.AverageColor, expected.AverageColor, 2) || jpegAllocated > 1_000_000)
                throw new Exception($"JPEG scanline decode failed: {actual}, expected={expected}, allocated={jpegAllocated}");
            Console.WriteLine($"PASS: large JPEG scanline fingerprint matches legacy and allocates {jpegAllocated} managed bytes");
        }
        Console.WriteLine($"PASS: {checkedCases} PNG color/depth/filter/Adam7 fingerprints preserve legacy output; 4096² decode allocates {allocated} bytes");
    }

    private static MemeFingerprint Legacy(byte[] data)
    {
        var image=ImageResult.FromMemory(data,ColorComponents.RedGreenBlueAlpha);
        var gray=new byte[72];long r=0,g=0,b=0;
        for(int ty=0;ty<8;ty++)for(int tx=0;tx<9;tx++)
        {
            long sum=0,count=0;
            for(int y=ty*image.Height/8;y<Math.Min(image.Height,Math.Max(ty*image.Height/8+1,(ty+1)*image.Height/8));y++)
            for(int x=tx*image.Width/9;x<Math.Min(image.Width,Math.Max(tx*image.Width/9+1,(tx+1)*image.Width/9));x++)
            {int i=(y*image.Width+x)*4;sum+=(image.Data[i]*299+image.Data[i+1]*587+image.Data[i+2]*114)/1000;r+=image.Data[i];g+=image.Data[i+1];b+=image.Data[i+2];count++;}
            gray[ty*9+tx]=count==0?(byte)0:(byte)(sum/count);
        }
        ulong hash=0;for(int y=0;y<8;y++)for(int x=0;x<8;x++)if(gray[y*9+x]>gray[y*9+x+1])hash|=1UL<<(y*8+x);
        int pixels=image.Width*image.Height;return new(hash,((int)(r/pixels)<<16)|((int)(g/pixels)<<8)|(int)(b/pixels));
    }

    internal static byte[] Png(int width,int height,int color,int depth,int interlace,int filter,bool solid=false)
    {
        int channels=color switch {0 or 3=>1,2=>3,4=>2,_=>4};
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,leaveOpen:true))
        {
            int[] xs=[0,4,0,2,0,1,0],ys=[0,0,4,0,2,0,1],dx=[8,8,4,4,2,2,1],dy=[8,8,8,4,4,2,2];
            for(int pass=0;pass<(interlace==0?1:7);pass++)
            {
                int x0=interlace==0?0:xs[pass],y0=interlace==0?0:ys[pass],xstep=interlace==0?1:dx[pass],ystep=interlace==0?1:dy[pass];
                int columns=width<=x0?0:(width-x0+xstep-1)/xstep;if(columns==0||height<=y0)continue;
                int length=(columns*channels*depth+7)/8,bpp=Math.Max(1,(channels*depth+7)/8);
                var previous=new byte[length];var row=new byte[length];var encoded=new byte[length];
                for(int y=y0;y<height;y+=ystep)
                {
                    row.AsSpan().Clear();
                    for(int i=0,x=x0;i<columns;i++,x+=xstep)for(int c=0;c<channels;c++)
                    {
                        int value=solid?(c switch{0=>0x66,1=>0x88,2=>0xaa,_=>255}):(x*17+y*31+c*59)&255;
                        if(depth<8)row[i*depth/8]|=(byte)((value&((1<<depth)-1))<<(8-depth-i*depth%8));
                        else {int o=(i*channels+c)*(depth/8);row[o]=(byte)value;if(depth==16)row[o+1]=(byte)(value^0x5a);}
                    }
                    for(int i=0;i<length;i++)
                    {
                        int a=i>=bpp?row[i-bpp]:0,b=previous[i],c=i>=bpp?previous[i-bpp]:0,p=a+b-c;
                        int pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);
                        int predictor=filter switch{0=>0,1=>a,2=>b,3=>(a+b)/2,_=>pa<=pb&&pa<=pc?a:pb<=pc?b:c};
                        encoded[i]=unchecked((byte)(row[i]-predictor));
                    }
                    zlib.WriteByte((byte)filter);zlib.Write(encoded);(row,previous)=(previous,row);
                }
            }
        }
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        var ihdr=new byte[13];BinaryPrimitives.WriteInt32BigEndian(ihdr,width);BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4),height);ihdr[8]=(byte)depth;ihdr[9]=(byte)color;ihdr[12]=(byte)interlace;Chunk(output,"IHDR"u8,ihdr);
        if(color==3){var palette=new byte[768];for(int i=0;i<palette.Length;i++)palette[i]=(byte)(i*37);Chunk(output,"PLTE"u8,palette);}
        var data=compressed.ToArray();for(int i=0;i<data.Length;i+=16384)Chunk(output,"IDAT"u8,data.AsSpan(i,Math.Min(16384,data.Length-i)));Chunk(output,"IEND"u8,[]);return output.ToArray();
    }
    private static void Chunk(Stream output,ReadOnlySpan<byte> name,ReadOnlySpan<byte> data)
    {
        Span<byte> value=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(value,data.Length);output.Write(value);output.Write(name);output.Write(data);
        uint crc=uint.MaxValue;foreach(byte b in name)crc=Update(crc,b);foreach(byte b in data)crc=Update(crc,b);BinaryPrimitives.WriteUInt32BigEndian(value,~crc);output.Write(value);
    }
    private static uint Update(uint crc,byte value){crc^=value;for(int i=0;i<8;i++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;return crc;}
}
