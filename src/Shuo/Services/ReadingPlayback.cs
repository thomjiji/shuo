using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Shuo.Services;

internal sealed class ReadingPlayback : IDisposable
{
    private readonly WasapiOut _output = new(AudioClientShareMode.Shared, 100);
    private readonly ReadingAudioBuffer _source = new(startupSilenceMilliseconds: 300, shutdownSilenceMilliseconds: 300);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private bool _disposed;
    internal bool Paused { get; private set; }
    internal bool Buffering => !Paused && (!_started || _source.Buffering);
    internal float Level => Paused ? 0 : _source.Level;

    internal ReadingPlayback()
    {
        _output.Init(_source);
        _output.PlaybackStopped += (_, args) =>
        {
            if (_disposed) return;
            if (args.Exception is { } error) _finished.TrySetException(error);
            else if (!_source.Drained) _finished.TrySetException(new IOException("播放设备提前停止。"));
            else _finished.TrySetResult();
        };
    }

    internal void TogglePause()
    {
        Paused = !Paused;
        if (Paused) _output.Pause();
        else if (_started) _output.Play();
    }

    internal async Task WriteAsync(byte[] audio, CancellationToken token)
    {
        for (var offset = 0; offset < audio.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (_finished.Task.IsCompleted) await _finished.Task;
            var count = Math.Min(8192, audio.Length - offset);
            if (!_source.TryWrite(audio.AsSpan(offset, count)))
            {
                await Task.Delay(20, token);
                continue;
            }
            offset += count;
            // A small prebuffer absorbs packet jitter without waiting for all the audio.
            if (!_started && _source.BufferedBytes >= 9600) Start();
        }
    }

    internal async Task CompleteAsync(CancellationToken token)
    {
        _source.Complete();
        if (!_started) Start();
        await _finished.Task.WaitAsync(token);
    }

    private void Start()
    {
        _started = true;
        if (!Paused) _output.Play();
    }

    public void Dispose()
    {
        _disposed = true;
        _output.Dispose();
    }
}
