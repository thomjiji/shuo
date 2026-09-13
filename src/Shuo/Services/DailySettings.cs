using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shuo.Services;

internal sealed record DailyOptions(int Source = 0, int Destination = 0, int CaptionLanguage = 0, int TextLanguage = 0)
{
    internal DailyOptions Normalize() => this with
    {
        Source = Source == 1 ? 1 : 0,
        Destination = Source == 1 ? 2 : Math.Clamp(Destination, 0, 2),
        CaptionLanguage = Math.Clamp(CaptionLanguage, 0, 2),
        TextLanguage = TextLanguage == 1 ? 1 : 0,
    };
}

internal static class DailySettings
{
    internal static DailyOptions Load(string? path = null)
    {
        path ??= HotkeySettings.GetPath();
        return (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["daily"]?.Deserialize<DailyOptions>() ?? new() : new()).Normalize();
    }

    internal static void Save(DailyOptions options, string? path = null)
    {
        path ??= HotkeySettings.GetPath();
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取设置文件。") : new JsonObject();
        root["daily"] = JsonSerializer.SerializeToNode(options.Normalize());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
