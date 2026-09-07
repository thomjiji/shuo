using System.Net.Http;
using System.Security.Cryptography;

namespace Shuo.Services;

internal static class LocalModelDownload
{
    internal const string FileName = "Qwen3-ASR-0.6B-Q8_0.gguf";
    internal const long FileSize = 850423456;
    internal const string Sha256 = "f081b2d5e23bd669d92cc331d722a8a0681943b8e6f34b48996fd5c319b5acd8";
    internal const string Url = "https://huggingface.co/handy-computer/Qwen3-ASR-0.6B-gguf/resolve/e4e16599b900eb0cb36e524514756bb92eb092b7/Qwen3-ASR-0.6B-Q8_0.gguf";

    internal static async Task DownloadAsync(HttpClient client, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken,
        string url = Url, long expectedSize = FileSize, string expectedHash = Sha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != expectedSize)
                throw new InvalidDataException("模型文件大小不符，请重试。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[131072];
                long received = 0;
                var lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > expectedSize) throw new InvalidDataException("模型文件大小不符，请重试。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    var percent = (int)(100 * received / expectedSize);
                    if (percent != lastPercent)
                    {
                        progress?.Report(percent);
                        lastPercent = percent;
                    }
                }
                if (received != expectedSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模型文件校验失败，请重新下载。");
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
