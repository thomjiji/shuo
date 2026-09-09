using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Security.Credentials;

namespace Shuo.Services;

internal sealed record CloudOptions(bool Enabled = false, string ResourceId = "volc.seedasr.sauc.duration",
    string ApiKey = "", string AppId = "", string AccessToken = "", string Provider = "doubao",
    string QwenApiKey = "", string QwenRegion = "cn-beijing", string SelfHostedUrl = "", string SelfHostedModel = "Qwen3-ASR-1.7B-8bit")
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string Backend => Enabled ? Provider : "local";
    [System.Text.Json.Serialization.JsonIgnore]
    internal string ServiceName => Provider switch { "selfhosted" => "自托管识别", "qwen" => "阿里云百炼", _ => "火山引擎" };
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
        if (provider is not ("local" or "doubao" or "qwen" or "selfhosted"))
            throw new InvalidDataException("未知转录服务，请重新选择。");
        var options = new CloudOptions(
            provider != "local",
            root?["doubao"]?["resourceId"]?.GetValue<string>() ?? "volc.seedasr.sauc.duration",
            Provider: provider == "local" ? "doubao" : provider,
            QwenRegion: root?["qwen"]?["region"]?.GetValue<string>() ?? "cn-beijing",
            SelfHostedUrl: root?["selfhosted"]?["url"]?.GetValue<string>() ?? "",
            SelfHostedModel: root?["selfhosted"]?["model"]?.GetValue<string>() == "Qwen3-ASR-0.6B-8bit"
                ? "Qwen3-ASR-0.6B-8bit" : "Qwen3-ASR-1.7B-8bit");
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

    private static void RemoveSecret(PasswordVault vault, string resource, string path)
    {
        try { vault.Remove(vault.Retrieve(resource, path)); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
    }

    internal static void SaveProvider(string provider)
    {
        if (provider is not ("local" or "doubao" or "qwen" or "selfhosted")) throw new ArgumentException("未知转录服务。");
        var path = HotkeySettings.GetPath();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取听写设置。");
        root["transcriptionProvider"] = provider;
        var doubao = root["doubao"] as JsonObject ?? new JsonObject();
        doubao["enabled"] = provider == "doubao";
        if (root["doubao"] is not JsonObject) root["doubao"] = doubao;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Save(CloudOptions options)
    {
        var path = HotkeySettings.GetPath();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取听写设置。");
        if (options.Backend is not ("local" or "doubao" or "qwen" or "selfhosted"))
            throw new InvalidDataException("未知转录服务。");
        if (options.QwenRegion is not ("cn-beijing" or "ap-southeast-1"))
            throw new InvalidDataException("请选择百炼服务地域。");

        var vault = new PasswordVault();
        // Keep each provider's credentials when switching; no API keys enter settings.json.
        if (!string.IsNullOrWhiteSpace(options.ApiKey) || !string.IsNullOrWhiteSpace(options.AppId)
            || !string.IsNullOrWhiteSpace(options.AccessToken))
            vault.Add(new PasswordCredential(VaultResource, path,
                JsonSerializer.Serialize(new { options.ApiKey, options.AppId, options.AccessToken })));
        else
            RemoveSecret(vault, VaultResource, path);
        if (!string.IsNullOrWhiteSpace(options.QwenApiKey))
            vault.Add(new PasswordCredential(QwenVaultResource, path, options.QwenApiKey));
        else
            RemoveSecret(vault, QwenVaultResource, path);

        root["transcriptionProvider"] = options.Backend;
        var doubao = root["doubao"] as JsonObject ?? new JsonObject();
        doubao["enabled"] = options.Backend == "doubao";
        doubao["resourceId"] = options.ResourceId;
        doubao.Remove("semanticSmoothing");
        if (root["doubao"] is not JsonObject) root["doubao"] = doubao;
        var qwen = root["qwen"] as JsonObject ?? new JsonObject();
        qwen["region"] = options.QwenRegion;
        if (root["qwen"] is not JsonObject) root["qwen"] = qwen;
        var selfhosted = root["selfhosted"] as JsonObject ?? new JsonObject();
        selfhosted["url"] = options.SelfHostedUrl;
        selfhosted["model"] = options.SelfHostedModel;
        if (root["selfhosted"] is not JsonObject) root["selfhosted"] = selfhosted;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
