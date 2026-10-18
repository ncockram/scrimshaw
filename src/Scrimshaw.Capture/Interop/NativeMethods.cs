using System.Runtime.InteropServices;

namespace Scrimshaw.Capture.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct RectInt32
{
    public int Left, Top, Right, Bottom;

    public RectInt32(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"({Left},{Top}) {Width}×{Height}";
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MonitorInfoEx
{
    public int Size;
    public RectInt32 Monitor;
    public RectInt32 Work;
    public uint Flags;
    public fixed char Device[32];
}

internal static unsafe partial class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const uint GA_ROOTOWNER = 3;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const uint MONITORINFOF_PRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RectInt32*, IntPtr, int> callback, IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr monitor, MonitorInfoEx* info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> callback, IntPtr data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(IntPtr hwnd, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(IntPtr hwnd, char* buffer, int max);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, void* value, int size);

    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WindowsCreateString(string source, int length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(IntPtr hstring);

    public static string ReadWindowText(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        char* buffer = stackalloc char[Math.Min(len + 1, 1024)];
        int got = GetWindowText(hwnd, buffer, Math.Min(len + 1, 1024));
        return new string(buffer, 0, got);
    }

    public static string ReadClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[256];
        int got = GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, got);
    }

    /// <summary>Activation factory for a WinRT runtime class, queried for <paramref name="iid"/>. Caller releases it.</summary>
    public static IntPtr GetActivationFactory(string runtimeClass, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out IntPtr hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, iid, out IntPtr factory));
            return factory;
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }
}
