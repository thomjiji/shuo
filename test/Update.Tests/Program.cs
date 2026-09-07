using Velopack;
using Velopack.Locators;

if (args is ["--network-check"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    // Run explicitly on Windows to verify the real system proxy and both request paths.
    var downloader = new Shuo.SystemProxyDownloader();
    const string url = "https://api.github.com/repos/thomjiji/shuo/releases/latest";
    var json = await downloader.DownloadString(url, null, 0.5);
    using var metadata = System.Text.Json.JsonDocument.Parse(json);
    var file = Path.GetTempFileName();
    try
    {
        await downloader.DownloadFile(url, file, _ => { }, null, 0.5);
        using var downloaded = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
        if (metadata.RootElement.GetProperty("tag_name").GetString() != downloaded.RootElement.GetProperty("tag_name").GetString())
            throw new Exception("Downloaded release metadata differs.");
        Console.WriteLine("Passed HTTPS metadata check and file download using the Windows system proxy.");
    }
    finally { File.Delete(file); }
    return;
}

if (args.Length < 3) throw new ArgumentException("Expected feed directory, target version, and app id.");
var feed = Path.GetFullPath(args[0]);
var targetVersion = args[1];
var appId = args[2];
var root = Path.Combine(Path.GetTempPath(), "shuo-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var locator = new TestVelopackLocator(appId, "0.0.1", root, root, root, Path.Combine(root, "Update.exe"), "win");
    var manager = new UpdateManager(feed, locator: locator);
    var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("Expected an available update.");
    if (update.TargetFullRelease.Version.ToString() != targetVersion) throw new Exception("Wrong target version.");
    await manager.DownloadUpdatesAsync(update);
    if (manager.UpdatePendingRestart?.Version.ToString() != targetVersion) throw new Exception("Downloaded update must be pending restart.");
    var current = new UpdateManager(feed, locator: new TestVelopackLocator(appId, targetVersion, root, root, root, Path.Combine(root, "Update.exe"), "win"));
    if (await current.CheckForUpdatesAsync() is not null) throw new Exception("Do not offer the current version again.");
    var newer = new UpdateManager(feed, locator: new TestVelopackLocator(appId, "99.0.0", root, root, root, Path.Combine(root, "Update.exe"), "win"));
    if (await newer.CheckForUpdatesAsync() is not null) throw new Exception("Do not downgrade newer installations.");
    Console.WriteLine("Passed feed discovery, package checksum/download, pending update, and version checks.");
}
finally
{
    foreach (var file in Directory.GetFiles(root)) File.Delete(file);
    Directory.Delete(root);
}
