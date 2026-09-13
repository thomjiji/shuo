using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shuo.Services;

internal static class TranslationSettings
{
    internal static TranslationOptions Load()
    {
        var path = HotkeySettings.GetPath();
        if (!File.Exists(path)) return new();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Dictation settings must be a JSON object.");
        if (root["translation"] is not JsonObject translation) return new(Host: ServiceSettings.CaptionHost(root));
        var defaults = new TranslationOptions();
        return new(
            translation["region"]?.GetValue<string>() is "ap-southeast-1" ? "ap-southeast-1" : "cn-beijing",
            translation["workspaceId"]?.GetValue<string>() ?? "",
            translation["targetLanguage"]?.GetValue<string>() == "en" ? "en" : "zh",
            translation["enabled"]?.GetValue<bool>() ?? true,
            translation["hotkeyModifiers"]?.GetValue<uint>() ?? defaults.HotkeyModifiers,
            translation["hotkeyVirtualKey"]?.GetValue<uint>() ?? defaults.HotkeyVirtualKey,
            translation["backend"]?.GetValue<string>() == "self-hosted" ? "self-hosted" : "cloud",
            ServiceSettings.CaptionHost(root));
    }

    internal static string LoadApiKey() => ServiceSettings.ReadSecret(ServiceSettings.BailianTranslation);

    internal static void Save(TranslationOptions options, string ownKey)
    {
        var path = HotkeySettings.GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("Dictation settings must be a JSON object.")
            : new JsonObject();
        ServiceSettings.SaveSecret(ServiceSettings.BailianTranslation, ownKey, path);
        root["translation"] = new JsonObject { ["region"] = options.Region, ["workspaceId"] = options.WorkspaceId,
            ["targetLanguage"] = options.TargetLanguage, ["enabled"] = options.Enabled,
            ["hotkeyModifiers"] = options.HotkeyModifiers, ["hotkeyVirtualKey"] = options.HotkeyVirtualKey,
            ["backend"] = options.Backend, ["host"] = options.Host };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
