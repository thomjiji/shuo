using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Shuo.Services;

internal static partial class TranscriptFormatter
{
    internal static async Task<string> FormatAsync(string text, string? autocorrectPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(autocorrectPath) || !File.Exists(autocorrectPath) || !CjkRegex().IsMatch(text))
            return text;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = autocorrectPath, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--stdin");
            startInfo.ArgumentList.Add("--type");
            startInfo.ArgumentList.Add("txt");
            startInfo.ArgumentList.Add("--no-diff-bg-color");
            process = Process.Start(startInfo);
            if (process is null) return text;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(text.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var output = await outputTask;
            await errorTask;
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return text; }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex CjkRegex();
}

