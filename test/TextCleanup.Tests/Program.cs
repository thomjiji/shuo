using Shuo.Services;
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

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "shuo-settings-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
var settingsPath = Path.Combine(temporaryDirectory, "settings.json");
var previousOverride = Environment.GetEnvironmentVariable("SHUO_SETTINGS");
Environment.SetEnvironmentVariable("SHUO_SETTINGS", settingsPath);
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
    Environment.SetEnvironmentVariable("SHUO_SETTINGS", previousOverride);
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

var localSettingsDirectory = Path.Combine(Path.GetTempPath(), "shuo-path-tests");
var currentSettings = Path.Combine(localSettingsDirectory, "Shuo", "settings.json");
var previousSettings = Path.Combine(localSettingsDirectory, "WindowsDictation", "settings.json");
foreach (var paths in new[] { Array.Empty<string>(), new[] { currentSettings }, new[] { previousSettings }, new[] { currentSettings, previousSettings } })
{
    var expected = paths.Contains(currentSettings) || !paths.Contains(previousSettings) ? currentSettings : previousSettings;
    Assert(HotkeySettings.ResolvePath(null, null, localSettingsDirectory, paths.Contains) == expected, "Prefer new settings, reuse old settings if needed.");
}
var primaryOverride = Path.Combine(localSettingsDirectory, "primary.json");
var legacyOverride = Path.Combine(localSettingsDirectory, "legacy.json");
bool NoDefaultLookup(string path) => throw new Exception("An explicit override must not inspect default files.");
Assert(HotkeySettings.ResolvePath(primaryOverride, legacyOverride, localSettingsDirectory, NoDefaultLookup) == primaryOverride, "SHUO_SETTINGS takes precedence.");
Assert(HotkeySettings.ResolvePath(null, legacyOverride, localSettingsDirectory, NoDefaultLookup) == legacyOverride, "The previous environment variable remains compatible.");
Assert(HotkeySettings.ResolvePath("  ", legacyOverride, localSettingsDirectory, NoDefaultLookup) == legacyOverride, "An empty new override does not hide a legacy override.");
Console.WriteLine("Passed Shuo settings path compatibility checks.");

var historyDirectory = Path.Combine(Path.GetTempPath(), "shuo-history-tests-" + Guid.NewGuid().ToString("N"));
var historyPath = Path.Combine(historyDirectory, "history.jsonl");
try
{
    var history = new TranscriptHistory(historyPath);
    Assert(history.Load(out var skipped).Count == 0 && skipped == 0, "A fresh install has no history.");
    var first = new TranscriptEntry(DateTimeOffset.Parse("2026-09-06T09:00:00+08:00"), "第一行\n第二行 \"quoted\" \\ path", "豆包云端");
    history.Append(first);
    Assert(File.ReadAllText(historyPath).Contains("第一行"), "New history stores directly searchable Unicode.");
    history.Append(first);
    var restored = new TranscriptHistory(historyPath).Load(out skipped);
    Assert(restored.Count == 2 && restored.All(item => item == first), "Restart preserves Unicode, newlines, timestamps, and repeated dictations.");
    File.AppendAllText(historyPath, "{\"CreatedAt\":");
    var latest = new TranscriptEntry(first.CreatedAt.AddMinutes(1), "本地模型最终文本", "本地模型");
    history.Append(latest);
    restored = history.Load(out skipped);
    Assert(restored.Count == 3 && restored[0] == latest && skipped == 1, "An interrupted write cannot swallow the next record; latest entries appear first.");
    var exportedPath = history.ExportText(out var exportSkipped);
    var exported = File.ReadAllText(exportedPath);
    Assert(exported.Contains(first.Text) && exported.Contains(latest.Text) && exportSkipped == 1, "Text export includes decoded multiline text and reports damaged rows.");
    File.AppendAllText(historyPath, "\n" + JsonSerializer.Serialize(first) + "\n");
    Assert(File.ReadAllText(history.ExportText(out _)).Contains(first.Text), "Legacy escaped records remain readable in text exports.");
    var sizeBeforeEmpty = new FileInfo(historyPath).Length;
    history.Append(latest with { Text = "  " });
    Assert(new FileInfo(historyPath).Length == sizeBeforeEmpty, "Empty results do not create history.");
    for (var index = 0; index < 110; index++) history.Append(latest with { Text = $"Record {index}" });
    Assert(history.Load(out skipped).Count == 114, "History is not truncated at the UI page size.");
    using (var locked = new FileStream(historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var failed = false;
        try { history.Append(latest); } catch (IOException) { failed = true; }
        Assert(failed, "Persistence failures are reported to the caller.");
    }
    Assert(history.Load(out skipped).Count == 114, "Failed writes leave saved records intact.");
    Console.WriteLine("Passed transcript history persistence and recovery checks.");
}
finally
{
    if (File.Exists(Path.ChangeExtension(historyPath, ".txt"))) File.Delete(Path.ChangeExtension(historyPath, ".txt"));
    if (File.Exists(historyPath)) File.Delete(historyPath);
    if (Directory.Exists(historyDirectory)) Directory.Delete(historyDirectory);
}
