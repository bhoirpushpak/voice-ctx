using System.Numerics;

namespace VoiceCtx.Audio.Dsp;

/// <summary>
/// Real-time pitch shifter (duration preserving): granular overlap-add with Hann windows at 50%
/// overlap, where each new grain's read position is aligned to the previous grain by
/// cross-correlation (WSOLA). Grains read the input at the pitch ratio, so pitch changes while the
/// grain clock keeps real time. Constant latency; fixed allocation.
/// </summary>
public sealed class PitchShifter
{
    public const double MinSemitones = -12;
    public const double MaxSemitones = 13;
    private static readonly double MaxRatio = Math.Pow(2, MaxSemitones / 12);

    private readonly float[] _buffer;  // mirrored: every sample is stored at i and i + _size
    private readonly int _size;
    private readonly long _mask;
    private readonly float[] _window;
    private readonly int _grainLength;
    private readonly int _hop;
    private readonly int _searchRadius;
    private readonly int _latency;

    private long _written;
    private int _phase;
    private double _ratio = 1;
    private Grain _current;
    private Grain _previous;

    private struct Grain
    {
        public double Position;
        public double Rate;
        public int Index;
        public bool Active;
    }

    public PitchShifter(int sampleRate)
    {
        _grainLength = Math.Max(64, (int)(sampleRate * 0.021) & ~1);
        _hop = _grainLength / 2;
        _searchRadius = Math.Max(8, (int)(sampleRate * 0.005));
        _latency = _searchRadius + (int)Math.Ceiling(Math.Max(_hop * MaxRatio, _grainLength * (MaxRatio - 1))) + 4;

        _size = (int)BitOperations.RoundUpToPowerOf2((uint)(_latency + _grainLength * 3 + _searchRadius * 2 + 8));
        _mask = _size - 1;
        _buffer = new float[_size * 2];

        _window = new float[_grainLength];
        for (int n = 0; n < _grainLength; n++)
            _window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / _grainLength));
    }

    public int LatencySamples => _latency;

    public double Semitones
    {
        set => _ratio = Math.Pow(2, Math.Clamp(value, MinSemitones, MaxSemitones) / 12);
    }

    public void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int w = (int)(_written & _mask);
            _buffer[w] = samples[i];
            _buffer[w + _size] = samples[i];
            _written++;

            if (_phase == 0) StartGrain();
            if (++_phase == _hop) _phase = 0;

            samples[i] = Advance(ref _current) + Advance(ref _previous);
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _written = 0;
        _phase = 0;
        _current = default;
        _previous = default;
    }

    private float Advance(ref Grain grain)
    {
        if (!grain.Active) return 0f;
        float value = _window[grain.Index] * Read(grain.Position);
        grain.Position += grain.Rate;
        if (++grain.Index >= _grainLength) grain.Active = false;
        return value;
    }

    private void StartGrain()
    {
        long ideal = _written - 1 - _latency;
        long start = ideal;
        if (_current.Active)
        {
            // The running grain's read head is where a seamless continuation would start.
            long continuation = (long)Math.Floor(_current.Position);
            start = BestAlignment(ideal, continuation);
        }

        _previous = _current;
        _current = new Grain { Position = start, Rate = _ratio, Index = 0, Active = true };
    }

    private long BestAlignment(long ideal, long continuation)
    {
        var reference = Segment(continuation, _hop);
        long best = ideal;
        float bestScore = Dot(Segment(ideal, _hop), reference);
        for (long candidate = ideal - _searchRadius; candidate <= ideal + _searchRadius; candidate++)
        {
            float score = Dot(Segment(candidate, _hop), reference);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private ReadOnlySpan<float> Segment(long start, int length) => _buffer.AsSpan((int)(start & _mask), length);

    private float Read(double position)
    {
        long i = (long)Math.Floor(position);
        float t = (float)(position - i);
        int k = (int)((i - 1) & _mask);
        float y0 = _buffer[k], y1 = _buffer[k + 1], y2 = _buffer[k + 2], y3 = _buffer[k + 3];
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * t + c2) * t + c1) * t + y1;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        var acc = Vector<float>.Zero;
        int width = Vector<float>.Count;
        for (; i <= a.Length - width; i += width)
            acc += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
