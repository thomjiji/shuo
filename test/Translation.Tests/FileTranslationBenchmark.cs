using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Shuo.Services;

internal static class FileTranslationBenchmark
{
    internal static async Task RunAsync(string audioPath, string outputPath)
    {
        const string model = "qwen3-livetranslate-flash";
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(audioPath));
        await SendAsync(model, new
        {
            model,
            messages = new[] { new { role = "user", content = new[] {
                new { type = "input_audio", input_audio = new { data = "data:audio/mpeg;base64," + data, format = "mp3" } }
            } } },
            modalities = new[] { "text" }, stream = true, stream_options = new { include_usage = true },
            translation_options = new { source_lang = "ja", target_lang = "zh" },
        }, new { audioPath }, outputPath);
    }

    internal static async Task RunTextAsync(string transcriptPath, string outputPath)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(transcriptPath));
        var source = string.Join("\n", document.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("message", out var message) &&
                message.GetProperty("type").GetString() == "conversation.item.input_audio_transcription.completed")
            .Select(item => item.GetProperty("message").GetProperty("transcript").GetString()));
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("No completed ASR transcript.");
        const string model = "qwen3.5-plus";
        const string instruction = "Translate the Japanese text into natural Simplified Chinese. Preserve the meaning, relationships between clauses, and ambiguity of the source. Do not infer unstated gender or add context. Output only the translation.";
        await SendAsync(model, new
        {
            model,
            messages = new[] { new { role = "system", content = instruction }, new { role = "user", content = source } },
            enable_thinking = false, temperature = 0, stream = true,
            stream_options = new { include_usage = true },
        }, new { transcriptPath, source, instruction, enable_thinking = false, temperature = 0 }, outputPath);
    }

    private static async Task SendAsync(string model, object body, object input, string outputPath)
    {
        var options = TranslationSettings.Load();
        var endpoint = new UriBuilder(TranslationSession.Endpoint(options))
            { Scheme = "https", Port = 443, Path = "/compatible-mode/v1/chat/completions", Query = "" }.Uri;
        var key = TranslationSettings.LoadApiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No saved translation API key.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(body);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var clock = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Translation HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var text = new StringBuilder();
        var events = new List<JsonElement>();
        double? firstTextSeconds = null;
        string? finishReason = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            if (line == "data: [DONE]") break;
            using var document = JsonDocument.Parse(line[6..]);
            var chunk = document.RootElement;
            events.Add(chunk.Clone());
            if (!chunk.TryGetProperty("choices", out var choices)) continue;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.GetProperty("delta").TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var value = content.GetString();
                    if (!string.IsNullOrEmpty(value)) firstTextSeconds ??= clock.Elapsed.TotalSeconds;
                    text.Append(value);
                }
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    finishReason = finish.GetString();
            }
        }
        var result = new { model, input, firstTextSeconds, totalSeconds = clock.Elapsed.TotalSeconds,
            finishReason, translation = text.ToString(), events };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (finishReason != "stop" || text.Length == 0) throw new IOException("Translation did not complete normally.");
        Console.WriteLine($"First text: {firstTextSeconds:F2}s; complete: {clock.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine(text);
    }
}
