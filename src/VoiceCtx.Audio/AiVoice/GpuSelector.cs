using System.Runtime.InteropServices;

namespace VoiceCtx.Audio.AiVoice;

public sealed record GpuAdapter(int Index, string Name, ulong DedicatedVideoMemory)
{
    public override string ToString() => $"{Name} ({DedicatedVideoMemory / (1024 * 1024 * 1024.0):0.#} GB)";
}

/// <summary>
/// Enumerates DXGI adapters (the same ordering DirectML's device_id uses) and picks the one with the
/// most dedicated video memory. On hybrid laptops this selects the discrete GPU instead of the
/// integrated one that DirectML would otherwise use by default.
/// </summary>
public static unsafe partial class GpuSelector
{
    private const uint SoftwareAdapterFlag = 0x2;
    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static IReadOnlyList<GpuAdapter> EnumerateAdapters()
    {
        var result = new List<GpuAdapter>();
        Guid iid = IidDxgiFactory1;
        if (CreateDXGIFactory1(&iid, out IntPtr factory) < 0 || factory == IntPtr.Zero) return result;
        try
        {
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12 (IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5).
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break;
                try
                {
                    // IDXGIAdapter1::GetDesc1 is vtable slot 10 (IUnknown 3 + IDXGIObject 4 + IDXGIAdapter 3).
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & SoftwareAdapterFlag) == 0)
                        result.Add(new GpuAdapter((int)i, new string(desc.Description).TrimEnd('\0'), (ulong)desc.DedicatedVideoMemory));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        return result;
    }

    /// <summary>The adapter best suited to inference, or null when only software rendering exists.</summary>
    public static GpuAdapter? ChooseBest() =>
        EnumerateAdapters().OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, out IntPtr factory);
}
