using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shuo.Services;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

if (args.Length == 6 && args[0] == "--latency")
{
    var options = new TranslationOptions(args[1], args[2], "zh");
    var key = TranslationSettings.ResolveApiKey(TranslationSettings.LoadApiKey(), options.Region);
    if (string.IsNullOrWhiteSpace(key)) throw new Exception("No saved Bailian API key.");
    var source = await File.ReadAllBytesAsync(args[3]);
    var pcm = new byte[source.Length + 64000];
    source.CopyTo(pcm, 0);
    var watch = new Stopwatch();
    var records = new List<object>();
    var formatTimes = new List<double>();
    var last = "";
    await new TranslationSession(options, key, () => watch.Start(), text =>
    {
        last = text;
        records.Add(new { stage = "display", seconds = watch.Elapsed.TotalSeconds, text });
    }, _ => { }, async (text, token) =>
    {
        var timer = Stopwatch.StartNew();
        var value = await TranscriptFormatter.FormatAsync(text, args[4], token);
        formatTimes.Add(timer.Elapsed.TotalMilliseconds);
        return value;
    }, int.Parse(args[5]), value =>
    {
        var type = value.GetProperty("type").GetString();
        if (type == "response.text.text" || type == "response.text.done")
            records.Add(new { stage = type, seconds = watch.Elapsed.TotalSeconds,
                text = value.TryGetProperty("text", out var text) ? text.GetString() : "",
                stash = value.TryGetProperty("stash", out var stash) ? stash.GetString() : "" });
        else if (type is "input_audio_buffer.speech_started" or "input_audio_buffer.speech_stopped")
            records.Add(new { stage = type, seconds = watch.Elapsed.TotalSeconds });
    }).RunAsync(CancellationToken.None, TimedReplay(pcm));
    Console.WriteLine(JsonSerializer.Serialize(new { silenceMs = int.Parse(args[5]),
        audioSeconds = source.Length / 32000.0, formatMeanMs = formatTimes.DefaultIfEmpty().Average(),
        final = last, records }));
    return;
}
if (args.Length == 2 && args[0] == "--autocorrect")
{
    var snapshots = new[] { "中文Open", "中文OpenAI", "中文OpenAI研究人员在2020年3月", "中文OpenAI研究人员在2020年3月工作。" };
    var watch = Stopwatch.StartNew();
    foreach (var raw in snapshots)
    {
        var formatted = await TranscriptFormatter.FormatAsync(raw, args[1]);
        if (!formatted.Contains("中文 Open") || formatted.Contains("  ")) throw new Exception("Mixed-language spacing failed.");
        if (await TranscriptFormatter.FormatAsync(formatted, args[1]) != formatted) throw new Exception("Formatting must be idempotent.");
        Console.WriteLine(formatted);
    }
    Console.WriteLine($"Formatting checks: {watch.Elapsed.TotalMilliseconds:F0} ms total.");
    return;
}
if (args.Length > 0 && args[0] == "--live")
{
    if (args.Length != 4) throw new ArgumentException("--live REGION WORKSPACE PCM_FILE");
    var options = new TranslationOptions(args[1], args[2], "zh");
    var ownKey = TranslationSettings.LoadApiKey();
    var key = TranslationSettings.ResolveApiKey(ownKey, options.Region);
    if (string.IsNullOrWhiteSpace(key)) throw new Exception("No saved Bailian API key.");
    TranslationSettings.Save(options, ownKey);
    var pcm = await File.ReadAllBytesAsync(args[3]);
    var watch = new Stopwatch();
    var first = true;
    await new TranslationSession(options, key, () => { watch.Start(); Console.WriteLine("Ready"); }, text =>
    {
        if (first) { Console.WriteLine($"First caption: {watch.Elapsed.TotalSeconds:F2}s"); first = false; }
        Console.WriteLine(text);
    }, _ => { }).RunAsync(CancellationToken.None, Replay(pcm));
    Console.WriteLine($"Finished: {watch.Elapsed.TotalSeconds:F2}s");
    return;
}

void Assert(bool value, string message) { if (!value) throw new Exception(message); }
string? Caption(TranslationCaptions captions, string type, string id, string text, string stash = "")
{
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { type, item_id = id, text, stash }));
    return captions.Update(document.RootElement);
}
Assert(SystemAudioSource.LevelFromRms(0) == 0, "Silence has no meter activity");
Assert(SystemAudioSource.LevelFromRms(0.001) == 0, "Noise below microphone meter floor is hidden");
Assert(Math.Abs(SystemAudioSource.LevelFromRms(0.01) - 0.375) < 0.0001, "Playback and microphone share loudness scale");
Assert(SystemAudioSource.LevelFromRms(1) == 1, "Meter clips safely at full scale");
var captions = new TranslationCaptions();
Assert(Caption(captions, "response.text.text", "a", "", "wrong guess") is null, "Do not show predictions before confirmation");
Assert(Caption(captions, "response.text.text", "a", "Hello", " wrong") == "Hello", "Show confirmed text only");
Assert(Caption(captions, "response.text.text", "a", "Hello", " world") is null, "Prediction revisions do not redraw subtitles");
Assert(Caption(captions, "response.text.text", "a", "Hello world", "!") == "Hello world", "Append newly confirmed words");
Assert(Caption(captions, "response.text.done", "a", "Hello world.") == "Hello world.", "Deliver final suffix");
Assert(Caption(captions, "response.text.text", "b", "Goodbye", "!") == "Hello world. Goodbye", "Keep preceding item");
Assert(Caption(captions, "response.text.done", "a", "Hello world.") is null, "Duplicate final does not redraw");
Assert(Caption(captions, "response.text.text", "a", "Late obsolete text") is null, "Finalized item cannot be revised by late preview");
Assert(Caption(captions, "conversation.item.input_audio_transcription.text", "x", "source") is null, "Ignore source transcript");
for (var i = 0; i < 30; i++) Caption(captions, "response.text.done", i.ToString(), new string('a', 100));
Assert(Caption(captions, "response.text.done", "last", "tail") is { Length: <= 1200 }, "Bound caption memory");
foreach (var invalid in new[] { "", "../evil", "a.evil", "a?x", "a/b", "a\r\n" })
{
    try { TranslationSession.Endpoint(new(WorkspaceId: invalid)); throw new Exception("Accepted invalid workspace"); }
    catch (ArgumentException) { }
}
Assert(TranslationSession.Endpoint(new(WorkspaceId: "ws-example")).Host == "ws-example.cn-beijing.maas.aliyuncs.com", "Regional host");

await CheckWire(false, false);
await CheckWire(true, false);
await CheckWire(false, true);
Console.WriteLine("Passed caption revision, endpoint validation, audio framing, graceful stop, tail delivery, and server error tests.");

async Task CheckWire(bool cancelCapture, bool fail)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var listener = builder.Build();
    listener.UseWebSockets();
    using var stop = new CancellationTokenSource();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var final = "";
    var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.Run(async context =>
    {
        try
        {
        Assert(context.Request.Headers.Authorization.ToString() == "Bearer test-key", "Bearer auth");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await Send(socket, new { type = "session.created" });
        using var update = await Receive(socket);
        var session = update.RootElement.GetProperty("session");
        Assert(update.RootElement.GetProperty("type").GetString() == "session.update", "Configure before capture");
        Assert(session.GetProperty("modalities").GetArrayLength() == 1 && session.GetProperty("modalities")[0].GetString() == "text", "Text-only output");
        Assert(session.GetProperty("sample_rate").GetInt32() == 16000, "16 kHz audio");
        Assert(session.GetProperty("turn_detection").GetProperty("silence_duration_ms").GetInt32() == 500, "Use shorter trailing silence");
        Assert(session.GetProperty("input_audio_transcription").GetProperty("model").ValueKind == JsonValueKind.Null, "Disable source ASR");
        if (fail)
        {
            await Send(socket, new { type = "error", error = new { code = "AccessDenied", message = "secret-must-not-leak" } });
            try { await socket.ReceiveAsync(new byte[64].AsMemory(), deadline.Token); }
            catch (WebSocketException) { }
            return;
        }
        await Send(socket, new { type = "session.updated" });
        using var packet = await Receive(socket);
        Assert(packet.RootElement.GetProperty("type").GetString() == "input_audio_buffer.append", "Audio event");
        Assert(Convert.FromBase64String(packet.RootElement.GetProperty("audio").GetString()!).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Exact PCM bytes");
        if (cancelCapture) stop.Cancel();
        using var finish = await Receive(socket);
        Assert(finish.RootElement.GetProperty("type").GetString() == "session.finish", "Finish after capture");
        await Send(socket, new { type = "response.text.done", item_id = "tail", text = "Final tail." });
        await Send(socket, new { type = "session.finished" });
        var close = new byte[64];
        await socket.ReceiveAsync(close.AsMemory(), deadline.Token);
        }
        catch (Exception error) { server.TrySetException(error); }
        finally { server.TrySetResult(); }
    });
    await listener.StartAsync(deadline.Token);
    var endpoint = new Uri(listener.Urls.Single().Replace("http:", "ws:"));
    var client = new TranslationSession(new(WorkspaceId: "ws-example"), "test-key", () => { }, text => final = text, _ => { }, async (text, token) => { await Task.Delay(5, token); return "formatted:" + text; })
        .RunAsync(stop.Token, Packets(cancelCapture, stop.Token), endpoint);
    if (fail)
    {
        try { await client.WaitAsync(deadline.Token); throw new Exception("Expected service error"); }
        catch (IOException error) { Assert(error.Message.Contains("AccessDenied") && !error.Message.Contains("secret-must-not-leak"), "Safe actionable service error"); }
    }
    else
    {
        await client.WaitAsync(deadline.Token);
        Assert(final == "formatted:Final tail.", "Receive final translation after stop");
    }
    await server.Task.WaitAsync(deadline.Token);
}

static async IAsyncEnumerable<byte[]> Packets(bool wait, [EnumeratorCancellation] CancellationToken token)
{
    yield return [1, 2, 3, 4];
    if (wait) await Task.Delay(Timeout.Infinite, token);
}
static async IAsyncEnumerable<byte[]> Replay(byte[] pcm, [EnumeratorCancellation] CancellationToken token = default)
{
    for (var offset = 0; offset < pcm.Length; offset += 6400)
    {
        yield return pcm.AsSpan(offset, Math.Min(6400, pcm.Length - offset)).ToArray();
        await Task.Delay(200, token);
    }
}
static Task Send(WebSocket socket, object message) =>
    socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
static async Task<JsonDocument> Receive(WebSocket socket)
{
    var buffer = new byte[16384];
    var count = 0;
    ValueWebSocketReceiveResult result;
    do { result = await socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None); count += result.Count; }
    while (!result.EndOfMessage);
    return JsonDocument.Parse(buffer.AsMemory(0, count));
}

static async IAsyncEnumerable<byte[]> TimedReplay(byte[] pcm, [EnumeratorCancellation] CancellationToken token = default)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(64));
    for (var offset = 0; offset < pcm.Length; offset += 2048)
    {
        await timer.WaitForNextTickAsync(token);
        yield return pcm.AsSpan(offset, Math.Min(2048, pcm.Length - offset)).ToArray();
    }
}