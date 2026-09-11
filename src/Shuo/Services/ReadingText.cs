using System.Text;

namespace Shuo.Services;

internal static class ReadingText
{
    internal const int MaximumLength = 30000;
    // Application request budget; not a verified service maximum.
    internal const int MaximumRequestBytes = 1800;

    internal static IReadOnlyList<string> Split(string text, int maximumRequestBytes = MaximumRequestBytes)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("没有可朗读的文字。");
        if (text.Length > MaximumLength) throw new ArgumentException("一次最多朗读 30000 个字符，请分段选择。");
        if (maximumRequestBytes < 4) throw new ArgumentOutOfRangeException(nameof(maximumRequestBytes));
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            var bytes = 0;
            var paragraphEnd = start;
            var sentenceEnd = start;
            foreach (var rune in text.AsSpan(start).EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > maximumRequestBytes) break;
                bytes += rune.Utf8SequenceLength;
                end += rune.Utf16SequenceLength;
                if (rune.Value is '\n' or 0x2029 || rune.Value == '\r' && (end == text.Length || text[end] != '\n'))
                    paragraphEnd = end;
                if (rune.Value is '。' or '！' or '？' or '!' or '?' or '.') sentenceEnd = end;
            }
            // Only split when the remainder actually exceeds the budget. Prefer the
            // last complete paragraph, then a sentence in an oversized paragraph.
            if (end < text.Length)
            {
                end = paragraphEnd > start ? paragraphEnd : sentenceEnd > start ? sentenceEnd : end;
                if (end > start && text[end - 1] == '\r' && text[end] == '\n') end--;
            }
            chunks.Add(text[start..end]);
            start = end;
        }
        return chunks;
    }
}
