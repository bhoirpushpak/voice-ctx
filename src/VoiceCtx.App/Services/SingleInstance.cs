namespace VoiceCtx.App.Services;

/// <summary>
/// Keeps one running copy per user. A second launch signals the first one (which shows its window)
/// and exits, so the hotkey and audio devices are never fought over.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Name = "VoiceCtx.SingleInstance";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{Name}", out bool createdNew);
        IsFirst = createdNew;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Name}.Activate");
    }

    public bool IsFirst { get; }

    public void SignalFirstInstance() => _activate.Set();

    public void ListenForActivation(Action onActivate) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
