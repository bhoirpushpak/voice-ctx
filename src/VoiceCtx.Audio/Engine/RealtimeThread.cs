using System.Runtime.InteropServices;

namespace VoiceCtx.Audio.Engine;

/// <summary>Registers the calling thread with MMCSS ("Pro Audio") for glitch-free scheduling.</summary>
internal static partial class RealtimeThread
{
    public static IntPtr Enter()
    {
        int taskIndex = 0;
        IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        if (handle == IntPtr.Zero)
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        return handle;
    }

    public static void Leave(IntPtr handle)
    {
        if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle);
    }

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
