using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Security.Credentials;

namespace Shuo.Services;

internal static class TranslationSettings
{
    private const string VaultResource = "shuo-qwen-translation";

    internal static TranslationOptions Load()
    {
        var path = HotkeySettings.GetPath();
        if (!File.Exists(path)) return new();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Dictation settings must be a JSON object.");
        if (root["translation"] is not JsonObject translation) return new();
        var defaults = new TranslationOptions();
        return new(
            translation["region"]?.GetValue<string>() is "ap-southeast-1" ? "ap-southeast-1" : "cn-beijing",
            translation["workspaceId"]?.GetValue<string>() ?? "",
            translation["targetLanguage"]?.GetValue<string>() == "en" ? "en" : "zh",
            translation["enabled"]?.GetValue<bool>() ?? true,
            translation["hotkeyModifiers"]?.GetValue<uint>() ?? defaults.HotkeyModifiers,
            translation["hotkeyVirtualKey"]?.GetValue<uint>() ?? defaults.HotkeyVirtualKey,
            translation["backend"]?.GetValue<string>() == "self-hosted" ? "self-hosted" : "cloud",
            translation["host"]?.GetValue<string>() ?? "");
    }

    internal static string LoadApiKey()
    {
        PasswordCredential credential;
        try { credential = new PasswordVault().Retrieve(VaultResource, HotkeySettings.GetPath()); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return ""; }
        credential.RetrievePassword();
        return credential.Password;
    }

    internal static void Save(TranslationOptions options, string ownKey)
    {
        var path = HotkeySettings.GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("Dictation settings must be a JSON object.")
            : new JsonObject();
        var vault = new PasswordVault();
        if (!string.IsNullOrWhiteSpace(ownKey)) vault.Add(new PasswordCredential(VaultResource, path, ownKey.Trim()));
        else
        {
            try { vault.Remove(vault.Retrieve(VaultResource, path)); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
        }
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
