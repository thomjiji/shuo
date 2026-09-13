namespace Shuo.Services;

internal sealed record TranslationOptions(string Region = "cn-beijing", string WorkspaceId = "", string TargetLanguage = "zh",
    bool Enabled = false, uint HotkeyModifiers = HotkeyBinding.Control | HotkeyBinding.Alt, uint HotkeyVirtualKey = 0x54,
    string Backend = "cloud", string Host = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyVirtualKey);
}
