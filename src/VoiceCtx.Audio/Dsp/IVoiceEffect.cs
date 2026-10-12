namespace VoiceCtx.Audio.Dsp;

/// <summary>A real-time mono effect. <see cref="Process"/> and <see cref="Reset"/> run on the audio thread.</summary>
public interface IVoiceEffect
{
    string Name { get; }

    /// <summary>Dry/wet balance, 0 = dry, 1 = fully processed. Changes are smoothed.</summary>
    float Mix { get; set; }

    IReadOnlyList<EffectParameter> Parameters { get; }

    /// <summary>Processes samples in place. Must not allocate or lock.</summary>
    void Process(Span<float> buffer);

    /// <summary>Clears all internal state (delay lines, filters, oscillators).</summary>
    void Reset();
}

/// <summary>A user-adjustable value read by the audio thread once per block.</summary>
public sealed class EffectParameter(string name, float min, float max, float defaultValue)
{
    private volatile float _value = defaultValue;

    public string Name { get; } = name;
    public float Min { get; } = min;
    public float Max { get; } = max;
    public float Default { get; } = defaultValue;

    public float Value
    {
        get => _value;
        set => _value = Math.Clamp(value, Min, Max);
    }
}

/// <summary>Shared plumbing: block chunking, smoothed dry/wet mix and output safety clamp.</summary>
public abstract class EffectBase : IVoiceEffect
{
    private readonly float[] _dry;
    private volatile float _mix = 1f;
    private float _appliedMix = 1f;

    protected EffectBase(int sampleRate, int maxBlockSize)
    {
        SampleRate = sampleRate;
        _dry = new float[Math.Max(1, maxBlockSize)];
    }

    public abstract string Name { get; }

    public int SampleRate { get; }

    public float Mix
    {
        get => _mix;
        set => _mix = Math.Clamp(value, 0f, 1f);
    }

    public virtual IReadOnlyList<EffectParameter> Parameters => [];

    public void Process(Span<float> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, _dry.Length)];
            ProcessChunk(chunk);
            buffer = buffer[chunk.Length..];
        }
    }

    public void Reset()
    {
        _appliedMix = _mix;
        OnReset();
    }

    /// <summary>Replaces the buffer contents with the fully processed (100% wet) signal.</summary>
    protected abstract void ProcessWet(Span<float> buffer);

    protected virtual void OnReset() { }

    private void ProcessChunk(Span<float> buffer)
    {
        var dry = _dry.AsSpan(0, buffer.Length);
        buffer.CopyTo(dry);
        ProcessWet(buffer);

        float from = _appliedMix, to = _mix;
        float step = (to - from) / buffer.Length;
        for (int i = 0; i < buffer.Length; i++)
        {
            float m = from + step * (i + 1);
            buffer[i] = SafeClamp(dry[i] + m * (buffer[i] - dry[i]));
        }
        _appliedMix = to;
    }

    protected static float SafeClamp(float v)
    {
        if (v > 1f) return 1f;
        if (v < -1f) return -1f;
        return float.IsNaN(v) ? 0f : v;
    }
}
