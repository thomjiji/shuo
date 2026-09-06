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
        MakeReadable();
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

    private void MakeReadable()
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var changed = false;
        try
        {
            using (var reader = new StreamReader(path, Encoding.UTF8))
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                while (reader.ReadLine() is { } line)
                {
                    var readable = line;
                    if (line.Contains("\\u", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            readable = JsonSerializer.Serialize(document.RootElement, JsonOptions);
                        }
                        catch (JsonException) { } // Preserve damaged lines for recovery.
                    }
                    changed |= readable != line;
                    writer.WriteLine(readable);
                }
            }
            if (changed)
                File.Replace(temporary, path, path + "." + Guid.NewGuid().ToString("N") + ".bak");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string ModelName(bool cloud, string? resourceId, string? localPath)
    {
        if (!cloud)
            return string.IsNullOrWhiteSpace(localPath) ? "本地模型（模型名称未提供）" : Path.GetFileNameWithoutExtension(localPath);
        var resource = string.IsNullOrWhiteSpace(resourceId) ? "volc.seedasr.sauc.duration" : resourceId;
        var name = resource switch
        {
            "volc.seedasr.sauc.duration" or "volc.seedasr.sauc.concurrent" => "豆包流式语音识别模型 2.0",
            "volc.bigasr.sauc.duration" or "volc.bigasr.sauc.concurrent" => "豆包流式语音识别模型 1.0",
            _ => "豆包语音识别（模型版本未提供）"
        };
        return $"{name} [{resource}]";
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
