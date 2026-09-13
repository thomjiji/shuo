using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shuo.Services;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Shuo.Services.WebSocketJson;

internal static class CapabilityTests
{
    internal static async Task RunAsync()
    {
        var sharedMac = MacServiceAddresses.Shared(" mac.local ");
        Check(sharedMac == new MacServiceAddresses("http://mac.local:18765", "mac.local", "mac.local"), "One Mac host resolves ports for all capabilities");
        Check(!sharedMac.Separate && sharedMac.SharedHost == "mac.local", "Shared Mac is displayed once");
        Check(new MacServiceAddresses("http://asr:18765", "captions", "reader").Separate, "Keep distinct existing hosts");
        Check(new MacServiceAddresses("http://asr:19000", "asr", "asr").Separate, "Keep custom recognition port");
        Check(MacServiceAddresses.Shared("[::1]").Recognition == "http://[::1]:18765", "Shared IPv6 host");
        Check(new MacServiceAddresses("", "", "reader").SharedHost == "reader", "Reuse the sole configured Mac");
        try { MacServiceAddresses.Shared("http://mac:19000"); throw new Exception("Shared host accepted a custom service port"); }
        catch (ArgumentException) { }
        sharedMac.Validate();
        Check(new DailyOptions(CaptionLanguage: 9).Normalize() == new DailyOptions(2), "Normalize language preferences");
        var dailyPath = Path.Combine(Path.GetTempPath(), "shuo-daily-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(dailyPath, "{\"unrelated\":42,\"daily\":{\"Source\":1,\"Destination\":1,\"CaptionLanguage\":2,\"TextLanguage\":1}}");
            var preferences = new DailyOptions(2);
            Check(DailySettings.Load(dailyPath) == preferences, "Ignore retired choices without losing caption language");
            DailySettings.Save(preferences, dailyPath);
            Check(DailySettings.Load(dailyPath) == preferences, "Daily choices survive restart");
            Check(JsonNode.Parse(File.ReadAllText(dailyPath))!["unrelated"]!.GetValue<int>() == 42, "Preserve service settings");
        }
        finally { File.Delete(dailyPath); }
        var root = JsonNode.Parse("""{"reading":{"SelfHostedHost":"reader"},"translation":{"host":"captions"},"selfhosted":{"url":"http://asr:18765"}}""");
        Check(ServiceSettings.ReadingHost(root) == "reader" && ServiceSettings.CaptionHost(root) == "captions", "Preserve explicit hosts");
        root!["reading"] = null;
        Check(ServiceSettings.ReadingHost(root) == "captions", "Shared host fallback");
        root["translation"] = 123;
        Check(ServiceSettings.ReadingHost(root) == "asr", "Ignore unrelated malformed feature config");
        foreach (var speechReady in new[] { true, false })
        {
            using var health = JsonDocument.Parse(JsonSerializer.Serialize(new { protocol = 1, ready = true,
                capabilities = new { translation = new { ready = true }, speech = new { ready = speechReady } },
                sample_rate = 24000, format = "pcm_s16le", voice = "Serena" }));
            LocalServiceHealth.Validate(health.RootElement, "translation");
            try
            {
                LocalServiceHealth.Validate(health.RootElement, "speech");
                Check(speechReady, "Unavailable speech must fail health check");
            }
            catch (IOException) when (!speechReady) { }
        }
        using (var legacy = JsonDocument.Parse("""{"protocol":1,"ready":true}"""))
            LocalServiceHealth.Validate(legacy.RootElement, "translation");
        await AsrAsync(false);
        await AsrAsync(true);
        Console.WriteLine("Passed capability health, configuration isolation, standalone ASR, and translation failure cancellation tests.");
    }

    private static async Task AsrAsync(bool translationFails)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.UseWebSockets();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var captureClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        server.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var start = await ReceiveAsync(socket, deadline.Token);
            if (context.Request.Path == "/translation")
            {
                Interlocked.Increment(ref requests);
                await SendAsync(socket, new { type = "error", message = "Translation unavailable" }, deadline.Token);
                return;
            }
            await SendAsync(socket, new { type = "ready", protocol = 1 }, deadline.Token);
            await socket.ReceiveAsync(new byte[16].AsMemory(), deadline.Token);
            await SendAsync(socket, new { type = "partial", text = "hello" }, deadline.Token);
            if (translationFails)
            {
                try { await socket.ReceiveAsync(new byte[16].AsMemory(), deadline.Token); }
                catch (WebSocketException) { }
            }
            else
            {
                using var finish = await ReceiveAsync(socket, deadline.Token);
                Check(finish.RootElement.GetProperty("type").GetString() == "finish", "ASR graceful finish");
                await SendAsync(socket, new { type = "final", text = "hello final" }, deadline.Token);
            }
        });
        await server.StartAsync(deadline.Token);
        var url = server.Urls.Single().Replace("http:", "ws:");
        var snapshots = new List<string>();
        if (translationFails)
        {
            try
            {
                await new SelfHostedTranslationSession(new(), () => { }, _ => { })
                    .RunAsync(Audio(deadline.Token), deadline.Token, new Uri(url + "/asr"), new Uri(url + "/translation"));
                throw new Exception("Translation failure was lost");
            }
            catch (IOException) { }
            await captureClosed.Task.WaitAsync(deadline.Token);
            Check(requests == 1, "Translation called once");
        }
        else
        {
            await new SelfHostedAsrClient(new Uri(url + "/asr")).RunAsync(Audio(deadline.Token), () => { }, snapshots.Add, deadline.Token);
            Check(snapshots.Last() == "hello final" && requests == 0, "ASR works without translation");
        }
        await server.StopAsync(deadline.Token);

        async IAsyncEnumerable<byte[]> Audio([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return [1, 0, 2, 0];
                if (translationFails) await Task.Delay(Timeout.Infinite, token);
            }
            finally { captureClosed.TrySetResult(); }
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
