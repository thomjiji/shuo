namespace Shuo.Services;

internal sealed record ReadingOptions(bool Enabled = false, string Provider = "doubao", bool UseExistingKey = true,
    string Speaker = "zh_female_wenroumama_uranus_bigtts", int SpeechRate = 0,
    string CosyVoiceUrl = "", string CosyVoiceVoice = "default",
    uint HotkeyModifiers = 3, uint HotkeyVirtualKey = 0x20)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyVirtualKey);
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
        else if (Provider == "cosyvoice")
        {
            _ = CosyVoiceAddress.Endpoint(CosyVoiceUrl);
            if (!System.Text.RegularExpressions.Regex.IsMatch(CosyVoiceVoice, "^[a-z0-9][a-z0-9_-]{0,31}$"))
                throw new ArgumentException("CosyVoice 音色 ID 只能包含小写字母、数字、连字符和下划线。");
        }
        else throw new ArgumentException("不支持所选朗读服务。");
    }
}
