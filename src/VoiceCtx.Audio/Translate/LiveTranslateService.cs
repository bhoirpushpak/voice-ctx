using System.Collections.Concurrent;
using System.Diagnostics;
using VoiceCtx.Audio.Dsp;
using VoiceCtx.Audio.Engine;
using VoiceCtx.Audio.Speech;

namespace VoiceCtx.Audio.Translate;

public enum TranslateStatus { NotInstalled, Off, Loading, Listening, Translating, Error }

/// <summary>One translated utterance, for subtitles.</summary>
public sealed record TranslatedLine(string Original, string Translation, string FromLanguage, string ToLanguage, TimeSpan Elapsed);

/// <summary>Live translate settings shared with the UI.</summary>
public sealed class LiveTranslateOptions
{
    private volatile bool _keepOriginal;

    /// <summary>Whisper code of the language you speak, or "auto".</summary>
    public string From { get; set; } = "auto";

    /// <summary>Whisper code of the language to translate into.</summary>
    public string To { get; set; } = "en";

    /// <summary>Windows voice used to speak translations (null = best match for the target language).</summary>
    public string? VoiceId { get; set; }

    public double SpeakingRate { get; set; } = 1.0;

    /// <summary>Keep your real voice quietly under the translation instead of muting it.</summary>
    public bool KeepOriginal
    {
        get => _keepOriginal;
        set => _keepOriginal = value;
    }
}

/// <summary>Where the Live translate models live (%AppData%\VoiceCtx\Translate).</summary>
public sealed class TranslateLibrary(string root)
{
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceCtx", "Translate");

    public string Root { get; } = root;
    public string WhisperModel => Path.Combine(Root, "whisper", "ggml-large-v3-turbo-q5_0.bin");
    public string VadModel => Path.Combine(Root, "vad", "silero_vad.onnx");
    public string NllbFolder => Path.Combine(Root, "nllb");
    public string CreditsPath => Path.Combine(Root, "CREDITS.txt");

    public bool Installed =>
        File.Exists(WhisperModel) && File.Exists(VadModel)
        && File.Exists(Path.Combine(NllbFolder, "encoder_model.onnx"))
        && File.Exists(Path.Combine(NllbFolder, "decoder_model_merged.onnx"))
        && File.Exists(Path.Combine(NllbFolder, "sentencepiece.bpe.model"))
        && File.Exists(Path.Combine(NllbFolder, "vocab.json"));
}

/// <summary>
/// Live translate: listens to your (cleaned-up) mic, cuts it into sentences, transcribes with
/// Whisper, translates with NLLB, speaks the result with a Windows voice and injects it into the
/// mic path, where AI Voice and voice effects apply. Your real mic is muted (or kept quiet) while on.
/// </summary>
public sealed class LiveTranslateService : IDisposable
{
    private const int VadRate = SpeechSegmenter.SampleRate;

    private readonly SpeechInjector _injector;
    private readonly WindowsTts _tts = new();
    private readonly BlockingCollection<float[]> _utterances = new(boundedCapacity: 8);
    private readonly object _modelLock = new();
    private SpscRingBuffer? _tap;
    private int _tapRate;
    private SileroVad? _vad;
    private WhisperTranscriber? _whisper;
    private NllbTranslator? _nllb;
    private Thread? _listenThread;
    private Thread? _translateThread;
    private volatile bool _running;
    private volatile bool _enabled;
    private bool _disposed;

    public LiveTranslateService(TranslateLibrary library, SpeechInjector injector)
    {
        Library = library;
        _injector = injector;
        Status = library.Installed ? TranslateStatus.Off : TranslateStatus.NotInstalled;
        StatusMessage = library.Installed ? "Off" : "Live translate models are not installed";
    }

    public event EventHandler? StatusChanged;

    /// <summary>Raised on a worker thread for every translated sentence.</summary>
    public event EventHandler<TranslatedLine>? LineTranslated;

    public TranslateLibrary Library { get; }
    public LiveTranslateOptions Options { get; } = new();
    public TranslateStatus Status { get; private set; }
    public string StatusMessage { get; private set; }
    public bool Enabled => _enabled;
    public bool SpeechRecognitionOnGpu => _whisper?.UsesGpu ?? false;

    /// <summary>Turns Live translate on (loading models on first use) or off.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled == _enabled || _disposed) return;
        if (!enabled)
        {
            _enabled = false;
            ApplyMicGain();
            SetStatus(TranslateStatus.Off, "Off");
            return;
        }
        if (!Library.Installed)
        {
            SetStatus(TranslateStatus.NotInstalled, "Live translate models are not installed");
            return;
        }

        SetStatus(TranslateStatus.Loading, "Loading speech recognition and translation models…");
        try
        {
            await Task.Run(LoadModels).ConfigureAwait(false);
            _enabled = true;
            ApplyMicGain();
            StartWorkers();
            SetStatus(TranslateStatus.Listening, ListeningMessage());
        }
        catch (Exception ex)
        {
            _enabled = false;
            ApplyMicGain();
            SetStatus(TranslateStatus.Error, $"Couldn't start Live translate: {ex.Message}");
        }
    }

    /// <summary>Re-applies the mic level after the "keep my original voice" option changes.</summary>
    public void ApplyMicGain() => _injector.BaseMicGain = !_enabled ? 1f : Options.KeepOriginal ? 0.25f : 0f;

    /// <summary>Control thread: a new audio pipeline started at this mic sample rate.</summary>
    internal void AttachPipeline(int sampleRate)
    {
        _tapRate = sampleRate;
        Volatile.Write(ref _tap, new SpscRingBuffer(sampleRate * 4));
    }

    /// <summary>Audio thread: called with every mic block (before speech injection). Allocation-free.</summary>
    internal void Tap(ReadOnlySpan<float> block)
    {
        if (_enabled) Volatile.Read(ref _tap)?.Write(block);
    }

    private void LoadModels()
    {
        lock (_modelLock)
        {
            _vad ??= new SileroVad(Library.VadModel);
            if (_whisper == null)
            {
                _whisper = new WhisperTranscriber(Library.WhisperModel, useGpu: true);
                // The first run compiles GPU kernels (many seconds); pay that here, not on your first sentence.
                _whisper.Transcribe(new float[VadRate], Options.From);
            }
            if (_nllb == null)
            {
                _nllb = new NllbTranslator(Library.NllbFolder);
                _nllb.Translate("Hello.", "eng_Latn", "spa_Latn");
            }
        }
    }

    private void StartWorkers()
    {
        if (_running) return;
        _running = true;
        _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "VoiceCtx Translate listen", Priority = ThreadPriority.AboveNormal };
        _translateThread = new Thread(TranslateLoop) { IsBackground = true, Name = "VoiceCtx Translate work" };
        _listenThread.Start();
        _translateThread.Start();
    }

    private void ListenLoop()
    {
        var segmenter = new SpeechSegmenter(SileroVad.FrameSize);
        var chunk = new float[4096];
        var resampled = new float[8192];
        var frame = new float[SileroVad.FrameSize];
        int frameFill = 0;
        StreamResampler? resampler = null;
        SpscRingBuffer? source = null;
        int sourceRate = 0;
        bool wasEnabled = false;

        while (_running)
        {
            if (!_enabled)
            {
                if (wasEnabled) { segmenter.Reset(); _vad?.Reset(); frameFill = 0; }
                wasEnabled = false;
                Thread.Sleep(50);
                continue;
            }
            wasEnabled = true;

            var tap = Volatile.Read(ref _tap);
            if (tap != source)
            {
                source = tap;
                sourceRate = _tapRate;
                resampler = sourceRate > 0 ? new StreamResampler(chunk.Length) : null;
                segmenter.Reset();
                frameFill = 0;
            }
            if (source == null || resampler == null)
            {
                Thread.Sleep(20);
                continue;
            }

            int read = source.Read(chunk);
            if (read == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            int produced = resampler.Process(chunk.AsSpan(0, read), resampled, (double)sourceRate / VadRate);
            for (int i = 0; i < produced; i++)
            {
                frame[frameFill++] = resampled[i];
                if (frameFill < frame.Length) continue;
                frameFill = 0;
                float probability = _vad!.Process(frame);
                var utterance = segmenter.Push(frame, probability);
                if (utterance != null && !_utterances.TryAdd(utterance))
                    SetStatus(TranslateStatus.Translating, "Falling behind — skipped a sentence");
            }
        }
    }

    private void TranslateLoop()
    {
        foreach (var utterance in _utterances.GetConsumingEnumerable())
        {
            if (!_running) break;
            if (!_enabled) continue;
            try
            {
                Process(utterance);
            }
            catch (Exception ex)
            {
                SetStatus(TranslateStatus.Error, $"Translation failed: {ex.Message}");
            }
            if (_enabled && _utterances.Count == 0) SetStatus(TranslateStatus.Listening, ListeningMessage());
        }
    }

    private void Process(float[] utterance)
    {
        var timer = Stopwatch.StartNew();
        SetStatus(TranslateStatus.Translating, "Heard you — translating…");
        var options = Options;
        string from = options.From;
        var target = Languages.FromWhisper(options.To) ?? Languages.All[0];

        // Recognition bursts the GPU. Let the previous translation finish first, so AI Voice (on the same
        // GPU) never misses a deadline mid-line and drops back to the plain Windows voice. That line has
        // to finish before this one can be spoken anyway.
        var wait = Stopwatch.StartNew();
        while (_injector.IsSpeaking && _enabled && _running && wait.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(20);

        Transcription heard;
        heard = _whisper!.Transcribe(utterance, from);
        if (WhisperTranscriber.IsLikelyHallucination(heard, utterance.Length / (double)VadRate)) return;

        var source = Languages.FromWhisper(heard.Language) ?? Languages.FromWhisper(from) ?? Languages.All[0];
        string translation = heard.Text;
        if (source.Nllb != target.Nllb)
            translation = _nllb!.Translate(heard.Text, source.Nllb, target.Nllb);
        if (string.IsNullOrWhiteSpace(translation)) return;

        string? voiceId = options.VoiceId ?? WindowsTts.FindVoiceForLanguage(target.Voice)?.Id;
        if (voiceId != null)
        {
            var clip = _tts.SynthesizeAsync(translation, voiceId, options.SpeakingRate).GetAwaiter().GetResult();
            if (_enabled) _injector.Enqueue(clip);
        }

        LineTranslated?.Invoke(this, new TranslatedLine(heard.Text, translation, source.Name, target.Name, timer.Elapsed));
    }

    private string ListeningMessage() =>
        $"Listening… (speech recognition on {(SpeechRecognitionOnGpu ? "GPU" : "CPU")})";

    private void SetStatus(TranslateStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _running = false;
        _utterances.CompleteAdding();
        _listenThread?.Join(2000);
        _translateThread?.Join(10000);
        lock (_modelLock)
        {
            _vad?.Dispose();
            _whisper?.Dispose();
            _nllb?.Dispose();
        }
        _utterances.Dispose();
    }
}
