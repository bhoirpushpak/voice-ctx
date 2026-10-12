using VoiceCtx.Audio.Engine;

namespace VoiceCtx.Tests;

public class OutputSinkTests
{
    /// <summary>
    /// Simulates capture and render devices whose clocks disagree slightly: the producer pushes 10 ms
    /// blocks at its rate while the consumer pulls 10 ms blocks at its own. The drift controller must
    /// keep the FIFO near its target with no underruns once primed.
    /// </summary>
    [Theory]
    [InlineData(48000, 48000, 1.001)]
    [InlineData(48000, 48000, 0.999)]
    [InlineData(44100, 48000, 1.0005)]
    [InlineData(48000, 44100, 0.9995)]
    public void DriftControllerKeepsLatencyBounded(int inputRate, int outputRate, double clockSkew)
    {
        int inBlock = inputRate / 100, outBlock = outputRate / 100;
        int target = outBlock * 3 / 2;
        var sink = new OutputSink(inputRate, outputRate, inBlock, target, outBlock * 6);
        var input = TestSignals.Sine(440, inputRate, inBlock);
        var output = new float[outBlock];

        double producerClock = 0, consumerClock = 0;
        double consumerPeriod = 0.01 / clockSkew;
        int underruns = 0, pulls = 0;
        var fills = new List<int>();
        for (int step = 0; step < 6000; step++) // one simulated minute
        {
            if (producerClock <= consumerClock)
            {
                sink.Push(input);
                producerClock += 0.01;
            }
            else
            {
                sink.Pull(output);
                consumerClock += consumerPeriod;
                if (++pulls > 50)
                {
                    if (output[^1] == 0 && output[^2] == 0) underruns++;
                    fills.Add(sink.Fill);
                }
            }
        }

        Assert.Equal(0, underruns);
        Assert.InRange(fills.Skip(fills.Count / 2).Max(), 0, target + outBlock * 2);
    }

    [Fact]
    public void RecoversLatencyAfterStall()
    {
        var sink = new OutputSink(48000, 48000, 480, 720, 2880);
        var block = new float[480];
        for (int i = 0; i < 40; i++) sink.Push(block); // consumer stalled: 400 ms piles up
        sink.Pull(new float[480]);
        Assert.InRange(sink.Fill, 0, 720);
    }

    [Fact]
    public void PushAndPullDoNotAllocate()
    {
        var sink = new OutputSink(44100, 48000, 441, 720, 2880);
        var input = TestSignals.Sine(440, 44100, 441);
        var output = new float[480];
        for (int i = 0; i < 10; i++) { sink.Push(input); sink.Pull(output); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { sink.Push(input); sink.Pull(output); }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

public class SampleConverterTests
{
    [Theory]
    [InlineData((int)SampleEncoding.Float32)]
    [InlineData((int)SampleEncoding.Pcm16)]
    [InlineData((int)SampleEncoding.Pcm24)]
    [InlineData((int)SampleEncoding.Pcm32)]
    public void MonoRoundTripsThroughDeviceFormat(int encodingValue)
    {
        var encoding = (SampleEncoding)encodingValue;
        var format = new StreamFormat(48000, 2, encoding);
        var mono = TestSignals.Sine(1000, 48000, 480, 0.8f);
        var bytes = new byte[mono.Length * format.BytesPerFrame];
        SampleConverter.FromMono(mono, format, bytes);

        var back = new float[mono.Length];
        SampleConverter.ToMono(bytes, format, back);

        float tolerance = encoding == SampleEncoding.Pcm16 ? 1e-4f : 1e-6f;
        for (int i = 0; i < mono.Length; i++) Assert.Equal(mono[i], back[i], tolerance);
    }

    [Fact]
    public void DownmixAveragesChannels()
    {
        var format = new StreamFormat(48000, 2, SampleEncoding.Float32);
        var interleaved = new float[] { 0.5f, -0.5f, 1f, 0f, -1f, -1f };
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray();
        var mono = new float[3];
        SampleConverter.ToMono(bytes, format, mono);
        Assert.Equal(new[] { 0f, 0.5f, -1f }, mono);
    }

    [Fact]
    public void PcmOutputClampsOverload()
    {
        var format = new StreamFormat(48000, 1, SampleEncoding.Pcm16);
        var bytes = new byte[4];
        SampleConverter.FromMono(new[] { 2f, -2f }, format, bytes);
        var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes);
        Assert.Equal(short.MaxValue, pcm[0]);
        Assert.Equal(-short.MaxValue, pcm[1]);
    }
}
