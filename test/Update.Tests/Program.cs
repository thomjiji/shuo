using Velopack;
using Velopack.Locators;

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
