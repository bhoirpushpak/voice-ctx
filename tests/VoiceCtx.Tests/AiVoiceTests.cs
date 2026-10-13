using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VoiceCtx.Audio.AiVoice;

namespace VoiceCtx.Tests;

public class FftTests
{
    [Fact]
    public void MatchesDirectDft()
    {
        const int n = 64;
        var rng = new Random(3);
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++) { re[i] = rng.NextDouble() - 0.5; im[i] = rng.NextDouble() - 0.5; }
        var (xr, xi) = ((double[])re.Clone(), (double[])im.Clone());

        new Fft(n).Forward(re, im);

        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int t = 0; t < n; t++)
            {
                double a = -2 * Math.PI * k * t / n;
                sr += xr[t] * Math.Cos(a) - xi[t] * Math.Sin(a);
                si += xr[t] * Math.Sin(a) + xi[t] * Math.Cos(a);
            }
            Assert.Equal(sr, re[k], 9);
            Assert.Equal(si, im[k], 9);
        }
    }
}

public class RmvpeFrontEndTests
{
    private static readonly JsonElement Fixture = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rmvpe_frontend.json"))).RootElement;

    internal static float[] FixtureSignal()
    {
        var x = new float[8000];
        for (int n = 0; n < x.Length; n++)
            x[n] = (float)(0.4 * Math.Sin(2 * Math.PI * 220 * n / 16000) + 0.2 * Math.Sin(2 * Math.PI * 523.25 * n / 16000));
        return x;
    }

    [Fact]
    public void MelFilterbankMatchesLibrosa()
    {
        var expected = Fixture.GetProperty("melBasisRowSums").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var actual = new MelFrontEnd().BasisRowSums();
        Assert.Equal(expected.Length, actual.Length);
        for (int m = 0; m < expected.Length; m++)
            Assert.True(Math.Abs(expected[m] - actual[m]) < 1e-5f, $"mel band {m}: {actual[m]} vs librosa {expected[m]}");
    }

    [Fact]
    public void LogMelMatchesLibrosa()
    {
        var expected = Fixture.GetProperty("mel").EnumerateArray()
            .Select(row => row.EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToArray();
        var signal = FixtureSignal();
        int frames = MelFrontEnd.FrameCount(signal.Length);
        int padded = MelFrontEnd.PaddedFrameCount(frames);
        Assert.Equal(expected[0].Length, frames);

        var mel = new float[MelFrontEnd.Mels * padded];
        new MelFrontEnd().Compute(signal, mel);

        double worst = 0;
        for (int m = 0; m < MelFrontEnd.Mels; m++)
            for (int t = 0; t < frames; t++)
                worst = Math.Max(worst, Math.Abs(expected[m][t] - mel[m * padded + t]));
        Assert.True(worst < 2e-3, $"max log-mel difference {worst}");
        for (int m = 0; m < MelFrontEnd.Mels; m++)
            for (int t = frames; t < padded; t++) Assert.Equal(0f, mel[m * padded + t]);
    }

    [Fact]
    public void DecodesSaliencePeakToHz()
    {
        var salience = new float[2 * PitchMath.SalienceBins];
        for (int c = 0; c < PitchMath.SalienceBins; c++)
            salience[c] = (float)Math.Exp(-0.5 * Math.Pow((c - 100) / 1.5, 2));   // voiced frame peaked at bin 100
        salience[PitchMath.SalienceBins + 50] = 0.02f;                              // below threshold → unvoiced

        var f0 = new float[2];
        PitchMath.DecodeSalience(salience, 2, f0);

        double expectedHz = 10 * Math.Pow(2, (20 * 100 + 1997.3794084376191) / 1200);
        Assert.Equal(expectedHz, f0[0], 2);
        Assert.Equal(0f, f0[1]);
    }

    [Fact]
    public void CoarsePitchTokensMatchRvc()
    {
        Assert.Equal(1, PitchMath.Coarse(0));
        Assert.Equal(1, PitchMath.Coarse(50));
        Assert.Equal(255, PitchMath.Coarse(1100));
        Assert.Equal(255, PitchMath.Coarse(3000));
        long previous = 0;
        for (float hz = 60; hz < 1000; hz += 7)
        {
            long token = PitchMath.Coarse(hz);
            Assert.True(token >= previous);
            previous = token;
        }
    }
}

public sealed class VoiceLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoiceCtxTests", Guid.NewGuid().ToString("N"));

    private static string VoiceJson(string id, int frames = 68) =>
        $$"""{"format":1,"id":"{{id}}","name":"Test {{id}}","sampleRate":40000,"blockMs":100,"contextMs":600,"frames":{{frames}},"tail":14,"marginFrames":4,"gender":"male","builtIn":false}""";

    [Fact]
    public void ScansOnlyCompleteSupportedVoices()
    {
        var voices = Path.Combine(_root, "voices");
        Directory.CreateDirectory(voices);
        File.WriteAllText(Path.Combine(voices, "good.json"), VoiceJson("good"));
        File.WriteAllBytes(Path.Combine(voices, "good.onnx"), [1]);
        File.WriteAllText(Path.Combine(voices, "no-model.json"), VoiceJson("no-model"));
        File.WriteAllText(Path.Combine(voices, "bad-shape.json"), VoiceJson("bad-shape", frames: 70));
        File.WriteAllBytes(Path.Combine(voices, "bad-shape.onnx"), [1]);
        File.WriteAllText(Path.Combine(voices, "broken.json"), "{ nope");
        File.WriteAllBytes(Path.Combine(voices, "broken.onnx"), [1]);

        var library = new VoiceLibrary(_root);
        var found = library.ScanVoices();
        Assert.Equal(["good"], found.Select(v => v.Id));
        Assert.EndsWith("good.onnx", found[0].ModelPath);
        Assert.False(library.BaseModelsInstalled);
    }

    [Fact]
    public void ImportRequiresDescriptionAndCopiesBothFiles()
    {
        var source = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "x.onnx"), [1, 2, 3]);
        var library = new VoiceLibrary(Path.Combine(_root, "lib"));
        Assert.Throws<InvalidOperationException>(() => library.Import(Path.Combine(source, "x.onnx")));

        File.WriteAllText(Path.Combine(source, "x.json"), VoiceJson("user-x"));
        var info = library.Import(Path.Combine(source, "x.onnx"));
        Assert.True(File.Exists(Path.Combine(library.VoicesFolder, "user-x.onnx")));
        Assert.True(File.Exists(Path.Combine(library.VoicesFolder, "user-x.json")));
        Assert.Single(library.ScanVoices());
        Assert.Equal("user-x", info.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public class AiVoiceStageTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private const int Rate = 48000;
    private const int Block = 480;

    /// <summary>Produces a phase-continuous sine so a correct stage outputs a clean tone.</summary>
    private sealed class SineConverter(double hz, int delayMs = 0) : IVoiceConverter
    {
        private long _blockStart;
        public int ContextSamples16k => 11200;
        public int BlockSamples16k => 1600;
        public int OutputSampleRate => 40000;
        public int OutputSamples => 5600;
        public int MarginSamples => 1600;
        public int Calls;
        public volatile int DelayMs = delayMs;

        public void Convert(ReadOnlySpan<float> context16k, float pitchShiftSemitones, Span<float> output)
        {
            Calls++;
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            long first = _blockStart - MarginSamples;
            for (int i = 0; i < OutputSamples; i++)
                output[i] = 0.5f * (float)Math.Sin(2 * Math.PI * hz * (first + i) / OutputSampleRate);
            _blockStart += OutputSamples - MarginSamples;
        }

        public void Dispose() { }
    }

    private static float[] RunRealtime(AiVoiceStage stage, Func<int, float[]> input, int blocks, Action<int>? perBlock = null)
    {
        var output = new float[blocks * Block];
        var clock = Stopwatch.StartNew();
        for (int b = 0; b < blocks; b++)
        {
            perBlock?.Invoke(b);
            var buffer = input(b);
            stage.Process(buffer);
            buffer.CopyTo(output, b * Block);
            var due = TimeSpan.FromMilliseconds((b + 1) * 10.0);
            while (clock.Elapsed < due) Thread.Sleep(1);
        }
        return output;
    }

    private static float[] Dry(int b) => TestSignals.Sine(110, Rate, Block, 0.2f).Select((v, i) => 0.2f * (float)Math.Sin(2 * Math.PI * 110 * (b * Block + i) / Rate)).ToArray();

    [Fact]
    public void FadesFromDryToCleanConvertedVoice()
    {
        var controls = new AiVoiceControls { Enabled = true };
        using var service = new AiVoiceService(new SineConverter(300));
        using var stage = new AiVoiceStage(Rate, Block, controls, service);

        var output = RunRealtime(stage, Dry, blocks: 150);
        var steady = output.AsSpan(100 * Block);

        Assert.Equal(300, TestSignals.EstimateFrequency(steady, Rate), 1.0);
        float maxStep = 0;
        for (int i = 1; i < steady.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(steady[i] - steady[i - 1]));
        float sineStep = (float)(0.5 * 2 * Math.PI * 300 / Rate);
        Assert.True(maxStep < sineStep * 1.5f, $"discontinuity {maxStep} vs sine slope {sineStep}");
        Assert.Equal(0, stage.Underruns);
    }

    /// <summary>Passes the newest input block through (16 kHz → 40 kHz) so real delay can be measured.</summary>
    private sealed class EchoConverter : IVoiceConverter
    {
        private readonly float[] _previousTail = new float[1600];
        public int ContextSamples16k => 11200;
        public int BlockSamples16k => 1600;
        public int OutputSampleRate => 40000;
        public int OutputSamples => 5600;
        public int MarginSamples => 1600;

        public void Convert(ReadOnlySpan<float> context16k, float pitchShiftSemitones, Span<float> output)
        {
            var block = context16k[^1600..];
            _previousTail.CopyTo(output);
            for (int j = 0; j < 4000; j++)
            {
                double pos = j * 0.4;
                int i = (int)pos;
                float a = block[i], b = i + 1 < block.Length ? block[i + 1] : a;
                output[1600 + j] = a + (float)(pos - i) * (b - a);
            }
            output[^1600..].CopyTo(_previousTail);
        }

        public void Dispose() { }
    }

    [Fact]
    public void EndToEndDelayIsBoundedAndConstant()
    {
        var controls = new AiVoiceControls { Enabled = true };
        using var service = new AiVoiceService(new EchoConverter());
        using var stage = new AiVoiceStage(Rate, Block, controls, service);

        // 150 ms tone bursts every 600 ms; compare burst onsets in input and output.
        float[] Input(int b)
        {
            var x = new float[Block];
            for (int i = 0; i < Block; i++)
            {
                long n = (long)b * Block + i;
                x[i] = n % (Rate * 6 / 10) < Rate * 15 / 100 ? 0.5f * (float)Math.Sin(2 * Math.PI * 440 * n / Rate) : 0f;
            }
            return x;
        }

        var output = RunRealtime(stage, Input, blocks: 500);
        var input = Enumerable.Range(0, 500).SelectMany(Input).ToArray();

        static List<int> Onsets(float[] x)
        {
            var onsets = new List<int>();
            const int window = 240;
            bool on = false;
            for (int i = 0; i + window < x.Length; i += window)
            {
                double rms = TestSignals.Rms(x.AsSpan(i, window));
                if (!on && rms > 0.1) { onsets.Add(i); on = true; }
                else if (on && rms < 0.02) on = false;
            }
            return onsets;
        }

        var inOnsets = Onsets(input);
        var outOnsets = Onsets(output).Where(o => o > Rate).ToList();
        var delays = outOnsets.Select(o => o - inOnsets.Where(i => i <= o).Max()).Select(d => 1000.0 * d / Rate).ToList();

        log.WriteLine($"AI Voice end-to-end delay: {delays.Average():F0} ms (min {delays.Min():F0}, max {delays.Max():F0})");
        Assert.True(delays.Count >= 4, $"only {delays.Count} bursts detected");
        Assert.True(delays.Max() - delays.Min() < 15, $"delay drifts: {string.Join(", ", delays.Select(d => d.ToString("F0")))} ms");
        Assert.InRange(delays.Average(), 120, 220);
        Assert.InRange(Math.Abs(delays.Average() - stage.LatencyMs), 0, 15);
        Assert.Equal(0, stage.Underruns);
    }

    [Fact]
    public void FallsBackToDryWhenInferenceIsTooSlow()
    {
        var controls = new AiVoiceControls { Enabled = true };
        var converter = new SineConverter(300);
        using var service = new AiVoiceService(converter);
        using var stage = new AiVoiceStage(Rate, Block, controls, service);

        var output = RunRealtime(stage, Dry, blocks: 220, perBlock: b => { if (b == 100) converter.DelayMs = 250; });

        Assert.True(stage.Underruns > 0);
        Assert.Equal(110, TestSignals.EstimateFrequency(output.AsSpan(200 * Block), Rate), 2.0);
        Assert.All(output, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1f));
    }

    [Fact]
    public void SlowInferenceDuringSilenceDoesNotLeaveNextSpeechDry()
    {
        // Live translate: the mic is muted while the GPU recognises speech, then a spoken line arrives.
        var controls = new AiVoiceControls { Enabled = true };
        var converter = new SineConverter(300);
        using var service = new AiVoiceService(converter);
        using var stage = new AiVoiceStage(Rate, Block, controls, service);

        var output = RunRealtime(stage, b => b is >= 60 and < 150 ? new float[Block] : Dry(b), blocks: 220, perBlock: b =>
        {
            if (b == 80) converter.DelayMs = 250;
            if (b == 110) converter.DelayMs = 0;
        });

        Assert.True(stage.Underruns > 0);
        Assert.False(stage.IsFallingBehind);
        // Speech resumes at block 150 and is converted (300 Hz) well within the 2 s a hold would last.
        Assert.Equal(300, TestSignals.EstimateFrequency(output.AsSpan(190 * Block), Rate), 2.0);
    }

    [Fact]
    public void DisablingReturnsBitExactDrySignal()
    {
        var controls = new AiVoiceControls { Enabled = true };
        using var service = new AiVoiceService(new SineConverter(300));
        using var stage = new AiVoiceStage(Rate, Block, controls, service);

        var dryCopies = new List<float[]>();
        var output = RunRealtime(stage, b => { var d = Dry(b); dryCopies.Add((float[])d.Clone()); return d; }, blocks: 160,
            perBlock: b => { if (b == 100) controls.Enabled = false; });

        for (int b = 110; b < 160; b++)
            Assert.Equal(dryCopies[b], output.AsSpan(b * Block, Block).ToArray());
    }

    [Fact]
    public void AudioThreadProcessDoesNotAllocate()
    {
        var controls = new AiVoiceControls { Enabled = true };
        using var service = new AiVoiceService(new SineConverter(300));
        using var stage = new AiVoiceStage(Rate, Block, controls, service);
        var buffer = new float[Block];
        RunRealtime(stage, Dry, blocks: 40);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) stage.Process(buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

/// <summary>Runs only when the AI Voice models are installed (tools/aivoice/build_models.py).</summary>
public sealed class RequiresAiVoiceModelsFact : FactAttribute
{
    public RequiresAiVoiceModelsFact()
    {
        var library = new VoiceLibrary(VoiceLibrary.DefaultRoot);
        if (!library.BaseModelsInstalled || library.ScanVoices().Count == 0)
            Skip = "AI Voice models not installed";
    }
}

public class RvcConverterIntegrationTests
{
    [RequiresAiVoiceModelsFact]
    public void ConvertsSpeechLikeSignalWithInstalledModels()
    {
        var library = new VoiceLibrary(VoiceLibrary.DefaultRoot);
        using var runtime = RvcRuntime.Load(library);
        using var converter = new RvcConverter(runtime, library.ScanVoices()[0]);

        var context = new float[converter.ContextSamples16k];
        var tone = RmvpeFrontEndTests.FixtureSignal();
        for (int i = 0; i < context.Length; i++) context[i] = tone[i % tone.Length];
        var output = new float[converter.OutputSamples];

        for (int i = 0; i < 5; i++) converter.Convert(context, 0, output);   // DirectML compiles kernels lazily
        double best = double.MaxValue;
        for (int i = 0; i < 10; i++)
        {
            var timer = Stopwatch.StartNew();
            converter.Convert(context, 0, output);
            best = Math.Min(best, timer.Elapsed.TotalMilliseconds);
        }

        Assert.All(output, s => Assert.True(float.IsFinite(s)));
        Assert.True(TestSignals.Rms(output) > 1e-3, "converter produced silence");
        if (runtime.UsesGpu)
            Assert.True(best < 100, $"block took {best:F0} ms on {runtime.DeviceName}; the budget is 100 ms");
    }

    [Fact]
    public void GpuEnumerationDoesNotThrow()
    {
        var adapters = GpuSelector.EnumerateAdapters();
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }
}
