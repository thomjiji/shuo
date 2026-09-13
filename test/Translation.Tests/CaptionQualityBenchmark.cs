using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Shuo.Services;

internal static class CaptionQualityBenchmark
{
    internal static async Task RunAsync(string output)
    {
        var cases = new[]
        {
            new { Name = "sample-tail", Pending = new[] { "それは子供の頃からまるで成長できなかった僕の心情とは違って。", "大人の美貌。", "保ったまま。" },
                Context = new[] { new CaptionContext("憧れていたあの人はずっと変わらなかった。", "曾经憧憬的那个人，始终没有改变。") }, Final = true },
            new { Name = "relative-clause", Pending = new[] { "昨日駅で会った。", "先生は来月退職します。" }, Context = Array.Empty<CaptionContext>(), Final = false },
            new { Name = "dangling-topic", Pending = new[] { "昨日駅で会った先生は。" }, Context = Array.Empty<CaptionContext>(), Final = false },
            new { Name = "english-negation", Pending = new[] { "I didn't say the treatment failed.", "I said we don't have enough evidence to know whether it worked." }, Context = Array.Empty<CaptionContext>(), Final = false },
        };
        var results = new List<object>();
        foreach (var candidate in new[] { (Model: "qwen3.5-plus", Thinking: false), (Model: "qwen3.5-plus", Thinking: true), (Model: "qwen3.6-plus", Thinking: false) })
        {
            using var client = new HttpClient();
            var translator = new ContextualCaptionTranslator(TranslationSettings.Load(), TranslationSettings.LoadApiKey(), client, candidate.Model, candidate.Thinking);
            foreach (var test in cases)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var result = await translator.TranslateAsync(test.Pending, test.Context, test.Final, CancellationToken.None);
                    results.Add(new { model = candidate.Model, thinking = candidate.Thinking, test, seconds = watch.Elapsed.TotalSeconds, result });
                    Console.WriteLine($"{candidate.Model} thinking={candidate.Thinking} {test.Name} {watch.Elapsed.TotalSeconds:F2}s consumed={result.Consumed}: {result.Text}");
                }
                catch (Exception error) { results.Add(new { model = candidate.Model, thinking = candidate.Thinking, test, error = error.Message }); Console.WriteLine(error.Message); break; }
            }
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
