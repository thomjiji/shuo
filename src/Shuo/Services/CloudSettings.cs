using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Security.Credentials;

namespace Shuo.Services;

internal sealed record CloudOptions(bool Enabled = false, string ResourceId = "volc.seedasr.sauc.duration",
    string ApiKey = "", string AppId = "", string AccessToken = "");

internal static class CloudSettings
{
    private const string VaultResource = "shuo-doubao";

    internal static CloudOptions Load()
    {
        var path = HotkeySettings.GetPath();
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        var options = new CloudOptions(
            root?["doubao"]?["enabled"]?.GetValue<bool>() ?? false,
            root?["doubao"]?["resourceId"]?.GetValue<string>() ?? "volc.seedasr.sauc.duration");
        var vault = new PasswordVault();
        PasswordCredential? credential = null;
        try { credential = vault.Retrieve(VaultResource, path); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
        if (credential is null) return options;
        credential.RetrievePassword();
        var secret = JsonSerializer.Deserialize<CloudOptions>(credential.Password)
            ?? throw new InvalidDataException("无法读取豆包凭据。");
        return options with { ApiKey = secret.ApiKey, AppId = secret.AppId, AccessToken = secret.AccessToken };
    }

    internal static void Save(CloudOptions options)
    {
        var path = HotkeySettings.GetPath();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取听写设置。");
        if (options.Enabled && string.IsNullOrWhiteSpace(options.ApiKey)
            && (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.AccessToken)))
            throw new InvalidDataException("请填写 API Key，或旧版 App ID 和 Access Token。");
        new PasswordVault().Add(new PasswordCredential(VaultResource, path, JsonSerializer.Serialize(options)));
        root["doubao"] = new JsonObject { ["enabled"] = options.Enabled, ["resourceId"] = options.ResourceId };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
