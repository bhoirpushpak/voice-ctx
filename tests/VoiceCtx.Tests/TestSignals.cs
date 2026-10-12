namespace VoiceCtx.Tests;

internal static class TestSignals
{
    public static float[] Sine(double frequency, int sampleRate, int length, float amplitude = 0.5f)
    {
        var x = new float[length];
        for (int i = 0; i < length; i++)
            x[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        return x;
    }

    public static float[] Noise(int length, int seed = 1234, float amplitude = 0.9f)
    {
        var rng = new Random(seed);
        var x = new float[length];
        for (int i = 0; i < length; i++) x[i] = amplitude * (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    /// <summary>Frequency from interpolated rising zero crossings; accurate for a dominant sinusoid.</summary>
    public static double EstimateFrequency(ReadOnlySpan<float> x, int sampleRate)
    {
        double first = -1, last = -1;
        int crossings = 0;
        for (int i = 1; i < x.Length; i++)
        {
            if (x[i - 1] < 0 && x[i] >= 0)
            {
                double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                if (first < 0) first = t;
                last = t;
                crossings++;
            }
        }
        return crossings < 2 ? 0 : (crossings - 1) * sampleRate / (last - first);
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float s in x) sum += s * s;
        return x.Length == 0 ? 0 : Math.Sqrt(sum / x.Length);
    }
}
