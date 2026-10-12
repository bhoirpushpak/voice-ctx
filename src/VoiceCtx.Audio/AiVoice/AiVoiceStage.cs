using VoiceCtx.Audio.Dsp;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.Audio.AiVoice;

/// <summary>
/// Real-time bridge for AI Voice. The audio thread hands its block to a lock-free FIFO and reads
/// converted audio from another, delayed by a fixed priming amount; a dedicated worker thread does
/// resampling to 16 kHz, model inference, block crossfading and resampling back. The audio thread
/// never waits on inference: if the worker falls behind, the stage fades back to the dry signal and
/// re-primes, instead of glitching.
/// </summary>
public sealed class AiVoiceStage : IDisposable
{
    private const double FadeSeconds = 0.030;
    private const double JitterMarginSeconds = 0.040;
    private const int WorkerSampleRate = 16000;

    private readonly int _rate;
    private readonly AiVoiceControls _controls;
    private readonly AiVoiceService _service;
    private readonly SpscRingBuffer _input;
    private readonly SpscRingBuffer _output;
    private readonly float[] _wet;
    private readonly float _gainStep;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private volatile bool _running = true;
    private volatile bool _resetRequested = true;

    private const double ShortHoldSeconds = 2, LongHoldSeconds = 5, RepeatWindowSeconds = 5;

    // Audio-thread state.
    private long _clock;
    private long _holdUntil;
    private long _lastUnderrun = long.MinValue / 2;
    private float _wetGain;
    private bool _active;
    private bool _primed;
    private volatile int _primeSamples;
    private volatile int _maxBuffered;

    public AiVoiceStage(int sampleRate, int maxBlockSize, AiVoiceControls controls, AiVoiceService service)
    {
        _rate = sampleRate;
        _controls = controls;
        _service = service;
        _input = new SpscRingBuffer(sampleRate * 2);
        _output = new SpscRingBuffer(sampleRate * 2);
        _wet = new float[Math.Max(1, maxBlockSize)];
        _gainStep = 1f / Math.Max(1, (float)(sampleRate * FadeSeconds));
        _primeSamples = int.MaxValue;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VoiceCtx AI Voice", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    /// <summary>Times the worker could not keep up (the stage fell back to the dry signal).</summary>
    public int Underruns { get; private set; }

    /// <summary>True while the stage is temporarily using the dry voice because inference was too slow.</summary>
    public bool IsFallingBehind => _active && _clock < _holdUntil;

    /// <summary>Approximate added delay of the AI voice path, in milliseconds.</summary>
    public double LatencyMs => _primeSamples == int.MaxValue ? 0 : 1000.0 * _primeSamples / _rate + _marginMs;

    private volatile float _marginMs;

    private int BlockDeviceSamples { get; set; }

    /// <summary>Audio thread. Replaces the buffer with the converted voice (crossfaded on toggle).</summary>
    public void Process(Span<float> buffer)
    {
        bool want = _controls.Enabled && _service.IsReady;
        if (!want && !_active && _wetGain <= 0f) return;

        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, _wet.Length)];
            ProcessChunk(chunk, want);
            buffer = buffer[chunk.Length..];
        }
    }

    private void ProcessChunk(Span<float> buffer, bool want)
    {
        if (want && !_active)
        {
            _active = true;
            _primed = false;
            _output.Skip(_output.Count);
            _resetRequested = true;
        }

        if (_active)
        {
            _input.Write(buffer);
            _signal.Set();
        }

        var wet = _wet.AsSpan(0, buffer.Length);
        wet.Clear();
        if (_active)
        {
            int available = _output.Count;
            if (_clock < _holdUntil)
            {
                // Recovering from an underrun: stay on the dry voice and discard stale output.
                _output.Skip(available);
            }
            else
            {
                if (!_primed && available >= _primeSamples)
                {
                    // Output arrives in whole blocks; start exactly at the prime level so the
                    // buffered delay is the minimum that still absorbs inference jitter.
                    _output.Skip(available - _primeSamples);
                    available = _primeSamples;
                    _primed = true;
                }
                if (_primed)
                {
                    if (available > _maxBuffered) _output.Skip(available - _primeSamples);
                    int read = _output.Read(wet);
                    if (read < wet.Length) OnUnderrun();
                }
            }
        }
        _clock += buffer.Length;

        float target = want && _primed ? 1f : 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            _wetGain = target > _wetGain ? Math.Min(target, _wetGain + _gainStep) : Math.Max(target, _wetGain - _gainStep);
            buffer[i] += _wetGain * (wet[i] - buffer[i]);
        }

        if (!want && _wetGain <= 0f && _active)
        {
            _active = false;
            _primed = false;
        }
    }

    /// <summary>
    /// The worker missed its deadline. Fall back to the dry voice for a while instead of flapping
    /// between wet and dry; repeated misses back off longer.
    /// </summary>
    private void OnUnderrun()
    {
        Underruns++;
        _primed = false;
        bool recent = _clock - _lastUnderrun < _rate * RepeatWindowSeconds;
        _lastUnderrun = _clock;
        _holdUntil = _clock + (long)(_rate * (recent ? LongHoldSeconds : ShortHoldSeconds));
    }

    // ------------------------------------------------------------------ worker thread

    private void WorkerLoop()
    {
        var state = new WorkerState(_rate);
        var chunk = new float[_rate / 10];
        while (_running)
        {
            _signal.WaitOne(50);
            if (!_running) break;

            if (_resetRequested)
            {
                _resetRequested = false;
                _input.Skip(_input.Count);
                state.Reset();
            }

            var shape = _service.CurrentShape;
            if (shape is not { } s)
            {
                _input.Skip(_input.Count);
                continue;
            }
            if (state.Configure(s))
            {
                BlockDeviceSamples = (int)((long)s.BlockSamples16k * _rate / WorkerSampleRate);
                _marginMs = 1000f * s.MarginSamples / s.OutputSampleRate;
                // Prime one block plus scheduling/inference jitter; cap at three blocks to bound delay.
                _primeSamples = BlockDeviceSamples + (int)(_rate * JitterMarginSeconds);
                _maxBuffered = _primeSamples + BlockDeviceSamples * 2;
            }

            int read;
            while ((read = _input.Read(chunk)) > 0)
            {
                state.PushInput(chunk.AsSpan(0, read));
                while (state.HasBlock)
                {
                    float shift = _controls.PitchShift;
                    if (!state.ConvertBlock(_service, shift)) break;
                    _output.Write(state.DeviceOutput);
                }
            }
        }
    }

    /// <summary>Worker-only buffers; reallocated only when the voice's shape changes.</summary>
    private sealed class WorkerState(int deviceRate)
    {
        private ConverterShape _shape;
        private StreamResampler? _toWorker;
        private StreamResampler? _toDevice;
        private float[] _resampled = [];
        private float[] _context = [];
        private float[] _pending = [];
        private int _pendingCount;
        private float[] _converted = [];
        private float[] _block = [];
        private float[] _fadeIn = [];
        private float[] _tailMemory = [];
        private float[] _deviceOut = [];
        private int _deviceCount;
        private bool _hasPrevious;

        public bool HasBlock => _pendingCount >= _shape.BlockSamples16k && _shape.BlockSamples16k > 0;

        public ReadOnlySpan<float> DeviceOutput => _deviceOut.AsSpan(0, _deviceCount);

        /// <summary>Returns true when the shape changed (buffers rebuilt).</summary>
        public bool Configure(ConverterShape shape)
        {
            if (shape == _shape) return false;
            _shape = shape;
            int maxIn = deviceRate / 10;
            _toWorker = new StreamResampler(maxIn);
            _toDevice = new StreamResampler(shape.BlockOutputSamples);
            _resampled = new float[StreamResampler.MaxOutputLength(maxIn, (double)deviceRate / WorkerSampleRate) + 8];
            _context = new float[shape.ContextSamples16k];
            _pending = new float[shape.BlockSamples16k * 4 + _resampled.Length];
            _converted = new float[shape.OutputSamples];
            _block = new float[shape.BlockOutputSamples];
            _tailMemory = new float[shape.MarginSamples];
            _fadeIn = new float[shape.MarginSamples];
            for (int i = 0; i < _fadeIn.Length; i++)
            {
                double x = Math.Sin(Math.PI / 2 * i / Math.Max(1, _fadeIn.Length - 1));
                _fadeIn[i] = (float)(x * x);
            }
            _deviceOut = new float[StreamResampler.MaxOutputLength(shape.BlockOutputSamples, (double)shape.OutputSampleRate / deviceRate) + 8];
            Reset();
            return true;
        }

        public void Reset()
        {
            _toWorker?.Reset();
            _toDevice?.Reset();
            Array.Clear(_context);
            Array.Clear(_tailMemory);
            _pendingCount = 0;
            _hasPrevious = false;
        }

        public void PushInput(ReadOnlySpan<float> deviceSamples)
        {
            if (_toWorker == null) return;
            int produced = _toWorker.Process(deviceSamples, _resampled, (double)deviceRate / WorkerSampleRate);
            int room = _pending.Length - _pendingCount;
            if (produced > room)
            {
                // Inference fell far behind: drop the oldest audio rather than grow latency.
                int drop = produced - room;
                _pending.AsSpan(drop, _pendingCount - drop).CopyTo(_pending);
                _pendingCount -= drop;
            }
            _resampled.AsSpan(0, produced).CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += produced;
        }

        public bool ConvertBlock(AiVoiceService service, float pitchShift)
        {
            int block = _shape.BlockSamples16k;
            _context.AsSpan(block).CopyTo(_context);
            _pending.AsSpan(0, block).CopyTo(_context.AsSpan(_context.Length - block));
            _pending.AsSpan(block, _pendingCount - block).CopyTo(_pending);
            _pendingCount -= block;

            if (!service.TryConvert(_context, pitchShift, _converted, out var shape) || shape != _shape)
            {
                _deviceCount = 0;
                return false;
            }

            // Crossfade the overlap with the previous block's last samples, then emit one block.
            int margin = _shape.MarginSamples;
            int emitted = 0;
            for (int i = 0; i < margin; i++)
            {
                float fresh = _converted[i];
                _block[emitted++] = _hasPrevious ? _tailMemory[i] + _fadeIn[i] * (fresh - _tailMemory[i]) : fresh * _fadeIn[i];
            }
            int body = _shape.BlockOutputSamples - margin;
            _converted.AsSpan(margin, body).CopyTo(_block.AsSpan(emitted));
            _converted.AsSpan(_shape.OutputSamples - margin, margin).CopyTo(_tailMemory);
            _hasPrevious = true;

            _deviceCount = _toDevice!.Process(_block, _deviceOut, (double)_shape.OutputSampleRate / deviceRate);
            return true;
        }
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _worker.Join(5000);
        _signal.Dispose();
    }
}
