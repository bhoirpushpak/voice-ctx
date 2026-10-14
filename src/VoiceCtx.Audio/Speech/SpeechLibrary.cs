using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceCtx.Audio.Speech;

/// <summary>An installed speech language (one Pocket TTS model folder).</summary>
public sealed record SpeechLanguage(string Code, string Name, string Folder)
{
    public override string ToString() => Name;
}

/// <summary>A voice lines can be spoken in: your recording, a ready-made voice or an imported clip.</summary>
public sealed record SpeechVoice(string Id, string Name, string WavPath, bool BuiltIn)
{
    public bool IsMine => Id == SpeechLibrary.MyVoiceId;
    public override string ToString() => Name;
}

/// <summary>Speech models and voices in %AppData%\VoiceCtx\Speech (built by tools/tts/build_models.py).</summary>
public sealed class SpeechLibrary(string root)
{
    public const string MyVoiceId = "my-voice";
    public const int VoiceSampleRate = 24000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceCtx", "Speech");

    public string Root { get; } = root;
    public string VoicesFolder => Path.Combine(Root, "voices");
    public string CacheFolder => Path.Combine(VoicesFolder, "cache");
    public string CreditsPath => Path.Combine(Root, "CREDITS.txt");

    /// <summary>Installed languages, in a stable order (English first).</summary>
    public IReadOnlyList<SpeechLanguage> ScanLanguages()
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<SpeechLanguage>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (!PocketTts.IsModelFolder(folder)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<VoiceCtxMeta>(File.ReadAllText(Path.Combine(folder, "voicectx.json")), Json);
                if (meta?.Language is { Length: > 0 } code) result.Add(new SpeechLanguage(code, meta.Name ?? code, folder));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Skip a broken folder; the others still work.
            }
        }
        string[] order = ["en", "hi", "fr", "de", "es", "it", "pt", "nl"];
        return result.OrderBy(l => Array.IndexOf(order, l.Code) is var i && i < 0 ? int.MaxValue : i).ThenBy(l => l.Name).ToList();
    }

    /// <summary>Your recorded voice first, then the ready-made voices, then imported ones.</summary>
    public IReadOnlyList<SpeechVoice> ScanVoices()
    {
        if (!Directory.Exists(VoicesFolder)) return [];
        var voices = new List<SpeechVoice>();
        foreach (var wav in Directory.EnumerateFiles(VoicesFolder, "*.wav"))
        {
            string id = Path.GetFileNameWithoutExtension(wav);
            VoiceMeta? meta = null;
            string metaPath = Path.ChangeExtension(wav, ".json");
            try
            {
                if (File.Exists(metaPath)) meta = JsonSerializer.Deserialize<VoiceMeta>(File.ReadAllText(metaPath), Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            voices.Add(new SpeechVoice(id, meta?.Name ?? (id == MyVoiceId ? "My voice" : id), wav, meta?.BuiltIn ?? false));
        }
        return voices.OrderBy(v => v.IsMine ? 0 : v.BuiltIn ? 1 : 2).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Saves a recording of your voice (any rate, mono) as "My voice".</summary>
    public SpeechVoice SaveMyVoice(ReadOnlySpan<float> samples, int sampleRate) =>
        SaveVoice(MyVoiceId, "My voice", samples, sampleRate);

    /// <summary>Copies a WAV recording in as a new voice. Only use recordings of people who agreed.</summary>
    public SpeechVoice Import(string wavPath, string? name = null)
    {
        var (samples, rate) = WavDecoder.DecodeMono(File.ReadAllBytes(wavPath));
        if (samples.Length < rate * 3) throw new InvalidDataException("The recording is too short. Use at least 5 seconds of clear speech.");
        name ??= Path.GetFileNameWithoutExtension(wavPath);
        string id = "imported-" + new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return SaveVoice(id, name, samples, rate);
    }

    public void Delete(SpeechVoice voice)
    {
        if (voice.BuiltIn) return;
        File.Delete(voice.WavPath);
        File.Delete(Path.ChangeExtension(voice.WavPath, ".json"));
        ClearCache(voice.Id);
    }

    internal void ClearCache(string voiceId)
    {
        if (!Directory.Exists(CacheFolder)) return;
        foreach (var f in Directory.EnumerateFiles(CacheFolder, voiceId + ".*.voice")) File.Delete(f);
    }

    private SpeechVoice SaveVoice(string id, string name, ReadOnlySpan<float> samples, int sampleRate)
    {
        Directory.CreateDirectory(VoicesFolder);
        var audio = PocketTts.Resample(samples.ToArray(), sampleRate, VoiceSampleRate);
        string wav = Path.Combine(VoicesFolder, id + ".wav");
        WavDecoder.WriteMono16(wav, audio, VoiceSampleRate);
        File.WriteAllText(Path.ChangeExtension(wav, ".json"), JsonSerializer.Serialize(new VoiceMeta { Name = name }, Json));
        ClearCache(id);
        return new SpeechVoice(id, name, wav, false);
    }

    private sealed class VoiceCtxMeta
    {
        public string? Language { get; set; }
        public string? Name { get; set; }
    }

    private sealed class VoiceMeta
    {
        public string? Name { get; set; }
        public bool BuiltIn { get; set; }
        public string? Credits { get; set; }
        public string? License { get; set; }
    }
}
