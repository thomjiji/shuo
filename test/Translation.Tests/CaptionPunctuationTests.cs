using Shuo.Services;

internal static class CaptionPunctuationTests
{
    internal static async Task RunAsync()
    {
        var state = new PunctuatedCaption();
        state.Update("你好 世界 新来的文字");
        Check(state.Repair("你好 世界", "你好，世界。") == "你好，世界。 新来的文字", "Repair never drops newer text");
        Check(state.Update("你好 世界 新来的文字继续") == "你好，世界。 新来的文字继续", "Keep punctuation during append");
        Check(state.Repair("你好 世界", "你好，朋友。") is null, "Reject changed words");
        Check(state.Update("原文已修订") == "原文已修订", "Revision invalidates old punctuation");
        Check(state.Repair("你好 世界", "你好，世界。") is null, "Stale result cannot replace a revision");
        state.Update("It costs 1.5 dollars");
        Check(state.Repair("It costs 1.5 dollars", "It costs 15 dollars.") is null, "Preserve decimal punctuation");
        state.Update("变化 -5%");
        Check(state.Repair("变化 -5%", "变化 5%。") is null, "Preserve negative signs");
        Check(state.Repair("变化 -5%", "变化 -5。") is null, "Preserve percentage signs");
        state.Update("I can't go");
        Check(state.Repair("I can't go", "I cant go.") is null, "Preserve apostrophes");
        state.Update("hello world");
        Check(state.Repair("hello world", "helloworld.") is null, "Preserve English word boundaries");
        Check(state.Repair("hello world", "hello, world.") == "hello, world.", "Allow punctuation between English words");
        Check(state.Repair("hello ", "hello") == "hello world", "Keep spaces before an appended English word");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new List<string>();
        var calls = new List<string>();
        await PunctuatedTranslationSession.RunAsync(async (publish, token) =>
        {
            publish("第一段文字还没结束");
            Check(output.SequenceEqual(new[] { "第一段文字还没结束" }), "Publish raw text synchronously before punctuation");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            publish("第一段文字还没结束 还有后文");
            publish("第一段文字还没结束 还有后文 最终内容");
            Check(output[^1].EndsWith("最终内容"), "Slow punctuation never delays new words");
            release.SetResult();
        }, async (raw, token) =>
        {
            calls.Add(raw);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return raw.Replace("结束", "结束，").Replace("后文", "后文，") + "。";
        }, output.Add, CancellationToken.None);
        Check(calls.Count == 2, "Coalesce obsolete pending repairs");
        Check(output[^1] == "第一段文字还没结束， 还有后文， 最终内容。", "Finish with the newest punctuated caption");

        output.Clear();
        await PunctuatedTranslationSession.RunAsync((publish, _) =>
        {
            publish("标点服务失败仍显示原文");
            return Task.CompletedTask;
        }, (_, _) => throw new IOException("unavailable"), output.Add, CancellationToken.None);
        Check(output.Single() == "标点服务失败仍显示原文", "Punctuation failure leaves live subtitles available");
        Console.WriteLine("Passed nonblocking punctuation, coalescing, stale-result, text-integrity, and fallback tests.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
