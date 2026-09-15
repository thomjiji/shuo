using Microsoft.Win32;

namespace Shuo.Services;

internal enum StartupRegistrationState
{
    Disabled,
    Enabled,
    Conflict,
}

internal readonly record struct StartupRegistrationSnapshot(StartupRegistrationState State, bool Repaired = false);

internal interface IStartupRegistry
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class WindowsStartupRegistry : IStartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Shuo";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("无法打开当前用户的 Windows 启动项。");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal sealed class StartupRegistration
{
    internal const string StartupArgument = "--startup";
    private const string ExecutableName = "shuo.exe";
    private readonly string _executablePath;
    private readonly string _expectedCommand;
    private readonly IStartupRegistry _registry;

    internal StartupRegistration(string executablePath, IStartupRegistry registry)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _expectedCommand = BuildCommand(_executablePath);
        _registry = registry;
    }

    internal static StartupRegistration CreateCurrent()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            executablePath = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        return new StartupRegistration(executablePath, new WindowsStartupRegistry());
    }

    internal static bool IsStartupLaunch(IEnumerable<string> arguments) =>
        arguments.Skip(1).Any(argument => string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase));

    internal StartupRegistrationSnapshot Refresh()
    {
        var command = _registry.Read();
        if (command is null) return new(StartupRegistrationState.Disabled);
        if (!TryGetOwnedExecutablePath(command, out var registeredPath))
            return new(StartupRegistrationState.Conflict);
        if (string.Equals(registeredPath, _executablePath, StringComparison.OrdinalIgnoreCase))
            return new(StartupRegistrationState.Enabled);

        _registry.Write(_expectedCommand);
        return new(StartupRegistrationState.Enabled, Repaired: true);
    }

    internal StartupRegistrationSnapshot SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var command = _registry.Read();
            if (!TryGetOwnedExecutablePath(command ?? "", out var registeredPath)
                || !string.Equals(registeredPath, _executablePath, StringComparison.OrdinalIgnoreCase))
                _registry.Write(_expectedCommand);
        }
        else
        {
            var command = _registry.Read();
            if (command is not null && !TryGetOwnedExecutablePath(command, out _))
                throw new InvalidOperationException("Windows 中存在同名但不属于 Shuo 的启动项，未将其删除。");
            _registry.Delete();
        }

        var snapshot = Read();
        if (enabled && snapshot.State != StartupRegistrationState.Enabled)
            throw new IOException("Windows 未保留 Shuo 的启动项。");
        if (!enabled && snapshot.State != StartupRegistrationState.Disabled)
            throw new IOException("Windows 未删除 Shuo 的启动项。");
        return snapshot;
    }

    internal StartupRegistrationSnapshot Read()
    {
        var command = _registry.Read();
        if (command is null) return new(StartupRegistrationState.Disabled);
        if (!TryGetOwnedExecutablePath(command, out var registeredPath))
            return new(StartupRegistrationState.Conflict);
        return new(string.Equals(registeredPath, _executablePath, StringComparison.OrdinalIgnoreCase)
            ? StartupRegistrationState.Enabled
            : StartupRegistrationState.Disabled);
    }

    internal static string BuildCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" {StartupArgument}";

    internal static bool TryGetOwnedExecutablePath(string command, out string executablePath)
    {
        executablePath = "";
        var trimmed = command.Trim();
        const string suffix = " --startup";
        if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        var executable = trimmed[..^suffix.Length].Trim();
        if (executable.Length >= 2 && executable[0] == '"' && executable[^1] == '"')
            executable = executable[1..^1];
        else if (executable.Contains('"'))
            return false;

        try
        {
            executablePath = Path.GetFullPath(executable);
            return string.Equals(Path.GetFileName(executablePath), ExecutableName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            executablePath = "";
            return false;
        }
    }
}
