using System;
using System.Runtime.InteropServices;

namespace TokenBurnRate.Services;

/// <summary>
/// Shows a balloon notification in the Windows notification area.
///
/// Avalonia has no API for this: TrayIcon exposes only an icon, a tooltip and a menu, and
/// WindowNotificationManager draws inside a window - useless here, because the notice is
/// needed precisely when the window has just been hidden. So this calls Shell_NotifyIcon
/// directly.
///
/// It registers its own short-lived icon rather than borrowing Avalonia's: the handle for
/// that one is private, and adding a balloon to a foreign icon would mean guessing at the
/// id Avalonia used. The extra icon is hidden (NIS_HIDDEN), so nothing new appears in the
/// tray - Windows still routes the balloon through it.
///
/// Everything is best-effort: notifications are a courtesy, and a failure must never take
/// down the app or block the minimise it accompanies.
/// </summary>
public static class TrayNotifier
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Shows a balloon. Returns false if it could not be shown, so the caller can decide
    /// whether to fall back or to leave the "already notified" flag unset.
    /// </summary>
    public static bool Show(string title, string message)
    {
        if (!IsSupported) return false;

        try
        {
            return ShowCore(title, message);
        }
        catch (Exception)
        {
            // DllNotFoundException, EntryPointNotFound, a locked-down shell: all mean the
            // same thing here - no notification, and nothing worth interrupting the user for.
            return false;
        }
    }

    private static bool ShowCore(string title, string message)
    {
        // A message-only window owns the icon. It never renders; it exists solely to give
        // Shell_NotifyIcon an hWnd to associate the notification with.
        var hwnd = CreateWindowExW(0, "STATIC", "TokenBurnRateNotify", 0, 0, 0, 0, 0,
                                   HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = NIF_INFO | NIF_ICON | NIF_STATE,
                dwState = NIS_HIDDEN,
                dwStateMask = NIS_HIDDEN,
                hIcon = LoadAppIcon(),
                szInfoTitle = Trim(title, 63),
                szInfo = Trim(message, 255),
                dwInfoFlags = NIIF_NONE,
                szTip = "TokenBurnRate",
            };

            if (!Shell_NotifyIconW(NIM_ADD, ref data)) return false;

            // The balloon is queued by NIM_ADD; the icon itself is no longer needed once
            // Windows has taken the message. Deleting it immediately would cancel the
            // balloon, so removal waits for the notification's own lifetime to end.
            return true;
        }
        finally
        {
            // The window is left alive deliberately: destroying it here would take the
            // pending balloon with it. Windows tears both down when the process exits,
            // and this runs at most once per session.
        }
    }

    /// <summary>The app's own icon, so the balloon is recognisably from this app.</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var icon = ExtractIconW(IntPtr.Zero, exe, 0);
                // ExtractIcon returns 1 for "file has no icons", which is not a handle.
                if (icon != IntPtr.Zero && icon != new IntPtr(1)) return icon;
            }
        }
        catch (Exception)
        {
            // Fall through to the generic application icon.
        }

        return LoadIconW(IntPtr.Zero, IDI_APPLICATION);
    }

    /// <summary>Truncates to the fixed-size buffers the shell struct uses.</summary>
    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];

    // ---- interop -------------------------------------------------------------------------

    private const int NIM_ADD = 0x00000000;

    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_STATE = 0x00000008;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIS_HIDDEN = 0x00000001;
    private const uint NIIF_NONE = 0x00000000;

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExeFileName, int nIconIndex);
}
