using System.Runtime.InteropServices;

namespace VoiceCtx.Audio.Enhancement;

/// <summary>Bindings to the bundled RNNoise v0.2 library (BSD-3-Clause, see Native/RNNOISE-LICENSE.txt).</summary>
internal static partial class RnNoise
{
    private const string Library = "rnnoise";

    /// <summary>RNNoise always works on 10 ms frames at 48 kHz.</summary>
    public const int SampleRate = 48000;
    public const int FrameSize = 480;

    private static readonly Lazy<bool> s_available = new(Probe);

    /// <summary>False when the native library is missing or cannot load on this machine.</summary>
    public static bool IsAvailable => s_available.Value;

    [LibraryImport(Library, EntryPoint = "rnnoise_create")]
    public static partial IntPtr Create(IntPtr model);

    [LibraryImport(Library, EntryPoint = "rnnoise_destroy")]
    public static partial void Destroy(IntPtr state);

    [LibraryImport(Library, EntryPoint = "rnnoise_get_frame_size")]
    public static partial int GetFrameSize();

    /// <summary>Denoises one frame of samples scaled to ±32768. Returns voice-activity probability.</summary>
    [LibraryImport(Library, EntryPoint = "rnnoise_process_frame")]
    public static unsafe partial float ProcessFrame(IntPtr state, float* output, float* input);

    private static bool Probe()
    {
        try
        {
            return GetFrameSize() == FrameSize;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>Owns one native RNNoise state.</summary>
internal sealed class RnNoiseHandle : SafeHandle
{
    public RnNoiseHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(RnNoise.Create(IntPtr.Zero));
        if (IsInvalid) throw new InvalidOperationException("rnnoise_create failed");
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        RnNoise.Destroy(handle);
        return true;
    }
}
