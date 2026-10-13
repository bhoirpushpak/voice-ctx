using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoiceCtx.Audio.Translate;

public sealed record Transcription(string Text, string Language, float NoSpeechProbability);

/// <summary>
/// Speech-to-text with whisper.cpp (Whisper.net). Prefers the Vulkan GPU runtime (NVIDIA, AMD and
/// Intel) and falls back to the CPU runtime automatically.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private static readonly HashSet<string> Hallucinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thank you.", "thanks.", "thanks for watching!", "thanks for watching.", "thank you for watching.",
        "thank you for watching!", "you", "bye.", "bye!", "[music]", "(music)", "[blank_audio]", "(silence)", ".", "...",
        "subtitles by the amara.org community", "please subscribe.",
    };

    private readonly WhisperFactory _factory;
    private readonly Dictionary<string, WhisperProcessor> _processors = new();

    public WhisperTranscriber(string modelPath, bool useGpu = true)
    {
        var gpu = useGpu ? VulkanDevices.ChooseBest() : null;
        RuntimeOptions.RuntimeLibraryOrder = gpu != null ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu] : [RuntimeLibrary.Cpu];
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
        {
            UseGpu = gpu != null,
            GpuDevice = gpu?.Index ?? 0,
        });
        UsesGpu = gpu != null && RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan;
        DeviceName = UsesGpu ? gpu!.Name : "CPU";
    }

    /// <summary>True when the Vulkan GPU runtime was loaded (otherwise Whisper runs on the CPU).</summary>
    public bool UsesGpu { get; }

    /// <summary>The GPU (or "CPU") speech recognition runs on.</summary>
    public string DeviceName { get; }

    /// <param name="samples16k">One utterance, mono 16 kHz.</param>
    /// <param name="language">Whisper language code, or "auto" to detect.</param>
    public Transcription Transcribe(float[] samples16k, string language)
    {
        var processor = GetProcessor(language);
        var text = new StringBuilder();
        string detected = language;
        float noSpeech = 0;
        var segments = processor.ProcessAsync(samples16k).GetAsyncEnumerator();
        try
        {
            while (segments.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var segment = segments.Current;
                text.Append(segment.Text);
                if (!string.IsNullOrEmpty(segment.Language)) detected = segment.Language;
                noSpeech = Math.Max(noSpeech, segment.NoSpeechProbability);
            }
        }
        finally
        {
            segments.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return new Transcription(text.ToString().Trim(), detected, noSpeech);
    }

    /// <summary>True when the transcription is probably Whisper inventing text for noise/silence.</summary>
    public static bool IsLikelyHallucination(Transcription t, double durationSeconds)
    {
        string text = t.Text.Trim();
        if (text.Length == 0 || t.NoSpeechProbability > 0.6f) return true;
        if (!text.Any(char.IsLetterOrDigit)) return true;
        return durationSeconds < 3 && Hallucinations.Contains(text);
    }

    private WhisperProcessor GetProcessor(string language)
    {
        if (_processors.TryGetValue(language, out var processor)) return processor;
        processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2))
            .WithNoContext()
            .WithSingleSegment()
            .Build();
        _processors[language] = processor;
        return processor;
    }

    public void Dispose()
    {
        foreach (var p in _processors.Values) p.Dispose();
        _processors.Clear();
        _factory.Dispose();
    }
}
