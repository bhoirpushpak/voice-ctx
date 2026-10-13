using System.Diagnostics;
using VoiceCtx.Audio.Speech;
using VoiceCtx.Audio.Translate;

namespace VoiceCtx.Tests;

public class SpeechSegmenterTests
{
    private const int Frame = 512;

    private static List<float[]> Feed(SpeechSegmenter segmenter, IEnumerable<float> probabilities, float level = 0.3f)
    {
        var utterances = new List<float[]>();
        var frame = new float[Frame];
        foreach (float p in probabilities)
        {
            Array.Fill(frame, p > 0.5f ? level : 0.001f);
            var u = segmenter.Push(frame, p);
            if (u != null) utterances.Add(u);
        }
        return utterances;
    }

    private static IEnumerable<float> Repeat(float p, double seconds) =>
        Enumerable.Repeat(p, (int)Math.Ceiling(seconds * 16000 / Frame));

    [Fact]
    public void CutsUtteranceAfterPauseAndKeepsPreRoll()
    {
        var segmenter = new SpeechSegmenter(Frame, minSilenceSeconds: 0.6, preRollSeconds: 0.2);
        var utterances = Feed(segmenter, Repeat(0.02f, 1).Concat(Repeat(0.95f, 2)).Concat(Repeat(0.02f, 1)));

        var u = Assert.Single(utterances);
        double seconds = u.Length / 16000.0;
        Assert.InRange(seconds, 2.1, 2.5);                 // 2 s speech + pre-roll + a little tail
        Assert.True(u[0] < 0.01f, "utterance should start with pre-roll audio");
    }

    [Fact]
    public void ShortPausesDoNotSplitASentence()
    {
        var segmenter = new SpeechSegmenter(Frame, minSilenceSeconds: 0.6);
        var probabilities = Repeat(0.9f, 1).Concat(Repeat(0.1f, 0.3)).Concat(Repeat(0.9f, 1)).Concat(Repeat(0.05f, 1));
        Assert.Single(Feed(segmenter, probabilities));
    }

    [Fact]
    public void IgnoresBlipsShorterThanMinimumSpeech()
    {
        var segmenter = new SpeechSegmenter(Frame, minSpeechSeconds: 0.25);
        Assert.Empty(Feed(segmenter, Repeat(0.9f, 0.1).Concat(Repeat(0.01f, 1.5))));
    }

    [Fact]
    public void ForceSplitsVeryLongSpeech()
    {
        var segmenter = new SpeechSegmenter(Frame, maxSeconds: 5);
        var utterances = Feed(segmenter, Repeat(0.95f, 12).Concat(Repeat(0.01f, 1)));
        Assert.True(utterances.Count >= 2);
        Assert.All(utterances, u => Assert.True(u.Length <= 5.1 * 16000));
    }
}

public class TranslateBasicsTests
{
    [Fact]
    public void LanguageTableIsConsistent()
    {
        Assert.Equal(Languages.All.Count, Languages.All.Select(l => l.Whisper).Distinct().Count());
        Assert.Equal(Languages.All.Count, Languages.All.Select(l => l.Nllb).Distinct().Count());
        Assert.All(Languages.All, l => Assert.Matches(@"^[a-z]{3}_[A-Z][a-z]{3}$", l.Nllb));
        Assert.Equal("hin_Deva", Languages.FromWhisper("HI")!.Nllb);
        Assert.Null(Languages.FromWhisper("xx"));
    }

    [Theory]
    [InlineData("Thank you.", 0.0f, 1.0, true)]
    [InlineData("Thank you.", 0.0f, 5.0, false)]
    [InlineData("...", 0.0f, 2.0, true)]
    [InlineData("See you tomorrow.", 0.9f, 2.0, true)]
    [InlineData("See you tomorrow.", 0.1f, 2.0, false)]
    [InlineData("", 0.0f, 2.0, true)]
    public void FiltersWhisperHallucinations(string text, float noSpeech, double seconds, bool expected) =>
        Assert.Equal(expected, WhisperTranscriber.IsLikelyHallucination(new Transcription(text, "en", noSpeech), seconds));

    [Fact]
    public void VulkanEnumerationDoesNotThrow() =>
        Assert.All(VulkanDevices.Enumerate(), d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
}

/// <summary>Runs only when the Live translate models are installed (tools/translate/build_models.py).</summary>
public sealed class RequiresTranslateModelsFact : FactAttribute
{
    public RequiresTranslateModelsFact()
    {
        if (!new TranslateLibrary(TranslateLibrary.DefaultRoot).Installed) Skip = "Live translate models not installed";
        else if (WindowsTts.InstalledVoices().Count == 0) Skip = "No Windows voices to generate test speech";
    }
}

[CollectionDefinition(nameof(LiveTranslateIntegrationTests), DisableParallelization = true)]
public class LiveTranslateCollection;

/// <summary>Heavy GPU/CPU work; runs alone so it can't disturb the timing and allocation tests.</summary>
[Collection(nameof(LiveTranslateIntegrationTests))]
public class LiveTranslateIntegrationTests
{
    private static readonly TranslateLibrary Library = new(TranslateLibrary.DefaultRoot);

    [RequiresTranslateModelsFact]
    public void TranslatorCoversEveryListedLanguage()
    {
        using var nllb = new NllbTranslator(Library.NllbFolder);
        Assert.All(Languages.All, l => Assert.True(nllb.Supports(l.Nllb), $"{l.Name} ({l.Nllb}) missing from NLLB"));
        Assert.Equal("Hola a todos.", nllb.Translate("Hello everyone.", "eng_Latn", "spa_Latn"));
    }

    [RequiresTranslateModelsFact]
    public async Task SpokenEnglishIsTranslatedIntoSpanish()
    {
        var english = WindowsTts.InstalledVoices().First(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var clip = await new WindowsTts().SynthesizeAsync("Hello everyone, I will be back in five minutes.", english.Id);

        var injector = new SpeechInjector();
        using var service = new LiveTranslateService(Library, injector);
        service.Options.From = "auto";
        service.Options.To = "es";
        var lines = new List<TranslatedLine>();
        service.LineTranslated += (_, line) => { lock (lines) lines.Add(line); };

        await service.SetEnabledAsync(true);
        Assert.Equal(0f, injector.BaseMicGain);                       // your real voice is muted while translating

        // Feed the clip like a live mic (faster than real time, but paced so the 4 s tap ring never overflows).
        int rate = clip.SampleRate, block = rate / 10;
        service.AttachPipeline(rate);
        var audio = new float[rate].Concat(clip.Samples).Concat(new float[rate * 2]).ToArray();
        for (int i = 0; i + block <= audio.Length; i += block)
        {
            service.Tap(audio.AsSpan(i, block));
            await Task.Delay(20);
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            lock (lines) if (lines.Count > 0) break;
            await Task.Delay(100);
        }

        TranslatedLine line;
        lock (lines) line = Assert.Single(lines);
        Assert.Equal("English", line.FromLanguage);
        Assert.Contains("cinco minutos", line.Translation, StringComparison.OrdinalIgnoreCase);
        Assert.True(line.Elapsed < TimeSpan.FromSeconds(5), $"translation took {line.Elapsed.TotalSeconds:F1} s");

        await service.SetEnabledAsync(false);
        Assert.Equal(1f, injector.BaseMicGain);
    }
}
