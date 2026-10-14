using System.Buffers.Binary;

namespace VoiceCtx.Audio.Speech;

/// <summary>A synthesized utterance ready to be injected into the live audio path.</summary>
public sealed record SpeechClip(float[] Samples, int SampleRate, string Text);

/// <summary>Minimal RIFF/WAVE reader (16-bit / 32-bit PCM and float, mixed to mono) and 16-bit writer.</summary>
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

    /// <summary>Writes mono 16-bit PCM.</summary>
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        var bytes = new byte[44 + samples.Length * 2];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + 2 * i)..], (short)Math.Clamp(MathF.Round(samples[i] * 32767f), -32768, 32767));
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
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
                    (1, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                    _ => throw new FormatException($"Unsupported WAV format {format}/{bits}-bit."),
                };
            }
            mono[f] = sum / channels;
        }
        return mono;
    }
}
