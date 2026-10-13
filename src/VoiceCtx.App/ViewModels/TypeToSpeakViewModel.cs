using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.Audio.Speech;

namespace VoiceCtx.App.ViewModels;

/// <summary>"Type to speak": text is spoken by a Windows voice into the live mic path.</summary>
public sealed class TypeToSpeakViewModel : ObservableObject, IDisposable
{
    private const int MaxHistory = 20;

    private readonly SpeechInjector _injector;
    private readonly WindowsTts _tts = new();
    private readonly DispatcherTimer _statusTimer;
    private TtsVoice? _selectedVoice;
    private string _text = "";
    private double _rate = 1.0;
    private bool _muteMic = true;
    private string _status = "";
    private bool _isSpeaking;
    private int _pending;

    public TypeToSpeakViewModel(SpeechInjector injector)
    {
        _injector = injector;
        SpeakCommand = new RelayCommand(() => _ = SpeakAsync(Text, clearText: true));
        StopCommand = new RelayCommand(Stop);
        OpenSpeechSettingsCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }));
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            (_, _) => IsSpeaking = _injector.IsSpeaking || _pending > 0, Dispatcher.CurrentDispatcher);
    }

    public event EventHandler? SettingsChanged;

    public ObservableCollection<TtsVoice> Voices { get; } = [];
    public ObservableCollection<SpokenLine> History { get; } = [];
    public ICommand SpeakCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenSpeechSettingsCommand { get; }

    public TtsVoice? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (SetField(ref _selectedVoice, value)) SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    /// <summary>Speaking rate (0.5–2.0).</summary>
    public double Rate
    {
        get => _rate;
        set
        {
            if (!SetField(ref _rate, Math.Round(Math.Clamp(value, 0.5, 2.0), 1))) return;
            OnPropertyChanged(nameof(RateText));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string RateText => $"{Rate:0.0}×";

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

    public string? SelectedVoiceId => SelectedVoice?.Id;

    public void Initialize(string? voiceId, double rate, bool muteMic)
    {
        Voices.Clear();
        foreach (var v in WindowsTts.InstalledVoices()) Voices.Add(v);
        _selectedVoice = Voices.FirstOrDefault(v => v.Id == voiceId)
            ?? Voices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? Voices.FirstOrDefault();
        _rate = Math.Clamp(rate <= 0 ? 1.0 : rate, 0.5, 2.0);
        _muteMic = muteMic;
        _injector.DuckGain = muteMic ? 0f : 1f;
        foreach (var name in new[] { nameof(SelectedVoice), nameof(Rate), nameof(RateText), nameof(MuteMic) }) OnPropertyChanged(name);
        if (Voices.Count == 0) Status = "No Windows voices installed. Add one in Settings → Time & language → Speech.";
        _statusTimer.Start();
    }

    /// <summary>Speaks text with the selected voice (also used to replay history).</summary>
    public async Task SpeakAsync(string text, bool clearText)
    {
        text = text.Trim();
        if (text.Length == 0 || SelectedVoice == null) return;
        if (clearText) Text = "";
        Interlocked.Increment(ref _pending);
        try
        {
            var clip = await _tts.SynthesizeAsync(text, SelectedVoice.Id, Rate);
            _injector.Enqueue(clip);
            if (History.FirstOrDefault()?.Text != text)
            {
                History.Insert(0, new SpokenLine(text, this));
                while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
            }
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

    public void Dispose() => _statusTimer.Stop();
}

/// <summary>A previously spoken line that can be replayed.</summary>
public sealed class SpokenLine(string text, TypeToSpeakViewModel owner)
{
    public string Text { get; } = text;
    public ICommand ReplayCommand { get; } = new RelayCommand(() => _ = owner.SpeakAsync(text, clearText: false));
}
