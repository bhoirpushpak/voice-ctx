using System.Numerics;

namespace VoiceCtx.Audio.Engine;

/// <summary>Lock-free single-producer / single-consumer float FIFO.</summary>
internal sealed class SpscRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _writePos;
    private long _readPos;

    public SpscRingBuffer(int minimumCapacity)
    {
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, minimumCapacity));
        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    public int Count => (int)(Volatile.Read(ref _writePos) - Volatile.Read(ref _readPos));

    /// <summary>Producer side. Returns the number of samples written; excess is dropped when full.</summary>
    public int Write(ReadOnlySpan<float> data)
    {
        long write = _writePos;
        int free = _buffer.Length - (int)(write - Volatile.Read(ref _readPos));
        int count = Math.Min(free, data.Length);
        int start = (int)(write & _mask);
        int first = Math.Min(count, _buffer.Length - start);
        data[..first].CopyTo(_buffer.AsSpan(start));
        data.Slice(first, count - first).CopyTo(_buffer);
        Volatile.Write(ref _writePos, write + count);
        return count;
    }

    /// <summary>Consumer side. Returns the number of samples read.</summary>
    public int Read(Span<float> destination)
    {
        long read = _readPos;
        int available = (int)(Volatile.Read(ref _writePos) - read);
        int count = Math.Min(available, destination.Length);
        int start = (int)(read & _mask);
        int first = Math.Min(count, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        Volatile.Write(ref _readPos, read + count);
        return count;
    }

    /// <summary>Consumer side. Discards up to <paramref name="count"/> samples.</summary>
    public int Skip(int count)
    {
        long read = _readPos;
        int available = (int)(Volatile.Read(ref _writePos) - read);
        int skipped = Math.Min(available, count);
        Volatile.Write(ref _readPos, read + skipped);
        return skipped;
    }
}
