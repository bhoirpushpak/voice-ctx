using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Enhancement;

/// <summary>Mic clean-up settings shared between the UI and the audio thread (lock-free).</summary>
public sealed class MicEnhancementControls
{
    private volatile bool _noiseCancel;
    private volatile float _noiseReduction = 1f;
    private volatile bool _studio;
    private volatile float _studioStrength = 0.7f;

    public bool NoiseCancel
    {
        get => _noiseCancel;
        set => _noiseCancel = value;
    }

    /// <summary>0..1 blend between the original and the fully denoised signal.</summary>
    public float NoiseReduction
    {
        get => _noiseReduction;
        set => _noiseReduction = Math.Clamp(value, 0f, 1f);
    }

    public bool Studio
    {
        get => _studio;
        set => _studio = value;
    }

    /// <summary>0..1 intensity of the studio vocal chain.</summary>
    public float StudioStrength
    {
        get => _studioStrength;
        set => _studioStrength = Math.Clamp(value, 0f, 1f);
    }
}

/// <summary>
/// Runs noise cancellation then the studio chain on the clean mic signal, before voice effects.
/// Each stage fades in/out over 20 ms when toggled and is skipped entirely while off.
/// </summary>
public sealed class MicEnhancer : IDisposable
{
    private readonly MicEnhancementControls _controls;
    private readonly NoiseSuppressor _noise;
    private readonly StudioEffect _studio;
    private readonly ToggleStage _noiseStage;
    private readonly ToggleStage _studioStage;

    public MicEnhancer(int sampleRate, int maxBlockSize, MicEnhancementControls controls)
    {
        _controls = controls;
        _noise = new NoiseSuppressor(sampleRate, maxBlockSize);
        _studio = new StudioEffect(sampleRate, maxBlockSize);
        _noiseStage = new ToggleStage(_noise, sampleRate, maxBlockSize);
        _studioStage = new ToggleStage(_studio, sampleRate, maxBlockSize);
    }

    public static bool NoiseCancellationSupported => Enhancement.RnNoise.IsAvailable;

    public void Process(Span<float> buffer)
    {
        _noise.Reduction.Value = _controls.NoiseReduction;
        _studio.Strength.Value = _controls.StudioStrength;
        _noiseStage.Process(buffer, _controls.NoiseCancel && _noise.IsAvailable);
        _studioStage.Process(buffer, _controls.Studio);
    }

    public void Dispose() => _noise.Dispose();

    private sealed class ToggleStage(IVoiceEffect effect, int sampleRate, int maxBlockSize)
    {
        private readonly float[] _dry = new float[Math.Max(1, maxBlockSize)];
        private readonly float _step = 1f / Math.Max(1, sampleRate * 0.020f);
        private float _gain;
        private bool _active;

        public void Process(Span<float> buffer, bool enabled)
        {
            if (!enabled && _gain <= 0f)
            {
                _active = false;
                return;
            }
            if (!_active)
            {
                effect.Reset();
                _active = true;
            }

            while (!buffer.IsEmpty)
            {
                var chunk = buffer[..Math.Min(buffer.Length, _dry.Length)];
                ProcessChunk(chunk, enabled ? 1f : 0f);
                buffer = buffer[chunk.Length..];
            }
        }

        private void ProcessChunk(Span<float> buffer, float target)
        {
            if (_gain == target && target == 1f)
            {
                effect.Process(buffer);
                return;
            }

            var dry = _dry.AsSpan(0, buffer.Length);
            buffer.CopyTo(dry);
            effect.Process(buffer);
            for (int i = 0; i < buffer.Length; i++)
            {
                _gain = target > _gain ? Math.Min(target, _gain + _step) : Math.Max(target, _gain - _step);
                buffer[i] = dry[i] + _gain * (buffer[i] - dry[i]);
            }
        }
    }
}
