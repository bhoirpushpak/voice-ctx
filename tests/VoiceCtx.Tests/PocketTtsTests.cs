using System.Diagnostics;
using System.IO;
using VoiceCtx.App.ViewModels;
using VoiceCtx.Audio.Speech;
using VoiceCtx.Audio.Translate;
using Xunit.Abstractions;

namespace VoiceCtx.Tests;

public class SpeechTextTests
{
    private static readonly PocketTtsSettings Kyutai = new()
    {
        Language = "fr",
        ReplaceCharacters = new() { ["\""] = "", ["«"] = "", ["»"] = "", ["’"] = "'", [":"] = "," },
        RemoveSemicolons = true,
    };

    private static readonly PocketTtsSettings Hindi = new()
    {
        Language = "hi",
        AppendTerminalPunctuation = false,
        CapitalizeFirstLetter = false,
        StripPunctuation = true,
    };

    [Theory]
    [InlineData("hello there", "Hello there.")]
    [InlineData("Is it you?", "Is it you?")]
    [InlineData("wait,", "Wait.")]
    [InlineData("« Bonjour » : c’est moi; oui", "Bonjour , c'est moi, oui.")]   // same as pocket-tts
    public void PreparesTextLikePocketTts(string raw, string expected) =>
        Assert.Equal(expected, PocketTts.PrepareText(raw, Kyutai).Text);

    [Fact]
    public void ShortLinesGetMoreTrailingFrames()
    {
        Assert.Equal(3, PocketTts.PrepareText("Yes please", Kyutai).FramesAfterEosGuess);
        Assert.Equal(1, PocketTts.PrepareText("I will be back in five minutes", Kyutai).FramesAfterEosGuess);
    }

    [Fact]
    public void StripsPunctuationForHindi() =>
        Assert.Equal("मैं पांच मिनट में वापस आऊंगा", PocketTts.PrepareText("मैं पांच मिनट में, वापस आऊंगा।", Hindi).Text);

    [Fact]
    public void EmptyAfterCleanupIsEmpty() => Assert.Equal("", PocketTts.PrepareText(" ।? ", Hindi).Text);
}

public class SpeechLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "voicectx-speech-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SavesMyVoiceAt24kAndListsItFirst()
    {
        var library = new SpeechLibrary(_root);
        Directory.CreateDirectory(library.VoicesFolder);
        WavDecoder.WriteMono16(Path.Combine(library.VoicesFolder, "narrator.wav"), new float[24000], 24000);
        File.WriteAllText(Path.Combine(library.VoicesFolder, "narrator.json"), """{ "name": "Narrator", "builtIn": true }""");

        var mine = library.SaveMyVoice(TestSignals.Sine(220, 48000, 48000 * 6, 0.4f), 48000);

        var (samples, rate) = WavDecoder.DecodeMono(File.ReadAllBytes(mine.WavPath));
        Assert.Equal(24000, rate);
        Assert.InRange(samples.Length, 24000 * 6 - 50, 24000 * 6 + 50);
        Assert.Equal(220, TestSignals.EstimateFrequency(samples, 24000), 2.0);

        var voices = library.ScanVoices();
        Assert.Equal(["my-voice", "narrator"], voices.Select(v => v.Id));
        Assert.True(voices[0].IsMine);
        Assert.True(voices[1].BuiltIn);
    }

    [Fact]
    public void ImportRejectsShortClipsAndDeleteKeepsBuiltIns()
    {
        var library = new SpeechLibrary(_root);
        string shortClip = Path.Combine(_root, "short.wav");
        Directory.CreateDirectory(_root);
        WavDecoder.WriteMono16(shortClip, new float[16000], 16000);
        Assert.Throws<InvalidDataException>(() => library.Import(shortClip));

        string clip = Path.Combine(_root, "Friend Voice.wav");
        WavDecoder.WriteMono16(clip, TestSignals.Sine(150, 16000, 16000 * 8, 0.3f), 16000);
        var imported = library.Import(clip);
        Assert.Equal("imported-friend-voice", imported.Id);
        Assert.Equal("Friend Voice", library.ScanVoices().Single().Name);

        library.Delete(imported);
        Assert.Empty(library.ScanVoices());
    }

    [Fact]
    public void IgnoresIncompleteModelFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "en"));
        File.WriteAllText(Path.Combine(_root, "en", "voicectx.json"), """{ "language": "en", "name": "English" }""");
        Assert.Empty(new SpeechLibrary(_root).ScanLanguages());
    }

    [Fact]
    public void WavRoundTrip()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "x.wav");
        WavDecoder.WriteMono16(path, [0f, 0.5f, -0.5f, 1f], 24000);
        var (samples, rate) = WavDecoder.DecodeMono(File.ReadAllBytes(path));
        Assert.Equal(24000, rate);
        Assert.Equal([0f, 0.5f, -0.5f, 1f], samples.Select(s => MathF.Round(s, 3)));
    }

    [Fact]
    public void TrimsSilenceAroundARecording()
    {
        var audio = new float[48000 * 4];
        TestSignals.Sine(200, 48000, 48000, 0.5f).CopyTo(audio, 48000 * 2);
        var trimmed = SpeechVoicesViewModel.TrimSilence(audio, 48000);
        Assert.InRange(trimmed.Length / 48000.0, 1.3, 1.45);   // 1 s of speech + 0.2 s margin each side
    }
}

/// <summary>Runs only when the Hindi speech model is installed (tools/tts/build_models.py --languages hi).</summary>
public sealed class RequiresHindiSpeechFact : FactAttribute
{
    public RequiresHindiSpeechFact()
    {
        var library = new SpeechLibrary(SpeechLibrary.DefaultRoot);
        if (!library.ScanLanguages().Any(l => l.Code == "hi")) Skip = "Hindi speech model not installed";
        else if (!library.ScanVoices().Any(v => v.BuiltIn)) Skip = "Ready-made voices not installed";
    }
}

[Collection(nameof(LiveTranslateIntegrationTests))]
public class PocketTtsIntegrationTests(ITestOutputHelper log)
{
    [RequiresHindiSpeechFact]
    public void TokenChunksStayWithinTheModelLimit()
    {
        using var tts = new PocketTts(Path.Combine(SpeechLibrary.DefaultRoot, "hi"));
        string longText = string.Join(" ", Enumerable.Repeat("आज सुबह मौसम बहुत सुहावना था और बच्चे मैदान में खेल रहे थे।", 6));
        var chunks = tts.SplitIntoChunks(longText);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.InRange(tts.Tokenize(tts.Prepare(c).Text).Count, 1, 50));
        Assert.Equal(longText.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
    }

    [RequiresHindiSpeechFact]
    public async Task SpeaksHindiThatWhisperUnderstands()
    {
        using var speech = new SpeechService(new SpeechLibrary(SpeechLibrary.DefaultRoot));
        var voice = speech.Voices.First(v => v.BuiltIn);
        await speech.WarmUpAsync("hi", voice.Id);

        var timer = Stopwatch.StartNew();
        var clip = await speech.SynthesizeAsync("क्या आप मेरी आवाज़ सुन सकते हैं?", "hi", voice.Id);
        double seconds = clip.Samples.Length / (double)clip.SampleRate;
        log.WriteLine($"{seconds:F2} s of speech in {timer.Elapsed.TotalSeconds:F2} s");

        Assert.Equal(24000, clip.SampleRate);
        Assert.InRange(seconds, 1.0, 5.0);
        Assert.True(timer.Elapsed.TotalSeconds < seconds, "should be faster than real time");
        Assert.All(clip.Samples, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.5f));

        var translate = new TranslateLibrary(TranslateLibrary.DefaultRoot);
        if (!File.Exists(translate.WhisperModel)) return;
        using var whisper = new WhisperTranscriber(translate.WhisperModel);
        // Generation samples randomly and the Hindi model is an early release: allow one retry.
        for (int attempt = 1; ; attempt++)
        {
            var heard = whisper.Transcribe(PocketTts.Resample(clip.Samples, 24000, 16000), "hi");
            log.WriteLine($"Attempt {attempt}, Whisper heard: {heard.Text}");
            if (heard.Text.Contains("आवाज") || attempt == 2)
            {
                Assert.Contains("आवाज", heard.Text);
                break;
            }
            clip = await speech.SynthesizeAsync("क्या आप मेरी आवाज़ सुन सकते हैं?", "hi", voice.Id);
        }
    }
}
