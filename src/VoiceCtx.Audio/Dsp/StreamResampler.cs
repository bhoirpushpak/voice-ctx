namespace VoiceCtx.Audio.Dsp;

/// <summary>
/// Streaming mono resampler using 4-point Catmull-Rom interpolation. The ratio can change on
/// every call, which the engine uses to absorb clock drift between capture and render devices.
/// Fixed allocation; safe to call from the audio thread.
/// </summary>
public sealed class StreamResampler
{
    private const int History = 3;
    private readonly float[] _work;
    private double _position = 1.0;

    public StreamResampler(int maxInputBlock)
    {
        _work = new float[maxInputBlock + History];
    }

    public int MaxInputBlock => _work.Length - History;

    /// <summary>Upper bound on output samples produced for <paramref name="inputLength"/> input samples.</summary>
    public static int MaxOutputLength(int inputLength, double step) => (int)Math.Ceiling(inputLength / step) + 2;

    /// <param name="step">Input samples consumed per output sample (inputRate / outputRate).</param>
    /// <returns>Number of samples written to <paramref name="output"/>.</returns>
    public int Process(ReadOnlySpan<float> input, Span<float> output, double step)
    {
        int written = 0;
        while (!input.IsEmpty)
        {
            var chunk = input[..Math.Min(input.Length, MaxInputBlock)];
            written += ProcessChunk(chunk, output[written..], step);
            input = input[chunk.Length..];
        }
        return written;
    }

    public void Reset()
    {
        Array.Clear(_work);
        _position = 1.0;
    }

    private int ProcessChunk(ReadOnlySpan<float> chunk, Span<float> output, double step)
    {
        chunk.CopyTo(_work.AsSpan(History));
        int length = History + chunk.Length;
        double p = _position;
        int o = 0;
        while (o < output.Length)
        {
            int i = (int)p;
            if (i + 2 >= length) break;
            float t = (float)(p - i);
            output[o++] = CatmullRom(_work[i - 1], _work[i], _work[i + 1], _work[i + 2], t);
            p += step;
        }

        _work.AsSpan(length - History, History).CopyTo(_work);
        // Clamp only matters if the caller's output span was too small and input got dropped.
        _position = Math.Max(1.0, p - (length - History));
        return o;
    }

    private static float CatmullRom(float y0, float y1, float y2, float y3, float t)
    {
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * t + c2) * t + c1) * t + y1;
    }
}
