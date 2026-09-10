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
    /// The message-only window and shell icon registered by the last successful Show, kept
    /// so a second call reuses them instead of leaking a fresh set. Both outlive the call
    /// deliberately - see ShowCore - and are released by <see cref="Cleanup"/> at exit.
    /// </summary>
    private static IntPtr _hwnd;
    private static IntPtr _hIcon;
    private static bool _registered;

    /// <summary>
    /// Whether _hIcon came from ExtractIcon (ours to destroy) rather than being the shared
    /// IDI_APPLICATION handle (which must never be destroyed).
    /// </summary>
    private static bool _ownsIcon;

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

    /// <summary>
    /// Removes the shell icon and releases the window and icon handles. Called at shutdown;
    /// safe to call when nothing was ever shown, and safe to call twice.
    /// </summary>
    public static void Cleanup()
    {
        if (!IsSupported) return;

        try
        {
            if (_registered && _hwnd != IntPtr.Zero)
            {
                // NIM_DELETE reads only hWnd and uID, but the ByValTStr fields are marshalled
                // whatever it reads, and a null one throws. Empty strings keep that quiet.
                var data = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _hwnd,
                    uID = NotifyIconId,
                    szTip = "",
                    szInfo = "",
                    szInfoTitle = "",
                };
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _registered = false;
            }

            // Only an ExtractIcon handle is ours; IDI_APPLICATION is shared and destroying
            // it would corrupt an object other windows in the process still use.
            if (_hIcon != IntPtr.Zero && _ownsIcon) DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
            _ownsIcon = false;

            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }
        catch (Exception)
        {
            // Cleanup runs on the way out; the process is about to release all of this
            // anyway, so a failure here has nothing left to affect.
        }
    }

    private static bool ShowCore(string title, string message)
    {
        // A message-only window owns the icon. It never renders; it exists solely to give
        // Shell_NotifyIcon an hWnd to associate the notification with. Created once and
        // reused: a retry after a failed NIM_ADD must not leak a second window.
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = CreateWindowExW(0, "STATIC", "TokenBurnRateNotify", 0, 0, 0, 0, 0,
                                    HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        if (_hwnd == IntPtr.Zero) return false;

        if (_hIcon == IntPtr.Zero) _hIcon = LoadAppIcon();

        var icon = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = NotifyIconId,
            uFlags = NIF_INFO | NIF_ICON | NIF_STATE,
            dwState = NIS_HIDDEN,
            dwStateMask = NIS_HIDDEN,
            hIcon = _hIcon,
            szInfoTitle = Trim(title, 63),
            szInfo = Trim(message, 255),
            dwInfoFlags = NIIF_NONE,
            szTip = "Token Burn Rate",
        };

        // NIM_ADD registers the icon and queues the balloon in one call, but only the first
        // time: a second NIM_ADD on a live id fails. Once registered, NIM_MODIFY carries
        // the balloon instead.
        var command = _registered ? NIM_MODIFY : NIM_ADD;
        if (!Shell_NotifyIconW(command, ref icon))
        {
            // A stale registration from a previous instance that died without cleaning up
            // makes NIM_ADD fail. Drop it and try once more before giving up.
            if (command == NIM_ADD)
            {
                Shell_NotifyIconW(NIM_DELETE, ref icon);
                if (Shell_NotifyIconW(NIM_ADD, ref icon)) { _registered = true; return true; }
            }
            return false;
        }

        // The icon stays registered: deleting it now would cancel the balloon it is
        // carrying. Cleanup removes it when the process shuts down.
        _registered = true;
        return true;
    }

    /// <summary>
    /// The app's own icon, so the balloon is recognisably from this app.
    ///
    /// Sets <see cref="_ownsIcon"/> when the handle came from ExtractIcon and is therefore
    /// ours to destroy. The IDI_APPLICATION fallback is a shared system handle: passing it
    /// to DestroyIcon is undefined, so ownership is tracked rather than assumed.
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var icon = ExtractIconW(IntPtr.Zero, exe, 0);
                // ExtractIcon returns 1 for "file has no icons", which is not a handle.
                if (icon != IntPtr.Zero && icon != new IntPtr(1))
                {
                    _ownsIcon = true;
                    return icon;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the generic application icon.
        }

        _ownsIcon = false;
        return LoadIconW(IntPtr.Zero, IDI_APPLICATION);
    }

    /// <summary>Truncates to the fixed-size buffers the shell struct uses.</summary>
    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];

    // ---- interop -------------------------------------------------------------------------

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    /// <summary>Fixed id for this app's notification icon, paired with the hWnd above.</summary>
    private const uint NotifyIconId = 1;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    // Only for handles from ExtractIcon; a shared handle from LoadIcon must not be passed
    // here, which is why LoadAppIcon's fallback path is tracked separately below.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExeFileName, int nIconIndex);
}
