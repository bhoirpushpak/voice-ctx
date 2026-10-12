using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Enhancement;

/// <summary>
/// "Studio mic" vocal chain: rumble filter → expander → broadcast EQ → de-esser → compressor →
/// limiter. <see cref="Strength"/> scales EQ amounts, compression and make-up together.
/// </summary>
public sealed class StudioEffect : EffectBase
{
    private readonly Biquad _rumble1 = new();
    private readonly Biquad _rumble2 = new();
    private readonly Biquad _warmth = new();
    private readonly Biquad _mud = new();
    private readonly Biquad _presence = new();
    private readonly Biquad _air = new();
    private readonly NoiseGate _gate;
    private readonly DeEsser _deEsser;
    private readonly Compressor _compressor;
    private readonly Compressor _limiter;
    private readonly EffectParameter _strength = new("Strength", 0f, 1f, 0.7f);
    private float _configuredStrength = float.NaN;

    public StudioEffect(int sampleRate, int maxBlockSize) : base(sampleRate, maxBlockSize)
    {
        _rumble1.SetHighPass(sampleRate, 80);
        _rumble2.SetHighPass(sampleRate, 80);
        _gate = new NoiseGate(sampleRate);
        _deEsser = new DeEsser(sampleRate);
        _compressor = new Compressor(sampleRate, attackMs: 8, releaseMs: 150);
        _limiter = new Compressor(sampleRate, attackMs: 0.3, releaseMs: 60) { ThresholdDb = -1.5f, Ratio = 20f };
        Configure();
    }

    public override string Name => "Studio sound";

    public override IReadOnlyList<EffectParameter> Parameters => [_strength];

    /// <summary>0 = subtle, 1 = heavily produced broadcast sound.</summary>
    public EffectParameter Strength => _strength;

    protected override void ProcessWet(Span<float> buffer)
    {
        if (_strength.Value != _configuredStrength) Configure();

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = _rumble2.Process(_rumble1.Process(buffer[i]));
            x = _gate.Process(x);
            x = _air.Process(_presence.Process(_mud.Process(_warmth.Process(x))));
            x = _deEsser.Process(x);
            x = _compressor.Process(x);
            x = _limiter.Process(x);
            buffer[i] = SoftClip(x);
        }
    }

    protected override void OnReset()
    {
        _rumble1.Reset();
        _rumble2.Reset();
        _warmth.Reset();
        _mud.Reset();
        _presence.Reset();
        _air.Reset();
        _gate.Reset();
        _deEsser.Reset();
        _compressor.Reset();
        _limiter.Reset();
    }

    private void Configure()
    {
        float s = _strength.Value;
        _configuredStrength = s;
        int rate = SampleRate;

        _warmth.SetLowShelf(rate, 140, 2.5 * s);
        _mud.SetPeaking(rate, 350, 1.0, -3.0 * s);
        _presence.SetPeaking(rate, 4500, 0.9, 3.5 * s);
        _air.SetHighShelf(rate, 11000, 2.5 * s);

        _gate.ThresholdDb = -52f;
        _gate.FloorDb = -6f - 24f * s;

        _compressor.ThresholdDb = -18f - 10f * s;
        _compressor.Ratio = 1.5f + 2.5f * s;
        // Partial make-up so quiet speech comes up without pumping room noise.
        _compressor.MakeupDb = -_compressor.ThresholdDb * (1f - 1f / _compressor.Ratio) * 0.6f;
    }

    private static float SoftClip(float x)
    {
        const float knee = 0.9f;
        float a = Math.Abs(x);
        if (a <= knee) return x;
        float over = (a - knee) / (1f - knee);
        return MathF.CopySign(knee + (1f - knee) * MathF.Tanh(over), x);
    }
}
