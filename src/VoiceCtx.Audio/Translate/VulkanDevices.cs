using System.Runtime.InteropServices;
using System.Text;

namespace VoiceCtx.Audio.Translate;

public sealed record VulkanDevice(int Index, string Name, bool Discrete);

/// <summary>
/// Lists Vulkan GPUs in driver enumeration order (the order whisper.cpp's Vulkan backend numbers
/// them) so Whisper can run on the discrete GPU instead of an integrated one on hybrid laptops.
/// </summary>
public static unsafe partial class VulkanDevices
{
    private const int StructureTypeInstanceCreateInfo = 1;
    private const int DeviceTypeIntegrated = 1;
    private const int DeviceTypeDiscrete = 2;

    public static IReadOnlyList<VulkanDevice> Enumerate()
    {
        var result = new List<VulkanDevice>();
        try
        {
            var info = new InstanceCreateInfo { SType = StructureTypeInstanceCreateInfo };
            if (vkCreateInstance(&info, IntPtr.Zero, out IntPtr instance) != 0) return result;
            try
            {
                uint count = 0;
                if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0) return result;
                var devices = new IntPtr[count];
                fixed (IntPtr* p = devices)
                    if (vkEnumeratePhysicalDevices(instance, ref count, p) != 0) return result;

                var props = new byte[1024];   // VkPhysicalDeviceProperties is ~824 bytes
                for (int i = 0; i < count; i++)
                {
                    fixed (byte* b = props) vkGetPhysicalDeviceProperties(devices[i], b);
                    int type = BitConverter.ToInt32(props, 16);
                    int nameEnd = Array.IndexOf(props, (byte)0, 20, 256);
                    string name = Encoding.UTF8.GetString(props, 20, (nameEnd < 0 ? 276 : nameEnd) - 20);
                    if (type is DeviceTypeIntegrated or DeviceTypeDiscrete)
                        result.Add(new VulkanDevice(i, name, type == DeviceTypeDiscrete));
                }
            }
            finally
            {
                vkDestroyInstance(instance, IntPtr.Zero);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No Vulkan loader installed: Whisper falls back to the CPU.
        }
        return result;
    }

    /// <summary>Index of the best GPU for Whisper (discrete first), or null when there is none.</summary>
    public static VulkanDevice? ChooseBest()
    {
        var devices = Enumerate();
        return devices.FirstOrDefault(d => d.Discrete) ?? devices.FirstOrDefault();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [LibraryImport("vulkan-1.dll")]
    private static partial int vkCreateInstance(InstanceCreateInfo* createInfo, IntPtr allocator, out IntPtr instance);

    [LibraryImport("vulkan-1.dll")]
    private static partial int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr* devices);

    [LibraryImport("vulkan-1.dll")]
    private static partial void vkGetPhysicalDeviceProperties(IntPtr device, byte* properties);

    [LibraryImport("vulkan-1.dll")]
    private static partial void vkDestroyInstance(IntPtr instance, IntPtr allocator);
}
