using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shuo.Services;

if (args.Length == 0) throw new ArgumentException("Pass the Mac host, optionally followed by --play.");
var endpoint = SelfHostedReadingClient.Endpoint(args[0]);
var original = args.Contains("--original");
if (original) endpoint = new UriBuilder(endpoint) { Path = "/v1/speech" }.Uri;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
await SelfHostedReadingClient.TestAsync(args[0], timeout.Token);
using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
var health = new UriBuilder(endpoint) { Scheme = "http", Path = "/health" }.Uri;
async Task WaitIdle()
{
    for (var attempt = 0; attempt < 40; attempt++)
    {
        using var body = JsonDocument.Parse(await http.GetStringAsync(health, timeout.Token));
        if (!body.RootElement.GetProperty("busy").GetBoolean()) return;
        await Task.Delay(100, timeout.Token);
    }
    throw new Exception("Mac did not release the reading session");
}
foreach (var (source, speed) in new[]
{
    ("Please save your work before restarting the computer. Your files will not be deleted.", 1.0),
    ("会議は明日の午後三時に始まります。資料を忘れないでください。参加費は無料です。", 1.3),
})
{
    var translated = new StringBuilder();
    var watch = Stopwatch.StartNew();
    double? firstAudio = null;
    var bytes = 0L;
    using var playback = args.Contains("--play") ? new ReadingPlayback() : null;
    var paused = false;
    await foreach (var chunk in SelfHostedReadingClient.ReadAsync(source, endpoint, speed, part => translated.Append(part), timeout.Token))
    {
        firstAudio ??= watch.Elapsed.TotalSeconds;
        bytes += chunk.Length;
        if (playback is not null)
        {
            await playback.WriteAsync(chunk, timeout.Token);
            if (!paused)
            {
                playback.TogglePause();
                await Task.Delay(500, timeout.Token);
                if (!playback.Paused || playback.Level != 0) throw new Exception("Pause did not silence playback");
                playback.TogglePause();
                paused = true;
            }
        }
    }
    if (playback is not null) await playback.CompleteAsync(timeout.Token);
    if (bytes < 9600 || translated.Length == 0) throw new Exception("Missing local translation/audio");
    if (original && translated.ToString() != source) throw new Exception("Original text was modified");
    Console.WriteLine(JsonSerializer.Serialize(new { speed, firstAudioSeconds = firstAudio, audioSeconds = bytes / 48000.0, translation = translated.ToString() }));
    await WaitIdle();
}
using (var cancelled = new CancellationTokenSource())
{
    await using (var stream = SelfHostedReadingClient.ReadAsync("Please save the report before closing the window.", endpoint, 1,
        _ => { }, cancelled.Token).GetAsyncEnumerator())
    {
        if (!await stream.MoveNextAsync()) throw new Exception("Expected an audio chunk");
        await Task.Delay(600, timeout.Token);
        using var body = JsonDocument.Parse(await http.GetStringAsync(health, timeout.Token));
        if (!body.RootElement.GetProperty("busy").GetBoolean()) throw new Exception("Server ignored withheld playback acknowledgment");
        cancelled.Cancel();
        try { await stream.MoveNextAsync(); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { }
    }
    await WaitIdle();
}
Console.WriteLine("ok local health, bilingual audio, speed, backpressure, cancellation and session release");
