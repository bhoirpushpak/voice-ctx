using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.Audio.Speech;
using VoiceCtx.Audio.Translate;

namespace VoiceCtx.App.ViewModels;

/// <summary>A subtitle line shown in the Live translate tab.</summary>
public sealed record SubtitleLine(string Original, string Translation, string Languages, string Time);

/// <summary>Feature 4: speak → transcribe → translate → your chosen voice (Pocket TTS) → cable.</summary>
public sealed class LiveTranslateViewModel : ObservableObject, IDisposable
{
    private const int MaxSubtitles = 50;

    private readonly LiveTranslateService _service;
    private readonly SpeechService _speech;
    private readonly Dispatcher _dispatcher;
    private Language _from = Languages.Auto;
    private Language _to = Languages.All[0];
    private bool _keepOriginal;
    private string _status = "";
    private bool _busy;

    public LiveTranslateViewModel(LiveTranslateService service, SpeechService speech, SpeechVoicesViewModel voices)
    {
        _service = service;
        _speech = speech;
        Voices = voices;
        Voices.VoiceChanged += (_, _) => _service.Options.VoiceId = Voices.Selected?.Id;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service.StatusChanged += (_, _) => _dispatcher.BeginInvoke(UpdateStatus);
        _service.LineTranslated += (_, line) => _dispatcher.BeginInvoke(() => AddSubtitle(line));
        OpenCreditsCommand = new RelayCommand(() =>
        {
            if (File.Exists(_service.Library.CreditsPath))
                Process.Start(new ProcessStartInfo(_service.Library.CreditsPath) { UseShellExecute = true });
        });
        ClearCommand = new RelayCommand(() => Subtitles.Clear());
    }

    public event EventHandler? SettingsChanged;

    public IReadOnlyList<Language> SourceLanguages { get; } = [Languages.Auto, .. Languages.All];

    /// <summary>Languages translations can be spoken in (those with a speech model installed).</summary>
    public IReadOnlyList<Language> TargetLanguages =>
        Languages.All.Where(l => _speech.FindLanguage(l.Whisper) != null).ToList() is { Count: > 0 } spoken ? spoken : Languages.All;

    public SpeechVoicesViewModel Voices { get; }
    public ObservableCollection<SubtitleLine> Subtitles { get; } = [];
    public ICommand OpenCreditsCommand { get; }
    public ICommand ClearCommand { get; }
    public string ModelFolder => _service.Library.Root;
    public bool ModelsInstalled => _service.Library.Installed;

    public bool IsEnabled
    {
        get => _service.Enabled;
        set
        {
            if (value == _service.Enabled || _busy) return;
            _ = SetEnabledAsync(value);
        }
    }

    public Language From
    {
        get => _from;
        set
        {
            if (!SetField(ref _from, value ?? Languages.Auto)) return;
            _service.Options.From = _from.Whisper;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Language To
    {
        get => _to;
        set
        {
            if (!SetField(ref _to, value ?? Languages.All[0])) return;
            _service.Options.To = _to.Whisper;
            OnPropertyChanged(nameof(NoVoiceForTarget));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when translations into the target language can only be shown as text.</summary>
    public bool NoVoiceForTarget => _speech.FindLanguage(_to.Whisper) == null;

    /// <summary>Keep your real voice quietly under the translation instead of muting it.</summary>
    public bool KeepOriginal
    {
        get => _keepOriginal;
        set
        {
            if (!SetField(ref _keepOriginal, value)) return;
            _service.Options.KeepOriginal = value;
            _service.ApplyMicGain();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public TranslateStatus State => _service.Status;

    public string FromCode => _from.Whisper;
    public string ToCode => _to.Whisper;

    public void Initialize(bool enabled, string? from, string? to, bool keepOriginal)
    {
        _from = SourceLanguages.FirstOrDefault(l => l.Whisper == from) ?? Languages.Auto;
        _to = TargetLanguages.FirstOrDefault(l => l.Whisper == to) ?? TargetLanguages[0];
        _keepOriginal = keepOriginal;
        _service.Options.From = _from.Whisper;
        _service.Options.To = _to.Whisper;
        _service.Options.KeepOriginal = keepOriginal;
        _service.Options.VoiceId = Voices.Selected?.Id;
        foreach (var name in new[] { nameof(From), nameof(To), nameof(TargetLanguages), nameof(NoVoiceForTarget), nameof(KeepOriginal), nameof(ModelsInstalled) })
            OnPropertyChanged(name);
        UpdateStatus();
        if (enabled && ModelsInstalled) _ = SetEnabledAsync(true);
    }

    public void Toggle()
    {
        if (ModelsInstalled) IsEnabled = !IsEnabled;
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        _busy = true;
        try
        {
            await _service.SetEnabledAsync(enabled);
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(IsEnabled));
            UpdateStatus();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddSubtitle(TranslatedLine line)
    {
        Subtitles.Insert(0, new SubtitleLine(line.Original, line.Translation,
            $"{line.FromLanguage} → {line.ToLanguage} · {line.Elapsed.TotalSeconds:0.0} s", DateTime.Now.ToString("HH:mm:ss")));
        while (Subtitles.Count > MaxSubtitles) Subtitles.RemoveAt(Subtitles.Count - 1);
    }

    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsEnabled));
        Status = _service.StatusMessage;
    }

    public void Dispose() { }
}
