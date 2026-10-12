namespace VoiceCtx.Audio.AiVoice;

/// <summary>
/// Log-mel spectrogram matching RMVPE's training front end (librosa: centered reflect-padded STFT
/// magnitude, periodic Hann, HTK mel scale with Slaney area normalisation, natural log clamped at
/// 1e-5). Output is laid out [mel, frame] with frames zero-padded to a multiple of 32.
/// </summary>
internal sealed class MelFrontEnd
{
    public const int SampleRate = 16000;
    public const int FftSize = 1024;
    public const int Hop = 160;
    public const int Mels = 128;
    private const double FMin = 30, FMax = 8000;
    private const int Bins = FftSize / 2 + 1;

    private readonly Fft _fft = new(FftSize);
    private readonly double[] _window = new double[FftSize];
    private readonly float[] _basis = new float[Mels * Bins];
    private readonly int[] _basisStart = new int[Mels];
    private readonly int[] _basisEnd = new int[Mels];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly float[] _magnitude = new float[Bins];

    public MelFrontEnd()
    {
        for (int n = 0; n < FftSize; n++) _window[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FftSize);
        BuildBasis();
    }

    public static int FrameCount(int samples) => 1 + samples / Hop;

    public static int PaddedFrameCount(int frames) => 32 * ((frames - 1) / 32 + 1);

    /// <summary>Row sums of the mel filterbank (for verification against librosa).</summary>
    internal float[] BasisRowSums()
    {
        var sums = new float[Mels];
        for (int m = 0; m < Mels; m++)
            for (int k = 0; k < Bins; k++) sums[m] += _basis[m * Bins + k];
        return sums;
    }

    /// <param name="audio">16 kHz mono samples.</param>
    /// <param name="mel">Destination of size <see cref="Mels"/> × PaddedFrameCount(FrameCount(audio.Length)).</param>
    public void Compute(ReadOnlySpan<float> audio, Span<float> mel)
    {
        int frames = FrameCount(audio.Length);
        int padded = PaddedFrameCount(frames);
        if (mel.Length < Mels * padded) throw new ArgumentException("mel buffer too small", nameof(mel));
        mel[..(Mels * padded)].Clear();

        int half = FftSize / 2;
        for (int t = 0; t < frames; t++)
        {
            int origin = t * Hop - half;
            for (int n = 0; n < FftSize; n++)
            {
                _re[n] = Reflect(audio, origin + n) * _window[n];
                _im[n] = 0;
            }
            _fft.Forward(_re, _im);
            for (int k = 0; k < Bins; k++) _magnitude[k] = (float)Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);

            for (int m = 0; m < Mels; m++)
            {
                double sum = 0;
                int row = m * Bins;
                for (int k = _basisStart[m]; k < _basisEnd[m]; k++) sum += _basis[row + k] * _magnitude[k];
                mel[m * padded + t] = (float)Math.Log(Math.Max(sum, 1e-5));
            }
        }
    }

    private static float Reflect(ReadOnlySpan<float> x, int i)
    {
        int n = x.Length;
        if (n == 1) return x[0];
        int period = 2 * (n - 1);
        i %= period;
        if (i < 0) i += period;
        return i < n ? x[i] : x[period - i];
    }

    private void BuildBasis()
    {
        static double HzToMel(double f) => 2595.0 * Math.Log10(1.0 + f / 700.0);
        static double MelToHz(double m) => 700.0 * (Math.Pow(10.0, m / 2595.0) - 1.0);

        var melF = new double[Mels + 2];
        double lo = HzToMel(FMin), hi = HzToMel(FMax);
        for (int i = 0; i < melF.Length; i++) melF[i] = MelToHz(lo + (hi - lo) * i / (Mels + 1));

        for (int m = 0; m < Mels; m++)
        {
            double lower = melF[m], center = melF[m + 1], upper = melF[m + 2];
            double norm = 2.0 / (upper - lower);
            int first = Bins, last = 0;
            for (int k = 0; k < Bins; k++)
            {
                double f = (double)k * SampleRate / FftSize;
                double rising = (f - lower) / (center - lower);
                double falling = (upper - f) / (upper - center);
                double w = Math.Max(0, Math.Min(rising, falling)) * norm;
                _basis[m * Bins + k] = (float)w;
                if (w > 0) { first = Math.Min(first, k); last = k; }
            }
            _basisStart[m] = first;
            _basisEnd[m] = Math.Max(first, last + 1);
        }
    }
}

/// <summary>Turns RMVPE's 360-bin pitch salience into Hz, and Hz into RVC's coarse pitch tokens.</summary>
internal static class PitchMath
{
    public const int SalienceBins = 360;
    private const double Threshold = 0.03;
    private static readonly double CoarseLow = 1127.0 * Math.Log(1 + 50.0 / 700);
    private static readonly double CoarseHigh = 1127.0 * Math.Log(1 + 1100.0 / 700);

    /// <summary>Local weighted average of ±4 bins around the peak, in cents → Hz (0 when unvoiced).</summary>
    public static void DecodeSalience(ReadOnlySpan<float> salience, int frames, Span<float> f0)
    {
        for (int t = 0; t < frames; t++)
        {
            var row = salience.Slice(t * SalienceBins, SalienceBins);
            int peak = 0;
            float max = row[0];
            for (int c = 1; c < SalienceBins; c++)
                if (row[c] > max) { max = row[c]; peak = c; }

            if (max <= Threshold)
            {
                f0[t] = 0;
                continue;
            }

            double weighted = 0, weight = 0;
            for (int c = peak - 4; c <= peak + 4; c++)
            {
                if (c < 0 || c >= SalienceBins) continue;
                double cents = 20.0 * c + 1997.3794084376191;
                weighted += row[c] * cents;
                weight += row[c];
            }
            f0[t] = (float)(10.0 * Math.Pow(2, weighted / weight / 1200.0));
        }
    }

    /// <summary>RVC's 1..255 mel-scaled pitch token (1 = unvoiced).</summary>
    public static long Coarse(float f0)
    {
        double mel = 1127.0 * Math.Log(1 + f0 / 700.0);
        double c = mel > 0 ? (mel - CoarseLow) * 254.0 / (CoarseHigh - CoarseLow) + 1 : 1;
        return (long)Math.Clamp(Math.Round(c, MidpointRounding.ToEven), 1, 255);
    }
}
