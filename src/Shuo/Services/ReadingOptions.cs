using System.Text;

namespace Shuo.Services;

internal static class SelfHostedTranslationModels
{
    internal const string Default = "mlx-community/Qwen3-8B-4bit";
}

internal static class SelfHostedSpeechModels
{
    internal const string Default = "mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit";
    internal const string Large = "mlx-community/Qwen3-TTS-12Hz-1.7B-CustomVoice-8bit";
    internal const string LegacyDefaultPrompt = "请用自然、克制、清晰的中文文章朗读方式，根据语义安排停连和重音；突出转折、否定、数字与结论，不要逐字播报，不要夸张表演。";
    internal const string DefaultPrompt = "请用平实、专业、克制、清晰的中文文章朗读方式。句间停顿应简短自然，只在段落边界或语义确有需要时稍作停顿；不要为了制造情绪、悬念或起承转合刻意延长停顿，不要戏剧化表演。准确读出否定、数字与结论，不要逐字播报。";
    internal const int MaxPromptLength = 300;
    internal const int MaxPromptBytes = 1200;
    internal static readonly string[] All = [Default, Large];

    internal static bool IsSupported(string model) => All.Contains(model, StringComparer.Ordinal);

    internal static string NormalizePrompt(string? prompt)
    {
        if (prompt is null) return DefaultPrompt;
        var value = prompt.Trim();
        return value == LegacyDefaultPrompt ? DefaultPrompt : value;
    }

    internal static string ValidatePrompt(string prompt)
    {
        var value = prompt?.Trim() ?? "";
        if (value.Length > MaxPromptLength || Encoding.UTF8.GetByteCount(value) > MaxPromptBytes)
            throw new ArgumentException($"朗读提示词不能超过 {MaxPromptLength} 个字符。");
        return value;
    }
}

internal static class SelfHostedSpeechVoices
{
    internal const string Default = "Serena";
    internal const string Alternative = "Vivian";
    internal static readonly string[] All = [Default, Alternative];

    internal static bool IsSupported(string voice) => All.Contains(voice, StringComparer.Ordinal);
}

internal sealed record ReadingOptions(bool Enabled = false, bool UseExistingKey = true,
    string Speaker = "zh_female_vv_uranus_bigtts", int SpeechRate = 0,
    uint HotkeyModifiers = 3, uint HotkeyVirtualKey = 0x20, bool TranslateToChinese = false,
    bool UseSelfHostedTranslation = false,
    string SelfHostedHost = "", double LocalPlaybackSpeed = 1.0, bool UseSelfHostedOriginal = false,
    string SelfHostedSpeechModel = SelfHostedSpeechModels.Default,
    string SelfHostedSpeechPrompt = SelfHostedSpeechModels.DefaultPrompt,
    string SelfHostedSpeechVoice = SelfHostedSpeechVoices.Default)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyVirtualKey);
    internal string ResourceId => "seed-tts-2.0";

    internal void Validate()
    {
        if (!Hotkey.IsValid) throw new ArgumentException("请设置有效的朗读快捷键。");
        if (LocalPlaybackSpeed is not (0.85 or 1.0 or 1.15 or 1.3)) throw new ArgumentException("请选择有效的本地播放速度。");
        if (!SelfHostedSpeechModels.IsSupported(SelfHostedSpeechModel)) throw new ArgumentException("请选择支持的自托管语音合成模型。");
        if (!SelfHostedSpeechVoices.IsSupported(SelfHostedSpeechVoice)) throw new ArgumentException("请选择支持的自托管朗读音色。");
        SelfHostedSpeechModels.ValidatePrompt(SelfHostedSpeechPrompt);
        if (TranslateToChinese ? UseSelfHostedTranslation : UseSelfHostedOriginal) return;
        if (string.IsNullOrWhiteSpace(Speaker)) throw new ArgumentException("请选择朗读音色。");
        if (Speaker.StartsWith("S_", StringComparison.Ordinal))
            throw new ArgumentException("已保存的音色不可用，请重新选择。");
        if (SpeechRate is < -50 or > 100) throw new ArgumentException("语速必须在 -50 到 100 之间。");
    }
}
