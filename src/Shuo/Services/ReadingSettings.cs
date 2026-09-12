using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Security.Credentials;

namespace Shuo.Services;

internal static class ReadingSettings
{
    private const string Resource = "shuo-doubao-tts";

    internal static ReadingOptions Load()
    {
        var path = HotkeySettings.GetPath();
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        return root?["reading"]?.Deserialize<ReadingOptions>() ?? new();
    }

    internal static string LoadApiKey()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, HotkeySettings.GetPath());
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return ""; }
    }

    internal static void Save(ReadingOptions options, string apiKey)
    {
        var path = HotkeySettings.GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("无法读取设置文件。") : new JsonObject();
        var vault = new PasswordVault();
        if (!string.IsNullOrWhiteSpace(apiKey)) vault.Add(new PasswordCredential(Resource, path, apiKey.Trim()));
        else
        {
            try { vault.Remove(vault.Retrieve(Resource, path)); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
        }
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
