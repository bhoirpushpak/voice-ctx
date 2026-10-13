using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.Audio.AiVoice;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.App.ViewModels;

/// <summary>A selectable voice tile in the AI Voice tab.</summary>
public sealed class AiVoiceOption(VoiceInfo info, Action<AiVoiceOption> select) : ObservableObject
{
    private bool _isSelected;

    public VoiceInfo Info { get; } = info;
    public string Title => Info.Name;
    public string Subtitle => $"{Capitalize(Info.Gender)} · {(Info.BuiltIn ? "Built-in" : "Imported")}";
    public string Credits => Info.Credits;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value) && value) select(this);
        }
    }

    internal void SetSelected(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

public sealed class AiVoiceViewModel : ObservableObject, IDisposable
{
    public const string RightsNotice =
        "Only use voices you have the rights to. Don't use AI voices to impersonate real people or deceive anyone.";

    private readonly AiVoiceService _service;
    private readonly AudioEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private string? _preferredVoiceId;
    private AiVoiceOption? _selected;
    private string _status = "";
    private bool _fallingBehind;
    private string? _importError;

    public AiVoiceViewModel(AiVoiceService service, AudioEngine engine)
    {
        _service = service;
        _engine = engine;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service.StatusChanged += (_, _) => _dispatcher.BeginInvoke(UpdateStatus);
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus(), _dispatcher);
        RefreshCommand = new RelayCommand(Refresh);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenCreditsCommand = new RelayCommand(OpenCredits);
        ImportCommand = new RelayCommand(Import);
    }

    /// <summary>Raised when a persisted setting changes.</summary>
    public event EventHandler? SettingsChanged;

    public ObservableCollection<AiVoiceOption> Voices { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenCreditsCommand { get; }
    public ICommand ImportCommand { get; }
    public string Notice => RightsNotice;
    public string ModelFolder => _service.Library.Root;

    public bool ModelsInstalled => _service.Library.BaseModelsInstalled;
    public bool ShowInstallHelp => !ModelsInstalled || Voices.Count == 0;

    public bool IsEnabled
    {
        get => _engine.AiVoice.Enabled;
        set
        {
            if (_engine.AiVoice.Enabled == value) return;
            _engine.AiVoice.Enabled = value;
            OnPropertyChanged();
            if (value) LoadSelected();
            UpdateStatus();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedVoiceId => _selected?.Info.Id ?? _preferredVoiceId;

    public double PitchShift
    {
        get => _engine.AiVoice.PitchShift;
        set
        {
            _engine.AiVoice.PitchShift = (float)Math.Round(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PitchShiftText));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string PitchShiftText => $"{PitchShift:+0;−0;0} st";

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public AiVoiceStatus State => _service.Status;

    public bool FallingBehind
    {
        get => _fallingBehind;
        private set => SetField(ref _fallingBehind, value);
    }

    public string? ImportError
    {
        get => _importError;
        private set => SetField(ref _importError, value);
    }

    public void Initialize(bool enabled, string? voiceId, double pitchShift)
    {
        _preferredVoiceId = voiceId;
        _engine.AiVoice.PitchShift = (float)pitchShift;
        Refresh();
        _engine.AiVoice.Enabled = enabled && ModelsInstalled && Voices.Count > 0;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(PitchShift));
        OnPropertyChanged(nameof(PitchShiftText));
        if (IsEnabled) LoadSelected();
        _statusTimer.Start();
        UpdateStatus();
    }

    public void Toggle()
    {
        if (ModelsInstalled && Voices.Count > 0) IsEnabled = !IsEnabled;
    }

    private void Refresh()
    {
        var voices = _service.Library.ScanVoices();
        Voices.Clear();
        foreach (var v in voices) Voices.Add(new AiVoiceOption(v, Select));
        var preferred = Voices.FirstOrDefault(v => v.Info.Id == (_selected?.Info.Id ?? _preferredVoiceId)) ?? Voices.FirstOrDefault();
        _selected = preferred;
        foreach (var v in Voices) v.SetSelected(v == preferred);
        OnPropertyChanged(nameof(ModelsInstalled));
        OnPropertyChanged(nameof(ShowInstallHelp));
        if (IsEnabled) LoadSelected();
        UpdateStatus();
    }

    private void Select(AiVoiceOption option)
    {
        bool changed = _selected != option;
        _selected = option;
        foreach (var v in Voices) if (v != option) v.SetSelected(false);
        if (changed)
        {
            PitchShift = option.Info.DefaultPitchShift;
            if (IsEnabled) LoadSelected();
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadSelected()
    {
        if (_selected != null) _ = _service.LoadVoiceAsync(_selected.Info);
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import AI voice",
            Filter = "Voice CTX voice (*.onnx)|*.onnx",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var info = _service.Library.Import(dialog.FileName);
            ImportError = null;
            _preferredVoiceId = info.Id;
            _selected = null;
            Refresh();
            var imported = Voices.FirstOrDefault(v => v.Info.Id == info.Id);
            if (imported != null) imported.IsSelected = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ImportError = ex.Message;
        }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_service.Library.VoicesFolder);
        Process.Start(new ProcessStartInfo(_service.Library.VoicesFolder) { UseShellExecute = true });
    }

    private void OpenCredits()
    {
        if (File.Exists(_service.Library.CreditsPath))
            Process.Start(new ProcessStartInfo(_service.Library.CreditsPath) { UseShellExecute = true });
    }

    private string _heardAs = "";

    /// <summary>For Type to speak and Live translate: which voice people actually hear.</summary>
    public string HeardAs
    {
        get => _heardAs;
        private set => SetField(ref _heardAs, value);
    }

    private void UpdateStatus()
    {
        HeardAs = IsEnabled && _selected != null
            ? $"AI Voice is on, so this voice is converted and people hear it as “{_selected.Info.Name}”."
            : "AI Voice is off, so people hear this Windows voice. Turn on AI Voice to speak in your AI voice.";
        FallingBehind = IsEnabled && _engine.AiVoiceFallingBehind;
        OnPropertyChanged(nameof(State));
        if (!ModelsInstalled) { Status = "Models not installed"; return; }
        if (Voices.Count == 0) { Status = "No voices installed"; return; }
        if (!IsEnabled) { Status = "Off"; return; }

        string text = _service.StatusMessage;
        double latency = _engine.AiVoiceLatencyMs;
        if (_service.Status == AiVoiceStatus.Ready && latency > 0) text += $" · adds ~{latency:0} ms delay";
        if (FallingBehind) text = "Your PC can't keep up right now — using your normal voice for a moment";
        Status = text;
    }

    public void Dispose() => _statusTimer.Stop();
}
