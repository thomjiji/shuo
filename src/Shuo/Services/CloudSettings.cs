using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shuo.Services;

internal sealed record CloudOptions(bool Enabled = false, string ResourceId = "volc.seedasr.sauc.duration",
    string ApiKey = "", string AppId = "", string AccessToken = "", string Provider = "doubao",
    string QwenApiKey = "", string QwenRegion = "cn-beijing", string SelfHostedUrl = "", string SelfHostedModel = "Qwen3-ASR-1.7B-8bit", string QwenModel = "fun-asr-realtime")
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string Backend => Enabled ? Provider : "local";
    [System.Text.Json.Serialization.JsonIgnore]
    internal string ServiceName => Provider switch { "selfhosted" => "自托管（MLX）", "qwen" => "阿里云百炼", _ => "火山引擎" };
}

internal static class CloudSettings
{

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
            QwenRegion: "cn-beijing",
            QwenModel: root?["qwen"]?["model"]?.GetValue<string>() == "qwen3-asr-flash-realtime"
                ? "qwen3-asr-flash-realtime" : "fun-asr-realtime",
            SelfHostedUrl: root?["selfhosted"]?["url"]?.GetValue<string>() ?? "",
            SelfHostedModel: root?["selfhosted"]?["model"]?.GetValue<string>() == "Qwen3-ASR-0.6B-8bit"
                ? "Qwen3-ASR-0.6B-8bit" : "Qwen3-ASR-1.7B-8bit");
        var credentials = ServiceSettings.LoadDoubao(path);
        return options with { ApiKey = credentials.ApiKey, AppId = credentials.AppId, AccessToken = credentials.AccessToken,
            QwenApiKey = ServiceSettings.ReadSecret(ServiceSettings.BailianAsr, path) };
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

        var hasDoubao = !string.IsNullOrWhiteSpace(options.ApiKey) || !string.IsNullOrWhiteSpace(options.AppId)
            || !string.IsNullOrWhiteSpace(options.AccessToken);
        ServiceSettings.SaveSecret(ServiceSettings.Doubao, hasDoubao
            ? JsonSerializer.Serialize(new DoubaoCredentials(options.ApiKey, options.AppId, options.AccessToken)) : "", path);
        ServiceSettings.SaveSecret(ServiceSettings.BailianAsr, options.QwenApiKey, path);

        root["transcriptionProvider"] = options.Backend;
        var doubao = root["doubao"] as JsonObject ?? new JsonObject();
        doubao["enabled"] = options.Backend == "doubao";
        doubao["resourceId"] = options.ResourceId;
        doubao.Remove("semanticSmoothing");
        if (root["doubao"] is not JsonObject) root["doubao"] = doubao;
        var qwen = root["qwen"] as JsonObject ?? new JsonObject();
        qwen["region"] = options.QwenRegion;
        qwen["model"] = options.QwenModel;
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
