using NAudio.CoreAudioApi;

namespace VoiceCtx.Audio.Engine;

public enum EngineState { Stopped, Starting, Running, Recovering, Error }

/// <param name="InputDeviceId">Microphone; null or missing falls back to the default capture device.</param>
/// <param name="OutputDeviceId">Virtual cable input; null or missing falls back to "CABLE Input".</param>
/// <param name="MonitorDeviceId">Headphones for self-monitoring; null uses the default render device.</param>
public sealed record EngineConfig(string? InputDeviceId, string? OutputDeviceId, string? MonitorDeviceId, bool MonitorEnabled);

/// <summary>
/// Owns the real-time pipeline: mic capture → processing → virtual cable (+ optional monitor).
/// Control-plane calls are serialized on background threads; the audio path never locks or allocates.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int CaptureBufferMs = 10;
    private const int RenderBufferMs = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly DeviceService _devices;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly object _stateLock = new();
    private EngineConfig? _desired;
    private Pipeline? _pipeline;
    private int _rebuildRequested;
    private volatile float _masterVolume = 1f;
    private volatile float _inputLevel;
    private bool _disposed;

    public AudioEngine(DeviceService devices)
    {
        _devices = devices;
        _devices.DevicesChanged += OnDevicesChanged;
    }

    public event EventHandler? StateChanged;

    public EngineState State { get; private set; } = EngineState.Stopped;

    public string StatusMessage { get; private set; } = "Stopped";

    /// <summary>Peak input level 0..1 with ~250 ms decay, for metering.</summary>
    public float InputLevel => _inputLevel;

    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 2f);
    }

    public void Start(EngineConfig config)
    {
        lock (_stateLock) _desired = config;
        RequestRebuild();
    }

    public void Stop()
    {
        lock (_stateLock) _desired = null;
        RequestRebuild();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        bool degraded = State is EngineState.Error or EngineState.Recovering
            || (State == EngineState.Running && _pipeline?.Degraded == true);
        if (degraded) RequestRebuild();
    }

    private void RequestRebuild()
    {
        Interlocked.Exchange(ref _rebuildRequested, 1);
        _ = Task.Run(RebuildLoopAsync);
    }

    private async Task RebuildLoopAsync()
    {
        if (!await _rebuildGate.WaitAsync(0)) return;
        try
        {
            while (Interlocked.Exchange(ref _rebuildRequested, 0) == 1 && !_disposed)
                Rebuild();
        }
        finally
        {
            _rebuildGate.Release();
        }
        if (Volatile.Read(ref _rebuildRequested) == 1 && !_disposed) _ = Task.Run(RebuildLoopAsync);
    }

    private void Rebuild()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        _inputLevel = 0;

        EngineConfig? config;
        lock (_stateLock) config = _desired;
        if (config == null)
        {
            SetState(EngineState.Stopped, "Stopped");
            return;
        }

        SetState(EngineState.Starting, "Starting…");
        try
        {
            _pipeline = Pipeline.Create(this, config, out string status);
            _pipeline.Start();
            SetState(EngineState.Running, status);
        }
        catch (Exception ex)
        {
            _pipeline?.Dispose();
            _pipeline = null;
            SetState(EngineState.Error, ex.Message);
            ScheduleRetry();
        }
    }

    private void ScheduleRetry() => _ = Task.Delay(RetryDelay).ContinueWith(_ =>
    {
        if (State is EngineState.Error or EngineState.Recovering) RequestRebuild();
    }, TaskScheduler.Default);

    private void OnStreamFaulted(WasapiStream stream, Exception ex)
    {
        SetState(EngineState.Recovering, "Audio device lost — reconnecting…");
        _ = Task.Delay(300).ContinueWith(_ => RequestRebuild(), TaskScheduler.Default);
    }

    private void SetState(EngineState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Audio thread: one captured mono block at the input device rate.</summary>
    private void ProcessCaptured(Span<float> block, Pipeline pipeline)
    {
        float peak = 0;
        foreach (float s in block) peak = Math.Max(peak, Math.Abs(s));
        float decay = MathF.Exp(-block.Length / (pipeline.InputRate * 0.25f));
        _inputLevel = Math.Max(Math.Min(peak, 1f), _inputLevel * decay);

        float volume = _masterVolume;
        for (int i = 0; i < block.Length; i++)
            block[i] = Math.Clamp(block[i] * volume, -1f, 1f);

        pipeline.Output.Push(block);
        pipeline.Monitor?.Push(block);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _devices.DevicesChanged -= OnDevicesChanged;
        Task.Run(() =>
        {
            _rebuildGate.Wait();
            try
            {
                _pipeline?.Dispose();
                _pipeline = null;
            }
            finally
            {
                _rebuildGate.Release();
            }
        }).GetAwaiter().GetResult();
    }

    private sealed class Pipeline : IDisposable
    {
        private WasapiCaptureStream? _capture;
        private WasapiRenderStream? _output;
        private WasapiRenderStream? _monitor;

        public bool Degraded { get; private set; }
        public int InputRate { get; private set; }
        public OutputSink Output { get; private set; } = null!;
        public OutputSink? Monitor { get; private set; }

        public static Pipeline Create(AudioEngine engine, EngineConfig config, out string status)
        {
            var devices = engine._devices;
            var notes = new List<string>();
            var pipeline = new Pipeline();
            try
            {
                var input = devices.TryGetActive(config.InputDeviceId);
                if (input == null)
                {
                    input = devices.TryGetDefault(DataFlow.Capture)
                        ?? throw new InvalidOperationException("No microphone found.");
                    if (config.InputDeviceId != null) notes.Add("selected mic missing, using default");
                }

                var output = devices.TryGetActive(config.OutputDeviceId) ?? devices.TryFindCableInput();
                if (output == null)
                {
                    input.Dispose();
                    throw new InvalidOperationException(
                        "VB-Audio Virtual Cable not found. Install it from https://vb-audio.com/Cable/");
                }
                if (config.OutputDeviceId != null && output.ID != config.OutputDeviceId)
                    notes.Add("selected output missing, using CABLE Input");

                string inputName = input.FriendlyName;
                string outputName = output.FriendlyName;
                if (IsMuted(output)) notes.Add($"{outputName} is muted in Windows Sound settings");
                if (IsMuted(input)) notes.Add($"{inputName} is muted in Windows Sound settings");
                pipeline._capture = new WasapiCaptureStream(input, CaptureBufferMs, block => engine.ProcessCaptured(block, pipeline));
                pipeline.InputRate = pipeline._capture.Format.SampleRate;
                pipeline._output = new WasapiRenderStream(output, RenderBufferMs, mono => pipeline.Output.Pull(mono));
                pipeline.Output = CreateSink(pipeline._capture, pipeline._output);

                if (config.MonitorEnabled)
                {
                    var monitor = devices.TryGetActive(config.MonitorDeviceId) ?? devices.TryGetDefault(DataFlow.Render);
                    if (monitor == null)
                    {
                        notes.Add("no monitor device");
                    }
                    else
                    {
                        try
                        {
                            var monitorStream = new WasapiRenderStream(monitor, RenderBufferMs, mono => pipeline.Monitor!.Pull(mono));
                            pipeline.Monitor = CreateSink(pipeline._capture, monitorStream);
                            pipeline._monitor = monitorStream;
                        }
                        catch (Exception ex)
                        {
                            monitor.Dispose();
                            notes.Add($"monitor unavailable: {ex.Message}");
                        }
                    }
                }

                foreach (var stream in pipeline.Streams()) stream.Faulted += engine.OnStreamFaulted;

                pipeline.Degraded = notes.Count > 0;
                status = $"{inputName} → {outputName}" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "");
                return pipeline;
            }
            catch
            {
                pipeline.Dispose();
                throw;
            }
        }

        private static bool IsMuted(MMDevice device)
        {
            try { return device.AudioEndpointVolume.Mute; }
            catch (Exception) { return false; }
        }

        private static OutputSink CreateSink(WasapiCaptureStream capture, WasapiRenderStream render)
        {
            int inRate = capture.Format.SampleRate;
            int outRate = render.Format.SampleRate;
            int captureFramesAtOut = (int)((long)capture.BufferFrames * outRate / inRate);
            int period = Math.Max(captureFramesAtOut, render.BufferFrames);
            return new OutputSink(inRate, outRate, capture.BufferFrames,
                targetFillFrames: period * 3 / 2, maxFillFrames: period * 6);
        }

        public void Start()
        {
            _output?.Start();
            _monitor?.Start();
            _capture?.Start();
        }

        private IEnumerable<WasapiStream> Streams()
        {
            if (_capture != null) yield return _capture;
            if (_output != null) yield return _output;
            if (_monitor != null) yield return _monitor;
        }

        public void Dispose()
        {
            foreach (var stream in Streams()) stream.Dispose();
        }
    }
}
