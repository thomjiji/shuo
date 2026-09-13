using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shuo.Services;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class ContextualTranslationTests
{
    internal static async Task RunAsync()
    {
        var source = new CaptionSourceSegments();
        Check(Update("a", "confirmed", "prediction。", false).Count == 0, "Never translate predicted punctuation");
        Check(Update("a", "confirmed。tail", "", false).SequenceEqual(new[] { "confirmed。" }), "Confirmed sentence emits before final");
        Check(Update("a", "confirmed。tail。", "", true).SequenceEqual(new[] { "tail。" }), "Final only emits unconsumed tail");
        Check(Update("a", "confirmed。tail。", "", true).Count == 0, "Duplicate completion ignored");
        Check(Update("b", "confirmed。tail。", "", true).Count == 1, "Same speech in a new item retained");
        Check(Update("c", "hello。", "", false).Count == 1, "Confirmed prefix emitted");
        try { Update("c", "changed。", "", true); throw new Exception("Accepted changed committed source"); }
        catch (IOException) { }
        foreach (var stop in new[] { false, true }) await WireAsync(stop);
        await WireAsync(false, quiet: true);
        Console.WriteLine("Passed contextual subtitle assembly, prediction isolation, repeated speech, context, and stop/tail tests.");

        List<string> Update(string id, string text, string stash, bool final) => source.Update(JsonSerializer.SerializeToElement(new
        {
            type = final ? "conversation.item.input_audio_transcription.completed" : "conversation.item.input_audio_transcription.text",
            item_id = id, text, transcript = text, stash,
        })).ToList();
    }

    private static async Task WireAsync(bool cancelCapture, bool quiet = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var listener = builder.Build();
        listener.UseWebSockets();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();
        var initialDecision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCaption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCaption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdCaption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captions = new List<string>();
        var forcedTail = false;
        listener.Run(async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await Send(socket, new { type = "session.created" });
                using var update = await Receive(socket);
                var session = update.RootElement.GetProperty("session");
                Check(!session.TryGetProperty("translation", out _), "ASR does not request a competing audio translation");
                Check(!session.GetProperty("input_audio_transcription").TryGetProperty("language", out _), "Automatic language detection omits null language");
                await Send(socket, new { type = "session.updated" });
                using var packet = await Receive(socket);
                Check(packet.RootElement.GetProperty("type").GetString() == "input_audio_buffer.append", "Audio reaches ASR");
                await Source("one", "topic");
                await initialDecision.Task.WaitAsync(deadline.Token);
                Check(captions.Count == 0, "Incomplete topic is withheld");
                await Source("two", "predicate");
                await Source("two", "predicate");
                await firstCaption.Task.WaitAsync(deadline.Token);
                await Source("three", "topic predicate");
                await secondCaption.Task.WaitAsync(deadline.Token);
                if (quiet)
                {
                    await Source("four", "tail fragment");
                    await Send(socket, new { type = "input_audio_buffer.speech_stopped" });
                    await thirdCaption.Task.WaitAsync(deadline.Token);
                }
                if (cancelCapture) stop.Cancel(); else endAudio.TrySetResult();
                using var finish = await Receive(socket);
                Check(finish.RootElement.GetProperty("type").GetString() == "session.finish", "Finish on stop");
                if (!quiet) await Source("four", "tail fragment");
                await Send(socket, new { type = "session.finished" });
                await socket.ReceiveAsync(new byte[64].AsMemory(), deadline.Token);
                async Task Source(string id, string text) => await Send(socket, new
                    { type = "conversation.item.input_audio_transcription.completed", item_id = id, transcript = text });
                serverDone.TrySetResult();
            }
            catch (Exception error) { serverDone.TrySetException(error); }
        });
        await listener.StartAsync(deadline.Token);
        var endpoint = new Uri(listener.Urls.Single().Replace("http:", "ws:"));
        var task = new ContextualTranslationSession(new(WorkspaceId: "test"), "test-key", () => { }, text =>
        {
            captions.Add(text);
            if (captions.Count == 1) firstCaption.TrySetResult();
            if (captions.Count == 2) secondCaption.TrySetResult();
            if (captions.Count == 3) thirdCaption.TrySetResult();
        }, _ => { }, (text, _) => Task.FromResult("formatted:" + text)).RunAsync(stop.Token, Audio(), endpoint, Translate);
        await task.WaitAsync(deadline.Token);
        await serverDone.Task.WaitAsync(deadline.Token);
        Check(captions.SequenceEqual(new[] { "formatted:main", "formatted:main", "formatted:tail" }), "Ordered captions, repeated speech and final tail delivered exactly once");
        Check(forcedTail, "An unfinished trailing fragment is flushed at session end");

        Task<CaptionTranslation> Translate(IReadOnlyList<string> pending, IReadOnlyList<CaptionContext> history, bool final, CancellationToken token)
        {
            if (pending.SequenceEqual(new[] { "topic" }))
            {
                initialDecision.TrySetResult();
                return Task.FromResult(new CaptionTranslation(0, ""));
            }
            if (pending.SequenceEqual(new[] { "topic", "predicate" }))
            {
                Check(history.Count == 0, "First caption has no fabricated context");
                return Task.FromResult(new CaptionTranslation(2, "main"));
            }
            if (pending.SequenceEqual(new[] { "topic predicate" }))
            {
                Check(history.Count == 1 && history[0].Source == "topic predicate" && history[0].Translation == "main", "Raw source and translation retained as context");
                return Task.FromResult(new CaptionTranslation(1, "main"));
            }
            Check(pending.SequenceEqual(new[] { "tail fragment" }), "Tail source remains intact");
            forcedTail |= final;
            return Task.FromResult(final ? new CaptionTranslation(1, "tail") : new CaptionTranslation(0, ""));
        }

        async IAsyncEnumerable<byte[]> Audio([EnumeratorCancellation] CancellationToken token = default)
        {
            yield return [0, 0, 1, 0];
            await endAudio.Task.WaitAsync(token);
        }
    }

    private static Task Send(WebSocket socket, object value) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonDocument> Receive(WebSocket socket)
    {
        var buffer = new byte[16384];
        var count = 0;
        ValueWebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None); count += result.Count; } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, count));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
