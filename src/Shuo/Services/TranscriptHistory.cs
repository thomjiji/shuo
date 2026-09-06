using System.Text;
using System.Text.Json;

namespace Shuo.Services;

public sealed record TranscriptEntry(DateTimeOffset CreatedAt, string Text, string Provider)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description => $"{CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {Provider}";
}

internal sealed class TranscriptHistory(string path)
{
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shuo", "history.jsonl");

    internal IReadOnlyList<TranscriptEntry> Load(out int skipped)
    {
        skipped = 0;
        var entries = new List<TranscriptEntry>();
        if (!File.Exists(path)) return entries;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<TranscriptEntry>(line);
                if (entry is null || string.IsNullOrWhiteSpace(entry.Text)) { skipped++; continue; }
                entries.Add(entry);
            }
            catch (JsonException) { skipped++; }
        }
        entries.Reverse();
        return entries;
    }

    internal void Append(TranscriptEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Text)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // A fresh line isolates an incomplete record left by an interrupted write.
        var bytes = Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(entry) + "\n");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
