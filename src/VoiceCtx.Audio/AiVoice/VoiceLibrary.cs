using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceCtx.Audio.AiVoice;

/// <summary>Metadata written next to every voice model by tools/aivoice (format 1).</summary>
public sealed class VoiceInfo
{
    public int Format { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int SampleRate { get; init; }
    public int BlockMs { get; init; } = 100;
    public int ContextMs { get; init; } = 600;
    public int Frames { get; init; }
    public int Tail { get; init; }
    public int MarginFrames { get; init; }
    public string Gender { get; init; } = "other";
    public int DefaultPitchShift { get; init; }
    public string Credits { get; init; } = "";
    public string License { get; init; } = "";
    public bool BuiltIn { get; init; }

    [JsonIgnore] public string ModelPath { get; internal set; } = "";

    public override string ToString() => Name;
}

/// <summary>Locates the AI Voice model folder and the voices installed in it.</summary>
public sealed class VoiceLibrary(string root)
{
    public const string EncoderFile = "content_encoder.onnx";
    public const string PitchFile = "rmvpe.onnx";
    public const string CreditsFile = "CREDITS.txt";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceCtx", "AiVoice");

    public string Root { get; } = root;
    public string VoicesFolder => Path.Combine(Root, "voices");
    public string EncoderPath => Path.Combine(Root, EncoderFile);
    public string PitchPath => Path.Combine(Root, PitchFile);
    public string CreditsPath => Path.Combine(Root, CreditsFile);

    /// <summary>True when the shared content encoder and pitch models are present.</summary>
    public bool BaseModelsInstalled => File.Exists(EncoderPath) && File.Exists(PitchPath);

    /// <summary>Valid voices (built-in first), skipping files that are malformed or unsupported.</summary>
    public IReadOnlyList<VoiceInfo> ScanVoices()
    {
        if (!Directory.Exists(VoicesFolder)) return [];
        var voices = new List<VoiceInfo>();
        foreach (var json in Directory.EnumerateFiles(VoicesFolder, "*.json"))
        {
            var model = Path.ChangeExtension(json, ".onnx");
            if (!File.Exists(model)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<VoiceInfo>(File.ReadAllText(json), JsonOptions);
                if (info == null || !IsSupported(info)) continue;
                info.ModelPath = model;
                voices.Add(info);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return voices.OrderByDescending(v => v.BuiltIn).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Copies a converted voice (.onnx plus its .json) into the voices folder.</summary>
    public VoiceInfo Import(string onnxPath)
    {
        var json = Path.ChangeExtension(onnxPath, ".json");
        if (!File.Exists(json))
            throw new InvalidOperationException("A voice needs its .json description next to the .onnx file. Convert RVC voices with tools/aivoice/convert_voice.py.");
        var info = JsonSerializer.Deserialize<VoiceInfo>(File.ReadAllText(json), JsonOptions)
            ?? throw new InvalidOperationException("Voice description is empty.");
        if (!IsSupported(info)) throw new InvalidOperationException("This voice was made for a different Voice CTX version.");

        Directory.CreateDirectory(VoicesFolder);
        string baseName = string.Concat(info.Id.Split(Path.GetInvalidFileNameChars()));
        File.Copy(onnxPath, Path.Combine(VoicesFolder, baseName + ".onnx"), overwrite: true);
        File.Copy(json, Path.Combine(VoicesFolder, baseName + ".json"), overwrite: true);
        info.ModelPath = Path.Combine(VoicesFolder, baseName + ".onnx");
        return info;
    }

    internal static bool IsSupported(VoiceInfo v) =>
        v.Format == 1 && !string.IsNullOrWhiteSpace(v.Id) && v.SampleRate is >= 16000 and <= 48000
        && v.SampleRate % 100 == 0 && v.BlockMs > 0 && v.ContextMs > 0 && v.Tail > v.MarginFrames
        && v.Tail - v.MarginFrames == v.BlockMs / 10 && v.Frames == ExpectedFrames(v.BlockMs, v.ContextMs);

    internal static int ExpectedFrames(int blockMs, int contextMs) => ((blockMs + contextMs) * 16 - 400) / 320 * 2 + 2;
}
