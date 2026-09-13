using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shuo.Services;

internal sealed record DailyOptions(int CaptionLanguage = 0)
{
    internal DailyOptions Normalize() => this with
    {
        CaptionLanguage = Math.Clamp(CaptionLanguage, 0, 2),
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
