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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

    internal string ExportText(out int skipped)
    {
        var entries = Load(out skipped);
        var exportPath = Path.ChangeExtension(path, ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(exportPath))!);
        var text = string.Join(Environment.NewLine + Environment.NewLine,
            entries.Select(entry => entry.Description + Environment.NewLine + entry.Text));
        File.WriteAllText(exportPath, text, new UTF8Encoding(false));
        return exportPath;
    }

    internal void Append(TranscriptEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Text)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // A fresh line isolates an incomplete record left by an interrupted write.
        var bytes = Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
