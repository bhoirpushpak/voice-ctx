using NAudio.CoreAudioApi;

namespace VoiceCtx.Audio.Engine;

/// <summary>
/// Event-driven WASAPI shared-mode stream running its own MMCSS thread. Subclasses service the
/// buffer in <see cref="Service"/>, which must not allocate or lock.
/// </summary>
internal abstract class WasapiStream : IDisposable
{
    private const long HundredNsPerMs = 10_000;

    private readonly MMDevice _device;
    private readonly EventWaitHandle _bufferEvent = new(false, EventResetMode.AutoReset);
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    protected WasapiStream(MMDevice device, int bufferMilliseconds)
    {
        _device = device;
        Client = device.AudioClient;
        Format = StreamFormat.From(Client.MixFormat);
        Client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
            bufferMilliseconds * HundredNsPerMs, 0, Client.MixFormat, Guid.Empty);
        Client.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
        BufferFrames = Client.BufferSize;
    }

    /// <summary>Raised on the audio thread when the stream fails (for example the device was unplugged).</summary>
    public event Action<WasapiStream, Exception>? Faulted;

    public StreamFormat Format { get; }

    public int BufferFrames { get; }

    public string DeviceId => _device.ID;

    protected AudioClient Client { get; }

    public void Start()
    {
        BeforeStart();
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = $"VoiceCtx {GetType().Name}" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        Client.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _bufferEvent.Set();
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
        try { Client.Stop(); } catch { /* device may already be gone */ }
    }

    protected virtual void BeforeStart() { }

    protected abstract void Service();

    private void Run()
    {
        IntPtr mmcss = RealtimeThread.Enter();
        try
        {
            while (_running)
            {
                _bufferEvent.WaitOne(200);
                if (!_running) break;
                Service();
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                _running = false;
                Faulted?.Invoke(this, ex);
            }
        }
        finally
        {
            RealtimeThread.Leave(mmcss);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { Client.Dispose(); } catch { /* device may already be gone */ }
        _device.Dispose();
        _bufferEvent.Dispose();
    }
}

/// <summary>Captures from a device and delivers mono float blocks to a callback on the audio thread.</summary>
internal sealed unsafe class WasapiCaptureStream : WasapiStream
{
    public delegate void BlockHandler(Span<float> mono);

    private readonly AudioCaptureClient _captureClient;
    private readonly BlockHandler _handler;
    private readonly float[] _mono;

    public WasapiCaptureStream(MMDevice device, int bufferMilliseconds, BlockHandler handler)
        : base(device, bufferMilliseconds)
    {
        _captureClient = Client.AudioCaptureClient;
        _handler = handler;
        _mono = new float[BufferFrames];
    }

    protected override void Service()
    {
        while (_captureClient.GetNextPacketSize() > 0)
        {
            IntPtr data = _captureClient.GetBuffer(out int frames, out AudioClientBufferFlags flags);
            frames = Math.Min(frames, _mono.Length);
            var mono = _mono.AsSpan(0, frames);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                mono.Clear();
            else
                SampleConverter.ToMono(new ReadOnlySpan<byte>((void*)data, frames * Format.BytesPerFrame), Format, mono);
            _captureClient.ReleaseBuffer(frames);
            _handler(mono);
        }
    }
}

/// <summary>Renders mono float supplied by a callback on the audio thread to every device channel.</summary>
internal sealed unsafe class WasapiRenderStream : WasapiStream
{
    public delegate void FillHandler(Span<float> mono);

    private readonly AudioRenderClient _renderClient;
    private readonly FillHandler _fill;
    private readonly float[] _mono;

    public WasapiRenderStream(MMDevice device, int bufferMilliseconds, FillHandler fill)
        : base(device, bufferMilliseconds)
    {
        _renderClient = Client.AudioRenderClient;
        _fill = fill;
        _mono = new float[BufferFrames];
    }

    protected override void BeforeStart()
    {
        _renderClient.GetBuffer(BufferFrames);
        _renderClient.ReleaseBuffer(BufferFrames, AudioClientBufferFlags.Silent);
    }

    protected override void Service()
    {
        int frames = BufferFrames - Client.CurrentPadding;
        if (frames <= 0) return;
        var mono = _mono.AsSpan(0, frames);
        _fill(mono);
        IntPtr data = _renderClient.GetBuffer(frames);
        SampleConverter.FromMono(mono, Format, new Span<byte>((void*)data, frames * Format.BytesPerFrame));
        _renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }
}
