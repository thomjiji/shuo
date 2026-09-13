using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Security.Credentials;

namespace Shuo.Services;

internal sealed record DoubaoCredentials(string ApiKey = "", string AppId = "", string AccessToken = "");

// Service connection and credential ownership is independent of feature pages.
// Keep existing storage keys so upgrades retain configured endpoints and vault entries.
internal static class ServiceSettings
{
    internal const string Doubao = "shuo-doubao";
    internal const string BailianAsr = "shuo-qwen";
    internal const string BailianTranslation = "shuo-qwen-translation";
    internal const string DoubaoSpeech = "shuo-doubao-tts";

    internal static string ReadSecret(string resource, string? path = null)
    {
        PasswordCredential credential;
        try { credential = new PasswordVault().Retrieve(resource, path ?? HotkeySettings.GetPath()); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return ""; }
        credential.RetrievePassword();
        return credential.Password;
    }

    internal static void SaveSecret(string resource, string value, string? path = null)
    {
        path ??= HotkeySettings.GetPath();
        var vault = new PasswordVault();
        if (!string.IsNullOrWhiteSpace(value)) vault.Add(new PasswordCredential(resource, path, value.Trim()));
        else
        {
            try { vault.Remove(vault.Retrieve(resource, path)); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
        }
    }

    internal static DoubaoCredentials LoadDoubao(string? path = null)
    {
        var secret = ReadSecret(Doubao, path);
        return string.IsNullOrWhiteSpace(secret) ? new()
            : JsonSerializer.Deserialize<DoubaoCredentials>(secret) ?? throw new InvalidDataException("无法读取豆包凭据。");
    }

    internal static string LoadSpeechKey(bool useSharedKey) => useSharedKey ? LoadDoubao().ApiKey : ReadSecret(DoubaoSpeech);

    internal static string ReadingHost(JsonNode? root) => First(
        Text(root, "reading", "SelfHostedHost"), Text(root, "translation", "host"), AsrHost(root));

    internal static string CaptionHost(JsonNode? root) => First(
        Text(root, "translation", "host"), Text(root, "reading", "SelfHostedHost"), AsrHost(root));

    private static string AsrHost(JsonNode? root) => SelfHostedAddress.ToDisplay(Text(root, "selfhosted", "url"));

    private static string Text(JsonNode? root, string group, string key) =>
        root is JsonObject obj && obj[group] is JsonObject section && section[key] is JsonValue value
            && value.TryGetValue<string>(out var text) ? text : "";

    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
