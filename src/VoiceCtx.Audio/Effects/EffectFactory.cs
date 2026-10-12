using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Effects;

public enum EffectKind
{
    None,
    MalePitch,
    FemalePitch,
    Helium,
    Baby,
    CustomPitch,
    Robot,
    Alien,
    Atari,
    Clone,
    Mutation,
    FastMutation,
    SlowMutation,
    Radio,
    Silence,
}

public static class EffectFactory
{
    public const float MaleSemitones = -3f;
    public const float FemaleSemitones = 4f;
    public const float BabySemitones = 8f;
    public const float HeliumSemitones = 12f;

    public static IVoiceEffect Create(EffectKind kind, int sampleRate, int maxBlockSize) => kind switch
    {
        EffectKind.None => new NoneEffect(),
        EffectKind.MalePitch => new PitchEffect("Male pitch", sampleRate, maxBlockSize, MaleSemitones),
        EffectKind.FemalePitch => new PitchEffect("Female pitch", sampleRate, maxBlockSize, FemaleSemitones),
        EffectKind.Helium => new PitchEffect("Helium", sampleRate, maxBlockSize, HeliumSemitones),
        EffectKind.Baby => new PitchEffect("Baby", sampleRate, maxBlockSize, BabySemitones),
        EffectKind.CustomPitch => new PitchEffect("Custom pitch", sampleRate, maxBlockSize, 0, adjustable: true),
        EffectKind.Robot => new RobotEffect(sampleRate, maxBlockSize),
        EffectKind.Alien => new AlienEffect(sampleRate, maxBlockSize),
        EffectKind.Atari => new AtariEffect(sampleRate, maxBlockSize),
        EffectKind.Clone => new CloneEffect(sampleRate, maxBlockSize),
        EffectKind.Mutation => new MutationEffect("Mutation", sampleRate, maxBlockSize, 10f),
        EffectKind.FastMutation => new MutationEffect("Fast mutation", sampleRate, maxBlockSize, 30f),
        EffectKind.SlowMutation => new MutationEffect("Slow mutation", sampleRate, maxBlockSize, 1f),
        EffectKind.Radio => new RadioEffect(sampleRate, maxBlockSize),
        EffectKind.Silence => new SilenceEffect(sampleRate, maxBlockSize),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
