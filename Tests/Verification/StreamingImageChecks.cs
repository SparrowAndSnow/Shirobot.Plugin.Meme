using System.Text.Json;
using ShiroBot.MemePlugin.Meme;

internal static class StreamingImageChecks
{
    internal static async Task RunAsync()
    {
        var image=new byte[12*1024+7];new Random(42).NextBytes(image);
        using(var content=new StreamingMemeImageContent("model","prompt \"quoted\" 中文",image,"image/png"))
        using(var output=new MemoryStream())
        {
            await content.CopyToAsync(output);using var json=JsonDocument.Parse(output.ToArray());
            var message=json.RootElement.GetProperty("messages")[0].GetProperty("content");
            if(message[0].GetProperty("text").GetString()!="prompt \"quoted\" 中文"||
                message[1].GetProperty("image_url").GetProperty("url").GetString()!="data:image/png;base64,"+Convert.ToBase64String(image))
                throw new Exception("Streaming tagging request changed text or image bytes");
        }
        var large=new byte[3_000_000];
        using(var warm=new StreamingMemeImageContent("model","prompt",large,"image/png"))await warm.CopyToAsync(Stream.Null);
        long before=GC.GetAllocatedBytesForCurrentThread();
        using(var content=new StreamingMemeImageContent("model","prompt",large,"image/png"))await content.CopyToAsync(Stream.Null);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        if(allocated>1_000_000)throw new Exception($"Tagging serialized a whole image buffer: {allocated}");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        using(var content=new StreamingMemeImageContent("model","prompt",large,"image/png"))
        {
            try{await content.CopyToAsync(Stream.Null,canceled.Token);throw new Exception("Tagging ignored cancellation");}
            catch(OperationCanceledException)when(canceled.IsCancellationRequested){}
        }
        Console.WriteLine($"PASS: streaming tagging preserves escaped text and Base64 boundaries; 3 MB image serializes with {allocated} allocated bytes; cancellation stops output");
    }
}
