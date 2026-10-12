using VoiceCtx.Audio.Dsp;
using VoiceCtx.Audio.Enhancement;

namespace VoiceCtx.Tests;

public class NoiseSuppressorTests
{
    private const int Block = 480;

    private static float[] Run(IVoiceEffect effect, float[] input, int block = Block)
    {
        var output = (float[])input.Clone();
        for (int i = 0; i < output.Length; i += block)
            effect.Process(output.AsSpan(i, Math.Min(block, output.Length - i)));
        return output;
    }

    [Fact]
    public void NativeLibraryLoads()
    {
        Assert.True(MicEnhancer.NoiseCancellationSupported);
        using var suppressor = new NoiseSuppressor(48000, Block);
        Assert.True(suppressor.IsAvailable);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(16000)]
    public void SuppressesSteadyNoise(int rate)
    {
        using var suppressor = new NoiseSuppressor(rate, rate / 100);
        var noise = TestSignals.Noise(rate * 4, amplitude: 0.05f);
        var output = Run(suppressor, noise, rate / 100);

        double before = TestSignals.Rms(noise.AsSpan(rate * 2));
        double after = TestSignals.Rms(output.AsSpan(rate * 2));
        double reductionDb = 20 * Math.Log10(after / before);
        Assert.True(reductionDb < -10, $"only {reductionDb:F1} dB of noise reduction");
    }

    [Fact]
    public void ZeroReductionIsTheOriginalDelayed()
    {
        using var suppressor = new NoiseSuppressor(48000, Block);
        suppressor.Reduction.Value = 0f;
        var input = TestSignals.Noise(48000);
        var output = Run(suppressor, input);

        int latency = suppressor.LatencySamples;
        Assert.Equal(960, latency);
        for (int i = latency; i < input.Length; i++)
            Assert.True(Math.Abs(input[i - latency] - output[i]) < 1e-5f, $"sample {i} misaligned");
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void OutputIsFiniteBoundedAndAllocationFree(int rate)
    {
        using var suppressor = new NoiseSuppressor(rate, 1000);
        var loud = TestSignals.Noise(rate * 2, amplitude: 1f);
        foreach (float s in Run(suppressor, loud, 1000))
        {
            Assert.True(float.IsFinite(s));
            Assert.InRange(s, -1f, 1f);
        }

        var buffer = TestSignals.Noise(rate / 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) suppressor.Process(buffer);
        suppressor.Reset();
        suppressor.Process(buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

public class StudioEffectTests
{
    private const int Rate = 48000;

    private static float[] Run(IVoiceEffect effect, float[] input)
    {
        var output = (float[])input.Clone();
        for (int i = 0; i < output.Length; i += 480)
            effect.Process(output.AsSpan(i, Math.Min(480, output.Length - i)));
        return output;
    }

    [Fact]
    public void RemovesLowRumble()
    {
        var rumble = TestSignals.Sine(30, Rate, Rate * 2, 0.3f);
        var output = Run(new StudioEffect(Rate, 480) { Strength = { Value = 0f } }, rumble);
        double db = 20 * Math.Log10(TestSignals.Rms(output.AsSpan(Rate)) / TestSignals.Rms(rumble.AsSpan(Rate)));
        Assert.True(db < -12, $"30 Hz only reduced by {db:F1} dB");
    }

    [Fact]
    public void EvensOutLoudAndQuietSpeechLevels()
    {
        var quiet = TestSignals.Sine(300, Rate, Rate, 0.02f);
        var loud = TestSignals.Sine(300, Rate, Rate, 0.5f);
        var studio = new StudioEffect(Rate, 480) { Strength = { Value = 1f } };
        var outQuiet = Run(studio, quiet);
        studio.Reset();
        var outLoud = Run(studio, loud);

        double inputRangeDb = 20 * Math.Log10(0.5 / 0.02);
        double outputRangeDb = 20 * Math.Log10(TestSignals.Rms(outLoud.AsSpan(Rate / 2)) / TestSignals.Rms(outQuiet.AsSpan(Rate / 2)));
        Assert.True(outputRangeDb < inputRangeDb - 8, $"dynamic range {inputRangeDb:F1} dB → {outputRangeDb:F1} dB");
    }

    [Fact]
    public void LimiterKeepsHotInputBelowFullScale()
    {
        var hot = TestSignals.Sine(1000, Rate, Rate, 1f);
        var output = Run(new StudioEffect(Rate, 480) { Strength = { Value = 1f } }, hot);
        Assert.All(output, s => Assert.InRange(s, -1f, 1f));
        Assert.True(output.Skip(Rate / 2).Max(Math.Abs) < 0.999f);
    }

    [Fact]
    public void GateKeepsRoomToneFarBelowSpeech()
    {
        double GainDb(float[] input)
        {
            var output = Run(new StudioEffect(Rate, 480) { Strength = { Value = 1f } }, input);
            return 20 * Math.Log10(TestSignals.Rms(output.AsSpan(Rate)) / TestSignals.Rms(input.AsSpan(Rate)));
        }

        double hissGain = GainDb(TestSignals.Noise(Rate * 2, amplitude: 0.001f));
        double speechGain = GainDb(TestSignals.Sine(300, Rate, Rate * 2, 0.05f));
        Assert.True(speechGain - hissGain > 18, $"speech {speechGain:F1} dB vs room tone {hissGain:F1} dB");
    }
}

public class MicEnhancerTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    [Fact]
    public void BothOffIsBitIdentical()
    {
        using var enhancer = new MicEnhancer(Rate, Block, new MicEnhancementControls());
        var input = TestSignals.Noise(Rate);
        var output = (float[])input.Clone();
        for (int i = 0; i < output.Length; i += Block) enhancer.Process(output.AsSpan(i, Block));
        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TogglingIsClickFreeAndReturnsToBypass(bool noise, bool studio)
    {
        var controls = new MicEnhancementControls();
        using var enhancer = new MicEnhancer(Rate, Block, controls);
        var signal = TestSignals.Sine(200, Rate, Rate * 3, 0.3f);
        var original = (float[])signal.Clone();
        for (int i = 0, b = 0; i < signal.Length; i += Block, b++)
        {
            if (b == 50) { controls.NoiseCancel = noise; controls.Studio = studio; }
            if (b == 200) { controls.NoiseCancel = false; controls.Studio = false; }
            enhancer.Process(signal.AsSpan(i, Block));
        }

        float maxStep = 0;
        for (int i = 1; i < signal.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(signal[i] - signal[i - 1]));
        float sineStep = (float)(0.3 * 2 * Math.PI * 200 / Rate);
        Assert.True(maxStep < sineStep * 4 + 0.02f, $"max step {maxStep}");

        for (int i = 250 * Block; i < signal.Length; i++) Assert.Equal(original[i], signal[i]);
    }

    [Fact]
    public void ProcessingDoesNotAllocate()
    {
        var controls = new MicEnhancementControls { NoiseCancel = true, Studio = true };
        using var enhancer = new MicEnhancer(Rate, Block, controls);
        var buffer = TestSignals.Noise(Block);
        for (int i = 0; i < 10; i++) enhancer.Process(buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            if (i % 50 == 0) controls.Studio = !controls.Studio;
            enhancer.Process(buffer);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
