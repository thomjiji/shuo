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
        return new("cn-beijing",
            root["translation"]?["workspaceId"]?.GetValue<string>() ?? "",
            root["translation"]?["targetLanguage"]?.GetValue<string>() == "en" ? "en" : "zh");
    }

    internal static string LoadApiKey()
    {
        PasswordCredential credential;
        try { credential = new PasswordVault().Retrieve(VaultResource, HotkeySettings.GetPath()); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return ""; }
        credential.RetrievePassword();
        return credential.Password;
    }

    internal static string ResolveApiKey(string ownKey, string region)
    {
        if (!string.IsNullOrWhiteSpace(ownKey)) return ownKey.Trim();
        var cloud = CloudSettings.Load();
        if (cloud.QwenRegion != region) throw new ArgumentException("转录凭据地域不匹配，请填写翻译服务对应地域的 API Key。");
        return cloud.QwenApiKey;
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
            ["targetLanguage"] = options.TargetLanguage };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
