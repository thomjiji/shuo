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
Check(ReadingText.Split("第一段。\n第二段！").SequenceEqual(new[] { "第一段。\n第二段！" }), "paragraphs retain sentence context in one request");
Check(ReadingText.Split(new string('汉', 300)).Count == 1, "long paragraphs are not split into short sentences");
var fits = new string('汉', 450) + "。\n" + new string('字', 100);
Check(ReadingText.Split(fits).SequenceEqual(new[] { fits }), "entire input within budget is sent once despite punctuation");
var paragraph = new string('汉', 300) + "。\r\n\r\n";
var paragraphText = paragraph + new string('字', 350);
Check(ReadingText.Split(paragraphText).SequenceEqual(new[] { paragraph, new string('字', 350) }), "overflow splits at the preceding paragraph rather than inside the next one");
var latestParagraph = new string('a', 800) + "\n" + new string('b', 700) + "\n";
Check(ReadingText.Split(latestParagraph + new string('c', 500))[0] == latestParagraph, "packs as many complete paragraphs as fit");
var sentence = new string('汉', 400) + "。";
Check(ReadingText.Split(sentence + new string('字', 300))[0] == sentence, "oversized single paragraph falls back to a sentence boundary");
Check(ReadingText.Split(new string('a', 1800)).Count == 1, "exact request byte budget remains a single request");
var crlfBoundary = new string('a', 1799) + "\r\n" + "end";
Check(ReadingText.Split(crlfBoundary).SequenceEqual(new[] { new string('a', 1799), "\r\nend" }), "CRLF is not split at the request byte boundary");
Check((await Parse(": keepalive\n\nevent: audio\ndata: {\"code\":0,\"data\":\"AQIDBA==\"}\n\ndata: {\"code\":20000000}\n\n")).SequenceEqual(new byte[] {1,2,3,4}), "SSE audio and completion decoded");
Check((await Parse("data: {\"code\":20000000,\"data\":\"AQI=\"}")).Length == 2, "final audio without trailing newline retained");
await Fails<IOException>(() => Parse("data: {\"code\":20000000}\n\n"), "empty success is a failure");
await Fails<IOException>(() => Parse("data: {\"code\":0,\"data\":\"AQI=\"}\n\n"), "truncated stream is not reported complete");
await Fails<IOException>(() => Parse("data: {\"code\":55000000,\"message\":\"private input\"}\n\n"), "service error rejected");
await Fails<FormatException>(() => Parse("data: {\"code\":0,\"data\":\"!\"}\n\n"), "invalid audio rejected");
await Fails<JsonException>(() => Parse("data: not-json\n\n"), "invalid event rejected");

var migrated = JsonSerializer.Deserialize<ReadingOptions>("{\"Enabled\":true}")!;
Check(migrated.Hotkey == new HotkeyBinding(3, 0x20), "missing shortcut uses Ctrl Alt Space");
var custom = new ReadingOptions(HotkeyModifiers: 6, HotkeyVirtualKey: 0x79);
Check(JsonSerializer.Deserialize<ReadingOptions>(JsonSerializer.Serialize(custom))!.Hotkey == custom.Hotkey, "custom reading shortcut survives settings roundtrip");
Check(ReadingVoices.All.Any(voice => voice.Id == new ReadingOptions().Speaker), "default voice appears in picker");
Check(ReadingVoices.All.Any(voice => voice.Name == "温柔妈妈 2.0" && voice.Id == "zh_female_wenroumama_uranus_bigtts"), "saved voice ID maps to its display name");
Check(ReadingVoices.All.Select(voice => voice.Id).Distinct(StringComparer.Ordinal).Count() == ReadingVoices.All.Length, "voice picker IDs are unique");
await Fails<ArgumentException>(() => { new ReadingOptions(HotkeyModifiers: 0).Validate(); return Task.CompletedTask; }, "invalid reading shortcut rejected");
byte[] CopyMetadata(string value)
{
    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory, Encoding.Unicode, leaveOpen: true);
    writer.Write(0); writer.Write(1);
    foreach (var part in new[] { "editor-copy-data", value })
    {
        writer.Write(part.Length);
        writer.Write(Encoding.Unicode.GetBytes(part));
        while (memory.Length % 4 != 0) writer.Write((byte)0);
    }
    var payload = memory.ToArray();
    BitConverter.GetBytes(payload.Length - 4).CopyTo(payload, 0);
    return payload;
}
Check(ClipboardSelectionMetadata.IsEmptySelection(CopyMetadata("{\"isFromEmptySelection\":true}")), "empty-selection copy metadata rejects copied line");
Check(!ClipboardSelectionMetadata.IsEmptySelection(CopyMetadata("{\"isFromEmptySelection\":false,\"multicursorText\":[\"first\",\"second\"]}")), "multiple selections remain readable");
Check(!ClipboardSelectionMetadata.IsEmptySelection(new byte[] {255,255,255,255,1,0,0,0}), "invalid clipboard metadata stays bounded");
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
var translatedOptions = new ReadingOptions(TranslateToChinese: true);
Check(!migrated.TranslateToChinese && JsonSerializer.Deserialize<ReadingOptions>(JsonSerializer.Serialize(translatedOptions))!.TranslateToChinese,
    "old settings keep original reading and Chinese mode persists");
var english = string.Concat(Enumerable.Repeat("This is a complete sentence with context. ", 100));
var passages = ReadingText.Split(english, OmniReadingClient.PassageBytes);
Check(string.Concat(passages) == english && passages.All(p => Encoding.UTF8.GetByteCount(p) <= 900), "translated passages are bounded without losing source text");
var words = string.Join(" ", Enumerable.Repeat("selection", 120));
Check(ReadingText.Split(words, 900)[0].EndsWith(' '), "oversized English sentences split between words");
await Fails<ArgumentException>(() => Task.FromResult(OmniReadingClient.Endpoint("a/b", "cn-beijing")), "workspace cannot change request host or path");
await Fails<ArgumentException>(() => Task.FromResult(OmniReadingClient.Endpoint("workspace", "other")), "unknown Omni region rejected");
var translatedText = new StringBuilder();
async Task<byte[]> ParseOmni(string events)
{
    translatedText.Clear();
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(events));
    var pcm = new List<byte>();
    await foreach (var bytes in OmniReadingClient.ReadEventsAsync(stream, part => translatedText.Append(part), default)) pcm.AddRange(bytes);
    return pcm.ToArray();
}
const string omniText = "data: {\"choices\":[{\"delta\":{\"content\":\"你好。\"}}]}\n\n";
const string omniAudio = "data: {\"choices\":[{\"delta\":{\"audio\":{\"data\":\"AQIDBA==\"}}}]}\n\n";
const string omniFinish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
const string omniSuccess = omniText + omniAudio + omniFinish + "data: [DONE]\n\n";
Check((await ParseOmni(": keepalive\n\n" + omniSuccess)).SequenceEqual(new byte[] {1,2,3,4}) && translatedText.ToString() == "你好。",
    "Omni streams raw PCM and translated text without replaying deltas");
Check((await ParseOmni(omniText + omniAudio + omniFinish.TrimEnd())).Length == 4, "final event at EOF is processed");
await Fails<IOException>(() => ParseOmni(omniText + omniAudio), "truncated Omni stream never reports completion");
await Fails<IOException>(() => ParseOmni(omniText + omniFinish), "text-only Omni response is rejected");
await Fails<IOException>(() => ParseOmni(omniAudio + omniFinish), "missing translation is rejected");
await Fails<IOException>(() => ParseOmni(omniText + omniAudio + "data: {\"choices\":[{\"finish_reason\":\"length\"}]}\n\n"), "incomplete generation is reported");
await Fails<IOException>(() => ParseOmni("data: {\"error\":{\"message\":\"private input\"}}\n\n"), "Omni service error is sanitized");
await Fails<IOException>(() => ParseOmni("data: not-json\n\n"), "invalid JSON is rejected");
await Fails<IOException>(() => ParseOmni(omniSuccess.Replace("AQIDBA==", "!")), "invalid audio encoding is rejected");
await Fails<IOException>(() => ParseOmni(omniSuccess.Replace("AQIDBA==", "AQ==")), "odd PCM sample rejected");
using (var cancelled = new CancellationTokenSource())
using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(omniSuccess)))
{
    cancelled.Cancel();
    await Fails<OperationCanceledException>(async () =>
    {
        await foreach (var _ in OmniReadingClient.ReadEventsAsync(stream, _ => throw new Exception("Cancelled text was delivered"), cancelled.Token))
            throw new Exception("Cancelled audio was delivered");
    }, "cancelled Omni stream delivers no text or audio");
}
var omniHandler = new OmniHandler(omniSuccess);
foreach (var source in new[] { "会議は明日の午後三時です。", "회의는 내일입니다.", "明日。Tomorrow. 明天。" })
{
    var languageHandler = new OmniHandler(omniSuccess);
    using var languageHttp = new HttpClient(languageHandler);
    await foreach (var _ in new OmniReadingClient(languageHttp).ReadAsync(source, OmniReadingClient.Endpoint("workspace", "cn-beijing"), "test-key", _ => { }, default)) { }
    using var languageBody = JsonDocument.Parse(languageHandler.Body!);
    var prompt = languageBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    Check(prompt.Contains("自动识别") && prompt.Contains("简体中文") && prompt.EndsWith(source), "multilingual input reaches Chinese translation request unchanged");
}
Check(JsonSerializer.Deserialize<ReadingOptions>("{\"TranslateToChinese\":true}")!.TranslationSpeechRate == 1,
    "existing reading settings default translated speech to slightly faster");
foreach (var (rate, phrase) in new[] { (-1, "偏慢"), (0, "正常的语速"), (1, "稍快"), (2, "明显偏快") })
{
    var paceHandler = new OmniHandler(omniSuccess);
    using var paceHttp = new HttpClient(paceHandler);
    await foreach (var _ in new OmniReadingClient(paceHttp).ReadAsync("Hello.", OmniReadingClient.Endpoint("workspace", "cn-beijing"), "test-key", _ => { }, default, rate)) { }
    using var paceBody = JsonDocument.Parse(paceHandler.Body!);
    var prompt = paceBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    Check(prompt.Contains(phrase) && prompt.EndsWith("Hello."), $"translated speech pace {rate} reaches request and preserves source");
}
using (var omniHttp = new HttpClient(omniHandler))
{
    var endpoint = OmniReadingClient.Endpoint("workspace", "cn-beijing");
    await foreach (var _ in new OmniReadingClient(omniHttp).ReadAsync("Hello.", endpoint, "test-key", _ => { }, default)) { }
    using var body = JsonDocument.Parse(omniHandler.Body!);
    Check(omniHandler.Endpoint == endpoint && omniHandler.Authorization == "Bearer test-key"
        && body.RootElement.GetProperty("model").GetString() == "qwen3.5-omni-flash"
        && body.RootElement.GetProperty("audio").GetProperty("voice").GetString() == "Tina", "Omni receives configured endpoint, credentials and tested model/voice");
}
using (var omniHttp = new HttpClient(new OmniHandler("private service body", HttpStatusCode.Unauthorized)))
{
    await Fails<HttpRequestException>(async () =>
    {
        await foreach (var _ in new OmniReadingClient(omniHttp).ReadAsync("Hello.", OmniReadingClient.Endpoint("workspace", "cn-beijing"), "test-key", _ => { }, default)) { }
    }, "Omni HTTP failures are reported");
}
checks += await LocalProtocolChecks.Run();
Console.WriteLine($"Passed {checks} checks.");

sealed class OmniHandler(string events, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public string? Body, Authorization;
    public Uri? Endpoint;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Body = await request.Content!.ReadAsStringAsync(token);
        Endpoint = request.RequestUri;
        Authorization = request.Headers.Authorization?.ToString();
        return new(status) { Content = new StringContent(events) };
    }
}

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
