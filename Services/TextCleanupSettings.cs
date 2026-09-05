using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsDictation.Services;

internal static class TextCleanupSettings
{
    internal static TextCleanupOptions Load()
    {
        var path = HotkeySettings.GetPath();
        if (!File.Exists(path)) return new();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Dictation settings must be a JSON object.");
        return new(
            root["removeFillerWords"]?.GetValue<bool>() ?? false,
            root["trimTrailingPeriod"]?.GetValue<bool>() ?? false);
    }

    internal static void Save(TextCleanupOptions options)
    {
        var path = HotkeySettings.GetPath();
        if (!File.Exists(path)) throw new FileNotFoundException("Dictation settings are not ready yet.", path);
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Dictation settings must be a JSON object.");
        root["removeFillerWords"] = options.RemoveFillerWords;
        root["trimTrailingPeriod"] = options.TrimTrailingPeriod;
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
