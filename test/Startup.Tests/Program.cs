using Shuo.Services;

var currentPath = Path.GetFullPath(@"C:\Program Files\Shuo\shuo.exe");
var oldPath = Path.GetFullPath(@"C:\Users\test\AppData\Local\ShuoDesktop\old\shuo.exe");
var registry = new FakeStartupRegistry();
var registration = new StartupRegistration(currentPath, registry);

Assert(registration.Read().State == StartupRegistrationState.Disabled, "Missing registration is disabled.");
Assert(!StartupRegistration.IsStartupLaunch([currentPath]), "A normal launch is not a startup launch.");
Assert(StartupRegistration.IsStartupLaunch([currentPath, "--STARTUP"]), "The startup argument is case insensitive.");

var enabled = registration.SetEnabled(true);
Assert(enabled.State == StartupRegistrationState.Enabled, "Enabling creates a working registration.");
Assert(registry.Command == $"\"{currentPath}\" --startup", "Executable paths are quoted and use the startup argument.");
var writesAfterEnable = registry.Writes;
registration.SetEnabled(true);
Assert(registry.Writes == writesAfterEnable && registry.Command == $"\"{currentPath}\" --startup", "Repeated enabling is idempotent.");

registry.Command = $"\"{oldPath}\" --startup";
var repaired = registration.Refresh();
Assert(repaired.State == StartupRegistrationState.Enabled && repaired.Repaired, "An owned stale path is repaired.");
Assert(registry.Command == $"\"{currentPath}\" --startup", "Path repair writes the current executable.");

registry.Command = "\"C:\\Other App\\other.exe\" --startup";
Assert(registration.Refresh().State == StartupRegistrationState.Conflict, "An unrelated same-name entry is reported as a conflict.");
ExpectFailure(() => registration.SetEnabled(false), "Disabling does not delete an unrelated same-name entry.");
Assert(registry.Command is not null, "The conflicting entry remains untouched.");
registration.SetEnabled(true);
Assert(registration.Read().State == StartupRegistrationState.Enabled, "Explicit enabling replaces a conflicting entry.");

registration.SetEnabled(false);
Assert(registration.Read().State == StartupRegistrationState.Disabled && registry.Command is null, "Disabling removes the owned registration.");
registration.SetEnabled(false);
Assert(registry.Command is null, "Repeated disabling is idempotent.");

registry.IgnoreWrites = true;
ExpectFailureOf<IOException>(() => registration.SetEnabled(true), "A rejected registry write is reported.");
registry.IgnoreWrites = false;
registration.SetEnabled(true);
registry.IgnoreDeletes = true;
ExpectFailureOf<IOException>(() => registration.SetEnabled(false), "A rejected registry delete is reported.");
registry.IgnoreDeletes = false;
registration.SetEnabled(false);

foreach (var invalid in new[]
{
    "\"C:\\Program Files\\Shuo\\shuo.exe\"",
    "\"C:\\Program Files\\Shuo\\other.exe\" --startup",
    "\"C:\\Program Files\\Shuo\\shuo.exe\" --startup --extra",
    "\"C:\\Program Files\\Shuo\\shuo.exe --startup",
})
    Assert(!StartupRegistration.TryGetOwnedExecutablePath(invalid, out _), "Malformed or unrelated commands are not claimed.");

Console.WriteLine("Passed startup argument, registration, path repair, conflict and idempotency checks.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void ExpectFailure(Action operation, string message)
{
    ExpectFailureOf<InvalidOperationException>(operation, message);
}

static void ExpectFailureOf<TException>(Action operation, string message) where TException : Exception
{
    try { operation(); }
    catch (TException) { return; }
    throw new Exception(message);
}

sealed class FakeStartupRegistry : IStartupRegistry
{
    public string? Command { get; set; }
    public int Writes { get; private set; }
    public bool IgnoreWrites { get; set; }
    public bool IgnoreDeletes { get; set; }

    public string? Read() => Command;
    public void Write(string command)
    {
        Writes++;
        if (!IgnoreWrites) Command = command;
    }
    public void Delete()
    {
        if (!IgnoreDeletes) Command = null;
    }
}
