namespace VoiceCtx.Audio.Dsp;

/// <summary>One-pole attack/release envelope follower on the absolute signal.</summary>
internal struct EnvelopeFollower
{
    private float _attack, _release, _envelope;

    public void Configure(int sampleRate, double attackMs, double releaseMs)
    {
        _attack = Coefficient(sampleRate, attackMs);
        _release = Coefficient(sampleRate, releaseMs);
    }

    public float Value => _envelope;

    public float Next(float x)
    {
        float level = Math.Abs(x);
        float coeff = level > _envelope ? _attack : _release;
        _envelope = level + coeff * (_envelope - level);
        if (_envelope < 1e-12f) _envelope = 0;
        return _envelope;
    }

    public void Reset() => _envelope = 0;

    private static float Coefficient(int sampleRate, double ms) =>
        ms <= 0 ? 0f : (float)Math.Exp(-1.0 / (sampleRate * ms / 1000.0));
}

/// <summary>Feed-forward peak compressor with soft knee and make-up gain.</summary>
public sealed class Compressor
{
    private const float KneeDb = 6f;
    private EnvelopeFollower _detector;
    private float _gainSmooth = 1f;
    private float _gainCoeff;

    public Compressor(int sampleRate, double attackMs = 5, double releaseMs = 120)
    {
        _detector.Configure(sampleRate, attackMs, releaseMs);
        _gainCoeff = (float)Math.Exp(-1.0 / (sampleRate * 0.002));
    }

    public float ThresholdDb { get; set; } = -20f;
    public float Ratio { get; set; } = 3f;
    public float MakeupDb { get; set; }

    /// <summary>Most recent gain reduction in dB (≤ 0), for metering and tests.</summary>
    public float GainReductionDb { get; private set; }

    public float Process(float x)
    {
        float env = _detector.Next(x);
        float levelDb = env > 1e-9f ? 20f * MathF.Log10(env) : -180f;
        float over = levelDb - ThresholdDb;
        float reductionDb;
        if (over <= -KneeDb / 2) reductionDb = 0;
        else if (over >= KneeDb / 2) reductionDb = over * (1f / Ratio - 1f);
        else
        {
            float k = over + KneeDb / 2;
            reductionDb = (1f / Ratio - 1f) * k * k / (2f * KneeDb);
        }
        GainReductionDb = reductionDb;
        float target = MathF.Pow(10f, (reductionDb + MakeupDb) / 20f);
        _gainSmooth = target + _gainCoeff * (_gainSmooth - target);
        return x * _gainSmooth;
    }

    public void Reset()
    {
        _detector.Reset();
        _gainSmooth = MathF.Pow(10f, MakeupDb / 20f);
        GainReductionDb = 0;
    }
}

/// <summary>
/// Downward expander/gate with hysteresis and hold: attenuates (rather than hard-mutes) signal that
/// stays below the threshold, so breaths and room tone drop away between phrases.
/// </summary>
public sealed class NoiseGate
{
    private readonly float _openCoeff;
    private readonly float _closeCoeff;
    private readonly int _holdSamples;
    private EnvelopeFollower _detector;
    private float _gain = 1f;
    private int _hold;

    public NoiseGate(int sampleRate)
    {
        _detector.Configure(sampleRate, 1, 60);
        _openCoeff = (float)Math.Exp(-1.0 / (sampleRate * 0.002));
        _closeCoeff = (float)Math.Exp(-1.0 / (sampleRate * 0.080));
        _holdSamples = sampleRate * 120 / 1000;
    }

    public float ThresholdDb { get; set; } = -50f;
    public float FloorDb { get; set; } = -24f;

    public float Process(float x)
    {
        float env = _detector.Next(x);
        float open = DbToLinear(ThresholdDb);
        float target;
        if (env >= open) { _hold = _holdSamples; target = 1f; }
        else if (env >= open * 0.5f || _hold > 0) { _hold--; target = _gain < 1f ? _gain : 1f; }
        else target = DbToLinear(FloorDb);

        float coeff = target > _gain ? _openCoeff : _closeCoeff;
        _gain = target + coeff * (_gain - target);
        return x * _gain;
    }

    public void Reset()
    {
        _detector.Reset();
        _gain = 1f;
        _hold = 0;
    }

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}

/// <summary>
/// Split-band de-esser: a band-pass sidechain around the sibilance region drives gain reduction
/// that is applied only to the content above the crossover.
/// </summary>
public sealed class DeEsser
{
    private readonly Biquad _sidechain = new();
    private readonly Biquad _splitLow = new();
    private readonly Biquad _splitLow2 = new();
    private EnvelopeFollower _detector;
    private float _gain = 1f;

    public DeEsser(int sampleRate, double frequency = 6500)
    {
        _sidechain.SetBandPass(sampleRate, frequency, 1.2);
        // 4th-order (LR4-style) low band; high band = input − low (complementary, phase-coherent enough for speech).
        _splitLow.SetLowPass(sampleRate, frequency * 0.75);
        _splitLow2.SetLowPass(sampleRate, frequency * 0.75);
        _detector.Configure(sampleRate, 0.5, 40);
    }

    public float ThresholdDb { get; set; } = -30f;
    public float MaxReductionDb { get; set; } = -8f;

    public float Process(float x)
    {
        float s = _detector.Next(_sidechain.Process(x));
        float sDb = s > 1e-9f ? 20f * MathF.Log10(s) : -180f;
        float reductionDb = Math.Max(MaxReductionDb, Math.Min(0f, (ThresholdDb - sDb) * 0.6f));
        _gain = MathF.Pow(10f, reductionDb / 20f);

        float low = _splitLow2.Process(_splitLow.Process(x));
        float high = x - low;
        return low + high * _gain;
    }

    public void Reset()
    {
        _sidechain.Reset();
        _splitLow.Reset();
        _splitLow2.Reset();
        _detector.Reset();
        _gain = 1f;
    }
}
