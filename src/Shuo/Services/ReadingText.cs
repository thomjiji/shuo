using System.Text;

namespace Shuo.Services;

internal static class ReadingText
{
    internal const int MaximumLength = 30000;

    internal static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("没有可朗读的文字。");
        if (text.Length > MaximumLength) throw new ArgumentException("一次最多朗读 30000 个字符，请分段选择。");
        // Bound each cloud request by UTF-8 bytes, preserving Unicode scalar values.
        var chunks = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 1800) Flush();
            current.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
            if (bytes >= 300 && "。！？!?\n".Contains(rune.ToString(), StringComparison.Ordinal)) Flush();
        }
        Flush();
        return chunks;

        void Flush()
        {
            var part = current.ToString().Trim();
            if (part.Length > 0) chunks.Add(part);
            current.Clear();
            bytes = 0;
        }
    }
}
