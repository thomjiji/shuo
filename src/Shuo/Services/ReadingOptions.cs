namespace Shuo.Services;

internal sealed record ReadingOptions(bool Enabled = false, bool UseExistingKey = true,
    string Speaker = "zh_female_vv_uranus_bigtts", int SpeechRate = 0,
    uint HotkeyModifiers = 3, uint HotkeyVirtualKey = 0x20, bool TranslateToChinese = false,
    int TranslationSpeechRate = 1, bool UseSelfHostedTranslation = false,
    string SelfHostedHost = "", double LocalPlaybackSpeed = 1.0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyVirtualKey);
    internal string ResourceId => "seed-tts-2.0";

    internal void Validate()
    {
        if (!Hotkey.IsValid) throw new ArgumentException("请设置有效的朗读快捷键。");
        if (TranslationSpeechRate is < -1 or > 2) throw new ArgumentException("请选择有效的中文译读语速。");
        if (LocalPlaybackSpeed is not (0.85 or 1.0 or 1.15 or 1.3)) throw new ArgumentException("请选择有效的本地播放速度。");
        if (TranslateToChinese) return;
        if (string.IsNullOrWhiteSpace(Speaker)) throw new ArgumentException("请选择朗读音色。");
        if (Speaker.StartsWith("S_", StringComparison.Ordinal))
            throw new ArgumentException("已保存的音色不可用，请重新选择。");
        if (SpeechRate is < -50 or > 100) throw new ArgumentException("语速必须在 -50 到 100 之间。");
    }
}
