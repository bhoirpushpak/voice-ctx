using System.IO;
using VoiceCtx.Audio.Speech;

namespace VoiceCtx.Tests;

public class WavDecoderTests
{
    private static byte[] MakeWav(short[] samples, int rate, int channels)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8.ToArray());
        w.Write("LIST"u8.ToArray()); w.Write(4); w.Write("INFO"u8.ToArray());   // unknown chunk to skip
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }

    [Fact]
    public void DecodesPcm16AndSkipsUnknownChunks()
    {
        var (mono, rate) = WavDecoder.DecodeMono(MakeWav([0, 16384, -16384, 32767], 22050, 1));
        Assert.Equal(22050, rate);
        Assert.Equal(new[] { 0f, 0.5f, -0.5f, 32767 / 32768f }, mono);
    }

    [Fact]
    public void MixesStereoToMono()
    {
        var (mono, _) = WavDecoder.DecodeMono(MakeWav([16384, 0, -16384, -16384], 16000, 2));
        Assert.Equal(new[] { 0.25f, -0.5f }, mono);
    }

    [Fact]
    public void RejectsNonWav() => Assert.Throws<FormatException>(() => WavDecoder.DecodeMono(new byte[64]));
}

public class SpeechStageTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    private static float[] Run(SpeechStage stage, float[] mic)
    {
        var output = (float[])mic.Clone();
        for (int i = 0; i < output.Length; i += Block) stage.Process(output.AsSpan(i, Math.Min(Block, output.Length - i)));
        return output;
    }

    [Fact]
    public void PlaysClipAtCorrectPitchAndLengthWhenRatesDiffer()
    {
        var injector = new SpeechInjector { DuckGain = 0f };
        var stage = new SpeechStage(Rate, injector);
        injector.Enqueue(new SpeechClip(TestSignals.Sine(440, 22050, 22050, 0.5f), 22050, "test"));

        var output = Run(stage, new float[Rate * 2]);

        Assert.Equal(440, TestSignals.EstimateFrequency(output.AsSpan(Rate / 10, Rate * 8 / 10), Rate), 2.0);
        Assert.True(TestSignals.Rms(output.AsSpan(Rate * 11 / 10)) < 1e-4, "clip should end after ~1 s");
        Assert.False(injector.IsSpeaking);
    }

    [Fact]
    public void MutesMicWhileSpeakingAndRestoresAfterwards()
    {
        var injector = new SpeechInjector { DuckGain = 0f };
        var stage = new SpeechStage(Rate, injector);
        var mic = TestSignals.Sine(200, Rate, Rate * 2, 0.3f);
        injector.Enqueue(new SpeechClip(new float[Rate / 2], Rate, "silent line"));

        var output = Run(stage, mic);

        Assert.True(TestSignals.Rms(output.AsSpan(Rate / 10, Rate / 4)) < 1e-3, "mic should be muted during speech");
        var after = output.AsSpan(Rate * 3 / 2);
        Assert.Equal(TestSignals.Rms(mic.AsSpan(Rate * 3 / 2)), TestSignals.Rms(after), 3);
    }

    [Fact]
    public void BaseMicGainZeroMutesMicEvenWithoutSpeech()
    {
        var injector = new SpeechInjector { BaseMicGain = 0f };
        var stage = new SpeechStage(Rate, injector);
        var output = Run(stage, TestSignals.Sine(200, Rate, Rate, 0.3f));
        Assert.True(TestSignals.Rms(output.AsSpan(Rate / 10)) < 1e-6);
    }

    [Fact]
    public void ClearStopsImmediately()
    {
        var injector = new SpeechInjector();
        var stage = new SpeechStage(Rate, injector);
        injector.Enqueue(new SpeechClip(TestSignals.Sine(300, Rate, Rate * 5, 0.5f), Rate, "long"));
        injector.Enqueue(new SpeechClip(TestSignals.Sine(300, Rate, Rate * 5, 0.5f), Rate, "queued"));
        var buffer = new float[Block];
        for (int i = 0; i < 20; i++) stage.Process(buffer.AsSpan());

        injector.Clear();
        Array.Clear(buffer);
        var output = Run(stage, new float[Rate]);
        Assert.True(TestSignals.Rms(output) < 1e-6);
    }

    [Fact]
    public void ProcessDoesNotAllocate()
    {
        var injector = new SpeechInjector();
        var stage = new SpeechStage(Rate, injector);
        injector.Enqueue(new SpeechClip(TestSignals.Sine(300, 24000, 24000 * 3, 0.5f), 24000, "x"));
        var buffer = new float[Block];
        stage.Process(buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) stage.Process(buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
