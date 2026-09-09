

using Windows.ApplicationModel.DataTransfer;

namespace Shuo.Services;

internal static partial class TranscriptPaster
{


    internal static async Task<string> PrepareAsync(string text, string? autocorrectPath, TextCleanupOptions options)
    {
        var formatted = await TranscriptFormatter.FormatAsync(text, autocorrectPath);
        return TextCleanup.Apply(formatted, options);
    }

    internal static void Paste(string formatted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(formatted)) return;
        var package = new DataPackage();
        package.SetText(formatted);
        Clipboard.SetContent(package);
        System.Windows.Forms.SendKeys.SendWait("^v");
    }

}
