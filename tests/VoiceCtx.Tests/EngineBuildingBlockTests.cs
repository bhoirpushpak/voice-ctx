using VoiceCtx.Audio.Dsp;
using VoiceCtx.Audio.Engine;

namespace VoiceCtx.Tests;

public class SpscRingBufferTests
{
    [Fact]
    public void PreservesOrderAcrossWrapAround()
    {
        var ring = new SpscRingBuffer(8);
        var output = new float[5];
        float next = 0, expected = 0;
        for (int round = 0; round < 20; round++)
        {
            var input = new float[5];
            for (int i = 0; i < input.Length; i++) input[i] = next++;
            Assert.Equal(5, ring.Write(input));
            Assert.Equal(5, ring.Read(output));
            foreach (float v in output) Assert.Equal(expected++, v);
        }
    }

    [Fact]
    public void DropsWhenFullAndReportsCount()
    {
        var ring = new SpscRingBuffer(4);
        Assert.Equal(4, ring.Write(new float[] { 1, 2, 3, 4, 5, 6 }));
        Assert.Equal(4, ring.Count);
        Assert.Equal(2, ring.Skip(2));
        var output = new float[4];
        Assert.Equal(2, ring.Read(output));
        Assert.Equal(new float[] { 3, 4, 0, 0 }, output);
        Assert.Equal(0, ring.Count);
    }
}

public class StreamResamplerTests
{
    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(16000, 48000)]
    public void PreservesFrequencyAndLength(int inRate, int outRate)
    {
        var input = TestSignals.Sine(440, inRate, inRate);
        var resampler = new StreamResampler(480);
        double step = (double)inRate / outRate;
        var output = new float[StreamResampler.MaxOutputLength(input.Length, step) + 1000];
        int written = 0;
        for (int i = 0; i < input.Length; i += 480)
            written += resampler.Process(input.AsSpan(i, Math.Min(480, input.Length - i)), output.AsSpan(written), step);

        Assert.InRange(written, outRate - 5, outRate + 5);
        double f = TestSignals.EstimateFrequency(output.AsSpan(1000, written - 2000), outRate);
        Assert.InRange(f, 438.0, 442.0);
    }

    [Fact]
    public void UnityStepIsDelayedCopy()
    {
        var input = TestSignals.Noise(1000);
        var resampler = new StreamResampler(256);
        var output = new float[1100];
        int written = resampler.Process(input, output, 1.0);
        Assert.Equal(1000, written);
        for (int i = 2; i < written; i++) Assert.Equal(input[i - 2], output[i], 5);
    }
}
