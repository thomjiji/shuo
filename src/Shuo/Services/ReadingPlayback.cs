using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Shuo.Services;

internal sealed class ReadingPlayback : IDisposable
{
    private readonly BufferedWaveProvider _buffer = new(new WaveFormat(24000, 16, 1))
    {
        BufferDuration = TimeSpan.FromSeconds(30), ReadFully = true, DiscardOnBufferOverflow = false,
    };
    private readonly WasapiOut _output = new(AudioClientShareMode.Shared, 100);
    private Exception? _failure;
    private bool _started;
    private bool _disposed;
    internal bool Paused { get; private set; }

    internal ReadingPlayback()
    {
        _output.Init(_buffer);
        _output.PlaybackStopped += (_, args) =>
        {
            if (!_disposed) Interlocked.CompareExchange(ref _failure,
                args.Exception ?? new IOException("播放设备已停止。"), null);
        };
    }

    internal void TogglePause()
    {
        Paused = !Paused;
        if (Paused) _output.Pause();
        else if (_started) _output.Play();
    }

    internal async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _failure) is { } error) throw error;
            var count = Math.Min(8192, bytes.Length - offset);
            if (Paused || _buffer.BufferLength - _buffer.BufferedBytes < count)
            {
                await Task.Delay(30, token);
                continue;
            }
            _buffer.AddSamples(bytes, offset, count);
            offset += count;
            if (!_started) { _started = true; _output.Play(); }
        }
    }

    internal async Task DrainAsync(CancellationToken token)
    {
        while (Paused || _buffer.BufferedBytes > 0)
        {
            if (Volatile.Read(ref _failure) is { } error) throw error;
            await Task.Delay(30, token);
        }
        // Allow the final WASAPI device buffer to play before disposing the output.
        await Task.Delay(200, token);
    }

    public void Dispose()
    {
        _disposed = true;
        _output.Dispose();
        _buffer.ClearBuffer();
    }
}
