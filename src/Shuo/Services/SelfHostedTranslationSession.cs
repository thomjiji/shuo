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
        var snapshots = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
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
                    _ => { }, capture.Token, lifetime.Token, committed: text =>
                    {
                        if (!snapshots.Writer.TryWrite(text))
                            throw new IOException("Mac 翻译速度跟不上语音，请暂停后重试。");
                    }, model: options.SelfHostedAsrModel);
            }
            finally { snapshots.Writer.TryComplete(); }
        }

        async Task TranslateAsync()
        {
            await foreach (var source in snapshots.Reader.ReadAllAsync(lifetime.Token))
            {
                foreach (var passage in ReadingText.Split(source, ReadingText.CaptionRequestBytes))
                {
                    var text = await translator.TranslateAsync(passage, options.TargetLanguage, lifetime.Token);
                    transcript(format is null ? text : await format(text, lifetime.Token));
                }
            }
        }
    }

}
