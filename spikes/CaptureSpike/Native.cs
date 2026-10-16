using System.Runtime.InteropServices;
using System.Text;

namespace CaptureSpike;

internal record MonitorInfo(IntPtr Handle, string DeviceName, Rect32 Bounds, uint Dpi, bool Primary);
internal record WindowInfo(IntPtr Handle, string Title, string Process, Rect32 Bounds);

[StructLayout(LayoutKind.Sequential)]
internal struct Rect32
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

internal static class Native
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect32 rect, IntPtr data);
    private delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Rect32 rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect32 value, int size);
    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Rect32 _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfoW(h, ref mi);
            GetDpiForMonitor(h, 0, out uint dpi, out _);
            list.Add(new MonitorInfo(h, mi.szDevice, mi.rcMonitor, dpi, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<WindowInfo> GetWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetAncestor(h, 3 /* GA_ROOTOWNER */) != h) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var sb = new StringBuilder(512);
            if (GetWindowTextW(h, sb, sb.Capacity) == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
            DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out Rect32 r, Marshal.SizeOf<Rect32>());
            list.Add(new WindowInfo(h, sb.ToString(), proc, r));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Activation factory for a WinRT runtime class, QI'd to <paramref name="iid"/>. Caller releases.</summary>
    public static IntPtr GetActivationFactory(string runtimeClass, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out IntPtr hs));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, iid, out IntPtr factory));
            return factory;
        }
        finally { WindowsDeleteString(hs); }
    }
}
