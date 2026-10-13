using System.Buffers.Binary;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace VoiceCtx.Audio.Speech;

/// <summary>A synthesized utterance ready to be injected into the live audio path.</summary>
public sealed record SpeechClip(float[] Samples, int SampleRate, string Text);

/// <summary>An installed Windows text-to-speech voice.</summary>
public sealed record TtsVoice(string Id, string Name, string Language, string Gender)
{
    public override string ToString() => $"{Name} ({Language})";
}

/// <summary>Text-to-speech using the voices installed in Windows (Settings → Time &amp; language → Speech).</summary>
public sealed class WindowsTts
{
    /// <summary>All installed voices, sorted by language then name.</summary>
    public static IReadOnlyList<TtsVoice> InstalledVoices() =>
        SpeechSynthesizer.AllVoices
            .Select(v => new TtsVoice(v.Id, v.DisplayName.Replace("Microsoft ", ""), v.Language, v.Gender.ToString()))
            .OrderBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The best installed voice for a language ("es", "es-MX", "hin_Deva"…), or null if none.</summary>
    public static TtsVoice? FindVoiceForLanguage(string language, string? preferredGender = null)
    {
        var voices = InstalledVoices();
        string primary = language.Split('-', '_')[0].ToLowerInvariant();
        var matches = voices.Where(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = voices.Where(v => v.Language.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.FirstOrDefault(v => preferredGender != null && v.Gender.Equals(preferredGender, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    /// <summary>Speaks <paramref name="text"/> into an in-memory clip.</summary>
    /// <param name="rate">Speaking rate, 0.5 (slow) to 2.0 (fast); 1 is normal.</param>
    public async Task<SpeechClip> SynthesizeAsync(string text, string? voiceId, double rate = 1.0, CancellationToken cancellation = default)
    {
        using var synth = new SpeechSynthesizer();
        if (voiceId != null)
        {
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId);
            if (voice != null) synth.Voice = voice;
        }
        synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 2.0);
        synth.Options.AppendedSilence = SpeechAppendedSilence.Min;

        using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(cancellation).ConfigureAwait(false);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size).AsTask(cancellation).ConfigureAwait(false);
            reader.ReadBytes(bytes);
        }
        var (samples, sampleRate) = WavDecoder.DecodeMono(bytes);
        return new SpeechClip(samples, sampleRate, text);
    }
}

/// <summary>Minimal RIFF/WAVE reader for 16-bit / 32-bit-float PCM, mixed down to mono float.</summary>
public static class WavDecoder
{
    public static (float[] Samples, int SampleRate) DecodeMono(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new FormatException("Not a WAV file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(pos + 4, 4));
            var body = wav.Slice(pos + 8, Math.Min(Math.Max(0, size), wav.Length - pos - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && body.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels <= 0 || rate <= 0) throw new FormatException("WAV data before format chunk.");
                return (ToMono(body, format, channels, bits), rate);
            }
            pos += 8 + size + (size & 1);
        }
        throw new FormatException("WAV file has no data chunk.");
    }

    private static float[] ToMono(ReadOnlySpan<byte> data, int format, int channels, int bits)
    {
        int bytesPerSample = bits / 8;
        int frames = data.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = data.Slice((f * channels + c) * bytesPerSample, bytesPerSample);
                sum += (format, bits) switch
                {
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    _ => throw new FormatException($"Unsupported WAV format {format}/{bits}-bit."),
                };
            }
            mono[f] = sum / channels;
        }
        return mono;
    }
}
