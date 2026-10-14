using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.Audio.Speech;

namespace VoiceCtx.App.ViewModels;

/// <summary>"Type to speak": text is spoken in your chosen voice (Pocket TTS) into the live mic path.</summary>
public sealed class TypeToSpeakViewModel : ObservableObject, IDisposable
{
    private const int MaxHistory = 20;

    private readonly SpeechInjector _injector;
    private readonly SpeechService _speech;
    private readonly DispatcherTimer _statusTimer;
    private SpeechLanguage? _language;
    private string _text = "";
    private bool _muteMic = true;
    private string _status = "";
    private bool _isSpeaking;
    private int _pending;

    public TypeToSpeakViewModel(SpeechInjector injector, SpeechService speech, SpeechVoicesViewModel voices)
    {
        _injector = injector;
        _speech = speech;
        Voices = voices;
        Voices.VoiceChanged += (_, _) => WarmUp();
        SpeakCommand = new RelayCommand(() => _ = SpeakAsync(Text, clearText: true));
        StopCommand = new RelayCommand(Stop);
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            (_, _) => IsSpeaking = _injector.IsSpeaking, Dispatcher.CurrentDispatcher);
    }

    public event EventHandler? SettingsChanged;

    public SpeechVoicesViewModel Voices { get; }
    public ObservableCollection<SpeechLanguage> Languages { get; } = [];
    public ObservableCollection<SpokenLine> History { get; } = [];
    public ICommand SpeakCommand { get; }
    public ICommand StopCommand { get; }

    public SpeechLanguage? Language
    {
        get => _language;
        set
        {
            if (!SetField(ref _language, value)) return;
            WarmUp();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    /// <summary>Mute your real mic while a line is being spoken.</summary>
    public bool MuteMic
    {
        get => _muteMic;
        set
        {
            if (!SetField(ref _muteMic, value)) return;
            _injector.DuckGain = value ? 0f : 1f;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set
        {
            if (!SetField(ref _isSpeaking, value)) return;
            if (value) Status = "Speaking…";
            else if (Status == "Speaking…") Status = "";
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? LanguageCode => _language?.Code;

    public void Initialize(string? languageCode, bool muteMic)
    {
        Languages.Clear();
        foreach (var l in _speech.Languages) Languages.Add(l);
        _language = Languages.FirstOrDefault(l => l.Code == languageCode) ?? Languages.FirstOrDefault();
        _muteMic = muteMic;
        _injector.DuckGain = muteMic ? 0f : 1f;
        foreach (var name in new[] { nameof(Language), nameof(MuteMic) }) OnPropertyChanged(name);
        _statusTimer.Start();
    }

    /// <summary>Speaks text in the selected language and voice (also used to replay history).</summary>
    public async Task SpeakAsync(string text, bool clearText)
    {
        text = text.Trim();
        if (text.Length == 0 || _language == null) return;
        if (Voices.Selected == null)
        {
            Status = "Record your voice first (or pick a ready-made voice).";
            return;
        }
        if (clearText) Text = "";
        Interlocked.Increment(ref _pending);
        Status = "Preparing…";
        try
        {
            var clip = await _speech.SynthesizeAsync(text, _language.Code, Voices.Selected.Id);
            _injector.Enqueue(clip);
            if (History.FirstOrDefault()?.Text != text)
            {
                History.Insert(0, new SpokenLine(text, this));
                while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
            }
            if (Status == "Preparing…") Status = "";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't speak that line: {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    public void Stop()
    {
        _injector.Clear();
        Status = "";
    }

    private void WarmUp()
    {
        if (_language != null && Voices.Selected != null) _ = _speech.WarmUpAsync(_language.Code, Voices.Selected.Id);
    }

    public void Dispose() => _statusTimer.Stop();
}

/// <summary>A previously spoken line that can be replayed.</summary>
public sealed class SpokenLine(string text, TypeToSpeakViewModel owner)
{
    public string Text { get; } = text;
    public ICommand ReplayCommand { get; } = new RelayCommand(() => _ = owner.SpeakAsync(text, clearText: false));
}
