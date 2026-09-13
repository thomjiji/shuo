using System.Globalization;

namespace Shuo.Services;

internal sealed class CaptionLines
{
    private readonly Queue<string> _pending = new();
    private readonly System.Text.StringBuilder _history = new();
    private DateTimeOffset _next;

    internal void Clear() { _pending.Clear(); _history.Clear(); _next = default; }

    internal void Add(string text, Func<string, bool> fits)
    {
        var line = "";
        var elements = StringInfo.GetTextElementEnumerator(text.Replace('\r', ' ').Replace('\n', ' '));
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (line.Length > 0 && !fits(line + element))
            {
                var space = line.LastIndexOf(' ');
                if (space > line.Length / 2)
                {
                    _pending.Enqueue(line[..space]);
                    line = line[(space + 1)..];
                }
                else { _pending.Enqueue(line.Trim()); line = ""; }
            }
            line += element;
        }
        if (!string.IsNullOrWhiteSpace(line)) _pending.Enqueue(line.Trim());
    }

    internal string? Advance(DateTimeOffset now)
    {
        if (_pending.Count == 0 || now < _next) return null;
        var line = _pending.Dequeue();
        if (_history.Length > 0) _history.Append('\n');
        _history.Append(line);
        // Each row remains visible for two advances. Speed up gently under backlog.
        var seconds = Math.Clamp(new StringInfo(line).LengthInTextElements * 0.055, 1.2, 2.6);
        _next = now.AddSeconds(_pending.Count > 6 ? 1.2 : seconds);
        return _history.ToString();
    }
}
