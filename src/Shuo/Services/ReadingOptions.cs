namespace Shuo.Services;

internal sealed record ReadingOptions(bool Enabled = false, bool UseExistingKey = true,
    string Speaker = "zh_female_vv_uranus_bigtts", int SpeechRate = 0,
    uint HotkeyModifiers = 3, uint HotkeyVirtualKey = 0x20)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyVirtualKey);
    internal string ResourceId => "seed-tts-2.0";

    internal void Validate()
    {
        if (!Hotkey.IsValid) throw new ArgumentException("请设置有效的朗读快捷键。");
        if (string.IsNullOrWhiteSpace(Speaker)) throw new ArgumentException("请填写音色 ID。");
        if (Speaker.StartsWith("S_", StringComparison.Ordinal))
            throw new ArgumentException("请使用豆包语音合成 2.0 的系统音色 ID。");
        if (SpeechRate is < -50 or > 100) throw new ArgumentException("语速必须在 -50 到 100 之间。");
    }
}
