using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceCtx.App.Services;
using VoiceCtx.App.ViewModels;
using VoiceCtx.Audio.Engine;

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
        _engine = new AudioEngine(_devices);
        _viewModel = new MainViewModel(_devices, _engine);

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += OnWindowClosing;

        _tray = new TrayIcon();
        _tray.ShowRequested += (_, _) => ShowWindow();
        _tray.ToggleRequested += (_, _) => _viewModel.Toggle();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _hotkey = new GlobalHotkey(_window, ModifierKeys.Control | ModifierKeys.Alt, Key.V, _viewModel.Toggle);
        if (!_hotkey.IsRegistered)
            _viewModel.HotkeyWarning = $"{_hotkey.Display} is used by another app, so the global hotkey is unavailable.";

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
        _tray?.Dispose();
        _viewModel?.Dispose();
        _engine?.Dispose();
        _devices?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
