using System.Runtime.CompilerServices;

namespace Shuo.Services;

internal sealed class TranslatedSpeechClient(HttpClient client)
{
    internal async IAsyncEnumerable<byte[]> ReadAsync(string source, Uri translationEndpoint,
        ReadingOptions options, string apiKey, Action<string> translated,
        [EnumeratorCancellation] CancellationToken token)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请配置豆包语音 API Key。");
        var text = await new SelfHostedTextTranslator(translationEndpoint).TranslateAsync(source, "zh", token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new IOException("Mac 未返回有效译文。");
        translated(text);
        var speech = new DoubaoSpeechClient(client);
        // Synthesize completed translations so TTS sees sentence relationships,
        // rather than the individual token deltas produced by the translator.
        foreach (var passage in ReadingText.Split(text))
        {
            token.ThrowIfCancellationRequested();
            await foreach (var audio in speech.SynthesizeAsync(passage, options, apiKey, token))
                yield return audio;
        }
    }
}
