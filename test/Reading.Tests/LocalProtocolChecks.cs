using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Shuo.Services;

internal static class LocalProtocolChecks
{
    private const string Ready = "{\"type\":\"ready\",\"protocol\":1,\"sample_rate\":24000,\"format\":\"pcm_s16le\",\"voice\":\"Serena\"}";
    internal static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            checks++;
            Console.WriteLine("ok " + name);
        }
        var endpoint = SelfHostedReadingClient.Endpoint("100.64.1.2");
        Check(endpoint.ToString() == "ws://100.64.1.2:18766/v1/reading", "local host uses the reading port");
        Check(SelfHostedReadingClient.Endpoint("https://example.com").Scheme == "wss", "HTTPS reading endpoint uses secure WebSocket");
        foreach (var host in new[] { "", "http://user:password@example.com", "http://example.com/path", "http://example.com?x=1" })
        {
            try { SelfHostedReadingClient.Endpoint(host); throw new Exception("Invalid local host was accepted"); }
            catch (ArgumentException) { Check(true, "invalid local endpoint rejected"); }
        }
        var old = JsonSerializer.Deserialize<ReadingOptions>("{}")!;
        Check(!old.UseSelfHostedTranslation && old.LocalPlaybackSpeed == 1, "old settings preserve cloud backend and natural local speed");
        var settings = old with { UseSelfHostedTranslation = true, SelfHostedHost = "100.64.1.2", LocalPlaybackSpeed = 1.3 };
        Check(JsonSerializer.Deserialize<ReadingOptions>(JsonSerializer.Serialize(settings)) == settings, "local backend host and speed survive settings roundtrip");
        using (var socket = new FakeSocket())
        {
            socket.Text(Ready);
            var text = Encoding.UTF8.GetBytes("{\"type\":\"text\",\"text\":\"你好。\"}");
            socket.Add(WebSocketMessageType.Text, text[..25], false);
            socket.Add(WebSocketMessageType.Text, text[25..], true);
            socket.Add(WebSocketMessageType.Binary, [1, 2, 3, 4], true);
            socket.Text("{\"type\":\"done\"}");
            var translated = "";
            await using var stream = SelfHostedReadingClient.ReadEventsAsync(socket, part => translated += part, default).GetAsyncEnumerator();
            Check(await stream.MoveNextAsync() && stream.Current.SequenceEqual(new byte[] { 1, 2, 3, 4 }) && translated == "你好。", "local fragmented text and PCM are delivered without loss");
            Check(socket.Acknowledged == 0, "local audio is not acknowledged before the player accepts it");
            Check(!await stream.MoveNextAsync() && socket.Acknowledged == 1, "local audio acknowledgment follows consumption and done completes");
        }
        foreach (var scenario in new[] { "truncated", "odd", "missing-text", "wrong-format", "busy" })
        {
            using var socket = new FakeSocket();
            socket.Text(scenario == "wrong-format" ? Ready.Replace("24000", "48000") : Ready);
            if (scenario != "missing-text") socket.Text("{\"type\":\"text\",\"text\":\"你好。\"}");
            socket.Add(WebSocketMessageType.Binary, scenario == "odd" ? [1] : [1, 2], true);
            if (scenario != "truncated") socket.Text(scenario == "busy" ? "{\"type\":\"error\",\"message\":\"busy\"}" : "{\"type\":\"done\"}");
            try
            {
                await foreach (var _ in SelfHostedReadingClient.ReadEventsAsync(socket, _ => { }, default)) { }
                throw new Exception("Broken local stream was accepted: " + scenario);
            }
            catch (IOException) { Check(true, "local stream rejects " + scenario); }
        }
        return checks;
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Data, bool End)> _messages = new();
        internal int Acknowledged;
        internal void Text(string text) => Add(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text), true);
        internal void Add(WebSocketMessageType type, byte[] bytes, bool end) => _messages.Enqueue((type, bytes, end));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_messages.TryDequeue(out var message)) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            message.Data.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(message.Data.Length, message.Type, message.End));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Encoding.UTF8.GetString(buffer) != "{\"type\":\"ack\"}") throw new Exception("Unexpected local control");
            Acknowledged++;
            return Task.CompletedTask;
        }
    }
}
