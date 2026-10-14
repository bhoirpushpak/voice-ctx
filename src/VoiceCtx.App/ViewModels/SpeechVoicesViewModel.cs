using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VoiceCtx.Audio.Engine;
using VoiceCtx.Audio.Speech;

namespace VoiceCtx.App.ViewModels;

/// <summary>
/// The voice Type to speak and Live translate speak in (shared by both tabs): your own recording,
/// a ready-made voice or an imported clip, cloned by Pocket TTS.
/// </summary>
public sealed class SpeechVoicesViewModel : ObservableObject, IDisposable
{
    public const double MinRecordSeconds = 5, MaxRecordSeconds = 15;

    public const string RecordingScript =
        "Hi, this is my voice. I'm recording a short sample so Voice CTX can speak in my voice. " +
        "The quick brown fox jumps over the lazy dog, then runs all the way back home.";

    public const string ConsentNotice =
        "Only clone your own voice or a voice you have permission to use. Don't use cloned voices to impersonate or deceive anyone.";

    private readonly SpeechService _speech;
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _recordTimer;
    private SpeechVoice? _selected;
    private string? _preferredId;
    private bool _isRecording;
    private double _recordedSeconds;
    private int _recordRate;
    private string _message = "";

    public SpeechVoicesViewModel(SpeechService speech, AudioEngine engine)
    {
        _speech = speech;
        _engine = engine;
        _recordTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, OnRecordTick, Dispatcher.CurrentDispatcher);
        _recordTimer.Stop();
        RecordCommand = new RelayCommand(ToggleRecording);
        ImportCommand = new RelayCommand(Import);
        DeleteCommand = new RelayCommand(Delete);
        OpenCreditsCommand = new RelayCommand(() =>
        {
            if (File.Exists(_speech.Library.CreditsPath))
                Process.Start(new ProcessStartInfo(_speech.Library.CreditsPath) { UseShellExecute = true });
        });
    }

    public event EventHandler? SettingsChanged;

    /// <summary>Raised when the selected voice (or its recording) changes.</summary>
    public event EventHandler? VoiceChanged;

    public ObservableCollection<SpeechVoice> Voices { get; } = [];
    public ICommand RecordCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenCreditsCommand { get; }
    public string Script => RecordingScript;
    public string Consent => ConsentNotice;
    public bool ModelsInstalled => _speech.Installed;
    public string ModelFolder => _speech.Library.Root;

    public SpeechVoice? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(CanDelete));
            VoiceChanged?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedId => _selected?.Id ?? _preferredId;
    public bool CanDelete => _selected is { BuiltIn: false };
    public bool HasMyVoice => Voices.Any(v => v.IsMine);
    public string RecordButtonText => IsRecording ? "Stop" : HasMyVoice ? "Re-record my voice" : "Record my voice";

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!SetField(ref _isRecording, value)) return;
            OnPropertyChanged(nameof(RecordButtonText));
        }
    }

    public double RecordedSeconds
    {
        get => _recordedSeconds;
        private set => SetField(ref _recordedSeconds, value);
    }

    public double MaxSeconds => MaxRecordSeconds;

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public void Initialize(string? voiceId)
    {
        _preferredId = voiceId;
        Refresh();
        if (!ModelsInstalled) Message = "Speech models aren't installed yet.";
        else if (!HasMyVoice) Message = "Record about 10 seconds of your voice so lines are spoken in it. Until then a ready-made voice is used.";
    }

    private void Refresh()
    {
        string? keep = _selected?.Id ?? _preferredId;
        Voices.Clear();
        foreach (var v in _speech.Voices) Voices.Add(v);
        _selected = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault(v => v.IsMine) ?? Voices.FirstOrDefault();
        foreach (var name in new[] { nameof(Selected), nameof(CanDelete), nameof(HasMyVoice), nameof(RecordButtonText), nameof(ModelsInstalled) })
            OnPropertyChanged(name);
        VoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ recording

    private void ToggleRecording()
    {
        if (IsRecording)
        {
            FinishRecording();
            return;
        }
        _recordRate = _engine.StartRecording(MaxRecordSeconds + 1);
        if (_recordRate == 0)
        {
            Message = "Your microphone isn't running. Check the Microphone setting, then try again.";
            return;
        }
        RecordedSeconds = 0;
        IsRecording = true;
        Message = "Recording… read the text above in your normal voice.";
        _recordTimer.Start();
    }

    private void OnRecordTick(object? sender, EventArgs e)
    {
        RecordedSeconds = Math.Min(MaxRecordSeconds, _engine.RecordedSeconds);
        if (RecordedSeconds >= MaxRecordSeconds) FinishRecording();
    }

    private async void FinishRecording()
    {
        _recordTimer.Stop();
        var samples = _engine.StopRecording();
        IsRecording = false;
        double seconds = _recordRate > 0 ? samples.Length / (double)_recordRate : 0;
        if (seconds < MinRecordSeconds)
        {
            Message = $"That was only {seconds:0} seconds. Record at least {MinRecordSeconds:0} seconds.";
            return;
        }
        if (Peak(samples) < 0.02f)
        {
            Message = "That recording is almost silent. Check your microphone and try again.";
            return;
        }
        try
        {
            Message = "Saving your voice…";
            int rate = _recordRate;
            await Task.Run(() => _speech.Library.SaveMyVoice(TrimSilence(samples, rate), rate));
            _speech.ForgetVoice(SpeechLibrary.MyVoiceId);
            _preferredId = SpeechLibrary.MyVoiceId;
            _selected = null;
            Refresh();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            Message = "Saved. Lines are now spoken in your voice.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't save the recording: {ex.Message}";
        }
    }

    private static float Peak(float[] samples)
    {
        float peak = 0;
        foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    /// <summary>Drops leading and trailing silence (keeps 0.2 s of margin).</summary>
    public static float[] TrimSilence(float[] samples, int rate)
    {
        float threshold = Math.Max(0.01f, Peak(samples) * 0.05f);
        int first = Array.FindIndex(samples, s => Math.Abs(s) > threshold);
        int last = Array.FindLastIndex(samples, s => Math.Abs(s) > threshold);
        if (first < 0) return samples;
        int margin = rate / 5;
        first = Math.Max(0, first - margin);
        last = Math.Min(samples.Length - 1, last + margin);
        return samples[first..(last + 1)];
    }

    // ------------------------------------------------------------------ import / delete

    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a voice recording (WAV, 5–20 seconds of clear speech)",
            Filter = "WAV recordings (*.wav)|*.wav",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var voice = _speech.Library.Import(dialog.FileName);
            _speech.ForgetVoice(voice.Id);
            _preferredId = voice.Id;
            _selected = null;
            Refresh();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            Message = $"Imported \"{voice.Name}\".";
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Couldn't import that file: {ex.Message}";
        }
    }

    private void Delete()
    {
        if (_selected is not { BuiltIn: false } voice) return;
        try
        {
            _speech.Library.Delete(voice);
            _speech.ForgetVoice(voice.Id);
            _selected = null;
            _preferredId = null;
            Refresh();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            Message = $"Deleted \"{voice.Name}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't delete it: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _recordTimer.Stop();
        if (IsRecording) _engine.StopRecording();
    }
}
