using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Shuo.Services;

internal static class ModelDownloadTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "shuo-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "model.gguf");
        var bytes = "GGUF test data"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            foreach (var scenario in new[] { "ok", "bad hash", "truncated", "http error", "cancelled" })
            {
                File.WriteAllText(destination, "existing model");
                using var cancellation = new CancellationTokenSource();
                using var client = new HttpClient(new ResponseHandler(() =>
                {
                    if (scenario == "cancelled") cancellation.Cancel();
                    var content = new ByteArrayContent(scenario == "truncated" ? bytes[..4] : bytes);
                    if (scenario == "truncated") content.Headers.ContentLength = null;
                    return new HttpResponseMessage(scenario == "http error" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content };
                }));
                var failed = false;
                try
                {
                    await LocalModelDownload.DownloadAsync(client, destination, null, cancellation.Token,
                        "https://test.invalid/model", bytes.Length, scenario == "bad hash" ? new string('0', 64) : hash);
                }
                catch (Exception error) when (error is InvalidDataException or HttpRequestException or OperationCanceledException) { failed = true; }
                if (failed != (scenario != "ok")) throw new Exception("Unexpected download result: " + scenario);
                var expected = scenario == "ok" ? System.Text.Encoding.UTF8.GetString(bytes) : "existing model";
                if (File.ReadAllText(destination) != expected) throw new Exception("Existing model was damaged: " + scenario);
                if (Directory.GetFiles(root).Length != 1) throw new Exception("Partial download was not cleaned up: " + scenario);
            }
            Console.WriteLine("Passed 5 model download cases (success, checksum, truncation, HTTP error, cancellation).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }
}
