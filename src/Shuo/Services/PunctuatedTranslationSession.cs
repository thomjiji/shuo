using System.Threading.Channels;

namespace Shuo.Services;

internal sealed class PunctuatedTranslationSession(TranslationOptions options, string apiKey, Action ready,
    Action<string> transcript, Action<double> audioLevel, Func<string, CancellationToken, Task<string>>? format = null)
{
    internal async Task RunAsync(CancellationToken stop, IAsyncEnumerable<byte[]>? audio = null)
    {
        using var client = new HttpClient();
        var punctuation = new CaptionPunctuation(options, apiKey, client);
        await RunAsync((publish, token) => new TranslationSession(options, apiKey, ready, publish, audioLevel, format)
            .RunAsync(token, audio), punctuation.RestoreAsync, transcript, stop);
    }

    internal static async Task RunAsync(Func<Action<string>, CancellationToken, Task> stream,
        Func<string, CancellationToken, Task<string>> restore, Action<string> publish, CancellationToken stop)
    {
        var updates = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        using var lifetime = new CancellationTokenSource();
        var state = new PunctuatedCaption();
        var gate = new object();
        string? displayed = null;
        var repairing = RepairAsync();
        try
        {
            await stream(raw =>
            {
                lock (gate) Show(state.Update(raw));
                if (raw.Length >= 8) updates.Writer.TryWrite(raw);
            }, stop);
            updates.Writer.TryComplete();
            try { await repairing.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
        }
        finally
        {
            lifetime.Cancel();
            updates.Writer.TryComplete();
            try { await repairing; } catch (OperationCanceledException) { }
        }

        void Show(string text)
        {
            if (text == displayed) return;
            displayed = text;
            publish(text);
        }

        async Task RepairAsync()
        {
            await foreach (var raw in updates.Reader.ReadAllAsync(lifetime.Token))
            {
                string result;
                try { result = await restore(raw, lifetime.Token); }
                catch (Exception) when (!lifetime.IsCancellationRequested) { continue; }
                lock (gate) { if (state.Repair(raw, result) is { } text) Show(text); }
            }
        }
    }
}
