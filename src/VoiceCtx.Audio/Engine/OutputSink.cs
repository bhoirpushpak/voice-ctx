using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Engine;

/// <summary>
/// Bridges the capture thread (producer, input rate) to one render thread (consumer, output rate).
/// The resampling ratio is nudged from the FIFO fill level so independent device clocks never
/// drift into underruns or growing latency.
/// </summary>
internal sealed class OutputSink
{
    private const double MaxCorrection = 0.005;
    private const double CorrectionGain = 0.002;
    private const double ErrorSmoothing = 0.05;

    private readonly SpscRingBuffer _ring;
    private readonly StreamResampler _resampler;
    private readonly float[] _scratch;
    private readonly double _nominalStep;
    private readonly int _targetFill;
    private readonly int _maxFill;
    private double _smoothedError;
    private bool _primed;

    public OutputSink(int inputRate, int outputRate, int maxInputBlock, int targetFillFrames, int maxFillFrames)
    {
        _nominalStep = (double)inputRate / outputRate;
        _targetFill = Math.Max(1, targetFillFrames);
        _maxFill = Math.Max(_targetFill * 2, maxFillFrames);
        _ring = new SpscRingBuffer(Math.Max(outputRate, _maxFill * 2));
        _resampler = new StreamResampler(maxInputBlock);
        _scratch = new float[StreamResampler.MaxOutputLength(maxInputBlock, _nominalStep * (1 - MaxCorrection)) + 8];
    }

    /// <summary>Producer (capture thread).</summary>
    public void Push(ReadOnlySpan<float> input)
    {
        double error = (_ring.Count - _targetFill) / (double)_targetFill;
        _smoothedError += ErrorSmoothing * (error - _smoothedError);
        double correction = Math.Clamp(_smoothedError * CorrectionGain, -MaxCorrection, MaxCorrection);
        int produced = _resampler.Process(input, _scratch, _nominalStep * (1 + correction));
        _ring.Write(_scratch.AsSpan(0, produced));
    }

    /// <summary>Consumer (render thread). Fills with silence while priming or on underrun.</summary>
    public void Pull(Span<float> destination)
    {
        int available = _ring.Count;
        if (available > _maxFill)
            _ring.Skip(available - _targetFill);

        if (!_primed)
        {
            if (available < _targetFill)
            {
                destination.Clear();
                return;
            }
            _primed = true;
        }

        int read = _ring.Read(destination);
        if (read < destination.Length)
        {
            destination[read..].Clear();
            _primed = false;
        }
    }
}
