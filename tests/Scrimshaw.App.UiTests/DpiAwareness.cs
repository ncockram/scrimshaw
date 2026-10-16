using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Scrimshaw.App.UiTests;

/// <summary>
/// The test host has no manifest, so make it per-monitor DPI aware like the app. Otherwise Windows reports
/// scaled monitor coordinates and windows get placed partly off-screen (Phase 2 desktop test finding).
/// </summary>
internal static partial class DpiAwareness
{
    [ModuleInitializer]
    internal static void Initialize() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(IntPtr value);
}
