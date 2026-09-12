namespace Shuo.Services;

internal sealed record ReadingOptions(bool Enabled = false, string Provider = "doubao", bool UseExistingKey = true,
    string Speaker = "zh_female_wenroumama_uranus_bigtts", int SpeechRate = 0,
    string CosyVoiceUrl = "", string CosyVoiceVoice = "default",
    string Qwen3Url = "", string Qwen3Voice = "",
    uint HotkeyModifiers = 3, uint HotkeyVirtualKey = 0x20)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyVirtualKey);
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsSelfHosted => Provider is "cosyvoice" or "qwen3";
    [System.Text.Json.Serialization.JsonIgnore]
    internal string SelfHostedUrl => Provider == "qwen3" ? Qwen3Url : CosyVoiceUrl;
    [System.Text.Json.Serialization.JsonIgnore]
    internal string SelfHostedVoice => Provider == "qwen3" ? Qwen3Voice : CosyVoiceVoice;
    [System.Text.Json.Serialization.JsonIgnore]
    internal int SelfHostedPort => Provider == "qwen3" ? 18767 : 18766;
    [System.Text.Json.Serialization.JsonIgnore]
    internal string ServiceName => Provider == "qwen3" ? "Qwen3-TTS" : "CosyVoice";
    internal string ResourceId => "seed-tts-2.0";

    internal void Validate()
    {
        if (!Hotkey.IsValid) throw new ArgumentException("请设置有效的朗读快捷键。");
        if (SpeechRate is < -50 or > 100) throw new ArgumentException("语速必须在 -50 到 100 之间。");
        if (Provider == "doubao")
        {
            if (string.IsNullOrWhiteSpace(Speaker)) throw new ArgumentException("请填写音色 ID。");
            if (Speaker.StartsWith("S_", StringComparison.Ordinal))
                throw new ArgumentException("请使用豆包语音合成 2.0 的系统音色 ID。");
        }
        else if (IsSelfHosted)
        {
            _ = CosyVoiceAddress.Endpoint(SelfHostedUrl, SelfHostedPort);
            if (!System.Text.RegularExpressions.Regex.IsMatch(SelfHostedVoice, "^[a-z0-9][a-z0-9_-]{0,31}$"))
                throw new ArgumentException("自托管音色 ID 只能包含小写字母、数字、连字符和下划线。");
        }
        else throw new ArgumentException("不支持所选朗读服务。");
    }
}
