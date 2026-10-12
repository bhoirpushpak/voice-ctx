using System.Windows;
using VoiceCtx.App.ViewModels;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.App;

public partial class MainWindow : Window
{
    private readonly DeviceService _devices = new();
    private readonly AudioEngine _engine;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new AudioEngine(_devices);
        _viewModel = new MainViewModel(_devices, _engine);
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.InitializeAsync(null, null, null, monitorEnabled: false);
        Closed += (_, _) =>
        {
            _viewModel.Dispose();
            _engine.Dispose();
            _devices.Dispose();
        };
    }
}
