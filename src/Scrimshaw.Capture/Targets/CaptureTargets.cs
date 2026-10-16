using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Capture.Targets;

/// <param name="Bounds">Physical pixels on the virtual desktop (the process is per-monitor DPI aware).</param>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, RectInt32 Bounds, uint Dpi, bool IsPrimary)
{
    public int ScalePercent => (int)Math.Round(Dpi * 100 / 96.0);
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId, RectInt32 Bounds);

/// <summary>Something the user can capture (FR-C2).</summary>
public abstract record CaptureTarget
{
    public abstract string DisplayName { get; }
    public abstract CaptureTargetDescriptor ToDescriptor();
}

public sealed record AllMonitorsTarget(IReadOnlyList<MonitorInfo> Monitors) : CaptureTarget
{
    public override string DisplayName => string.Create(CultureInfo.InvariantCulture, $"Whole desktop ({Monitors.Count} monitors)");
    public override CaptureTargetDescriptor ToDescriptor() => new() { Kind = CaptureTargetKind.AllMonitors };
}

public sealed record MonitorTarget(MonitorInfo Monitor, int Number) : CaptureTarget
{
    public override string DisplayName => string.Create(CultureInfo.InvariantCulture,
        $"Monitor {Number}: {Monitor.Bounds.Width}×{Monitor.Bounds.Height} at {Monitor.ScalePercent}%{(Monitor.IsPrimary ? " (main)" : "")}");

    public override CaptureTargetDescriptor ToDescriptor() =>
        new() { Kind = CaptureTargetKind.Monitor, MonitorDeviceName = Monitor.DeviceName };
}

/// <summary>An application window; a browser window is how v1 captures a webpage (A3).</summary>
public sealed record WindowTarget(WindowInfo Window) : CaptureTarget
{
    public override string DisplayName => $"{Window.Title} — {Window.ProcessName}";

    public override CaptureTargetDescriptor ToDescriptor() => new()
    {
        Kind = CaptureTargetKind.Window,
        WindowProcessName = Window.ProcessName,
        WindowTitle = Window.Title,
    };
}

/// <summary>Lists what can be captured, and finds a saved target again.</summary>
public static unsafe class CaptureTargets
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        GCHandle handle = GCHandle.Alloc(list);
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    /// <summary>
    /// Top-level windows worth offering: visible, titled, not cloaked, not tool windows or overlays
    /// (e.g. the NVIDIA overlay), not the desktop or taskbar, and not this app's own windows.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetWindows()
    {
        var handles = new List<IntPtr>();
        GCHandle handle = GCHandle.Alloc(handles);
        try
        {
            NativeMethods.EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        int self = Environment.ProcessId;
        var result = new List<WindowInfo>();
        foreach (IntPtr hwnd in handles)
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER) != hwnd) continue;
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0) continue;
            int cloaked = 0;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (ShellClasses.Contains(NativeMethods.ReadClassName(hwnd))) continue;
            string title = NativeMethods.ReadWindowText(hwnd);
            if (title.Length == 0) continue;
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) continue;
            RectInt32 bounds = default;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(RectInt32)) != 0) continue;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            result.Add(new WindowInfo(hwnd, title, ProcessName((int)pid), (int)pid, bounds));
        }
        return result;
    }

    /// <summary>Everything capturable, in picker order: whole desktop, each monitor, then windows.</summary>
    public static IReadOnlyList<CaptureTarget> GetAll()
    {
        IReadOnlyList<MonitorInfo> monitors = GetMonitors();
        var targets = new List<CaptureTarget>();
        if (monitors.Count > 1) targets.Add(new AllMonitorsTarget(monitors));
        targets.AddRange(monitors.Select((m, i) => new MonitorTarget(m, i + 1)));
        targets.AddRange(GetWindows().OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).Select(w => new WindowTarget(w)));
        return targets;
    }

    /// <summary>
    /// Finds a saved target among <paramref name="available"/>. Windows match on process and exact title
    /// first, then on process alone if that process has exactly one window. Returns null if nothing fits.
    /// </summary>
    public static CaptureTarget? Resolve(CaptureTargetDescriptor? descriptor, IReadOnlyList<CaptureTarget> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (descriptor is null) return null;
        switch (descriptor.Kind)
        {
            case CaptureTargetKind.AllMonitors:
                return available.OfType<AllMonitorsTarget>().FirstOrDefault()
                    ?? (CaptureTarget?)available.OfType<MonitorTarget>().SingleOrDefault();
            case CaptureTargetKind.Monitor:
                return available.OfType<MonitorTarget>().FirstOrDefault(m =>
                    string.Equals(m.Monitor.DeviceName, descriptor.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            case CaptureTargetKind.Window:
                var sameProcess = available.OfType<WindowTarget>()
                    .Where(w => string.Equals(w.Window.ProcessName, descriptor.WindowProcessName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return sameProcess.FirstOrDefault(w => string.Equals(w.Window.Title, descriptor.WindowTitle, StringComparison.Ordinal))
                    ?? (sameProcess.Count == 1 ? sameProcess[0] : null);
            default:
                return null;
        }
    }

    public static bool IsWindowAlive(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return "?";
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(IntPtr monitor, IntPtr hdc, RectInt32* rect, IntPtr data)
    {
        var list = (List<MonitorInfo>)GCHandle.FromIntPtr(data).Target!;
        var info = new MonitorInfoEx { Size = sizeof(MonitorInfoEx) };
        if (!NativeMethods.GetMonitorInfo(monitor, &info)) return 1;
        uint dpi = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 ? dx : 96;
        string device = new string(info.Device);
        list.Add(new MonitorInfo(monitor, device, info.Monitor, dpi, (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindow(IntPtr hwnd, IntPtr data)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(data).Target!).Add(hwnd);
        return 1;
    }
}
