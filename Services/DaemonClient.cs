using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsDictation.Services;

internal sealed record DaemonMessage(
    string Type,
    string? Text = null,
    string? Error = null,
    string? Model = null,
    string? AutocorrectPath = null);

internal sealed class DaemonClient : IAsyncDisposable
{
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _stopping;

    internal event EventHandler<DaemonMessage>? MessageReceived;
    internal event EventHandler<string>? ErrorReceived;
    internal event EventHandler? Exited;

    internal bool IsRunning => _process is { HasExited: false };

    internal Task StartAsync(string nodeExecutable, string workerPath)
    {
        if (IsRunning) return Task.CompletedTask;
        if (!File.Exists(workerPath)) throw new FileNotFoundException("Dictation worker was not found.", workerPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = nodeExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(workerPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the dictation worker.");
        }

        _stopping = false;
        _process = process;
        _stdoutTask = ReadOutputAsync(process.StandardOutput, false);
        _stderrTask = ReadOutputAsync(process.StandardError, true);
        return Task.CompletedTask;
    }

    internal async Task SendAsync(string command)
    {
        var process = _process;
        if (process is null || process.HasExited) throw new InvalidOperationException("The dictation worker has stopped.");

        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
    }

    private async Task ReadOutputAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (isError)
            {
                ErrorReceived?.Invoke(this, line);
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                MessageReceived?.Invoke(this, new DaemonMessage(
                    GetString(root, "type") ?? "error",
                    GetString(root, "text"),
                    GetString(root, "message"),
                    GetString(root, "model"),
                    GetString(root, "autocorrectPath")));
            }
            catch (JsonException)
            {
                ErrorReceived?.Invoke(this, line);
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        if (!_stopping) ErrorReceived?.Invoke(this, "The dictation worker stopped unexpectedly.");
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        _stopping = true;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("shutdown");
                    await process.StandardInput.FlushAsync();
                }
                catch (InvalidOperationException)
                {
                    // The worker closed stdin before shutdown.
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                }
            }
        }
        finally
        {
            if (_stdoutTask is not null) await IgnoreFailureAsync(_stdoutTask);
            if (_stderrTask is not null) await IgnoreFailureAsync(_stderrTask);
            process.Dispose();
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Process teardown has already handled the worker failure.
        }
    }
}
