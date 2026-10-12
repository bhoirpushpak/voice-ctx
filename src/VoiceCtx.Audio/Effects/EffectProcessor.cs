using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Effects;

/// <summary>Effect selection shared between the UI and the audio thread (lock-free).</summary>
public sealed class EffectControls
{
    private volatile int _effect;
    private volatile bool _enabled = true;
    private volatile float _customSemitones;
    private volatile float _mix = 1f;

    public EffectKind Effect
    {
        get => (EffectKind)_effect;
        set => _effect = (int)value;
    }

    /// <summary>The On/Off switch. Off bypasses all effects (clean voice) with a crossfade.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public float CustomSemitones
    {
        get => _customSemitones;
        set => _customSemitones = Math.Clamp(value, -12f, 12f);
    }

    public float Mix
    {
        get => _mix;
        set => _mix = Math.Clamp(value, 0f, 1f);
    }
}

/// <summary>
/// Owns one pre-built instance of every effect and crossfades (equal-power, 20 ms) whenever the
/// requested effect changes, so switching never clicks and never allocates on the audio thread.
/// </summary>
public sealed class EffectProcessor
{
    private const double CrossfadeSeconds = 0.020;

    private readonly EffectControls _controls;
    private readonly IVoiceEffect[] _bank;
    private readonly PitchEffect _custom;
    private readonly float[] _from;
    private readonly float[] _to;
    private readonly int _fadeLength;
    private int _current = (int)EffectKind.None;
    private int _target = -1;
    private int _fadePosition;

    public EffectProcessor(int sampleRate, int maxBlockSize, EffectControls controls)
    {
        _controls = controls;
        var kinds = Enum.GetValues<EffectKind>();
        _bank = new IVoiceEffect[kinds.Length];
        foreach (var kind in kinds) _bank[(int)kind] = EffectFactory.Create(kind, sampleRate, maxBlockSize);
        _custom = (PitchEffect)_bank[(int)EffectKind.CustomPitch];
        _from = new float[maxBlockSize];
        _to = new float[maxBlockSize];
        _fadeLength = Math.Max(1, (int)(sampleRate * CrossfadeSeconds));
    }

    public EffectKind ActiveEffect => (EffectKind)_current;

    public void Process(Span<float> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, _from.Length)];
            ProcessChunk(chunk);
            buffer = buffer[chunk.Length..];
        }
    }

    private void ProcessChunk(Span<float> buffer)
    {
        int desired = _controls.Enabled ? (int)_controls.Effect : (int)EffectKind.None;
        if ((uint)desired >= (uint)_bank.Length) desired = (int)EffectKind.None;
        _custom.Semitones.Value = _controls.CustomSemitones;

        if (_target < 0 && desired != _current)
        {
            _target = desired;
            _fadePosition = 0;
            _bank[_target].Reset();
        }

        float mix = _controls.Mix;
        _bank[_current].Mix = mix;
        if (_target < 0)
        {
            _bank[_current].Process(buffer);
            return;
        }

        _bank[_target].Mix = mix;
        var from = _from.AsSpan(0, buffer.Length);
        var to = _to.AsSpan(0, buffer.Length);
        buffer.CopyTo(from);
        buffer.CopyTo(to);
        _bank[_current].Process(from);
        _bank[_target].Process(to);

        for (int i = 0; i < buffer.Length; i++)
        {
            float p = Math.Min(1f, (_fadePosition + i) / (float)_fadeLength);
            float angle = p * MathF.PI * 0.5f;
            buffer[i] = Math.Clamp(from[i] * MathF.Cos(angle) + to[i] * MathF.Sin(angle), -1f, 1f);
        }

        _fadePosition += buffer.Length;
        if (_fadePosition >= _fadeLength)
        {
            _current = _target;
            _target = -1;
        }
    }
}
