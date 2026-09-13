using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;

namespace Shuo.Services;

internal sealed class ContextualTranslationSession(TranslationOptions options, string apiKey, Action ready,
    Action<string> caption, Action<double> audioLevel,
    Func<string, CancellationToken, Task<string>>? format = null,
    Action<JsonElement>? received = null, Action<object>? diagnostic = null)
{
    internal async Task RunAsync(CancellationToken stop, IAsyncEnumerable<byte[]>? audio = null,
        Uri? asrEndpoint = null, Func<IReadOnlyList<string>, IReadOnlyList<CaptionContext>, bool,
            CancellationToken, Task<CaptionTranslation>>? translate = null)
    {
        using var client = new HttpClient();
        var translator = new ContextualCaptionTranslator(options, apiKey, client);
        translate ??= translator.TranslateAsync;
        var segments = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var lifetime = new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        using var stopping = stop.Register(() => lifetime.CancelAfter(TimeSpan.FromSeconds(45)));
        var source = new CaptionSourceSegments();
        long silenceStarted = 0;
        var recognizing = RecognizeAsync();
        var translating = TranslateAsync();
        try
        {
            await await Task.WhenAny(recognizing, translating);
            await Task.WhenAll(recognizing, translating);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw new IOException("等待最后一段字幕超时，请检查网络后重试。");
        }
        finally
        {
            lifetime.Cancel();
            capture.Cancel();
            segments.Writer.TryComplete();
            try { await Task.WhenAll(recognizing, translating); } catch { }
        }

        async Task RecognizeAsync()
        {
            Exception? failure = null;
            try
            {
                await new TranslationSession(options, apiKey, ready, _ => { }, audioLevel,
                    silenceDurationMs: 1000, received: message =>
                    {
                        received?.Invoke(message);
                        var type = message.GetProperty("type").GetString();
                        if (type == "input_audio_buffer.speech_started") Interlocked.Exchange(ref silenceStarted, 0);
                        else if (type == "input_audio_buffer.speech_stopped") Interlocked.Exchange(ref silenceStarted, Stopwatch.GetTimestamp());
                        foreach (var segment in source.Update(message))
                        {
                            diagnostic?.Invoke(new { stage = "source", text = segment });
                            if (!segments.Writer.TryWrite(segment))
                                throw new IOException("字幕翻译跟不上语音，请暂停后重试。");
                        }
                    }, sourceOnly: true).RunAsync(capture.Token, audio, asrEndpoint);
            }
            catch (Exception error) { failure = error; throw; }
            finally { segments.Writer.TryComplete(failure); }
        }

        async Task TranslateAsync()
        {
            var pending = new List<string>();
            var context = new List<CaptionContext>();
            var waiting = segments.Reader.WaitToReadAsync(lifetime.Token).AsTask();
            while (true)
            {
                if (pending.Count > 0 && !waiting.IsCompleted)
                {
                    var tick = Task.Delay(500, lifetime.Token);
                    if (await Task.WhenAny(waiting, tick) == tick)
                    {
                        await tick;
                        var silence = Interlocked.Read(ref silenceStarted);
                        if (silence != 0 && Stopwatch.GetElapsedTime(silence) >= TimeSpan.FromSeconds(3))
                            await TranslatePendingAsync(final: true);
                        continue;
                    }
                }
                if (!await waiting) break;
                while (segments.Reader.TryRead(out var segment)) pending.Add(segment);
                waiting = segments.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                await TranslatePendingAsync(final: (waiting.IsCompletedSuccessfully && !waiting.Result)
                    || pending.Sum(value => value.Length) >= 800);
            }
            if (pending.Count > 0) await TranslatePendingAsync(final: true);

            async Task TranslatePendingAsync(bool final)
            {
                if (pending.Count == 0) return;
                var result = await translate(pending, context, final, lifetime.Token);
                diagnostic?.Invoke(new { stage = "decision", source = pending.ToArray(), final,
                    consumed = result.Consumed, text = result.Text });
                if (result.Consumed == 0) return;
                var original = string.Join(" ", pending.Take(result.Consumed));
                pending.RemoveRange(0, result.Consumed);
                context.Add(new CaptionContext(original, result.Text));
                while (context.Count > 4 || (context.Count > 1 && context.Sum(item => item.Source.Length + item.Translation.Length) > 2000))
                    context.RemoveAt(0);
                caption(format is null ? result.Text : await format(result.Text, lifetime.Token));
            }
        }
    }
}

// ASR text is cumulative per item. Only confirmed text enters translation; stash is prediction.
internal sealed class CaptionSourceSegments
{
    private readonly Dictionary<string, string> _consumed = new();
    private readonly HashSet<string> _finished = new();
    private readonly Queue<string> _order = new();

    internal IEnumerable<string> Update(JsonElement message)
    {
        var type = message.GetProperty("type").GetString();
        if (type == "conversation.item.input_audio_transcription.failed")
            throw new IOException("百炼语音识别失败，请重新开始字幕。");
        if (type is not ("conversation.item.input_audio_transcription.text" or "conversation.item.input_audio_transcription.completed"))
            yield break;
        var id = message.GetProperty("item_id").GetString() ?? throw new IOException("识别结果缺少消息 ID。");
        if (_finished.Contains(id)) yield break;
        var final = type == "conversation.item.input_audio_transcription.completed";
        var text = message.GetProperty(final ? "transcript" : "text").GetString() ?? "";
        if (!_consumed.TryGetValue(id, out var consumed))
        {
            consumed = "";
            _consumed.Add(id, consumed);
            _order.Enqueue(id);
            while (_order.Count > 32) { var old = _order.Dequeue(); _consumed.Remove(old); _finished.Remove(old); }
        }
        if (!text.StartsWith(consumed, StringComparison.Ordinal))
            throw new IOException("语音识别修改了已确认的原文，请重新开始字幕。");
        var end = final ? text.Length : FindSentenceEnd(text, consumed.Length);
        if (end > consumed.Length)
        {
            _consumed[id] = text[..end];
            if (!string.IsNullOrWhiteSpace(text[consumed.Length..end])) yield return text[consumed.Length..end];
        }
        if (final) _finished.Add(id);
    }

    private static int FindSentenceEnd(string text, int start)
    {
        for (var index = text.Length - 1; index >= start; index--)
            if ("。！？!?".Contains(text[index]) || (text[index] == '.' && index + 1 < text.Length && char.IsWhiteSpace(text[index + 1])))
                return index + 1;
        return start;
    }
}
