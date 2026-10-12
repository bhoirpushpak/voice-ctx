using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Effects;

/// <summary>Bypass. Leaves samples bit-identical (no mix stage).</summary>
public sealed class NoneEffect : IVoiceEffect
{
    public string Name => "None";
    public float Mix { get; set; } = 1f;
    public IReadOnlyList<EffectParameter> Parameters => [];
    public void Process(Span<float> buffer) { }
    public void Reset() { }
}

public sealed class SilenceEffect(int sampleRate, int maxBlockSize) : EffectBase(sampleRate, maxBlockSize)
{
    public override string Name => "Silence";
    protected override void ProcessWet(Span<float> buffer) => buffer.Clear();
}

/// <summary>Fixed or user-set pitch shift (male, female, baby, helium, custom).</summary>
public sealed class PitchEffect : EffectBase
{
    private readonly PitchShifter _shifter;
    private readonly EffectParameter _semitones;

    public PitchEffect(string name, int sampleRate, int maxBlockSize, float semitones, bool adjustable = false)
        : base(sampleRate, maxBlockSize)
    {
        Name = name;
        _shifter = new PitchShifter(sampleRate);
        _semitones = new EffectParameter("Semitones", -12, 12, semitones) { Value = semitones };
        Parameters = adjustable ? [_semitones] : [];
    }

    public override string Name { get; }

    public override IReadOnlyList<EffectParameter> Parameters { get; }

    public EffectParameter Semitones => _semitones;

    protected override void ProcessWet(Span<float> buffer)
    {
        _shifter.Semitones = _semitones.Value;
        _shifter.Process(buffer);
    }

    protected override void OnReset() => _shifter.Reset();
}

/// <summary>Pitch that drifts back and forth across a wide range at a fixed speed.</summary>
public sealed class MutationEffect(string name, int sampleRate, int maxBlockSize, float semitonesPerSecond)
    : EffectBase(sampleRate, maxBlockSize)
{
    private const float Low = -4f, High = 13f;
    private readonly PitchShifter _shifter = new(sampleRate);
    private float _semitones;
    private float _direction = 1f;

    public override string Name { get; } = name;

    protected override void ProcessWet(Span<float> buffer)
    {
        _semitones += _direction * semitonesPerSecond * buffer.Length / SampleRate;
        if (_semitones > High) { _semitones = High; _direction = -1f; }
        else if (_semitones < Low) { _semitones = Low; _direction = 1f; }
        _shifter.Semitones = _semitones;
        _shifter.Process(buffer);
    }

    protected override void OnReset()
    {
        _shifter.Reset();
        _semitones = 0;
        _direction = 1f;
    }
}

/// <summary>Metallic comb resonance followed by a low-frequency ring modulator.</summary>
public sealed class RobotEffect : EffectBase
{
    private const float Feedback = 0.55f;
    private readonly float[] _comb;
    private readonly double _carrierStep;
    private int _combIndex;
    private double _carrierPhase;

    public RobotEffect(int sampleRate, int maxBlockSize) : base(sampleRate, maxBlockSize)
    {
        _comb = new float[Math.Max(1, sampleRate / 160)];
        _carrierStep = 2 * Math.PI * 50 / sampleRate;
    }

    public override string Name => "Robot";

    protected override void ProcessWet(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            float y = buffer[i] + Feedback * _comb[_combIndex];
            if (Math.Abs(y) < 1e-15f) y = 0;
            _comb[_combIndex] = y;
            if (++_combIndex == _comb.Length) _combIndex = 0;

            float carrier = (float)Math.Sin(_carrierPhase);
            _carrierPhase += _carrierStep;
            if (_carrierPhase > 2 * Math.PI) _carrierPhase -= 2 * Math.PI;

            buffer[i] = MathF.Tanh(1.5f * (1 - Feedback) * y * carrier) * 1.1f;
        }
    }

    protected override void OnReset()
    {
        Array.Clear(_comb);
        _combIndex = 0;
        _carrierPhase = 0;
    }
}

/// <summary>Raised pitch with fast vibrato plus a touch of high ring modulation.</summary>
public sealed class AlienEffect(int sampleRate, int maxBlockSize) : EffectBase(sampleRate, maxBlockSize)
{
    private readonly PitchShifter _shifter = new(sampleRate);
    private readonly double _carrierStep = 2 * Math.PI * 900 / sampleRate;
    private double _lfoPhase;
    private double _carrierPhase;

    public override string Name => "Alien";

    protected override void ProcessWet(Span<float> buffer)
    {
        _lfoPhase += 2 * Math.PI * 7 * buffer.Length / SampleRate;
        if (_lfoPhase > 2 * Math.PI) _lfoPhase -= 2 * Math.PI;
        _shifter.Semitones = 3 + 0.8 * Math.Sin(_lfoPhase);
        _shifter.Process(buffer);

        for (int i = 0; i < buffer.Length; i++)
        {
            float carrier = (float)Math.Sin(_carrierPhase);
            _carrierPhase += _carrierStep;
            if (_carrierPhase > 2 * Math.PI) _carrierPhase -= 2 * Math.PI;
            buffer[i] = buffer[i] * (0.7f + 0.3f * carrier);
        }
    }

    protected override void OnReset()
    {
        _shifter.Reset();
        _lfoPhase = 0;
        _carrierPhase = 0;
    }
}

/// <summary>8-bit console style: sample-and-hold to a low rate and 4-bit quantisation.</summary>
public sealed class AtariEffect(int sampleRate, int maxBlockSize) : EffectBase(sampleRate, maxBlockSize)
{
    private const float Levels = 7.5f;
    private readonly double _holdStep = 6000.0 / sampleRate;
    private double _holdPhase = 1;
    private float _held;

    public override string Name => "Atari";

    protected override void ProcessWet(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            _holdPhase += _holdStep;
            if (_holdPhase >= 1)
            {
                _holdPhase -= 1;
                _held = MathF.Round(Math.Clamp(buffer[i] * 1.5f, -1f, 1f) * Levels) / Levels;
            }
            buffer[i] = _held;
        }
    }

    protected override void OnReset()
    {
        _holdPhase = 1;
        _held = 0;
    }
}

/// <summary>Your voice plus a delayed, octave-up copy of itself.</summary>
public sealed class CloneEffect : EffectBase
{
    private readonly PitchShifter _shifter;
    private readonly float[] _copy;
    private readonly float[] _delay;
    private int _delayIndex;

    public CloneEffect(int sampleRate, int maxBlockSize) : base(sampleRate, maxBlockSize)
    {
        _shifter = new PitchShifter(sampleRate) { Semitones = 12 };
        _copy = new float[Math.Max(1, maxBlockSize)];
        _delay = new float[Math.Max(1, sampleRate * 60 / 1000)];
    }

    public override string Name => "Clone";

    protected override void ProcessWet(Span<float> buffer)
    {
        var copy = _copy.AsSpan(0, buffer.Length);
        buffer.CopyTo(copy);
        _shifter.Process(copy);
        for (int i = 0; i < buffer.Length; i++)
        {
            float delayed = _delay[_delayIndex];
            _delay[_delayIndex] = copy[i];
            if (++_delayIndex == _delay.Length) _delayIndex = 0;
            buffer[i] = 0.6f * buffer[i] + 0.5f * delayed;
        }
    }

    protected override void OnReset()
    {
        _shifter.Reset();
        Array.Clear(_delay);
        _delayIndex = 0;
    }
}

/// <summary>Telephone/AM-radio band (300 Hz – 3.4 kHz) with gentle saturation.</summary>
public sealed class RadioEffect(int sampleRate, int maxBlockSize) : EffectBase(sampleRate, maxBlockSize)
{
    private const float Drive = 2.5f;
    private static readonly float DriveNorm = 0.9f / MathF.Tanh(Drive);
    private readonly Biquad _highPass1 = Biquad.HighPass(sampleRate, 300);
    private readonly Biquad _highPass2 = Biquad.HighPass(sampleRate, 300);
    private readonly Biquad _lowPass1 = Biquad.LowPass(sampleRate, 3400);
    private readonly Biquad _lowPass2 = Biquad.LowPass(sampleRate, 3400);

    public override string Name => "Radio";

    protected override void ProcessWet(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            float x = _lowPass2.Process(_lowPass1.Process(_highPass2.Process(_highPass1.Process(buffer[i]))));
            buffer[i] = MathF.Tanh(Drive * x) * DriveNorm;
        }
    }

    protected override void OnReset()
    {
        _highPass1.Reset();
        _highPass2.Reset();
        _lowPass1.Reset();
        _lowPass2.Reset();
    }
}
