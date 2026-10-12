using VoiceCtx.Audio.Effects;

namespace VoiceCtx.App.ViewModels;

public sealed class EffectOption(EffectKind kind, string title, string subtitle, Action<EffectKind> select) : ObservableObject
{
    private bool _isSelected;

    public EffectKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value) && value) select(Kind);
        }
    }

    /// <summary>Updates the highlight without re-triggering selection.</summary>
    internal void SetSelected(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    public static IReadOnlyList<EffectOption> CreateCatalog(Action<EffectKind> select) =>
    [
        new(EffectKind.None, "None", "Clean voice", select),
        new(EffectKind.MalePitch, "Male", $"{EffectFactory.MaleSemitones:+0;−0} semitones", select),
        new(EffectKind.FemalePitch, "Female", $"{EffectFactory.FemaleSemitones:+0;−0} semitones", select),
        new(EffectKind.Baby, "Baby", $"{EffectFactory.BabySemitones:+0;−0} semitones", select),
        new(EffectKind.Helium, "Helium", $"{EffectFactory.HeliumSemitones:+0;−0} semitones", select),
        new(EffectKind.CustomPitch, "Custom pitch", "Use the slider", select),
        new(EffectKind.Robot, "Robot", "Metallic ring", select),
        new(EffectKind.Alien, "Alien", "Warbling high", select),
        new(EffectKind.Atari, "Atari", "8-bit crunch", select),
        new(EffectKind.Clone, "Clone", "You, twice", select),
        new(EffectKind.Mutation, "Mutation", "Drifting pitch", select),
        new(EffectKind.FastMutation, "Fast mutation", "Rapid drift", select),
        new(EffectKind.SlowMutation, "Slow mutation", "Lazy drift", select),
        new(EffectKind.Radio, "Radio", "Old AM band", select),
        new(EffectKind.Silence, "Silence", "Mute voice", select),
    ];
}
