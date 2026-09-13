using System.Text;
using System.Threading.Channels;

namespace Shuo.Services;

internal sealed class SelfHostedTranslationSession(TranslationOptions options, Action ready,
    Action<string> transcript, Func<string, CancellationToken, Task<string>>? format = null)
{
    internal async Task RunAsync(IAsyncEnumerable<byte[]> audio, CancellationToken stop, Uri? asrEndpoint = null,
        Uri? translationEndpoint = null)
    {
        var endpoint = translationEndpoint ?? SelfHostedTextTranslator.Endpoint(options.Host);
        var asr = asrEndpoint ?? new UriBuilder(endpoint) { Port = 18765, Path = "/v1/asr" }.Uri;
        var translator = new SelfHostedTextTranslator(endpoint);
        var snapshots = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        using var lifetime = new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        using var stopping = stop.Register(() => lifetime.CancelAfter(TimeSpan.FromSeconds(60)));
        var recognizing = RecognizeAsync();
        var translating = TranslateAsync();
        try
        {
            var first = await Task.WhenAny(recognizing, translating);
            await first;
            await Task.WhenAll(recognizing, translating);
        }
        finally
        {
            lifetime.Cancel();
            capture.Cancel();
            snapshots.Writer.TryComplete();
            try { await Task.WhenAll(recognizing, translating); } catch { }
        }

        async Task RecognizeAsync()
        {
            try
            {
                await new SelfHostedAsrClient(asr).RunAsync(audio, ready,
                    text => snapshots.Writer.TryWrite(RecentPassage(text)), capture.Token, lifetime.Token);
            }
            finally { snapshots.Writer.TryComplete(); }
        }

        async Task TranslateAsync()
        {
            string? previous = null;
            await foreach (var source in snapshots.Reader.ReadAllAsync(lifetime.Token))
            {
                if (source == previous) continue;
                var text = await translator.TranslateAsync(source, options.TargetLanguage, lifetime.Token);
                transcript(format is null ? text : await format(text, lifetime.Token));
                previous = source;
            }
        }
    }

    // ASR snapshots revise the current phrase. Translate only a bounded recent window and
    // replace the caption; never append a revised translation to the earlier prediction.
    internal static string RecentPassage(string source)
    {
        var start = source.Length;
        var bytes = 0;
        while (start > 0)
        {
            var count = start > 1 && char.IsSurrogatePair(source, start - 2) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(source.AsSpan(start - count, count));
            if (bytes + size > 900) break;
            bytes += size;
            start -= count;
        }
        if (start > 0)
        {
            var boundary = source.IndexOfAny([' ', '\n', '。', '！', '？', '.', '!', '?'], start);
            if (boundary >= start && boundary < source.Length - 1 && boundary - start < 100) start = boundary + 1;
        }
        return source[start..].Trim();
    }

}
