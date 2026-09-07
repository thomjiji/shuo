using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Security.Credentials;

namespace Shuo.Services;

internal sealed record CloudOptions(bool Enabled = false, string ResourceId = "volc.seedasr.sauc.duration",
    string ApiKey = "", string AppId = "", string AccessToken = "", string Provider = "doubao",
    string QwenApiKey = "", string QwenRegion = "cn-beijing")
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string Backend => Enabled ? Provider : "local";
    [System.Text.Json.Serialization.JsonIgnore]
    internal string ServiceName => Provider == "qwen" ? "阿里云百炼" : "火山引擎";
}

internal static class CloudSettings
{
    private const string VaultResource = "shuo-doubao";
    private const string QwenVaultResource = "shuo-qwen";

    internal static CloudOptions Load()
    {
        var path = HotkeySettings.GetPath();
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        var provider = root?["transcriptionProvider"]?.GetValue<string>()
            ?? ((root?["doubao"]?["enabled"]?.GetValue<bool>() ?? false) ? "doubao" : "local");
        if (provider is not ("local" or "doubao" or "qwen"))
            throw new InvalidDataException("未知转录服务，请重新选择并保存。");
        var options = new CloudOptions(
            provider != "local",
            root?["doubao"]?["resourceId"]?.GetValue<string>() ?? "volc.seedasr.sauc.duration",
            Provider: provider == "local" ? "doubao" : provider,
            QwenRegion: root?["qwen"]?["region"]?.GetValue<string>() ?? "cn-beijing");
        var vault = new PasswordVault();
        var doubao = ReadSecret(vault, VaultResource, path);
        if (doubao is not null)
        {
            var secret = JsonSerializer.Deserialize<CloudOptions>(doubao)
                ?? throw new InvalidDataException("无法读取豆包凭据。");
            options = options with { ApiKey = secret.ApiKey, AppId = secret.AppId, AccessToken = secret.AccessToken };
        }
        return options with { QwenApiKey = ReadSecret(vault, QwenVaultResource, path) ?? "" };
    }

    private static string? ReadSecret(PasswordVault vault, string resource, string path)
    {
        PasswordCredential credential;
        try { credential = vault.Retrieve(resource, path); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return null; }
        credential.RetrievePassword();
        return credential.Password;
    }

    internal static void Save(CloudOptions options)
    {
        var path = HotkeySettings.GetPath();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取听写设置。");
        if (options.Backend is not ("local" or "doubao" or "qwen"))
            throw new InvalidDataException("未知转录服务。");
        if (options.Backend == "doubao" && string.IsNullOrWhiteSpace(options.ApiKey)
            && (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.AccessToken)))
            throw new InvalidDataException("请填写豆包 API Key，或旧版 App ID 和 Access Token。");
        if (options.Backend == "qwen" && string.IsNullOrWhiteSpace(options.QwenApiKey))
            throw new InvalidDataException("请填写百炼 API Key。");
        if (options.QwenRegion is not ("cn-beijing" or "ap-southeast-1"))
            throw new InvalidDataException("请选择百炼服务地域。");

        var vault = new PasswordVault();
        // Keep each provider's credentials when switching; no API keys enter settings.json.
        vault.Add(new PasswordCredential(VaultResource, path,
            JsonSerializer.Serialize(new { options.ApiKey, options.AppId, options.AccessToken })));
        if (!string.IsNullOrWhiteSpace(options.QwenApiKey))
            vault.Add(new PasswordCredential(QwenVaultResource, path, options.QwenApiKey));

        root["transcriptionProvider"] = options.Backend;
        var doubao = root["doubao"] as JsonObject ?? new JsonObject();
        doubao["enabled"] = options.Backend == "doubao";
        doubao["resourceId"] = options.ResourceId;
        doubao.Remove("semanticSmoothing");
        if (root["doubao"] is not JsonObject) root["doubao"] = doubao;
        var qwen = root["qwen"] as JsonObject ?? new JsonObject();
        qwen["region"] = options.QwenRegion;
        if (root["qwen"] is not JsonObject) root["qwen"] = qwen;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
