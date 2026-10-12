using System.IO;
using VoiceCtx.App.Services;
using VoiceCtx.Audio.Effects;

namespace VoiceCtx.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceCtxTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void RoundTripsAllValues()
    {
        var store = new SettingsStore(SettingsPath);
        var saved = new AppSettings
        {
            InputDeviceId = "{mic}",
            OutputDeviceId = "{cable}",
            MonitorDeviceId = "{phones}",
            MonitorEnabled = true,
            Effect = EffectKind.FastMutation,
            IsOn = false,
            CustomSemitones = -5,
            MasterVolume = 140,
            Mix = 35,
        };
        store.Save(saved);

        var loaded = store.Load();
        Assert.Equal("{mic}", loaded.InputDeviceId);
        Assert.Equal("{cable}", loaded.OutputDeviceId);
        Assert.Equal("{phones}", loaded.MonitorDeviceId);
        Assert.True(loaded.MonitorEnabled);
        Assert.Equal(EffectKind.FastMutation, loaded.Effect);
        Assert.False(loaded.IsOn);
        Assert.Equal(-5, loaded.CustomSemitones);
        Assert.Equal(140, loaded.MasterVolume);
        Assert.Equal(35, loaded.Mix);
        Assert.Contains("\"FastMutation\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(EffectKind.None, loaded.Effect);
        Assert.True(loaded.IsOn);
        Assert.Equal(100, loaded.MasterVolume);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Effect\": \"NoSuchEffect\"}")]
    [InlineData("")]
    public void CorruptFileGivesDefaults(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content);
        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(EffectKind.None, loaded.Effect);
        Assert.Equal(100, loaded.Mix);
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{\"CustomSemitones\": 99, \"MasterVolume\": -3, \"Mix\": 500, \"Effect\": 42}");
        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(12, loaded.CustomSemitones);
        Assert.Equal(0, loaded.MasterVolume);
        Assert.Equal(100, loaded.Mix);
        Assert.Equal(EffectKind.None, loaded.Effect);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
