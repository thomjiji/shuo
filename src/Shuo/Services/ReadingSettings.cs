using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shuo.Services;

internal static class ReadingSettings
{
    internal static ReadingOptions Load()
    {
        var path = HotkeySettings.GetPath();
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        var options = root?["reading"]?.Deserialize<ReadingOptions>() ?? new();
        return options with { SelfHostedHost = ServiceSettings.ReadingHost(root) };
    }

    internal static string LoadApiKey() => ServiceSettings.ReadSecret(ServiceSettings.DoubaoSpeech);

    internal static void Save(ReadingOptions options, string apiKey)
    {
        var path = HotkeySettings.GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取设置文件。") : new JsonObject();
        ServiceSettings.SaveSecret(ServiceSettings.DoubaoSpeech, apiKey, path);
        root["reading"] = JsonSerializer.SerializeToNode(options);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
