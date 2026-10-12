using VoiceCtx.Audio.Dsp;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.Audio.Enhancement;

/// <summary>
/// RNNoise noise suppression for any device rate. Audio is resampled to 48 kHz, processed in
/// 480-sample frames, blended with the time-aligned original by <see cref="Reduction"/>, and
/// resampled back. Constant latency of about 20 ms. Passes audio through untouched if the native
/// library is unavailable.
/// </summary>
public sealed class NoiseSuppressor : EffectBase, IDisposable
{
    private const int Frame = RnNoise.FrameSize;
    private const int ResamplerSlack = 16;

    private readonly RnNoiseHandle? _state;
    private readonly bool _resample;
    private readonly double _upStep;
    private readonly double _downStep;
    private readonly StreamResampler? _up;
    private readonly StreamResampler? _down;
    private readonly SpscRingBuffer _in48;
    private readonly SpscRingBuffer _out;
    private readonly float[] _upScratch;
    private readonly float[] _downScratch;
    private readonly float[] _frameIn = new float[Frame];
    private readonly float[] _frameOut = new float[Frame];
    private readonly float[] _previousFrame = new float[Frame];
    private readonly int _primeSamples;
    private readonly EffectParameter _reduction = new("Reduction", 0f, 1f, 1f);
    private bool _disposed;

    public NoiseSuppressor(int sampleRate, int maxBlockSize) : base(sampleRate, maxBlockSize)
    {
        IsAvailable = RnNoise.IsAvailable;
        if (IsAvailable) _state = new RnNoiseHandle();

        _resample = sampleRate != RnNoise.SampleRate;
        _upStep = (double)sampleRate / RnNoise.SampleRate;
        _downStep = (double)RnNoise.SampleRate / sampleRate;
        int block = Math.Max(1, maxBlockSize);
        if (_resample)
        {
            _up = new StreamResampler(block);
            _down = new StreamResampler(Frame);
        }
        _upScratch = new float[StreamResampler.MaxOutputLength(block, _upStep) + 8];
        _downScratch = new float[StreamResampler.MaxOutputLength(Frame, _downStep) + 8];

        _primeSamples = (int)Math.Ceiling(Frame * (double)sampleRate / RnNoise.SampleRate) + (_resample ? ResamplerSlack : 0);
        _in48 = new SpscRingBuffer(Frame * 4 + _upScratch.Length);
        _out = new SpscRingBuffer(_primeSamples + block * 2 + _downScratch.Length * 2);
        Prime();
    }

    public override string Name => "Noise cancellation";

    public override IReadOnlyList<EffectParameter> Parameters => [_reduction];

    /// <summary>True when the RNNoise library loaded; otherwise processing is a pass-through.</summary>
    public bool IsAvailable { get; }

    /// <summary>0 = original signal, 1 = fully denoised.</summary>
    public EffectParameter Reduction => _reduction;

    /// <summary>Delay introduced by the processed path, in samples at the device rate.</summary>
    public int LatencySamples => _primeSamples + (int)Math.Round(Frame * (double)SampleRate / RnNoise.SampleRate);

    protected override unsafe void ProcessWet(Span<float> buffer)
    {
        if (_state == null || _disposed) return;

        if (_resample)
        {
            int produced = _up!.Process(buffer, _upScratch, _upStep);
            _in48.Write(_upScratch.AsSpan(0, produced));
        }
        else
        {
            _in48.Write(buffer);
        }

        float reduction = _reduction.Value;
        while (_in48.Count >= Frame)
        {
            _in48.Read(_frameIn);
            for (int i = 0; i < Frame; i++) _frameIn[i] *= 32768f;
            fixed (float* input = _frameIn)
            fixed (float* output = _frameOut)
            {
                RnNoise.ProcessFrame(_state.DangerousGetHandle(), output, input);
            }

            // RNNoise output lags its input by exactly one frame; blend with the matching original.
            const float scale = 1f / 32768f;
            for (int i = 0; i < Frame; i++)
            {
                float wet = _frameOut[i] * scale;
                float dry = _previousFrame[i];
                _frameOut[i] = dry + reduction * (wet - dry);
                _previousFrame[i] = _frameIn[i] * scale;
            }

            if (_resample)
            {
                int produced = _down!.Process(_frameOut, _downScratch, _downStep);
                _out.Write(_downScratch.AsSpan(0, produced));
            }
            else
            {
                _out.Write(_frameOut);
            }
        }

        int read = _out.Read(buffer);
        if (read < buffer.Length) buffer[read..].Clear();
    }

    protected override void OnReset()
    {
        if (_state != null && !_disposed)
        {
            // rnnoise_init would need the struct size; recreating state is not allocation-free, so
            // instead flush the network with silence, which settles its recurrent state quickly.
            Array.Clear(_frameIn);
            unsafe
            {
                fixed (float* input = _frameIn)
                fixed (float* output = _frameOut)
                {
                    for (int i = 0; i < 8; i++) RnNoise.ProcessFrame(_state.DangerousGetHandle(), output, input);
                }
            }
        }
        _up?.Reset();
        _down?.Reset();
        _in48.Skip(_in48.Count);
        _out.Skip(_out.Count);
        Array.Clear(_previousFrame);
        Prime();
    }

    private void Prime()
    {
        Span<float> zeros = stackalloc float[256];
        zeros.Clear();
        for (int left = _primeSamples; left > 0; left -= zeros.Length)
            _out.Write(zeros[..Math.Min(left, zeros.Length)]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state?.Dispose();
    }
}
