using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;

namespace WindowsDictation.Services;

internal static partial class TranscriptPaster
{
    private static readonly Regex Cjk = CjkRegex();

    internal static async Task PasteAsync(string text, string? autocorrectPath)
    {
        var formatted = await FormatAsync(text, autocorrectPath);
        var package = new DataPackage();
        package.SetText(formatted);
        Clipboard.SetContent(package);
        NativeMethods.SendPasteShortcut();
    }

    private static async Task<string> FormatAsync(string text, string? autocorrectPath)
    {
        if (string.IsNullOrWhiteSpace(autocorrectPath) || !File.Exists(autocorrectPath) || !Cjk.IsMatch(text))
        {
            return text;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = autocorrectPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--stdin");
            startInfo.ArgumentList.Add("--type");
            startInfo.ArgumentList.Add("txt");
            startInfo.ArgumentList.Add("--no-diff-bg-color");

            using var process = Process.Start(startInfo);
            if (process is null) return text;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();
            var output = await outputTask;
            await errorTask;
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : text;
        }
        catch (Exception)
        {
            return text;
        }
    }

    [GeneratedRegex("\\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex CjkRegex();
}
