using System.Diagnostics;
using Shuo.Services;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("ok " + message);
}

var buffer = new ReadingAudioBuffer();
var output = new byte[4];
Check(buffer.Read(output, 0, 4) == 4 && output.All(x => x == 0), "network underrun is silence, not EOF");
Check(buffer.TryWrite(new byte[] { 1 }), "first partial sample accepted");
buffer.Read(output, 0, 4);
Check(buffer.BufferedBytes == 1, "partial PCM sample retained across reads");
buffer.TryWrite(new byte[] { 2, 3, 4 });
buffer.Read(output, 0, 4);
Check(output.SequenceEqual(new byte[] {1,2,3,4}), "packet boundaries do not drop leading audio bytes");
Check(buffer.Level > 0, "indicator level comes from consumed PCM");
buffer.Read(output, 0, 4);
Check(buffer.Level == 0 && buffer.Buffering, "network silence clears audio level");
buffer.TryWrite(new byte[] { 5, 6 });
buffer.Complete();
Check(buffer.Read(output, 0, 4) == 2 && output[0] == 5 && output[1] == 6, "final PCM tail retained");
Check(buffer.Read(output, 0, 4) == 0, "EOF appears only after explicit completion");

var wrap = new ReadingAudioBuffer();
var expected = Enumerable.Range(0, 480000).Select(x => (byte)(x % 251)).ToArray();
Check(wrap.TryWrite(expected) && !wrap.TryWrite(new byte[2]), "bounded audio buffer applies backpressure");
var head = new byte[300000];
wrap.Read(head, 0, head.Length);
Check(wrap.TryWrite(expected.AsSpan(0, head.Length)), "released capacity can be reused");
var tail = new byte[480000];
wrap.Read(tail, 0, tail.Length);
Check(tail.SequenceEqual(expected.Skip(300000).Concat(expected.Take(300000))), "ring wrap preserves audio order exactly");

// A completed short utterance must retain its very first sample after device warmup.
var startup = new ReadingAudioBuffer(startupSilenceMilliseconds: 300);
var firstSpeech = new byte[] { 255, 127, 0, 128, 42, 11 };
startup.TryWrite(firstSpeech.AsSpan(0, 1));
startup.TryWrite(firstSpeech.AsSpan(1));
startup.Complete();
var initial = new byte[10000];
Check(startup.Read(initial, 0, initial.Length) == initial.Length && initial.All(x => x == 0)
    && startup.BufferedBytes == firstSpeech.Length && !startup.Drained && startup.Level == 0,
    "device warmup emits silence without consuming the first speech sample");
var boundary = Enumerable.Repeat((byte)99, 5000).ToArray();
Check(startup.Read(boundary, 2, 4800) == 4400 + firstSpeech.Length
    && boundary.Take(2).All(x => x == 99)
    && boundary.Skip(2).Take(4400).All(x => x == 0)
    && boundary.Skip(4402).Take(firstSpeech.Length).SequenceEqual(firstSpeech)
    && startup.Drained && startup.Level > 0,
    "warmup boundary preserves every speech byte including the first and last sample");
Check(startup.Read(boundary, 0, boundary.Length) == 0, "startup silence occurs only once per playback");

var ending = new ReadingAudioBuffer(shutdownSilenceMilliseconds: 300);
ending.TryWrite(firstSpeech);
ending.Complete();
var reused = Enumerable.Repeat((byte)99, 10000).ToArray();
Check(ending.Read(reused, 0, reused.Length) == reused.Length
    && reused.Take(firstSpeech.Length).SequenceEqual(firstSpeech)
    && reused.Skip(firstSpeech.Length).All(x => x == 0) && !ending.Drained,
    "completion plays the full speech tail followed by silence before reporting drained");
Array.Fill(reused, (byte)99);
Check(ending.Read(reused, 2, 9000) == 4406 && ending.Drained && ending.Level == 0
    && reused.Take(2).All(x => x == 99) && reused.Skip(2).Take(9000).All(x => x == 0)
    && reused.Skip(9002).All(x => x == 99),
    "short final read clears reused output memory and finishes exactly 300 ms of silence");
Check(ending.Read(reused, 0, reused.Length) == 0 && reused.All(x => x == 0),
    "EOF cannot expose a previous audio fragment in the output buffer");
var next = new ReadingAudioBuffer(startupSilenceMilliseconds: 300, shutdownSilenceMilliseconds: 300);
next.TryWrite(new byte[] { 7, 8 });
next.Complete();
var consecutive = new byte[30000];
Check(next.Read(consecutive, 0, consecutive.Length) == 28802
    && consecutive.Take(14400).All(x => x == 0)
    && consecutive[14400] == 7 && consecutive[14401] == 8
    && consecutive.Skip(14402).All(x => x == 0),
    "next utterance contains only its own speech between startup and shutdown silence");

// Silent PCM exercises the real Windows output device without speaking user text.
using (var playback = new ReadingPlayback())
{
    var elapsed = Stopwatch.StartNew();
    await playback.WriteAsync(new byte[24000 * 2 * 2], default);
    var playing = playback.CompleteAsync(default);
    await Task.Delay(300);
    Check(!playing.IsCompleted, "playback waits for the audio device");
    playback.TogglePause();
    await Task.Delay(900);
    Check(!playing.IsCompleted && playback.Paused && playback.Level == 0, "pause holds playback and silences indicator");
    playback.TogglePause();
    await playing.WaitAsync(TimeSpan.FromSeconds(5));
    Check(elapsed.Elapsed >= TimeSpan.FromSeconds(2.5), "completion includes paused duration");
}
using (var playback = new ReadingPlayback())
using (var cancellation = new CancellationTokenSource(150))
{
    await playback.WriteAsync(new byte[24000 * 2 * 5], default);
    try
    {
        await playback.CompleteAsync(cancellation.Token);
        throw new Exception("Cancelled playback reported completion.");
    }
    catch (OperationCanceledException) { Console.WriteLine("ok cancellation interrupts playback wait"); }
}

for (var iteration = 0; iteration < 3; iteration++)
{
    using var playback = new ReadingPlayback();
    var elapsed = Stopwatch.StartNew();
    await playback.WriteAsync(new byte[480], default);
    await playback.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
    Check(elapsed.Elapsed >= TimeSpan.FromMilliseconds(550),
        $"successive playback {iteration + 1} waits for startup and shutdown audio to drain");
}
