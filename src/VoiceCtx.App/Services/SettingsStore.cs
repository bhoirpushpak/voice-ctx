using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceCtx.Audio.Effects;

namespace VoiceCtx.App.Services;

public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public bool MonitorEnabled { get; set; }
    public EffectKind Effect { get; set; } = EffectKind.None;
    public bool IsOn { get; set; } = true;
    public double CustomSemitones { get; set; }
    public double MasterVolume { get; set; } = 100;
    public double Mix { get; set; } = 100;
    public bool NoiseCancel { get; set; }
    public double NoiseReduction { get; set; } = 100;
    public bool StudioSound { get; set; }
    public double StudioStrength { get; set; } = 70;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON (default: %AppData%\VoiceCtx\settings.json).</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceCtx", "settings.json");

    public string Path { get; } = path;

    /// <summary>Returns saved settings, or defaults if the file is missing or unreadable.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
            if (!Enum.IsDefined(settings.Effect)) settings.Effect = EffectKind.None;
            settings.CustomSemitones = Math.Clamp(settings.CustomSemitones, -12, 12);
            settings.MasterVolume = Math.Clamp(settings.MasterVolume, 0, 200);
            settings.Mix = Math.Clamp(settings.Mix, 0, 100);
            settings.NoiseReduction = Math.Clamp(settings.NoiseReduction, 0, 100);
            settings.StudioStrength = Math.Clamp(settings.StudioStrength, 0, 100);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a truncated file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, Path, overwrite: true);
    }
}
