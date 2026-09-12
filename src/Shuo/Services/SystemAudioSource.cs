using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Shuo.Services;

internal static class SystemAudioSource
{
    internal static async IAsyncEnumerable<byte[]> ReadAsync(Action<double> level,
        [EnumeratorCancellation] CancellationToken cancellationToken, Func<bool>? paused = null)
    {
        using var capture = new WasapiLoopbackCapture();
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
        Exception? failure = null;
        var stopping = false;
        capture.DataAvailable += (_, args) =>
        {
            try { buffer.AddSamples(args.Buffer, 0, args.BytesRecorded); }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
        };
        capture.RecordingStopped += (_, args) =>
        {
            if (!stopping)
                Interlocked.CompareExchange(ref failure,
                    args.Exception ?? new IOException("系统音频采集已停止，请检查播放设备。"), null);
        };
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1) samples = new MonoMixer(samples);
        samples = new WdlResamplingSampleProvider(samples, 16000);
        var floats = new float[1024];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(64));
        capture.StartRecording();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Volatile.Read(ref failure) is { } error)
                    throw new IOException("无法继续采集系统音频。", error);
                var count = samples.Read(floats, 0, floats.Length);
                // Consume and discard paused audio. Silence keeps VAD and connection timeouts healthy.
                if (paused?.Invoke() == true) Array.Clear(floats, 0, count);
                var pcm = new byte[count * 2];
                double energy = 0;
                for (var i = 0; i < count; i++)
                {
                    var value = float.IsFinite(floats[i]) ? Math.Clamp(floats[i], -1, 1) : 0;
                    var sample = (short)Math.Clamp((int)Math.Round(value * 32768), short.MinValue, short.MaxValue);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
                    energy += value * value;
                }
                level(LevelFromRms(count == 0 ? 0 : Math.Sqrt(energy / count)));
                if (pcm.Length > 0) yield return pcm;
            }
        }
        finally
        {
            stopping = true;
            capture.StopRecording();
        }
    }

    // Match the microphone worker's -55 dB floor and -15 dB ceiling.
    internal static double LevelFromRms(double rms) =>
        double.IsFinite(rms) && rms > 0 ? Math.Clamp((20 * Math.Log10(rms) + 55) / 40, 0, 1) : 0;

    private sealed class MonoMixer(ISampleProvider source) : ISampleProvider
    {
        private float[] _input = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            if (_input.Length < count * channels) _input = new float[count * channels];
            var read = source.Read(_input, 0, count * channels) / channels;
            for (var i = 0; i < read; i++)
            {
                float total = 0;
                for (var channel = 0; channel < channels; channel++) total += _input[i * channels + channel];
                buffer[offset + i] = total / channels;
            }
            return read;
        }
    }
}
