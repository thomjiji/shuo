using System.Text.RegularExpressions;

namespace WindowsDictation.Services;

internal sealed record TextCleanupOptions(bool RemoveFillerWords = false, bool TrimTrailingPeriod = false);

internal static class TextCleanup
{
    // Only isolated hesitation sounds with a following pause are eligible.
    // Sentence particles (including 啊) and ordinary words are deliberately retained.
    private static readonly Regex Filler = new(
        @"(?<boundary>^|[，,。！？!?；;\r\n])[\t ]*[嗯呃]+[，,、…]+[\t ]*",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc", "Inc", "Ltd",
        "Co", "Corp", "No", "Fig", "approx", "dept", "vol", "Jan", "Feb", "Mar", "Apr",
        "Jun", "Jul", "Aug", "Sep", "Sept", "Oct", "Nov", "Dec",
    };

    internal static string Apply(string text, TextCleanupOptions options)
    {
        if (options.RemoveFillerWords) text = RemoveFillers(text);
        if (options.TrimTrailingPeriod) text = TrimPeriod(text);
        return text;
    }

    private static string RemoveFillers(string text)
    {
        // A complete utterance such as “嗯。” may be an acknowledgment.
        if (!text.Any(c => char.IsLetterOrDigit(c) && c is not ('嗯' or '呃'))) return text;

        while (true)
        {
            var original = text;
            text = Filler.Replace(original, match => IsQuoted(original, match.Index)
                ? match.Value
                : match.Groups["boundary"].Value);
            if (text == original) return text;
        }
    }

    private static bool IsQuoted(string text, int index)
    {
        var closingQuotes = new Stack<char>();
        for (var i = 0; i < index; i++)
        {
            var c = text[i];
            if (closingQuotes.TryPeek(out var closing) && c == closing)
                closingQuotes.Pop();
            else if (c is '“' or '「' or '『' or '‘' or '"')
                closingQuotes.Push(c switch { '“' => '”', '「' => '」', '『' => '』', '‘' => '’', _ => '"' });
        }
        return closingQuotes.Count != 0;
    }

    private static string TrimPeriod(string text)
    {
        var end = text.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end])) end--;
        if (end <= 0 || text[end] is not ('。' or '.')) return text;
        if (text[end - 1] is '.' or '。' or '…' || IsQuoted(text, end)) return text;
        if (text[end] == '.')
        {
            var start = end - 1;
            while (start >= 0 && !char.IsWhiteSpace(text[start]) && text[start] is not ('，' or ',' or '。' or '！' or '？' or '!' or '?')) start--;
            var token = text[(start + 1)..end];
            // Keep abbreviations, initials, numbers, URLs, addresses and paths.
            if (token.Length <= 1 && token.All(char.IsAsciiLetter)
                || token.Any(c => char.IsDigit(c) || c is '.' or '/' or '\\' or '@' or ':')
                || Abbreviations.Contains(token)) return text;
        }
        return text.Remove(end, 1);
    }
}
