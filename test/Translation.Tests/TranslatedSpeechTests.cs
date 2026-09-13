using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shuo.Services;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal static class TranslatedSpeechTests
{
    internal static async Task RunAsync()
    {
        foreach (var scenario in new[] { "complete", "long", "incomplete", "cancel" })
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var server = builder.Build();
            server.UseWebSockets();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var finished = false;
            var expected = scenario == "long" ? string.Concat(Enumerable.Repeat("增长放缓不等于收入下降。", 120))
                : "他没有否定结论，只是否定了推导过程。";
            var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Run(async context =>
            {
                try
                {
                    Check(context.Request.Path == "/v1/translation", "Only text translation endpoint is used");
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    var buffer = new byte[4096];
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    Check(received.MessageType == WebSocketMessageType.Text, "No ASR audio input");
                    using var start = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                    Check(start.RootElement.GetProperty("text").GetString() == "Read this paragraph."
                        && start.RootElement.GetProperty("target").GetString() == "zh", "Original text and Chinese target reach Mac");
                    await Send(new { type = "ready", protocol = 1 });
                    await Send(new { type = "text", text = expected[..4] });
                    if (scenario != "incomplete")
                    {
                        await Send(new { type = "text", text = expected[4..] });
                        finished = true;
                        await Send(new { type = "done" });
                    }
                    requestSeen.TrySetResult();
                    async Task Send(object value) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(),
                        WebSocketMessageType.Text, true, timeout.Token);
                }
                catch (Exception error) { requestSeen.TrySetException(error); }
            });
            await server.StartAsync(timeout.Token);
            var endpoint = new Uri(server.Urls.Single().Replace("http://", "ws://") + "/v1/translation");
            using var handler = new SpeechHandler(() => finished);
            using var http = new HttpClient(handler);
            var shown = "";
            var audio = 0;
            try
            {
                await foreach (var pcm in new TranslatedSpeechClient(http).ReadAsync("Read this paragraph.", endpoint,
                    new(TranslateToChinese: true, SpeechRate: 15), "test-key", text =>
                    {
                        shown += text;
                        if (scenario == "cancel") timeout.Cancel();
                    }, timeout.Token)) audio += pcm.Length;
                Check(scenario is "complete" or "long", "Incomplete or cancelled translation must not succeed");
                Check(shown == expected && string.Concat(handler.Texts) == expected, "Display and TTS receive exact completed translation");
                Check(handler.Texts.All(t => Encoding.UTF8.GetByteCount(t) <= ReadingText.MaximumRequestBytes)
                    && audio == handler.Texts.Count * 4, "Bounded speech requests preserve all audio");
                if (scenario == "complete") Check(handler.Texts.Count == 1, "A complete short paragraph is synthesized together");
            }
            catch (Exception error) when (scenario == "incomplete" && error is IOException or WebSocketException)
            { Check(handler.Texts.Count == 0, "Truncated translation never reaches TTS"); }
            catch (OperationCanceledException) when (scenario == "cancel") { Check(handler.Texts.Count == 0, "Cancellation before speech prevents cloud requests"); }
            await requestSeen.Task;
            Console.WriteLine("ok Mac translation + Doubao: " + scenario);
        }
    }

    private sealed class SpeechHandler(Func<bool> complete) : HttpMessageHandler
    {
        internal readonly List<string> Texts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(complete(), "TTS must wait for completed translation");
            Check(request.Headers.GetValues("X-Api-Key").Single() == "test-key"
                && request.Headers.GetValues("X-Api-Resource-Id").Single() == "seed-tts-2.0", "Doubao credentials and model are reused");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var parameters = body.RootElement.GetProperty("req_params");
            Check(parameters.GetProperty("audio_params").GetProperty("speech_rate").GetInt32() == 15,
                "Configured TTS synthesis rate is preserved");
            Texts.Add(parameters.GetProperty("text").GetString()!);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"code\":20000000,\"data\":\"AQIDBA==\"}\n\n") };
        }
    }

    internal static async Task LiveAsync(string host, string directory, string? sourcePath)
    {
        var source = sourcePath is null
            ? "Revenue increased, but profit fell. This does not mean that customers bought less; it means that costs grew faster than sales. The company therefore needs to control its costs, rather than simply sell more products. A lower growth rate is not the same thing as a decline."
            : await File.ReadAllTextAsync(sourcePath);
        var output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        var options = ReadingSettings.Load() with { TranslateToChinese = true, UseSelfHostedTranslation = false, SpeechRate = 0 };
        var speechKey = ServiceSettings.LoadSpeechKey(options.UseExistingKey);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var records = new List<object>();
        const string route = "mac-doubao";
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var watch = Stopwatch.StartNew();
            double? firstAudio = null;
            var translated = new StringBuilder();
            using var audio = new MemoryStream();
            foreach (var passage in ReadingText.Split(source, ReadingText.LocalRequestBytes))
            {
                if (translated.Length > 0) translated.AppendLine().AppendLine();
                var stream = new TranslatedSpeechClient(http).ReadAsync(passage, SelfHostedTextTranslator.Endpoint(host), options,
                    speechKey, text => translated.Append(text), deadline.Token);
                await foreach (var pcm in stream)
                {
                    firstAudio ??= watch.Elapsed.TotalSeconds;
                    audio.Write(pcm);
                }
            }
            Check(audio.Length > 9600 && audio.Length % 2 == 0 && translated.Length > 0, "Live translated speech is complete");
            using (var writer = new BinaryWriter(File.Create(Path.Combine(output, route + ".wav"))))
            {
                writer.Write("RIFF"u8); writer.Write(36 + (int)audio.Length); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
                writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write((int)audio.Length); writer.Write(audio.ToArray());
            }
            await File.WriteAllTextAsync(Path.Combine(output, route + ".txt"), translated.ToString());
            records.Add(new { route, source, translation = translated.ToString(), firstAudioSeconds = firstAudio,
                totalSeconds = watch.Elapsed.TotalSeconds, audioSeconds = audio.Length / 48000d });
            Console.WriteLine($"{route}: first audio {firstAudio:F2}s, duration {audio.Length / 48000d:F2}s");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
