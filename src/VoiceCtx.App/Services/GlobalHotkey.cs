using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace VoiceCtx.App.Services;

/// <summary>System-wide hotkey via RegisterHotKey, delivered through the window's message loop.</summary>
public sealed partial class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;
    private static int s_nextId = 0xB00C;

    private readonly int _id = Interlocked.Increment(ref s_nextId);
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Action _onPressed;

    public GlobalHotkey(Window window, ModifierKeys modifiers, Key key, Action onPressed)
    {
        _onPressed = onPressed;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
        Display = $"{modifiers.ToString().Replace(", ", "+")}+{key}";
        IsRegistered = RegisterHotKey(_hwnd, _id, ToNative(modifiers) | ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>False when another application already owns the combination.</summary>
    public bool IsRegistered { get; }

    public string Display { get; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            _onPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static uint ToNative(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
        | (modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
        | (modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
        | (modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(_hwnd, _id);
        _source.RemoveHook(WndProc);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
