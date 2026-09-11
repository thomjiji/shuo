using System.Net;
using System.Text;
using System.Text.Json;
using Shuo.Services;

var checks = 0;
void Check(bool passed, string name)
{
    if (!passed) throw new Exception(name);
    checks++;
    Console.WriteLine("ok " + name);
}
async Task Fails<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("Expected failure: " + name);
}
async Task<byte[]> Parse(string events)
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(events));
    var result = new List<byte>();
    await foreach (var bytes in DoubaoSpeechClient.ReadEventsAsync(stream, default)) result.AddRange(bytes);
    return result.ToArray();
}

var longText = string.Concat(Enumerable.Repeat("汉字😀", 1500));
var chunks = ReadingText.Split(longText);
Check(string.Concat(chunks) == longText && chunks.All(x => Encoding.UTF8.GetByteCount(x) <= 1800), "long Unicode text is lossless and requests stay bounded");
await Fails<ArgumentException>(() => Task.FromResult(ReadingText.Split("  \n")), "empty text rejected");
await Fails<ArgumentException>(() => Task.FromResult(ReadingText.Split(new string('x', 30001))), "oversized input rejected before sending");
Check(ReadingText.Split("第一段。\n第二段！").Count > 0, "short paragraphs accepted");
Check((await Parse(": keepalive\n\nevent: audio\ndata: {\"code\":0,\"data\":\"AQIDBA==\"}\n\ndata: {\"code\":20000000}\n\n")).SequenceEqual(new byte[] {1,2,3,4}), "SSE audio and completion decoded");
Check((await Parse("data: {\"code\":20000000,\"data\":\"AQI=\"}")).Length == 2, "final audio without trailing newline retained");
await Fails<IOException>(() => Parse("data: {\"code\":20000000}\n\n"), "empty success is a failure");
await Fails<IOException>(() => Parse("data: {\"code\":0,\"data\":\"AQI=\"}\n\n"), "truncated stream is not reported complete");
await Fails<IOException>(() => Parse("data: {\"code\":55000000,\"message\":\"private input\"}\n\n"), "service error rejected");
await Fails<FormatException>(() => Parse("data: {\"code\":0,\"data\":\"!\"}\n\n"), "invalid audio rejected");
await Fails<JsonException>(() => Parse("data: not-json\n\n"), "invalid event rejected");

var handler = new RecordingHandler();
using var client = new HttpClient(handler);
var tts = new DoubaoSpeechClient(client);
await foreach (var _ in tts.SynthesizeAsync("hello", new(), "test-key", default)) { }
Check(handler.Resource == "seed-tts-2.0" && handler.ApiKey == "test-key", "TTS uses the configured resource and API key");
using (var body = JsonDocument.Parse(handler.Body!))
{
    var req = body.RootElement.GetProperty("req_params");
    Check(req.GetProperty("speaker").GetString() == "zh_female_vv_uranus_bigtts" && req.GetProperty("audio_params").GetProperty("sample_rate").GetInt32() == 24000, "voice ID and PCM sample rate reach service");
}
await Fails<ArgumentException>(async () =>
{
    await foreach (var _ in tts.SynthesizeAsync("hello", new(Speaker: ""), "test-key", default)) { }
}, "empty voice rejected locally");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Fails<OperationCanceledException>(async () =>
    {
        await foreach (var _ in tts.SynthesizeAsync("hello", new(), "test-key", cancelled.Token)) { }
    }, "cancelled request does not play");
}
Console.WriteLine($"Passed {checks} checks.");

sealed class RecordingHandler : HttpMessageHandler
{
    public string? Body, Resource, ApiKey;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Resource = request.Headers.GetValues("X-Api-Resource-Id").Single();
        ApiKey = request.Headers.GetValues("X-Api-Key").Single();
        Body = await request.Content!.ReadAsStringAsync(token);
        return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"code\":20000000,\"data\":\"AAA=\"}\n\n") };
    }
}
