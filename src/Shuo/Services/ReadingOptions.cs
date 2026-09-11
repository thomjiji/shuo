namespace Shuo.Services;

internal sealed record ReadingOptions(bool Enabled = false, bool UseExistingKey = true,
    string Speaker = "zh_female_vv_uranus_bigtts", int SpeechRate = 0)
{
    internal string ResourceId => "seed-tts-2.0";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Speaker)) throw new ArgumentException("请填写音色 ID。");
        if (Speaker.StartsWith("S_", StringComparison.Ordinal))
            throw new ArgumentException("请使用豆包语音合成 2.0 的系统音色 ID。");
        if (SpeechRate is < -50 or > 100) throw new ArgumentException("语速必须在 -50 到 100 之间。");
    }
}
