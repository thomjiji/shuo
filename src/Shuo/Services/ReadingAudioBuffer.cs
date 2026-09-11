using System.Buffers.Binary;
using NAudio.Wave;

namespace Shuo.Services;

// One producer and one device reader. Network packet boundaries have no audio meaning.
internal sealed class ReadingAudioBuffer : IWaveProvider
{
    private readonly object _gate = new();
    private readonly byte[] _bytes = new byte[24000 * 2 * 10];
    private int _head, _count;
    private bool _complete;
    private float _level;
    private bool _buffering = true;
    private int _startupBytes;
    private int _shutdownBytes;
    internal ReadingAudioBuffer(int startupSilenceMilliseconds = 0, int shutdownSilenceMilliseconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startupSilenceMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(shutdownSilenceMilliseconds);
        _startupBytes = checked(startupSilenceMilliseconds * 48);
        _shutdownBytes = checked(shutdownSilenceMilliseconds * 48);
    }
    public WaveFormat WaveFormat { get; } = new(24000, 16, 1);
    internal int BufferedBytes { get { lock (_gate) return _count; } }
    internal bool Drained { get { lock (_gate) return _complete && _count == 0 && _startupBytes == 0 && _shutdownBytes == 0; } }
    internal bool Buffering { get { lock (_gate) return _buffering; } }
    internal float Level { get { lock (_gate) return _level; } }

    internal bool TryWrite(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_complete) throw new InvalidOperationException("Audio has already ended.");
            if (data.Length > _bytes.Length - _count) return false;
            var tail = (_head + _count) % _bytes.Length;
            var first = Math.Min(data.Length, _bytes.Length - tail);
            data[..first].CopyTo(_bytes.AsSpan(tail));
            data[first..].CopyTo(_bytes);
            _count += data.Length;
            return true;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if ((_count & 1) != 0) throw new IOException("语音音频格式不完整。");
            _complete = true;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            // Also clear the unused part of a short final read in the reused output buffer.
            Array.Clear(buffer, offset, count);
            // Warm up the output path before consuming any speech. This is emitted PCM,
            // not a wall-clock delay while the audio device remains stopped.
            var silence = Math.Min(count & ~1, _startupBytes);
            _startupBytes -= silence;
            offset += silence;
            count -= silence;
            // Preserve a trailing half-sample until its next network byte arrives.
            var available = Math.Min(count & ~1, _count & ~1);
            var first = Math.Min(available, _bytes.Length - _head);
            _bytes.AsSpan(_head, first).CopyTo(buffer.AsSpan(offset));
            _bytes.AsSpan(0, available - first).CopyTo(buffer.AsSpan(offset + first));
            _head = (_head + available) % _bytes.Length;
            _count -= available;
            var peak = 0;
            for (var index = offset; index < offset + available; index += 2)
                peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2))));
            _level = peak / 32768f;
            _buffering = available == 0 && (silence > 0 || !_complete);
            if (_complete)
            {
                // Keep the device running on silence after the final speech sample so
                // downstream output buffers end with silence before WASAPI stops/resets.
                var ending = _count == 0 && _startupBytes == 0
                    ? Math.Min((count - available) & ~1, _shutdownBytes) : 0;
                _shutdownBytes -= ending;
                return silence + available + ending;
            }
            // An underrun is silence, never EOF; later packets keep the same device alive.
            return silence + count;
        }
    }
}
