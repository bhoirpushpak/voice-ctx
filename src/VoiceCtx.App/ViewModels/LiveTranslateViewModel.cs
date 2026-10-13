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

/// <summary>Feature 4: speak → transcribe → translate → Windows voice → (AI Voice) → cable.</summary>
public sealed class LiveTranslateViewModel : ObservableObject, IDisposable
{
    private const int MaxSubtitles = 50;

    private readonly LiveTranslateService _service;
    private readonly Dispatcher _dispatcher;
    private Language _from = Languages.Auto;
    private Language _to = Languages.All[0];
    private TtsVoice? _voice;
    private bool _keepOriginal;
    private string _status = "";
    private bool _busy;

    public LiveTranslateViewModel(LiveTranslateService service)
    {
        _service = service;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service.StatusChanged += (_, _) => _dispatcher.BeginInvoke(UpdateStatus);
        _service.LineTranslated += (_, line) => _dispatcher.BeginInvoke(() => AddSubtitle(line));
        OpenSpeechSettingsCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }));
        OpenCreditsCommand = new RelayCommand(() =>
        {
            if (File.Exists(_service.Library.CreditsPath))
                Process.Start(new ProcessStartInfo(_service.Library.CreditsPath) { UseShellExecute = true });
        });
        ClearCommand = new RelayCommand(() => Subtitles.Clear());
    }

    public event EventHandler? SettingsChanged;

    public IReadOnlyList<Language> SourceLanguages { get; } = [Languages.Auto, .. Languages.All];
    public IReadOnlyList<Language> TargetLanguages { get; } = Languages.All;
    public ObservableCollection<TtsVoice> Voices { get; } = [];
    public ObservableCollection<SubtitleLine> Subtitles { get; } = [];
    public ICommand OpenSpeechSettingsCommand { get; }
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
            RefreshVoices(null);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Windows voice that speaks the translation (filtered to the target language).</summary>
    public TtsVoice? Voice
    {
        get => _voice;
        set
        {
            if (!SetField(ref _voice, value)) return;
            _service.Options.VoiceId = value?.Id;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool NoVoiceForTarget => Voices.Count == 0;

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
    public string? VoiceId => _voice?.Id;

    public void Initialize(bool enabled, string? from, string? to, string? voiceId, bool keepOriginal)
    {
        _from = SourceLanguages.FirstOrDefault(l => l.Whisper == from) ?? Languages.Auto;
        _to = TargetLanguages.FirstOrDefault(l => l.Whisper == to) ?? Languages.All[0];
        _keepOriginal = keepOriginal;
        _service.Options.From = _from.Whisper;
        _service.Options.To = _to.Whisper;
        _service.Options.KeepOriginal = keepOriginal;
        RefreshVoices(voiceId);
        foreach (var name in new[] { nameof(From), nameof(To), nameof(KeepOriginal), nameof(ModelsInstalled) }) OnPropertyChanged(name);
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

    private void RefreshVoices(string? preferredId)
    {
        string primary = _to.Voice.Split('-')[0];
        var matching = WindowsTts.InstalledVoices()
            .Where(v => v.Language.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Voices.Clear();
        foreach (var v in matching) Voices.Add(v);
        _voice = Voices.FirstOrDefault(v => v.Id == preferredId)
            ?? Voices.FirstOrDefault(v => v.Language.Equals(_to.Voice, StringComparison.OrdinalIgnoreCase))
            ?? Voices.FirstOrDefault();
        _service.Options.VoiceId = _voice?.Id;
        OnPropertyChanged(nameof(Voice));
        OnPropertyChanged(nameof(NoVoiceForTarget));
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
