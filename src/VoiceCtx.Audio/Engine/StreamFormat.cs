using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoiceCtx.Audio.Engine;

internal enum SampleEncoding { Float32, Pcm16, Pcm24, Pcm32 }

internal readonly record struct StreamFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BytesPerFrame => BytesPerSample * Channels;

    public static StreamFormat From(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

        var encoding = isFloat ? SampleEncoding.Float32 : format.BitsPerSample switch
        {
            16 => SampleEncoding.Pcm16,
            24 => SampleEncoding.Pcm24,
            32 => SampleEncoding.Pcm32,
            _ => throw new NotSupportedException($"Unsupported device format: {format}"),
        };
        return new StreamFormat(format.SampleRate, format.Channels, encoding);
    }
}

internal static class SampleConverter
{
    /// <summary>Converts interleaved device samples to mono float by averaging channels.</summary>
    public static void ToMono(ReadOnlySpan<byte> source, StreamFormat format, Span<float> mono)
    {
        int ch = format.Channels;
        float scale = 1f / ch;
        switch (format.Encoding)
        {
            case SampleEncoding.Float32:
            {
                var s = MemoryMarshal.Cast<byte, float>(source);
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += s[k++];
                    mono[i] = sum * scale;
                }
                break;
            }
            case SampleEncoding.Pcm16:
            {
                var s = MemoryMarshal.Cast<byte, short>(source);
                scale /= 32768f;
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < ch; c++) sum += s[k++];
                    mono[i] = sum * scale;
                }
                break;
            }
            case SampleEncoding.Pcm24:
            {
                scale /= 8388608f;
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++, k += 3)
                        sum += source[k] | (source[k + 1] << 8) | ((sbyte)source[k + 2] << 16);
                    mono[i] = sum * scale;
                }
                break;
            }
            case SampleEncoding.Pcm32:
            {
                var s = MemoryMarshal.Cast<byte, int>(source);
                scale /= 2147483648f;
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += s[k++];
                    mono[i] = sum * scale;
                }
                break;
            }
        }
    }

    /// <summary>Writes each mono sample to every channel of the interleaved device buffer.</summary>
    public static void FromMono(ReadOnlySpan<float> mono, StreamFormat format, Span<byte> destination)
    {
        int ch = format.Channels;
        switch (format.Encoding)
        {
            case SampleEncoding.Float32:
            {
                var d = MemoryMarshal.Cast<byte, float>(destination);
                for (int i = 0, k = 0; i < mono.Length; i++)
                    for (int c = 0; c < ch; c++) d[k++] = mono[i];
                break;
            }
            case SampleEncoding.Pcm16:
            {
                var d = MemoryMarshal.Cast<byte, short>(destination);
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    short v = (short)(Math.Clamp(mono[i], -1f, 1f) * 32767f);
                    for (int c = 0; c < ch; c++) d[k++] = v;
                }
                break;
            }
            case SampleEncoding.Pcm24:
            {
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    int v = (int)(Math.Clamp(mono[i], -1f, 1f) * 8388607f);
                    for (int c = 0; c < ch; c++, k += 3)
                    {
                        destination[k] = (byte)v;
                        destination[k + 1] = (byte)(v >> 8);
                        destination[k + 2] = (byte)(v >> 16);
                    }
                }
                break;
            }
            case SampleEncoding.Pcm32:
            {
                var d = MemoryMarshal.Cast<byte, int>(destination);
                for (int i = 0, k = 0; i < mono.Length; i++)
                {
                    int v = (int)(Math.Clamp(mono[i], -1f, 1f) * 2147483647.0);
                    for (int c = 0; c < ch; c++) d[k++] = v;
                }
                break;
            }
        }
    }
}
