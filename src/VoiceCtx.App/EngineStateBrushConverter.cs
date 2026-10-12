using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.App;

public sealed class EngineStateBrushConverter : IValueConverter
{
    private static readonly Brush Running = Frozen(Color.FromRgb(0x2F, 0xBF, 0x71));
    private static readonly Brush Busy = Frozen(Color.FromRgb(0xF2, 0xA9, 0x3B));
    private static readonly Brush Failed = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush Idle = Frozen(Color.FromRgb(0x8B, 0x92, 0xA3));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        EngineState.Running => Running,
        EngineState.Starting or EngineState.Recovering => Busy,
        EngineState.Error => Failed,
        _ => Idle,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
