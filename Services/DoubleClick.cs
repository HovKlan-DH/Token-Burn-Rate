using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TokenBurnRate.Services;

/// <summary>
/// How long to wait before deciding that a click stood alone.
///
/// Needed because Avalonia's TrayIcon raises one Clicked event per release and offers no
/// double-click of its own, so telling one click from two is a matter of timing. Taking the
/// figure from the system rather than picking one here means the tray icon answers to the
/// same speed as every other icon on the desktop, including a user who has slowed it down
/// for accessibility.
/// </summary>
public static class DoubleClick
{
    /// <summary>
    /// Used where the real figure cannot be read. 500ms is the Windows out-of-the-box
    /// value and close enough to the other desktops' defaults to feel unremarkable.
    /// </summary>
    private static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(500);

    private static readonly Lazy<TimeSpan> _interval = new(Resolve);

    public static TimeSpan Interval => _interval.Value;

    private static TimeSpan Resolve()
    {
        // Only Windows publishes the setting. The other targets get the default rather
        // than a guess dressed up as a reading.
        if (!OperatingSystem.IsWindows()) return Fallback;

        return WindowsInterval();
    }

    [SupportedOSPlatform("windows")]
    private static TimeSpan WindowsInterval()
    {
        var ms = GetDoubleClickTime();

        // Documented as never zero, but a zero would collapse the wait entirely and
        // make every double-click read as two separate toggles.
        return ms > 0 ? TimeSpan.FromMilliseconds(ms) : Fallback;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
