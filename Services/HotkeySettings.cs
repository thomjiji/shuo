using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsDictation.Services;

internal static class HotkeySettings
{
    private const string SettingsOverride = "WINDOWS_DICTATION_SETTINGS";

    internal static HotkeyBinding? Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return HotkeyBinding.Default;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return HotkeyBinding.Default;
            if (!root.TryGetPropertyValue("hotkey", out var stored)) return HotkeyBinding.Default;
            if (stored is null) return null;
            if (stored is not JsonObject hotkey) return HotkeyBinding.Default;

            var binding = new HotkeyBinding(
                hotkey["modifiers"]?.GetValue<uint>() ?? 0,
                hotkey["virtualKey"]?.GetValue<uint>() ?? 0);
            return binding.IsValid ? binding : HotkeyBinding.Default;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or FormatException)
        {
            return HotkeyBinding.Default;
        }
    }

    internal static void Save(HotkeyBinding? binding)
    {
        if (binding is { } selected && !selected.IsValid)
        {
            throw new ArgumentException("The hotkey is invalid.", nameof(binding));
        }

        var path = GetPath();
        if (!File.Exists(path)) throw new FileNotFoundException("Dictation settings are not ready yet.", path);

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Dictation settings must be a JSON object.");
        root["hotkey"] = binding is { } value
            ? new JsonObject
            {
                ["modifiers"] = JsonValue.Create(value.Modifiers),
                ["virtualKey"] = JsonValue.Create(value.VirtualKey),
            }
            : null;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string GetPath()
    {
        var configured = Environment.GetEnvironmentVariable(SettingsOverride);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDictation",
            "settings.json");
    }
}
