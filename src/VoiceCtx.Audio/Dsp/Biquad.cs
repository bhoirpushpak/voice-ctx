namespace VoiceCtx.Audio.Dsp;

/// <summary>RBJ-cookbook biquad, transposed direct form II. Coefficients can be changed in place (no allocation).</summary>
public sealed class Biquad
{
    private float _b0 = 1, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    public static Biquad LowPass(int sampleRate, double frequency, double q = 0.7071)
    {
        var f = new Biquad();
        f.SetLowPass(sampleRate, frequency, q);
        return f;
    }

    public static Biquad HighPass(int sampleRate, double frequency, double q = 0.7071)
    {
        var f = new Biquad();
        f.SetHighPass(sampleRate, frequency, q);
        return f;
    }

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

    public void SetLowPass(int sampleRate, double frequency, double q = 0.7071)
    {
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetHighPass(int sampleRate, double frequency, double q = 0.7071)
    {
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Constant-skirt band-pass (0 dB peak gain).</summary>
    public void SetBandPass(int sampleRate, double frequency, double q)
    {
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetPeaking(int sampleRate, double frequency, double q, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public void SetLowShelf(int sampleRate, double frequency, double gainDb, double slope = 1)
    {
        double a = Math.Pow(10, gainDb / 40);
        var (cos, alpha) = ShelfPrewarp(sampleRate, frequency, a, slope);
        double s = 2 * Math.Sqrt(a) * alpha;
        Set(a * ((a + 1) - (a - 1) * cos + s),
            2 * a * ((a - 1) - (a + 1) * cos),
            a * ((a + 1) - (a - 1) * cos - s),
            (a + 1) + (a - 1) * cos + s,
            -2 * ((a - 1) + (a + 1) * cos),
            (a + 1) + (a - 1) * cos - s);
    }

    public void SetHighShelf(int sampleRate, double frequency, double gainDb, double slope = 1)
    {
        double a = Math.Pow(10, gainDb / 40);
        var (cos, alpha) = ShelfPrewarp(sampleRate, frequency, a, slope);
        double s = 2 * Math.Sqrt(a) * alpha;
        Set(a * ((a + 1) + (a - 1) * cos + s),
            -2 * a * ((a - 1) + (a + 1) * cos),
            a * ((a + 1) + (a - 1) * cos - s),
            (a + 1) - (a - 1) * cos + s,
            2 * ((a - 1) - (a + 1) * cos),
            (a + 1) - (a - 1) * cos - s);
    }

    private static (double Cos, double Alpha) Prewarp(int sampleRate, double frequency, double q)
    {
        double w0 = 2 * Math.PI * Math.Min(frequency, sampleRate * 0.45) / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    private static (double Cos, double Alpha) ShelfPrewarp(int sampleRate, double frequency, double a, double slope)
    {
        double w0 = 2 * Math.PI * Math.Min(frequency, sampleRate * 0.45) / sampleRate;
        double alpha = Math.Sin(w0) / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        return (Math.Cos(w0), alpha);
    }

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }
}
