using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shuo.Services;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

internal static class LocalTranslationTests
{
    internal static async Task RunAsync()
    {
        var source = string.Concat(Enumerable.Repeat("你好😀 hello。", 200));
        var passages = ReadingText.Split(source, ReadingText.LocalRequestBytes);
        Check(string.Concat(passages) == source && passages.All(p => Encoding.UTF8.GetByteCount(p) <= 900), "Split long Unicode segments without losing text");
        Check(SelfHostedTextTranslator.Endpoint("100.119.85.74").AbsoluteUri == "ws://100.119.85.74:18766/v1/translation", "Local endpoint");
        foreach (var stopEarly in new[] { false, true }) await WireAsync(stopEarly);
        Console.WriteLine("Passed local translation, committed segments, repeated speech, Unicode bounds, and tail delivery tests.");
    }

    private static async Task WireAsync(bool stopEarly)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var listener = builder.Build();
        listener.UseWebSockets();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();
        var finalSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translated = new List<string>();
        listener.Run(async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var start = await Receive(socket, deadline.Token);
                Check(start.RootElement.GetProperty("protocol").GetInt32() == 1, "Protocol version");
                if (context.Request.Path == "/translation")
                {
                    Check(start.RootElement.GetProperty("target").GetString() == "en", "Target language forwarded");
                    var text = start.RootElement.GetProperty("text").GetString();
                    await Send(socket, new { type = "ready", protocol = 1 });
                    await Send(socket, new { type = "text", text = "translated:" });
                    await Send(socket, new { type = "text", text });
                    await Send(socket, new { type = "done" });
                    if (text == "最终句。") finalSeen.TrySetResult();
                    return;
                }
                Check(start.RootElement.GetProperty("sample_rate").GetInt32() == 16000, "ASR sample rate");
                await Send(socket, new { type = "ready", protocol = 1 });
                var buffer = new byte[1024];
                var frame = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                Check(frame.MessageType == WebSocketMessageType.Binary && frame.Count == 4, "Binary PCM");
                await Send(socket, new { type = "partial", text = "临时句。" });
                await Send(socket, new { type = "partial", text = "定稿句。", confirmed = "定稿句。" });
                await Send(socket, new { type = "partial", text = "定稿句。修改中的文字", confirmed = "定稿句。" });
                await Send(socket, new { type = "partial", text = "定稿句。定稿句。", confirmed = "定稿句。定稿句。" });
                if (stopEarly) stop.Cancel();
                using var finish = await Receive(socket, deadline.Token);
                Check(finish.RootElement.GetProperty("type").GetString() == "finish", "Finish on capture stop");
                await Send(socket, new { type = "final", text = "定稿句。定稿句。最终句。" });
                await finalSeen.Task.WaitAsync(deadline.Token);
                serverDone.TrySetResult();
            }
            catch (Exception error) { serverDone.TrySetException(error); }
        });
        await listener.StartAsync(deadline.Token);
        var baseUrl = listener.Urls.Single().Replace("http:", "ws:");
        await new SelfHostedTranslationSession(new(TargetLanguage: "en", Backend: "self-hosted"),
            () => { }, translated.Add, (text, _) => Task.FromResult("formatted:" + text))
            .RunAsync(Audio(stopEarly, stop.Token), stop.Token, new Uri(baseUrl + "/asr"), new Uri(baseUrl + "/translation"))
            .WaitAsync(deadline.Token);
        await serverDone.Task.WaitAsync(deadline.Token);
        Check(translated.SequenceEqual(new[] { "formatted:translated:定稿句。", "formatted:translated:定稿句。", "formatted:translated:最终句。" }),
            "Translate committed segments once, preserve repeated speech, and flush the final tail");
    }

    private static async IAsyncEnumerable<byte[]> Audio(bool wait, [EnumeratorCancellation] CancellationToken token)
    {
        yield return [1, 0, 2, 0];
        if (wait) await Task.Delay(Timeout.Infinite, token);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Task Send(WebSocket socket, object value) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value),
        WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonDocument> Receive(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        var size = 0;
        ValueWebSocketReceiveResult message;
        do
        {
            message = await socket.ReceiveAsync(buffer.AsMemory(size), token);
            if (message.MessageType != WebSocketMessageType.Text) throw new IOException("Unexpected frame");
            size += message.Count;
        } while (!message.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, size));
    }
}
