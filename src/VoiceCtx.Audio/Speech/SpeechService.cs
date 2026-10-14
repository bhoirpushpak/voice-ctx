namespace VoiceCtx.Audio.Speech;

/// <summary>
/// Speaks text in a cloned voice with Pocket TTS, for Type to speak and Live translate. Models load
/// on first use (the two most recent stay loaded) and each voice is encoded once per language and
/// cached on disk. One line is synthesized at a time, on the CPU.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private const int MaxLoadedModels = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<(string Code, PocketTts Model)> _models = [];
    private readonly Dictionary<(string Language, string Voice), PocketVoice> _voices = [];
    private bool _disposed;

    public SpeechService(SpeechLibrary library)
    {
        Library = library;
        Refresh();
    }

    public SpeechLibrary Library { get; }
    public IReadOnlyList<SpeechLanguage> Languages { get; private set; } = [];
    public IReadOnlyList<SpeechVoice> Voices { get; private set; } = [];
    public bool Installed => Languages.Count > 0;

    /// <summary>Raised (on any thread) after languages or voices change.</summary>
    public event EventHandler? Changed;

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    public void Refresh()
    {
        Languages = Library.ScanLanguages();
        Voices = Library.ScanVoices();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public SpeechLanguage? FindLanguage(string code) => Languages.FirstOrDefault(l => l.Code == code);

    public SpeechVoice? FindVoice(string? id) => Voices.FirstOrDefault(v => v.Id == id);

    /// <summary>The voice to use: the requested one, else yours, else the first ready-made one.</summary>
    public SpeechVoice? ResolveVoice(string? id) =>
        FindVoice(id) ?? FindVoice(SpeechLibrary.MyVoiceId) ?? Voices.FirstOrDefault();

    /// <summary>Forgets cached encodings of a voice (after it was re-recorded or deleted).</summary>
    public void ForgetVoice(string voiceId)
    {
        _gate.Wait();
        try
        {
            foreach (var key in _voices.Keys.Where(k => k.Voice == voiceId).ToList()) _voices.Remove(key);
        }
        finally
        {
            _gate.Release();
        }
        Refresh();
    }

    /// <summary>Speaks <paramref name="text"/> in <paramref name="languageCode"/> with the given voice.</summary>
    public Task<SpeechClip> SynthesizeAsync(string text, string languageCode, string? voiceId, CancellationToken cancellation = default) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var model = GetModel(languageCode);
                var voice = GetVoice(model, languageCode, voiceId);
                var audio = model.Synthesize(text, voice, cancellation);
                return new SpeechClip(audio, PocketTts.SampleRate, text);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellation);

    /// <summary>Loads a language and encodes the voice ahead of time, so the first line is quick.</summary>
    public Task WarmUpAsync(string languageCode, string? voiceId) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || FindLanguage(languageCode) == null || ResolveVoice(voiceId) == null) return;
                GetVoice(GetModel(languageCode), languageCode, voiceId);
            }
            finally
            {
                _gate.Release();
            }
        });

    private PocketTts GetModel(string code)
    {
        int index = _models.FindIndex(m => m.Code == code);
        if (index >= 0)
        {
            var hit = _models[index];
            _models.RemoveAt(index);
            _models.Insert(0, hit);
            return hit.Model;
        }
        var language = FindLanguage(code) ?? throw new InvalidOperationException($"The speech model for '{code}' isn't installed.");
        var model = new PocketTts(language.Folder, DefaultThreads);
        _models.Insert(0, (code, model));
        while (_models.Count > MaxLoadedModels)
        {
            var (oldCode, old) = _models[^1];
            _models.RemoveAt(_models.Count - 1);
            foreach (var key in _voices.Keys.Where(k => k.Language == oldCode).ToList()) _voices.Remove(key);
            old.Dispose();
        }
        return model;
    }

    private PocketVoice GetVoice(PocketTts model, string languageCode, string? voiceId)
    {
        var voice = ResolveVoice(voiceId) ?? throw new InvalidOperationException("No voice yet. Record your voice first.");
        if (_voices.TryGetValue((languageCode, voice.Id), out var cached)) return cached;

        string cachePath = Path.Combine(Library.CacheFolder, $"{voice.Id}.{languageCode}.voice");
        var encoded = TryLoad(cachePath, voice, languageCode);
        if (encoded == null)
        {
            var (samples, rate) = WavDecoder.DecodeMono(File.ReadAllBytes(voice.WavPath));
            encoded = model.EncodeVoice(samples, rate);
            TrySave(cachePath, voice, encoded);
        }
        _voices[(languageCode, voice.Id)] = encoded;
        return encoded;
    }

    // Cache file: [stamp (wav length, wav write time)] [frame count] [frames as float32].
    private static long Stamp(SpeechVoice voice)
    {
        var info = new FileInfo(voice.WavPath);
        return info.Length ^ info.LastWriteTimeUtc.Ticks;
    }

    private static PocketVoice? TryLoad(string path, SpeechVoice voice, string language)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt64() != Stamp(voice)) return null;
            int frames = reader.ReadInt32();
            var bytes = reader.ReadBytes(frames * 1024 * sizeof(float));
            if (bytes.Length != frames * 1024 * sizeof(float)) return null;
            var data = new float[frames * 1024];
            Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
            return new PocketVoice(data, frames, language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TrySave(string path, SpeechVoice voice, PocketVoice encoded)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(Stamp(voice));
            writer.Write(encoded.FrameCount);
            var bytes = new byte[encoded.Frames.Length * sizeof(float)];
            Buffer.BlockCopy(encoded.Frames, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache only saves time.
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            foreach (var (_, model) in _models) model.Dispose();
            _models.Clear();
            _voices.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
