namespace VoiceCtx.Audio.Dsp;

/// <summary>RBJ-cookbook biquad, transposed direct form II.</summary>
public sealed class Biquad
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    public static Biquad LowPass(int sampleRate, double frequency, double q = 0.7071) =>
        Create(sampleRate, frequency, q, highPass: false);

    public static Biquad HighPass(int sampleRate, double frequency, double q = 0.7071) =>
        Create(sampleRate, frequency, q, highPass: true);

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        if (Math.Abs(_z1) < 1e-15f) _z1 = 0;
        if (Math.Abs(_z2) < 1e-15f) _z2 = 0;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0;

    private static Biquad Create(int sampleRate, double frequency, double q, bool highPass)
    {
        double w0 = 2 * Math.PI * Math.Min(frequency, sampleRate * 0.45) / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        double b0 = highPass ? (1 + cos) / 2 : (1 - cos) / 2;
        double b1 = highPass ? -(1 + cos) : 1 - cos;
        return new Biquad
        {
            _b0 = (float)(b0 / a0),
            _b1 = (float)(b1 / a0),
            _b2 = (float)(b0 / a0),
            _a1 = (float)(-2 * cos / a0),
            _a2 = (float)((1 - alpha) / a0),
        };
    }
}
