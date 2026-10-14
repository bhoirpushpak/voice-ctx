using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.App.Services;
using VoiceCtx.App.ViewModels;
using VoiceCtx.Audio.AiVoice;
using VoiceCtx.Audio.Engine;
using VoiceCtx.Audio.Speech;
using VoiceCtx.Audio.Translate;

namespace VoiceCtx.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private SettingsStore? _settings;
    private DeviceService? _devices;
    private AudioEngine? _engine;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _noiseHotkey;
    private GlobalHotkey? _aiVoiceHotkey;
    private GlobalHotkey? _speakHotkey;
    private GlobalHotkey? _translateHotkey;
    private LiveTranslateService? _translate;
    private AiVoiceService? _aiVoice;
    private SpeechService? _speech;
    private DispatcherTimer? _saveTimer;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(ShowWindow));

        _settings = new SettingsStore(SettingsStore.DefaultPath);
        _devices = new DeviceService();
        _aiVoice = new AiVoiceService(new VoiceLibrary(VoiceLibrary.DefaultRoot));
        _engine = new AudioEngine(_devices, _aiVoice);
        _speech = new SpeechService(new SpeechLibrary(SpeechLibrary.DefaultRoot));
        _translate = new LiveTranslateService(new TranslateLibrary(TranslateLibrary.DefaultRoot), _engine.Speech, _speech);
        _engine.AttachTranslator(_translate);
        var speechVoices = new SpeechVoicesViewModel(_speech, _engine);
        _viewModel = new MainViewModel(_devices, _engine, new AiVoiceViewModel(_aiVoice, _engine), speechVoices,
            new TypeToSpeakViewModel(_engine.Speech, _speech, speechVoices), new LiveTranslateViewModel(_translate, _speech, speechVoices));

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += OnWindowClosing;

        _tray = new TrayIcon();
        _tray.ShowRequested += (_, _) => ShowWindow();
        _tray.ToggleRequested += (_, _) => _viewModel.Toggle();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _hotkey = new GlobalHotkey(_window, ModifierKeys.Control | ModifierKeys.Alt, Key.V, _viewModel.Toggle);
        _noiseHotkey = new GlobalHotkey(_window, ModifierKeys.Control | ModifierKeys.Alt, Key.N, _viewModel.ToggleNoiseCancel);
        _aiVoiceHotkey = new GlobalHotkey(_window, ModifierKeys.Control | ModifierKeys.Alt, Key.A, _viewModel.AiVoice.Toggle);
        _speakHotkey = new GlobalHotkey(_window, ModifierKeys.Control | ModifierKeys.Alt, Key.T, () =>
        {
            ShowWindow();
            _window.FocusTypeToSpeak();
        });
        _translateHotkey = new GlobalHotkey(_window, ModifierKeys.Control | ModifierKeys.Alt, Key.L, _viewModel.LiveTranslate.Toggle);
        var taken = new[] { _hotkey, _noiseHotkey, _aiVoiceHotkey, _speakHotkey, _translateHotkey }.Where(h => !h.IsRegistered).Select(h => h.Display).ToList();
        if (taken.Count > 0)
            _viewModel.HotkeyWarning = $"{string.Join(", ", taken)} {(taken.Count == 1 ? "is" : "are")} used by another app, so {(taken.Count == 1 ? "that hotkey is" : "those hotkeys are")} unavailable.";

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveSettings();
        _viewModel.SettingsChanged += (_, _) =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsOn) or nameof(MainViewModel.EffectDisplayName))
                _tray.Update(_viewModel.IsOn, _viewModel.EffectDisplayName);
        };

        _window.Show();
        await _viewModel.InitializeAsync(_settings.Load());
        _tray.Update(_viewModel.IsOn, _viewModel.EffectDisplayName);
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        _window!.Hide();
        _tray!.NotifyStillRunning();
    }

    private void SaveSettings()
    {
        _saveTimer?.Stop();
        if (_viewModel == null || _settings == null) return;
        try
        {
            _settings.Save(_viewModel.ToSettings());
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never take the app down over them.
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        SaveSettings();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _noiseHotkey?.Dispose();
        _aiVoiceHotkey?.Dispose();
        _speakHotkey?.Dispose();
        _translateHotkey?.Dispose();
        _tray?.Dispose();
        _viewModel?.Dispose();
        _engine?.Dispose();
        _aiVoice?.Dispose();
        _translate?.Dispose();
        _speech?.Dispose();
        _devices?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
