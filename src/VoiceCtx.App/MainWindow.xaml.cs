using System.Windows;
using System.Windows.Threading;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.App;

public partial class MainWindow : Window
{
    private readonly DeviceService _devices = new();
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public MainWindow()
    {
        InitializeComponent();
        _engine = new AudioEngine(_devices);
        _engine.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateStatus);
        _meterTimer.Tick += (_, _) => LevelBar.Value = _engine.InputLevel;
        _meterTimer.Start();
        Loaded += async (_, _) => await LoadDevicesAsync();
        Closed += (_, _) =>
        {
            _engine.Dispose();
            _devices.Dispose();
        };
    }

    private async Task LoadDevicesAsync()
    {
        var inputs = await _devices.GetInputDevicesAsync();
        var outputs = await _devices.GetOutputDevicesAsync();
        InputBox.ItemsSource = inputs;
        InputBox.SelectedIndex = inputs.Count > 0 ? 0 : -1;
        OutputBox.ItemsSource = outputs;
        var cable = DeviceService.FindCableInput(outputs);
        OutputBox.SelectedItem = cable;
        MonitorBox.ItemsSource = outputs.Where(d => d != cable).ToList();
        MonitorBox.SelectedIndex = 0;
        UpdateStatus();
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_engine.State is EngineState.Stopped)
        {
            _engine.Start(new EngineConfig(
                (InputBox.SelectedItem as AudioDeviceInfo)?.Id,
                (OutputBox.SelectedItem as AudioDeviceInfo)?.Id,
                (MonitorBox.SelectedItem as AudioDeviceInfo)?.Id,
                MonitorCheck.IsChecked == true));
        }
        else
        {
            _engine.Stop();
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_engine != null) _engine.MasterVolume = (float)e.NewValue;
    }

    private void UpdateStatus()
    {
        StatusText.Text = $"{_engine.State}: {_engine.StatusMessage}";
        StartButton.Content = _engine.State is EngineState.Stopped ? "Start" : "Stop";
    }
}
