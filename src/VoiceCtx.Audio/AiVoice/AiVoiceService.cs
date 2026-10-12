namespace VoiceCtx.Audio.AiVoice;

public enum AiVoiceStatus { NotInstalled, Idle, Loading, Ready, Error }

/// <summary>AI Voice switches shared between the UI and the audio thread (lock-free).</summary>
public sealed class AiVoiceControls
{
    private volatile bool _enabled;
    private volatile float _pitchShift;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Semitones applied to your pitch before conversion (e.g. +12 for male → female voice).</summary>
    public float PitchShift
    {
        get => _pitchShift;
        set => _pitchShift = Math.Clamp(value, -24f, 24f);
    }
}

/// <summary>
/// Owns the loaded models (GPU memory) independently of audio pipelines, so device changes don't
/// reload ~800 MB of models. Inference workers borrow the converter under <see cref="_sync"/>,
/// which voice switching also takes before disposing the previous voice.
/// </summary>
public sealed class AiVoiceService : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private RvcRuntime? _runtime;
    private IVoiceConverter? _converter;
    private volatile bool _ready;
    private bool _disposed;

    public AiVoiceService(VoiceLibrary library)
    {
        Library = library;
        Status = library.BaseModelsInstalled ? AiVoiceStatus.Idle : AiVoiceStatus.NotInstalled;
        StatusMessage = library.BaseModelsInstalled ? "No voice loaded" : "AI Voice models are not installed";
    }

    /// <summary>For tests: wraps an already-constructed converter.</summary>
    internal AiVoiceService(IVoiceConverter converter)
    {
        Library = new VoiceLibrary(Path.GetTempPath());
        _converter = converter;
        _ready = true;
        Status = AiVoiceStatus.Ready;
        StatusMessage = "Ready";
    }

    public event EventHandler? StatusChanged;

    public VoiceLibrary Library { get; }
    public AiVoiceStatus Status { get; private set; }
    public string StatusMessage { get; private set; }
    public VoiceInfo? CurrentVoice { get; private set; }
    public string? DeviceName => _runtime?.DeviceName;
    public bool UsesGpu => _runtime?.UsesGpu ?? false;

    /// <summary>True when a voice is loaded and conversion can run.</summary>
    public bool IsReady => _ready;

    /// <summary>Loads (or switches to) a voice on a background thread. Safe to call repeatedly.</summary>
    public async Task LoadVoiceAsync(VoiceInfo voice)
    {
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || CurrentVoice?.Id == voice.Id && _ready) return;
            if (!Library.BaseModelsInstalled)
            {
                SetStatus(AiVoiceStatus.NotInstalled, "AI Voice models are not installed");
                return;
            }

            SetStatus(AiVoiceStatus.Loading, $"Loading {voice.Name}…");
            var converter = await Task.Run(() =>
            {
                _runtime ??= RvcRuntime.Load(Library);
                var c = new RvcConverter(_runtime, voice);
                c.Convert(new float[c.ContextSamples16k], 0, new float[c.OutputSamples]); // warm up GPU kernels
                return c;
            }).ConfigureAwait(false);

            IVoiceConverter? previous;
            lock (_sync)
            {
                previous = _converter;
                _converter = converter;
                CurrentVoice = voice;
                _ready = true;
            }
            previous?.Dispose();
            SetStatus(AiVoiceStatus.Ready, UsesGpu ? $"Running on {DeviceName}" : "Running on CPU (slow — a GPU is recommended)");
        }
        catch (Exception ex)
        {
            SetStatus(AiVoiceStatus.Error, $"Could not load voice: {ex.Message}");
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Worker threads only. Returns false when no voice is ready (caller should output silence).</summary>
    internal bool TryConvert(ReadOnlySpan<float> context16k, float pitchShift, Span<float> output, out ConverterShape shape)
    {
        lock (_sync)
        {
            var c = _converter;
            if (c == null || !_ready)
            {
                shape = default;
                return false;
            }
            shape = new ConverterShape(c.ContextSamples16k, c.BlockSamples16k, c.OutputSampleRate, c.OutputSamples, c.MarginSamples);
            if (context16k.Length < c.ContextSamples16k || output.Length < c.OutputSamples) return false;
            c.Convert(context16k, pitchShift, output);
            return true;
        }
    }

    /// <summary>Shape of the current converter, or null when nothing is loaded.</summary>
    internal ConverterShape? CurrentShape
    {
        get
        {
            lock (_sync)
            {
                var c = _converter;
                return c == null ? null : new ConverterShape(c.ContextSamples16k, c.BlockSamples16k, c.OutputSampleRate, c.OutputSamples, c.MarginSamples);
            }
        }
    }

    private void SetStatus(AiVoiceStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _loadGate.Wait();
        try
        {
            _disposed = true;
            lock (_sync)
            {
                _ready = false;
                _converter?.Dispose();
                _converter = null;
            }
            _runtime?.Dispose();
            _runtime = null;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}

internal readonly record struct ConverterShape(int ContextSamples16k, int BlockSamples16k, int OutputSampleRate, int OutputSamples, int MarginSamples)
{
    public int BlockOutputSamples => OutputSamples - MarginSamples;
}
