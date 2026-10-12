using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoiceCtx.Audio.Engine;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates audio endpoints and reports device changes. All COM work happens on MTA
/// thread-pool threads so callers (including the WPF STA thread) never host audio COM objects.
/// </summary>
public sealed class DeviceService : IDisposable
{
    public const string CableInputName = "CABLE Input";

    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notifications;

    public DeviceService()
    {
        _enumerator = Task.Run(() => new MMDeviceEnumerator()).GetAwaiter().GetResult();
        _notifications = new NotificationClient(this);
        Task.Run(() => _enumerator.RegisterEndpointNotificationCallback(_notifications)).GetAwaiter().GetResult();
    }

    /// <summary>Raised on a background thread when devices are added, removed or change state/default.</summary>
    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioDeviceInfo>> GetInputDevicesAsync() => Task.Run(() => List(DataFlow.Capture));

    public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync() => Task.Run(() => List(DataFlow.Render));

    public static AudioDeviceInfo? FindCableInput(IEnumerable<AudioDeviceInfo> outputs) =>
        outputs.FirstOrDefault(d => d.Name.Contains(CableInputName, StringComparison.OrdinalIgnoreCase));

    /// <summary>MTA only. Returns the active device with this id, or null.</summary>
    internal MMDevice? TryGetActive(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var device = _enumerator.GetDevice(id);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
        }
        catch (Exception) { /* device no longer exists */ }
        return null;
    }

    /// <summary>MTA only.</summary>
    internal MMDevice? TryGetDefault(DataFlow flow)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, Role.Console)
                ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Console)
                : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>MTA only.</summary>
    internal MMDevice? TryFindCableInput()
    {
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(CableInputName, StringComparison.OrdinalIgnoreCase)) return device;
            device.Dispose();
        }
        return null;
    }

    private IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Dispose()
    {
        Task.Run(() =>
        {
            _enumerator.UnregisterEndpointNotificationCallback(_notifications);
            _enumerator.Dispose();
        }).GetAwaiter().GetResult();
    }

    private void RaiseChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    private sealed class NotificationClient(DeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.RaiseChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => owner.RaiseChanged();
        public void OnDeviceRemoved(string deviceId) => owner.RaiseChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => owner.RaiseChanged();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
