using WindowsDictation.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

var fillers = new TextCleanupOptions(RemoveFillerWords: true);
var periods = new TextCleanupOptions(TrimTrailingPeriod: true);
var both = new TextCleanupOptions(true, true);
var cases = new (string Input, string Expected, TextCleanupOptions Options)[]
{
    ("我想，呃，明天再试。", "我想，呃，明天再试。", new()),
    ("我想，呃，明天再试。", "我想，明天再试。", fillers),
    ("嗯……我再想想。", "我再想想。", fillers),
    ("嗯，呃，明天。", "明天。", fillers),
    ("我想，嗯，呃，明天。", "我想，明天。", fillers),
    ("嗯。", "嗯。", fillers),
    ("嗯，呃……", "嗯，呃……", fillers),
    ("好啊。", "好啊。", fillers),
    ("啊，我知道了。", "啊，我知道了。", fillers),
    ("然后我们明天出发。", "然后我们明天出发。", fillers),
    ("嗯我知道了。", "嗯我知道了。", fillers),
    ("他说：“我想，嗯，再试。”", "他说：“我想，嗯，再试。”", fillers),
    ("他说：\"我想，呃，再试。\"", "他说：\"我想，呃，再试。\"", fillers),
    ("是。", "是", periods),
    ("是？", "是？", periods),
    ("是！", "是！", periods),
    ("等等……", "等等……", periods),
    ("Wait...", "Wait...", periods),
    ("第一句。第二句。", "第一句。第二句", periods),
    ("“是。”", "“是。”", periods),
    ("“是。", "“是。", periods),
    ("是。\r\n", "是\r\n", periods),
    ("Hello world.", "Hello world", periods),
    ("Talk to Dr.", "Talk to Dr.", periods),
    ("U.S.", "U.S.", periods),
    ("Version 3.14.", "Version 3.14.", periods),
    ("Visit https://example.com.", "Visit https://example.com.", periods),
    ("user@example.com.", "user@example.com.", periods),
    ("嗯，明天再试。", "明天再试", both),
    ("", "", both),
    ("...", "...", both),
};

foreach (var (input, expected, options) in cases)
{
    var actual = TextCleanup.Apply(input, options);
    if (actual != expected) throw new Exception($"Input: {input}\nExpected: {expected}\nActual: {actual}");
}
Console.WriteLine($"Passed {cases.Length} text cleanup cases.");

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "winspeak-settings-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
var settingsPath = Path.Combine(temporaryDirectory, "settings.json");
var previousOverride = Environment.GetEnvironmentVariable("WINDOWS_DICTATION_SETTINGS");
Environment.SetEnvironmentVariable("WINDOWS_DICTATION_SETTINGS", settingsPath);
try
{
    Assert(TextCleanupSettings.Load() == new TextCleanupOptions(), "Missing settings default to disabled.");
    File.WriteAllText(settingsPath, """{"model":"existing-model","hotkey":{"modifiers":3,"virtualKey":220},"custom":{"keep":true}}""");
    Assert(TextCleanupSettings.Load() == new TextCleanupOptions(), "Missing flags default to disabled.");
    foreach (var options in new[] { fillers, periods, both, new TextCleanupOptions() })
    {
        TextCleanupSettings.Save(options);
        Assert(TextCleanupSettings.Load() == options, "Both toggles round-trip independently.");
        var stored = JsonNode.Parse(File.ReadAllText(settingsPath))!;
        Assert(stored["model"]!.GetValue<string>() == "existing-model", "Model is preserved.");
        Assert(stored["hotkey"]!["modifiers"]!.GetValue<int>() == 3
            && stored["hotkey"]!["virtualKey"]!.GetValue<int>() == 220, "Hotkey is preserved.");
        Assert(stored["custom"]!["keep"]!.GetValue<bool>(), "Unknown nested fields are preserved.");
    }

    foreach (var malformed in new[] { "{invalid", "[]", "null" })
    {
        File.WriteAllText(settingsPath, malformed);
        ExpectFailure(() => TextCleanupSettings.Load());
        ExpectFailure(() => TextCleanupSettings.Save(both));
        Assert(File.ReadAllText(settingsPath) == malformed, "Invalid settings remain unchanged.");
    }
    Assert(Directory.GetFiles(temporaryDirectory).Length == 1, "No temporary files remain.");
    Console.WriteLine("Passed settings defaults, persistence, preservation and corruption checks.");
}
finally
{
    Environment.SetEnvironmentVariable("WINDOWS_DICTATION_SETTINGS", previousOverride);
    File.Delete(settingsPath);
    Directory.Delete(temporaryDirectory);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void ExpectFailure(Action operation)
{
    try { operation(); }
    catch (Exception error) when (error is JsonException or InvalidDataException) { return; }
    throw new Exception("Invalid settings should cause an explicit error.");
}
