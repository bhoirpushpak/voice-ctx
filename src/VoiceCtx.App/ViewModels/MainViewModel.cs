using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.App.Services;
using VoiceCtx.Audio.Effects;
using VoiceCtx.Audio.Engine;
using VoiceCtx.Audio.Enhancement;

namespace VoiceCtx.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const string CableDownloadUrl = "https://vb-audio.com/Cable/";
    private static readonly AudioDeviceInfo SystemDefault = new("", "System default");

    private readonly DeviceService _devices;
    private readonly AudioEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer;

    private string? _preferredInputId;
    private string? _preferredOutputId;
    private string? _preferredMonitorId;
    private AudioDeviceInfo? _selectedInput;
    private AudioDeviceInfo? _selectedOutput;
    private AudioDeviceInfo? _selectedMonitor;
    private bool _monitorEnabled;
    private bool _populating;
    private bool _cableMissing;
    private double _inputLevel;
    private string _status = "Starting…";
    private EngineState _engineState;
    private string? _hotkeyWarning;

    public MainViewModel(DeviceService devices, AudioEngine engine, AiVoiceViewModel aiVoice, TypeToSpeakViewModel typeToSpeak, LiveTranslateViewModel liveTranslate)
    {
        _devices = devices;
        _engine = engine;
        AiVoice = aiVoice;
        AiVoice.SettingsChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        TypeToSpeak = typeToSpeak;
        TypeToSpeak.SettingsChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        LiveTranslate = liveTranslate;
        LiveTranslate.SettingsChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        _dispatcher = Dispatcher.CurrentDispatcher;
        Effects = EffectOption.CreateCatalog(SelectEffect);
        OpenCableSiteCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo(CableDownloadUrl) { UseShellExecute = true }));

        _engine.StateChanged += (_, _) => _dispatcher.BeginInvoke(UpdateStatus);
        _devices.DevicesChanged += (_, _) => _dispatcher.BeginInvoke(async () => await RefreshDevicesAsync());
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
            (_, _) => InputLevel = ToMeter(_engine.InputLevel), _dispatcher);
    }

    /// <summary>Raised whenever a user-facing setting changes (used for persistence).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The separate AI Voice feature (its own tab).</summary>
    public AiVoiceViewModel AiVoice { get; }

    /// <summary>Feature 3: type text and have it spoken into the mic path.</summary>
    public TypeToSpeakViewModel TypeToSpeak { get; }

    /// <summary>Feature 4: speak in one language, be heard in another.</summary>
    public LiveTranslateViewModel LiveTranslate { get; }

    public ObservableCollection<AudioDeviceInfo> Inputs { get; } = [];
    public ObservableCollection<AudioDeviceInfo> Outputs { get; } = [];
    public ObservableCollection<AudioDeviceInfo> Monitors { get; } = [];
    public IReadOnlyList<EffectOption> Effects { get; }
    public ICommand OpenCableSiteCommand { get; }
    public string CableDownloadLink => CableDownloadUrl;

    public AudioDeviceInfo? SelectedInput
    {
        get => _selectedInput;
        set => SelectDevice(ref _selectedInput, value, ref _preferredInputId);
    }

    public AudioDeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set => SelectDevice(ref _selectedOutput, value, ref _preferredOutputId);
    }

    public AudioDeviceInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set => SelectDevice(ref _selectedMonitor, value, ref _preferredMonitorId);
    }

    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (!SetField(ref _monitorEnabled, value)) return;
            RestartEngine();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsOn
    {
        get => _engine.Effects.Enabled;
        set
        {
            if (_engine.Effects.Enabled == value) return;
            _engine.Effects.Enabled = value;
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public EffectKind SelectedEffect => _engine.Effects.Effect;

    public double CustomSemitones
    {
        get => _engine.Effects.CustomSemitones;
        set
        {
            _engine.Effects.CustomSemitones = (float)Math.Round(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomSemitonesText));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string CustomSemitonesText => $"{CustomSemitones:+0;−0;0} st";

    /// <summary>Master volume in percent (0–200).</summary>
    public double MasterVolume
    {
        get => Math.Round(_engine.MasterVolume * 100);
        set
        {
            _engine.MasterVolume = (float)(value / 100);
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Effect dry/wet in percent (0 = dry, 100 = fully processed).</summary>
    public double Mix
    {
        get => Math.Round(_engine.Effects.Mix * 100);
        set
        {
            _engine.Effects.Mix = (float)(value / 100);
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool NoiseCancelSupported { get; } = MicEnhancer.NoiseCancellationSupported;

    public bool NoiseCancel
    {
        get => _engine.Enhancement.NoiseCancel;
        set
        {
            if (_engine.Enhancement.NoiseCancel == value || (value && !NoiseCancelSupported)) return;
            _engine.Enhancement.NoiseCancel = value;
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Noise reduction amount in percent.</summary>
    public double NoiseReduction
    {
        get => Math.Round(_engine.Enhancement.NoiseReduction * 100);
        set
        {
            _engine.Enhancement.NoiseReduction = (float)(value / 100);
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool StudioSound
    {
        get => _engine.Enhancement.Studio;
        set
        {
            if (_engine.Enhancement.Studio == value) return;
            _engine.Enhancement.Studio = value;
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Studio chain intensity in percent.</summary>
    public double StudioStrength
    {
        get => Math.Round(_engine.Enhancement.StudioStrength * 100);
        set
        {
            _engine.Enhancement.StudioStrength = (float)(value / 100);
            OnPropertyChanged();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ToggleNoiseCancel() => NoiseCancel = !NoiseCancel;

    public double InputLevel
    {
        get => _inputLevel;
        private set => SetField(ref _inputLevel, value);
    }

    public bool CableMissing
    {
        get => _cableMissing;
        private set => SetField(ref _cableMissing, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public EngineState EngineState
    {
        get => _engineState;
        private set => SetField(ref _engineState, value);
    }

    public string? PreferredInputId => _preferredInputId;
    public string? PreferredOutputId => _preferredOutputId;
    public string? PreferredMonitorId => _preferredMonitorId;

    public string EffectDisplayName => Effects.First(e => e.Kind == SelectedEffect).Title;

    /// <summary>Applies saved settings, loads devices and starts audio.</summary>
    public async Task InitializeAsync(AppSettings settings)
    {
        _preferredInputId = settings.InputDeviceId;
        _preferredOutputId = settings.OutputDeviceId;
        _preferredMonitorId = settings.MonitorDeviceId;
        _monitorEnabled = settings.MonitorEnabled;
        _engine.Effects.Enabled = settings.IsOn;
        _engine.Effects.CustomSemitones = (float)settings.CustomSemitones;
        _engine.Effects.Mix = (float)(settings.Mix / 100);
        _engine.MasterVolume = (float)(settings.MasterVolume / 100);
        _engine.Enhancement.NoiseCancel = settings.NoiseCancel && NoiseCancelSupported;
        _engine.Enhancement.NoiseReduction = (float)(settings.NoiseReduction / 100);
        _engine.Enhancement.Studio = settings.StudioSound;
        _engine.Enhancement.StudioStrength = (float)(settings.StudioStrength / 100);
        ApplyEffect(settings.Effect);
        foreach (var name in new[] { nameof(MonitorEnabled), nameof(IsOn), nameof(CustomSemitones), nameof(CustomSemitonesText), nameof(Mix), nameof(MasterVolume),
                     nameof(NoiseCancel), nameof(NoiseReduction), nameof(StudioSound), nameof(StudioStrength) })
            OnPropertyChanged(name);

        AiVoice.Initialize(settings.AiVoiceEnabled, settings.AiVoiceId, settings.AiVoicePitchShift);
        TypeToSpeak.Initialize(settings.TtsVoiceId, settings.TtsRate, settings.TtsMuteMic);
        LiveTranslate.Initialize(settings.TranslateEnabled, settings.TranslateFrom, settings.TranslateTo, settings.TranslateVoiceId, settings.TranslateKeepOriginal);

        await RefreshDevicesAsync();
        _meterTimer.Start();
        RestartEngine();
    }

    public AppSettings ToSettings() => new()
    {
        InputDeviceId = _preferredInputId,
        OutputDeviceId = _preferredOutputId,
        MonitorDeviceId = _preferredMonitorId,
        MonitorEnabled = _monitorEnabled,
        Effect = _engine.Effects.Effect,
        IsOn = _engine.Effects.Enabled,
        CustomSemitones = _engine.Effects.CustomSemitones,
        MasterVolume = MasterVolume,
        Mix = Mix,
        NoiseCancel = NoiseCancel,
        NoiseReduction = NoiseReduction,
        StudioSound = StudioSound,
        StudioStrength = StudioStrength,
        AiVoiceEnabled = AiVoice.IsEnabled,
        AiVoiceId = AiVoice.SelectedVoiceId,
        AiVoicePitchShift = AiVoice.PitchShift,
        TtsVoiceId = TypeToSpeak.SelectedVoiceId,
        TtsRate = TypeToSpeak.Rate,
        TtsMuteMic = TypeToSpeak.MuteMic,
        TranslateEnabled = LiveTranslate.IsEnabled,
        TranslateFrom = LiveTranslate.FromCode,
        TranslateTo = LiveTranslate.ToCode,
        TranslateVoiceId = LiveTranslate.VoiceId,
        TranslateKeepOriginal = LiveTranslate.KeepOriginal,
    };

    /// <summary>Shown in the status line when the global hotkey could not be registered.</summary>
    public string? HotkeyWarning
    {
        get => _hotkeyWarning;
        set => SetField(ref _hotkeyWarning, value);
    }

    public void Toggle() => IsOn = !IsOn;

    public void ApplyEffect(EffectKind kind)
    {
        _engine.Effects.Effect = kind;
        foreach (var option in Effects) option.SetSelected(option.Kind == kind);
        OnPropertyChanged(nameof(SelectedEffect));
        OnPropertyChanged(nameof(EffectDisplayName));
    }

    private void SelectEffect(EffectKind kind)
    {
        ApplyEffect(kind);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectDevice(ref AudioDeviceInfo? field, AudioDeviceInfo? value, ref string? preferredId, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name) || _populating || value == null) return;
        preferredId = value.Id;
        RestartEngine();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshDevicesAsync()
    {
        var inputs = await _devices.GetInputDevicesAsync();
        var outputs = await _devices.GetOutputDevicesAsync();

        _populating = true;
        try
        {
            Replace(Inputs, [SystemDefault, .. inputs]);
            Replace(Outputs, outputs);
            Replace(Monitors, [SystemDefault, .. outputs.Where(d => !d.Name.Contains(DeviceService.CableInputName, StringComparison.OrdinalIgnoreCase))]);

            var cable = DeviceService.FindCableInput(outputs);
            CableMissing = cable == null;

            SelectedInput = Inputs.FirstOrDefault(d => d.Id == (_preferredInputId ?? "")) ?? SystemDefault;
            SelectedOutput = Outputs.FirstOrDefault(d => d.Id == _preferredOutputId) ?? cable;
            SelectedMonitor = Monitors.FirstOrDefault(d => d.Id == (_preferredMonitorId ?? "")) ?? SystemDefault;
        }
        finally
        {
            _populating = false;
        }
    }

    private static void Replace(ObservableCollection<AudioDeviceInfo> target, IReadOnlyList<AudioDeviceInfo> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void RestartEngine()
    {
        if (_populating) return;
        _engine.Start(new EngineConfig(
            NullIfDefault(_preferredInputId),
            _preferredOutputId,
            NullIfDefault(_preferredMonitorId),
            _monitorEnabled));
    }

    private static string? NullIfDefault(string? id) => string.IsNullOrEmpty(id) ? null : id;

    private void UpdateStatus()
    {
        EngineState = _engine.State;
        Status = _engine.StatusMessage;
    }

    /// <summary>Maps a linear peak to a 60 dB meter scale.</summary>
    private static double ToMeter(float peak) =>
        Math.Clamp((20 * Math.Log10(Math.Max(peak, 1e-6)) + 60) / 60, 0, 1);

    public void Dispose()
    {
        _meterTimer.Stop();
        AiVoice.Dispose();
        TypeToSpeak.Dispose();
        LiveTranslate.Dispose();
    }
}
