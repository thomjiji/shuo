using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Windows.Security.Credentials;
using Shuo.Services;

// Opt-in live comparison. Run from the repository root. Never prints or stores credentials.
var output = Path.GetFullPath("artifacts/test/reading-comparison");
Directory.CreateDirectory(output);
using var settings = JsonDocument.Parse(File.ReadAllText(HotkeySettings.GetPath()));
var translation = settings.RootElement.GetProperty("translation");
var workspace = translation.GetProperty("workspaceId").GetString()!;
var region = translation.GetProperty("region").GetString()!;
if (!System.Text.RegularExpressions.Regex.IsMatch(workspace, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,62}\z")
    || region is not ("cn-beijing" or "ap-southeast-1")) throw new Exception("Invalid translation endpoint settings.");
var endpoint = new Uri($"https://{workspace}.{region}.maas.aliyuncs.com/compatible-mode/v1/chat/completions");
var credential = new PasswordVault().Retrieve("shuo-qwen-translation", HotkeySettings.GetPath());
credential.RetrievePassword();
var qwenKey = credential.Password;
if (args.Contains("--multilingual"))
{
    using var liveHttp = new HttpClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    foreach (var source in new[] {
        "会議は明日の午後三時に始まります。資料を忘れないでください。参加費は無料です。",
        "회의는 내일 오후 세 시에 시작합니다. 참가비는 무료입니다.",
        "明日の会議は午後三時です。Please bring the report. 地点是二楼会议室。" })
    {
        var result = new StringBuilder();
        var bytes = 0L;
        await foreach (var pcm in new OmniReadingClient(liveHttp).ReadAsync(source, endpoint, qwenKey,
            part => result.Append(part), timeout.Token)) bytes += pcm.Length;
        Console.WriteLine($"Source: {source}\nChinese: {result}\nPCM bytes: {bytes}");
    }
    return;
}
var reading = ReadingSettings.Load();
var speechKey = reading.UseExistingKey ? CloudSettings.Load().ApiKey : ReadingSettings.LoadApiKey();
Console.WriteLine($"Credentials: translation={!string.IsNullOrWhiteSpace(qwenKey)}, speech={!string.IsNullOrWhiteSpace(speechKey)}; region={region}; voice={reading.Speaker}; rate={reading.SpeechRate}");
if (!args.Contains("--run")) return;
var model = args.FirstOrDefault(a => a.StartsWith("--model="))?.Split('=', 2)[1] ?? "qwen3.5-omni-flash";
var voice = args.FirstOrDefault(a => a.StartsWith("--voice="))?.Split('=', 2)[1] ?? "Tina";
var rounds = args.Contains("--probe") ? 1 : 2;
var samples = new[]
{
    ("short", "Please save your work before restarting the computer. The update should take about five minutes, and your files will not be deleted."),
    ("numbers", "Revenue rose by 12.5 percent to 3.2 million dollars in the second quarter. However, net profit fell by 8 percent because shipping costs increased. The company did not lower its forecast for the full year. A lower growth rate does not mean that revenue declined."),
    ("article", "When a reading app translates an article, it should preserve the author's meaning rather than summarize it. The first sentence can be spoken while the remaining paragraphs are still being processed. However, starting too early can split a phrase in the wrong place. For example, 'not only faster, but also more reliable' describes two advantages, not a trade-off.\n\nPausing playback should keep the current position. Resuming should continue from that position without repeating the previous sentence. Stopping should cancel pending requests and discard buffered audio. If the network fails halfway through, the app should explain what happened instead of claiming that reading is complete.\n\nThis design does not require every stage to use the same model. Translation quality, voice quality, and response time should be evaluated separately. A single service may simplify integration, but it is not necessarily faster. The final choice should be based on measurements and listening tests, not on the number of API calls.")
};
var results = new List<object>();
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
for (var round = 1; round <= rounds; round++)
foreach (var (id, source) in args.Contains("--probe") ? samples.Take(1) : samples)
foreach (var route in round % 2 == 1 ? new[] { "cascade", "omni" } : new[] { "omni", "cascade" })
{
    var name = $"{id}-{route}-{round}";
    var watch = Stopwatch.StartNew();
    double? firstText = null, firstAudio = null, playable = null;
    var text = new StringBuilder();
    using var audio = new MemoryStream();
    var arrivals = new List<object>();
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var token = stop.Token;
    string? error = null;
    void Audio(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        firstAudio ??= watch.Elapsed.TotalSeconds;
        audio.Write(bytes);
        if (audio.Length >= 9600) playable ??= watch.Elapsed.TotalSeconds;
        arrivals.Add(new { seconds = watch.Elapsed.TotalSeconds, bytes = audio.Length });
    }
    try
    {
        if (route == "omni")
        {
            await Stream(new
            {
                model, stream = true, stream_options = new { include_usage = true },
                messages = new[] { new { role = "user", content = "将以下英文完整、忠实地翻译成简体中文并朗读。保留所有信息、数字、否定和段落顺序。只输出中文译文，不要解释、总结或添加开场白。\n\n" + source } },
                modalities = new[] { "text", "audio" }, audio = new { voice, format = "wav" },
            }, delta =>
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } part)
                { firstText ??= watch.Elapsed.TotalSeconds; text.Append(part); }
                if (delta.TryGetProperty("audio", out var a) && a.TryGetProperty("data", out var data))
                    Audio(Convert.FromBase64String(data.GetString()!));
            }, token);
        }
        else
        {
            // Translate the complete source for context; start TTS at confirmed sentence boundaries.
            var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var synthesis = Consume();
            try
            {
                var pending = new StringBuilder();
                await Stream(new
                {
                    model = "qwen-mt-flash", stream = true, stream_options = new { include_usage = true },
                    messages = new[] { new { role = "user", content = source } },
                    translation_options = new { source_lang = "English", target_lang = "Chinese" },
                }, delta =>
                {
                    if (!delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return;
                    var part = content.GetString()!;
                    if (part.Length > 0) firstText ??= watch.Elapsed.TotalSeconds;
                    text.Append(part);
                    foreach (var c in part)
                    {
                        pending.Append(c);
                        if (c is '。' or '！' or '？' or '\n')
                        {
                            if (!string.IsNullOrWhiteSpace(pending.ToString())) queue.Writer.TryWrite(pending.ToString());
                            pending.Clear();
                        }
                    }
                }, token);
                if (!string.IsNullOrWhiteSpace(pending.ToString())) queue.Writer.TryWrite(pending.ToString());
                queue.Writer.TryComplete();
                await synthesis;
            }
            finally
            {
                queue.Writer.TryComplete();
                stop.Cancel();
                try { await synthesis; } catch (OperationCanceledException) { }
            }
            async Task Consume()
            {
                try
                {
                    await foreach (var sentence in queue.Reader.ReadAllAsync(token))
                        foreach (var chunk in ReadingText.Split(sentence))
                            await foreach (var bytes in new DoubaoSpeechClient(http).SynthesizeAsync(chunk, reading, speechKey, token)) Audio(bytes);
                }
                catch { stop.Cancel(); throw; }
            }
        }
        if (audio.Length == 0 || audio.Length % 2 != 0 || text.Length == 0) throw new IOException("Incomplete text/audio output");
        playable ??= watch.Elapsed.TotalSeconds;
        WriteWave(Path.Combine(output, name + ".wav"), audio.ToArray());
    }
    catch (Exception ex)
    {
        error = ex is HttpRequestException ? ex.Message : ex.GetType().Name;
    }
    var result = new { name, route, round, model = route == "omni" ? model : "qwen-mt-flash + seed-tts-2.0", voice = route == "omni" ? voice : reading.Speaker,
        source, translation = text.ToString(), firstText, firstAudio, playable, total = watch.Elapsed.TotalSeconds, duration = audio.Length / 48000d, error, arrivals };
    results.Add(result);
    await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{name}: firstText={firstText:F2}s firstAudio={firstAudio:F2}s playable={playable:F2}s total={watch.Elapsed.TotalSeconds:F2}s audio={audio.Length / 48000d:F1}s error={error ?? "none"}");
    if (error != null && args.Contains("--probe")) break;
}

async Task Stream(object body, Action<JsonElement> receive, CancellationToken token)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", qwenKey);
    request.Content = JsonContent.Create(body);
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    if (!response.IsSuccessStatusCode)
    {
        // Only retain a service error code, never its raw message or request headers.
        string code = "unknown";
        try
        {
            using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (failure.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
        }
        catch (JsonException) { }
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({code})");
    }
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
    var finished = false;
    while (await reader.ReadLineAsync(token) is { } line)
    {
        if (!line.StartsWith("data:")) continue;
        var payload = line[5..].Trim();
        if (payload == "[DONE]") break;
        using var message = JsonDocument.Parse(payload);
        if (message.RootElement.TryGetProperty("error", out _)) throw new IOException("Stream error");
        if (!message.RootElement.TryGetProperty("choices", out var choices)) continue;
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("delta", out var delta)) receive(delta);
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                if (reason.GetString() != "stop") throw new IOException("Incomplete generation");
                finished = true;
            }
        }
    }
    if (!finished) throw new IOException("Truncated stream");
}

static void WriteWave(string path, byte[] pcm)
{
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
    writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
}
