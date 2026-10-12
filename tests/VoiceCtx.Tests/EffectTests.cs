using VoiceCtx.Audio.Dsp;
using VoiceCtx.Audio.Effects;

namespace VoiceCtx.Tests;

public class EffectTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    public static TheoryData<EffectKind> AllEffects()
    {
        var data = new TheoryData<EffectKind>();
        foreach (var kind in Enum.GetValues<EffectKind>()) data.Add(kind);
        return data;
    }

    private static float[] Run(IVoiceEffect effect, float[] input, int block = Block)
    {
        var output = (float[])input.Clone();
        for (int i = 0; i < output.Length; i += block)
            effect.Process(output.AsSpan(i, Math.Min(block, output.Length - i)));
        return output;
    }

    private static void AssertFiniteAndBounded(float[] samples)
    {
        foreach (float s in samples)
        {
            Assert.True(float.IsFinite(s), "non-finite sample");
            Assert.InRange(s, -1f, 1f);
        }
    }

    [Theory]
    [MemberData(nameof(AllEffects))]
    public void OutputIsFiniteAndWithinRange(EffectKind kind)
    {
        var effect = EffectFactory.Create(kind, Rate, Block);
        AssertFiniteAndBounded(Run(effect, new float[Rate]));
        AssertFiniteAndBounded(Run(effect, TestSignals.Sine(440, Rate, Rate, 0.99f)));
        AssertFiniteAndBounded(Run(effect, TestSignals.Noise(Rate * 2, amplitude: 1f)));
        // Odd block sizes and blocks larger than the configured maximum must also be safe.
        AssertFiniteAndBounded(Run(effect, TestSignals.Noise(Rate, seed: 7), block: 1000));
        AssertFiniteAndBounded(Run(effect, TestSignals.Sine(97, Rate, Rate), block: 37));
    }

    [Theory]
    [MemberData(nameof(AllEffects))]
    public void ResetClearsState(EffectKind kind)
    {
        var effect = EffectFactory.Create(kind, Rate, Block);
        Run(effect, TestSignals.Noise(Rate));
        effect.Reset();
        var output = Run(effect, new float[Rate / 2]);
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Theory]
    [MemberData(nameof(AllEffects))]
    public void ProcessDoesNotAllocate(EffectKind kind)
    {
        var effect = EffectFactory.Create(kind, Rate, Block);
        var buffer = TestSignals.Noise(Block);
        effect.Process(buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) effect.Process(buffer);
        effect.Reset();
        effect.Process(buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void SilenceOutputsZeros()
    {
        var output = Run(EffectFactory.Create(EffectKind.Silence, Rate, Block), TestSignals.Noise(Rate));
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void NoneIsBitIdentical()
    {
        var input = TestSignals.Noise(Rate, amplitude: 1f);
        input[10] = 1f;
        input[11] = -1f;
        input[12] = 1e-30f;
        var output = Run(EffectFactory.Create(EffectKind.None, Rate, Block), input);
        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData(EffectKind.MalePitch, EffectFactory.MaleSemitones)]
    [InlineData(EffectKind.FemalePitch, EffectFactory.FemaleSemitones)]
    [InlineData(EffectKind.Baby, EffectFactory.BabySemitones)]
    [InlineData(EffectKind.Helium, EffectFactory.HeliumSemitones)]
    public void PresetPitchShiftsSineToExpectedFrequency(EffectKind kind, float semitones)
    {
        AssertShiftedFrequency(EffectFactory.Create(kind, Rate, Block), semitones);
    }

    [Theory]
    [InlineData(-12f)]
    [InlineData(-7f)]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(5f)]
    [InlineData(12f)]
    public void CustomPitchShiftsSineToExpectedFrequency(float semitones)
    {
        var effect = (PitchEffect)EffectFactory.Create(EffectKind.CustomPitch, Rate, Block);
        effect.Semitones.Value = semitones;
        AssertShiftedFrequency(effect, semitones);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(16000)]
    public void PitchShifterWorksAtOtherSampleRates(int rate)
    {
        var shifter = new PitchShifter(rate) { Semitones = 7 };
        var signal = TestSignals.Sine(300, rate, rate * 2);
        for (int i = 0; i < signal.Length; i += 256) shifter.Process(signal.AsSpan(i, Math.Min(256, signal.Length - i)));
        double f = TestSignals.EstimateFrequency(signal.AsSpan(rate / 2), rate);
        Assert.InRange(f, 300 * Math.Pow(2, 7 / 12.0) * 0.98, 300 * Math.Pow(2, 7 / 12.0) * 1.02);
    }

    [Fact]
    public void PitchShifterPreservesLevel()
    {
        var effect = EffectFactory.Create(EffectKind.FemalePitch, Rate, Block);
        var output = Run(effect, TestSignals.Sine(440, Rate, Rate * 2, 0.5f));
        double rms = TestSignals.Rms(output.AsSpan(Rate));
        Assert.InRange(rms, 0.5 / Math.Sqrt(2) * 0.85, 0.5 / Math.Sqrt(2) * 1.1);
    }

    [Fact]
    public void ZeroMixReturnsDrySignal()
    {
        var effect = EffectFactory.Create(EffectKind.Radio, Rate, Block);
        effect.Mix = 0f;
        effect.Reset();
        var input = TestSignals.Sine(440, Rate, Rate, 0.5f);
        var output = Run(effect, input);
        for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i], 6);
    }

    private static void AssertShiftedFrequency(IVoiceEffect effect, float semitones)
    {
        const double source = 440;
        double expected = source * Math.Pow(2, semitones / 12.0);
        var output = Run(effect, TestSignals.Sine(source, Rate, Rate * 2, 0.5f));
        double measured = TestSignals.EstimateFrequency(output.AsSpan(Rate / 2), Rate);
        Assert.InRange(measured, expected * 0.98, expected * 1.02);
    }
}

public class EffectProcessorTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    [Theory]
    [InlineData(EffectKind.None, EffectKind.Helium)]
    [InlineData(EffectKind.Helium, EffectKind.None)]
    [InlineData(EffectKind.Radio, EffectKind.MalePitch)]
    [InlineData(EffectKind.None, EffectKind.Silence)]
    [InlineData(EffectKind.Robot, EffectKind.Atari)]
    public void SwitchingEffectsDoesNotClick(EffectKind from, EffectKind to)
    {
        var controls = new EffectControls { Effect = from };
        var processor = new EffectProcessor(Rate, Block, controls);
        var signal = TestSignals.Sine(220, Rate, Rate * 3, 0.4f);
        for (int i = 0, b = 0; i < signal.Length; i += Block, b++)
        {
            if (b == 100) controls.Effect = to;
            processor.Process(signal.AsSpan(i, Block));
        }

        // Largest sample-to-sample step in each phase: steady "from", the switch, steady "to".
        float steadyFrom = MaxStep(signal, 50, 100);
        float transition = MaxStep(signal, 100, 106);
        float steadyTo = MaxStep(signal, 200, 250);

        Assert.Equal(to, processor.ActiveEffect);
        float allowed = Math.Max(steadyFrom, steadyTo) + 0.05f;
        Assert.True(transition <= allowed, $"step of {transition} while switching (steady max {allowed - 0.05f})");
    }

    private static float MaxStep(float[] samples, int fromBlock, int toBlock)
    {
        float max = 0;
        for (int i = Math.Max(1, fromBlock * Block); i < toBlock * Block; i++)
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        return max;
    }

    [Fact]
    public void OffBypassesToCleanSignal()
    {
        var controls = new EffectControls { Effect = EffectKind.Robot };
        var processor = new EffectProcessor(Rate, Block, controls);
        processor.Process(TestSignals.Noise(Rate));
        controls.Enabled = false;
        processor.Process(new float[Block * 5]);

        var input = TestSignals.Noise(Block);
        var output = (float[])input.Clone();
        processor.Process(output);
        Assert.Equal(EffectKind.None, processor.ActiveEffect);
        Assert.Equal(input, output);
    }

    [Fact]
    public void SwitchingDoesNotAllocate()
    {
        var controls = new EffectControls();
        var processor = new EffectProcessor(Rate, Block, controls);
        var buffer = TestSignals.Noise(Block);
        var kinds = Enum.GetValues<EffectKind>();
        processor.Process(buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var kind in kinds)
        {
            controls.Effect = kind;
            for (int i = 0; i < 5; i++) processor.Process(buffer);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
